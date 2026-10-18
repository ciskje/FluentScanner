import AppKit
import SwiftUI

@main
struct FluentScannerApp: App {
    @StateObject private var settings = SettingsStore()

    init() {
        // Headless self-test (see macos/AGENTS.md): FS_SELFTEST=1 <binary>
        if ProcessInfo.processInfo.environment["FS_SELFTEST"] == "1" {
            let ok = Scanner.selfTest()
            fputs(ok ? "SELFTEST OK\n" : "SELFTEST FAILED\n", stderr)
            exit(ok ? 0 : 1)
        }
    }

    var body: some Scene {
        WindowGroup("FluentScanner") {
            ContentView()
                .environmentObject(settings)
        }
        .defaultSize(width: 760, height: 520)
        .commands {
            CommandGroup(replacing: .newItem) {}
            CommandGroup(replacing: .appInfo) {
                Button(L("aboutMenu")) {
                    NotificationCenter.default.post(name: .showAbout, object: nil)
                }
            }
        }
        Settings {
            SettingsView()
                .environmentObject(settings)
        }
    }
}
