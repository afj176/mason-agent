import XCTest
@testable import MasonCore

/// The core behind /agent new, /commands new, /reasoning and /theme light|dark|system.
final class CliParityTests: XCTestCase {
    var folder: URL!

    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-cli-parity-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder) }

    func testNewAgentAndCommandStartFromTheTemplate() throws {
        let workspace = try Workspace(path: folder.path)
        let agent = try Customization.create("careful", command: false, workspace: workspace)
        let command = try Customization.create("ship", command: true, workspace: workspace)
        XCTAssertTrue(agent.path.hasSuffix(".mason/agents/careful.md"))
        XCTAssertTrue(command.path.hasSuffix(".mason/commands/ship.md"))
        let text = try String(contentsOf: agent, encoding: .utf8)
        XCTAssertTrue(text.hasPrefix("---\ndescription: \n---\n\n# careful\n"))
        let home = folder.appendingPathComponent("home")
        XCTAssertEqual(Customization.agents(home: home, workspace: workspace.root.path).map(\.name), ["careful"])
        XCTAssertEqual(Customization.commands(home: home, workspace: workspace.root.path).map(\.name), ["ship"])
        XCTAssertThrowsError(try Customization.create("careful", command: false, workspace: workspace))
        XCTAssertThrowsError(try Customization.create("../out", command: true, workspace: workspace))
        XCTAssertThrowsError(try Customization.create("compact", command: true, workspace: workspace))
    }

    func testSessionReasoningGoesInTheExtraBodyLikeChat() throws {
        let client = try OpenAIClient(ModelEndpoint(url: "http://127.0.0.1:9", model: "m"))
        Agent.applyReasoning(nil, to: client)
        XCTAssertNil(client.extraBody["chat_template_kwargs"])
        Agent.applyReasoning("off", to: RoleModel(client, role: "implementer"))
        XCTAssertEqual(client.extraBody["reasoning_effort"] as? String, "none")
        XCTAssertEqual(client.extraBody["thinking_budget_tokens"] as? Int, 0)
        Agent.applyReasoning("high", to: client)
        XCTAssertEqual(client.extraBody["thinking_budget_tokens"] as? Int, 8192)
        XCTAssertEqual((client.extraBody["chat_template_kwargs"] as? [String: Bool])?["enable_thinking"], true)
        // A session saved before the setting existed still loads.
        var session = Session(workspace: folder.path)
        session.reasoning = "low"
        let data = try JSONEncoder().encode(session)
        var object = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        XCTAssertEqual(object["reasoning"] as? String, "low")
        object["reasoning"] = nil
        XCTAssertNil(try JSONDecoder().decode(Session.self, from: JSONSerialization.data(withJSONObject: object)).reasoning)
    }

    func testAppearanceModeIsSavedWhereTheAppReadsIt() throws {
        let domain = "site.masonagent.tests." + UUID().uuidString
        defer { UserDefaults(suiteName: domain)?.removePersistentDomain(forName: domain) }
        XCTAssertEqual(AppearanceMode.saved(domain: domain), .system)
        try AppearanceMode.save(.dark, domain: domain)
        XCTAssertEqual(UserDefaults(suiteName: domain)?.string(forKey: "appearance"), "Dark")
        XCTAssertEqual(AppearanceMode.saved(domain: domain), .dark)
        XCTAssertEqual(AppearanceMode.named("LIGHT"), .light)
        XCTAssertEqual(AppearanceMode.named("system"), .system)
        XCTAssertNil(AppearanceMode.named("kiln"))
    }
}
