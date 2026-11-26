#include "project_tests.hpp"

#include "experiments.hpp"
#include "shell.hpp"
#include "tools.hpp"

#include <cctype>
#include <chrono>
#include <set>

namespace mason::project_tests {

namespace {

const char* const kManifest = ".mason/tests.json";

/// A field of a manifest entry, whatever the case of its name.
const json* field(const json& item, const std::string& name) {
    for (auto it = item.begin(); it != item.end(); ++it) {
        if (to_lower(it.key()) == to_lower(name)) return &it.value();
    }
    return nullptr;
}

std::string text_of(const json& item, const std::string& name) {
    const json* value = field(item, name);
    return value && value->is_string() ? trim(value->get<std::string>()) : "";
}

std::vector<std::string> list_of(const json& item, const std::string& name) {
    std::vector<std::string> found;
    const json* value = field(item, name);
    if (!value || !value->is_array()) return found;
    for (const json& entry : *value) {
        if (entry.is_string()) found.push_back(entry.get<std::string>());
    }
    return found;
}

bool inside(const std::string& path) {
    if (path.empty() || path[0] == '/') return false;
    for (const std::string& part : split(path, '/')) {
        if (part == "..") return false;
    }
    return true;
}

/// Two statements of a requirement are the same when they differ only in case, outer space and a final full stop.
std::string plain(const std::string& statement) {
    std::string text = trim(statement);
    while (!text.empty() && text.back() == '.') text.pop_back();
    return to_lower(text);
}

std::string unescaped(std::string text) {
    for (const auto& [from, to] : std::vector<std::pair<std::string, std::string>>{{"&quot;", "\""}, {"&apos;", "'"}, {"&lt;", "<"}, {"&gt;", ">"}, {"&amp;", "&"}}) text = replace_all(text, from, to);
    return text;
}

/// The value of name="..." in an XML start tag.
std::optional<std::string> attribute(const std::string& tag, const std::string& name) {
    for (size_t at = tag.find(name + "=\""); at != std::string::npos; at = tag.find(name + "=\"", at + 1)) {
        if (at > 0 && (std::isalnum(static_cast<unsigned char>(tag[at - 1])) || tag[at - 1] == '_' || tag[at - 1] == ':')) continue;
        const size_t start = at + name.size() + 2, end = tag.find('"', start);
        if (end == std::string::npos) return std::nullopt;
        return unescaped(tag.substr(start, end - start));
    }
    return std::nullopt;
}

/// A name for the exact content of the test's files and the command that runs them.
std::string pin(const std::vector<std::string>& files, const std::string& workflow, const Workspace& workspace) {
    std::string text = "no workflow";
    try {
        const auto known = workflows::load(workspace);
        const auto found = known.find(workflow);
        if (found != known.end()) text = workflows::command_line(found->second);
    } catch (const std::exception&) {
    }
    for (const std::string& path : files) {
        std::string content = "missing";
        try {
            if (workspace.exists(path)) content = workspace.read(path);
        } catch (const std::exception&) {
        }
        text += "\n" + path + "\n" + content;
    }
    return experiments::fingerprint(text);
}

std::vector<std::string> value(const DerivedCheck& check, const std::string& key) {
    for (const std::vector<std::string>& step : check.steps) {
        if (!step.empty() && step[0] == key) return std::vector<std::string>(step.begin() + 1, step.end());
    }
    return {};
}

struct Ran {
    std::optional<std::string> outcome;
    int exit_code = 0;
    std::string diagnostics;
};

/// Runs the test's workflow and reads the named test's outcome. No outcome when the results did not mention it.
Ran run(const std::string& test, const std::string& workflow, const std::string& format, const std::string& report, const Workspace& workspace, bool sandboxed, const CancelToken& cancel) {
    const auto known = workflows::load(workspace);
    const auto found = known.find(workflow);
    if (found == known.end()) throw Error("The workflow " + workflow + " is no longer in mason.workflows.json.");
    const auto started = fs::file_time_type::clock::now();
    const CommandResult result = shell::run(workflows::command_line(found->second), workspace, sandboxed, 300, cancel);
    std::string text = result.output;
    Ran ran;
    ran.exit_code = result.exit_code;
    if (format == "junit" || format == "trx") {
        std::error_code problem;
        const fs::path file = fs::path(workspace.root()) / report;
        const auto written = fs::last_write_time(file, problem);
        if (problem || written < started - std::chrono::seconds(2)) {
            ran.diagnostics = "The workflow did not write " + report + " in this run.";
            return ran;
        }
        text = workspace.read(report);
    }
    ran.outcome = outcome_of(test, outcomes(format, text));
    ran.diagnostics = result.timed_out ? "The workflow ran past 300 seconds." : utf8_length(result.output) > 1200 ? "…" + utf8_suffix(result.output, 1200) : result.output;
    return ran;
}

}  // namespace

std::vector<ProjectTest> load(const Workspace& workspace) {
    std::vector<ProjectTest> tests;
    if (!fs::exists(fs::path(workspace.root()) / kManifest)) return tests;
    const Error invalid(std::string(kManifest) + " must list up to 64 tests, each with requirement, workflow, format (tap, junit, trx, go-json or flutter-json), test, 1 to 16 testFiles and 1 to 32 inputs.");
    const json listed = json::parse(read_file(fs::path(workspace.root()) / kManifest), nullptr, false);
    if (!listed.is_array() || listed.size() > 64) throw invalid;
    const auto known = workflows::load(workspace);
    static const std::set<std::string> formats = {"tap", "junit", "trx", "go-json", "flutter-json"};
    for (const json& item : listed) {
        if (!item.is_object()) throw invalid;
        ProjectTest test{text_of(item, "requirement"), text_of(item, "workflow"), to_lower(text_of(item, "format")), text_of(item, "report"), text_of(item, "test"), list_of(item, "testFiles"), list_of(item, "inputs")};
        const bool needs_report = test.format == "junit" || test.format == "trx";
        if (test.requirement.empty() || test.test.empty() || utf8_length(test.test) > 400 || !formats.count(test.format) || test.test_files.empty() || test.test_files.size() > 16 || test.inputs.empty() ||
            test.inputs.size() > 32 || (needs_report && test.report.empty())) {
            throw invalid;
        }
        if (!known.count(test.workflow)) throw Error(std::string(kManifest) + " names the workflow " + test.workflow + ", which is not in mason.workflows.json.");
        std::vector<std::string> paths = test.test_files;
        paths.insert(paths.end(), test.inputs.begin(), test.inputs.end());
        if (!test.report.empty()) paths.push_back(test.report);
        for (const std::string& path : paths) {
            if (!inside(path)) throw Error(std::string(kManifest) + ": " + path + " is not a path inside the project.");
        }
        tests.push_back(std::move(test));
    }
    return tests;
}

std::optional<ProjectTest> bound(const std::string& unit, const std::vector<ProjectTest>& tests) {
    for (const ProjectTest& test : tests) {
        if (plain(test.requirement) == plain(unit) || plain("verify test " + test.test) == plain(unit)) return test;
    }
    return std::nullopt;
}

std::map<std::string, std::string> outcomes(const std::string& format, const std::string& text) {
    std::map<std::string, std::string> found;
    auto record = [&](const std::string& given, const std::string& outcome) {
        const std::string name = trim(given);
        if (name.empty()) return;
        const auto existing = found.find(name);
        if (existing != found.end() && existing->second == "failed") return;
        if (outcome == "failed" || existing == found.end() || existing->second == "skipped") found[name] = outcome;
    };
    if (format == "tap") {
        for (const std::string& raw : split(text, '\n', false)) {
            std::string line = trim(raw);
            bool ok = true;
            if (line.rfind("not ok", 0) == 0) { ok = false; line = line.substr(6); }
            else if (line.rfind("ok", 0) == 0) line = line.substr(2);
            else continue;
            if (!line.empty() && line[0] != ' ' && line[0] != '\t') continue;  // a word that merely starts with "ok"
            line = trim(line);
            size_t at = 0;
            while (at < line.size() && std::isdigit(static_cast<unsigned char>(line[at]))) at++;
            line = trim(line.substr(at));
            if (line.rfind("- ", 0) == 0) line = trim(line.substr(2));
            else if (line == "-") line.clear();
            std::string directive;
            const size_t hash = line.find('#');
            if (hash != std::string::npos) {
                directive = to_lower(trim(line.substr(hash + 1)));
                if (directive.rfind("skip", 0) == 0 || directive.rfind("todo", 0) == 0) line = trim(line.substr(0, hash));
                else directive.clear();
            }
            record(line, directive.rfind("skip", 0) == 0 ? "skipped" : ok ? "passed" : "failed");
        }
    } else if (format == "junit") {
        for (size_t at = text.find("<testcase"); at != std::string::npos; at = text.find("<testcase", at + 1)) {
            const char after = at + 9 < text.size() ? text[at + 9] : ' ';
            if (!std::isspace(static_cast<unsigned char>(after)) && after != '>' && after != '/') continue;
            const size_t close = text.find('>', at);
            if (close == std::string::npos) break;
            const std::string tag = text.substr(at, close - at);
            std::string body;
            if (text[close - 1] != '/') {
                const size_t end = text.find("</testcase>", close);
                body = text.substr(close + 1, end == std::string::npos ? std::string::npos : end - close - 1);
            }
            const auto name = attribute(tag, "name");
            if (!name) continue;
            const std::string outcome = body.find("<failure") != std::string::npos || body.find("<error") != std::string::npos ? "failed" : body.find("<skipped") != std::string::npos ? "skipped" : "passed";
            record(*name, outcome);
            const auto suite = attribute(tag, "classname");
            if (suite && !suite->empty()) record(*suite + "." + *name, outcome);
        }
    } else if (format == "trx") {
        for (size_t at = text.find("<UnitTestResult"); at != std::string::npos; at = text.find("<UnitTestResult", at + 1)) {
            const size_t close = text.find('>', at);
            if (close == std::string::npos) break;
            const std::string tag = text.substr(at, close - at);
            const auto name = attribute(tag, "testName"), outcome = attribute(tag, "outcome");
            if (!name || !outcome) continue;
            record(*name, *outcome == "Passed" ? "passed" : *outcome == "Failed" || *outcome == "Error" || *outcome == "Timeout" || *outcome == "Aborted" ? "failed" : "skipped");
        }
    } else if (format == "go-json") {
        for (const std::string& line : split(text, '\n', false)) {
            const json event = json::parse(line, nullptr, false);
            if (!event.is_object() || !event.contains("Test") || !event["Test"].is_string() || !event.contains("Action") || !event["Action"].is_string()) continue;
            const std::string action = event["Action"].get<std::string>();
            if (action == "pass" || action == "fail" || action == "skip") record(event["Test"].get<std::string>(), action == "pass" ? "passed" : action == "fail" ? "failed" : "skipped");
        }
    } else if (format == "flutter-json") {
        std::map<long long, std::string> names;
        for (const std::string& line : split(text, '\n', false)) {
            const json event = json::parse(line, nullptr, false);
            if (!event.is_object()) continue;
            const std::string type = event.value("type", "");
            if (type == "testStart" && event.contains("test") && event["test"].is_object() && event["test"].contains("id") && event["test"]["id"].is_number_integer() && event["test"].contains("name") && event["test"]["name"].is_string()) {
                names[event["test"]["id"].get<long long>()] = event["test"]["name"].get<std::string>();
            }
            if (type == "testDone" && event.contains("testID") && event["testID"].is_number_integer() && !(event.contains("hidden") && event["hidden"].is_boolean() && event["hidden"].get<bool>())) {
                const auto name = names.find(event["testID"].get<long long>());
                if (name == names.end()) continue;
                const bool skipped = event.contains("skipped") && event["skipped"].is_boolean() && event["skipped"].get<bool>();
                record(name->second, skipped ? "skipped" : event.value("result", "") == "success" ? "passed" : "failed");
            }
        }
    }
    return found;
}

std::optional<std::string> outcome_of(const std::string& name, const std::map<std::string, std::string>& all) {
    const auto exact = all.find(name);
    if (exact != all.end()) return exact->second;
    std::optional<std::string> ending;
    int count = 0;
    for (const auto& [key, outcome] : all) {
        if (key.size() <= name.size() || key.compare(key.size() - name.size(), name.size(), name) != 0) continue;
        if (std::string(" ./>:").find(key[key.size() - name.size() - 1]) == std::string::npos) continue;
        ending = outcome;
        count++;
    }
    return count == 1 ? ending : std::nullopt;
}

DerivedCheck check(const ProjectTest& test, int unit, const Workspace& workspace, bool require_sandbox, const CancelToken& cancel) {
    std::string baseline = "not run";
    if (!require_sandbox || shell::sandbox_available()) {
        try {
            baseline = run(test.test, test.workflow, test.format, test.report, workspace, shell::sandbox_available(), cancel).outcome.value_or("not reported");
        } catch (const Cancelled&) {
            throw;
        } catch (const std::exception&) {
        }
    }
    DerivedCheck made{unit, test.requirement, "project_test", test.workflow, test.test, "passed", {}, ""};
    std::vector<std::string> files = {"files"};
    files.insert(files.end(), test.test_files.begin(), test.test_files.end());
    made.steps = {{"format", test.format}, {"report", test.report}, files, {"pin", pin(test.test_files, test.workflow, workspace)}, {"baseline", baseline}};
    return made;
}

DerivedResult verify(const DerivedCheck& check, const Workspace& workspace, bool require_sandbox, const CancelToken& cancel) {
    auto first = [&](const std::string& key) {
        const std::vector<std::string> found = value(check, key);
        return found.empty() ? std::string() : found[0];
    };
    const std::vector<std::string> files = value(check, "files");
    if (pin(files, check.path, workspace) != first("pin")) {
        return DerivedResult{check, "failed", "The test's own files (" + join(files, ", ") + ") or the workflow that runs them changed during this request. A pinned test has to pass as it was written; put it back."};
    }
    if (require_sandbox && !shell::sandbox_available()) return DerivedResult{check, "notRun", "The sandbox is unavailable here, so the project's tests were not run."};
    try {
        const Ran ran = run(check.target, check.path, first("format"), first("report"), workspace, shell::sandbox_available(), cancel);
        const std::string story = "baseline: " + (first("baseline").empty() ? std::string("not run") : first("baseline")) + "; now: " + ran.outcome.value_or("not reported") + " (the workflow " + check.path + " exited " +
                                  std::to_string(ran.exit_code) + ")";
        const bool passed = ran.outcome && *ran.outcome == "passed";
        return DerivedResult{check, passed ? "passed" : "failed", passed ? story : story + "\n" + ran.diagnostics};
    } catch (const Cancelled&) {
        throw;
    } catch (const std::exception& error) {
        return DerivedResult{check, "failed", error.what()};
    }
}

}  // namespace mason::project_tests
