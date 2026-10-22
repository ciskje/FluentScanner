import Foundation

/// UI strings: .lproj lookup, with a Settings language override winning
/// over the system preferred localization; in a manual .app the
/// Resources/<lang>.lproj bundles load fine without MRT.
/// Supported UI localizations (EFIGS). A forced or system language outside
/// this list falls back to English — never raw keys.
private let supportedLocalizations = ["en", "fr", "it", "de", "es"]

func L(_ key: String, _ args: CVarArg...) -> String {
    var code: String?
    if let forced = UserDefaults.standard.string(forKey: "language"),
        supportedLocalizations.contains(forced) {
        code = forced
    } else if let first = Locale.preferredLanguages.first {
        let sys = String(first.prefix(2)).lowercased()
        code = supportedLocalizations.contains(sys) ? sys : "en"
    }
    let bundle = code.flatMap { Bundle.main.path(forResource: $0, ofType: "lproj") }
        .flatMap { Bundle(path: $0) }
    let fmt = bundle?.localizedString(forKey: key, value: key, table: nil)
        ?? key
    return args.isEmpty ? fmt : String(format: fmt, arguments: args)
}
