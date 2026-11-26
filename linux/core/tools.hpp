// Tools beyond the basic file and shell ones: project workflows, skills, and background jobs.
#pragma once
#include <map>
#include <mutex>
#include <thread>

#include "harness.hpp"
#include "shell.hpp"

namespace mason {

/// Named commands a project defines in `mason.workflows.json`, such as its tests or its linter:
/// {"version": 1, "workflows": {"test": {"executable": "python3", "arguments": ["-m", "unittest"], "directory": "."}}}
struct ProjectWorkflow {
    std::string executable;
    std::vector<std::string> arguments;
    std::string directory = ".";
    /// When the workflow runs by itself as a hook: afterEdit, beforeVerify. (afterPlan is accepted and not used here.)
    std::vector<std::string> events;
};

namespace workflows {
/// The project's workflows by name. Throws Error when the file is present but not valid.
std::map<std::string, ProjectWorkflow> load(const Workspace& workspace);
/// The shell command that runs a workflow from the project folder.
std::string command_line(const ProjectWorkflow& workflow);
}  // namespace workflows

/// Instructions a project keeps for particular kinds of work, each in `.mason/skills/<name>/SKILL.md`
/// (also `.claude/skills` and `.agents/skills`). The agent sees their names and reads one when it needs it.
namespace skills {
/// The project's skills, then those of installed bundles. A project skill wins over a bundle's of the same name.
std::vector<Definition> list(const Workspace& workspace, const fs::path& home = data_home());
}

/// An outline of a project for the model: each source file with its size and the top-level things it defines.
namespace repository_map {
std::string outline(const Workspace& workspace, const std::string& under = ".");
}

/// Programs the agent starts and leaves running during one request: a dev server, a watcher, a long test run.
/// Every job is stopped when the request ends.
class Jobs {
public:
    static constexpr size_t kMaxRunning = 3, kMaxTotal = 24;
    static constexpr double kTimeoutSeconds = 30 * 60;
    ~Jobs() { stop_all(); }

    /// `interactive` gives the program a standard input that `send` writes lines to.
    std::string start(const std::string& command, const Workspace& workspace, bool sandboxed, bool interactive = false);
    /// Writes one line to a terminal's standard input, and closes the input when `eof` is set.
    std::string send(const std::string& id, const std::string& text, bool eof);
    /// The job's state and the end of its output so far. `stop` ends it first; otherwise waits up to `wait_ms` for it to finish.
    std::string observe(const std::string& id, bool stop, int wait_ms, const CancelToken& cancel = nullptr);
    void stop_all();
    bool empty();

private:
    struct Job {
        std::string command, output, failure;
        std::optional<CommandResult> result;
        CancelToken cancel = make_cancel();
        std::thread thread;
        bool interactive = false;
        int input_fd = -1;  // the writing end of the program's standard input, while it is open
        bool done() const { return result.has_value() || !failure.empty(); }
    };
    std::mutex lock_;
    std::map<std::string, std::shared_ptr<Job>> jobs_;
};

}  // namespace mason
