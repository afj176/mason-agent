// A background task is one request run by a separate `mason` process that keeps going after the app or the terminal
// that started it has closed. The worker is the only writer of its own record; anyone else reads the record, answers
// the approval it is waiting on, or asks it to stop, through files in the task's folder.
#pragma once
#include "model.hpp"
#include "workspace.hpp"

#include <ctime>

namespace mason {

struct BackgroundTask {
    std::string id, workspace, prompt;
    ModelEndpoint endpoint;
    bool automatic = false;  ///< Auto: edits and sandboxed commands without asking. Otherwise each one waits for an answer.
    std::time_t created = 0;
};

struct TaskApproval {
    std::string id, tool, title, detail, subject;
};

struct TaskStatus {
    std::string state, detail;
    int pid = 0;
    std::time_t started = 0;
    std::string session;
    std::optional<TaskApproval> approval;
    bool active() const { return state == "Starting" || state == "Running" || state == "Waiting for approval"; }
};

namespace background {

extern const char* const kUsage;

/// The path of the running program, which is what a worker is started from.
std::string own_program();
/// Records the task and starts its worker, `program task-run --id ID`, detached from this process. Returns the id.
std::string start(const std::string& program, const Workspace& workspace, const std::string& prompt, const ModelEndpoint& endpoint, bool automatic, const fs::path& home);
/// A task's state. A worker that has gone without finishing is reported as such.
TaskStatus status(const std::string& id, const fs::path& home);
std::optional<BackgroundTask> task(const std::string& id, const fs::path& home);
/// Every task, newest first; only one project's when `workspace` is given.
std::vector<std::pair<BackgroundTask, TaskStatus>> all(const Workspace* workspace, const fs::path& home);
/// The end of what the task has done so far, one JSON object per line.
std::vector<std::string> tail(const std::string& id, size_t lines, const fs::path& home);
/// Answers the approval the task is waiting on. `request` must be that approval's id, so an answer meant for an
/// earlier question cannot allow a later one.
void decide(const std::string& id, const std::string& request, bool allow, const fs::path& home);
void cancel(const std::string& id, const fs::path& home);

/// Runs the task to its end. This is what `mason task-run` does. `model` replaces the endpoint in tests.
int work(const std::string& id, const fs::path& home, ChatModel* model = nullptr);
/// The worker leaves the terminal or app that started it: its own session, and no hang-up when that one closes.
void detach();

/// The /task command, shared by the command line and the app. Returns what to tell the user.
std::string command(const std::string& rest, const Workspace& workspace, const std::optional<ModelEndpoint>& endpoint, bool automatic, const std::string& program, const fs::path& home);

}  // namespace background
}  // namespace mason
