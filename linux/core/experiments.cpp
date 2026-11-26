#include "experiments.hpp"

#include "shell.hpp"

#include <regex>

namespace mason::experiments {

namespace {

// Nothing in the repository's own settings may start a program, and paths are never read as patterns.
const char* const kOptions = "-c core.hooksPath=/dev/null -c core.fsmonitor= -c core.quotepath=false -c commit.gpgsign=false -c user.name='Mason Experiment' -c user.email=mason@localhost --no-optional-locks --literal-pathspecs";

struct Removed {
    fs::path path;
    ~Removed() { std::error_code ignored; fs::remove(path, ignored); fs::remove(path.string() + ".lock", ignored); }
};

/// Runs git in `workspace` and returns what it printed. This is not sandboxed: a worktree lives outside the project.
std::string git(const Workspace& workspace, const std::vector<std::string>& arguments, const std::string& index = "") {
    const Removed printed{fs::temp_directory_path() / ("mason-git-" + new_id())};
    std::string command = (index.empty() ? "" : "GIT_INDEX_FILE=" + shell::quote(index) + " ") + "git " + kOptions;
    for (const std::string& argument : arguments) command += " " + shell::quote(argument);
    command += " > " + shell::quote(printed.path.string());
    const CommandResult result = shell::run(command, workspace, false, 90);
    if (result.exit_code != 0 || result.timed_out) throw Error("git " + (arguments.empty() ? std::string() : arguments[0]) + " failed: " + trim(result.output));
    return fs::exists(printed.path) ? read_file(printed.path) : "";
}

/// The tree of everything in the checkout now, built in a private index so the user's own index is untouched.
std::string snapshot(const Workspace& workspace) {
    const Removed index{fs::temp_directory_path() / ("mason-index-" + new_id())};
    git(workspace, {"read-tree", "HEAD"}, index.path.string());
    git(workspace, {"add", "--all", "--", "."}, index.path.string());
    return trim(git(workspace, {"write-tree"}, index.path.string()));
}

std::string real(const std::string& path) {
    std::error_code ignored;
    const fs::path resolved = fs::weakly_canonical(path, ignored);
    return resolved.empty() ? path : resolved.string();
}

/// A short name for a patch's exact text (FNV-1a), so a hunk that has changed is not applied by an old name.
std::string fingerprint(const std::string& text) {
    uint64_t hash = 0xcbf29ce484222325ULL;
    for (unsigned char byte : text) { hash ^= byte; hash *= 0x00000100000001b3ULL; }
    static const char* digits = "0123456789abcdef";
    std::string name;
    do { name.insert(name.begin(), digits[hash & 15]); hash >>= 4; } while (hash != 0);
    return name.size() > 8 ? name.substr(name.size() - 8) : name;
}

void save(const std::vector<Experiment>& all, const fs::path& home) {
    json list = json::array();
    for (const Experiment& item : all) {
        list.push_back({{"name", item.name}, {"parent", item.parent}, {"path", item.path}, {"branch", item.branch}, {"revision", item.revision}, {"created", static_cast<long long>(item.created)}});
    }
    fs::create_directories(home);
    write_file_atomic(home / "experiments.json", list.dump(2));
}

bool same(const Experiment& a, const Experiment& b) { return a.parent == b.parent && a.name == b.name && a.path == b.path; }

}  // namespace

const char* const kUsage =
    "/experiment                       List this project's experiments and what each has changed\n"
    "/experiment new NAME              Copy the project as it stands into a worktree beside it (PROJECT-NAME, branch mason/NAME)\n"
    "/experiment diff NAME [PATH]      The files an experiment changed, or one file's hunks with their ids\n"
    "/experiment apply NAME PATH ID    Apply one hunk to this project (or \"all\" for every hunk of the file)\n"
    "/experiment forget NAME           Drop the record; the worktree and branch are left for you to remove";

std::string create_worktree(const Workspace& project, const std::string& destination, const std::string& branch, const std::string& revision) {
    if (revision != "HEAD" && !std::regex_match(revision, std::regex("(?:[0-9a-f]{40}|[0-9a-f]{64})"))) throw Error("Invalid base revision.");
    if (!std::regex_match(branch, std::regex(R"(\w[\w/-]{0,100})")) || branch.find("..") != std::string::npos || branch.find("//") != std::string::npos || branch.back() == '/') {
        throw Error("Use a new Git branch name.");
    }
    const fs::path target = fs::absolute(destination).lexically_normal();
    if (!fs::is_directory(target.parent_path())) throw Error("The folder that would hold the worktree does not exist.");
    const std::string place = real(target.parent_path().string()) + "/" + target.filename().string(), root = real(project.root());
    if (fs::exists(place) || place == root || (place + "/").rfind(root + "/", 0) == 0 || (root + "/").rfind(place + "/", 0) == 0) {
        throw Error("Choose a new worktree path outside the project.");
    }
    git(project, {"worktree", "add", "-b", branch, "--", place, revision});
    return real(place);
}

std::vector<Experiment> load(const fs::path& home) {
    std::vector<Experiment> all;
    if (!fs::exists(home / "experiments.json")) return all;
    const json parsed = json::parse(read_file(home / "experiments.json"), nullptr, false);
    if (!parsed.is_array()) return all;
    for (const json& item : parsed) {
        if (!item.is_object()) continue;
        Experiment experiment;
        experiment.name = item.value("name", "");
        experiment.parent = item.value("parent", "");
        experiment.path = item.value("path", "");
        experiment.branch = item.value("branch", "");
        experiment.revision = item.value("revision", "");
        experiment.created = static_cast<std::time_t>(item.value("created", 0LL));
        if (!experiment.name.empty() && !experiment.path.empty()) all.push_back(experiment);
    }
    return all;
}

std::vector<Experiment> list(const Workspace& workspace, const fs::path& home) {
    const std::string root = real(workspace.root());
    std::vector<Experiment> mine;
    for (const Experiment& item : load(home)) {
        if (item.parent == root && fs::exists(item.path)) mine.push_back(item);
    }
    return mine;
}

Experiment fork(const Workspace& workspace, const std::string& name, const fs::path& home) {
    if (!std::regex_match(name, std::regex("[A-Za-z0-9][A-Za-z0-9_-]{0,40}"))) throw Error("Name the experiment with letters, digits, - or _ (up to 41 characters).");
    const std::string root = real(workspace.root());
    std::vector<Experiment> all = load(home);
    for (const Experiment& item : all) {
        if (item.parent == root && to_lower(item.name) == to_lower(name)) throw Error("This project already has an experiment named " + name + ".");
    }
    if (!fs::exists(fs::path(root) / ".git")) throw Error("Experiments need a Git repository, and this project is not one.");
    const std::string head = trim(git(workspace, {"rev-parse", "HEAD"}));
    const std::string tree = snapshot(workspace);
    if (snapshot(workspace) != tree || trim(git(workspace, {"rev-parse", "HEAD"})) != head) throw Error("The project changed while the experiment was being captured. Try again.");
    Experiment experiment;
    experiment.name = name;
    experiment.parent = root;
    experiment.branch = "mason/" + name;
    experiment.revision = trim(git(workspace, {"commit-tree", tree, "-p", head, "-m", "Mason experiment starting state"}));
    experiment.path = create_worktree(workspace, (fs::path(root).parent_path() / (fs::path(root).filename().string() + "-" + name)).string(), experiment.branch, experiment.revision);
    experiment.created = std::time(nullptr);
    all.push_back(experiment);
    save(all, home);
    return experiment;
}

std::vector<std::string> changed(const Experiment& experiment) {
    const Workspace fork(experiment.path);
    const std::string tree = snapshot(fork);
    return split(git(fork, {"diff", "--no-renames", "--name-only", experiment.revision, tree, "--"}), '\n', false);
}

std::vector<ExperimentHunk> hunks(const Experiment& experiment, const std::string& path) {
    const Workspace fork(experiment.path);
    bool outside = !path.empty() && path[0] == '/';
    for (const std::string& part : split(path, '/')) outside = outside || part == "..";
    if (outside || path.empty()) throw Error("Name a file inside the experiment.");
    const std::string tree = snapshot(fork);
    const std::string diff = git(fork, {"diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--binary", experiment.revision, tree, "--", path});
    std::vector<ExperimentHunk> found;
    if (diff.empty()) return found;
    // Where each hunk begins: a line starting with "@@ ".
    std::vector<size_t> starts;
    for (size_t at = 0; at < diff.size();) {
        if (diff.compare(at, 3, "@@ ") == 0) starts.push_back(at);
        const size_t next = diff.find('\n', at);
        if (next == std::string::npos) break;
        at = next + 1;
    }
    if (starts.empty() || diff.find("\nnew file mode ") != std::string::npos || diff.find("\ndeleted file mode ") != std::string::npos || diff.find("\nold mode ") != std::string::npos) {
        found.push_back({fingerprint(diff), "Whole file · " + path, diff});
        return found;
    }
    const std::string header = diff.substr(0, starts[0]);
    for (size_t i = 0; i < starts.size(); i++) {
        const size_t end = i + 1 < starts.size() ? starts[i + 1] : diff.size();
        const std::string patch = header + diff.substr(starts[i], end - starts[i]);
        const size_t line_end = diff.find('\n', starts[i]);
        found.push_back({fingerprint(patch), diff.substr(starts[i], (line_end == std::string::npos ? diff.size() : line_end) - starts[i]), patch});
    }
    return found;
}

void apply(const Experiment& experiment, const std::string& path, const std::string& id) {
    std::optional<ExperimentHunk> chosen;
    for (const ExperimentHunk& hunk : hunks(experiment, path)) {
        if (hunk.id == id) chosen = hunk;
    }
    if (!chosen) throw Error("The experiment changed, or there is no hunk " + id + " in " + path + ". Read the diff again.");
    const Removed patch{fs::temp_directory_path() / ("mason-patch-" + new_id())};
    write_file_atomic(patch.path, chosen->patch);
    const Workspace parent(experiment.parent);
    try {
        git(parent, {"apply", "--check", "--whitespace=nowarn", patch.path.string()});
    } catch (const Error& error) {
        throw Error(std::string("The hunk does not fit the project as it is now, so nothing was changed. ") + error.what());
    }
    git(parent, {"apply", "--whitespace=nowarn", patch.path.string()});
}

std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    const std::vector<Experiment> all = list(workspace, home);
    auto named = [&](size_t at) -> Experiment {
        const std::string name = at < words.size() ? words[at] : "";
        for (const Experiment& item : all) {
            if (to_lower(item.name) == to_lower(name)) return item;
        }
        throw Error("No experiment named " + name + " in this project. /experiment lists them.");
    };
    auto joined = [&](size_t from, size_t to) {
        std::string text;
        for (size_t i = from; i < to; i++) text += (i > from ? " " : "") + words[i];
        return text;
    };
    const std::string verb = words.empty() ? "list" : to_lower(words[0]);
    if (verb == "list") {
        if (all.empty()) return "No experiments. /experiment new NAME copies this project, as it stands, into a worktree beside it.";
        std::string text;
        for (const Experiment& item : all) {
            std::vector<std::string> files;
            try { files = changed(item); } catch (const std::exception&) {}
            const std::vector<std::string> shown(files.begin(), files.begin() + static_cast<std::ptrdiff_t>(std::min<size_t>(8, files.size())));
            text += (text.empty() ? "" : "\n") + item.name + "  ·  " + item.path + "  ·  " + std::to_string(files.size()) + (files.size() == 1 ? " file changed" : " files changed") +
                    (files.empty() ? "" : ": " + join(shown, ", ") + (files.size() > 8 ? ", …" : ""));
        }
        return text;
    }
    if (verb == "new") {
        if (words.size() != 2) throw Error("Use /experiment new NAME.");
        const Experiment made = fork(workspace, words[1], home);
        return "Started the experiment " + made.name + " in " + made.path + " on the branch " + made.branch + ", from this project as it stands.\nWork on it with: mason --workspace " + shell::quote(made.path);
    }
    if (verb == "diff") {
        const Experiment experiment = named(1);
        if (words.size() == 2) {
            const std::vector<std::string> files = changed(experiment);
            return files.empty() ? experiment.name + " has changed nothing yet." : experiment.name + " changed:\n" + join(files, "\n") + "\n/experiment diff " + experiment.name + " PATH shows a file's hunks.";
        }
        const std::string path = joined(2, words.size());
        const std::vector<ExperimentHunk> found = hunks(experiment, path);
        if (found.empty()) return experiment.name + " has not changed " + path + ".";
        std::string text;
        for (const ExperimentHunk& hunk : found) text += "hunk " + hunk.id + "  " + hunk.title + "\n" + (hunk.patch.size() > 6000 ? utf8_prefix(hunk.patch, 6000) : hunk.patch) + "\n";
        return text + "Apply one with /experiment apply " + experiment.name + " " + path + " ID.";
    }
    if (verb == "apply") {
        if (words.size() < 4) throw Error("Use /experiment apply NAME PATH ID.");
        const Experiment experiment = named(1);
        const std::string path = joined(2, words.size() - 1), id = words.back();
        if (to_lower(id) == "all") {
            const std::vector<ExperimentHunk> found = hunks(experiment, path);
            if (found.empty()) throw Error(experiment.name + " has not changed " + path + ".");
            for (const ExperimentHunk& hunk : found) apply(experiment, path, hunk.id);
            return "Applied " + std::to_string(found.size()) + (found.size() == 1 ? " hunk of " : " hunks of ") + path + " from " + experiment.name + " to this project.";
        }
        apply(experiment, path, id);
        return "Applied hunk " + id + " of " + path + " from " + experiment.name + " to this project.";
    }
    if (verb == "forget") {
        const Experiment experiment = named(1);
        std::vector<Experiment> kept;
        for (const Experiment& item : load(home)) {
            if (!same(item, experiment)) kept.push_back(item);
        }
        save(kept, home);
        return "Forgot " + experiment.name + ". Its worktree is still at " + experiment.path + "; remove it with: git worktree remove " + shell::quote(experiment.path);
    }
    return kUsage;
}

}  // namespace mason::experiments
