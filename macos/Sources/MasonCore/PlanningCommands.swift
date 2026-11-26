import Foundation

// The typed forms of comments, milestones and the embeddings choice, shared by the command line and the tests.
// Each takes what followed the command name and returns what to tell the user.

extension Review {
    public static let help = """
    /comment PATH:LINE NOTE     Pin a note to a line; it is sent with your next request
    /comment                    List the notes waiting to be sent
    /comment remove NUMBER      Drop one    ·    /comment clear   drops them all
    /comment send               Send the notes now, as a request of their own
    """

    /// Everything except "send", which needs a model.
    public static func command(_ rest: String, workspace: Workspace, session: inout Session) throws -> String {
        let words = rest.split(separator: " ", maxSplits: 1).map(String.init)
        var comments = session.comments ?? []
        switch words.first?.lowercased() {
        case nil:
            if comments.isEmpty { return "No comments are waiting.\n" + help }
            return comments.enumerated().map { "\($0.offset + 1). \($0.element.location) — \($0.element.note)" }.joined(separator: "\n") + "\nThey are sent with your next request."
        case "help": return help
        case "clear":
            session.comments = nil
            return "Dropped \(comments.count) comment\(comments.count == 1 ? "" : "s")."
        case "remove":
            guard let number = words.count > 1 ? Int(words[1]) : nil, comments.indices.contains(number - 1) else { throw MasonError("Give the number of a comment. /comment lists them.") }
            let removed = comments.remove(at: number - 1)
            session.comments = comments.isEmpty ? nil : comments
            return "Dropped the comment on \(removed.location)."
        default:
            guard words.count == 2, let colon = words[0].lastIndex(of: ":"), let line = Int(words[0][words[0].index(after: colon)...]) else { throw MasonError("Write it as /comment PATH:LINE NOTE, for example /comment src/app.py:12 Use the helper here.") }
            guard comments.count < limit else { throw MasonError("At most \(limit) comments can wait at once.") }
            let made = try Review.comment(workspace, path: String(words[0][..<colon]), line: line, note: words[1])
            session.comments = comments + [made]
            return "Noted on \(made.location): \(made.quote.trimmingCharacters(in: .whitespaces).prefix(100))"
        }
    }
}

extension Milestones {
    public static let help = """
    /milestone                                  List the plan and where each milestone stands
    /milestone add TITLE | FILES [| after N,N] [| workflow NAME] [| decision TEXT]
                                                Add one. FILES are project paths separated by commas
    /milestone check NUMBER [WHAT YOU SAW]      Run its workflow, or record what you observed
    /milestone remove NUMBER
    """

    public static func list(_ workspace: Workspace, plan: [Milestone]) -> String {
        if plan.isEmpty { return "No milestones yet.\n" + help }
        return inspect(workspace, plan: plan).enumerated().map { index, row in
            let after = row.item.dependsOn.compactMap { id in plan.firstIndex { $0.id == id }.map { String($0 + 1) } }
            var text = "\(index + 1). [\(row.state.rawValue)] \(row.item.title)  ·  " + row.item.files.joined(separator: ", ")
            if !after.isEmpty { text += "  ·  after " + after.joined(separator: ", ") }
            if !row.item.workflow.isEmpty { text += "  ·  workflow \(row.item.workflow)" }
            return text + "\n   " + String(row.reason.split(separator: "\n").first ?? "")
        }.joined(separator: "\n")
    }

    /// `home` is where saved approval rules live; they decide whether a workflow may run outside Auto.
    public static func command(_ rest: String, workspace: Workspace, session: inout Session, home: URL = DataHome.url) async throws -> String {
        var plan = session.milestones ?? []
        let words = rest.split(separator: " ", maxSplits: 1).map(String.init)
        func numbered(_ text: String?) throws -> Int {
            guard let number = text.flatMap({ Int($0.trimmingCharacters(in: .whitespaces)) }), plan.indices.contains(number - 1) else { throw MasonError("Give the number of a milestone. /milestone lists them.") }
            return number - 1
        }
        switch words.first?.lowercased() {
        case nil: return list(workspace, plan: plan)
        case "help": return help
        case "add":
            let fields = (words.count > 1 ? words[1] : "").split(separator: "|").map { $0.trimmingCharacters(in: .whitespaces) }
            guard fields.count >= 2 else { throw MasonError("Write it as /milestone add TITLE | FILES, for example /milestone add Totals are right | src/totals.py, tests/test_totals.py") }
            var item = Milestone(title: fields[0], files: fields[1].split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty })
            for field in fields.dropFirst(2) {
                let parts = field.split(separator: " ", maxSplits: 1).map(String.init)
                let value = parts.count > 1 ? parts[1] : ""
                switch parts.first?.lowercased() {
                case "after": item.dependsOn = try value.split(separator: ",").map { plan[try numbered(String($0))].id }
                case "workflow":
                    _ = try workflow(workspace, value)
                    item.workflow = value
                case "decision": item.decision = value
                default: throw MasonError("After the files, use: after N,N · workflow NAME · decision TEXT.")
                }
            }
            plan = try put(workspace, current: plan, item: item)
            session.milestones = plan
            return "Added milestone \(plan.count): \(item.title)"
        case "remove":
            let index = try numbered(words.count > 1 ? words[1] : nil)
            let title = plan[index].title
            plan = remove(plan[index].id, from: plan)
            session.milestones = plan.isEmpty ? nil : plan
            return "Removed \(title)."
        case "check":
            let parts = (words.count > 1 ? words[1] : "").split(separator: " ", maxSplits: 1).map(String.init)
            let index = try numbered(parts.first)
            let item = plan[index]
            let allowed = session.permission == .auto || ApprovalRules(home: home, workspace: session.workspace).allows(tool: "run_workflow", subject: "workflow " + item.workflow)
            plan[index].receipt = try await check(workspace, item: item, plan: plan, runWorkflow: (allowed, session.permission == .auto), observation: parts.count > 1 ? parts[1] : "")
            session.milestones = plan
            let state = inspect(workspace, plan: plan)[index].state.rawValue
            return "\(item.title): \(state)."
        default: throw MasonError("Use /milestone, /milestone add, /milestone check or /milestone remove. /milestone help shows how.")
        }
    }
}

extension EmbeddingSettings {
    public static let help = """
    /embeddings                       Show the embeddings server used to search code by meaning
    /embeddings URL MODEL [KEY]       Choose one (any server with /v1/embeddings, such as llama-server --embedding)
    /embeddings rerank URL MODEL      Also reorder the best matches with a /v1/rerank server   ·   rerank off
    /embeddings test                  Embed a line of code to check the server answers
    /embeddings off                   Stop offering search by meaning
    """

    public static func command(_ rest: String, home: URL = DataHome.url) async throws -> String {
        let words = rest.split(separator: " ").map(String.init)
        let saved = load(home: home)
        switch words.first?.lowercased() {
        case nil:
            guard let saved else { return "Code search by meaning is off: no embeddings server is chosen.\n" + help }
            return "Embeddings: \(saved.model) at \(saved.url)" + (saved.rerankUrl.isEmpty ? "" : "\nReranker: \(saved.rerankModel) at \(saved.rerankUrl)") + "\nMason offers the model semantic_search in every session."
        case "help": return help
        case "off":
            try save(nil, home: home)
            return "Code search by meaning is off."
        case "test":
            guard let saved else { throw MasonError("Choose a server first: /embeddings URL MODEL") }
            return try await EmbeddingServers(saved).probe()
        case "rerank":
            guard var changed = saved else { throw MasonError("Choose an embeddings server first: /embeddings URL MODEL") }
            if words.count == 2, words[1].lowercased() == "off" { changed.rerankUrl = ""; changed.rerankModel = "" }
            else if words.count == 3 { changed.rerankUrl = words[1]; changed.rerankModel = words[2] }
            else { throw MasonError("Write it as /embeddings rerank URL MODEL, or /embeddings rerank off.") }
            try save(changed, home: home)
            return changed.rerankUrl.isEmpty ? "Matches are no longer reranked." : "The best matches are reranked by \(changed.rerankModel)."
        default:
            guard words.count == 2 || words.count == 3 else { throw MasonError("Write it as /embeddings URL MODEL, for example /embeddings http://127.0.0.1:8081/ nomic-embed-text") }
            try save(EmbeddingSettings(url: words[0], model: words[1], key: words.count == 3 ? words[2] : "", rerankUrl: saved?.rerankUrl ?? "", rerankModel: saved?.rerankModel ?? ""), home: home)
            return "Mason will search code by meaning with \(words[1]) at \(words[0]). /embeddings test checks it answers."
        }
    }
}
