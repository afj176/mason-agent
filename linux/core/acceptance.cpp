#include "acceptance.hpp"

#include <algorithm>
#include <chrono>
#include <regex>
#include <set>

#include "browser.hpp"
#include "models.hpp"
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

std::vector<AcceptanceResult> run(const Workspace& workspace, const AcceptanceContract& contract, const std::function<void(const std::string&)>& progress, const CancelToken& cancel, bool require_sandbox) {
    if (contract.application.empty()) {
        for (const AcceptanceScenario& scenario : contract.scenarios) {
            for (const AcceptanceStep& step : scenario.steps) {
                if (kApplicationSteps.count(step.op)) throw Error("API and database assertions require an isolated verification application.");
            }
        }
    } else if (require_sandbox && !shell::sandbox_available()) {
        throw Error("The sandbox is unavailable here, so Auto will not run the project's application. Choose another permission mode to be asked first.");
    }
    std::vector<AcceptanceResult> results;
    for (const AcceptanceScenario& scenario : contract.scenarios) {
        check_cancel(cancel);
        if (progress) progress("Checking " + scenario.id);
        AcceptanceResult outcome{scenario, true, {}};
        for (const int width : kWidths) {
            json steps = json::array({{{"action", "viewport"}, {"width", width}}});
            for (const AcceptanceStep& step : scenario.steps) steps.push_back({{"action", "accept"}, {"op", step.op}, {"target", step.target}, {"value", step.value}});
            // A fresh copy of the project and a fresh database for each width.
            std::unique_ptr<VerificationApp> app;
            if (!contract.application.empty()) app = std::make_unique<VerificationApp>(workspace, contract.application, shell::sandbox_available(), cancel);
            const browser::Result result = browser::run(workspace, scenario.page, steps, cancel, app.get());
            app.reset();
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

// ---- an application under test ---------------------------------------------------------------------------------

namespace {

/// The same bridge program the Windows app uses.
const char* const kBridge = R"py("""Run a WSGI application in Mason's disposable LPAC workspace. The browser never opens a socket."""
import base64
import importlib
import io
import json
import os
from pathlib import Path
import sys
import time
import traceback
from urllib.parse import urlsplit, unquote
from wsgiref.util import setup_testing_defaults

mailbox = Path(__file__).resolve().parent
sys.path.insert(0, os.getcwd())
module, name = sys.argv[1].split(":")
application = getattr(importlib.import_module(module), name)

def publish(path, data):
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(data), encoding="utf-8")
    os.replace(temp, path)

def dispatch(request):
    url = urlsplit(request["path"])
    body = base64.b64decode(request["body"])
    environ = {}
    setup_testing_defaults(environ)
    environ.update(REQUEST_METHOD=request["method"], PATH_INFO=unquote(url.path, encoding="latin1"),
                   QUERY_STRING=url.query, SERVER_NAME="workspace.mason.invalid", SERVER_PORT="443",
                   CONTENT_TYPE=request["contentType"], CONTENT_LENGTH=str(len(body)),
                   HTTP_COOKIE=request["cookie"], HTTP_HOST="workspace.mason.invalid")
    environ["wsgi.url_scheme"] = "https"
    environ["wsgi.input"] = io.BytesIO(body)
    response = {}
    output = io.BytesIO()
    def write(data):
        if output.tell() + len(data) > 2 * 1024 * 1024:
            raise ValueError("Response exceeds 2 MiB")
        output.write(data)
    def start(status, headers, exc_info=None):
        response.update(status=int(status.split()[0]), headers=dict((k.lower(), v) for k, v in headers))
        return write
    result = application(environ, start)
    try:
        for data in result:
            write(data)
    finally:
        if hasattr(result, "close"):
            result.close()
    response["body"] = base64.b64encode(output.getvalue()).decode("ascii")
    return response

publish(mailbox / "ready.json", {"ready": True})
while not (mailbox / "stop").exists():
    for path in mailbox.glob("*.request"):
        try:
            response = dispatch(json.loads(path.read_text(encoding="utf-8-sig")))
        except Exception:
            error = traceback.format_exc(limit=5)
            print(error, file=sys.stderr, flush=True)
            response = {"status": 500, "headers": {}, "body": "", "error": error[-1600:]}
        path.unlink()
        publish(path.with_suffix(".response"), response)
    time.sleep(.015)
)py";

std::string unbase64(const std::string& text) {
    static const std::string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    unsigned buffer = 0;
    int bits = 0;
    for (const char c : text) {
        const size_t at = alphabet.find(c);
        if (at == std::string::npos) continue;
        buffer = (buffer << 6) | static_cast<unsigned>(at);
        bits += 6;
        if (bits >= 8) { bits -= 8; out += static_cast<char>((buffer >> bits) & 0xFF); }
    }
    return out;
}

std::string base64(const std::string& data) {
    static const char* const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    for (size_t i = 0; i < data.size(); i += 3) {
        const unsigned a = static_cast<unsigned char>(data[i]);
        const unsigned b = i + 1 < data.size() ? static_cast<unsigned char>(data[i + 1]) : 0;
        const unsigned c = i + 2 < data.size() ? static_cast<unsigned char>(data[i + 2]) : 0;
        out += alphabet[a >> 2];
        out += alphabet[((a & 3) << 4) | (b >> 4)];
        out += i + 1 < data.size() ? alphabet[((b & 15) << 2) | (c >> 6)] : '=';
        out += i + 2 < data.size() ? alphabet[c & 63] : '=';
    }
    return out;
}

std::string percent_decode(const std::string& text) {
    std::string out;
    for (size_t i = 0; i < text.size(); i++) {
        if (text[i] == '%' && i + 2 < text.size() && std::isxdigit(static_cast<unsigned char>(text[i + 1])) && std::isxdigit(static_cast<unsigned char>(text[i + 2]))) {
            out += static_cast<char>(std::stoi(text.substr(i + 1, 2), nullptr, 16));
            i += 2;
        } else {
            out += text[i] == '+' ? ' ' : text[i];
        }
    }
    return out;
}

std::string quoted(const std::string& word) { return "'" + replace_all(word, "'", "'\\''") + "'"; }

}  // namespace

VerificationApp::VerificationApp(const Workspace& project, const std::string& entry, bool sandboxed, const CancelToken& cancel) {
    static const std::regex name(R"([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*:[A-Za-z_]\w*)");
    if (!std::regex_match(entry, name)) throw Error("Application must name a local Python WSGI module:callable.");
    const std::string python = transformers::python();
    if (python.empty()) throw Error("No python3 was found to run the application.");
    folder_ = fs::temp_directory_path() / ("mason-verify-" + new_id());
    mailbox_ = folder_ / ("mason-bridge-" + new_id().substr(0, 12));
    fs::create_directories(mailbox_);
    try {
        const Workspace copy(folder_.string());
        const FileListing listing = project.files(".", 1000);
        if (listing.truncated) throw Error("The application has more than 1,000 files, which is more than a verification copy holds.");
        size_t total = 0;
        for (const std::string& path : listing.paths) {
            check_cancel(cancel);
            const std::optional<std::string> data = project.data(path);
            if (!data) continue;
            total += data->size();
            if (data->size() > 4 * 1024 * 1024 || total > 32 * 1024 * 1024) throw Error("The application is larger than a verification copy holds (4 MiB a file, 32 MiB in all).");
            copy.write_data(path, *data);
        }
        write_file_atomic(mailbox_ / "runner.py", kBridge);
        const std::string command = quoted(python) + " -B " + quoted(mailbox_.filename().string() + "/runner.py") + " " + quoted(entry);
        thread_ = std::thread([this, copy, command, sandboxed] {
            CommandResult result;
            try {
                result = shell::run(command, copy, sandboxed, 600, stop_);
            } catch (const std::exception& error) {
                result.exit_code = -1;
                result.output = error.what();
            }
            std::lock_guard<std::mutex> guard(lock_);
            ended_ = result;
        });
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(60);
        std::error_code ignored;
        while (!fs::exists(mailbox_ / "ready.json", ignored)) {
            check_cancel(cancel);
            if (std::chrono::steady_clock::now() > deadline) throw Error("The application did not start within a minute.");
            {
                std::lock_guard<std::mutex> guard(lock_);
                if (ended_) throw Error("The application stopped as it started (exit " + std::to_string(ended_->exit_code) + "): " + utf8_suffix(ended_->output, 1200));
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    } catch (...) {
        stop();
        throw;
    }
}

VerificationApp::~VerificationApp() { stop(); }

void VerificationApp::stop() {
    std::error_code ignored;
    if (fs::exists(mailbox_, ignored)) {
        try { write_file_atomic(mailbox_ / "stop", ""); } catch (const std::exception&) {}
    }
    if (thread_.joinable()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        stop_->store(true);
        thread_.join();
    }
    // Only ever a folder this made in the temporary directory.
    if (starts_with(folder_.filename().string(), "mason-verify-")) fs::remove_all(folder_, ignored);
}

std::optional<browser::BackendReply> VerificationApp::answer(const std::string& method, const std::string& target, const std::map<std::string, std::string>& headers, const std::string& body) {
    const std::string path = target.substr(0, target.find('?'));
    if (path == "/__mason/harness") return std::nullopt;
    const auto told = [](const json& object, int status = 200) { return browser::BackendReply{status, "application/json", {}, object.dump()}; };
    if (path == "/__mason/assert") {
        std::map<std::string, std::string> query;
        const size_t mark = target.find('?');
        for (const std::string& pair : split(mark == std::string::npos ? "" : target.substr(mark + 1), '&', false)) {
            const size_t equals = pair.find('=');
            query[percent_decode(pair.substr(0, equals))] = equals == std::string::npos ? "" : percent_decode(pair.substr(equals + 1));
        }
        try {
            return told({{"ok", true}, {"detail", check(query["op"], query["target"], query["value"], query["captured"])}});
        } catch (const std::exception& error) {
            return told({{"ok", false}, {"detail", error.what()}});
        }
    }
    static const std::set<std::string> methods = {"GET", "POST", "HEAD", "PUT", "PATCH", "DELETE"};
    if (!methods.count(method) || body.size() > 128 * 1024) return told({{"error", "Unsupported request."}}, 405);
    const auto header = [&](const char* name) { const auto found = headers.find(name); return found == headers.end() ? std::string() : found->second; };
    const std::string id = new_id();
    const json request = {{"method", method}, {"path", target}, {"body", base64(body)}, {"contentType", header("content-type")}, {"cookie", header("cookie")}};
    std::error_code ignored;
    try {
        write_file_atomic(mailbox_ / (id + ".writing"), request.dump());
        fs::rename(mailbox_ / (id + ".writing"), mailbox_ / (id + ".request"));
    } catch (const std::exception&) {
        return told({{"error", "The request could not be handed to the application."}}, 502);
    }
    const fs::path response = mailbox_ / (id + ".response");
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
    while (!fs::exists(response, ignored)) {
        if (std::chrono::steady_clock::now() > deadline) return told({{"error", "The application did not answer " + path + " within eight seconds."}}, 504);
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    json object;
    try {
        object = json::parse(read_file(response), nullptr, false);
    } catch (const std::exception&) {
    }
    fs::remove(response, ignored);
    if (!object.is_object() || !object.contains("status") || !object["status"].is_number_integer()) return told({{"error", "The application's answer could not be read."}}, 502);
    browser::BackendReply reply;
    reply.status = object["status"].get<int>();
    const json given = object.value("headers", json::object());
    reply.content_type = given.is_object() ? given.value("content-type", "application/octet-stream") : "application/octet-stream";
    reply.body = unbase64(object.value("body", ""));
    {
        std::lock_guard<std::mutex> guard(lock_);
        if (reply.status >= 200 && reply.status < 400) responses_.push_back({method + " " + path, contains_ci(reply.content_type, "json") && reply.body.size() <= 65536 ? reply.body : ""});
    }
    if (starts_with(to_lower(reply.content_type), "text/html") && is_valid_utf8(reply.body)) reply.body = browser::with_recorder(reply.body);
    for (const char* name : {"set-cookie", "location"}) {
        if (given.is_object() && given.contains(name) && given[name].is_string()) reply.headers.push_back({name, given[name].get<std::string>()});
    }
    return reply;
}

std::string VerificationApp::check(const std::string& op, const std::string& target, const std::string& value, const std::string& captured) {
    const auto whole = [](const std::string& text) -> std::optional<int> {
        if (text.empty() || text.size() > 6 || !std::all_of(text.begin(), text.end(), [](unsigned char c) { return std::isdigit(c); })) return std::nullopt;
        return std::stoi(text);
    };
    if (op == "sqlite_count") {
        static const std::regex shape(R"(((?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_-]+\.(?:sqlite3?|db))#([A-Za-z_][A-Za-z0-9_]*))");
        std::smatch match;
        const std::optional<int> expected = whole(value);
        if (!std::regex_match(target, match, shape) || !expected) throw Error("Use a database filename in the project, #table and the expected number of rows.");
        const fs::path file = folder_ / match[1].str();
        std::error_code ignored;
        if (!fs::exists(file, ignored)) throw Error("The application has not made the database " + match[1].str() + ".");
        // A separate Python reads the committed rows, apart from the application and its open connection.
        const std::string query = "import pathlib, sqlite3, sys\nwith sqlite3.connect(pathlib.Path(sys.argv[1]).resolve().as_uri() + '?mode=ro', uri=True, timeout=2) as db:\n    db.execute('PRAGMA trusted_schema=OFF')\n"
                                  "    name = sys.argv[2]\n    if not any(row[0] == 'main' and row[1] == name and row[2] == 'table' for row in db.execute('PRAGMA table_list')):\n"
                                  "        raise ValueError('Expected a regular stored table')\n    print(db.execute('SELECT count(*) FROM \"' + name + '\"').fetchone()[0])\n";
        const CommandResult result = shell::run(quoted(transformers::python()) + " -I -B -c " + quoted(query) + " " + quoted(file.string()) + " " + quoted(match[2].str()), Workspace(folder_.string()), false, 20);
        const std::optional<int> rows = whole(trim(result.output));
        if (result.exit_code != 0 || !rows) throw Error("The committed rows of " + target + " could not be read.");
        if (*rows != *expected) throw Error("expected " + std::to_string(*expected) + ", observed " + std::to_string(*rows) + " committed rows");
        return std::to_string(*rows) + " rows";
    }
    std::vector<std::string> matched;
    {
        std::lock_guard<std::mutex> guard(lock_);
        for (const auto& [route, body] : responses_) {
            if (route == target) matched.push_back(body);
        }
    }
    if (op == "response_count") {
        const std::optional<int> expected = whole(value);
        if (!expected) throw Error("Give the expected number of responses.");
        if (static_cast<int>(matched.size()) != *expected) throw Error("expected " + std::to_string(*expected) + ", observed " + std::to_string(matched.size()) + " responses");
        return std::to_string(matched.size()) + " responses";
    }
    if (op != "response_same") throw Error("unknown step");
    if (matched.empty()) throw Error("no response was observed from " + target);
    for (const std::string& body : matched) {
        const json object = json::parse(body, nullptr, false);
        if (!object.is_object() || !object.contains(value) || !object[value].is_string() || object[value].get<std::string>() != captured) throw Error("field " + value + " differed from the captured value " + captured);
    }
    return std::to_string(matched.size()) + " responses matched " + captured;
}

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
