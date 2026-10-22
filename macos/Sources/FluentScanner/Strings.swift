import Foundation

/// UI strings: .lproj lookup, with a Settings language override winning
/// over the system preferred localization; in a manual .app the
/// Resources/<lang>.lproj bundles load fine without MRT.
func L(_ key: String, _ args: CVarArg...) -> String {
    var code: String?
    if let forced = UserDefaults.standard.string(forKey: "language"),
        forced == "en" || forced == "it" {
        code = forced
    } else if let first = Locale.preferredLanguages.first {
        // Outside EN/IT the UI shows Italian (same rule as Windows UiLang),
        // never raw keys.
        code = String(first.prefix(2)).lowercased() == "en" ? "en" : "it"
    }
    let bundle = code.flatMap { Bundle.main.path(forResource: $0, ofType: "lproj") }
        .flatMap { Bundle(path: $0) }
    let fmt = bundle?.localizedString(forKey: key, value: key, table: nil)
        ?? key
    return args.isEmpty ? fmt : String(format: fmt, arguments: args)
}
