import SwiftUI

/// Persisted UI prefs (UserDefaults, entitlement-free): language override,
/// table font size + mono toggle, theme override. Observed by ContentView
/// so changes apply live (`L()` re-evaluates in body).
final class SettingsStore: ObservableObject {
    @Published var language: String {
        didSet { UserDefaults.standard.set(language, forKey: "language") }
    }
    @Published var fontSize: Double {
        didSet { UserDefaults.standard.set(fontSize, forKey: "fontSize") }
    }
    @Published var monoBody: Bool {
        didSet { UserDefaults.standard.set(monoBody, forKey: "monoBody") }
    }
    @Published var theme: String {
        didSet { UserDefaults.standard.set(theme, forKey: "theme") }
    }
    @Published var discoveryTimeout: Double {
        didSet { UserDefaults.standard.set(discoveryTimeout, forKey: "discoveryTimeout") }
    }
    @Published var portTimeout: Double {
        didSet { UserDefaults.standard.set(portTimeout, forKey: "portTimeout") }
    }
    @Published var pingTimeoutMs: Double {
        didSet { UserDefaults.standard.set(pingTimeoutMs, forKey: "pingTimeoutMs") }
    }

    var scheme: ColorScheme? {
        switch theme {
        case "light": return .light
        case "dark": return .dark
        default: return nil
        }
    }

    var bodyFont: Font {
        .system(size: fontSize, design: monoBody ? .monospaced : .default)
    }

    init() {
        let d = UserDefaults.standard
        language = d.string(forKey: "language") ?? "system"
        fontSize = d.object(forKey: "fontSize") as? Double ?? 12
        monoBody = d.bool(forKey: "monoBody")
        theme = d.string(forKey: "theme") ?? "system"
        discoveryTimeout = d.object(forKey: "discoveryTimeout") as? Double ?? 2.0
        portTimeout = d.object(forKey: "portTimeout") as? Double ?? 1.5
        pingTimeoutMs = d.object(forKey: "pingTimeoutMs") as? Double ?? 1000
    }
}

struct SettingsView: View {
    @EnvironmentObject private var settings: SettingsStore
    @State private var discText = ""
    @State private var scanText = ""
    @State private var portsError: String?

    private func validatePorts() {
        let bad = Scanner.parsePorts(discText) == nil
            || Scanner.parsePorts(scanText) == nil
        portsError = bad ? L("portsInvalid") : nil
    }

    var body: some View {
        Form {
            Picker(L("languageLabel"), selection: $settings.language) {
                Text(L("languageSystem")).tag("system")
                Text("English").tag("en")
                Text("Italiano").tag("it")
            }
            Slider(value: $settings.fontSize, in: 10 ... 16, step: 1) {
                Text("\(L("fontSizeLabel")): \(Int(settings.fontSize))")
            } minimumValueLabel: {
                Text("10")
            } maximumValueLabel: {
                Text("16")
            }
            Toggle(L("monoLabel"), isOn: $settings.monoBody)
            Picker(L("themeLabel"), selection: $settings.theme) {
                Text(L("themeSystem")).tag("system")
                Text(L("themeLight")).tag("light")
                Text(L("themeDark")).tag("dark")
            }
            Slider(value: $settings.pingTimeoutMs, in: 500 ... 2000, step: 250) {
                Text("\(L("pingTimeoutLabel")): \(Int(settings.pingTimeoutMs)) ms")
            }
            Slider(value: $settings.discoveryTimeout, in: 0.5 ... 3, step: 0.5) {
                Text("\(L("discTimeoutLabel")): \(settings.discoveryTimeout, specifier: "%.1f") s")
            }
            Slider(value: $settings.portTimeout, in: 0.5 ... 3, step: 0.5) {
                Text("\(L("portTimeoutLabel")): \(settings.portTimeout, specifier: "%.1f") s")
            }
            TextField(L("discPortsLabel"), text: $discText)
                .onAppear {
                    discText = Scanner.discoveryPorts.map(String.init)
                        .joined(separator: ", ")
                }
                .onChange(of: discText) { validatePorts() }
                .onSubmit {
                    if Scanner.parsePorts(discText) != nil {
                        UserDefaults.standard.set(discText, forKey: "discoveryPortsText")
                    }
                }
            TextField(L("scanPortsLabel"), text: $scanText)
                .onAppear {
                    scanText = Scanner.scanPorts.map(String.init)
                        .joined(separator: ", ")
                }
                .onChange(of: scanText) { validatePorts() }
                .onSubmit {
                    if Scanner.parsePorts(scanText) != nil {
                        UserDefaults.standard.set(scanText, forKey: "scanPortsText")
                    }
                }
            if let err = portsError {
                Text(err).foregroundStyle(.red).font(.caption)
            }
        }
        .padding(20)
        .frame(width: 500)
    }
}
