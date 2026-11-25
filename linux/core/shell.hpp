// Running shell commands for the agent, optionally inside a sandbox.
#pragma once
#include "workspace.hpp"

namespace mason {

struct CommandResult {
    int exit_code = 0;
    std::string output;
    bool truncated = false;
    bool timed_out = false;
};

namespace shell {

constexpr size_t kOutputLimit = 64 * 1024;

/// The shell commands run in: bash, else sh.
std::string path();
/// Path of bubblewrap (bwrap), which provides the Auto sandbox, or empty when it is missing or cannot create a sandbox here.
std::string sandbox_tool();
bool sandbox_available();
/// The bubblewrap arguments for Auto: the whole system read-only, the project and a private /tmp writable,
/// credential folders hidden, no network, and everything stopped when Mason exits.
std::vector<std::string> sandbox_arguments(const std::string& workspace);

/// Runs one shell command in the project folder and returns its combined output. The command gets no terminal input.
/// It runs in its own session, so a deadline or a stop ends it together with every program it started.
CommandResult run(const std::string& command, const Workspace& workspace, bool sandboxed, double timeout_seconds = 120, const CancelToken& cancel = nullptr);

/// Starts a long-running program with its output in a log file. It is killed when the calling process exits.
/// Returns its process id.
int spawn_owned(const std::vector<std::string>& arguments, const fs::path& log);
bool is_running(int pid);
void stop_owned(int pid);

}  // namespace shell
}  // namespace mason
