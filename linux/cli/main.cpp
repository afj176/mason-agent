// The interactive mason session for Linux. It needs no options: it finds a running model, restarts the one
// used last, offers the coding models on this computer, or suggests a download that fits.
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <csignal>
#include <cstdlib>
#include <future>
#include <iostream>
#include <mutex>
#include <thread>

#include "agent.hpp"
#include "models.hpp"
#include "terminal.hpp"

using namespace mason;
using term::paint;

namespace {

volatile std::sig_atomic_t g_interrupts = 0;  // Ctrl+C presses seen at the prompt
volatile std::sig_atomic_t g_ending = 0;      // the terminal closed or a stop was requested
std::atomic<bool>* g_running_cancel = nullptr;

void on_interrupt(int) {
    if (g_running_cancel) g_running_cancel->store(true);
    else g_interrupts = g_interrupts + 1;
}
void on_ending(int) { g_ending = 1; if (g_running_cancel) g_running_cancel->store(true); }

const char* const kSpinner[] = {"⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"};

std::string label_of(const std::string& tool) { return replace_all(tool, "_", " "); }

/// What the engine reports while a request runs, handed from its thread to the terminal loop.
struct RunFeed {
    std::mutex lock;
    std::condition_variable answered;
    std::vector<Activity> finished;
    std::string live, running, note;
    const ApprovalRequest* asking = nullptr;
    std::optional<bool> answer;
    bool done = false;

    void push(const AgentEvent& event) {
        std::lock_guard<std::mutex> guard(lock);
        switch (event.kind) {
            case AgentEvent::Kind::Text: live += event.text; break;
            case AgentEvent::Kind::ActivityStarted: running = label_of(event.activity.tool) + " " + event.activity.subject; live.clear(); break;
            case AgentEvent::Kind::ActivityFinished: finished.push_back(event.activity); running.clear(); break;
            case AgentEvent::Kind::Status: note = event.text; break;
        }
    }
    /// Called on the engine thread; blocks until the terminal loop has asked the user.
    bool ask(const ApprovalRequest& request) {
        std::unique_lock<std::mutex> guard(lock);
        asking = &request;
        answer.reset();
        answered.wait(guard, [&] { return answer.has_value(); });
        asking = nullptr;
        return *answer;
    }
    std::string doing() {
        std::lock_guard<std::mutex> guard(lock);
        if (!live.empty()) return replace_all(live, "\n", " ");
        return !running.empty() ? running : !note.empty() ? note : "Thinking";
    }
};

class Cli {
public:
    Cli(SessionStore& store, Workspace workspace, Session session) : store_(store), workspace_(std::move(workspace)), session_(std::move(session)) {}

    std::optional<ModelEndpoint> endpoint;
    std::string endpoint_name;

    void set_permission(Permission permission) {
        session_.permission = permission;
        session_.ask_only = permission == Permission::ReadOnly;
        store_.save(session_);
    }

    void loop(bool resumed) {
        if (!endpoint) find_running_model();
        term::line();
        term::line("  " + paint(std::string(term::bold) + term::accent, "Mason Agent") + paint(term::dim, "  ·  " + workspace_.root()));
        term::line("  " + paint(term::dim, "model ") + (endpoint ? endpoint_name : paint(term::yellow, "none running")) + paint(term::dim, "  ·  " + mode_name() + "  ·  /help for commands"));
        if (resumed) term::line("  " + paint(term::dim, "resumed ") + session_.title + paint(term::dim, "  ·  " + std::to_string(session_.turns.size()) + " requests  ·  " + session_.state));
        if (!endpoint) first_run();
        while (true) {
            const std::optional<std::string> input = read_request();
            if (!input) { term::line(); return; }
            if (input->empty()) continue;
            try {
                if ((*input)[0] == '/') { if (!command(*input)) return; }
                else send(*input);
            } catch (const Cancelled&) {
                term::line(paint(term::yellow, "  Cancelled."));
            } catch (const std::exception& error) {
                term::line(paint(term::red, std::string("✗ ") + error.what()));
            }
        }
    }

    void shutdown() {
        term::raw(false);
        server_.reset();
    }

private:
    SessionStore& store_;
    Workspace workspace_;
    Session session_;
    std::unique_ptr<ModelServer> server_;
    std::vector<LocalModel> listed_;
    std::vector<HubModel> found_;
    std::chrono::steady_clock::time_point last_interrupt_{};

    std::string mode_name() const { return session_.ask_only ? "ask · read-only" : "build · " + permission_label(session_.permission); }
    std::string prompt() const {
        const std::string mode = mode_name();
        const size_t dot = mode.rfind(" · ");
        return paint(term::dim, (dot == std::string::npos ? mode : mode.substr(dot + std::string(" · ").size())) + " ") + paint(std::string(term::accent) + term::bold, "› ");
    }

    // ---- input -------------------------------------------------------------------------------------------------

    /// One line from the terminal, or nothing at end of input. Ctrl+C at the prompt asks once; a second press within five seconds leaves.
    std::optional<std::string> read_line() {
        while (true) {
            std::string line;
            errno = 0;
            if (std::getline(std::cin, line)) return line;
            if (g_ending) return std::nullopt;
            if (g_interrupts > 0) {
                g_interrupts = 0;
                std::cin.clear();
                clearerr(stdin);
                const auto now = std::chrono::steady_clock::now();
                if (last_interrupt_.time_since_epoch().count() != 0 && now - last_interrupt_ < std::chrono::seconds(5)) return std::nullopt;
                last_interrupt_ = now;
                term::line();
                term::line(paint(term::dim, "  Press Ctrl+C again or type /exit to leave."));
                term::write(prompt());
                continue;
            }
            return std::nullopt;
        }
    }

    /// One request. A trailing backslash continues on the next line.
    std::optional<std::string> read_request() {
        std::string text;
        while (true) {
            term::line();
            term::write(text.empty() ? prompt() : paint(term::dim, "  … "));
            const std::optional<std::string> line = read_line();
            if (!line) return text.empty() ? std::nullopt : std::optional<std::string>(trim(text));
            if (!line->empty() && line->back() == '\\') { text += line->substr(0, line->size() - 1) + "\n"; continue; }
            return trim(text + *line);
        }
    }

    bool confirm(const std::string& question) {
        if (!term::interactive) { term::line("  " + question + " Not confirmed: there is no terminal to ask."); return false; }
        term::write("  " + question + "  " + paint(term::accent, "y") + " yes   " + paint(term::accent, "n") + " no  › ");
        const std::string answer = to_lower(trim(read_line().value_or("")));
        return answer == "y" || answer == "yes";
    }

    /// Runs work on another thread behind a status line. Esc or Ctrl+C cancels it.
    template <typename T>
    T busy(const std::string& label, const std::function<T(const std::function<void(const std::string&)>&, const CancelToken&)>& work) {
        const CancelToken cancel = make_cancel();
        std::mutex lock;
        std::string detail = label;
        auto progress = [&](const std::string& text) { std::lock_guard<std::mutex> guard(lock); detail = text.empty() ? label : text; };
        std::future<T> result = std::async(std::launch::async, [&] { return work(progress, cancel); });
        g_running_cancel = cancel.get();
        term::raw(true);
        const auto started = std::chrono::steady_clock::now();
        int frame = 0;
        while (result.wait_for(std::chrono::milliseconds(0)) != std::future_status::ready) {
            const int pressed = term::key();
            if (pressed == 3 || (pressed == 27 && term::was_escape_alone())) cancel->store(true);
            std::string shown;
            { std::lock_guard<std::mutex> guard(lock); shown = detail; }
            const long seconds = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - started).count();
            term::set_status(std::string("  ") + kSpinner[frame++ % 10] + " " + term::clip(shown, std::max(20, term::width() - 34)) + "  ·  " + std::to_string(seconds) + "s  ·  Esc to cancel");
        }
        g_running_cancel = nullptr;
        term::set_status("");
        term::raw(false);
        return result.get();
    }

    // ---- running a request -------------------------------------------------------------------------------------

    void send(const std::string& input) {
        if (!endpoint) {
            term::line(paint(term::yellow, "  No model is running.") + paint(term::dim, " /models lists the ones on this computer; /models setup finds one to download."));
            return;
        }
        if (!OpenAIClient::models(endpoint->url)) {
            term::line(paint(term::yellow, "  " + endpoint_name + " is not answering at " + endpoint->url + ". Start it again or choose another with /model."));
            return;
        }
        OpenAIClient model(*endpoint);
        RunFeed feed;
        const CancelToken cancel = make_cancel();
        const size_t first_activity = session_.activities.size(), turns_before = session_.turns.size();
        const auto started = std::chrono::steady_clock::now();
        Agent agent(store_, model, [&](const ApprovalRequest& request) { return feed.ask(request); }, [&](const AgentEvent& event) { feed.push(event); });
        std::thread worker([&] {
            agent.run(session_, input, cancel);
            std::lock_guard<std::mutex> guard(feed.lock);
            feed.done = true;
        });
        g_running_cancel = cancel.get();
        term::line();
        term::raw(true);
        int frame = 0;
        bool stopping = false;
        while (true) {
            std::vector<Activity> finished;
            const ApprovalRequest* asking = nullptr;
            bool done;
            {
                std::lock_guard<std::mutex> guard(feed.lock);
                finished.swap(feed.finished);
                if (feed.asking && !feed.answer) asking = feed.asking;
                done = feed.done;
            }
            for (const Activity& activity : finished) show(activity);
            if (done) break;
            if (asking) {
                term::set_status("");
                const bool allow = approve(*asking, cancel);
                { std::lock_guard<std::mutex> guard(feed.lock); feed.answer = allow; }
                feed.answered.notify_all();
                continue;
            }
            const int pressed = term::key();
            if (pressed == 3 || (pressed == 27 && term::was_escape_alone())) cancel->store(true);
            if (cancel->load() && !stopping) { stopping = true; term::line(paint(term::dim, "  Stopping…")); }
            const std::string doing = feed.doing();
            const int room = std::max(20, term::width() - 36);
            const long seconds = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - started).count();
            term::set_status(std::string("  ") + kSpinner[frame++ % 10] + " " + (static_cast<int>(utf8_length(doing)) > room ? "…" + utf8_suffix(doing, static_cast<size_t>(room - 1)) : doing) +
                             "  ·  " + std::to_string(seconds) + "s  ·  Esc to stop");
        }
        worker.join();
        g_running_cancel = nullptr;
        term::set_status("");
        term::raw(false);
        outcome(first_activity, turns_before, std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count());
    }

    void show(const Activity& activity) {
        const std::string mark = activity.state == "completed" ? paint(term::green, "✓") : activity.state == "failed" ? paint(term::red, "✗")
                               : activity.state == "denied" ? paint(term::yellow, "⊘") : paint(term::dim, "·");
        const std::string label = label_of(activity.tool);
        term::line("  " + mark + " " + paint(term::bold, label) + " " + term::clip(replace_all(activity.subject, "\n", " "), std::max(10, term::width() - static_cast<int>(label.size()) - 8)) +
                   (activity.state == "denied" ? paint(term::dim, "  declined") : ""));
        if (activity.tool != "run_shell" && activity.state != "failed") return;
        std::vector<std::string> lines;
        for (const std::string& line : split(activity.result, '\n', false)) {
            if (!trim(line).empty()) lines.push_back(line);
        }
        const size_t limit = activity.state == "failed" ? 10 : 6;
        if (lines.size() > limit) term::line(paint(term::dim, "    │ … " + std::to_string(lines.size() - limit) + " earlier lines"));
        for (size_t i = lines.size() > limit ? lines.size() - limit : 0; i < lines.size(); i++) term::line(paint(term::dim, "    │ " + term::clip(lines[i], term::width() - 8)));
    }

    void outcome(size_t first_activity, size_t turns_before, double seconds) {
        const Turn* turn = session_.turns.size() > turns_before ? &session_.turns.back() : nullptr;
        if (turn && !turn->answer.empty()) {
            term::line();
            for (const std::string& line : term::markdown(turn->answer)) term::line("  " + line);
        }
        if (turn && !turn->changes.empty()) {
            term::line();
            for (size_t i = 0; i < turn->changes.size() && i < 30; i++) {
                const FileChange& change = turn->changes[i];
                const DiffCounts counts = diff_counts(change.before, change.after);
                term::line("  " + paint(term::accent, change.before ? "M" : "A") + " " + change.path + "  " + paint(term::green, "+" + std::to_string(counts.added)) + " " + paint(term::red, "−" + std::to_string(counts.removed)));
            }
            term::line(paint(term::dim, "  /diff shows the changes · /undo puts the edited files back"));
        }
        term::line();
        const std::string state = session_.state == "Completed" ? paint(term::green, "✓ Completed") : session_.state == "Stopped" ? paint(term::yellow, "■ Stopped")
                                : session_.state == "Failed" ? paint(term::red, "✗ Failed") : paint(term::yellow, "● " + session_.state);
        const size_t steps = session_.activities.size() - first_activity;
        char elapsed[32];
        if (seconds < 90) std::snprintf(elapsed, sizeof elapsed, "%ds", static_cast<int>(seconds));
        else std::snprintf(elapsed, sizeof elapsed, "%.1f min", seconds / 60);
        std::string facts = std::to_string(steps) + " tool call" + (steps == 1 ? "" : "s") + "  ·  " + elapsed;
        if (session_.prompt_tokens > 0) facts += "  ·  " + std::to_string(session_.prompt_tokens) + " tokens of context";
        term::line("  " + state + paint(term::dim, "  ·  " + facts));
        if (!session_.detail.empty()) term::line("  " + paint(term::dim, session_.detail));
    }

    // ---- approvals ---------------------------------------------------------------------------------------------

    /// Called in raw mode. y allows once, a saves a rule for this project, n or Esc declines.
    bool approve(const ApprovalRequest& request, const CancelToken& cancel) {
        const std::string tool = label_of(request.tool), bar = paint(term::yellow, "  ▌ ");
        term::line();
        term::line(bar + paint(term::bold, "Review " + tool));
        const std::vector<std::string> title = split(request.title, '\n');
        for (size_t i = 0; i < title.size() && i < 6; i++) term::line(bar + term::clip(title[i], term::width() - 6));
        const std::vector<std::string> detail = split(request.detail, '\n');
        for (size_t i = 0; i < detail.size() && i < 40; i++) {
            const std::string& row = detail[i];
            term::line(bar + paint(starts_with(row, "+") ? term::green : starts_with(row, "-") ? term::red : term::dim, term::clip(row, term::width() - 6)));
        }
        if (detail.size() > 40) term::line(bar + paint(term::dim, "… " + std::to_string(detail.size() - 40) + " more lines"));
        if (!term::interactive) { term::line(bar + "Declined: there is no terminal to ask."); return false; }
        while (true) {
            term::write("  " + paint(term::bold, "Allow?") + "  " + paint(term::accent, "y") + " once   " + paint(term::accent, "a") + " always for this project…   " + paint(term::accent, "n") + " decline  › ");
            term::drain_keys();
            char choice = 0;
            while (choice == 0) {
                if (cancel->load()) { term::line("cancelled"); return false; }
                const int pressed = term::key();
                if (pressed < 0) continue;
                if (pressed == 3 || pressed == 27) choice = 'n';
                else if (std::string("yan").find(static_cast<char>(std::tolower(pressed))) != std::string::npos) choice = static_cast<char>(std::tolower(pressed));
            }
            if (choice == 'n') { term::line(paint(term::yellow, "declined")); return false; }
            if (choice == 'y') { term::line(paint(term::green, "allowed once")); return true; }
            term::line("always");
            const std::string suggested = ApprovalRules::suggestion(request.tool, request.subject);
            term::raw(false);
            term::write("  Allow " + tool + " when it starts with [" + paint(term::cyan, suggested) + "] (Enter keeps it, a single - cancels): ");
            const std::optional<std::string> typed = read_line();
            term::raw(true);
            if (!typed || trim(*typed) == "-") continue;
            try {
                const std::string prefix = trim(*typed).empty() ? suggested : trim(*typed);
                ApprovalRules(store_.home(), session_.workspace).add(request.tool, prefix);
                term::line(paint(term::green, "  Saved: always allow " + tool + " · " + prefix) + paint(term::dim, "  ·  /rules lists and revokes"));
                return true;
            } catch (const Error& error) {
                term::line(paint(term::red, std::string("  ") + error.what()));
            }
        }
    }

    // ---- commands ----------------------------------------------------------------------------------------------

    /// Returns false to leave.
    bool command(const std::string& input) {
        const size_t space = input.find(' ');
        std::string name = to_lower(input.substr(0, space)), rest = space == std::string::npos ? "" : trim(input.substr(space + 1));
        if (name == "/read" || name == "/review" || name == "/edits" || name == "/auto") { rest = name.substr(1); name = "/permission"; }
        if (name == "/exit" || name == "/quit") return false;
        if (name == "/help" || name == "/?") { help(); return true; }
        if (name == "/new") {
            if (!session_.turns.empty()) {
                const Session previous = session_;
                session_ = store_.create(workspace_.root(), previous.permission);
                session_.ask_only = previous.ask_only;
                store_.save(session_);
            }
            term::line(paint(term::dim, "  New session · " + mode_name()));
            if (!rest.empty()) send(rest);
            return true;
        }
        if (name == "/resume" || name == "/sessions") {
            std::vector<Session> mine;
            for (const Session& item : store_.sessions_in(workspace_.root())) {
                if ((!item.turns.empty() || item.id == session_.id) && mine.size() < 20) mine.push_back(item);
            }
            const int number = rest.empty() ? 0 : std::atoi(rest.c_str());
            if (number >= 1 && static_cast<size_t>(number) <= mine.size()) {
                session_ = mine[static_cast<size_t>(number) - 1];
                term::line("  Resumed " + paint(term::bold, session_.title) + paint(term::dim, "  ·  " + mode_name()));
                return true;
            }
            term::line();
            for (size_t i = 0; i < mine.size(); i++) {
                const bool current = mine[i].id == session_.id;
                term::line("  " + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + (current ? paint(term::bold, term::clip(mine[i].title, 60)) : term::clip(mine[i].title, 60)) +
                           paint(term::dim, "  ·  " + mine[i].state + (current ? "  ← current" : "")));
            }
            term::line(paint(term::dim, "  Continue one with /resume NUMBER."));
            return true;
        }
        if (name == "/ask") {
            session_.ask_only = true;
            store_.save(session_);
            term::line(paint(term::dim, "  Ask mode: Mason reads and answers; it does not edit or run commands."));
            return true;
        }
        if (name == "/build") {
            session_.ask_only = false;
            if (session_.permission == Permission::ReadOnly) session_.permission = Permission::ReviewEdits;
            store_.save(session_);
            term::line(paint(term::dim, "  " + mode_name()));
            return true;
        }
        if (name == "/permission" || name == "/permissions") {
            if (rest.empty()) { term::line("  " + mode_name() + paint(term::dim, "  ·  /permission read | review | edits | auto")); return true; }
            const std::string choice = to_lower(rest);
            if (choice == "read" || choice == "read-only") { set_permission(Permission::ReadOnly); term::line(paint(term::dim, "  Read-only: Mason reads and answers.")); }
            else if (choice == "review") { set_permission(Permission::ReviewEdits); term::line(paint(term::dim, "  Review edits: you approve each edit and command.")); }
            else if (choice == "edits") { set_permission(Permission::WorkspaceEdits); term::line(paint(term::dim, "  Workspace edits: edits apply without asking; commands still ask.")); }
            else if (choice == "auto") {
                set_permission(Permission::Auto);
                term::line(paint(term::dim, "  Auto: commands run in a sandbox (bubblewrap), limited to this project, with network access off."));
                if (!shell::sandbox_available()) term::line(paint(term::yellow, "  bubblewrap cannot create a sandbox here, so commands will be refused in Auto. Install bubblewrap, or use /edits."));
            } else throw Error("Use /permission read, review, edits or auto.");
            return true;
        }
        if (name == "/status") { status(); return true; }
        if (name == "/diff" || name == "/changes") { diff(rest); return true; }
        if (name == "/undo" || name == "/redo") {
            const bool redo = name == "/redo";
            const std::optional<UndoResult> result = Agent::undo_latest(session_, store_, redo);
            if (!result) { term::line(paint(term::dim, redo ? "  Nothing to redo." : "  Nothing to undo in this session.")); return true; }
            term::line(paint(term::green, "  ✓ " + std::to_string(result->restored.size()) + " file" + (result->restored.size() == 1 ? "" : "s") + (redo ? " reapplied" : " put back")) +
                       (result->skipped.empty() ? "" : paint(term::yellow, "  ·  changed since, left alone: " + join(result->skipped, ", "))));
            term::line(paint(term::dim, "  Covers files changed by edits. Files created or changed by commands are not tracked yet."));
            return true;
        }
        if (name == "/rules") { rules(rest); return true; }
        if (name == "/model") { choose_model(rest); return true; }
        if (name == "/models") { models(rest); return true; }
        term::line(paint(term::yellow, "  Unknown command " + name + ".") + paint(term::dim, " /help lists them."));
        return true;
    }

    void help() {
        const std::pair<const char*, const char*> rows[] = {
            {"/new [request]", "Start a separate session, optionally with its first request"},
            {"/resume [number]", "List this folder's sessions, or continue one"},
            {"/ask · /build", "Answer questions only, or make changes"},
            {"/permission read|review|edits|auto", "What Mason may do without asking (also /review, /edits, /auto)"},
            {"/diff [path]", "Show what this session changed"},
            {"/undo · /redo", "Put back, or reapply, the files the latest request edited"},
            {"/rules [revoke NUMBER]", "Saved always-allow rules for this project"},
            {"/model [number]", "List running model servers, or switch to one"},
            {"/models", "List the models on this computer and what each is for"},
            {"/models start NUMBER|NAME", "Start a coding model here (add cpu, or a context size)"},
            {"/models stop", "Stop the model this session started"},
            {"/models search TEXT", "Search Hugging Face"},
            {"/models download REPO [BUILD]", "List a repository's builds, or download one"},
            {"/models setup", "Suggest coding models that fit this computer and download one"},
            {"/models runtime", "Install or update llama.cpp"},
            {"/status", "Session, model, permission and storage"},
            {"/exit", "Leave (Ctrl+C twice also works)"},
        };
        term::line();
        for (const auto& [name, text] : rows) term::line("  " + paint(term::accent, term::pad(name, 36)) + " " + paint(term::dim, text));
        term::line();
        term::line(paint(term::dim, "  While Mason works: Esc or Ctrl+C stops the run. End a line with \\ to continue on the next line."));
    }

    void status() {
        auto row = [](const std::string& label, const std::string& value) { term::line("  " + paint(term::dim, term::pad(label, 12)) + " " + value); };
        term::line();
        row("Project", workspace_.root());
        row("Session", session_.title + paint(term::dim, "  " + session_.id.substr(0, 8) + " · " + session_.state));
        row("Model", endpoint ? endpoint_name + paint(term::dim, "  " + endpoint->url) : paint(term::yellow, "none running"));
        row("Mode", mode_name());
        row("Shell", shell::path() + (shell::sandbox_available() ? paint(term::dim, "  ·  sandbox available for Auto") : paint(term::yellow, "  ·  no sandbox: Auto refuses commands")));
        if (session_.prompt_tokens > 0) row("Context", std::to_string(session_.prompt_tokens) + " tokens in the last request");
        row("Stored in", store_.folder().string());
    }

    void diff(const std::string& filter) {
        std::vector<FileChange> merged;
        for (const Turn& turn : session_.turns) {
            if (turn.undone) continue;
            for (const FileChange& change : turn.changes) {
                if (!filter.empty() && !contains_ci(change.path, filter)) continue;
                auto existing = std::find_if(merged.begin(), merged.end(), [&](const FileChange& item) { return item.path == change.path; });
                if (existing != merged.end()) existing->after = change.after;
                else merged.push_back(change);
            }
        }
        if (merged.empty()) { term::line(paint(term::dim, filter.empty() ? "  This session has not changed any files." : "  No changed file matches " + filter + ".")); return; }
        int budget = 400;
        for (const FileChange& change : merged) {
            const std::vector<DiffLine> rows = diff_lines(change.before, change.after);
            const DiffCounts counts = diff_counts(change.before, change.after);
            term::line();
            term::line("  " + paint(term::bold, change.path) + "  " + paint(term::green, "+" + std::to_string(counts.added)) + " " + paint(term::red, "−" + std::to_string(counts.removed)) +
                       (change.before ? "" : paint(term::dim, "  new file")));
            // Two lines of context around each change, with gaps marked.
            std::vector<bool> keep(rows.size(), false);
            for (size_t i = 0; i < rows.size(); i++) {
                if (rows[i].kind == ' ') continue;
                for (size_t near = i >= 2 ? i - 2 : 0; near <= std::min(rows.size() - 1, i + 2); near++) keep[near] = true;
            }
            bool gap = false;
            for (size_t i = 0; i < rows.size() && budget > 0; i++) {
                if (!keep[i]) { gap = true; continue; }
                if (gap && i > 0) term::line(paint(term::dim, "       ⋯"));
                gap = false;
                budget--;
                const std::string text = term::clip(replace_all(rows[i].text, "\t", "    "), term::width() - 12);
                const std::string number = paint(term::dim, term::left_pad(std::to_string(rows[i].number), 6) + " ");
                term::line(number + (rows[i].kind == '+' ? paint(term::green, "+ " + text) : rows[i].kind == '-' ? paint(term::red, "- " + text) : "  " + text));
            }
            if (budget <= 0) { term::line(paint(term::dim, "  … more changes not shown. Use /diff PATH for one file.")); return; }
        }
    }

    void rules(const std::string& rest) {
        ApprovalRules saved(store_.home(), session_.workspace);
        if (starts_with(to_lower(rest), "revoke")) {
            const int number = std::atoi(trim(rest.substr(6)).c_str());
            if (number < 1 || static_cast<size_t>(number) > saved.rules().size()) throw Error("Use /rules revoke NUMBER from the /rules list.");
            const ApprovalRules::Rule removed = saved.rules()[static_cast<size_t>(number) - 1];
            saved.remove(static_cast<size_t>(number) - 1);
            term::line(paint(term::dim, "  Revoked: " + label_of(removed.tool) + " · " + removed.prefix));
            return;
        }
        if (saved.rules().empty()) { term::line(paint(term::dim, "  No saved rules. Choose \"a\" on an approval to save one for this project.")); return; }
        for (size_t i = 0; i < saved.rules().size(); i++) {
            term::line("  " + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + paint(term::green, "allow") + "  " + label_of(saved.rules()[i].tool) + "  " + paint(term::cyan, saved.rules()[i].prefix));
        }
        term::line(paint(term::dim, "  Remove one with /rules revoke NUMBER. Rules never widen the sandbox."));
    }

    // ---- models ------------------------------------------------------------------------------------------------

    void use(const ModelEndpoint& chosen, const std::string& name) {
        endpoint = chosen;
        endpoint_name = name;
        Preferences preferences = Preferences::load();
        preferences.last_endpoint = chosen;
        preferences.save();
    }

    /// A model that is already answering: the one used last, else the first local server found.
    void find_running_model() {
        if (const auto last = Preferences::load().last_endpoint) {
            const auto listed = OpenAIClient::models(last->url);
            if (listed && std::find(listed->begin(), listed->end(), last->model) != listed->end()) { endpoint = *last; endpoint_name = last->model; return; }
        }
        const std::vector<RunningServer> servers = detect_servers();
        if (!servers.empty()) use(servers[0].endpoint, servers[0].endpoint.model + " (" + servers[0].name + ")");
    }

    void choose_model(const std::string& rest) {
        const auto servers = busy<std::vector<RunningServer>>("Looking for running model servers", [](const auto&, const CancelToken&) { return detect_servers(); });
        if (servers.empty()) { term::line(paint(term::dim, "  No model server is running on this computer. /models lists the models here and starts one.")); return; }
        const int number = rest.empty() ? 0 : std::atoi(rest.c_str());
        if (number >= 1 && static_cast<size_t>(number) <= servers.size()) {
            const RunningServer& chosen = servers[static_cast<size_t>(number) - 1];
            use(chosen.endpoint, chosen.endpoint.model + " (" + chosen.name + ")");
            term::line("  Model: " + paint(term::bold, endpoint_name));
            return;
        }
        term::line();
        for (size_t i = 0; i < servers.size(); i++) {
            const bool current = endpoint && servers[i].endpoint == *endpoint;
            term::line("  " + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + paint(term::green, "●") + " " + (current ? paint(term::bold, servers[i].endpoint.model) : servers[i].endpoint.model) +
                       paint(term::dim, "  " + servers[i].name + " · " + servers[i].endpoint.url + (current ? "  ← current" : "")));
        }
        term::line(paint(term::dim, "  Choose with /model NUMBER."));
    }

    void models(const std::string& rest) {
        const size_t space = rest.find(' ');
        const std::string verb = to_lower(rest.substr(0, space)), argument = space == std::string::npos ? "" : trim(rest.substr(space + 1));
        if (verb.empty() || verb == "list") list_models();
        else if (verb == "start") start_named(argument);
        else if (verb == "stop") {
            if (!server_) { term::line(paint(term::dim, "  This session has not started a model.")); return; }
            const std::string name = server_->model.name;
            if (endpoint && *endpoint == server_->endpoint) endpoint.reset();
            server_.reset();
            term::line(paint(term::dim, "  Stopped " + name + "."));
        } else if (verb == "search") search(argument);
        else if (verb == "download") download(argument);
        else if (verb == "setup") setup();
        else if (verb == "runtime") {
            const std::string path = install_runtime(true);
            if (!path.empty()) term::line(paint(term::green, "  ✓ llama.cpp is ready") + paint(term::dim, "  " + path));
        } else term::line(paint(term::yellow, "  Use /models, /models start, stop, search, download, setup or runtime."));
    }

    void list_models() {
        listed_ = busy<std::vector<LocalModel>>("Looking for models on this computer", [](const auto&, const CancelToken&) { return scan_models(); });
        if (listed_.empty()) {
            term::line(paint(term::dim, "  No model files were found on this computer."));
            term::line(paint(term::dim, "  /models setup suggests one that fits and downloads it · /models search TEXT looks on Hugging Face."));
            return;
        }
        term::line();
        for (size_t i = 0; i < listed_.size() && i < 40; i++) {
            const LocalModel& model = listed_[i];
            const bool running = server_ && server_->running() && server_->model.path == model.path;
            const std::string fit = model.startable() && !memory_fits(model.bytes) ? paint(term::yellow, "  larger than the memory available") : "";
            term::line("  " + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + (model.startable() ? paint(term::bold, term::clip(model.name, 52)) : term::clip(model.name, 52)) +
                       paint(term::dim, "  " + byte_text(model.bytes) + " · " + model.source) + (running ? paint(term::green, "  ● running here") : fit));
            term::line(paint(term::dim, "      " + model.verdict()));
        }
        if (listed_.size() > 40) term::line(paint(term::dim, "      … and " + std::to_string(listed_.size() - 40) + " more files"));
        term::line(paint(term::dim, "  Start a coding model with /models start NUMBER (add cpu, or a context size such as 16384)."));
    }

    void start_named(const std::string& argument) {
        std::vector<std::string> words = split(argument, ' ', false);
        bool cpu = false;
        int context = 0;
        for (size_t i = words.size(); i-- > 1;) {
            const int tokens = std::atoi(words[i].c_str());
            if (to_lower(words[i]) == "cpu") { cpu = true; words.erase(words.begin() + static_cast<std::ptrdiff_t>(i)); }
            else if (tokens >= 512 && std::to_string(tokens) == words[i]) { context = tokens; words.erase(words.begin() + static_cast<std::ptrdiff_t>(i)); }
        }
        const std::string wanted = join(words, " ");
        if (wanted.empty()) throw Error("Use /models start NUMBER or NAME. /models lists them.");
        if (listed_.empty()) listed_ = scan_models();
        const LocalModel* model = nullptr;
        const int number = std::atoi(wanted.c_str());
        if (number >= 1 && std::to_string(number) == wanted && static_cast<size_t>(number) <= listed_.size()) model = &listed_[static_cast<size_t>(number) - 1];
        else {
            for (const LocalModel& candidate : listed_) {
                if (candidate.startable() && contains_ci(candidate.name, wanted)) { model = &candidate; break; }
            }
        }
        if (!model) throw Error("No model on this computer matches \"" + wanted + "\". /models lists them.");
        if (!model->startable()) throw Error(model->name + " cannot be started here: " + model->verdict() + ".");
        start(*model, context, cpu);
    }

    /// Starts a model with llama.cpp and makes it this session's model.
    bool start(const LocalModel& chosen, int context = 0, bool cpu_only = false) {
        const LocalModel model = chosen;  // `listed_` may change while this runs
        std::string runtime = llama::find();
        if (runtime.empty()) {
            term::line(paint(term::yellow, "  llama.cpp is not installed yet; it runs GGUF models."));
            runtime = install_runtime(false);
        }
        if (runtime.empty()) return false;
        server_.reset();
        try {
            server_ = busy<std::unique_ptr<ModelServer>>("Starting " + model.name, [&](const auto& progress, const CancelToken& cancel) {
                return ModelServer::start(model, runtime, context, cpu_only, progress, cancel);
            });
        } catch (const Error& error) {
            term::line(paint(term::red, "  ✗ " + model.name + " did not start. ") + error.what());
            return false;
        }
        use(server_->endpoint, model.name);
        Preferences preferences = Preferences::load();
        preferences.last_model_path = model.path;
        preferences.last_context = server_->context;
        preferences.save();
        term::line(paint(term::green, "  ✓ " + model.name + " is running") + paint(term::dim, "  ·  " + std::to_string(server_->context) + " context · stops when you leave mason"));
        return true;
    }

    std::string install_runtime(bool force) {
        if (!force) {
            if (const std::string existing = llama::find(); !existing.empty()) return existing;
        }
        const llama::Release release = busy<llama::Release>("Finding the current llama.cpp release", [](const auto&, const CancelToken& cancel) { return llama::latest(cancel); });
        term::line("  llama.cpp " + release.tag + " · " + release.asset + " · " + byte_text(release.size));
        term::line(paint(term::dim, "  From the official ggml-org release on GitHub" + std::string(release.sha256.empty() ? "" : ", SHA-256 verified") + ", installed under " + llama::managed_folder().string()));
        if (!confirm("Download and install it?")) return "";
        return busy<std::string>("Installing llama.cpp", [&](const auto& progress, const CancelToken& cancel) { return llama::install(release, progress, cancel); });
    }

    void search(const std::string& query) {
        if (query.empty()) throw Error("Use /models search TEXT, for example /models search qwen coder.");
        const auto results = busy<std::vector<HubModel>>("Searching Hugging Face", [&](const auto&, const CancelToken& cancel) { return hub_search(query, cancel); });
        found_.assign(results.begin(), results.begin() + static_cast<std::ptrdiff_t>(std::min<size_t>(results.size(), 20)));
        if (found_.empty()) { term::line(paint(term::dim, "  Nothing on Hugging Face matches \"" + query + "\".")); return; }
        term::line();
        for (size_t i = 0; i < found_.size(); i++) {
            term::line("  " + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + paint(term::bold, term::clip(found_[i].id, 60)) +
                       paint(term::dim, "  " + std::to_string(found_[i].downloads) + " downloads" + (found_[i].gated ? " · access terms" : "")));
            term::line(paint(term::dim, "      " + found_[i].verdict()));
        }
        if (results.size() > found_.size()) term::line(paint(term::dim, "      … " + std::to_string(results.size() - found_.size()) + " more results. Narrow the search to see them."));
        term::line(paint(term::dim, "  See the builds of one with /models download NUMBER."));
    }

    void download(const std::string& argument) {
        const size_t space = argument.find(' ');
        const std::string first = argument.substr(0, space), second = space == std::string::npos ? "" : trim(argument.substr(space + 1));
        if (first.empty()) throw Error("Use /models download NUMBER (from a search) or OWNER/NAME, then choose a build.");
        const int number = std::atoi(first.c_str());
        const std::string id = number >= 1 && std::to_string(number) == first && static_cast<size_t>(number) <= found_.size() ? found_[static_cast<size_t>(number) - 1].id : first;
        const HubRepository repository = busy<HubRepository>("Reading " + id, [&](const auto&, const CancelToken& cancel) { return hub_inspect(id, cancel); });
        if (repository.builds.empty()) { term::line(paint(term::yellow, "  " + id + " has no single-file GGUF builds to download.")); return; }
        const std::optional<HubBuild> best = repository.suggested();
        if (second.empty()) {
            term::line();
            term::line("  " + paint(term::bold, repository.id) + paint(term::dim, "  license " + (repository.license.empty() ? "not stated" : repository.license) + (repository.gated ? " · access terms apply" : "") +
                       " · " + byte_text(memory_budget_bytes()) + " of " + memory_budget_source() + " available for a model"));
            for (size_t i = 0; i < repository.builds.size() && i < 40; i++) {
                const HubBuild& build = repository.builds[i];
                const std::string fit = build.is_projector() ? paint(term::dim, "vision companion file")
                                      : memory_fits(build.bytes) ? paint(term::green, "fits · about " + byte_text(memory_needed_bytes(build.bytes)))
                                      : paint(term::yellow, "needs about " + byte_text(memory_needed_bytes(build.bytes)));
                term::line("  " + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + term::pad(term::clip(build.file, 46), 46) + " " + paint(term::dim, term::pad(byte_text(build.bytes), 12)) + " " + fit +
                           (best && best->file == build.file ? paint(term::accent, "  ← suggested") : ""));
            }
            term::line(paint(term::dim, "  Download one with /models download " + repository.id + " NUMBER."));
            return;
        }
        const HubBuild* build = nullptr;
        const int pick = std::atoi(second.c_str());
        if (pick >= 1 && std::to_string(pick) == second && static_cast<size_t>(pick) <= repository.builds.size()) build = &repository.builds[static_cast<size_t>(pick) - 1];
        else {
            for (const HubBuild& candidate : repository.builds) {
                if (contains_ci(candidate.file, second)) { build = &candidate; break; }
            }
        }
        if (!build) throw Error("No build of " + id + " matches \"" + second + "\".");
        fetch(*build, repository);
    }

    /// Downloads one build after confirmation, then offers to start it.
    bool fetch(const HubBuild& build, const HubRepository& repository) {
        term::line("  " + paint(term::bold, repository.id) + " · " + build.file + " · " + byte_text(build.bytes) + " · license " + (repository.license.empty() ? "not stated" : repository.license));
        term::line(paint(term::dim, "  From huggingface.co, verified against its published SHA-256, saved under " + download_folder().string()));
        if (repository.gated) term::line(paint(term::yellow, "  This repository has access terms. Accept them on its model page if the download is refused."));
        if (!confirm("Download it?")) return false;
        const fs::path file = busy<fs::path>("Downloading " + build.file, [&](const auto& progress, const CancelToken& cancel) {
            auto last = std::chrono::steady_clock::now() - std::chrono::seconds(1);
            return hub_download(build, repository, [&](int64_t done, int64_t total) {
                const auto now = std::chrono::steady_clock::now();
                if (now - last < std::chrono::milliseconds(250)) return;
                last = now;
                progress("Downloading " + build.file + " · " + std::to_string(total > 0 ? done * 100 / total : 0) + "% · " + byte_text(done) + " of " + byte_text(total));
            }, cancel);
        });
        term::line(paint(term::green, "  ✓ Downloaded and verified") + paint(term::dim, "  " + file.string()));
        listed_.clear();
        const LocalModel model{file.stem().string(), file.string(), build.bytes, "Mason downloads"};
        if (!model.startable() || !confirm("Start it now?")) return true;
        return start(model);
    }

    void setup() {
        const auto picks = busy<std::vector<HubSuggestion>>("Finding coding models that fit this computer", [](const auto& progress, const CancelToken& cancel) { return hub_suggestions(4, progress, cancel); });
        term::line("  " + paint(term::dim, byte_text(memory_budget_bytes()) + " of " + memory_budget_source() + " available for a model"));
        if (picks.empty()) { term::line(paint(term::yellow, "  No suggested coding model fits. Pick one with /models search.")); return; }
        for (size_t i = 0; i < picks.size(); i++) {
            term::line("  " + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + paint(term::bold, picks[i].repository.id) + " · " + picks[i].build.file + " · " + byte_text(picks[i].build.bytes));
            term::line(paint(term::dim, "      " + picks[i].build.quant() + " is the highest-quality build that fits: about " + byte_text(memory_needed_bytes(picks[i].build.bytes)) + "."));
        }
        if (!term::interactive) return;
        term::write("  Download which? Type a number, or press Enter to skip: ");
        const int number = std::atoi(trim(read_line().value_or("")).c_str());
        if (number < 1 || static_cast<size_t>(number) > picks.size()) return;
        fetch(picks[static_cast<size_t>(number) - 1].build, picks[static_cast<size_t>(number) - 1].repository);
    }

    /// Runs when nothing is answering at startup: restart the model used last, else offer the coding models here, else offer a download.
    void first_run() {
        if (!term::interactive) {
            term::line(paint(term::yellow, "  No model is running.") + paint(term::dim, " /models lists the models on this computer; /models setup finds one to download."));
            return;
        }
        try {
            const Preferences preferences = Preferences::load();
            std::error_code failure;
            if (!preferences.last_model_path.empty() && fs::exists(preferences.last_model_path, failure) && !llama::find().empty()) {
                const fs::path file = preferences.last_model_path;
                const LocalModel model{file.stem().string(), file.string(), static_cast<int64_t>(fs::file_size(fs::canonical(file, failure), failure)), "Last used"};
                term::line(paint(term::dim, "  Nothing is answering yet, so Mason is starting the model you used last."));
                if (start(model, preferences.last_context)) return;
            }
            listed_ = busy<std::vector<LocalModel>>("Looking for models on this computer", [](const auto&, const CancelToken&) { return scan_models(); });
            std::vector<LocalModel> choices;
            for (const LocalModel& model : listed_) {
                if (model.startable() && choices.size() < 12) choices.push_back(model);
            }
            term::line();
            if (choices.empty()) {
                term::line("  Nothing is answering and no coding model was found on this computer.");
                if (confirm("Find one that fits this computer and download it?")) setup();
                else term::line(paint(term::dim, "  Later: /models setup, /models search TEXT, or start your own server and type /model."));
                return;
            }
            term::line("  Nothing is answering yet. Coding models on this computer:");
            for (size_t i = 0; i < choices.size(); i++) {
                term::line("  " + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + paint(term::bold, term::clip(choices[i].name, 60)) + paint(term::dim, "  " + byte_text(choices[i].bytes)));
            }
            term::write("  Start which? Type a number, or press Enter to skip: ");
            const int number = std::atoi(trim(read_line().value_or("")).c_str());
            if (number >= 1 && static_cast<size_t>(number) <= choices.size()) start(choices[static_cast<size_t>(number) - 1]);
            else term::line(paint(term::dim, "  Later: /models lists them · /models setup suggests a download · /model switches to a running server."));
        } catch (const Cancelled&) {
            term::line(paint(term::yellow, "  Cancelled.") + paint(term::dim, " /models continues when you are ready."));
        } catch (const std::exception& error) {
            term::line(paint(term::red, std::string("  ✗ ") + error.what()));
        }
    }
};

const char* const kUsage =
    "mason [--workspace PATH] [--resume] [--endpoint URL --model ID] [--read-only | --review | --edits | --auto]\n"
    "  Opens an interactive session in the current folder. No options are needed: mason uses a model that is\n"
    "  already running, restarts the one you used last, or helps you choose or download one.\n"
    "  New sessions ask before each edit and command (--review).\n";

}  // namespace

int main(int argc, char** argv) {
    std::vector<std::string> arguments(argv + 1, argv + argc);
    if (!arguments.empty() && arguments[0] == "chat") arguments.erase(arguments.begin());
    std::string workspace_path, endpoint_url, model_id;
    std::optional<Permission> permission;
    bool resume = false;
    for (size_t i = 0; i < arguments.size(); i++) {
        const std::string& name = arguments[i];
        if (name == "--help" || name == "-h") { std::cout << kUsage; return 0; }
        if (name == "--version") { std::cout << "mason " MASON_VERSION "\n"; return 0; }
        if (name == "--auto") permission = Permission::Auto;
        else if (name == "--edits") permission = Permission::WorkspaceEdits;
        else if (name == "--review") permission = Permission::ReviewEdits;
        else if (name == "--read-only") permission = Permission::ReadOnly;
        else if (name == "--resume") resume = true;
        else if ((name == "--workspace" || name == "--endpoint" || name == "--model") && i + 1 < arguments.size()) {
            (name == "--workspace" ? workspace_path : name == "--endpoint" ? endpoint_url : model_id) = arguments[++i];
        } else {
            std::cerr << "Unknown option " << name << ", or it needs a value. Run mason --help.\n";
            return 1;
        }
    }
    try {
        if (endpoint_url.empty() != model_id.empty()) throw Error("Use --endpoint and --model together.");
        Workspace workspace(workspace_path.empty() ? fs::current_path().string() : workspace_path);
        SessionStore store;
        std::optional<Session> session;
        for (const Session& item : store.sessions_in(workspace.root())) {
            if (item.turns.empty() != resume) { session = item; break; }
        }
        if (resume && !session) throw Error("No saved session to resume in this folder.");
        if (!session) session = store.create(workspace.root());

        struct sigaction interrupt {};
        interrupt.sa_handler = on_interrupt;  // no SA_RESTART: a blocked read returns, so the prompt can react
        sigaction(SIGINT, &interrupt, nullptr);
        struct sigaction ending {};
        ending.sa_handler = on_ending;
        sigaction(SIGTERM, &ending, nullptr);
        sigaction(SIGHUP, &ending, nullptr);
        std::signal(SIGPIPE, SIG_IGN);

        Cli cli(store, workspace, *session);
        if (permission) cli.set_permission(*permission);
        if (!endpoint_url.empty()) {
            cli.endpoint = ModelEndpoint{validated_endpoint(endpoint_url), model_id, ""};
            cli.endpoint_name = model_id;
        }
        cli.loop(resume);
        cli.shutdown();
        return 0;
    } catch (const std::exception& error) {
        term::raw(false);
        std::cerr << error.what() << "\n";
        return 1;
    }
}
