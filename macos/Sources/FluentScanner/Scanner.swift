import Foundation
import Network

/// Scan engine. Pure Foundation/Network, no dependencies, no app state:
/// the UI-side flow lives in ScannerVM.
enum Scanner {
    // MARK: Tuning knobs (Settings-editable, UserDefaults-backed)

    /// Comma/space-separated ports → list, nil on any invalid entry.
    /// Pure — exercised in the self-test.
    static func parsePorts(_ text: String) -> [UInt16]? {
        var out: [UInt16] = []
        for tok in text.split(whereSeparator: { $0 == "," || $0 == ";" || $0.isWhitespace }) {
            guard let p = UInt16(tok), p > 0 else { return nil }
            if !out.contains(p) {
                guard out.count < 256 else { return nil }
                out.append(p)
            }
        }
        return out.isEmpty ? nil : out
    }

    private static func portsPref(_ key: String, fallback: [UInt16]) -> [UInt16] {
        guard let text = UserDefaults.standard.string(forKey: key),
            let list = parsePorts(text) else { return fallback }
        return list
    }

    private static func timeoutPref(_ key: String, fallback: Double) -> Double {
        let v = UserDefaults.standard.object(forKey: key) as? Double ?? fallback
        return min(3.0, max(0.5, v))
    }

    /// Always-probed ports for host discovery (ordered by LAN hit rate).
    /// Wide list: devices that only answer on ssh/https/RDP/print ports must
    /// still be found. Swapped 1:1 with `windows/source/Scanner.cs`.
    static var discoveryPorts: [UInt16] {
        portsPref("discoveryPortsText",
                  fallback: [445, 80, 8080, 22, 443, 3389, 9100, 631])
    }
    /// Common-services list scanned for every live host.
    static var scanPorts: [UInt16] {
        portsPref("scanPortsText",
                  fallback: [
                      21, 22, 23, 25, 53, 80, 110, 111, 123, 135, 139, 143,
                      161, 389, 443, 445, 465, 587, 631, 993, 995, 1433, 1521,
                      2049, 2375, 3260, 3306, 3389, 5432, 5900, 5985, 6379,
                      8000, 8006, 8080, 8443, 8888, 9000, 9100, 49152,
                  ])
    }
    static let portNames: [UInt16: String] = [
        21: "FTP", 22: "SSH", 23: "Telnet", 25: "SMTP", 53: "DNS", 80: "HTTP",
        110: "POP3", 111: "Rpcbind", 123: "NTP", 135: "MS-RPC",
        139: "NetBIOS", 143: "IMAP", 161: "SNMP", 389: "LDAP", 443: "HTTPS",
        445: "SMB", 465: "SMTPS", 587: "SMTP", 631: "IPP", 993: "IMAPS",
        995: "POP3S", 1433: "MSSQL", 1521: "Oracle", 2049: "NFS",
        2375: "Docker", 3260: "iSCSI", 3306: "MySQL", 3389: "RDP",
        5432: "PostgreSQL", 5900: "VNC", 5985: "WinRM", 6379: "Redis",
        8000: "HTTP", 8006: "HTTP", 8080: "HTTP", 8443: "HTTPS",
        8888: "HTTP", 9000: "HTTP", 9100: "JetDirect", 49152: "WinRPC",
    ]
    // ponytail: tuned for over-Wi-Fi RTT + ARP retries; a shorter timeout
    // silently drops slow devices (the reason for these values)
    static var pingTimeoutMs: Int {
        let v = UserDefaults.standard.object(forKey: "pingTimeoutMs") as? Double ?? 1000
        return Int(min(2000, max(500, v)))
    }
    static var discoveryTimeout: TimeInterval { timeoutPref("discoveryTimeout", fallback: 2.0) }
    static var portTimeout: TimeInterval { timeoutPref("portTimeout", fallback: 1.5) }
    static let pingChunk = 64
    static let maxTargets = 4096
    // ponytail: fixed concurrence caps; an adaptive limiter if a /24+ scan
    // ever drops connects under load
    static let portChunk = 256

    /// Display text for one open port, shared by the table cell and the
    /// CSV export (`refreshServices`): the HTTP software joins in when known.
    static func serviceLabel(_ port: UInt16, server: String?) -> String {
        guard let name = portNames[port] else { return "\(port)" }
        if name == "HTTP", let srv = server {
            return "\(name)/\(srv) (\(port))" // e.g. HTTP/Caddy (80)
        }
        return "\(name) (\(port))"
    }

    /// One-line services markdown: HTTP/HTTPS entries become links so the
    /// cell truncates as a unit instead of wrapping per entry. Pure —
    /// exercised in the self-test.
    static func servicesMarkdown(ip: String, ports: [UInt16], server: String?) -> String {
        ports.sorted().map { p in
            let label = serviceLabel(p, server: server)
            let scheme: String?
            switch portNames[p] {
            case "HTTP": scheme = "http"
            case "HTTPS": scheme = "https"
            default: scheme = nil
            }
            if let scheme {
                return "[\(mdEscape(label))](\(scheme)://\(ip):\(p))"
            }
            return mdEscape(label)
        }.joined(separator: ", ")
    }

    private static func mdEscape(_ s: String) -> String {
        var out = ""
        out.reserveCapacity(s.count)
        for c in s {
            if "\\[]()_*~`#".contains(c) { out.append("\\") }
            out.append(c)
        }
        return out
    }

    // MARK: Target parsing

    /// Accepts "10.0.0.5", "10.0.0.0/24", "10.0.0.1-10.0.0.254",
    /// "10.0.0.1-254" (last-octet shorthand). nil on anything else.
    static func targets(_ text: String) -> [UInt32]? {
        let t = text.trimmingCharacters(in: .whitespaces)
        guard !t.isEmpty else { return nil }

        if t.contains("/") {
            let parts = t.split(separator: "/")
            guard parts.count == 2, let ip = ipv4(String(parts[0])),
                let bits = UInt32(parts[1]), bits <= 32 else { return nil }
            let hostBits = 32 - Int(bits)
            // ponytail: /12 sentinel stops broadcast-size storms; raise with
            // real need — port sweep cost is O(hosts x ports)
            guard hostBits <= 12 else { return nil }
            let mask: UInt32 = hostBits == 0 ? .max : ~((1 << hostBits) - 1)
            let base = ip & mask
            let n = UInt32(1) << hostBits
            // Network + broadcast are never probed (/31 has none per
            // RFC 3021, so both addresses stay).
            if n <= 2 { return (0 ..< n).map { base + $0 } }
            return (1 ..< n - 1).map { base + $0 }
        }

        if t.contains("-") {
            let sides = t.split(separator: "-", maxSplits: 1)
            guard sides.count == 2, let a = ipv4(String(sides[0])) else {
                return nil
            }
            let tail = String(sides[1])
            let b: UInt32
            if let full = ipv4(tail) { b = full }
            else if let last = UInt32(tail), last <= 255 {
                // Last-octet shorthand: same first three octets as the start.
                guard a & 0xFF <= last else { return nil }
                b = (a & 0xFF_FF_FF00) | last
            } else { return nil }
            guard a <= b, b - a < maxTargets else { return nil }
            return Array(a ... b)
        }

        guard let ip = ipv4(t) else { return nil }
        return [ip]
    }

    /// "192.168.1.10" → u32 with "a" as the most significant byte.
    static func ipv4(_ s: String) -> UInt32? {
        let octets = s.split(separator: ".")
        guard octets.count == 4,
            octets.allSatisfy({ !$0.isEmpty }),
            let bytes = octets.compactMap({ UInt8($0) }) as? [UInt8],
            bytes.count == 4 else { return nil }
        var v: UInt32 = 0
        for b in bytes { v = (v << 8) | UInt32(b) }
        return v
    }

    static func dotted(_ v: UInt32) -> String {
        let b = withUnsafeBytes(of: v.bigEndian) { Array($0) }
        return b.map(String.init).joined(separator: ".")
    }

    /// Numeric IP order ("1.2.0.10" > "1.2.0.9").
    static func ipLess(_ a: String, _ b: String) -> Bool {
        let x = a.split(separator: ".").compactMap { UInt8($0) }
        let y = b.split(separator: ".").compactMap { UInt8($0) }
        for (xi, yi) in zip(x, y) where xi != yi { return xi < yi }
        return x.count < y.count
    }

    // MARK: ARP (MAC identification)

    /// `arp -a` on macOS and Windows, one line-parser for both formats:
    /// "(192.168.1.1) at aa:bb:cc:dd:ee:ff on en0" / "1.2.3.4 aa-bb-… dynamic".
    /// Incomplete entries ("at (incomplete)") yield nothing. IPv6 lines drop
    /// (no IPv4 → no match). Called before and after the discovery pass:
    /// the TCP connects populate the local-table entries for the scan itself.
    static func arpTable() -> [String: String] {
        var out: [String: String] = [:]
        let arp = Process()
        arp.executableURL = URL(fileURLWithPath: "/usr/sbin/arp")
        arp.arguments = ["-a"]
        arp.standardError = Pipe()
        let pipe = Pipe()
        arp.standardOutput = pipe
        do {
            try arp.run()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            arp.waitUntilExit()
            guard let text = String(data: data, encoding: .utf8) else { return [:] }
            for line in text.split(separator: "\n") {
                if let (ip, mac) = parseArpLine(String(line)),
                    out[ip] == nil {
                    out[ip] = mac
                }
            }
        } catch {
            // No `arp` binary or broken output: MACs stay empty, scan continues.
        }
        return out
    }

    /// One `arp -a` line → (ip, normalized "aa:bb:cc:dd:ee:ff"); nil if the
    /// line carries no complete IPv4 + MAC pair. Visible for self-test.
    static func parseArpLine(_ line: String) -> (String, String)? {
        var ip: String?
        var mac: String?
        for tok in line.split(separator: " ") {
            var t = String(tok)
            if t.hasPrefix("(") && t.hasSuffix(")") {
                t = String(t.dropFirst().dropLast()) // macOS "(192.168.1.1)"
            }
            if ip == nil, let v = ipv4(t) {
                // Zero-octet junk ("0.0.0.0") is not useful either way.
                ip = v == 0 ? nil : t
                continue
            }
            if mac == nil, let m = normalizedMAC(t) { mac = m }
        }
        guard let ip, let mac else { return nil }
        return (ip, mac)
    }

    /// "aa:bb:cc:dd:ee:ff" / "aa-bb-cc-dd-ee-ff" → colon form, nil otherwise.
    /// Single-digit groups ("f8:e9:3:0:4b:21" — `arp` doesn't zero-pad)
    /// are padded; anything else drops.
    static func normalizedMAC(_ t: String) -> String? {
        // Windows prints dashes, macOS colons; pick whichever is present.
        let sep: Character = t.contains(":") ? ":" : "-"
        guard t.contains(sep) else { return nil }
        let groups = t.split(separator: sep, omittingEmptySubsequences: false)
        guard groups.count == 6 else { return nil }
        var bytes: [String] = []
        for g in groups {
            guard (g.count == 1 || g.count == 2),
                g.allSatisfy({ $0.isHexDigit }) else { return nil }
            bytes.append((g.count == 1 ? "0" : "") + g.lowercased())
        }
        return bytes.joined(separator: ":")
    }

    // MARK: ICMP ping (bulk discovery + ARP table populator)

    /// Unprivileged `ping -c 1` process per host: reaches devices that
    /// answer no TCP port, and answers populate the local ARP table (MAC
    /// coverage far beyond the TCP probes). Latency comes from the ping's
    /// own "time=" report; wall time is the fallback.
    static func ping(_ ip: String) async -> (ms: Int, ttl: Int?)? {
        await withCheckedContinuation { cont in
            DispatchQueue.global().async {
                let p = Process()
                p.executableURL = URL(fileURLWithPath: "/sbin/ping")
                p.arguments = ["-c", "1", "-W", "\(pingTimeoutMs)", ip]
                p.standardError = Pipe()
                let out = Pipe()
                p.standardOutput = out
                let start = DispatchTime.now()
                guard (try? p.run()) != nil else {
                    cont.resume(returning: nil)
                    return
                }
                let data = out.fileHandleForReading.readDataToEndOfFile()
                p.waitUntilExit()
                guard p.terminationStatus == 0 else {
                    cont.resume(returning: nil)
                    return
                }
                var ms = elapsedMs(start)
                var ttl: Int?
                if let text = String(data: data, encoding: .utf8) {
                    if let at = text.range(of: "time=") {
                        let raw = text[at.upperBound...]
                            .prefix(while: { $0.isNumber || $0 == "." })
                        if let v = Double(raw) { ms = max(1, Int(v.rounded())) }
                    }
                    if let at = text.range(of: "ttl=") {
                        let raw = text[at.upperBound...]
                            .prefix(while: { $0.isNumber })
                        ttl = Int(raw)
                    }
                }
                cont.resume(returning: (ms, ttl))
            }
        }
    }

    /// TTL guess from the ping reply: only 128 (Windows) is distinctive
    /// enough to label — 255 is the default of many embedded stacks
    /// (lwIP/Tuya/Espressif), not a router signature. No generic Linux
    /// label either. An initial hint only — the port/vendor classification
    /// overrides it later.
    static func ttlKind(_ ttl: Int) -> String? {
        switch ttl {
        case 118...130: return "Windows host"
        default: return nil
        }
    }

    // MARK: TCP probing

    /// One connect attempt → wall-clock latency (ms) on success, nil on
    /// failure. The deadline is an `asyncAfter` **on the connection's own
    /// queue**, so the cancel and the state callback can't race.
    /// `.waiting` fails fast (no route / ARP pending): full timeouts saved
    /// on physically absent hosts.
    static func probe(_ ip: String, _ port: UInt16, _ timeout: TimeInterval)
        async -> Int? {
        await withCheckedContinuation { cont in
            let q = DispatchQueue(label: "FluentScanner.probe")
            let start = DispatchTime.now()
            let tcp = NWProtocolTCP.Options()
            tcp.connectionTimeout = Int(ceil(timeout)) // seconds-granular fallback
            let conn = NWConnection(
                host: NWEndpoint.Host(ip),
                port: NWEndpoint.Port(rawValue: port)!,
                using: NWParameters(tls: nil, tcp: tcp))
            let lock = NSLock()
            var finished = false
            @Sendable func finish(_ ok: Bool) {
                lock.lock()
                defer { lock.unlock() }
                guard !finished else { return }
                finished = true
                conn.cancel()
                cont.resume(returning: ok ? elapsedMs(start) : nil)
            }
            conn.stateUpdateHandler = { state in
                switch state {
                case .ready: finish(true)
                case .failed, .cancelled: finish(false)
                case .waiting: finish(false)
                default: break
                }
            }
            conn.start(queue: q)
            q.asyncAfter(deadline: .now() + timeout) { finish(false) }
        }
    }

    private static func elapsedMs(_ start: DispatchTime) -> Int {
        let ns = DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds
        return max(0, Int(ns / 1_000_000))
    }

    // MARK: HTTP Server header (software hint for port-80/8080 hosts)

    /// First `Server:` header value of a response (nil on anything
    /// unexpected). Pure — exercised in the self-test.
    static func parseServerHeader(_ response: String) -> String? {
        // NB: split(separator: "\n") never fires — "\r\n" is one Character
        // (grapheme cluster), so only the NSString spelling splits CRLF.
        for raw in response.components(separatedBy: "\n").prefix(17) {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.isEmpty { break } // headers done
            guard line.count > 7, line.prefix(7).lowercased() == "server:" else { continue }
            let v = line.dropFirst(7).trimmingCharacters(in: .whitespaces)
            return v.isEmpty || v.count > 80 ? nil : String(v)
        }
        return nil
    }

    /// GET / on an already-open HTTP port, read ≤2 KB: the `Server:` header
    /// names the software (Caddy, lighttpd…) when OUI says nothing.
    /// Unprivileged TCP, single shot with a short deadline.
    static func httpServer(_ ip: String, _ port: UInt16) async -> String? {
        await withCheckedContinuation { cont in
            let q = DispatchQueue(label: "fscan.http")
            let conn = NWConnection(host: NWEndpoint.Host(ip),
                                    port: NWEndpoint.Port(rawValue: port)!,
                                    using: .tcp)
            let lock = NSLock()
            var finished = false
            @Sendable func finish(_ value: String?) {
                lock.lock()
                defer { lock.unlock() }
                guard !finished else { return }
                finished = true
                conn.cancel()
                cont.resume(returning: value)
            }
            conn.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    let req = "GET / HTTP/1.0\r\nHost: \(ip)\r\nConnection: close\r\n\r\n"
                    conn.send(content: Data(req.utf8),
                              completion: .contentProcessed { _ in
                                  conn.receive(minimumIncompleteLength: 1,
                                               maximumLength: 2048) { data, _, _, _ in
                                      guard let data,
                                          let text = String(data: data, encoding: .utf8),
                                          let srv = parseServerHeader(text) else {
                                          finish(nil)
                                          return
                                      }
                                      finish(srv)
                                  }
                              })
                case .failed, .cancelled: finish(nil)
                case .waiting: finish(nil)
                default: break
                }
            }
            conn.start(queue: q)
            q.asyncAfter(deadline: .now() + 1.5) { finish(nil) }
        }
    }

    // MARK: Reverse DNS (best effort, background thread, no UI cost)

    static func reverseDNS(_ ip: String) async -> String? {
        guard let addr = ipv4(ip) else { return nil }
        return await withCheckedContinuation { cont in
            let q = DispatchQueue(label: "FluentScanner.dns", attributes: .concurrent)
            let lock = NSLock()
            var finished = false
            @Sendable func finish(_ value: String?) {
                lock.lock()
                defer { lock.unlock() }
                guard !finished else { return }
                finished = true
                cont.resume(returning: value)
            }
            q.async {
                var sa = sockaddr_in()
                sa.sin_family = sa_family_t(AF_INET)
                sa.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                sa.sin_addr.s_addr = addr.bigEndian // network byte order
                var name = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                let rc: Int32 = withUnsafePointer(to: sa) { ptr in
                    ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                        getnameinfo(
                            sockPtr,
                            socklen_t(MemoryLayout<sockaddr_in>.size),
                            &name, socklen_t(NI_MAXHOST), nil, 0, 0)
                    }
                }
                var ret: String?
                if rc == 0 {
                    let s = String(cString: name)
                    if !s.isEmpty && s != ip { ret = s } // bare-IP answer = no name
                }
                finish(ret)
            }
            q.asyncAfter(deadline: .now() + 1.5) { finish(nil) }
        }
    }

    // MARK: mDNS harvest (hostnames from Bonjour — the source a silent
    // router can't provide)

    /// Service types probed with one mDNS PTR question each (`.local`
    /// suffix mandatory — responders ignore bare `_airplay._tcp`): every
    /// device answering also ships its A record (`hostname → ip`) in the
    /// extra records. QU bit set → responders answer in unicast to our port.
    private static let mdnsTypes = [
        "_airplay._tcp.local", "_raop._tcp.local", "_googlecast._tcp.local",
        "_companion-link._tcp.local", "_device-info._tcp.local",
        "_workstation._tcp.local", "_smb._tcp.local", "_http._tcp.local",
        "_ipp._tcp.local", "_spotify-connect._tcp.local", "_hap._tcp.local",
        "_printer._tcp.local", "_pdl-datastream._tcp.local",
        "_androidtvremote._tcp.local", "_sonos._tcp.local", "_hue._tcp.local",
        "_homekit._tcp.local", "_privet._tcp.local",
    ]

    /// One-question PTR query with the QU (unicast-response) class bit.
    static func mdnsQuery(domain: String) -> Data {
        var d = Data([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]) // id 0, 1 question
        for label in domain.split(separator: ".") {
            d.append(UInt8(label.count))
            d.append(contentsOf: label.utf8)
        }
        d.append(0)
        d.append(contentsOf: [0, 12])       // type PTR
        d.append(contentsOf: [0x80, 0x01])  // class IN + QU
        return d
    }

    /// `(ip → owner name)` from the A records in answers + authority +
    /// additionals — real mDNS replies carry the PTR in AN and the A
    /// records in AR, so scanning AN only finds nothing. RFC 1035 subset
    /// (labels + compression pointers), malformed input tolerated.
    /// Pure function — exercised in the self-test.
    static func parseMdns(data: Data) -> [String: String] {
        guard data.count > 12 else { return [:] }
        let b = [UInt8](data)
        func u16(_ i: Int) -> Int {
            guard i + 1 < b.count else { return 0 }
            return (Int(b[i]) << 8) | Int(b[i + 1])
        }

        // Consumes one name (the stored form length lands in `i0`) and
        // follows compression pointers with a jump cap.
        func readName(_ i0: inout Int) -> String {
            var i = i0
            var parts: [String] = []
            func push(_ at: Int, _ len: Int) {
                guard at + 1 + len <= b.count else { return }
                parts.append(
                    String(decoding: b[(at + 1) ..< (at + 1 + len)], as: UTF8.self))
            }
            while true {
                guard i < b.count, parts.count <= 128 else {
                    i0 = b.count
                    return parts.joined(separator: ".")
                }
                let l = Int(b[i])
                if l & 0xC0 == 0xC0 {
                    guard i + 1 < b.count else {
                        i0 = b.count
                        return parts.joined(separator: ".")
                    }
                    i0 = i + 2 // a pointer ends the stored form
                    var j = ((l & 0x3F) << 8) | Int(b[i + 1])
                    var jumps = 0
                    while jumps <= 10 {
                        guard j + 1 < b.count else { break }
                        let m = Int(b[j])
                        if m & 0xC0 == 0xC0 {
                            jumps += 1
                            j = ((m & 0x3F) << 8) | Int(b[j + 1])
                            continue
                        }
                        guard m > 0 else { break }
                        push(j, m)
                        j += 1 + m
                    }
                    return parts.joined(separator: ".")
                }
                guard l > 0 else {
                    i0 = i + 1
                    return parts.joined(separator: ".")
                }
                push(i, l)
                i += 1 + l
            }
        }

        var i = 12
        for _ in 0 ..< min(u16(4), 64) { // skip the questions
            readName(&i)
            i += 4
        }
        var out: [String: String] = [:]
        // Answers + authority + additionals: the A records ride in AR.
        for _ in 0 ..< min(u16(6) + u16(8) + u16(10), 256) {
            let owner = readName(&i)
            guard i + 10 <= b.count else { break }
            let rdlen = u16(i + 8)
            let rd = i + 10
            if u16(i) == 1, rdlen == 4, rd + 4 <= b.count { // type A
                let ip = b[(rd)..<(rd + 4)].map(String.init).joined(separator: ".")
                if out[ip] == nil { out[ip] = owner }
            }
            i = rd + rdlen
        }
        return out
    }

    /// Send the queries, then collect the unicast answers in the window:
    /// `[ip → hostname]` (mDNS listeners work unprivileged — no raw sockets).
    /// ponytail: fixed harvest window; per-service timing if a LAN needs it.
    static func mdnsHarvest(seconds: Double = 2.5) async -> [String: String] {
        await withCheckedContinuation { cont in
            DispatchQueue.global().async {
                let fd = socket(AF_INET, SOCK_DGRAM, 0)
                guard fd >= 0 else { return cont.resume(returning: [:]) }
                defer { close(fd) }
                var on: Int32 = 1
                setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on,
                           socklen_t(MemoryLayout<Int32>.size))
                var src = sockaddr_in()
                src.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                src.sin_family = sa_family_t(AF_INET)
                let bound = withUnsafePointer(to: &src) { p in
                    p.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                    }
                }
                guard bound == 0 else { return cont.resume(returning: [:]) }
                var dst = sockaddr_in()
                dst.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                dst.sin_family = sa_family_t(AF_INET)
                dst.sin_port = UInt16(5353).bigEndian
                dst.sin_addr.s_addr = UInt32(0xE000_00FB).bigEndian // 224.0.0.251
                // Burst twice (200 ms apart): flaky IoT responders often
                // miss the first one.
                for burst in 0 ..< 2 {
                    for t in mdnsTypes {
                        let q = mdnsQuery(domain: t)
                        var d = dst
                        let sent = withUnsafePointer(to: &d) { dptr in
                            dptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { dptr2 in
                                q.withUnsafeBytes { raw in
                                    sendto(fd, raw.baseAddress, raw.count, 0,
                                           dptr2, socklen_t(MemoryLayout<sockaddr_in>.size))
                                }
                            }
                        }
                        if sent < 0 { break } // no route → nothing will answer
                    }
                    if burst == 0 { usleep(200_000) }
                }
                let end = DispatchTime.now() + seconds
                var hosts: [String: String] = [:]
                while DispatchTime.now() < end {
                    var p = pollfd(fd: fd, events: Int16(0x1), revents: 0) // POLLIN
                    if poll(&p, 1, 200) <= 0 { continue }
                    var buf = [UInt8](repeating: 0, count: 65_536)
                    let n = recv(fd, &buf, buf.count, 0)
                    guard n > 12 else { continue }
                    for (ip, name) in parseMdns(data: Data(buf[..<n])) {
                        if hosts[ip] == nil { hosts[ip] = name }
                    }
                }
                cont.resume(returning: hosts)
            }
        }
    }

    // MARK: SSDP/UPnP harvest (TVs, routers, speakers announce here —
    // often the only clue for hosts that ignore ping and TCP)

    /// M-SEARCH for everything; responders answer unicast to our port.
    static func ssdpQuery() -> Data {
        Data("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ns:01\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n".utf8)
    }

    /// (location, server) from one M-SEARCH response; headers are
    /// case-insensitive, garbage yields nil. Pure — exercised in self-test.
    static func parseSsdpResponse(_ response: String) -> (location: String, server: String)? {
        // NB: split(separator: "\n") never fires — "\r\n" is one Character
        // (grapheme cluster), so only the NSString spelling splits CRLF.
        var loc: String?
        var srv = ""
        for raw in response.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = line[..<colon].trimmingCharacters(in: .whitespaces)
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            if name.caseInsensitiveCompare("location") == .orderedSame { loc = value }
            else if name.caseInsensitiveCompare("server") == .orderedSame { srv = value }
        }
        guard let loc, !loc.isEmpty else { return nil }
        return (loc, srv)
    }

    /// `(senderIp, location, server)` from the responses in the window.
    /// Unprivileged UDP — the replies come back unicast, no multicast
    /// membership needed. Same harvest shape as mDNS.
    /// ponytail: fixed harvest window; per-device timing if a LAN needs it.
    static func ssdpDiscover(seconds: Double = 3) async -> [(ip: String, location: String, server: String)] {
        await withCheckedContinuation { cont in
            DispatchQueue.global().async {
                let fd = socket(AF_INET, SOCK_DGRAM, 0)
                guard fd >= 0 else { return cont.resume(returning: []) }
                defer { close(fd) }
                var on: Int32 = 1
                setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on,
                           socklen_t(MemoryLayout<Int32>.size))
                var src = sockaddr_in()
                src.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                src.sin_family = sa_family_t(AF_INET)
                let bound = withUnsafePointer(to: &src) { p in
                    p.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                    }
                }
                guard bound == 0 else { return cont.resume(returning: []) }
                var dst = sockaddr_in()
                dst.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                dst.sin_family = sa_family_t(AF_INET)
                dst.sin_port = UInt16(1900).bigEndian
                dst.sin_addr.s_addr = UInt32(0xEFFF_FFFA).bigEndian // 239.255.255.250
                let q = ssdpQuery()
                for burst in 0 ..< 2 {
                    var d = dst
                    let sent = withUnsafePointer(to: &d) { dptr in
                        dptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { dptr2 in
                            q.withUnsafeBytes { raw in
                                sendto(fd, raw.baseAddress, raw.count, 0,
                                       dptr2, socklen_t(MemoryLayout<sockaddr_in>.size))
                            }
                        }
                    }
                    if sent < 0 { break } // no route → nothing will answer
                    if burst == 0 { usleep(200_000) }
                }
                let end = DispatchTime.now() + seconds
                var out: [(String, String, String)] = []
                var seen = Set<String>() // location, case-insensitive
                while DispatchTime.now() < end {
                    var p = pollfd(fd: fd, events: Int16(0x1), revents: 0) // POLLIN
                    if poll(&p, 1, 200) <= 0 { continue }
                    var buf = [UInt8](repeating: 0, count: 8192)
                    var from = sockaddr_storage()
                    var fromLen = socklen_t(MemoryLayout<sockaddr_storage>.size)
                    let n: Int = buf.withUnsafeMutableBytes { raw in
                        withUnsafeMutablePointer(to: &from) { fptr in
                            fptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sptr in
                                recvfrom(fd, raw.baseAddress, raw.count, 0, sptr, &fromLen)
                            }
                        }
                    }
                    guard n > 12 else { continue }
                    guard let text = String(bytes: buf[..<n], encoding: .ascii),
                        text.lowercased().hasPrefix("http/") else { continue }
                    guard let r = parseSsdpResponse(text), seen.insert(r.location.lowercased()).inserted else { continue }
                    var ip = ""
                    withUnsafePointer(to: from) { fptr in
                        fptr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { sin in
                            var a = sin.pointee.sin_addr
                            var b = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
                            if inet_ntop(AF_INET, &a, &b, socklen_t(INET_ADDRSTRLEN)) != nil {
                                ip = String(cString: b)
                            }
                        }
                    }
                    guard !ip.isEmpty else { continue }
                    out.append((ip, r.location, r.server))
                }
                cont.resume(returning: out)
            }
        }
    }

    /// The LOCATION host as the device sees itself is the most reliable
    /// row key; fall back to the UDP sender on weird URLs. Pure —
    /// exercised in the self-test.
    static func ssdpRowIp(_ location: String, sender: String) -> String? {
        if let host = URL(string: location)?.host, ipv4(host) != nil { return host }
        return ipv4(sender) != nil ? sender : nil
    }

    /// First `<tag>…</tag>` text of a UPnP device description. The open-tag
    /// match rejects `<manufacturerURL>` when looking for `manufacturer`
    /// (prefix collision); nested markup bails. Pure — exercised in self-test.
    static func ssdpTag(_ xml: String, _ tag: String) -> String? {
        var s = xml.startIndex
        while let open = xml.range(of: "<" + tag, options: .caseInsensitive, range: s ..< xml.endIndex) {
            let after = open.upperBound
            guard after < xml.endIndex else { return nil }
            let c = xml[after]
            guard c == ">" || c == "/" || c.isWhitespace else { s = after; continue }
            guard let gt = xml[after...].firstIndex(of: ">"),
                let close = xml.range(of: "</" + tag, options: .caseInsensitive,
                                      range: gt ..< xml.endIndex) else { return nil }
            let v = xml[xml.index(after: gt) ..< close.lowerBound]
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard !v.isEmpty, v.count <= 80, !v.contains("<") else { return nil }
            return v
        }
        return nil
    }

    static func parseSsdpDescription(_ xml: String) -> (manufacturer: String, friendlyName: String, modelName: String) {
        (ssdpTag(xml, "manufacturer") ?? "", ssdpTag(xml, "friendlyName") ?? "",
         ssdpTag(xml, "modelName") ?? "")
    }

    /// Display label: the user-given name first, maker in parens.
    /// Pure — exercised in the self-test.
    static func ssdpLabel(manufacturer: String, friendlyName: String, modelName: String) -> String {
        var parts: [String] = []
        for s in [friendlyName, manufacturer, modelName] where !s.isEmpty && !parts.contains(s) {
            parts.append(s)
        }
        guard !parts.isEmpty else { return "" }
        let label = parts.count == 1 ? parts[0] : "\(parts[0]) (\(parts[1]))"
        return label.count > 80 ? String(label.prefix(80)) : label
    }

    /// GET the description document → display label (nil on anything
    /// unexpected). Same unprivileged-TCP shape as `httpServer`: ≤8 KB,
    /// 2 s caps.
    static func ssdpDevice(_ location: String) async -> String? {
        guard let url = URL(string: location),
            (url.scheme?.lowercased().hasPrefix("http") == true),
            let host = url.host, !host.isEmpty else { return nil }
        let port = UInt16(url.port ?? 80)
        let path = (url.path.isEmpty ? "/" : url.path) + (url.query.map { "?\($0)" } ?? "")
        return await withCheckedContinuation { cont in
            let q = DispatchQueue(label: "fscan.ssdp")
            let conn = NWConnection(host: NWEndpoint.Host(host),
                                    port: NWEndpoint.Port(rawValue: port)!,
                                    using: .tcp)
            let lock = NSLock()
            var finished = false
            @Sendable func finish(_ value: String?) {
                lock.lock()
                defer { lock.unlock() }
                guard !finished else { return }
                finished = true
                conn.cancel()
                cont.resume(returning: value)
            }
            conn.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    let req = "GET \(path) HTTP/1.0\r\nHost: \(host)\r\nConnection: close\r\n\r\n"
                    conn.send(content: Data(req.utf8),
                              completion: .contentProcessed { _ in
                                  conn.receive(minimumIncompleteLength: 1,
                                               maximumLength: 8192) { data, _, _, _ in
                                      guard let data,
                                          let text = String(data: data, encoding: .utf8) else {
                                          finish(nil)
                                          return
                                      }
                                      let body: String
                                      if let r = text.range(of: "\r\n\r\n") {
                                          body = String(text[r.upperBound...])
                                      } else if let r = text.range(of: "\n\n") {
                                          body = String(text[r.upperBound...])
                                      } else {
                                          body = text
                                      }
                                      let (m, f, mo) = parseSsdpDescription(body)
                                      let label = ssdpLabel(manufacturer: m, friendlyName: f, modelName: mo)
                                      finish(label.isEmpty ? nil : label)
                                  }
                              })
                case .failed, .cancelled: finish(nil)
                case .waiting: finish(nil)
                default: break
                }
            }
            conn.start(queue: q)
            q.asyncAfter(deadline: .now() + 2.0) { finish(nil) }
        }
    }

    // MARK: NetBIOS name (Windows PCs announce no mDNS — NBSTAT over UDP 137)

    /// One-shot node-status query for the wildcard name (`*<00>`: 16 raw
    /// bytes → 32 encoded, matching the 0x20 length byte).
    static func nbnsQuery() -> Data {
        var q = Data([0x12, 0x34, 0x00, 0x00, 0x00, 0x01,
                      0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
        q.append(0x20) // 32 encoded bytes follow
        func enc(_ byte: UInt8) {
            q.append(0x41 + (byte >> 4))
            q.append(0x41 + (byte & 0x0F))
        }
        enc(0x2A) // '*'
        for _ in 0 ..< 14 { enc(0x00) } // NUL padding
        enc(0x00) // suffix
        q.append(0x00)
        q.append(contentsOf: [0x00, 0x21, 0x00, 0x01]) // NBSTAT, IN
        return q
    }

    /// First machine name from a node-status response (nil on garbage).
    /// Pure function — exercised in the self-test.
    static func parseNbns(data: Data) -> String? {
        let b = [UInt8](data)
        guard b.count > 12 else { return nil }
        func u16(_ i: Int) -> Int {
            guard i + 1 < b.count else { return 0 }
            return (Int(b[i]) << 8) | Int(b[i + 1])
        }
        guard u16(6) >= 1 else { return nil } // ANCOUNT
        var i = 12
        while i < b.count, b[i] != 0 { // skip the question name
            let l = Int(b[i])
            guard l & 0xC0 != 0xC0, i + 1 + l <= b.count else { return nil }
            i += 1 + l
        }
        i += 1 + 4 // null + type/class
        if i + 1 < b.count, b[i] & 0xC0 == 0xC0 {
            i += 2 // compressed answer name
        } else {
            while i < b.count, b[i] != 0 {
                let l = Int(b[i])
                guard l & 0xC0 != 0xC0, i + 1 + l <= b.count else { return nil }
                i += 1 + l
            }
            i += 1
        }
        guard i + 10 <= b.count, u16(i) == 0x0021 else { return nil } // NBSTAT
        let rd = i + 10
        guard rd + 18 <= b.count, Int(b[rd]) >= 1 else { return nil } // 1+ names
        let name = String(decoding: b[(rd + 1) ..< (rd + 16)], as: UTF8.self)
            .trimmingCharacters(in: .whitespaces)
            .trimmingCharacters(in: .controlCharacters)
        return name.isEmpty ? nil : name
    }

    /// NetBIOS node-status lookup (UDP 137, 1 s): the name Windows PCs
    /// answer with when they run no mDNS responder. Unprivileged UDP.
    static func nbnsName(_ ip: String) async -> String? {
        guard let addr = ipv4(ip) else { return nil }
        return await withCheckedContinuation { cont in
            DispatchQueue.global().async {
                let fd = socket(AF_INET, SOCK_DGRAM, 0)
                guard fd >= 0 else { return cont.resume(returning: nil) }
                defer { close(fd) }
                var tv = timeval(tv_sec: 1, tv_usec: 0)
                setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv,
                           socklen_t(MemoryLayout<timeval>.size))
                var dst = sockaddr_in()
                dst.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                dst.sin_family = sa_family_t(AF_INET)
                dst.sin_port = UInt16(137).bigEndian
                dst.sin_addr.s_addr = addr.bigEndian // network byte order
                let q = nbnsQuery()
                var d = dst
                let sent: Int = withUnsafePointer(to: &d) { dptr in
                    dptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { dptr2 in
                        q.withUnsafeBytes { raw in
                            sendto(fd, raw.baseAddress, raw.count, 0,
                                   dptr2, socklen_t(MemoryLayout<sockaddr_in>.size))
                        }
                    }
                }
                guard sent > 0 else { return cont.resume(returning: nil) }
                var buf = [UInt8](repeating: 0, count: 1024)
                let n: Int = buf.withUnsafeMutableBytes { raw in
                    recv(fd, raw.baseAddress, raw.count, 0)
                }
                guard n > 0 else { return cont.resume(returning: nil) }
                cont.resume(returning: parseNbns(data: Data(buf[..<n])))
            }
        }
    }

    /// Locally-administered MAC (U/L bit set: first octet & 0x02):
    /// privacy randomization, not a real vendor prefix — an OUI lookup
    /// on it would blame the wrong maker. Pure — exercised in self-test.
    static func isRandomized(_ mac: String) -> Bool {
        guard let first = mac.split(separator: ":").first,
            let byte = UInt8(first, radix: 16) else { return false }
        return byte & 0x02 != 0
    }

    // MARK: Local interfaces (identify the scanning host itself)

    /// (interface, ip, netmask?) for up non-loopback IPv4 interfaces.
    private static func v4Interfaces() -> [(iface: String, ip: String, mask: String?)] {
        var out: [(String, String, String?)] = []
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return [] }
        defer { freeifaddrs(head) }
        var p: UnsafeMutablePointer<ifaddrs>? = first
        while let cur = p {
            p = cur.pointee.ifa_next
            let ifa = cur.pointee
            guard let sa = ifa.ifa_addr else { continue }
            let flags = UInt32(ifa.ifa_flags)
            guard flags & UInt32(IFF_UP) != 0,
                flags & UInt32(IFF_LOOPBACK) == 0,
                Int32(sa.pointee.sa_family) == AF_INET else { continue }
            let name = String(cString: ifa.ifa_name)
            var addr = sa.pointee
            let addrs: (String, String?) = withUnsafePointer(to: &addr) { ptr in
                ptr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { sin in
                    var a = sin.pointee.sin_addr
                    var b1 = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
                    guard inet_ntop(AF_INET, &a, &b1,
                                     socklen_t(INET_ADDRSTRLEN)) != nil else { return ("", nil) }
                    var maskStr: String? = nil
                    if let nm = ifa.ifa_netmask {
                        var m = nm.pointee
                        maskStr = withUnsafePointer(to: &m) { mp in
                            mp.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { sp in
                                var ma = sp.pointee.sin_addr
                                var b2 = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
                                guard inet_ntop(AF_INET, &ma, &b2,
                                                 socklen_t(INET_ADDRSTRLEN)) != nil else { return nil }
                                return String(cString: b2)
                            }
                        }
                    }
                    return (String(cString: b1), maskStr)
                }
            }
            if !addrs.0.isEmpty { out.append((name, addrs.0, addrs.1)) }
        }
        return out
    }

    /// This machine's subnet as a scan range (network+1 … broadcast-1,
    /// clamped to maxTargets hosts). Powers the Get IP button. Offline-safe.
    static func localSubnet() -> (start: String, end: String)? {
        for v in v4Interfaces() {
            guard let m = v.mask, let ip = ipv4(v.ip), let mask = ipv4(m),
                mask != 0 else { continue }
            let net = ip & mask
            let bcast = net | ~mask
            guard bcast > net + 1 else { continue }
            var end = bcast - 1
            if end - (net + 1) >= UInt32(maxTargets) { end = net + UInt32(maxTargets) }
            return (dotted(net + 1), dotted(end))
        }
        return nil
    }

    /// This machine's own IPv4 + MAC per up interface (loopback excluded):
    /// the scan pre-inserts and highlights these rows. Local `getifaddrs`
    /// call — no network, offline-safe.
    static func localAddrs() -> [(ip: String, mac: String?)] {
        var macs: [String: String] = [:] // ifname → mac
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return [] }
        defer { freeifaddrs(head) }
        var p: UnsafeMutablePointer<ifaddrs>? = first
        while let cur = p {
            p = cur.pointee.ifa_next
            let ifa = cur.pointee
            guard let sa = ifa.ifa_addr else { continue }
            let flags = UInt32(ifa.ifa_flags)
            guard flags & UInt32(IFF_UP) != 0,
                flags & UInt32(IFF_LOOPBACK) == 0,
                Int32(sa.pointee.sa_family) == AF_LINK else { continue }
            let name = String(cString: ifa.ifa_name)
            // sockaddr_dl: sdl_data at byte 8, MAC at data + nlen,
            // sdl_len (byte 0) bounds the whole struct.
            let b = UnsafeRawPointer(sa).assumingMemoryBound(to: UInt8.self)
            guard Int(b[6]) == 6, // sdl_alen
                8 + Int(b[5]) + 6 <= Int(b[0]) else { continue } // +sdl_nlen
            let mac = (0 ..< 6).map { String(format: "%02x", b[8 + Int(b[5]) + $0]) }
                .joined(separator: ":")
            macs[name] = mac
        }
        return v4Interfaces().map { ($0.ip, macs[$0.iface]) }
    }

    // MARK: Device classification (vendor from OUI + service hints)

    /// Heuristic host type: strongest signal first — a Windows host with
    /// RDP/WinRM, a 9100 print server, NFS shares, then vendor-derived hints.
    static func classify(vendor: String?, ports: Set<UInt16>) -> String? {
        var hints: [String] = []
        if ports.contains(3389) || ports.contains(5985) { hints.append("Windows host") }
        if ports.contains(9100) { hints.append("Printer") }
        if ports.contains(2049) { hints.append("NAS") }
        if let v = vendor {
            if v.contains("Synology") || v.contains("QNAP") { hints.append("NAS") }
            if v.contains("Raspberry") { hints.append("Raspberry Pi") }
            if v.contains("Espressif") || v.contains("Tuya") || v.contains("Shenzhen")
                || v.contains("Dreame") || v.contains("Xiaoyi") || v.contains("AMPAK") {
                hints.append("IoT device")
            }
            if v.contains("Apple") { hints.append("Apple device") }
            if v.contains("Intel") || v.contains("Realtek") || v.contains("ASUSTeK")
                || v.contains("Gigabyte") { hints.append("PC") }
            if v.contains("Cisco") || v.contains("Ubiquiti") || v.contains("Netgear")
                || v.contains("TP-LINK") || v.contains("D-Link") || v.contains("MikroTik")
                || v.contains("AVM") || v.contains("Advanced Digital") { hints.append("Router/AP") }
        }
        return hints.isEmpty ? nil : hints.joined(separator: " · ")
    }

    // MARK: Headless self-test (FS_SELFTEST=1 — offline-safe: no network ops)

    static func selfTest() -> Bool {
        var ok = true
        func check(_ cond: Bool, _ name: String) {
            if !cond { ok = false }
            fputs("\(cond ? "PASS" : "FAIL") \(name)\n", stderr)
        }
        check(ipv4("10.0.0.1") == 0x0A00_0001, "ipv4 parse")
        check(ipv4("10.0.0.999") == nil, "ipv4 rejects 999")
        check(targets("10.0.0.1") == [0x0A00_0001], "targets single")
        check((targets("10.0.0.0/30") ?? []).count == 2, "targets cidr /30 skips net+broadcast")
        check(targets("10.0.0.0/30") == [0x0A00_0001, 0x0A00_0002], "targets /30 hosts")
        check(targets("10.0.0.5/32") == [0x0A00_0005], "targets /32 single")
        let c24 = targets("10.0.0.0/24") ?? []
        check(c24.count == 254 && c24.first == 0x0A00_0001 && c24.last == 0x0A00_00FE,
            "targets /24 skips .0 + .255")
        check(targets("10.0.0.0/13") == nil, "targets cidr too wide rejected")
        check(targets("10.0.0.1-4")?.count == 4, "targets range last-octet")
        check(targets("10.0.0.1-4")?.first == 0x0A00_0001, "range start octet")
        check(targets("10.0.0.4-10.0.0.2") == nil, "reversed range rejected")
        check(targets("10.0.2.30-10.0.2.31")?.count == 2, "full-IP range")
        check(targets("") == nil && targets("abc") == nil, "garbage rejected")
        check(dotted(ipv4("192.168.1.42") ?? 0) == "192.168.1.42", "dotted")
        let m = parseArpLine("(192.168.1.1) at f0:18:98:5b:83:d2 on en0")
        check(m?.0 == "192.168.1.1" && m?.1 == "f0:18:98:5b:83:d2", "arp macOS line")
        let w = parseArpLine("192.168.1.1    F0-18-98-5B-83-D2    dynamic")
        check(w?.0 == "192.168.1.1" && w?.1 == "f0:18:98:5b:83:d2", "arp windows line")
        check(parseArpLine("(192.168.1.1) at (incomplete) on en0") == nil,
            "arp incomplete dropped")
        check(parseArpLine("fe80::1%en0 aa:bb:cc:dd:ee:ff") == nil,
            "arp ipv6 dropped")
        check(normalizedMAC("f8:e9:3:0:4b:21") == "f8:e9:03:00:4b:21",
            "mac pads single digits")
        let nas = parseArpLine("? (192.168.1.11) at f8:e9:3:0:4b:21 on en0 ifscope [ethernet]")
        check(nas?.0 == "192.168.1.11" && nas?.1 == "f8:e9:03:00:4b:21",
            "arp real line with ? + ifscope")
        let perm = parseArpLine("? (192.168.1.117) at 4e:5e:ae:67:1f:98 on en0 ifscope permanent [ethernet]")
        check(perm?.0 == "192.168.1.117" && perm?.1 == "4e:5e:ae:67:1f:98",
            "arp permanent line")
        check(ttlKind(128) == "Windows host", "ttl 128 windows")
        check(ttlKind(64) == nil, "ttl 64 too generic, dropped")
        check(ttlKind(255) == nil, "ttl 255 is embedded stacks, not routers")
        check(ttlKind(30) == nil, "ttl unknown")
        func dn(_ s: String) -> [UInt8] {
            var o: [UInt8] = []
            for l in s.split(separator: ".") {
                o.append(UInt8(l.count))
                o.append(contentsOf: l.utf8)
            }
            o.append(0)
            return o
        }
        var pkt: [UInt8] = [0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0] // 2 answers
        pkt += dn("Mac.local")
        pkt += [0, 1, 0, 1]                 // type A, class IN
        pkt += [0, 0, 0, 10]                // ttl
        pkt += [0, 4, 192, 168, 1, 105]     // rdlen + A record
        pkt += [0xC0, 0x0C]                 // same owner name via compression
        pkt += [0, 1, 0, 1, 0, 0, 0, 10, 0, 4, 192, 168, 1, 110]
        let mp = parseMdns(data: Data(pkt))
        check(mp["192.168.1.105"] == "Mac.local" && mp["192.168.1.110"] == "Mac.local",
            "mdns parse + compression")
        check(parseMdns(data: Data([0, 1, 2, 3])).isEmpty, "mdns short packet")
        // Realistic shape: PTR in AN, A record in AR.
        var rp2: [UInt8] = [0, 0, 0x84, 0x00, 0, 0, 0, 1, 0, 0, 0, 1]
        rp2 += dn("_airplay._tcp.local")
        rp2 += [0, 12, 0, 1, 0, 0, 0, 120] // PTR, IN, ttl
        let inst = dn("Mac._airplay._tcp.local")
        rp2 += [UInt8(inst.count >> 8), UInt8(inst.count & 0xFF)] + inst
        rp2 += dn("Mac.local")
        rp2 += [0, 1, 0, 1, 0, 0, 0, 120, 0, 4, 192, 168, 1, 105]
        check(parseMdns(data: Data(rp2))["192.168.1.105"] == "Mac.local",
            "mdns additional A")
        check(mdnsQuery(domain: "_airplay._tcp.local").count > 12
            && String(decoding: mdnsQuery(domain: "_airplay._tcp.local")[13...], as: UTF8.self)
                .contains("_airplay"), "mdns query packet")
        check(nbnsQuery().count == 50, "nbns query size")
        var rp3: [UInt8] = [0x12, 0x34, 0x84, 0x00,
                            0, 1, 0, 1, 0, 0, 0, 0] // QD=1 AN=1
        rp3 += Array(nbnsQuery()[12...]) // same question back
        rp3 += [0xC0, 0x0C, 0x00, 0x21, 0x00, 0x01, // NBSTAT, IN
                0, 0, 0, 0] // ttl
        var rd3: [UInt8] = [0x01] // 1 name
        rd3 += Array("DESKTOP-ABC123 ".utf8) // 15 chars, space-padded
        rd3 += [0x00, 0x04, 0x00] // suffix + flags
        rp3 += [0x00, UInt8(rd3.count)] + rd3
        check(parseServerHeader("HTTP/1.0 200 OK\r\nServer: Caddy\r\n\r\n") == "Caddy",
            "http server header")
        check(parseServerHeader("HTTP/1.1 200 OK\nserver: lighttpd/1.4.28\n\n<body>") == "lighttpd/1.4.28",
            "http server lowercase + body guard")
        check(parseServerHeader("HTTP/1.0 200 OK\r\n\r\nno headers") == nil,
            "http no server")
        check(parseServerHeader("") == nil, "http empty")
        check(parseNbns(data: Data(rp3)) == "DESKTOP-ABC123", "nbns parse")
        check(parseNbns(data: Data([0, 1, 2, 3])) == nil, "nbns garbage")
        let sq = String(decoding: ssdpQuery(), as: UTF8.self)
        check(sq.contains("M-SEARCH * HTTP/1.1") && sq.contains("ST: ssdp:all"), "ssdp query")
        let sr = parseSsdpResponse("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://192.168.1.115:7344/desc.xml\r\nServer: Linux/1.0 UPnP/1.0\r\nST: upnp:rootdevice\r\n\r\n")
        check(sr?.location == "http://192.168.1.115:7344/desc.xml" && (sr?.server.hasPrefix("Linux") == true),
            "ssdp response parse")
        check(parseSsdpResponse("NOTIFY * HTTP/1.1\r\nNT: upnp:rootdevice\r\n\r\n") == nil,
            "ssdp response needs location")
        check(parseSsdpResponse("garbage") == nil, "ssdp response garbage")
        let desc = "<root><device><manufacturer>LG Electronics</manufacturer><manufacturerURL>http://lg.com/</manufacturerURL><friendlyName>TV Soggiorno</friendlyName><modelName>OLED55C</modelName></device></root>"
        let dd = parseSsdpDescription(desc)
        check(dd.manufacturer == "LG Electronics" && dd.friendlyName == "TV Soggiorno"
            && dd.modelName == "OLED55C", "ssdp description parse")
        check(parseSsdpDescription("<root></root>") == ("", "", ""), "ssdp description empty")
        check(ssdpLabel(manufacturer: "LG Electronics", friendlyName: "TV Soggiorno",
            modelName: "OLED55C") == "TV Soggiorno (LG Electronics)", "ssdp label")
        check(ssdpLabel(manufacturer: "Amazon", friendlyName: "", modelName: "") == "Amazon",
            "ssdp label maker only")
        check(ssdpLabel(manufacturer: "", friendlyName: "", modelName: "") == "", "ssdp label empty")
        check(ssdpRowIp("http://192.168.1.115:7344/desc.xml", sender: "192.168.1.99") == "192.168.1.115",
            "ssdp row ip location")
        check(ssdpRowIp("http://[::1]/x", sender: "192.168.1.99") == "192.168.1.99",
            "ssdp row ip sender fallback")
        check(ssdpRowIp("garbage", sender: "garbage") == nil, "ssdp row ip garbage")
        check(classify(vendor: "Dreame Technology", ports: [])?
            .contains("IoT device") == true, "classify dreame iot")
        check(classify(vendor: "Advanced Digital Broadcast SA", ports: [])?
            .contains("Router/AP") == true, "classify adb router")
        check(isRandomized("4e:5e:ae:67:1f:98"), "randomized bit set")
        check(isRandomized("02:00:00:00:00:01"), "randomized 02")
        check(!isRandomized("f8:e9:03:00:4b:21"), "global mac")
        check(!isRandomized("00:11:22:33:44:55"), "global zero-oui")
        check(!isRandomized(""), "randomized empty")
        check(parsePorts("80, 443") == [80, 443], "ports parse")
        check(parsePorts("80 80 443") == [80, 443], "ports dedupe")
        check(parsePorts("80;443 8080") == [80, 443, 8080], "ports semicolon")
        check(parsePorts("0") == nil, "ports zero rejected")
        check(parsePorts("abc") == nil, "ports garbage rejected")
        check(parsePorts("") == nil, "ports empty rejected")
        check(Scanner.serviceLabel(80, server: "Caddy") == "HTTP/Caddy (80)",
            "service label http server")
        check(Scanner.serviceLabel(443, server: nil) == "HTTPS (443)",
            "service label https")
        check(Scanner.serviceLabel(9999, server: nil) == "9999",
            "service label unknown port")
        check(Scanner.servicesMarkdown(ip: "1.2.3.4", ports: [80], server: "Caddy")
            == "[HTTP/Caddy \\(80\\)](http://1.2.3.4:80)", "services markdown link")
        check((try? AttributedString(
            markdown: Scanner.servicesMarkdown(ip: "1.2.3.4", ports: [22, 80],
                                               server: "Caddy"),
            options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) != nil,
            "services markdown parses")
        if let sub = localSubnet() {
            check(ipv4(sub.start) != nil && ipv4(sub.end) != nil
                && ipv4(sub.start)! < ipv4(sub.end)!, "localSubnet range")
        }
        var svc = HostRow(ip: "1.2.3.4", mac: nil, hostname: nil, services: nil,
                          latencyMs: nil, ports: [80, 22], vendor: nil, kind: nil,
                          server: "Caddy", isLocal: false)
        svc.refreshServices()
        check(svc.services == "SSH (22), HTTP/Caddy (80)", "services http server")
        var svc2 = HostRow(ip: "1.2.3.5", mac: nil, hostname: nil, services: nil,
                           latencyMs: nil, ports: [80], vendor: nil, kind: nil,
                           server: nil, isLocal: false)
        svc2.refreshServices()
        check(svc2.services == "HTTP (80)", "services plain http")
        for a in localAddrs() {
            check(ipv4(a.ip) != nil && (a.mac == nil || normalizedMAC(a.mac!) != nil),
                "localAddrs valid")
        }
        return ok
    }
}

// MARK: - Array chunks

extension Array {
    func chunked(_ size: Int) -> [[Element]] {
        stride(from: 0, to: count, by: size).map {
            Array(self[$0 ..< Swift.min($0 + size, count)])
        }
    }
}
