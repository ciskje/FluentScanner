import AppKit

/// One result row (the list row's Identifiable element).
struct HostRow: Identifiable {
    let ip: String
    var mac: String?
    var hostname: String?
    var services: String?
    var latencyMs: Int?
    var ports: [UInt16] = []
    var vendor: String?
    var kind: String?
    var server: String? // HTTP Server header: brand fallback when OUI is silent
    var isLocal = false // this machine: highlighted in the results
    var hostnameRank = 0 // 3 mDNS > 2 reverse DNS > 1 NetBIOS: rank wins, not arrival order
    var id: String { ip }

    /// The combined "Vendor — Type" cell (Table column + CSV share it):
    /// the HTTP software name fills in when no OUI vendor exists.
    var vendorText: String {
        [(vendor ?? server) ?? "", kind ?? ""].filter { !$0.isEmpty }.joined(separator: " — ")
    }

    var latencyText: String {
        latencyMs.map { "\($0) ms" } ?? "—"
    }

    /// Non-optional sort keys for the Table header clicks.
    var sortIp: String { ip }
    var sortHostname: String { hostname ?? "" }
    var sortMac: String { mac ?? "" }
    var sortBrand: String { (vendor ?? server) ?? "" }
    var sortKind: String { kind ?? "" }
    var sortServices: String { services ?? "" }
    var sortLatency: Int { latencyMs ?? Int.max }
}

/// Orchestrates one scan on the main actor: discovery → MAC → port sweep →
/// reverse DNS, updating published state as results land (live list).
@MainActor
final class ScannerVM: ObservableObject {
    @Published var running = false
    @Published var rows: [HostRow] = []
    @Published var progress: Double = 0
    @Published var status = ""
    @Published var sort: [KeyPathComparator<HostRow>] = []
    @Published var filter = "" {
        didSet { publishAll() }
    }

    private var task: Task<Void, Never>?
    private var byIp: [String: HostRow] = [:]

    func start(_ spec: String) {
        guard let ips = Scanner.targets(spec) else {
            status = L("invalidRange")
            return
        }
        task?.cancel()
        running = true
        rows = []
        byIp = [:]
        progress = 0
        status = L("scanning", 0, ips.count)
        task = Task { await run(ips) }
    }

    func stop() {
        task?.cancel()
        running = false
        status = L("stopped")
    }

    func toggle(start: String, end: String) {
        if running { stop(); return }
        let s = start.trimmingCharacters(in: .whitespaces)
        let e = end.trimmingCharacters(in: .whitespaces)
        self.start(s == e ? s : "\(s)-\(e)")
    }

    private func applyMac(_ ip: String, arp: [String: String]) {
        let old = byIp[ip, default: HostRow(ip: ip)]
        let mac = arp[ip]
        byIp[ip] = HostRow(ip: ip, mac: mac, hostname: old.hostname,
                           services: old.services, latencyMs: old.latencyMs,
                           ports: old.ports,
                           vendor: mac.flatMap { Oui.vendor($0) },
                           kind: old.kind, server: old.server,
                           isLocal: old.isLocal, hostnameRank: old.hostnameRank)
    }

    private func applyKind(_ ip: String, _ kind: String) {
        let old = byIp[ip, default: HostRow(ip: ip)]
        guard old.kind == nil || old.kind?.isEmpty == true else { return }
        byIp[ip] = HostRow(ip: ip, mac: old.mac, hostname: old.hostname,
                           services: old.services, latencyMs: old.latencyMs,
                           ports: old.ports, vendor: old.vendor, kind: kind,
                           server: old.server, isLocal: old.isLocal,
                           hostnameRank: old.hostnameRank)
    }

    /// Merge a hostname keeping the source precedence (3 mDNS >
    /// 2 reverse DNS > 1 NetBIOS). Lookups land chunk by chunk, in whatever
    /// order they answer, so the rank — not the merge order — decides.
    private func setHostname(_ ip: String, _ name: String, rank: Int) {
        var r = byIp[ip, default: HostRow(ip: ip)]
        var clean = name
        if clean.hasSuffix(".local") {
            clean = String(clean.dropLast(".local".count))
        }
        clean = clean.trimmingCharacters(in: CharacterSet(charactersIn: "."))
        guard !clean.isEmpty, r.hostnameRank < rank else { return }
        r.hostname = clean
        r.hostnameRank = rank
        byIp[ip] = r
    }

    private func reportDiscovery(total: Int) {
        progress = Double(min(2 * total, pingTested + tcpTested)) / Double(max(1, 2 * total))
        status = L("scanning", min(total, pingTested), total)
    }

    private var pingTested = 0
    private var tcpTested = 0 // hosts

    /// Bulk ICMP pass: hits carry latency + TTL. Updates pingTested live.
    private func sweepPing(_ dotted: [String], total: Int) async -> [(ip: String, ms: Int, ttl: Int?)] {
        var hits: [(ip: String, ms: Int, ttl: Int?)] = []
        for chunk in dotted.chunked(Scanner.pingChunk) {
            if Task.isCancelled { break }
            await withTaskGroup(of: (String, (ms: Int, ttl: Int?)?).self) { batch in
                for ip in chunk {
                    batch.addTask { (ip, await Scanner.ping(ip)) }
                }
                for await (ip, res) in batch {
                    pingTested += 1
                    if let res {
                        hits.append((ip, res.ms, res.ttl))
                        if byIp[ip]?.latencyMs == nil {
                            byIp[ip, default: HostRow(ip: ip)].latencyMs = res.ms
                        }
                        if let kind = res.ttl.flatMap(Scanner.ttlKind) {
                            applyKind(ip, kind)
                        }
                    }
                }
            }
            reportDiscovery(total: total)
            publishAll()
        }
        return hits
    }

    /// Full TCP discovery pass over every target (union with ping, not a
    /// fallback on the remainder): every discovery port probed in parallel
    /// per chunk. Extra probes on ping-alive hosts are harmless — they get
    /// the full sweep next anyway.
    private func sweepTCP(_ dotted: [String], total: Int) async -> [(ip: String, ms: Int)] {
        var hits: [(ip: String, ms: Int)] = []
        let pairs: [(String, UInt16)] =
            dotted.flatMap { ip in Scanner.discoveryPorts.map { (ip, $0) } }
        for chunk in pairs.chunked(Scanner.portChunk) {
            if Task.isCancelled { break }
            await withTaskGroup(of: (String, Int?).self) { batch in
                for (ip, p) in chunk {
                    batch.addTask {
                        if let ms = await Scanner.probe(ip, p, Scanner.discoveryTimeout) {
                            return (ip, ms)
                        }
                        return (ip, nil)
                    }
                }
                for await (ip, ms) in batch {
                    if let ms {
                        hits.append((ip, ms))
                        if byIp[ip]?.latencyMs == nil {
                            byIp[ip, default: HostRow(ip: ip)].latencyMs = ms
                        }
                    }
                }
            }
            tcpTested += chunk.count / Scanner.discoveryPorts.count
            reportDiscovery(total: total)
            publishAll()
        }
        return hits
    }

    private func publishAll() {
        var list = byIp.values.sorted { Scanner.ipLess($0.ip, $1.ip) }
        let f = filter.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if !f.isEmpty {
            list = list.filter {
                $0.ip.lowercased().contains(f)
                    || ($0.hostname?.lowercased().contains(f) == true)
                    || ($0.mac?.lowercased().contains(f) == true)
                    || $0.vendorText.lowercased().contains(f)
                    || ($0.services?.lowercased().contains(f) == true)
            }
        }
        if let cmp = sort.first { list.sort(using: cmp) }
        rows = list
    }

    /// Header click order changed: re-apply to the stored rows (Table also
    /// sorts its display with the same comparator, so both agree).
    func resort() {
        publishAll()
    }

    private func run(_ ips: [UInt32]) async {
        let total = ips.count
        var arp = Scanner.arpTable() // snapshot: devices seen before keep MACs

        // The scanning host always shows, highlighted — even if it drops
        // its own ping (firewall): local IP + MAC come from the interfaces.
        for (ip, mac) in Scanner.localAddrs() {
            var r = byIp[ip, default: HostRow(ip: ip)]
            r.isLocal = true
            if r.mac == nil, let mac {
                r.mac = mac
                r.vendor = Oui.vendor(mac)
            }
            byIp[ip] = r
        }

        // Phase 1 — ping sweep + TCP discovery run CONCURRENTLY (async let
        // interleaves on the network waits, same actor, no races): each
        // returns its hits, the merge unions them. Wall time ≈ the slower
        // pass, not the sum.
        let dotted = ips.map { Scanner.dotted($0) }
        pingTested = 0
        tcpTested = 0
        async let pingHits = sweepPing(dotted, total: total)
        async let tcpHits = sweepTCP(dotted, total: total)
        let (ph, th) = await (pingHits, tcpHits)
        var alive: Set<String> = []
        for h in ph {
            alive.insert(h.ip)
            if byIp[h.ip]?.latencyMs == nil {
                byIp[h.ip, default: HostRow(ip: h.ip)].latencyMs = h.ms
            }
            if let kind = h.ttl.flatMap(Scanner.ttlKind) {
                applyKind(h.ip, kind)
            }
        }
        for h in th {
            alive.insert(h.ip)
            if byIp[h.ip]?.latencyMs == nil {
                byIp[h.ip, default: HostRow(ip: h.ip)].latencyMs = h.ms
            }
        }
        // Both passes populate the ARP table → one refresh for all MACs.
        arp.merge(Scanner.arpTable()) { _, new in new }
        for ip in alive { applyMac(ip, arp: arp) }
        publishAll()

        // Responders with still-missing ARP entries (expired mid-scan or
        // lost the race): a targeted re-ping repopulates them — redo only
        // those, then re-read once.
        let missing = Array(alive).filter { arp[$0] == nil }
        if !missing.isEmpty {
            for chunk in missing.chunked(Scanner.pingChunk) {
                if Task.isCancelled { break }
                await withTaskGroup(of: Void.self) { batch in
                    for ip in chunk {
                        batch.addTask { _ = await Scanner.ping(ip) }
                    }
                    for await _ in batch {}
                }
            }
            arp.merge(Scanner.arpTable()) { _, new in new }
            for ip in alive { applyMac(ip, arp: arp) }
            publishAll()
        }

        if alive.isEmpty || Task.isCancelled {
            finish()
            return
        }
        status = L("portScanFmt", alive.count)
        publishAll()

        // Phase 2 — port sweep on live hosts, pairs chunked to bound sockets
        let pairs: [(String, UInt16)] =
            alive.flatMap { ip in Scanner.scanPorts.map { (ip, $0) } }
        for chunk in pairs.chunked(Scanner.portChunk) {
            if Task.isCancelled { break }
            await withTaskGroup(of: (String, UInt16).self) { batch in
                for (ip, p) in chunk {
                    batch.addTask {
                        await Scanner.probe(ip, p, Scanner.portTimeout) != nil
                            ? (ip, p) : (ip, 0)
                    }
                }
                for await (ip, p) in batch where p != 0 {
                    byIp[ip, default: HostRow(ip: ip)].addPort(p)
                }
            }
        }
        for ip in byIp.keys { byIp[ip]?.refreshServices() }
        publishAll()

        // Vendor + kind from ports/Vendor (OUI); the TTL hint from the ping
        // only survives when classification found nothing better.
        for ip in byIp.keys {
            var row = byIp[ip]!
            row.kind = Scanner.classify(vendor: row.vendor, ports: Set(row.ports))
                ?? row.kind
            byIp[ip] = row
        }
        publishAll()

        // Phase 3 — hostname + software lookups run CONCURRENTLY (all need
        // only the alive set): wall time ≈ the slowest, not the sum. Each
        // source merges the moment it lands (rank keeps mDNS 3 >
        // reverse DNS 2 > NetBIOS 1 whatever the arrival order); the HTTP
        // Server header fills the brand slot only when OUI is silent.
        status = L("dnsFmt")
        publishAll()
        let snap = Array(alive)
        await withTaskGroup(of: Void.self) { group in
            group.addTask { @MainActor in
                let mdns = await Scanner.mdnsHarvest(seconds: 2.5)
                for (ip, name) in mdns { self.setHostname(ip, name, rank: 3) }
                self.publishAll()
            }
            group.addTask { _ = await self.reverseAll(snap) }
            group.addTask { _ = await self.nbnsAll(snap) }
            group.addTask { _ = await self.httpAll(snap) }
        }
        for ip in byIp.keys { byIp[ip]?.refreshServices() } // server now known
        publishAll()
        finish()
    }

    /// Reverse DNS for every host, chunked (independent pass for phase 3).
    /// Each chunk merges the moment it answers (rank 2), so names land live.
    private func reverseAll(_ hosts: [String]) async -> [(String, String)] {
        var out: [(String, String)] = []
        for chunk in hosts.chunked(32) {
            if Task.isCancelled { break }
            await withTaskGroup(of: (String, String?).self) { batch in
                for ip in chunk {
                    batch.addTask { (ip, await Scanner.reverseDNS(ip)) }
                }
                for await (ip, name) in batch {
                    if let name {
                        out.append((ip, name))
                        setHostname(ip, name, rank: 2)
                    }
                }
            }
            publishAll()
        }
        return out
    }

    /// NetBIOS node-status for every host (no hostname prefilter: mDNS runs
    /// concurrently, so a prefilter would query almost everything anyway —
    /// a few spare UDP packets for a simpler flow). Merges per chunk (rank 1).
    private func nbnsAll(_ hosts: [String]) async -> [(String, String)] {
        var out: [(String, String)] = []
        for chunk in hosts.chunked(32) {
            if Task.isCancelled { break }
            await withTaskGroup(of: (String, String?).self) { batch in
                for ip in chunk {
                    batch.addTask { (ip, await Scanner.nbnsName(ip)) }
                }
                for await (ip, name) in batch {
                    if let name {
                        out.append((ip, name))
                        setHostname(ip, name, rank: 1)
                    }
                }
            }
            publishAll()
        }
        return out
    }

    /// HTTP Server headers for 80/8080 hosts (only already-open ports).
    /// Merges per chunk so brands land live.
    private func httpAll(_ hosts: [String]) async -> [(String, String)] {
        var out: [(String, String)] = []
        let pairs: [(String, UInt16)] = hosts.compactMap { ip in
            guard let ports = byIp[ip]?.ports else { return nil }
            if ports.contains(80) { return (ip, 80) }
            if ports.contains(8080) { return (ip, 8080) }
            return nil
        }
        for chunk in pairs.chunked(16) {
            if Task.isCancelled { break }
            await withTaskGroup(of: (String, String?).self) { batch in
                for (ip, p) in chunk {
                    batch.addTask { (ip, await Scanner.httpServer(ip, p)) }
                }
                for await (ip, srv) in batch {
                    if let srv {
                        out.append((ip, srv))
                        byIp[ip]?.server = srv
                        byIp[ip]?.refreshServices()
                    }
                }
            }
            publishAll()
        }
        return out
    }

    private func finish() {
        running = false
        status = Task.isCancelled ? L("stopped") : L("doneFmt", rows.count)
        progress = 1
    }

    // MARK: Export

    func exportCSV() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "FluentScanner.csv"
        panel.allowedContentTypes = [.commaSeparatedText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let head = "ip,hostname,mac,vendor,services,latency_ms"
        let lines = rows.map { r -> String in
            [r.ip, r.hostname ?? "", r.mac ?? "",
                [r.vendor ?? "", r.kind ?? ""].filter { !$0.isEmpty }
                    .joined(separator: " — "),
                r.services ?? "", r.latencyMs?.description ?? ""]
                .map { $0.contains(",") ? "\"\($0)\"" : $0 }
                .joined(separator: ",")
        }
        do {
            try ([head] + lines).joined(separator: "\n")
                .write(to: url, atomically: true, encoding: .utf8)
            status = L("exported")
        } catch {
            status = L("exportedFailed")
        }
    }
}

extension HostRow {
    mutating func addPort(_ p: UInt16) {
        let set = Set(ports)
        if set.contains(p) { return }
        ports.append(p)
    }

    mutating func refreshServices() {
        services = ports.sorted().map { Scanner.serviceLabel($0, server: server) }
            .joined(separator: ", ")
    }
}
