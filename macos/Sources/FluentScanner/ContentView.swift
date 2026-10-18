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
                .width(min: 90, ideal: 150, max: 220)
                TableColumn(L("colHostname"), value: \.sortHostname) { r in
                    Text(r.hostname ?? "")
                        .fontWeight(r.isLocal ? .bold : .regular)
                }
                    .width(min: 80, ideal: 160, max: 420)
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
                    .width(min: 110, ideal: 150, max: 200)
                TableColumn(L("colVendor"), value: \.sortBrand) { r in brandCell(r) }
                    .width(min: 90, ideal: 150, max: 300)
                TableColumn(L("colType"), value: \.sortKind) { r in typeCell(r) }
                    .width(min: 70, ideal: 110, max: 200)
                TableColumn(L("colServices"), value: \.sortServices) { r in servicesCell(r) }
                    .width(min: 120, ideal: 200, max: 560)
                TableColumn(L("colLatency"), value: \.sortLatency) { r in
                    Text(r.latencyText).monospaced()
                        .fontWeight(r.isLocal ? .bold : .regular)
                }
                    .width(min: 50, ideal: 68, max: 110)
            }
            .font(settings.bodyFont)
            .frame(minWidth: 860)
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
