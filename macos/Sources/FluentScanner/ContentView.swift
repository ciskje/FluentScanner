import AppKit
import SwiftUI

struct ContentView: View {
    @StateObject private var vm = ScannerVM()
    @EnvironmentObject private var settings: SettingsStore
    @Environment(\.colorScheme) private var colorScheme
    @State private var start = Scanner.localSubnet()?.start ?? "192.168.1.1"
    @State private var end = Scanner.localSubnet()?.end ?? "192.168.1.254"
    @State private var showHelp = false
    @State private var showAbout = false
    @State private var fits: [String: CGFloat] = [:]
    // Table ignores width changes on an existing view: bump the identity
    // so it rebuilds with the new ideals (scroll jumps to top, sort stays).
    @State private var tableID = 0

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            if vm.running { ProgressView(value: vm.progress).padding([.horizontal]) }
            // Native Table: resizable columns, keyboard navigation, row
            // selection for free — no hand-rolled header alignment needed.
            // Header clicks sort via the value: key paths (vm.resort keeps
            // the stored rows — and the CSV export — in the same order).
            Table(vm.rows, sortOrder: $vm.sort) {
                TableColumn(L("colIp"), value: \.sortIp) { r in
                    HStack(spacing: 6) {
                        Circle().fill(tint(for: r))
                            .frame(width: 8, height: 8)
                            .help(r.kind ?? "")
                        Text(r.ip).monospaced()
                            .fontWeight(r.isLocal ? .bold : .regular)
                            .foregroundStyle(r.isLocal ? selfColor : Color.primary)
                        if r.isLocal {
                            Image(systemName: "desktopcomputer")
                                .foregroundStyle(Color.accentColor)
                                .help(L("thisMac"))
                        }
                    }
                    
                }
                .width(min: 90, ideal: fitIdeal("ip", 150), max: fitMax("ip", 220))
                TableColumn(L("colHostname"), value: \.sortHostname) { r in
                    Text(r.hostname ?? "")
                        .fontWeight(r.isLocal ? .bold : .regular)
                        
                }
                    .width(min: 80, ideal: fitIdeal("hostname", 160), max: fitMax("hostname", 420))
                TableColumn(L("colMac"), value: \.sortMac) { r in
                    HStack(spacing: 4) {
                        Text(r.mac ?? "").monospaced()
                            .fontWeight(r.isLocal ? .bold : .regular)
                        if let m = r.mac, Scanner.isRandomized(m) {
                            Image(systemName: "shuffle")
                                .foregroundStyle(.secondary)
                                .help(L("randomMac"))
                        }
                    }
                    
                }
                    .width(min: 110, ideal: fitIdeal("mac", 150), max: fitMax("mac", 200))
                TableColumn(L("colVendor"), value: \.sortBrand) { r in
                    brandCell(r)
                }
                    .width(min: 90, ideal: fitIdeal("vendor", 150), max: fitMax("vendor", 300))
                TableColumn(L("colType"), value: \.sortKind) { r in
                    typeCell(r)
                }
                    .width(min: 70, ideal: fitIdeal("type", 110), max: fitMax("type", 200))
                TableColumn(L("colServices"), value: \.sortServices) { r in
                    servicesCell(r)
                }
                    .width(min: 120, ideal: fitIdeal("services", 200), max: fitMax("services", 560))
                TableColumn(L("colLatency"), value: \.sortLatency) { r in
                    Text(r.latencyText)
                        .monospaced()
                        .fontWeight(r.isLocal ? .bold : .regular)
                        
                }
                    .width(min: 50, ideal: fitIdeal("latency", 68), max: fitMax("latency", 110))
            }
            .font(settings.bodyFont)
            .frame(minWidth: 860)
            .id(tableID)
            .onChange(of: vm.sort) { vm.resort() }
            footer
        }
        .frame(minWidth: 860, minHeight: 460)
        .preferredColorScheme(settings.scheme)
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            TextField(L("startIp"), text: $start)
                .textFieldStyle(.roundedBorder)
                .frame(width: 132)
                .monospaced()
                .onSubmit { vm.toggle(start: start, end: end) }
            Text("–").foregroundStyle(.secondary)
            TextField(L("endIp"), text: $end)
                .textFieldStyle(.roundedBorder)
                .frame(width: 132)
                .monospaced()
                .onSubmit { vm.toggle(start: start, end: end) }
            Button(L("getIp")) {
                if let r = Scanner.localSubnet() { start = r.start; end = r.end }
            }
            .help(L("getIpHelp"))
            TextField(L("searchLabel"), text: $vm.filter)
                .textFieldStyle(.roundedBorder)
                .frame(width: 130)
            Button(vm.running ? L("stop") : L("start")) {
                vm.toggle(start: start, end: end)
            }
            .keyboardShortcut(.defaultAction)
            Button(L("exportCsv")) { vm.exportCSV() }
                .disabled(vm.rows.isEmpty || vm.running)
            Button { fitAll() } label: {
                Image(systemName: "arrow.left.and.right")
            }
            .help(L("fitAll"))
            .keyboardShortcut("f", modifiers: [.command, .shift])
            Button { showHelp = true } label: {
                Image(systemName: "questionmark.circle")
            }
            .help(L("helpTitle"))
            Button { showAbout = true } label: {
                Image(systemName: "info.circle")
            }
            .help(L("aboutTitle"))
        }
        .padding(8)
        .sheet(isPresented: $showHelp) { HelpView() }
        .sheet(isPresented: $showAbout) { AboutView() }
        .onReceive(NotificationCenter.default.publisher(for: .showAbout)) { _ in
            showAbout = true
        }
    }

    private var footer: some View {
        VStack(spacing: 2) {
            HStack(spacing: 10) {
                ForEach(Self.kinds, id: \.0) { name, color in
                    HStack(spacing: 3) {
                        Circle().fill(color).frame(width: 8, height: 8)
                        Text(name)
                    }
                }
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 8)
            .padding(.top, 4)
            HStack(spacing: 12) {
                HStack(spacing: 3) {
                    Image(systemName: "shuffle")
                    Text(L("randomMacLegend"))
                }
                HStack(spacing: 3) {
                    Image(systemName: "desktopcomputer")
                    Text(L("thisMacLegend"))
                }
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 8)
            Text(vm.status)
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 8)
                .padding(.bottom, 6)
        }
    }

    /// Self-host IP color: pure yellow vanishes on light backgrounds,
    /// so light mode gets a darker amber instead.
    private var selfColor: Color {
        colorScheme == .dark ? .yellow : Color(red: 0.7, green: 0.45, blue: 0.0)
    }

    private static func findTable(_ v: NSView) -> NSTableView? {
        if let tv = v as? NSTableView { return tv }
        for s in v.subviews { if let f = findTable(s) { return f } }
        return nil
    }

    /// Ideal width for a column: the fitted width once the fit button ran.
    private func fitIdeal(_ key: String, _ dflt: CGFloat) -> CGFloat {
        fits[key] ?? dflt
    }

    /// Max width, always at least the fitted width.
    private func fitMax(_ key: String, _ dflt: CGFloat) -> CGFloat {
        max(dflt, fits[key] ?? dflt)
    }

    /// Measured content width for a column (header + rows).
    private func fittedWidth(_ key: String) -> CGFloat {
        let vals: [String]
        let mono: Bool
        switch key {
        case "hostname":
            vals = [L("colHostname")] + vm.rows.map { $0.hostname ?? "" }; mono = false
        case "mac":
            vals = [L("colMac")] + vm.rows.map { $0.mac ?? "" }; mono = true
        case "vendor":
            vals = [L("colVendor")] + vm.rows.map { $0.vendor ?? $0.server ?? "" }; mono = false
        case "type":
            vals = [L("colType")] + vm.rows.map { $0.kind ?? "" }; mono = false
        case "services":
            vals = [L("colServices")] + vm.rows.map { $0.services ?? "" }; mono = false
        case "latency":
            vals = [L("colLatency")] + vm.rows.map(\.latencyText); mono = true
        default:
            vals = [L("colIp")] + vm.rows.map(\.ip); mono = true
        }
        let font = mono
            ? NSFont.monospacedSystemFont(ofSize: settings.fontSize, weight: .regular)
            : NSFont.systemFont(ofSize: settings.fontSize)
        let w = vals.map { ($0 as NSString).size(withAttributes: [.font: font]).width }.max() ?? 0
        return min(600, max(70, w + 30))
    }

    /// Fit every column at once (toolbar button + Cmd+Shift+F).
    private func fitAll() {
        for key in ["ip", "hostname", "mac", "vendor", "type", "services", "latency"] {
            fits[key] = fittedWidth(key)
        }
        pinchFits()
        tableID += 1
    }

    /// Scale fitted widths so their sum fits the visible table (Aqua min
    /// widths respected): fitting must never push a column out of view.
    private func pinchFits() {
        guard
            let tv = NSApp.windows.compactMap({ ($0.isVisible
                && $0.contentView != nil) ? Self.findTable($0.contentView!) : nil }).first
        else { return }
        let visible = tv.enclosingScrollView?.contentView.bounds.width ?? tv.frame.width
        let mins: [String: CGFloat] = ["ip": 90, "hostname": 80, "mac": 110,
            "vendor": 90, "type": 70, "services": 120, "latency": 50]
        let dflt: [String: CGFloat] = ["ip": 150, "hostname": 160, "mac": 150,
            "vendor": 150, "type": 110, "services": 200, "latency": 68]
        let sum = mins.keys.reduce(0.0) { $0 + (fits[$1] ?? dflt[$1] ?? 100) }
        guard sum > visible, visible > 400 else { return }
        let k = visible / sum
        for key in mins.keys {
            fits[key] = max(mins[key] ?? 60, floor((fits[key] ?? dflt[key] ?? 100) * k))
        }
    }

    /// Services cell: one Text (markdown links for HTTP/HTTPS) so the
    /// whole cell truncates on a single line instead of wrapping per entry.
    private func servicesCell(_ r: HostRow) -> some View {
        Text(servicesAttributed(r))
            .lineLimit(1)
            .truncationMode(.middle)
            .help(r.services ?? "")
            .fontWeight(r.isLocal ? .bold : .regular)
    }

    private func servicesAttributed(_ r: HostRow) -> AttributedString {
        (try? AttributedString(
            markdown: Scanner.servicesMarkdown(ip: r.ip, ports: r.ports,
                                               server: r.server),
            options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(r.services ?? "")
    }

    /// Device-type palette, shared by the row dots and the footer legend
    /// (first match wins — `kind` can combine several with " · ").
    private static let kinds: [(String, Color)] = [
        ("Printer", .orange), ("NAS", .purple), ("Router/AP", .green),
        ("Windows host", .blue), ("Raspberry Pi", .pink),
        ("Apple device", .gray), ("IoT device", .yellow), ("PC", .teal),
    ]

    private static let brandPalette: [Color] = [
        .blue, .green, .orange, .purple, .pink, .red, .teal, .indigo,
        .brown, .cyan,
    ]

    /// Stable brand color: the same vendor always lands on the same slot.
    private func brandColor(_ vendor: String) -> Color {
        var h: UInt = 0
        for s in vendor.unicodeScalars { h = h &* 31 &+ UInt(s.value) }
        return Self.brandPalette[Int(h % UInt(Self.brandPalette.count))]
    }

    private func brandCell(_ r: HostRow) -> some View {
        let brand = r.vendor ?? r.server // HTTP software fills OUI silence
        return HStack(spacing: 0) {
            Text(brand ?? "")
                .foregroundStyle(brand.map(brandColor) ?? .primary)
        }
        .lineLimit(1)
        .truncationMode(.middle)
        .help(r.vendorText)
        .fontWeight(r.isLocal ? .bold : .regular)
    }

    private func typeCell(_ r: HostRow) -> some View {
        Text(r.kind ?? "")
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .truncationMode(.middle)
            .help(r.vendorText)
            .fontWeight(r.isLocal ? .bold : .regular)
    }
    /// One dot color per device hint (unknown stays unfilled).
    private func tint(for r: HostRow) -> Color {
        let k = r.kind ?? ""
        return Self.kinds.first { k.contains($0.0) }?.1 ?? .clear
    }
}
