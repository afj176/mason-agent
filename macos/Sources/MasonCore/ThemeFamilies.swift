import Foundation

/// One mode of a theme family: the seven approved tokens. Everything else is derived from them.
public struct ThemeTokens: Equatable, Hashable, Sendable {
    public let background, pane, card, line, text, accent, onAccent: UInt32
    public init(_ background: UInt32, _ pane: UInt32, _ card: UInt32, _ line: UInt32, _ text: UInt32, _ accent: UInt32, _ onAccent: UInt32) {
        self.background = background; self.pane = pane; self.card = card; self.line = line; self.text = text; self.accent = accent; self.onAccent = onAccent
    }
}

/// An approved theme family with its light and dark modes.
public struct ThemeFamily: Equatable, Hashable, Identifiable, Sendable {
    public let name: String
    public let light: ThemeTokens
    public let dark: ThemeTokens
    public var id: String { name }
    public func tokens(dark isDark: Bool) -> ThemeTokens { isDark ? dark : light }
    /// The lower-case, hyphenated name used by the CLI and in files ("retro-neon").
    public var slug: String { name.lowercased().replacingOccurrences(of: " ", with: "-") }
}

/// The five approved theme families, the same values as the Windows app (Mason.Code.ThemeFamilies).
/// The Mac app derives every brand colour from them; the CLI uses each family's accent.
public enum ThemeFamilies {
    public static let blueprint = ThemeFamily(name: "Blueprint",
        light: ThemeTokens(0xF3F5FC, 0xE6EAF7, 0xE3E8F6, 0xD5DCF0, 0x172240, 0x374FAB, 0xFFFFFF),
        dark: ThemeTokens(0x151B30, 0x212A44, 0x232C46, 0x2F3852, 0xF4F6FF, 0xB4B8FF, 0x151B30))
    public static let evergreen = ThemeFamily(name: "Evergreen",
        light: ThemeTokens(0xF3F7F0, 0xE5EEE2, 0xE2ECE0, 0xD4E4D6, 0x18362A, 0x22603F, 0xFFFFFF),
        dark: ThemeTokens(0x111E19, 0x1C2D24, 0x1E3027, 0x2A3F33, 0xF0FBF2, 0x95DDAF, 0x111E19))
    public static let graphite = ThemeFamily(name: "Graphite",
        light: ThemeTokens(0xF6F7F9, 0xE9EBEF, 0xE6E9ED, 0xD9DDE4, 0x161A22, 0x124FBB, 0xFFFFFF),
        dark: ThemeTokens(0x141619, 0x202328, 0x23262B, 0x2F333A, 0xF6F7FA, 0x87B0FF, 0x141619))
    public static let kiln = ThemeFamily(name: "Kiln",
        light: ThemeTokens(0xFBF5EE, 0xF1E6DA, 0xEFE3D7, 0xE7D7C8, 0x35231E, 0x9F4125, 0xFFFFFF),
        dark: ThemeTokens(0x211915, 0x2F241E, 0x322620, 0x413129, 0xFFF1E6, 0xF2AB7E, 0x211915))
    public static let retroNeon = ThemeFamily(name: "Retro Neon",
        light: ThemeTokens(0xF7F0FE, 0xEEE2FA, 0xEDE0F9, 0xE1D3F2, 0x281132, 0x8F267B, 0xFFFFFF),
        dark: ThemeTokens(0x191126, 0x271835, 0x2A1937, 0x352146, 0xFFF4FF, 0xDD8BC8, 0x191126))

    public static let all: [ThemeFamily] = [blueprint, evergreen, graphite, kiln, retroNeon]
    public static var `default`: ThemeFamily { blueprint }

    /// The family with this display name or slug, ignoring case; nil for an unknown name.
    public static func named(_ name: String?) -> ThemeFamily? {
        guard let name, !name.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        let key = name.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "-", with: " ")
        return all.first { $0.name.caseInsensitiveCompare(key) == .orderedSame }
    }
    /// The family with this name or slug; unknown names give the default.
    public static func find(_ name: String?) -> ThemeFamily { named(name) ?? `default` }

    /// Mixes two 0xRRGGBB colours: amount 0 gives `a`, 1 gives `b`. Rounds half to even, as the Windows app does.
    public static func mix(_ a: UInt32, _ b: UInt32, _ amount: Double) -> UInt32 {
        func blend(_ shift: UInt32) -> UInt32 {
            let x = Double((a >> shift) & 0xFF), y = Double((b >> shift) & 0xFF)
            return UInt32(max(0, min(255, (x + (y - x) * amount).rounded(.toNearestOrEven))))
        }
        return (blend(16) << 16) | (blend(8) << 8) | blend(0)
    }

    /// The red, green and blue channels of a 0xRRGGBB colour, 0 to 255.
    public static func channels(_ rgb: UInt32) -> (red: Int, green: Int, blue: Int) {
        (Int((rgb >> 16) & 0xFF), Int((rgb >> 8) & 0xFF), Int(rgb & 0xFF))
    }

    /// The theme family saved in appearance.json ("Family") in Mason's data folder, shared by the app and the CLI.
    public static func saved(home: URL = DataHome.url) -> ThemeFamily {
        let url = home.appendingPathComponent("appearance.json")
        guard let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return `default` }
        return find(object["Family"] as? String)
    }

    /// Saves the family into appearance.json, keeping every other appearance setting written there.
    public static func save(_ family: ThemeFamily, home: URL = DataHome.url) throws {
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let url = home.appendingPathComponent("appearance.json")
        var values: [String: Any] = [:]
        if let data = try? Data(contentsOf: url), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { values = object }
        values["Family"] = family.name
        let data = try JSONSerialization.data(withJSONObject: values, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: url, options: .atomic)
    }
}
