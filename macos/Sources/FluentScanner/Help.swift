import AppKit
import SwiftUI

extension Notification.Name {
    static let showAbout = Notification.Name("FluentScanner.showAbout")
    static let showChangelog = Notification.Name("FluentScanner.showChangelog")
}

/// Opens the bundled Apple Help Book (chapters + menu search) in Help Viewer.
/// Registers the embedded .help bundle explicitly first: helpd's
/// launch-time scan can miss it (fresh installs, moved bundles), and then
/// Help Viewer silently falls back to the macOS manual. Page-based goto
/// (not anchor lookup) opens the landing page; the C functions are resolved
/// at runtime so a missing symbol can never break the build.
func openHelpBook() {
    guard let folder = Bundle.main.object(forInfoDictionaryKey: "CFBundleHelpBookFolder") as? String else { return }
    let helpURL = Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/\(folder)")
    guard FileManager.default.fileExists(atPath: helpURL.path),
        let lib = dlopen("/System/Library/Frameworks/Carbon.framework/Versions/A/Frameworks/Help.framework/Help", RTLD_LAZY),
        let regSym = dlsym(lib, "AHRegisterHelpBookWithURL"),
        let gotoSym = dlsym(lib, "AHGotoPage"),
        let helpBundle = Bundle(url: helpURL),
        let title = helpBundle.object(forInfoDictionaryKey: "HPDBookTitle") as? String
    else { return }
    typealias RegisterFn = @convention(c) (CFURL) -> Int32
    typealias GotoFn = @convention(c) (CFString, CFString, CFString?) -> Int32
    _ = unsafeBitCast(regSym, to: RegisterFn.self)(helpURL as CFURL)
    _ = unsafeBitCast(gotoSym, to: GotoFn.self)(title as CFString, "index.html" as CFString, nil)
}

/// Version changelog sheet (the Help menu's second item opens it).
struct ChangelogView: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("changelogTitle"))
                .font(.title2.weight(.semibold))
            ScrollView {
                Text(L("changelogBody"))
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            HStack {
                Spacer()
                Button(L("closeBtn")) { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 520, height: 480)
    }
}

/// Fuller About sheet (toolbar ⓘ and the app menu open it).
struct AboutView: View {
    @Environment(\.dismiss) private var dismiss

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    }

    var body: some View {
        VStack(spacing: 10) {
            if let img = NSImage(named: "AppIcon") {
                Image(nsImage: img)
                    .resizable()
                    .frame(width: 72, height: 72)
            }
            Text("FluentScanner \(version)")
                .font(.title2.weight(.semibold))
            Text(L("aboutDesc"))
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
            Text(L("aboutOui"))
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(L("aboutLicense"))
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button(L("closeBtn")) { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 6)
        }
        .padding(24)
        .frame(width: 440)
    }
}
