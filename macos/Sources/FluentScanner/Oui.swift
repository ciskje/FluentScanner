import Foundation

/// OUI vendor lookup from the bundled `oui.csv` ("aabbcc<TAB>Org", one line
/// per prefix; generated once from the IEEE registry — see macos/AGENTS.md).
enum Oui {
    private static var vendors: [String: String]?

    /// Vendor org for a normalized "aa:bb:cc:dd:ee:ff" MAC (nil when the
    /// table is missing, the prefix is unknown, or the MAC is randomized —
    /// a fake prefix would blame the wrong maker). CSV keys are colon-less
    /// ("2083f8"), so strip the separators before the lookup.
    static func vendor(_ mac: String) -> String? {
        guard !Scanner.isRandomized(mac) else { return nil }
        load()
        let key = mac.prefix(8).replacingOccurrences(of: ":", with: "")
        guard key.count == 6, let v = vendors?[key] else { return nil }
        return v
    }

    private static func load() {
        guard vendors == nil else { return }
        vendors = [:]
        guard let url = Bundle.main.url(forResource: "oui", withExtension: "csv"),
            let text = try? String(contentsOf: url, encoding: .utf8) else {
            return // missing table: vendor column stays empty
        }
        for line in text.split(separator: "\n") {
            let p = line.split(separator: "\t", maxSplits: 1)
            guard p.count == 2 else { continue }
            vendors?[String(p[0])] = String(p[1])
        }
    }
}
