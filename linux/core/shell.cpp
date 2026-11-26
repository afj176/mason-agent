#include "shell.hpp"

#include <cerrno>
#include <chrono>
#include <csignal>
#include <cstring>
#include <fcntl.h>
#include <poll.h>
#include <sys/prctl.h>
#include <sys/wait.h>
#include <unistd.h>

namespace mason::shell {

namespace {

bool executable(const std::string& file) { return ::access(file.c_str(), X_OK) == 0; }

std::string find_program(const std::string& name) {
    const char* search = std::getenv("PATH");
    for (const std::string& folder : split(search ? search : "/usr/bin:/bin", ':', false)) {
        const std::string candidate = folder + "/" + name;
        if (executable(candidate)) return candidate;
    }
    return "";
}

std::vector<char*> to_argv(const std::vector<std::string>& arguments) {
    std::vector<char*> argv;
    for (const std::string& argument : arguments) argv.push_back(const_cast<char*>(argument.c_str()));
    argv.push_back(nullptr);
    return argv;
}

/// In the child between fork and exec: default signal handling and an empty mask, whatever the parent thread had.
void reset_signals() {
    sigset_t none;
    sigemptyset(&none);
    sigprocmask(SIG_SETMASK, &none, nullptr);
    for (int number : {SIGINT, SIGTERM, SIGHUP, SIGPIPE, SIGQUIT, SIGCHLD}) ::signal(number, SIG_DFL);
}

double seconds_since(const std::chrono::steady_clock::time_point& start) {
    return std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
}

}  // namespace

std::string path() {
    for (const char* candidate : {"/bin/bash", "/usr/bin/bash", "/bin/sh"}) {
        if (executable(candidate)) return candidate;
    }
    return "/bin/sh";
}

std::string sandbox_tool() {
    static const std::string found = [] {
        const std::string tool = find_program("bwrap");
        if (tool.empty()) return std::string();
        // Some systems install bubblewrap but forbid unprivileged user namespaces; then it cannot create a sandbox.
        const pid_t child = fork();
        if (child == 0) {
            const int null = open("/dev/null", O_RDWR);
            dup2(null, 0); dup2(null, 1); dup2(null, 2);
            execl(tool.c_str(), "bwrap", "--ro-bind", "/", "/", "--unshare-net", "--die-with-parent", "/bin/true", static_cast<char*>(nullptr));
            _exit(127);
        }
        int status = 0;
        if (child < 0 || waitpid(child, &status, 0) < 0) return std::string();
        return WIFEXITED(status) && WEXITSTATUS(status) == 0 ? tool : std::string();
    }();
    return found;
}

bool sandbox_available() { return !sandbox_tool().empty(); }

std::vector<std::string> sandbox_arguments(const std::string& workspace) {
    std::vector<std::string> arguments = {
        "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--tmpfs", "/tmp", "--tmpfs", "/var/tmp",
        "--bind", workspace, workspace, "--unshare-net", "--unshare-ipc", "--unshare-pid", "--die-with-parent", "--new-session"};
    // Credentials are hidden behind empty folders. Only folders that exist are covered; bubblewrap cannot mount onto a missing path in a read-only tree.
    const std::string home = home_dir();
    for (const char* secret : {".ssh", ".aws", ".azure", ".gnupg", ".config/gh", ".kube", ".docker", ".local/share/keyrings", ".password-store", ".netrc"}) {
        const fs::path target = fs::path(home) / secret;
        std::error_code failure;
        if (starts_with(target.string(), workspace + "/")) continue;
        if (fs::is_directory(target, failure)) { arguments.insert(arguments.end(), {"--tmpfs", target.string()}); }
        else if (fs::is_regular_file(target, failure)) { arguments.insert(arguments.end(), {"--ro-bind", "/dev/null", target.string()}); }
    }
    arguments.insert(arguments.end(), {"--chdir", workspace, "--setenv", "TMPDIR", "/tmp"});
    return arguments;
}

CommandResult run(const std::string& command, const Workspace& workspace, bool sandboxed, double timeout_seconds, const CancelToken& cancel,
                  const std::function<void(const char*, size_t)>& on_output) {
    if (command.empty() || command.size() > 8000 || command.find('\0') != std::string::npos) throw Error("Use a shell command of 1–8,000 characters.");
    std::vector<std::string> arguments;
    if (sandboxed) {
        if (!sandbox_available()) throw Error("The sandbox (bubblewrap) is unavailable, so Auto cannot run commands here. No unrestricted fallback was run.");
        arguments = sandbox_arguments(workspace.root());
        arguments.insert(arguments.begin(), sandbox_tool());
    }
    arguments.insert(arguments.end(), {path(), "-c", command});

    int pipe_ends[2];
    if (pipe2(pipe_ends, O_CLOEXEC) != 0) throw Error("The command could not start: no pipe.");
    std::vector<char*> argv = to_argv(arguments);
    const pid_t child = fork();
    if (child < 0) {
        close(pipe_ends[0]); close(pipe_ends[1]);
        throw Error(std::string("The command could not start: ") + std::strerror(errno));
    }
    if (child == 0) {
        // Its own session and process group: a stop reaches the command and everything it started, and it has no terminal.
        setsid();
        reset_signals();
        prctl(PR_SET_PDEATHSIG, SIGKILL);
        const int null = open("/dev/null", O_RDONLY);
        dup2(null, 0); dup2(pipe_ends[1], 1); dup2(pipe_ends[1], 2);
        if (chdir(workspace.root().c_str()) != 0) _exit(126);
        execv(argv[0], argv.data());
        _exit(127);
    }
    close(pipe_ends[1]);
    fcntl(pipe_ends[0], F_SETFL, fcntl(pipe_ends[0], F_GETFL) | O_NONBLOCK);

    CommandResult result;
    const auto started = std::chrono::steady_clock::now();
    bool killed = false, stopped_by_user = false, exited = false;
    int status = 0;
    char buffer[4096];
    auto kill_group = [&] {
        if (!killed) { ::kill(-child, SIGKILL); killed = true; }
    };
    while (true) {
        pollfd waiting{pipe_ends[0], POLLIN, 0};
        poll(&waiting, 1, 100);
        while (true) {
            const ssize_t count = read(pipe_ends[0], buffer, sizeof buffer);
            if (count <= 0) break;
            if (on_output) on_output(buffer, static_cast<size_t>(count));
            const size_t room = kOutputLimit - std::min(kOutputLimit, result.output.size());
            if (static_cast<size_t>(count) > room) result.truncated = true;
            result.output.append(buffer, std::min(static_cast<size_t>(count), room));
        }
        if (!exited && waitpid(child, &status, WNOHANG) == child) exited = true;
        if (is_cancelled(cancel) && !killed) { stopped_by_user = true; kill_group(); }
        if (!killed && seconds_since(started) > timeout_seconds) { result.timed_out = true; kill_group(); }
        if (exited) {
            // The shell is gone. Anything it left running in its session still holds the pipe; end it so the output can close.
            kill_group();
            // Drain what is already buffered, then stop.
            for (int spins = 0; spins < 20; spins++) {
                const ssize_t count = read(pipe_ends[0], buffer, sizeof buffer);
                if (count == 0) break;
                if (count < 0) { if (errno == EAGAIN) { usleep(10'000); continue; } break; }
                if (on_output) on_output(buffer, static_cast<size_t>(count));
            const size_t room = kOutputLimit - std::min(kOutputLimit, result.output.size());
                if (static_cast<size_t>(count) > room) result.truncated = true;
                result.output.append(buffer, std::min(static_cast<size_t>(count), room));
            }
            break;
        }
    }
    close(pipe_ends[0]);
    if (stopped_by_user) throw Cancelled();
    result.exit_code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + (WIFSIGNALED(status) ? WTERMSIG(status) : 0);
    // Model output must be valid text; replace anything that is not.
    if (!is_valid_utf8(result.output)) {
        for (char& c : result.output) {
            if (static_cast<unsigned char>(c) >= 0x80) c = '?';
        }
    }
    if (result.truncated) result.output += "\n[Output clipped at 64 KiB.]";
    if (result.timed_out) result.output += "\n[Stopped: the command exceeded " + std::to_string(static_cast<int>(timeout_seconds)) + " seconds.]";
    return result;
}

int spawn_owned(const std::vector<std::string>& arguments, const fs::path& log) {
    if (arguments.empty()) throw Error("Nothing to start.");
    std::vector<char*> argv = to_argv(arguments);
    const std::string log_path = log.string();
    const pid_t parent = getpid();
    const pid_t child = fork();
    if (child < 0) throw Error(std::string("The program could not start: ") + std::strerror(errno));
    if (child == 0) {
        // This process is a watcher in its own session. It starts the program and ends it when Mason goes away for any
        // reason, including a crash. (A parent-death signal is not used: it is tied to the thread that forked, and
        // Mason starts models from short-lived worker threads.)
        setsid();
        reset_signals();
        const pid_t program = fork();
        if (program == 0) {
            const int null = open("/dev/null", O_RDONLY);
            const int out = open(log_path.c_str(), O_WRONLY | O_CREAT | O_TRUNC, 0600);
            dup2(null, 0); dup2(out, 1); dup2(out, 2);
            execv(argv[0], argv.data());
            _exit(127);
        }
        if (program < 0) _exit(126);
        while (true) {
            int status = 0;
            if (waitpid(program, &status, WNOHANG) == program) _exit(WIFEXITED(status) ? WEXITSTATUS(status) : 1);
            if (getppid() != parent) {
                ::kill(program, SIGKILL);
                _exit(1);
            }
            usleep(300'000);
        }
    }
    return child;
}

bool is_running(int pid) {
    if (pid <= 0) return false;
    int status = 0;
    const pid_t result = waitpid(pid, &status, WNOHANG);
    if (result == 0) return true;
    if (result == pid) return false;
    return ::kill(pid, 0) == 0;
}

void stop_owned(int pid) {
    if (pid <= 0) return;
    ::kill(-pid, SIGKILL);
    ::kill(pid, SIGKILL);
    int status = 0;
    waitpid(pid, &status, 0);
}

}  // namespace mason::shell
