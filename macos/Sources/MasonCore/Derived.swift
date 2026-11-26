import Foundation

// Checks Mason works out from a request before the work starts, and runs itself when the model says it is done.
// A requirement with an exact, observable outcome ("show a heading "Orders"") becomes a check directly. Otherwise a
// separate model call proposes black-box checks, and only those Mason can actually run are kept. In Auto, a failed
// check is handed back to the model to fix.

/// A small wrapper over NSRegularExpression: each match as its groups, by number.
enum Rx {
    static func matches(_ pattern: String, in text: String, options: NSRegularExpression.Options = []) -> [[String]] {
        guard let expression = try? NSRegularExpression(pattern: pattern, options: options) else { return [] }
        return expression.matches(in: text, range: NSRange(text.startIndex..., in: text)).map { match in
            (0..<match.numberOfRanges).map { Range(match.range(at: $0), in: text).map { String(text[$0]) } ?? "" }
        }
    }
    static func first(_ pattern: String, in text: String, options: NSRegularExpression.Options = []) -> [String]? { matches(pattern, in: text, options: options).first }
    static func found(_ pattern: String, in text: String, options: NSRegularExpression.Options = [.caseInsensitive]) -> Bool { first(pattern, in: text, options: options) != nil }
    static func split(_ pattern: String, _ text: String) -> [String] {
        guard let expression = try? NSRegularExpression(pattern: pattern) else { return [text] }
        var parts: [String] = [], start = text.startIndex
        for match in expression.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
            guard let range = Range(match.range, in: text) else { continue }
            parts.append(String(text[start..<range.lowerBound])); start = range.upperBound
        }
        parts.append(String(text[start...]))
        return parts
    }
}

public struct DerivedCheck: Codable, Equatable {
    /// The requirement this checks, numbered from 1 in the order the request states them.
    public var unit: Int
    public var feature: String
    /// document_text, browser_heading, browser_text, browser_click or browser_flow.
    public var kind: String
    public var path: String
    public var target: String
    public var expected: String
    /// For browser_flow: each step as [op, target, value].
    public var steps: [[String]]?
    public var application: String?
    public init(unit: Int, feature: String, kind: String, path: String, target: String = "", expected: String, steps: [[String]]? = nil, application: String? = nil) {
        self.unit = unit; self.feature = feature; self.kind = kind; self.path = path; self.target = target; self.expected = expected; self.steps = steps; self.application = application
    }

    /// The check in a sentence, for the model and for the receipt.
    public var description: String {
        switch kind {
        case "document_text": return "\(path) contains “\(expected)”"
        case "browser_heading": return "\(path) shows a heading that reads “\(expected)”"
        case "browser_text": return "\(path) shows “\(expected)”"
        case "browser_click": return "in \(path), clicking “\(target)” makes “\(expected)” appear"
        case "project_test": return "the project's test “\(target)” passes when the workflow \(path) runs (its test files are pinned: they must stay as they are)"
        default: return "\(path): \(feature)"
        }
    }
}

public struct DerivedResult: Codable, Equatable {
    public var check: DerivedCheck
    /// passed, failed, or notRun (no browser on this computer).
    public var state: String
    public var detail: String
}

/// What Mason found when it checked a request's requirements itself. The model does not write this.
public struct DerivedReceipt: Codable, Equatable {
    public var results: [DerivedResult]
    /// Requirements Mason had no way to check.
    public var unverified: [String]
    /// How many times the model was handed a failed check to fix.
    public var repairs: Int
    public var passed: Bool { results.allSatisfy { $0.state != "failed" } }

    public var summary: String {
        let failed = results.filter { $0.state == "failed" }.count, ran = results.filter { $0.state != "notRun" }.count
        var text = ran == 0 ? "Mason's own checks could not run here" : failed == 0 ? "Mason checked \(ran) requirement\(ran == 1 ? "" : "s") itself: all hold" : "Mason checked \(ran) requirement\(ran == 1 ? "" : "s") itself: \(failed) failed"
        if !unverified.isEmpty { text += " · \(unverified.count) had no check it could run" }
        if repairs > 0 { text += " · handed back \(repairs) time\(repairs == 1 ? "" : "s")" }
        return text
    }

    public var report: String {
        ([summary + "."] + results.map { "\($0.state == "passed" ? "PASSED" : $0.state == "failed" ? "FAILED" : "NOT RUN") \($0.check.description)" + ($0.detail.isEmpty ? "" : "\n  " + $0.detail) }
            + unverified.map { "NO CHECK \($0)" }).joined(separator: "\n")
    }
}

public struct DerivedPlan: Equatable {
    public var units: [String] = []
    public var checks: [DerivedCheck] = []
    public var unverified: [String] { units.enumerated().filter { index, _ in !checks.contains { $0.unit == index + 1 } }.map(\.element) }
    /// What the model is told before it starts, so it builds towards what will be checked.
    public var brief: String {
        checks.isEmpty ? "" : "Mason will check these itself when you say you are done:\n" + checks.map { "- " + $0.description }.joined(separator: "\n")
    }
}

public enum Derived {
    static let pagePattern = #"(?<![\w/\\:.-])(?:[\w-]+/)*[\w-]+\.html?\b"#

    /// The request without a contract block at its end.
    static func prose(_ request: String) -> String {
        guard let fence = request.range(of: "```mason.acceptance.json") else { return request }
        return String(request[..<fence.lowerBound])
    }

    /// The separate requirements of a request: its clauses, with quoted text kept whole.
    public static func units(_ request: String) -> [String] {
        let input = prose(request)
        guard let delimiter = try? NSRegularExpression(pattern: #"("(?:\\.|[^"\\])*"|‘[^’]*’|“[^”]*”|'[^'\r\n]+')|(?:[;\r\n]+|[.!?](?=\s|$)|,\s+|\s+(?:and(?: also| then)?|as well as|plus)\s+)"#, options: [.caseInsensitive]) else { return [input] }
        var units: [String] = [], start = input.startIndex
        for match in delimiter.matches(in: input, range: NSRange(input.startIndex..., in: input)) {
            if match.range(at: 1).location != NSNotFound { continue }
            guard let range = Range(match.range, in: input) else { continue }
            let unit = input[start..<range.lowerBound].trimmingCharacters(in: .whitespaces)
            if !unit.isEmpty { units.append(unit) }
            start = range.upperBound
        }
        let tail = input[start...].trimmingCharacters(in: .whitespacesAndNewlines)
        if !tail.isEmpty { units.append(tail) }
        return units
    }

    /// A requirement that names its own observable outcome exactly becomes a check with no model involved. The whole
    /// clause must match, so an extra condition cannot hide behind a loose prefix.
    static func explicit(_ clause: String, unit: Int, pages: [String]) -> DerivedCheck? {
        if let document = Rx.first(#"^(?:create|update|write)\s+([\w./-]+\.(?:txt|md|csv))\s+(?:containing|to contain|with the text)\s+"([^"]+)"$"#, in: clause, options: [.caseInsensitive]) {
            return DerivedCheck(unit: unit, feature: clause, kind: "document_text", path: document[1], expected: document[2])
        }
        guard pages.count <= 1 else { return nil }
        let page = pages.first ?? "index.html"
        if let heading = Rx.first(#"^(?:in\s+[\w./-]+\.html?\s+)?(?:show|display|add|create)\s+(?:a|the|an)\s+(?:page\s+)?heading\s+(?:(?:named|reading|labeled|saying|with (?:the )?text)\s+)?"([^"]+)"$"#, in: clause, options: [.caseInsensitive]) {
            return DerivedCheck(unit: unit, feature: clause, kind: "browser_heading", path: page, expected: heading[1])
        }
        if let click = Rx.first(#"^(?:clicking|pressing|tapping)\s+(?:(?:the|a)\s+)?(?:(?:button|link)\s+)?"([^"]+)"\s+(?:must\s+|should\s+)?(?:show|display|reveal)s?\s+"([^"]+)"(?:\s+on (?:the )?page)?$"#, in: clause, options: [.caseInsensitive]) {
            return DerivedCheck(unit: unit, feature: clause, kind: "browser_click", path: page, target: click[1], expected: click[2])
        }
        return nil
    }

    static let plannerPrompt = """
    Design black-box acceptance checks BEFORE the work is done. Reply with only JSON: {"checks": [{"Unit": 1, "Feature": "...", "Kind": "...", "Path": "...", "Target": "", "Expected": "..."}]}.
    Unit is the number of the requirement the check covers. Kind is one of:
    browser_heading: the page at Path shows a heading whose text is exactly Expected.
    browser_text: the page at Path shows the exact text Expected.
    browser_click: clicking the button or link labelled exactly Target on the page at Path makes the text Expected appear. Use this for any requirement about clicking, pressing or toggling.
    document_text: the text file at Path (.txt, .md or .csv) contains Expected.
    unverified: none of these can check the requirement; say why in Expected.
    Use only text the request itself gives in quotes or states exactly. Do not invent labels. Path is relative to the project; use index.html when the request names no page. At most four checks.
    """

    /// True when a proposed check is one Mason can run and that really covers its requirement.
    static func valid(_ check: DerivedCheck, unit: String, request: String, workspace: Workspace) -> Bool {
        guard !check.feature.trimmingCharacters(in: .whitespaces).isEmpty, check.feature.count <= 400, check.target.count <= 200, !check.expected.isEmpty, check.expected.count <= 500, !check.path.isEmpty, check.path.count <= 260,
              (try? workspace.resolve(check.path)) != nil else { return false }
        let kind = (check.path as NSString).pathExtension.lowercased()
        let browser = ["browser_heading", "browser_text", "browser_click"].contains(check.kind)
        if browser, !["html", "htm"].contains(kind) { return false }
        if check.kind == "document_text" {
            guard ["txt", "md", "csv"].contains(kind), Rx.found(#"\.(?:txt|md|csv)\b|\b(?:documentation|document|readme|notes|file|text)\b"#, in: request) else { return false }
        } else if !browser { return false }
        // "visible" or "true" is not something a page shows unless the request quotes it.
        if browser, Rx.found(#"^(?:visible|true|passed|successfully displayed)$"#, in: check.expected), !unit.lowercased().contains("\"" + check.expected.lowercased() + "\"") { return false }
        let interaction = Rx.found(#"\b(click(?:ing|ed)?|press(?:ing|ed)?|tapp?(?:ing|ed)?|toggl(?:e|ing))\b"#, in: unit)
        if interaction, check.kind != "browser_click" { return false }
        if check.kind == "browser_click", check.target.trimmingCharacters(in: .whitespaces).isEmpty { return false }
        // The expected text must come from the request, not from the model's imagination.
        return request.contains(check.expected) && (check.target.isEmpty || request.contains(check.target))
    }

    /// Works out what can be checked. `model` is asked only when some requirement has no exact check and the request
    /// is about pages or text documents.
    public static func prepare(_ request: String, workspace: Workspace, model: ChatModel?, requireSandbox: Bool = false) async -> DerivedPlan {
        var plan = DerivedPlan()
        // A contract given with the request belongs to this request: its scenarios are the checks.
        if let fence = request.range(of: "```mason.acceptance.json") {
            let block = request[fence.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
            guard block.hasSuffix("```"), let contract = try? Acceptance.parse(workspace, String(block.dropLast(3))) else { return plan }
            plan.units = contract.scenarios.map(\.requirement)
            plan.checks = contract.scenarios.enumerated().map { index, scenario in
                DerivedCheck(unit: index + 1, feature: scenario.requirement, kind: "browser_flow", path: scenario.page, target: scenario.id, expected: "All scenario assertions pass",
                             steps: scenario.steps.map { [$0.op, $0.target, $0.value] }, application: contract.application)
            }
            return plan
        }
        plan.units = Array(units(request).prefix(64))
        // A requirement the project names a test for is checked by that test. A request that is one such requirement
        // as a whole stays whole.
        let named = (try? ProjectTests.load(workspace)) ?? []
        if ProjectTests.bound(request, in: named) != nil, !plan.units.contains(where: { ProjectTests.bound($0, in: named) != nil }) { plan.units = [request.trimmingCharacters(in: .whitespacesAndNewlines)] }
        guard !plan.units.isEmpty, plan.units.count <= 32 else { return plan }
        for (index, unit) in plan.units.enumerated() {
            if let test = ProjectTests.bound(unit, in: named) { plan.checks.append(await ProjectTests.check(test, unit: index + 1, workspace: workspace, requireSandbox: requireSandbox)) }
        }
        let tested = Set(plan.checks.map(\.unit))
        var seen = Set<String>()
        let pages = Rx.matches(pagePattern, in: request, options: [.caseInsensitive]).map { $0[0] }.filter { seen.insert($0.lowercased()).inserted }
        plan.checks += plan.units.enumerated().compactMap { tested.contains($0 + 1) ? nil : explicit($1, unit: $0 + 1, pages: pages) }
        let open = plan.units.indices.filter { index in !plan.checks.contains { $0.unit == index + 1 } }
        let aboutPages = !pages.isEmpty || workspace.exists("index.html") || Rx.found(#"\.(?:txt|md|csv)\b"#, in: request)
        guard !open.isEmpty, aboutPages, let model else { return plan }
        let listed = plan.units.enumerated().map { "\($0 + 1). \($1)" }.joined(separator: "\n")
        guard let reply = try? await model.complete(messages: [ChatMessage(role: "system", content: plannerPrompt), ChatMessage(role: "user", content: "Request:\n\(prose(request))\n\nRequirements:\n\(listed)")], tools: [], onText: { _ in }),
              let start = reply.text.firstIndex(of: "{"), let end = reply.text.lastIndex(of: "}"), start < end,
              let object = try? JSONSerialization.jsonObject(with: Data(reply.text[start...end].utf8)) as? [String: Any], let proposed = object["checks"] as? [[String: Any]], proposed.count <= 64 else { return plan }
        for item in proposed.prefix(8) {
            func text(_ key: String) -> String { item.first { $0.key.lowercased() == key }?.value as? String ?? "" }
            guard let unit = item.first(where: { $0.key.lowercased() == "unit" })?.value as? Int, open.contains(unit - 1) else { continue }
            let check = DerivedCheck(unit: unit, feature: text("feature").isEmpty ? plan.units[unit - 1] : text("feature"), kind: text("kind"), path: text("path"), target: text("target"), expected: text("expected"))
            if valid(check, unit: plan.units[unit - 1], request: request, workspace: workspace), !plan.checks.contains(check) { plan.checks.append(check) }
        }
        plan.checks.sort { $0.unit < $1.unit }
        return plan
    }

    /// Runs every check. Browser checks are marked not run where there is no browser.
    public static func verify(_ plan: DerivedPlan, workspace: Workspace, requireSandbox: Bool = false) async throws -> [DerivedResult] {
        var results: [DerivedResult] = []
        for check in plan.checks {
            try Task.checkCancellation()
            if check.kind == "project_test" {
                results.append(await ProjectTests.verify(check, workspace: workspace, requireSandbox: requireSandbox))
                continue
            }
            if check.kind == "document_text" {
                let content = (try? workspace.read(check.path)) ?? nil
                results.append(DerivedResult(check: check, state: content?.contains(check.expected) == true ? "passed" : "failed", detail: content == nil ? "\(check.path) does not exist." : content!.contains(check.expected) ? "" : "The file does not contain it."))
                continue
            }
            guard BrowserCheck.findBrowser() != nil else { results.append(DerivedResult(check: check, state: "notRun", detail: "No Chrome, Chromium, Edge or Brave on this computer.")); continue }
            if check.kind == "browser_flow" {
                let scenario = AcceptanceScenario(id: check.target, requirement: check.feature, page: check.path, steps: (check.steps ?? []).map { AcceptanceStep(op: $0[0], target: $0.count > 1 ? $0[1] : "", value: $0.count > 2 ? $0[2] : "") })
                do {
                    let outcome = try await Acceptance.run(workspace, AcceptanceContract(application: check.application, scenarios: [scenario]), requireSandbox: requireSandbox)[0]
                    results.append(DerivedResult(check: check, state: outcome.passed ? "passed" : "failed", detail: outcome.detail.joined(separator: "; ")))
                } catch let error as MasonError { results.append(DerivedResult(check: check, state: "failed", detail: error.description)) }
                continue
            }
            guard workspace.exists(check.path) else { results.append(DerivedResult(check: check, state: "failed", detail: "\(check.path) does not exist.")); continue }
            let steps: [[String: Any]]
            switch check.kind {
            case "browser_heading": steps = [["action": "expect_heading", "text": check.expected]]
            case "browser_text": steps = [["action": "expect_text", "text": check.expected]]
            // The text has to appear because of the click, so it must not be there before it.
            default: steps = [["action": "expect_no_text", "text": check.expected], ["action": "click", "text": check.target], ["action": "expect_text", "text": check.expected]]
            }
            do {
                let result = try await BrowserCheck.run(workspace, page: check.path, steps: steps)
                var detail = ""
                if let failed = result.steps.firstIndex(where: { !$0.ok }) {
                    let step = result.steps[failed]
                    detail = check.kind == "browser_click" && failed == 0 ? "“\(check.expected)” is already shown before the click." : "\(step.action): \(step.detail)"
                } else if !result.errors.isEmpty { detail = "The page raised: " + result.errors.prefix(3).joined(separator: "; ") }
                results.append(DerivedResult(check: check, state: detail.isEmpty ? "passed" : "failed", detail: detail))
            } catch let error as MasonError { results.append(DerivedResult(check: check, state: "failed", detail: error.description)) }
        }
        return results
    }
}
