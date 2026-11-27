#include "delivery.hpp"

#include "shell.hpp"

#include <algorithm>
#include <cctype>
#include <regex>

namespace mason::delivery {

namespace {

void check_branch(const std::string& name) {
    const bool lock = name.size() >= 5 && name.compare(name.size() - 5, 5, ".lock") == 0;
    if (!std::regex_match(name, std::regex(R"([A-Za-z0-9][A-Za-z0-9_./-]{0,99})")) || name.find("..") != std::string::npos || name.find("//") != std::string::npos || name.back() == '/' || lock) {
        throw Error("Use the target branch name, such as main.");
    }
}

bool is_commit(const std::string& value) { return std::regex_match(value, std::regex("(?:[0-9a-f]{40}|[0-9a-f]{64})")); }

std::string real(const std::string& path) {
    std::error_code ignored;
    const fs::path resolved = fs::weakly_canonical(path, ignored);
    return resolved.empty() ? path : resolved.string();
}

struct Removed {
    fs::path path;
    ~Removed() { std::error_code ignored; fs::remove(path, ignored); fs::remove(path.string() + ".lock", ignored); }
};

std::string gh(const Workspace& workspace, const std::vector<std::string>& arguments) {
    const Removed printed{fs::temp_directory_path() / ("mason-gh-" + new_id())};
    std::string command = "gh";
    for (const std::string& argument : arguments) command += " " + shell::quote(argument);
    command += " > " + shell::quote(printed.path.string());
    const CommandResult result = shell::run(command, workspace, false, 60);
    if (result.exit_code == 127) throw Error("The GitHub command line (gh) is not installed. Install it and sign in with: gh auth login");
    if (result.exit_code != 0 || result.timed_out) throw Error("gh " + (arguments.empty() ? std::string() : arguments[0]) + " failed: " + trim(result.output));
    return fs::exists(printed.path) ? trim(read_file(printed.path)) : "";
}

std::vector<DeliveryDraft> load(const fs::path& home) {
    std::vector<DeliveryDraft> all;
    if (!fs::exists(home / "deliveries.json")) return all;
    const json parsed = json::parse(read_file(home / "deliveries.json"), nullptr, false);
    if (!parsed.is_array()) return all;
    for (const json& item : parsed) {
        if (!item.is_object()) continue;
        DeliveryDraft draft;
        draft.id = item.value("id", "");
        draft.workspace = item.value("workspace", "");
        draft.base = item.value("base", "");
        draft.base_commit = item.value("baseCommit", "");
        draft.commit = item.value("commit", "");
        draft.title = item.value("title", "");
        draft.body = item.value("body", "");
        draft.repository = item.value("repository", "");
        draft.branch = item.value("branch", "");
        draft.url = item.value("url", "");
        if (item.contains("paths") && item["paths"].is_array()) {
            for (const json& path : item["paths"]) {
                if (path.is_string()) draft.paths.push_back(path.get<std::string>());
            }
        }
        if (!draft.id.empty()) all.push_back(draft);
    }
    return all;
}

void save(const std::vector<DeliveryDraft>& all, const fs::path& home) {
    json list = json::array();
    for (const DeliveryDraft& draft : all) {
        json item = {{"id", draft.id}, {"workspace", draft.workspace}, {"base", draft.base}, {"baseCommit", draft.base_commit}, {"commit", draft.commit}, {"title", draft.title},
                     {"body", draft.body}, {"repository", draft.repository}, {"branch", draft.branch}, {"paths", draft.paths}};
        if (!draft.url.empty()) item["url"] = draft.url;
        list.push_back(item);
    }
    fs::create_directories(home);
    write_file_atomic(home / "deliveries.json", list.dump(2));
}

std::string field(const json& object, const char* name) {
    if (!object.is_object() || !object.contains(name)) return "";
    const json& value = object[name];
    if (value.is_string()) return value.get<std::string>();
    if (value.is_number_integer()) return std::to_string(value.get<long long>());
    return "";
}

std::string described(const DeliveryDraft& draft) {
    const std::vector<std::string> shown(draft.paths.begin(), draft.paths.begin() + static_cast<std::ptrdiff_t>(std::min<size_t>(6, draft.paths.size())));
    std::string text = "\"" + draft.title + "\" onto " + draft.base + ": commit " + draft.commit.substr(0, 12) + ", " + std::to_string(draft.paths.size()) + (draft.paths.size() == 1 ? " file (" : " files (") +
                       join(shown, ", ") + (draft.paths.size() > 6 ? ", …" : "") + ")";
    if (!draft.url.empty()) return text + "\nPublished: " + draft.url;
    return text + "\nNot published. /deliver publish pushes it to " + (draft.repository.empty() ? std::string("GitHub (this checkout has no GitHub origin yet)") : draft.repository) + " as " + draft.branch + " and opens a draft pull request.";
}

}  // namespace

const char* const kUsage =
    "/deliver review BASE                 What this checkout has that BASE does not, a hunk at a time\n"
    "/deliver draft BASE HUNKS TITLE      Build one commit from hunks (ids separated by commas, or \"all\") on top of BASE\n"
    "/deliver                             The current draft\n"
    "/deliver publish                     Push the draft's commit to a new branch and open a draft pull request (needs gh)\n"
    "/deliver feedback                    Failed checks, requested changes and review comments on that pull request\n"
    "/deliver feedback send               The same, sent to the model as your next request";

std::string repository(const std::string& remote) {
    std::smatch match;
    const std::string text = trim(remote);
    if (!std::regex_match(text, match, std::regex(R"((?:https://github\.com/|git@github\.com:)([\w.-]+/[\w.-]+?)(?:\.git)?/?)", std::regex::icase))) throw Error("Choose a checkout with a GitHub origin.");
    for (const std::string& part : split(match[1].str(), '/')) {
        if (part == "." || part == "..") throw Error("Choose a checkout with a GitHub origin.");
    }
    return match[1].str();
}

DeliveryReview review(const Workspace& workspace, const std::string& base) {
    check_branch(base);
    DeliveryReview reviewed;
    for (const std::string& ref : {"refs/remotes/origin/" + base, "refs/heads/" + base}) {
        if (!reviewed.revision.empty()) break;
        try {
            reviewed.revision = trim(experiments::git(workspace, {"rev-parse", "--verify", ref + "^{commit}"}));
        } catch (const Error&) {
        }
    }
    if (!is_commit(reviewed.revision)) throw Error("There is no branch " + base + " here, as origin/" + base + " or locally.");
    Experiment against;
    against.parent = against.path = real(workspace.root());
    against.revision = reviewed.revision;
    size_t bytes = 0;
    for (const std::string& path : experiments::changed(against)) {
        for (const ExperimentHunk& hunk : experiments::hunks(against, path)) {
            bytes += hunk.patch.size();
            reviewed.patches.push_back({path, hunk});
        }
        if (reviewed.patches.size() > 128 || bytes > 1500000) throw Error("That is more than 128 hunks or 1.5 MB. Narrow the delivery to a smaller change.");
    }
    return reviewed;
}

DeliveryDraft draft(const Workspace& workspace, const std::string& base, const std::string& pinned, const std::string& given, const std::set<std::string>& selected, const fs::path& home, const std::string& receipt) {
    const std::string title = trim(given);
    if (title.empty() || utf8_length(title) > 120 || title.find('\n') != std::string::npos || selected.empty() || selected.size() > 128) {
        throw Error("Choose reviewed hunks and a pull request title of up to 120 characters.");
    }
    const DeliveryReview reviewed = review(workspace, base);
    if (reviewed.revision != pinned) throw Error(base + " moved since you reviewed. Review again.");
    std::vector<ReviewedPatch> patches;
    for (const ReviewedPatch& patch : reviewed.patches) {
        if (selected.count(patch.hunk.id)) patches.push_back(patch);
    }
    if (patches.size() != selected.size()) throw Error("A chosen hunk changed. Review it again.");
    const Removed index{fs::temp_directory_path() / ("mason-delivery-" + new_id())};
    const Removed file{fs::temp_directory_path() / ("mason-patch-" + new_id())};
    experiments::git(workspace, {"read-tree", pinned}, index.path.string());
    // Hunks of one file share its header, so the file's later hunks are added without theirs.
    std::string combined;
    DeliveryDraft made;
    for (const ReviewedPatch& patch : patches) {
        if (!made.paths.empty() && made.paths.back() == patch.path) {
            const size_t at = patch.hunk.patch.find("\n@@ ");
            if (at == std::string::npos) throw Error("A hunk of " + patch.path + " could not be combined with the others.");
            combined += patch.hunk.patch.substr(at + 1);
        } else {
            combined += patch.hunk.patch;
            made.paths.push_back(patch.path);
        }
    }
    write_file_atomic(file.path, combined);
    experiments::git(workspace, {"apply", "--cached", "--whitespace=nowarn", file.path.string()}, index.path.string());
    const std::string tree = trim(experiments::git(workspace, {"write-tree"}, index.path.string()));
    // The commit carries the user's own name, as any commit of theirs would.
    made.commit = trim(experiments::git(workspace, {"commit-tree", tree, "-p", pinned, "-m", title}, "", false));
    try {
        made.repository = repository(experiments::git(workspace, {"remote", "get-url", "origin"}));
    } catch (const Error&) {
    }
    made.id = new_id();
    made.id.erase(std::remove(made.id.begin(), made.id.end(), '-'), made.id.end());
    made.workspace = real(workspace.root());
    made.base = base;
    made.base_commit = pinned;
    made.title = title;
    made.branch = "mason/review-" + made.id.substr(0, 12);
    made.body = "## Changes\n\n";
    for (const std::string& path : made.paths) made.body += "- " + path + "\n";
    made.body += "\n## Verification\n\n";
    if (patches.size() != reviewed.patches.size()) made.body += "This is a selection of the checkout's changes, which was not verified on its own.";
    else if (!receipt.empty()) made.body += "This is every change in the checkout against " + base + ". Mason's own checks on the latest request found:\n\n" + (utf8_length(receipt) > 12000 ? utf8_prefix(receipt, 12000) : receipt);
    else made.body += "This is every change in the checkout against " + base + ". No verification receipt is attached.";
    std::vector<DeliveryDraft> kept;
    for (const DeliveryDraft& other : load(home)) {
        if (other.workspace != made.workspace) kept.push_back(other);
    }
    kept.push_back(made);
    save(kept, home);
    return made;
}

std::string publish(const Workspace& workspace, const DeliveryDraft& draft, const fs::path& home) {
    check_branch(draft.base);
    check_branch(draft.branch);
    const std::string origin = repository(experiments::git(workspace, {"remote", "get-url", "origin"}));
    if (!is_commit(draft.commit) || !is_commit(draft.base_commit) || draft.repository.empty() || draft.repository != origin) throw Error("The checkout's GitHub repository is not the one this draft was made for.");
    std::string escaped;
    for (unsigned char c : draft.base) {
        static const char* digits = "0123456789ABCDEF";
        if (std::isalnum(c)) escaped += static_cast<char>(c);
        else { escaped += '%'; escaped += digits[c >> 4]; escaped += digits[c & 15]; }
    }
    if (gh(workspace, {"api", "repos/" + draft.repository + "/commits/" + escaped, "--jq", ".sha"}) != draft.base_commit) throw Error(draft.base + " on GitHub has moved. Fetch it and review again.");
    // No force: a retry can only publish the same commit again.
    experiments::git(workspace, {"push", "https://github.com/" + draft.repository + ".git", draft.commit + ":refs/heads/" + draft.branch});
    std::string url;
    const json earlier = json::parse(gh(workspace, {"pr", "list", "--repo", draft.repository, "--head", draft.branch, "--base", draft.base, "--state", "all", "--json", "url,headRefOid"}), nullptr, false);
    if (earlier.is_array() && !earlier.empty()) {
        if (field(earlier[0], "headRefOid") != draft.commit) throw Error("A pull request already uses this branch with other content.");
        url = field(earlier[0], "url");
    }
    if (url.empty()) url = gh(workspace, {"pr", "create", "--draft", "--repo", draft.repository, "--base", draft.base, "--head", draft.branch, "--title", draft.title, "--body", draft.body});
    std::vector<DeliveryDraft> all = load(home);
    for (DeliveryDraft& other : all) {
        if (other.id == draft.id) other.url = url;
    }
    save(all, home);
    return url;
}

DeliveryFeedback feedback(const Workspace& workspace, const DeliveryDraft& draft) {
    const std::string prefix = "https://github.com/" + draft.repository + "/pull/";
    const std::string number = draft.url.size() > prefix.size() && to_lower(draft.url.substr(0, prefix.size())) == to_lower(prefix) ? draft.url.substr(prefix.size()) : "";
    if (draft.repository.empty() || number.empty() || number.find_first_not_of("0123456789") != std::string::npos) throw Error("Publish this draft first.");
    const std::string state = gh(workspace, {"pr", "view", number, "--repo", draft.repository, "--json", "url,headRefOid,statusCheckRollup,reviews"});
    const std::string comments = gh(workspace, {"api", "repos/" + draft.repository + "/pulls/" + number + "/comments?per_page=100"});
    return parse_feedback(state, comments);
}

DeliveryFeedback parse_feedback(const std::string& state, const std::string& comments) {
    const json root = json::parse(state, nullptr, false);
    if (!root.is_object()) throw Error("GitHub's answer about the pull request could not be read.");
    const json notes = json::parse(comments, nullptr, false);
    DeliveryFeedback found;
    auto add = [&](const std::string& text) {
        if (!trim(text).empty() && found.observations.size() < 64) found.observations.push_back(utf8_length(text) > 1200 ? utf8_prefix(text, 1200) : text);
    };
    if (root.contains("statusCheckRollup") && root["statusCheckRollup"].is_array()) {
        for (const json& check : root["statusCheckRollup"]) {
            const std::string outcome = field(check, "conclusion").empty() ? field(check, "state") : field(check, "conclusion");
            if (outcome == "FAILURE" || outcome == "ERROR" || outcome == "TIMED_OUT" || outcome == "CANCELLED" || outcome == "ACTION_REQUIRED") {
                add("CI · " + field(check, "name") + field(check, "context") + " · " + outcome + " · " + field(check, "detailsUrl"));
            }
        }
    }
    if (root.contains("reviews") && root["reviews"].is_array()) {
        for (const json& review : root["reviews"]) {
            if (field(review, "state") == "CHANGES_REQUESTED") add("Review · " + field(review, "body"));
        }
    }
    if (notes.is_array()) {
        for (const json& note : notes) add(field(note, "path") + ":" + field(note, "line") + " · " + field(note, "body"));
    }
    found.head = field(root, "headRefOid");
    found.url = field(root, "url");
    return found;
}

std::optional<DeliveryDraft> current(const Workspace& workspace, const fs::path& home) {
    const std::string root = real(workspace.root());
    std::optional<DeliveryDraft> found;
    for (const DeliveryDraft& draft : load(home)) {
        if (draft.workspace == root) found = draft;
    }
    return found;
}

Said command(const std::string& rest, const Workspace& workspace, const fs::path& home, const std::string& receipt) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    const std::string verb = words.empty() ? "" : to_lower(words[0]);
    if (verb.empty()) {
        const auto draft = current(workspace, home);
        return {draft ? described(*draft) : "No draft. /deliver review BASE shows what this checkout would deliver.", ""};
    }
    if (verb == "review") {
        if (words.size() != 2) throw Error("Use /deliver review BASE, for example /deliver review main.");
        const DeliveryReview reviewed = review(workspace, words[1]);
        if (reviewed.patches.empty()) return {"This checkout has nothing that " + words[1] + " does not.", ""};
        std::string text = std::to_string(reviewed.patches.size()) + (reviewed.patches.size() == 1 ? " hunk against " : " hunks against ") + words[1] + " at " + reviewed.revision.substr(0, 12) + ":\n";
        for (const ReviewedPatch& patch : reviewed.patches) text += patch.hunk.id + "  " + patch.path + "  " + patch.hunk.title + "\n";
        return {text + "Next: /deliver draft " + words[1] + " all TITLE takes them all, or list ids separated by commas.", ""};
    }
    if (verb == "draft") {
        if (words.size() < 4) throw Error("Use /deliver draft BASE HUNKS TITLE, for example /deliver draft main all Fix the cart total.");
        const DeliveryReview reviewed = review(workspace, words[1]);
        std::set<std::string> chosen;
        if (to_lower(words[2]) == "all") {
            for (const ReviewedPatch& patch : reviewed.patches) chosen.insert(patch.hunk.id);
        } else {
            for (const std::string& id : split(words[2], ',', false)) chosen.insert(id);
        }
        std::string title;
        for (size_t i = 3; i < words.size(); i++) title += (i > 3 ? " " : "") + words[i];
        return {"Drafted " + described(draft(workspace, words[1], reviewed.revision, title, chosen, home, receipt)), ""};
    }
    if (verb == "publish" || verb == "feedback") {
        const auto made = current(workspace, home);
        if (!made) throw Error("No draft. Make one with /deliver draft.");
        if (verb == "publish") return {"Published as a draft pull request: " + publish(workspace, *made, home), ""};
        const DeliveryFeedback found = feedback(workspace, *made);
        if (found.observations.empty()) return {"Nothing to act on at " + found.url + ": no failed checks, requested changes or review comments.", ""};
        const std::string text = std::to_string(found.observations.size()) + (found.observations.size() == 1 ? " thing" : " things") + " to act on at " + found.url + ":\n" + join(found.observations, "\n");
        std::string request;
        if (words.size() > 1 && to_lower(words[1]) == "send") {
            request = "The pull request " + found.url + " has this feedback. Address each item, and say which you could not:";
            for (const std::string& observation : found.observations) request += "\n- " + observation;
        }
        return {text, request};
    }
    return {kUsage, ""};
}

std::optional<std::string> pull_request(const std::optional<DeliveryDraft>& draft) {
    if (!draft || trim(draft->url).empty()) return std::nullopt;
    return trim(draft->url);
}

}  // namespace mason::delivery
