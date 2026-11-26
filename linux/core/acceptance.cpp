#include "acceptance.hpp"

#include <algorithm>
#include <set>

#include "browser.hpp"
#include "planning.hpp"

namespace mason {

namespace acceptance {

namespace {

const int kWidths[] = {390, 1280};
const std::set<std::string> kActions = {"fill", "click", "double_click", "click_if_enabled", "reload"};
const std::set<std::string> kAssertions = {"count_distinct", "money", "shelf_price", "text", "empty", "capture", "same", "new_id"};
/// Steps that look inside a running application or its database. They need the isolated application run Windows has.
const std::set<std::string> kApplicationSteps = {"sqlite_count", "response_count", "response_same"};

/// Field names are matched without regard to case, as the Windows app does.
const json* field(const json& object, const std::string& name) {
    for (auto it = object.begin(); it != object.end(); ++it) {
        if (to_lower(it.key()) == name) return &it.value();
    }
    return nullptr;
}

std::string text_field(const json& object, const std::string& name) {
    const json* value = field(object, name);
    return value && value->is_string() ? value->get<std::string>() : "";
}

bool is_number(const std::string& text) {
    if (text.empty()) return false;
    char* end = nullptr;
    std::strtod(text.c_str(), &end);
    return end && *end == '\0';
}

void validate(const std::vector<AcceptanceStep>& steps) {
    if (steps.empty() || steps.size() > 24) throw Error("Provide 1–24 observable browser steps.");
    bool captured = false;
    for (const AcceptanceStep& step : steps) {
        if (step.target.size() > 180 || step.value.size() > 300 || !(kActions.count(step.op) || kAssertions.count(step.op) || kApplicationSteps.count(step.op))) throw Error("Unknown or oversized browser step: " + step.op + ".");
        if (step.op != "reload" && trim(step.target).empty()) throw Error("A browser step needs a target.");
        if (step.op == "count_distinct") {
            const bool digits = !step.value.empty() && step.value.size() <= 4 && std::all_of(step.value.begin(), step.value.end(), [](unsigned char c) { return std::isdigit(c); });
            if (!digits || std::stoi(step.value) < 1 || std::stoi(step.value) > 1000) throw Error("Specify a positive distinct item count.");
        }
        if ((step.op == "money" || step.op == "shelf_price") && !is_number(step.value)) throw Error("Specify the expected decimal amount.");
        if (step.op == "text" && trim(step.value).empty()) throw Error("Specify exact visible text.");
        if ((step.op == "same" || step.op == "new_id") && !captured) throw Error("Capture a value before comparing it.");
        if (step.op == "capture") captured = true;
    }
    const std::string& last = steps.back().op;
    if (!((kAssertions.count(last) && last != "capture") || kApplicationSteps.count(last))) throw Error("End each scenario with an assertion.");
}

}  // namespace

bool exists(const Workspace& workspace) {
    try {
        return workspace.exists(kFilename);
    } catch (const std::exception&) {
        return false;
    }
}

AcceptanceContract parse(const Workspace& workspace, const std::string& source) {
    const json object = json::parse(source, nullptr, false);
    const json* version = object.is_object() ? field(object, "version") : nullptr;
    const json* listed = object.is_object() ? field(object, "scenarios") : nullptr;
    if (!version || !version->is_number_integer() || version->get<int>() != 1 || !listed || !listed->is_array() || listed->empty() || listed->size() > 32) {
        throw Error("Use version 1 with 1–32 uniquely named acceptance scenarios.");
    }
    AcceptanceContract contract;
    contract.application = text_field(object, "application");
    std::set<std::string> ids;
    for (const json& item : *listed) {
        if (!item.is_object()) throw Error("Each scenario requires a bounded ID, a requirement and a page.");
        AcceptanceScenario scenario{text_field(item, "id"), text_field(item, "requirement"), text_field(item, "page"), {}};
        if (trim(scenario.id).empty() || utf8_length(scenario.id) > 100 || trim(scenario.requirement).empty() || utf8_length(scenario.requirement) > 500 || !field(item, "page")) {
            throw Error("Each scenario requires a bounded ID, a requirement and a page.");
        }
        workspace.resolve(scenario.page);
        if (const json* steps = field(item, "steps"); steps && steps->is_array()) {
            for (const json& step : *steps) {
                if (!step.is_object() || text_field(step, "op").empty()) throw Error("Each step needs an Op.");
                scenario.steps.push_back({text_field(step, "op"), text_field(step, "target"), text_field(step, "value")});
            }
        }
        validate(scenario.steps);
        if (!ids.insert(scenario.id).second) throw Error("Use version 1 with 1–32 uniquely named acceptance scenarios.");
        contract.scenarios.push_back(std::move(scenario));
    }
    return contract;
}

AcceptanceContract read(const Workspace& workspace) { return parse(workspace, workspace.read(kFilename)); }

std::vector<AcceptanceResult> run(const Workspace& workspace, const AcceptanceContract& contract, const std::function<void(const std::string&)>& progress, const CancelToken& cancel) {
    if (!contract.application.empty()) {
        throw Error("This contract runs a Python application (" + contract.application + ") in an isolated copy, which Mason does on Windows only for now. Contracts for static pages run here.");
    }
    for (const AcceptanceScenario& scenario : contract.scenarios) {
        for (const AcceptanceStep& step : scenario.steps) {
            if (kApplicationSteps.count(step.op)) throw Error("API and database assertions require an isolated verification application.");
        }
    }
    std::vector<AcceptanceResult> results;
    for (const AcceptanceScenario& scenario : contract.scenarios) {
        check_cancel(cancel);
        if (progress) progress("Checking " + scenario.id);
        AcceptanceResult outcome{scenario, true, {}};
        for (const int width : kWidths) {
            json steps = json::array({{{"action", "viewport"}, {"width", width}}});
            for (const AcceptanceStep& step : scenario.steps) steps.push_back({{"action", "accept"}, {"op", step.op}, {"target", step.target}, {"value", step.value}});
            const browser::Result result = browser::run(workspace, scenario.page, steps, cancel);
            const auto failed = std::find_if(result.steps.begin(), result.steps.end(), [](const browser::Step& step) { return !step.ok; });
            if (failed != result.steps.end()) {
                outcome.passed = false;
                outcome.detail = {"at " + std::to_string(width) + "px, step " + std::to_string(failed - result.steps.begin() + 1) + ", " + failed->action + ": " + failed->detail};
            } else if (!result.errors.empty()) {
                outcome.passed = false;
                std::vector<std::string> first(result.errors.begin(), result.errors.begin() + static_cast<std::ptrdiff_t>(std::min<size_t>(3, result.errors.size())));
                outcome.detail = {"at " + std::to_string(width) + "px the page raised: " + join(first, "; ")};
            } else if (width == kWidths[1]) {
                for (const browser::Step& step : result.steps) {
                    if (!step.detail.empty()) outcome.detail.push_back(step.action + " = " + step.detail);
                }
            }
            if (!outcome.passed) break;
        }
        results.push_back(std::move(outcome));
    }
    return results;
}

std::string receipt(const std::vector<AcceptanceResult>& results) {
    const auto passed = std::count_if(results.begin(), results.end(), [](const AcceptanceResult& result) { return result.passed; });
    std::string text = std::to_string(passed) + " of " + std::to_string(results.size()) + " acceptance scenarios passed, each at 390px and 1280px.";
    for (const AcceptanceResult& result : results) {
        text += std::string("\n") + (result.passed ? "PASSED " : "FAILED ") + result.scenario.id + " — " + result.scenario.requirement + " (" + result.scenario.page + ")";
        for (const std::string& line : result.detail) text += "\n  " + line;
    }
    return text;
}

}  // namespace acceptance

namespace review {

ReviewFindings findings(const Session& session, const Workspace& workspace, ChatModel& model, const CancelToken& cancel) {
    std::vector<std::string> paths;
    std::string request;
    for (const Turn& turn : session.turns) {
        if (turn.undone) continue;
        if (!turn.changes.empty()) request = turn.request;
        for (const FileChange& change : turn.changes) {
            if (!change.opaque() && !change.deleted() && std::find(paths.begin(), paths.end(), change.path) == paths.end()) paths.push_back(change.path);
        }
    }
    struct Source {
        std::string path, data;
        std::vector<std::string> lines;
    };
    std::vector<Source> sources;
    size_t size = 0;
    for (size_t i = 0; i < paths.size() && i < 12; i++) {
        std::optional<std::string> data;
        try {
            data = workspace.data(paths[i]);
        } catch (const std::exception&) {
        }
        if (!data || !is_valid_utf8(*data) || size + data->size() > 120000) continue;
        size += data->size();
        std::vector<std::string> lines = split(replace_all(*data, "\r\n", "\n"), '\n');
        if (!lines.empty() && lines.back().empty()) lines.pop_back();
        sources.push_back({paths[i], *data, std::move(lines)});
    }
    if (sources.empty()) throw Error("This session has not changed any text files to review.");
    std::string listing;
    ReviewFindings found;
    for (const Source& source : sources) {
        found.files.push_back(source.path);
        listing += (listing.empty() ? "" : "\n\n") + std::string("File: ") + source.path;
        for (size_t i = 0; i < source.lines.size(); i++) listing += "\n" + std::to_string(i + 1) + "\t" + source.lines[i];
    }
    const std::string system =
        "You review a change another engineer just made. Report only real defects: wrong results, crashes, broken edge cases, security problems. Not style.\n"
        "Reply with only a JSON array. Each finding is {\"path\": file, \"line\": line number, \"evidence\": text copied exactly from that line, \"problem\": one or two sentences}.\n"
        "Reply [] when you find nothing.";
    const ModelReply reply = model.complete({{"system", system, {}, ""}, {"user", "The request was: " + utf8_prefix(request, 2000) + "\n\nThe changed files as they are now, with line numbers:\n\n" + listing, {}, ""}},
                                            {}, [](const std::string&) {}, cancel);
    const size_t start = reply.text.find('['), end = reply.text.rfind(']');
    const json items = start == std::string::npos || end == std::string::npos || start > end ? json() : json::parse(reply.text.substr(start, end - start + 1), nullptr, false);
    if (!items.is_array()) throw Error("The model did not answer with a list of findings.");
    for (const json& item : items) {
        if (found.comments.size() >= kLimit) break;
        const std::string path = item.is_object() ? item.value("path", "") : "";
        const std::string evidence = item.is_object() && item.contains("evidence") && item["evidence"].is_string() ? trim(item["evidence"].get<std::string>()) : "";
        const std::string problem = item.is_object() && item.contains("problem") && item["problem"].is_string() ? trim(item["problem"].get<std::string>()) : "";
        int line = 0;
        if (item.is_object() && item.contains("line")) {
            if (item["line"].is_number_integer()) line = item["line"].get<int>();
            else if (item["line"].is_string()) line = std::atoi(item["line"].get<std::string>().c_str());
        }
        const auto source = std::find_if(sources.begin(), sources.end(), [&](const Source& each) { return each.path == path; });
        if (source == sources.end() || evidence.empty() || problem.empty() || line < 1 || static_cast<size_t>(line) > source->lines.size() ||
            source->lines[static_cast<size_t>(line) - 1].find(evidence) == std::string::npos) {
            found.dropped++;
            continue;
        }
        ReviewComment made;
        made.path = source->path;
        made.revision = sha256_hex(source->data);
        made.line = line;
        made.quote = source->lines[static_cast<size_t>(line) - 1];
        made.note = utf8_prefix(problem, 2000);
        found.comments.push_back(std::move(made));
    }
    return found;
}

std::string summary(const ReviewFindings& found) {
    const size_t count = found.comments.size();
    std::string text = count == 0 ? "The review of " + join(found.files, ", ") + " found nothing to report."
                                  : "The review found " + std::to_string(count) + (count == 1 ? " thing in " : " things in ") + join(found.files, ", ") + ":";
    for (const ReviewComment& item : found.comments) text += "\n" + location(item) + " — " + item.note;
    if (found.dropped > 0) text += "\n" + std::to_string(found.dropped) + (found.dropped == 1 ? " finding was" : " findings were") + " left out because the text quoted was not on the line named.";
    if (count > 0) text += "\nThey are pinned as comments and go with your next request. A model wrote them: check each before acting on it.";
    return text;
}

}  // namespace review

}  // namespace mason
