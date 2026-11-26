import Foundation

/// A project's own test as the evidence for a requirement. `.mason/tests.json` names the test, the workflow that runs
/// it, the format of its results and the files that define it. In Auto, a request that states the requirement gets a
/// check: Mason runs the workflow before the work (the baseline) and again when the model says it is done, and the
/// requirement holds only if the named test passes and the test's own files are as they were. The model cannot make a
/// pinned test pass by editing it.
public struct ProjectTest: Equatable {
    public var requirement: String
    public var workflow: String
    /// tap, junit, trx, go-json or flutter-json.
    public var format: String
    /// For junit and trx: the report file the workflow writes, relative to the project.
    public var report: String
    public var test: String
    public var testFiles: [String]
    public var inputs: [String]
    /// An extra check after the test passes: strings over `alphabet` must survive encode then decode. [module, encode, decode, alphabet].
    public var roundTrip: [String] = []
    /// "interchangeable" when the function under test must give the same result with its two arguments exchanged.
    public var argumentOrder = ""
}

public enum ProjectTests {
    public static let manifest = ".mason/tests.json"
    static let formats: Set<String> = ["tap", "junit", "trx", "go-json", "flutter-json"]

    /// The project's named tests. Nothing when there is no manifest; an error when it is not valid.
    public static func load(_ workspace: Workspace) throws -> [ProjectTest] {
        guard let data = try workspace.data(manifest) else { return [] }
        let invalid = MasonError("\(manifest) must list up to 64 tests, each with requirement, workflow, format (tap, junit, trx, go-json or flutter-json), test, 1 to 16 testFiles and 1 to 32 inputs.")
        guard let listed = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]], listed.count <= 64 else { throw invalid }
        let workflows = try ProjectWorkflows.load(workspace).workflows
        return try listed.map { item in
            func text(_ key: String) -> String { (item.first { $0.key.lowercased() == key.lowercased() }?.value as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }
            func list(_ key: String) -> [String] { item.first { $0.key.lowercased() == key.lowercased() }?.value as? [String] ?? [] }
            var test = ProjectTest(requirement: text("requirement"), workflow: text("workflow"), format: text("format").lowercased(), report: text("report"), test: text("test"), testFiles: list("testFiles"), inputs: list("inputs"))
            if let trip = item.first(where: { $0.key.lowercased() == "roundtrip" })?.value as? [String: Any] {
                func part(_ key: String) -> String { trip.first { $0.key.lowercased() == key }?.value as? String ?? "" }
                test.roundTrip = [part("module"), part("encode"), part("decode"), part("alphabet")]
                guard !test.roundTrip.contains(""), !test.roundTrip[0].hasPrefix("/"), !test.roundTrip[0].split(separator: "/").contains("..") else { throw MasonError("\(manifest): roundTrip needs module, encode, decode and alphabet.") }
            }
            test.argumentOrder = text("argumentOrder").lowercased()
            guard ["", "ordered", "interchangeable"].contains(test.argumentOrder) else { throw MasonError("\(manifest): argumentOrder is ordered or interchangeable.") }
            guard !test.requirement.isEmpty, !test.test.isEmpty, test.test.count <= 400, formats.contains(test.format), (1...16).contains(test.testFiles.count), (1...32).contains(test.inputs.count),
                  test.format == "junit" || test.format == "trx" ? !test.report.isEmpty : true else { throw invalid }
            guard workflows[test.workflow] != nil else { throw MasonError("\(manifest) names the workflow \(test.workflow), which is not in mason.workflows.json.") }
            for path in test.testFiles + test.inputs + (test.report.isEmpty ? [] : [test.report]) {
                guard !path.hasPrefix("/"), !path.split(separator: "/").contains("..") else { throw MasonError("\(manifest): \(path) is not a path inside the project.") }
            }
            return test
        }
    }

    /// Two statements of a requirement are the same when they differ only in case, outer space and a final full stop.
    static func same(_ a: String, _ b: String) -> Bool {
        func plain(_ text: String) -> String {
            var text = text.trimmingCharacters(in: .whitespacesAndNewlines)
            while text.hasSuffix(".") { text.removeLast() }
            return text.lowercased()
        }
        return plain(a) == plain(b)
    }

    /// The test that stands for a requirement, if the project names one.
    static func bound(_ unit: String, in tests: [ProjectTest]) -> ProjectTest? {
        tests.first { same($0.requirement, unit) || same("verify test " + $0.test, unit) }
    }

    /// A name for the exact content of the test's files and the command that runs them.
    static func pin(_ files: [String], workflow: String, workspace: Workspace) -> String {
        var text = (try? ProjectWorkflows.load(workspace).workflows[workflow]).flatMap { $0 }.map(ProjectWorkflows.commandLine) ?? "no workflow"
        for path in files { text += "\n" + path + "\n" + (((try? workspace.data(path)) ?? nil).map { String(decoding: $0, as: UTF8.self) } ?? "missing") }
        return Experiments.fingerprint(text)
    }

    // MARK: reading results

    static func unescaped(_ text: String) -> String {
        text.replacingOccurrences(of: "&quot;", with: "\"").replacingOccurrences(of: "&apos;", with: "'").replacingOccurrences(of: "&lt;", with: "<").replacingOccurrences(of: "&gt;", with: ">").replacingOccurrences(of: "&amp;", with: "&")
    }

    /// Each test's outcome (passed, failed or skipped) by name. A name reported more than once has failed if any of them did.
    public static func outcomes(format: String, text: String) -> [String: String] {
        var found: [String: String] = [:]
        func record(_ name: String, _ outcome: String) {
            let name = name.trimmingCharacters(in: .whitespaces)
            guard !name.isEmpty else { return }
            if found[name] == "failed" { return }
            if outcome == "failed" || found[name] == nil || found[name] == "skipped" { found[name] = outcome }
        }
        switch format {
        case "tap":
            for line in text.split(separator: "\n") {
                guard let match = Rx.first(#"^\s*(not ok|ok)\b\s*\d*\s*(?:-\s*)?(.*?)\s*(?:#\s*(SKIP|TODO)\b.*)?$"#, in: String(line), options: [.caseInsensitive]) else { continue }
                let directive = match.count > 3 ? match[3].uppercased() : ""
                record(match[2], directive == "SKIP" ? "skipped" : match[1].lowercased() == "ok" ? "passed" : "failed")
            }
        case "junit":
            for match in Rx.matches(#"<testcase\b([^>]*?)(?:/>|>([\s\S]*?)</testcase>)"#, in: text) {
                let attributes = match[1], body = match.count > 2 ? match[2] : ""
                guard let name = Rx.first(#"\bname="([^"]*)""#, in: attributes)?[1] else { continue }
                let outcome = body.contains("<failure") || body.contains("<error") ? "failed" : body.contains("<skipped") ? "skipped" : "passed"
                record(unescaped(name), outcome)
                if let suite = Rx.first(#"\bclassname="([^"]*)""#, in: attributes)?[1], !suite.isEmpty { record(unescaped(suite) + "." + unescaped(name), outcome) }
            }
        case "trx":
            for match in Rx.matches(#"<UnitTestResult\b([^>]*)>"#, in: text) {
                guard let name = Rx.first(#"\btestName="([^"]*)""#, in: match[1])?[1], let outcome = Rx.first(#"\boutcome="([^"]*)""#, in: match[1])?[1] else { continue }
                record(unescaped(name), outcome == "Passed" ? "passed" : outcome == "Failed" || outcome == "Error" || outcome == "Timeout" || outcome == "Aborted" ? "failed" : "skipped")
            }
        case "go-json":
            for line in text.split(separator: "\n") {
                guard let event = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any], let name = event["Test"] as? String, let action = event["Action"] as? String else { continue }
                if let outcome = ["pass": "passed", "fail": "failed", "skip": "skipped"][action] { record(name, outcome) }
            }
        case "flutter-json":
            var names: [Int: String] = [:]
            for line in text.split(separator: "\n") {
                guard let event = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any], let type = event["type"] as? String else { continue }
                if type == "testStart", let test = event["test"] as? [String: Any], let id = test["id"] as? Int, let name = test["name"] as? String { names[id] = name }
                if type == "testDone", let id = event["testID"] as? Int, let name = names[id], event["hidden"] as? Bool != true {
                    record(name, event["skipped"] as? Bool == true ? "skipped" : event["result"] as? String == "success" ? "passed" : "failed")
                }
            }
        default: break
        }
        return found
    }

    /// The named test's outcome: by its exact name, or by the one reported name that ends with it after a separator.
    public static func outcome(of name: String, in outcomes: [String: String]) -> String? {
        if let exact = outcomes[name] { return exact }
        let ending = outcomes.filter { key, _ in key.hasSuffix(name) && key.count > name.count && " ./>:".contains(key[key.index(key.endIndex, offsetBy: -name.count - 1)]) }
        return ending.count == 1 ? ending.first?.value : nil
    }

    /// Runs the test's workflow and reads the named test's outcome. Nil when the results did not mention it.
    static func run(test: String, workflow: String, format: String, report: String, workspace: Workspace, sandboxed: Bool) async throws -> (outcome: String?, exit: Int32, diagnostics: String) {
        guard let found = try ProjectWorkflows.load(workspace).workflows[workflow] else { throw MasonError("The workflow \(workflow) is no longer in mason.workflows.json.") }
        let started = Date()
        let result = try await Shell.run(ProjectWorkflows.commandLine(found), in: workspace, sandboxed: sandboxed, timeout: 300)
        var text = result.output
        if format == "junit" || format == "trx" {
            let file = workspace.root.appendingPathComponent(report)
            guard let written = (try? FileManager.default.attributesOfItem(atPath: file.path))?[.modificationDate] as? Date, written >= started.addingTimeInterval(-2), let data = try workspace.data(report) else {
                return (nil, result.exitCode, "The workflow did not write \(report) in this run.")
            }
            text = String(decoding: data, as: UTF8.self)
        }
        let tail = result.output.count > 1200 ? "…" + String(result.output.suffix(1200)) : result.output
        return (outcome(of: test, in: outcomes(format: format, text: text)), result.exitCode, result.timedOut ? "The workflow ran past 300 seconds." : tail)
    }

    // MARK: probes beyond the test itself

    static let roundTripScript = #"""
        var p=require('path'),m=require(p.resolve(process.argv[1])),enc=m[process.argv[2]],dec=m[process.argv[3]],alpha=Array.from(process.argv[4]);
        if(typeof enc!=='function'||typeof dec!=='function'||!alpha.length){console.log('MASON-PROBE fail the module does not export both functions');process.exit(0);}
        var seed=12345;function next(){seed=(seed*1103515245+12345)&0x7fffffff;return seed;}
        var cases=[''];for(var i=0;i<63;i++){var n=next()%24,s='';for(var j=0;j<n;j++)s+=alpha[next()%alpha.length];cases.push(s);}
        for(var k=0;k<cases.length;k++){var c=cases[k],out;try{out=dec(enc(c));}catch(e){console.log('MASON-PROBE fail '+JSON.stringify(c)+' threw '+String(e&&e.message));process.exit(0);}
        if(out!==c){console.log('MASON-PROBE fail '+JSON.stringify(c)+' came back as '+JSON.stringify(out));process.exit(0);}}
        console.log('MASON-PROBE pass '+cases.length+' strings came back unchanged');
        """#
    static let orderScript = #"""
        var p=require('path'),f=require(p.resolve(process.argv[1]))[process.argv[2]],a=JSON.parse(process.argv[3]),b=JSON.parse(process.argv[4]);
        if(typeof f!=='function'){console.log('MASON-PROBE fail the module does not export '+process.argv[2]);process.exit(0);}
        var x,y;try{x=f(a,b);y=f(b,a);}catch(e){console.log('MASON-PROBE fail it threw '+String(e&&e.message));process.exit(0);}
        try{require('assert').deepStrictEqual(y,x);console.log('MASON-PROBE pass the same result with the arguments exchanged ('+JSON.stringify(x)+')');}
        catch(e){console.log('MASON-PROBE fail '+process.argv[2]+'('+process.argv[3]+', '+process.argv[4]+') gives '+JSON.stringify(x)+' but exchanged it gives '+JSON.stringify(y));}
        """#

    /// A direct call of the one function a CommonJS module exports, with two literal arguments, taken from the
    /// test's own files: [module, function, first argument as JSON, second argument as JSON].
    static func example(_ test: ProjectTest, workspace: Workspace) -> [String]? {
        guard test.inputs.count == 1, ["cjs", "js"].contains((test.inputs[0] as NSString).pathExtension.lowercased()), let module = try? workspace.read(test.inputs[0]) else { return nil }
        let names = Set(Rx.matches(#"\bexports\.([A-Za-z_$][\w$]*)\s*="#, in: module).map { $0[1] })
        guard names.count == 1, let name = names.first else { return nil }
        let literal = #"(-?\d+(?:\.\d+)?|"[^"\\]*"|'[^'\\]*')"#
        for path in test.testFiles {
            guard let source = try? workspace.read(path), let call = Rx.first("\\b" + NSRegularExpression.escapedPattern(for: name) + "\\(\\s*" + literal + "\\s*,\\s*" + literal + "\\s*\\)", in: source) else { continue }
            func json(_ text: String) -> String { text.hasPrefix("'") ? "\"" + text.dropFirst().dropLast().replacingOccurrences(of: "\"", with: "\\\"") + "\"" : text }
            return [test.inputs[0], name, json(call[1]), json(call[2])]
        }
        return nil
    }

    /// Runs one probe with Node.js. Nil when it could not be run at all.
    static func probe(_ script: String, _ arguments: [String], workspace: Workspace, sandboxed: Bool) async -> (passed: Bool, text: String)? {
        let command = "node -e " + Shell.quote(script) + " " + arguments.map(Shell.quote).joined(separator: " ")
        guard let result = try? await Shell.run(command, in: workspace, sandboxed: sandboxed, timeout: 30), let line = result.output.components(separatedBy: "\n").first(where: { $0.hasPrefix("MASON-PROBE ") }) else { return nil }
        let said = String(line.dropFirst("MASON-PROBE ".count))
        return (said.hasPrefix("pass "), String(said.dropFirst(5)))
    }

    // MARK: as one of Mason's own checks

    static func value(_ check: DerivedCheck, _ key: String) -> [String] { Array((check.steps ?? []).first { $0.first == key }?.dropFirst() ?? []) }

    /// The check for a requirement the project names a test for: pinned now, with the baseline run recorded.
    static func check(_ test: ProjectTest, unit: Int, workspace: Workspace, requireSandbox: Bool) async -> DerivedCheck {
        var baseline = "not run"
        if !requireSandbox || Shell.sandboxAvailable {
            if let ran = try? await run(test: test.test, workflow: test.workflow, format: test.format, report: test.report, workspace: workspace, sandboxed: Shell.sandboxAvailable) { baseline = ran.outcome ?? "not reported" }
        }
        var steps = [["format", test.format], ["report", test.report], ["files"] + test.testFiles, ["pin", pin(test.testFiles, workflow: test.workflow, workspace: workspace)], ["baseline", baseline]]
        if !test.roundTrip.isEmpty { steps.append(["roundTrip"] + test.roundTrip) }
        if test.argumentOrder == "interchangeable", let found = example(test, workspace: workspace) { steps.append(["argumentOrder"] + found) }
        return DerivedCheck(unit: unit, feature: test.requirement, kind: "project_test", path: test.workflow, target: test.test, expected: "passed", steps: steps)
    }

    static func verify(_ check: DerivedCheck, workspace: Workspace, requireSandbox: Bool) async -> DerivedResult {
        let files = value(check, "files"), baseline = value(check, "baseline").first ?? "not run"
        guard pin(files, workflow: check.path, workspace: workspace) == value(check, "pin").first else {
            return DerivedResult(check: check, state: "failed", detail: "The test's own files (\(files.joined(separator: ", "))) or the workflow that runs them changed during this request. A pinned test has to pass as it was written; put it back.")
        }
        if requireSandbox, !Shell.sandboxAvailable { return DerivedResult(check: check, state: "notRun", detail: "The sandbox is unavailable here, so the project's tests were not run.") }
        do {
            let ran = try await run(test: check.target, workflow: check.path, format: value(check, "format").first ?? "", report: value(check, "report").first ?? "", workspace: workspace, sandboxed: Shell.sandboxAvailable)
            let now = ran.outcome ?? "not reported"
            let story = "baseline: \(baseline); now: \(now) (the workflow \(check.path) exited \(ran.exit))"
            guard ran.outcome == "passed" else { return DerivedResult(check: check, state: "failed", detail: story + "\n" + ran.diagnostics) }
            // The test passes. The probes the project asked for go further than its examples.
            var notes: [String] = [], broken = false
            for (key, script, label) in [("roundTrip", roundTripScript, "round trip"), ("argumentOrder", orderScript, "argument order")] {
                let arguments = value(check, key)
                guard !arguments.isEmpty else { continue }
                if let outcome = await probe(script, arguments, workspace: workspace, sandboxed: Shell.sandboxAvailable) {
                    notes.append("\(label): \(outcome.text)")
                    broken = broken || !outcome.passed
                } else {
                    notes.append("\(label): not run (Node.js was not found, or the module could not be loaded)")
                }
            }
            return DerivedResult(check: check, state: broken ? "failed" : "passed", detail: ([story] + notes).joined(separator: "\n"))
        } catch { return DerivedResult(check: check, state: "failed", detail: (error as? MasonError)?.description ?? error.localizedDescription) }
    }
}
