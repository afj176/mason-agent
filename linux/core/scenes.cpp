#include "scenes.hpp"

#include <algorithm>
#include <map>
#include <regex>
#include <set>

namespace mason::scenes {

namespace {

const std::set<std::string> kRead = {"read_file", "open_file", "read_lines", "list_files", "search_files", "search_next", "semantic_search", "repository_map",
                                     "inspect_symbol", "navigate_code", "read_documentation", "read_skill", "git_status", "mcp_read", "mcp_resources"};
const std::set<std::string> kEdit = {"edit_file", "write_file", "apply_edits"};
const std::set<std::string> kCommand = {"run_command", "run_shell", "start_job", "poll_job", "stop_job", "start_terminal", "terminal_input", "terminal_status",
                                        "terminal_stop", "run_workflow", "verify_acceptance"};
const std::set<std::string> kPage = {"open_preview", "check_page", "desktop_visual", "desktop_observe", "desktop_act"};
/// The calls whose subject is the project path they worked on (call_subject puts a path before a query).
const std::set<std::string> kPathTools = {"read_file", "open_file", "read_lines", "list_files", "edit_file", "write_file", "apply_edits", "inspect_symbol", "open_preview", "check_page"};
/// The calls that start a program: their scene is labelled "Ran …".
const std::set<std::string> kRuns = {"run_command", "run_shell", "start_job", "start_terminal", "run_workflow"};

std::string plural(size_t n, const char* one, const char* many) { return std::to_string(n) + " " + (n == 1 ? one : many); }

std::string clip(const std::string& text, size_t limit) { return utf8_length(text) > limit ? utf8_prefix(text, limit - 1) + "…" : text; }

/// Distinct paths among the calls; a call with no path counts on its own.
size_t distinct(const std::vector<const Activity*>& calls) {
    std::set<std::string> seen;
    for (size_t i = 0; i < calls.size(); i++) seen.insert(path_of(*calls[i]).value_or(std::string(1, '\0') + std::to_string(i)));
    return seen.size();
}

/// The words of a command line written with shell quoting: 'a b' and "a b" are one word, \x is x.
std::vector<std::string> words(const std::string& line) {
    std::vector<std::string> out;
    std::string word;
    bool any = false;
    for (size_t i = 0; i < line.size(); i++) {
        const char c = line[i];
        if (c == '\'') {
            const size_t end = line.find('\'', i + 1);
            word += line.substr(i + 1, (end == std::string::npos ? line.size() : end) - i - 1);
            i = end == std::string::npos ? line.size() : end;
            any = true;
        } else if (c == '"') {
            size_t j = i + 1;
            for (; j < line.size() && line[j] != '"'; j++) {
                if (line[j] == '\\' && j + 1 < line.size()) j++;
                word += line[j];
            }
            i = j;
            any = true;
        } else if (c == '\\' && i + 1 < line.size()) {
            word += line[++i];
            any = true;
        } else if (c == ' ' || c == '\t' || c == '\n') {
            if (any) out.push_back(word);
            word.clear();
            any = false;
        } else {
            word += c;
            any = true;
        }
    }
    if (any) out.push_back(word);
    return out;
}

std::string base_name(const std::string& path) {
    const size_t slash = path.find_last_of("/\\");
    return slash == std::string::npos || slash + 1 == path.size() ? path : path.substr(slash + 1);
}

/// The last number a pattern's first or second group matched, as the summary at the end of the output wins.
std::optional<int> last_count(const std::string& output, const std::regex& pattern) {
    std::optional<int> best;
    for (auto it = std::sregex_iterator(output.begin(), output.end(), pattern); it != std::sregex_iterator(); ++it) {
        for (size_t group = 1; group < it->size(); group++) {
            if (!(*it)[group].matched) continue;
            try {
                best = std::stoi((*it)[group].str());
            } catch (const std::exception&) {
            }
            break;
        }
    }
    return best;
}

bool failure_line(const std::string& line) {
    for (const char* glyph : {"✗", "✕", "×", "✘", "● "}) {
        if (starts_with(line, glyph)) return true;
    }
    static const std::regex pattern(R"(^(?:FAIL\b|FAILED\b|ERROR\b|error(?:\s+[A-Za-z]+\d+)?:|[\w./\\-]+\(\d+(?:,\d+)?\):\s*error|AssertionError|Traceback|npm ERR!))", std::regex::icase);
    return std::regex_search(line, pattern);
}

bool summary_line(const std::string& line) {
    static const std::regex pattern(R"(^(?:Tests?:|Test Suites:|Failed!\s+-|FAILED \(|=+.*(?:passed|failed)))", std::regex::icase);
    return std::regex_search(line, pattern);
}

}  // namespace

Kind kind_of(const std::string& tool) {
    if (kRead.count(tool)) return Kind::Read;
    if (kEdit.count(tool)) return Kind::Edit;
    if (kCommand.count(tool)) return Kind::Command;
    if (kPage.count(tool)) return Kind::Page;
    return Kind::Other;
}

bool is_search(const std::string& tool) { return tool == "search_files" || tool == "search_next" || tool == "semantic_search" || tool == "list_files"; }

std::vector<Scene> group(const std::vector<Activity>& activities) {
    std::vector<Scene> scenes;
    for (size_t i = 0; i < activities.size(); i++) {
        const Kind kind = kind_of(activities[i].tool);
        if (!scenes.empty() && scenes.back().kind == kind && scenes.back().count < kMaxSceneLength) scenes.back().count++;
        else scenes.push_back({kind, i, 1});
    }
    return scenes;
}

std::string normalize(const std::string& path) {
    std::string out = replace_all(path, "\\", "/");
    while (starts_with(out, "./")) out.erase(0, 2);
    const size_t start = out.find_first_not_of('/');
    out = start == std::string::npos ? "" : out.substr(start);
    return out.empty() ? replace_all(path, "\\", "/") : out;
}

std::optional<std::string> path_of(const Activity& activity) {
    if (!kPathTools.count(activity.tool) || trim(activity.subject).empty()) return std::nullopt;
    return normalize(trim(activity.subject));
}

bool failed(const Activity& activity) { return activity.state == "failed" || activity.state == "denied"; }

std::string command_text(const Activity& activity) {
    if (activity.tool == "run_workflow") return "workflow " + activity.subject;
    if (activity.tool != "run_command") return activity.subject;
    // run_command keeps each word quoted exactly; a person reads it without the quotes.
    std::string out;
    for (const std::string& word : words(activity.subject)) {
        const bool spaced = word.empty() || word.find_first_of(" \t\"'") != std::string::npos;
        out += (out.empty() ? "" : " ") + (spaced ? "\"" + replace_all(word, "\"", "\\\"") + "\"" : word);
    }
    return out;
}

std::string label(const Scene& scene, const std::vector<Activity>& activities) {
    std::vector<const Activity*> items;
    for (size_t i = scene.first; i < scene.first + scene.count && i < activities.size(); i++) items.push_back(&activities[i]);
    if (items.empty()) return "";
    switch (scene.kind) {
        case Kind::Read: {
            std::vector<const Activity*> reads;
            size_t searches = 0;
            for (const Activity* item : items) {
                if (is_search(item->tool)) searches++;
                else reads.push_back(item);
            }
            std::vector<std::string> parts;
            if (!reads.empty()) parts.push_back("Read " + plural(distinct(reads), "file", "files"));
            if (searches > 0) parts.push_back(std::string(parts.empty() ? "Searched " : "searched ") + plural(searches, "time", "times"));
            return join(parts, ", ");
        }
        case Kind::Edit: {
            const std::optional<std::string> one = items.size() == 1 ? path_of(*items[0]) : std::nullopt;
            return one ? "Edited " + base_name(*one) : "Edited " + plural(distinct(items), "file", "files");
        }
        case Kind::Command: {
            if (items.size() > 1) return "Ran " + std::to_string(items.size()) + " commands";
            const Activity& call = *items[0];
            if (!kRuns.count(call.tool)) return clip(replace_all(call.tool, "_", " ") + (call.subject.empty() ? "" : " " + call.subject), 48);
            // Name the program, not the folder it was found in: /usr/bin/python3 -m unittest → python3 -m unittest.
            std::string line = trim(replace_all(command_text(call), "\n", " "));
            const std::vector<std::string> parts = words(line);
            if (call.tool != "run_workflow" && !parts.empty() && parts[0].find('/') != std::string::npos) {
                // The first word as written, quotes and all, is replaced by the program's file name.
                const bool quoted = line[0] == '\'' || line[0] == '"';
                size_t end = quoted ? line.find(line[0], 1) : line.find(' ');
                if (quoted && end != std::string::npos) end++;
                line = base_name(parts[0]) + (end == std::string::npos ? "" : line.substr(end));
            }
            return "Ran " + clip(line, 48);
        }
        case Kind::Page: return items.size() == 1 ? "Checked the page" : "Checked the page " + std::to_string(items.size()) + " times";
        case Kind::Other: break;
    }
    return items.size() == 1 ? replace_all(items[0]->tool, "_", " ") : std::to_string(items.size()) + " other steps";
}

CommandSummary command(const Activity& activity) {
    CommandSummary summary;
    std::string output = replace_all(activity.result, "\r\n", "\n");
    // Mason writes "exit N" on the first line of a command's result.
    static const std::regex exit_line(R"(^exit (-?\d+)\n?)");
    std::smatch match;
    if (std::regex_search(output, match, exit_line)) {
        try {
            summary.exit_code = std::stoi(match[1].str());
        } catch (const std::exception&) {
        }
        output = match.suffix().str();
    }
    summary.output = output;
    summary.truncated = output.find("[Output clipped at 64 KiB.]") != std::string::npos || activity.result.size() >= 8000;

    static const std::regex passed(R"((\d+)\s+(?:tests?\s+)?passed|passed:?\s+(\d+))", std::regex::icase);
    static const std::regex failed_count(R"((\d+)\s+(?:tests?\s+)?failed|failed:?\s+(\d+))", std::regex::icase);
    summary.passed = last_count(output, passed);
    summary.failed = last_count(output, failed_count);

    std::vector<std::string> lines = split(output, '\n');
    // Python's unittest prints "Ran 6 tests in 0.002s" then "OK" or "FAILED (failures=1, errors=1)".
    if (!summary.passed) {
        static const std::regex ran(R"(^Ran (\d+) tests? in )");
        static const std::regex broken(R"(^FAILED \(([^)]*)\))");
        static const std::regex part(R"((failures|errors)=(\d+))");
        std::optional<int> total;
        int bad = 0;
        for (const std::string& line : lines) {
            if (std::regex_search(line, match, ran)) total = std::stoi(match[1].str());
            else if (std::regex_search(line, match, broken)) {
                bad = 0;
                const std::string inside = match[1].str();
                for (auto it = std::sregex_iterator(inside.begin(), inside.end(), part); it != std::sregex_iterator(); ++it) bad += std::stoi((*it)[2].str());
            }
        }
        if (total) {
            summary.passed = *total - bad;
            if (!summary.failed && bad > 0) summary.failed = bad;
        }
    }
    for (const std::string& raw : lines) {
        const std::string line = trim(raw);
        if (line.empty() || !failure_line(line) || summary_line(line)) continue;
        const std::string shown = clip(line, 220);
        if (std::find(summary.failures.begin(), summary.failures.end(), shown) == summary.failures.end()) summary.failures.push_back(shown);
        if (summary.failures.size() == 8) break;
    }
    return summary;
}

bool command_failed(const Activity& activity) {
    if (failed(activity)) return true;
    const std::optional<int> exit = command(activity).exit_code;
    return exit && *exit != 0;
}

std::vector<std::pair<std::string, int>> folders(const std::vector<Activity>& activities) {
    std::map<std::string, int> counts;
    for (const Activity& activity : activities) {
        const std::optional<std::string> path = path_of(activity);
        if (!path) continue;
        const size_t slash = path->find('/');
        counts[slash == std::string::npos ? *path : path->substr(0, slash + 1)]++;
    }
    std::vector<std::pair<std::string, int>> out(counts.begin(), counts.end());
    std::stable_sort(out.begin(), out.end(), [](const auto& a, const auto& b) { return a.second > b.second; });
    return out;
}

size_t request_start(const Session& session) {
    if (session.turns.empty()) return 0;
    return std::min(session.turns.back().first_activity, session.activities.size());
}

}  // namespace mason::scenes
