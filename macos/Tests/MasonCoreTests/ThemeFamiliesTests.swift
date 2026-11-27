import XCTest
@testable import MasonCore

final class ThemeFamiliesTests: XCTestCase {
    func testFindsFamiliesByNameOrSlug() {
        XCTAssertEqual(ThemeFamilies.all.map(\.slug), ["blueprint", "evergreen", "graphite", "kiln", "retro-neon"])
        XCTAssertEqual(ThemeFamilies.find("retro-neon"), ThemeFamilies.retroNeon)
        XCTAssertEqual(ThemeFamilies.find("  KILN "), ThemeFamilies.kiln)
        XCTAssertEqual(ThemeFamilies.find("plaid"), ThemeFamilies.blueprint)
        XCTAssertNil(ThemeFamilies.named("plaid"))
        XCTAssertNil(ThemeFamilies.named(""))
    }

    func testMixesChannelsLikeWindows() {
        XCTAssertEqual(ThemeFamilies.mix(0x000000, 0xFFFFFF, 0), 0x000000)
        XCTAssertEqual(ThemeFamilies.mix(0x000000, 0xFFFFFF, 1), 0xFFFFFF)
        // 0x10 + (0x11 - 0x10) * .5 = 16.5, which rounds to even (16) as .NET's Math.Round does.
        XCTAssertEqual(ThemeFamilies.mix(0x101010, 0x111111, 0.5), 0x101010)
        let blueprint = ThemeFamilies.blueprint.light
        XCTAssertEqual(ThemeFamilies.mix(blueprint.text, blueprint.background, 0.28), 0x555D75)
    }

    func testSavesTheFamilyBesideOtherAppearanceSettings() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("mason-theme-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: home) }
        XCTAssertEqual(ThemeFamilies.saved(home: home), ThemeFamilies.blueprint)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try Data(#"{"Theme":"Dark"}"#.utf8).write(to: home.appendingPathComponent("appearance.json"))
        try ThemeFamilies.save(ThemeFamilies.evergreen, home: home)
        XCTAssertEqual(ThemeFamilies.saved(home: home), ThemeFamilies.evergreen)
        let object = try JSONSerialization.jsonObject(with: Data(contentsOf: home.appendingPathComponent("appearance.json"))) as? [String: Any]
        XCTAssertEqual(object?["Theme"] as? String, "Dark")
        XCTAssertEqual(object?["Family"] as? String, "Evergreen")
    }
}
