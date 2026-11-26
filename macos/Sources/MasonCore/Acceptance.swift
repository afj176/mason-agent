import Foundation

// Acceptance scenarios a project keeps in mason.acceptance.json: for each requirement, a page and the steps that show
// it holds. Mason runs them in a real browser at a phone width and a desktop width and reports what it observed.
//
//   {"Version": 1, "Scenarios": [{"Id": "discount", "Requirement": "Ten percent off at three units", "Page": "index.html",
//     "Steps": [{"Op": "fill", "Target": "#qty", "Value": "3"}, {"Op": "money", "Target": "#total", "Value": "27.00"}]}]}

public struct AcceptanceStep: Equatable {
    public let op: String, target: String, value: String
}

public struct AcceptanceScenario: Equatable {
    public let id: String, requirement: String, page: String
    public let steps: [AcceptanceStep]
}

public struct AcceptanceContract: Equatable {
    public let application: String?
    public let scenarios: [AcceptanceScenario]
}

public struct AcceptanceResult: Equatable {
    public let scenario: AcceptanceScenario
    public let passed: Bool
    /// What each step observed, or where it stopped and why.
    public let detail: [String]
}

public enum Acceptance {
    public static let filename = "mason.acceptance.json"
    public static let widths = [390, 1280]
    static let actions: Set<String> = ["fill", "click", "double_click", "click_if_enabled", "reload"]
    static let assertions: Set<String> = ["count_distinct", "money", "shelf_price", "text", "empty", "capture", "same", "new_id"]
    /// Steps that look inside a running application or its database. They need the isolated application run Windows has.
    static let applicationSteps: Set<String> = ["sqlite_count", "response_count", "response_same"]

    public static func exists(_ workspace: Workspace) -> Bool { workspace.exists(filename) }

    public static func read(_ workspace: Workspace) throws -> AcceptanceContract { try parse(workspace, try workspace.read(filename)) }

    /// Field names are matched without regard to case, as the Windows app does.
    static func field(_ object: [String: Any], _ name: String) -> Any? { object.first { $0.key.lowercased() == name }?.value }

    public static func parse(_ workspace: Workspace, _ source: String) throws -> AcceptanceContract {
        guard let object = try? JSONSerialization.jsonObject(with: Data(source.utf8)) as? [String: Any], (field(object, "version") as? Int) == 1,
              let listed = field(object, "scenarios") as? [[String: Any]], (1...32).contains(listed.count) else {
            throw MasonError("Use version 1 with 1–32 uniquely named acceptance scenarios.")
        }
        var scenarios: [AcceptanceScenario] = []
        for item in listed {
            guard let id = field(item, "id") as? String, !id.trimmingCharacters(in: .whitespaces).isEmpty, id.count <= 100,
                  let requirement = field(item, "requirement") as? String, !requirement.trimmingCharacters(in: .whitespaces).isEmpty, requirement.count <= 500,
                  let page = field(item, "page") as? String else { throw MasonError("Each scenario requires a bounded ID, a requirement and a page.") }
            _ = try workspace.resolve(page)
            let steps = try (field(item, "steps") as? [[String: Any]] ?? []).map { step -> AcceptanceStep in
                guard let op = field(step, "op") as? String else { throw MasonError("Each step needs an Op.") }
                return AcceptanceStep(op: op, target: field(step, "target") as? String ?? "", value: field(step, "value") as? String ?? "")
            }
            try validate(steps)
            scenarios.append(AcceptanceScenario(id: id, requirement: requirement, page: page, steps: steps))
        }
        guard Set(scenarios.map(\.id)).count == scenarios.count else { throw MasonError("Use version 1 with 1–32 uniquely named acceptance scenarios.") }
        return AcceptanceContract(application: field(object, "application") as? String, scenarios: scenarios)
    }

    static func validate(_ steps: [AcceptanceStep]) throws {
        guard (1...24).contains(steps.count) else { throw MasonError("Provide 1–24 observable browser steps.") }
        var captured = false
        for step in steps {
            guard step.target.count <= 180, step.value.count <= 300, actions.contains(step.op) || assertions.contains(step.op) || applicationSteps.contains(step.op) else { throw MasonError("Unknown or oversized browser step: \(step.op).") }
            if step.op != "reload", step.target.trimmingCharacters(in: .whitespaces).isEmpty { throw MasonError("A browser step needs a target.") }
            if step.op == "count_distinct", !(Int(step.value).map { (1...1000).contains($0) } ?? false) { throw MasonError("Specify a positive distinct item count.") }
            if step.op == "money" || step.op == "shelf_price", Double(step.value) == nil { throw MasonError("Specify the expected decimal amount.") }
            if step.op == "text", step.value.trimmingCharacters(in: .whitespaces).isEmpty { throw MasonError("Specify exact visible text.") }
            if step.op == "same" || step.op == "new_id", !captured { throw MasonError("Capture a value before comparing it.") }
            if step.op == "capture" { captured = true }
        }
        guard let last = steps.last, assertions.contains(last.op) && last.op != "capture" || applicationSteps.contains(last.op) else { throw MasonError("End each scenario with an assertion.") }
    }

    /// Runs every scenario at each width. A scenario passes when every step does at both widths and the page raises no script error.
    public static func run(_ workspace: Workspace, _ contract: AcceptanceContract, progress: (String) -> Void = { _ in }) async throws -> [AcceptanceResult] {
        if let application = contract.application {
            throw MasonError("This contract runs a Python application (\(application)) in an isolated copy, which Mason does on Windows only for now. Contracts for static pages run here.")
        }
        if contract.scenarios.contains(where: { $0.steps.contains { applicationSteps.contains($0.op) } }) { throw MasonError("API and database assertions require an isolated verification application.") }
        var results: [AcceptanceResult] = []
        for scenario in contract.scenarios {
            try Task.checkCancellation()
            progress("Checking \(scenario.id)")
            var detail: [String] = [], passed = true
            for width in widths where passed {
                let steps: [[String: Any]] = [["action": "viewport", "width": width]] + scenario.steps.map { ["action": "accept", "op": $0.op, "target": $0.target, "value": $0.value] }
                let result = try await BrowserCheck.run(workspace, page: scenario.page, steps: steps)
                if let failed = result.steps.firstIndex(where: { !$0.ok }) {
                    passed = false
                    detail = ["at \(width)px, step \(failed + 1), \(result.steps[failed].action): \(result.steps[failed].detail)"]
                } else if !result.errors.isEmpty {
                    passed = false
                    detail = ["at \(width)px the page raised: " + result.errors.prefix(3).joined(separator: "; ")]
                } else if width == widths.last {
                    detail = result.steps.filter { !$0.detail.isEmpty }.map { "\($0.action) = \($0.detail)" }
                }
            }
            results.append(AcceptanceResult(scenario: scenario, passed: passed, detail: detail))
        }
        return results
    }

    /// The results as the user and the model read them.
    public static func receipt(_ results: [AcceptanceResult]) -> String {
        let passed = results.filter(\.passed).count
        var lines = ["\(passed) of \(results.count) acceptance scenarios passed, each at \(widths.map { "\($0)px" }.joined(separator: " and "))."]
        for result in results {
            lines.append("\(result.passed ? "PASSED" : "FAILED") \(result.scenario.id) — \(result.scenario.requirement) (\(result.scenario.page))")
            lines += result.detail.map { "  " + $0 }
        }
        return lines.joined(separator: "\n")
    }
}
