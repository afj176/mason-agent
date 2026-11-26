// An experiment is a copy of a project in its own Git worktree, started from the project exactly as it stands
// (uncommitted and untracked files included). Work there does not touch the project; a hunk that turns out well is
// applied back by the user. Creating one is the user's own operation: the model cannot choose another workspace.
#pragma once
#include "workspace.hpp"

#include <ctime>

namespace mason {

struct Experiment {
    std::string name;
    std::string parent;  ///< The project it was started from.
    std::string path;    ///< The worktree it lives in.
    std::string branch;
    std::string revision;  ///< The commit that records the project's state when the experiment began.
    std::time_t created = 0;
};

struct ExperimentHunk {
    std::string id, title, patch;
};

namespace experiments {

extern const char* const kUsage;

/// Runs git in `workspace` and returns what it printed, with nothing in the repository's own settings able to start a
/// program. It is not sandboxed: a worktree lives outside the project. `index` names a private index file.
/// `identity` false leaves a commit to carry the user's own name.
std::string git(const Workspace& workspace, const std::vector<std::string>& arguments, const std::string& index = "", bool identity = true);

/// Adds a worktree for a new branch at `destination`, which must be a new folder outside the project. Returns its path.
std::string create_worktree(const Workspace& project, const std::string& destination, const std::string& branch, const std::string& revision = "HEAD");

std::vector<Experiment> load(const fs::path& home);
/// This project's experiments whose worktrees still exist.
std::vector<Experiment> list(const Workspace& workspace, const fs::path& home);

/// Starts an experiment beside the project, in `PROJECT-NAME` on the branch `mason/NAME`.
Experiment fork(const Workspace& workspace, const std::string& name, const fs::path& home);
/// The files the experiment has changed since it began.
std::vector<std::string> changed(const Experiment& experiment);
/// One file's changes, a hunk at a time. A file that was added, deleted or changed mode comes as one piece.
std::vector<ExperimentHunk> hunks(const Experiment& experiment, const std::string& path);
/// Applies one hunk to the project the experiment came from. Git checks the hunk's context first, and the user's
/// index is left alone. The id is refused when the experiment has changed since the hunk was read.
void apply(const Experiment& experiment, const std::string& path, const std::string& id);

/// The /experiment command, shared by the command line and the app. Returns what to tell the user.
std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home);

}  // namespace experiments
}  // namespace mason
