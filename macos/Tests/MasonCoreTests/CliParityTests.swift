import XCTest
@testable import MasonCore

/// A server that answers HTTP 400 to any request that offers tools, as servers without tool support do.
final class ToolRefusingModel: ChatModel {
    let name = "refuses-tools-" + UUID().uuidString
    var seenTools: [[String]] = []
    func complete(messages: [ChatMessage], tools: [ToolSpec], onText: @escaping (String) -> Void) async throws -> ModelReply {
        seenTools.append(tools.map(\.name))
        if !tools.isEmpty { throw MasonError.http(400, "The model server returned HTTP 400. {\"error\":\"tools param requires --jinja flag\"}") }
        return ModelReply(text: seenTools.count == 2 ? "Hi without tools." : "Again without tools.")
    }
}

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

    func testWithAutomaticCompactionOffAFullContextStopsTheRunUncompacted() async throws {
        let home = folder.appendingPathComponent("home")
        let store = try SessionStore(home: home)
        var session = try store.create(workspace: try Workspace(path: folder.path).root.path)
        XCTAssertTrue(session.compactsAutomatically, "on by default")
        session.messages = [ChatMessage(role: "system", content: "s"), ChatMessage(role: "user", content: "one"), ChatMessage(role: "assistant", content: "two"),
                            ChatMessage(role: "user", content: "three"), ChatMessage(role: "assistant", content: "four")]
        session.promptTokens = 990
        session.automaticCompaction = false
        let model = ScriptedModel([ModelReply(text: "A summary."), ModelReply(text: "Done.")])
        model.window = 1000
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Go on")
        XCTAssertEqual(session.state, "Failed")
        XCTAssertEqual(session.detail, Compaction.fullMessage)
        XCTAssertNil(session.compactions, "nothing was summarized")
        XCTAssertTrue(model.seen.isEmpty, "the model was not asked")
        XCTAssertEqual(try Compaction.autoText("", current: false).text, "Automatic compaction is off · /compact auto on | off")
        XCTAssertEqual(try Compaction.autoText("on", current: false).on, true)
        XCTAssertThrowsError(try Compaction.autoText("sometimes", current: true))

        // A session saved before the setting existed decodes as on.
        let data = try JSONEncoder().encode(Session(workspace: folder.path))
        var object = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        XCTAssertNil(object["automaticCompaction"])
        object["automaticCompaction"] = nil
        XCTAssertTrue(try JSONDecoder().decode(Session.self, from: JSONSerialization.data(withJSONObject: object)).compactsAutomatically)
        XCTAssertTrue(try JSONDecoder().decode(ChatConversation.self, from: JSONEncoder().encode(ChatConversation())).compactsAutomatically)
    }

    func testAChatWithAutomaticCompactionOffStopsWhenFull() async throws {
        let chats = try ChatStore(home: folder.appendingPathComponent("home"))
        var conversation = try chats.create()
        conversation.automaticCompaction = false
        conversation.promptTokens = 995
        let model = ScriptedModel([ModelReply(text: "A summary."), ModelReply(text: "Hi.")])
        model.window = 1000
        do {
            try await Chat.send(&conversation, text: "Hello", model: model, store: chats) { _ in }
            XCTFail("a full chat with compaction off stops")
        } catch { XCTAssertEqual((error as? MasonError)?.description, Compaction.chatFullMessage) }
        XCTAssertTrue(model.seen.isEmpty)
    }

    func testAServerThatRefusesToolsGetsTheMessageAgainWithoutThem() async throws {
        let home = folder.appendingPathComponent("home")
        let skill = home.appendingPathComponent("skills/notes/SKILL.md")
        try FileManager.default.createDirectory(at: skill.deletingLastPathComponent(), withIntermediateDirectories: true)
        try "---\nname: notes\ndescription: Notes\n---\n\nTake notes.\n".write(to: skill, atomically: true, encoding: .utf8)
        let chats = try ChatStore(home: home)
        var conversation = try chats.create()
        let model = ToolRefusingModel()
        XCTAssertTrue(Chat.canCallTools(model))
        XCTAssertFalse(Chat.offeredTools(conversation, model: model, home: home).isEmpty, "the skill is offered")
        try await Chat.send(&conversation, text: "Hello", model: model, store: chats, tools: ChatTools(home: home, approve: { _ in .no })) { _ in }
        XCTAssertEqual(model.seenTools.count, 2)
        XCTAssertTrue(model.seenTools[0].contains("read_skill") && model.seenTools[1].isEmpty, "refused once with tools, then answered without them")
        XCTAssertEqual(conversation.messages.last?.content, "Hi without tools.")
        XCTAssertFalse(Chat.canCallTools(model), "the connection is remembered as unable to call tools")
        XCTAssertTrue(Chat.offeredTools(conversation, model: model, home: home).isEmpty)
        try await Chat.send(&conversation, text: "Again", model: model, store: chats, tools: ChatTools(home: home, approve: { _ in .no })) { _ in }
        XCTAssertEqual(model.seenTools.last, [], "later messages go without tools")
        XCTAssertTrue(Chat.refusesTools(MasonError.http(501, "The model server returned HTTP 501.")))
        XCTAssertTrue(Chat.refusesTools(MasonError.http(500, "The model server returned HTTP 500. tools are not supported")))
        XCTAssertFalse(Chat.refusesTools(MasonError.http(500, "The model server returned HTTP 500.")))
        XCTAssertFalse(Chat.refusesTools(MasonError("tool")), "not an answer from the server")
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
