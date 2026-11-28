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
    /// When the workflow runs by itself as a hook: afterPlan, afterEdit, beforeVerify.
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
struct Skill {
    std::string name, description, body;
    std::string source;       ///< Short: project, .claude, .agents or "bundle NAME" (or "computer" for Mason's own folder).
    std::string where;        ///< Where it lives as the user reads it: ".mason/skills/NAME" or "bundle NAME".
    fs::path file;            ///< The SKILL.md itself.
    bool enabled = true;      ///< False when the project's policy switches it off.
    std::string shadowed_by;  ///< For a skill hidden by an earlier one of the same name: that one's `where`.
    bool shadowed() const { return !shadowed_by.empty(); }
    /// "on", "off" or "hidden".
    std::string state() const { return shadowed() ? "hidden" : enabled ? "on" : "off"; }
};

namespace skills {
extern const char* const kUsage;
/// The skill folders of a project, in precedence order.
const std::vector<std::string>& folders();
bool valid_name(const std::string& name);
/// Every skill the project can see, in precedence order: .mason/skills, .claude/skills, .agents/skills, then
/// installed bundles. Shadowed skills are kept and marked, and skills the policy switches off are marked off.
std::vector<Skill> all(const Workspace& workspace, const fs::path& home = data_home());
/// Skills for every chat on this computer: Mason's own `skills` folder, then installed bundles.
std::vector<Skill> computer(const fs::path& home = data_home());
/// The skills the agent may use: not shadowed and not switched off.
std::vector<Definition> list(const Workspace& workspace, const fs::path& home = data_home());
/// What read_skill returns for a skill name from `known`. Refuses one that is off.
std::string read(const std::vector<Skill>& known, const std::string& name);
/// The system prompt's list of enabled skills, or nothing when there are none.
std::string prompt(const std::vector<Skill>& known);
/// The text a new skill starts with.
std::string template_text(const std::string& name, const std::string& description);
/// Makes .mason/skills/NAME/SKILL.md and returns its full path. Refuses an invalid name or one already there.
fs::path create(const Workspace& workspace, const std::string& name, const std::string& description);
/// The request /skill NAME REQUEST sends: the skill's body in front of the request.
std::string request(const std::vector<Skill>& known, const std::string& name, const std::string& request);
/// /skills, /skills show NAME, /skills on NAME and /skills off NAME. /skills new is the caller's, as it may open an editor.
std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home);
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
