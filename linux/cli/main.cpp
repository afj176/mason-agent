// The interactive mason session for Linux. It needs no options: it finds a running model, restarts the one
// used last, offers the coding models on this computer, or suggests a download that fits.
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <csignal>
#include <cstdlib>
#include <future>
#include <map>
#include <iostream>
#include <mutex>
#include <thread>

#include "generation.hpp"
#include "notes.hpp"
#include "language.hpp"
#include "agent.hpp"
#include "derived.hpp"
#include "transfer.hpp"
#include "acceptance.hpp"
#include "planning.hpp"
#include "schedules.hpp"
#include "script.hpp"
#include "experiments.hpp"
#include "delivery.hpp"
#include "background.hpp"
#include "roles.hpp"
#include "bundles.hpp"
#include "models.hpp"
#include "model_memory.hpp"
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

/// /theme and "mason theme": lists the families, or saves one to appearance.json for the CLI and the app.
void theme_command(const std::string& asked) {
    const std::string name = trim(asked);
    if (!name.empty()) {
        const theme::Family* found = theme::try_find(name);
        if (!found) {
            std::vector<std::string> slugs;
            for (const theme::Family& item : theme::families()) slugs.push_back(item.slug());
            term::line(paint(term::red, "  No theme called " + name + ". ") + "Choose one of: " + join(slugs, ", ") + ".");
            return;
        }
        theme::save(data_home(), *found);
        term::use_theme(*found);
        term::line("  " + paint(std::string(term::accent) + term::bold, "✓") + " Theme set to " + paint(term::accent, found->name) + paint(term::dim, ". Mason Agent uses it the next time it starts."));
        return;
    }
    term::line();
    for (const theme::Family& item : theme::families()) {
        const bool current = &item == term::family;
        const std::string swatch = term::styled ? "\x1b[38;2;" + term::rgb_code(item.dark.accent) + "m■" + term::reset + " \x1b[38;2;" + term::rgb_code(item.light.accent) + "m■" + term::reset : "";
        term::line("  " + (current ? paint(std::string(term::accent) + term::bold, "›") : std::string(" ")) + " " + swatch + " " + term::pad(item.slug(), 12) + (current ? paint(term::dim, "current") : ""));
    }
    term::line(paint(term::dim, "  /theme NAME chooses one. It is shared with Mason Agent's Appearance setting."));
}

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
            case AgentEvent::Kind::FollowUp: started.push_back(event.text); live.clear(); break;
        }
    }
    /// Follow-ups that have begun since the last look, so the terminal can say so.
    std::vector<std::string> started;
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

    void remember(const ModelEndpoint& chosen, const std::string& name) { use(chosen, name); }

    void set_permission(Permission permission) {
        session_.permission = permission;
        session_.ask_only = permission == Permission::ReadOnly;
        store_.save(session_);
    }

    void loop(bool resumed) {
        if (!endpoint) find_running_model();
        std::vector<std::string> lines = {
            paint(std::string(term::bold) + term::accent, "Mason Agent") + paint(term::dim, "  ·  " + workspace_.root()),
            paint(term::dim, "model ") + (endpoint ? endpoint_name : paint(term::yellow, "none running")) + paint(term::dim, "  ·  " + mode_name() + "  ·  /help for commands"),
        };
        if (resumed) lines.push_back(paint(term::dim, "resumed ") + session_.title + paint(term::dim, "  ·  " + std::to_string(session_.turns.size()) + " requests  ·  " + session_.state));
        term::line();
        // In a wide colour terminal the app icon sits at the left with the session lines beside its middle.
        const std::vector<std::string> picture = term::picture();
        if (picture.empty()) {
            for (const std::string& text : lines) term::line("  " + text);
        } else {
            const int first = (static_cast<int>(picture.size()) - static_cast<int>(lines.size())) / 2;
            for (int row = 0; row < static_cast<int>(picture.size()); row++) {
                const int at = row - first;
                term::line("  " + picture[row] + (at >= 0 && at < static_cast<int>(lines.size()) ? "   " + lines[at] : ""));
            }
        }
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
    /// Memory needs read from each listed model's metadata, and the last memory reading.
    std::map<std::string, MemoryProfile> profiles_;
    MemoryReading memory_;
    /// What was typed to leave the model list, kept as the start of the next request.
    std::string pending_input_;
    /// The high estimate for the running model, for the line above the prompt; 0 when not estimated.
    double run_gib_ = 0;

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
            // While this session's model runs, its status sits above the prompt.
            if (text.empty()) {
                if (const std::string footer = running_footer(); !footer.empty()) term::line(footer);
            }
            // A key typed to leave the model list starts the next request.
            const std::string typed = text.empty() ? pending_input_ : "";
            pending_input_.clear();
            term::write((text.empty() ? prompt() : paint(term::dim, "  … ")) + typed);
            std::optional<std::string> line = read_line();
            if (line) line = typed + *line;
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
        // With tracing on, every call to the model is written down.
        const std::unique_ptr<RoleModel> traced = roles::implementer(model, &workspace_, store_.home());
        Agent agent(store_, traced ? static_cast<ChatModel&>(*traced) : model, [&](const ApprovalRequest& request) { return feed.ask(request); }, [&](const AgentEvent& event) { feed.push(event); });
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
        // What the user types while Mason works: Enter queues it for after this request; a leading ! steers instead.
        std::string typed;
        while (true) {
            std::vector<Activity> finished;
            std::vector<std::string> begun;
            const ApprovalRequest* asking = nullptr;
            bool done;
            {
                std::lock_guard<std::mutex> guard(feed.lock);
                finished.swap(feed.finished);
                begun.swap(feed.started);
                if (feed.asking && !feed.answer) asking = feed.asking;
                done = feed.done;
            }
            for (const Activity& activity : finished) show(activity);
            for (const std::string& text : begun) {
                term::set_status("");
                term::line();
                term::line(paint(term::accent, "  ▸ ") + term::clip(replace_all(text, "\n", " "), std::max(20, term::width() - 8)));
            }
            if (done) break;
            if (asking) {
                term::set_status("");
                const bool allow = approve(*asking, cancel);
                { std::lock_guard<std::mutex> guard(feed.lock); feed.answer = allow; }
                feed.answered.notify_all();
                continue;
            }
            const int pressed = term::key();
            if (pressed == 3 || (pressed == 27 && term::was_escape_alone())) {
                cancel->store(true);
            } else if (!term::interactive || pressed < 0) {
            } else if (pressed == 13 || pressed == 10) {
                const std::string line = trim(typed);
                typed.clear();
                if (!line.empty()) {
                    const bool steer = line.front() == '!';
                    const std::string text = trim(steer ? line.substr(1) : line);
                    term::set_status("");
                    try {
                        agent.send_while_running(text, steer);
                        term::line(paint(term::dim, steer ? "  ↪ steering: " : "  ＋ queued: ") + term::clip(text, std::max(20, term::width() - 18)));
                    } catch (const std::exception& error) {
                        term::line(paint(term::yellow, std::string("  ") + error.what()));
                    }
                }
            } else if (pressed == 127 || pressed == 8) {
                // Remove one whole character, which may be several bytes.
                while (!typed.empty()) {
                    const unsigned char last = static_cast<unsigned char>(typed.back());
                    typed.pop_back();
                    if ((last & 0xC0) != 0x80) break;
                }
            } else if (pressed >= 32) {
                typed += static_cast<char>(pressed);
            }
            if (cancel->load() && !stopping) { stopping = true; term::line(paint(term::dim, "  Stopping…")); }
            const std::string doing = feed.doing();
            const int room = std::max(20, term::width() - 36);
            const long seconds = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - started).count();
            if (typed.empty()) {
                term::set_status(std::string("  ") + kSpinner[frame++ % 10] + " " + (static_cast<int>(utf8_length(doing)) > room ? "…" + utf8_suffix(doing, static_cast<size_t>(room - 1)) : doing) +
                                 "  ·  " + std::to_string(seconds) + "s  ·  Esc to stop · type to queue");
            } else {
                term::set_status(std::string("  ") + kSpinner[frame++ % 10] + " " + paint(term::dim, "Enter queues · start with ! to steer  › ") +
                                 utf8_suffix(typed, static_cast<size_t>(std::max(10, term::width() - 50))));
            }
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
                const std::string mark = paint(term::accent, change.deleted() ? "D" : change.created() ? "A" : "M");
                const std::string source = change.by_command ? paint(term::dim, "  by a command") : "";
                if (change.opaque()) { term::line("  " + mark + " " + change.path + "  " + paint(term::dim, "binary or large file") + source); continue; }
                const DiffCounts counts = diff_counts(change.before, change.deleted() ? "" : change.after);
                term::line("  " + mark + " " + change.path + "  " + paint(term::green, "+" + std::to_string(counts.added)) + " " + paint(term::red, "−" + std::to_string(counts.removed)) + source);
            }
            term::line(paint(term::dim, "  /diff shows the changes · /undo puts the files back"));
        }
        term::line();
        const std::string state = session_.state == "Completed" ? paint(term::green, "✓ Completed") : session_.state == "Stopped" ? paint(term::yellow, "■ Stopped")
                                : session_.state == "Failed" ? paint(term::red, "✗ Failed") : paint(term::yellow, "● " + session_.state);
        const size_t steps = session_.activities.size() - first_activity;
        char elapsed[32];
        if (seconds < 90) std::snprintf(elapsed, sizeof elapsed, "%ds", static_cast<int>(seconds));
        else std::snprintf(elapsed, sizeof elapsed, "%.1f min", seconds / 60);
        if (turn && turn->checks) {
            // What Mason found when it ran the checks it worked out from the request.
            const DerivedReceipt& receipt = *turn->checks;
            const bool ran = std::any_of(receipt.results.begin(), receipt.results.end(), [](const DerivedResult& result) { return result.state != "notRun"; });
            const bool good = derived::passed(receipt);
            term::line("  " + paint(!ran ? term::yellow : good ? term::green : term::red, std::string(!ran ? "· " : good ? "✓ " : "✗ ") + derived::summary(receipt)));
            for (const DerivedResult& result : receipt.results) {
                if (result.state == "failed") term::line(paint(term::dim, "    " + derived::description(result.check) + ": " + result.detail));
            }
        }
        if (turn && turn->verification) {
            // What Mason found when it ran the project's verify workflow itself.
            const Verification& receipt = *turn->verification;
            const bool good = receipt.verdict == "passed" || receipt.verdict == "fixed";
            const bool quiet = receipt.verdict == "notRun" || receipt.verdict == "failingBefore";
            term::line("  " + paint(good ? term::green : quiet ? term::yellow : term::red, std::string(good ? "✓ " : quiet ? "· " : "✗ ") + "Verified by Mason: " + receipt.summary()) +
                       (receipt.repairs > 0 ? paint(term::dim, "  ·  after " + std::to_string(receipt.repairs) + (receipt.repairs == 1 ? " repair" : " repairs")) : ""));
            if (!good) {
                const std::vector<std::string> lines = split(receipt.output, '\n', false);
                const size_t shown = receipt.verdict == "notRun" ? 2 : 6;
                for (size_t i = lines.size() > shown ? lines.size() - shown : 0; i < lines.size(); i++) term::line(paint(term::dim, "    │ " + term::clip(lines[i], term::width() - 8)));
            }
            term::line();
        }
        std::string facts = std::to_string(steps) + " tool call" + (steps == 1 ? "" : "s") + "  ·  " + elapsed;
        if (session_.prompt_tokens > 0) facts += "  ·  " + std::to_string(session_.prompt_tokens) + " tokens of context";
        if (!session_.pending_input.empty()) facts += "  ·  " + std::to_string(session_.pending_input.size()) + " queued, /queue runs them";
        term::line("  " + state + paint(term::dim, "  ·  " + facts));
        if (!session_.detail.empty()) term::line("  " + paint(term::dim, session_.detail));
        if (turn && !session_.suggested_note.empty()) suggested_note();
    }

    /// Mason suggested a note with this answer: y keeps it, n dismisses it, Enter leaves it undecided and unsaved.
    void suggested_note() {
        const std::string text = session_.suggested_note;
        term::line();
        term::line("  " + paint(std::string(term::accent) + term::bold, "Remember this for next time?"));
        term::line("  “" + text + "”");
        if (!term::interactive) return;
        term::write("  " + paint(term::accent, "y") + " remember   " + paint(term::accent, "n") + " dismiss   " + paint(term::dim, "Enter to skip") + "  › ");
        const std::string answer = to_lower(trim(read_line().value_or("")));
        if (answer != "y" && answer != "yes" && answer != "n" && answer != "no") return;
        try {
            if (answer[0] == 'y') {
                project_notes::add(text, "", workspace_, store_.home());
                term::line(paint(term::dim, "  Kept. Mason reads it with every request in this project."));
            } else {
                project_notes::dismiss(text, workspace_.root(), store_.home());
                term::line(paint(term::dim, "  Dismissed. Mason will not suggest it again."));
            }
            session_.suggested_note.clear();
            store_.save(session_);
        } catch (const std::exception& error) {
            term::line(paint(term::yellow, std::string("  ") + error.what()));
        }
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
        if (name == "/theme") { theme_command(rest); return true; }
        if (name == "/diff" || name == "/changes") { diff(rest); return true; }
        if (name == "/undo" || name == "/redo") {
            const bool redo = name == "/redo";
            const std::optional<UndoResult> result = Agent::undo_latest(session_, store_, redo);
            if (!result) { term::line(paint(term::dim, redo ? "  Nothing to redo." : "  Nothing to undo in this session.")); return true; }
            term::line(paint(term::green, "  ✓ " + std::to_string(result->restored.size()) + " file" + (result->restored.size() == 1 ? "" : "s") + (redo ? " reapplied" : " put back")) +
                       (result->skipped.empty() ? "" : paint(term::yellow, "  ·  changed since, left alone: " + join(result->skipped, ", "))));
            return true;
        }
        if (name == "/rules") { rules(rest); return true; }
        if (name == "/compact") { compact(); return true; }
        if (name == "/agent" || name == "/agents") { agent(rest); return true; }
        if (name == "/commands") { commands(); return true; }
        if (name == "/queue") {
            if (session_.pending_input.empty()) term::line(paint(term::dim, "  Nothing is queued. While Mason works, type a message and press Enter to queue it, or start it with ! to steer."));
            else send("/queue");
            return true;
        }
        if (name == "/schedule" || name == "/schedules") {
            if (starts_with(to_lower(rest), "run ")) {
                const std::string wanted = to_lower(trim(rest.substr(4)));
                std::optional<Schedule> found;
                for (const Schedule& item : schedules::load(store_.home())) {
                    if (to_lower(item.name) == wanted) found = item;
                }
                if (!found) throw Error("No schedule named " + wanted + ". /schedule lists them.");
                term::line(paint(term::dim, "  Running " + found->name + " now, unattended: approvals are declined…"));
                schedules::run(*found, store_.home(), store_);
                std::string outcome = "finished";
                for (const Schedule& item : schedules::load(store_.home())) {
                    if (item.id == found->id) outcome = item.last_outcome;
                }
                term::line("  " + outcome + paint(term::dim, "  ·  /resume shows its session"));
            } else {
                for (const std::string& line : split(schedules::command(rest, store_.home(), workspace_.root(), endpoint), '\n')) term::line("  " + line);
            }
            return true;
        }
        if (name == "/experiment" || name == "/experiments") {
            for (const std::string& line : split(experiments::command(rest, workspace_, store_.home()), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/bundle" || name == "/bundles") {
            for (const std::string& line : split(bundles::command(rest, store_.home()), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/languages" || name == "/language") {
            if (starts_with(trim(rest), "install")) term::line("  Installing. This can take a few minutes.");
            for (const std::string& line : split(language_servers::command(rest, &workspace_, store_.home()), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/notes" || name == "/note") {
            const std::string given = project_notes::arguments(name, rest);
            if (given == "clear") {
                const size_t count = project_notes::load(store_.home(), workspace_.root()).size();
                if (count > 0) {
                    if (!term::interactive) { term::line("  Not cleared: there is no terminal to ask."); return true; }
                    term::write("  Clear all " + std::to_string(count) + (count == 1 ? " note" : " notes") + "? [y/N] ");
                    const std::string answer = to_lower(trim(read_line().value_or("")));
                    if (answer != "y" && answer != "yes") { term::line(paint(term::dim, "  Kept them.")); return true; }
                }
            }
            const std::vector<std::string> lines = split(project_notes::command(given, workspace_, store_.home()), '\n');
            const bool list = !lines.empty() && lines.front() == project_notes::kListTitle;
            for (size_t i = 0; i < lines.size(); i++) {
                // The list: its title in bold, each note's time and file dim under it, and the hint dim at the end.
                if (list && i == 0) term::line("  " + paint(term::bold, lines[i]));
                else if (list && (starts_with(lines[i], " ") || i + 1 == lines.size())) term::line("  " + paint(term::dim, lines[i]));
                else term::line("  " + lines[i]);
            }
            return true;
        }
        if (name == "/generation") {
            for (const std::string& line : split(generation::command(rest, endpoint, store_.home()), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/policy") {
            for (const std::string& line : split(policies::command(rest, workspace_, store_.home()), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/network") {
            for (const std::string& line : split(network_settings::session_command(rest, session_, store_.home()), '\n')) term::line("  " + line);
            store_.save(session_);
            return true;
        }
        if (name == "/roles" || name == "/role") {
            for (const std::string& line : split(roles::command(rest, store_.home()), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/task" || name == "/tasks") {
            const std::string said = background::command(rest, workspace_, endpoint, session_.permission == Permission::Auto && !session_.ask_only, background::own_program(), store_.home());
            for (const std::string& line : split(said, '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/deliver") {
            const delivery::Said said = delivery::command(rest, workspace_, store_.home(), derived::passed_receipt(session_));
            for (const std::string& line : split(said.text, '\n')) term::line("  " + line);
            if (!said.request.empty()) send(said.request);
            return true;
        }
        if (name == "/comment" || name == "/comments") {
            if (to_lower(rest) == "send") {
                if (session_.comments.empty()) term::line(paint(term::dim, "  No comments are waiting."));
                else send(" ");
            } else {
                for (const std::string& line : split(review::command(rest, workspace_, session_), '\n')) term::line("  " + line);
                store_.save(session_);
            }
            return true;
        }
        if (name == "/milestone" || name == "/milestones") {
            // A check can run a workflow, so it shows progress and can be stopped.
            const std::string said = starts_with(to_lower(rest), "check")
                ? busy<std::string>("Checking the milestone", [&](const std::function<void(const std::string&)>&, const CancelToken& cancel) { return milestones::command(rest, workspace_, session_, store_.home(), cancel); })
                : milestones::command(rest, workspace_, session_, store_.home());
            for (const std::string& line : split(said, '\n')) term::line("  " + line);
            store_.save(session_);
            return true;
        }
        if (name == "/accept" || name == "/acceptance") {
            const std::vector<AcceptanceResult> results = busy<std::vector<AcceptanceResult>>("Running the acceptance scenarios", [&](const auto& progress, const CancelToken& cancel) {
                return acceptance::run(workspace_, acceptance::read(workspace_), progress, cancel);
            });
            for (const std::string& line : split(acceptance::receipt(results), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/review") {
            if (!endpoint) throw Error("No model is running. /models lists the ones on this computer.");
            OpenAIClient model(*endpoint);
            const std::unique_ptr<RoleModel> reviewer = roles::reviewer(model, &workspace_, store_.home());
            const ReviewFindings found = busy<ReviewFindings>("Reviewing what this session changed", [&](const auto&, const CancelToken& cancel) { return review::findings(session_, workspace_, *reviewer, cancel); });
            for (const ReviewComment& item : found.comments) {
                if (session_.comments.size() < review::kLimit) session_.comments.push_back(item);
            }
            store_.save(session_);
            for (const std::string& line : split(review::summary(found), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/fork") {
            const std::vector<std::string> words = split(rest, ' ', false);
            const int number = words.empty() ? 0 : std::atoi(words[0].c_str());
            if (number < 1) {
                for (size_t i = 0; i < session_.turns.size(); i++) term::line("  " + paint(term::dim, std::to_string(i + 1)) + "  " + term::clip(replace_all(session_.turns[i].request, "\n", " "), std::max(20, term::width() - 8)));
                for (const std::string& line : split(transfer::kHelp, '\n')) term::line(paint(term::dim, "  " + line));
                return true;
            }
            const SessionFork made = transfer::fork(session_, store_, static_cast<size_t>(number) - 1, words.size() > 1 && to_lower(words[1]) == "files");
            session_ = made.session;
            term::line("  " + session_.detail);
            term::line(paint(term::dim, "  The request was: ") + made.request);
            return true;
        }
        if (name == "/export") {
            const std::vector<std::string> words = split(rest, ' ', false);
            if (words.empty()) throw Error("Use /export FILE, or /export FILE sanitized.");
            const bool sanitized = words.size() > 1 && to_lower(words[1]) == "sanitized";
            const fs::path file = fs::absolute(starts_with(words[0], "~/") ? fs::path(home_dir()) / words[0].substr(2) : fs::path(words[0]));
            write_file_atomic(file, transfer::export_session(session_, sanitized));
            term::line(std::string("  Saved ") + (sanitized ? "a sanitized copy of " : "") + "this session to " + file.string());
            return true;
        }
        if (name == "/import") {
            if (rest.empty()) throw Error("Use /import FILE.");
            const fs::path file = starts_with(rest, "~/") ? fs::path(home_dir()) / rest.substr(2) : fs::path(rest);
            session_ = transfer::import_session(read_file(file), store_, workspace_.root());
            term::line("  " + session_.detail + paint(term::dim, "  ·  " + std::to_string(session_.turns.size()) + " requests"));
            return true;
        }
        if (name == "/window" || name == "/windows") {
            for (const std::string& line : split(desktop::command(rest, session_), '\n')) term::line("  " + line);
            store_.save(session_);
            return true;
        }
        if (name == "/embeddings") {
            for (const std::string& line : split(semantic::command(rest, store_.home()), '\n')) term::line("  " + line);
            return true;
        }
        if (name == "/mcp") { mcp_command(rest); return true; }
        if (name == "/model") { choose_model(rest); return true; }
        if (name == "/models") { models(rest); return true; }
        // A custom command from the project or the data folder runs as a request.
        if (customization::expand(customization::commands(store_.home(), session_.workspace), input)) { send(input); return true; }
        term::line(paint(term::yellow, "  Unknown command " + name + ".") + paint(term::dim, " /help lists them; /commands lists your own."));
        return true;
    }

    void help() {
        const std::pair<const char*, const char*> rows[] = {
            {"/new [request]", "Start a separate session, optionally with its first request"},
            {"/resume [number]", "List this folder's sessions, or continue one"},
            {"/ask · /build", "Answer questions only, or make changes"},
            {"/permission read|review|edits|auto", "What Mason may do without asking (also /review, /edits, /auto)"},
            {"/diff [path]", "Show what this session changed"},
            {"/undo · /redo", "Put back, or reapply, everything the latest request changed"},
            {"/compact", "Replace the conversation so far with a summary to free context"},
            {"/queue", "Run the messages you queued, after a stop or a failure"},
            {"/agent [name|none]", "List your custom agents, or use one for this session"},
            {"/commands", "List your custom slash commands"},
            {"/comment PATH:LINE NOTE", "Pin a note to a line, sent with your next request (see /comment help)"},
            {"/milestone", "A saved plan: milestones, the files each covers and how each is checked"},
            {"/bundle", "Install a folder of skills and documentation for every project to use (see /bundle help)"},
            {"/languages", "The languages in this project and the language server that checks each; install one (see /languages help)"},
            {"/notes", "Notes kept for this project. Mason reads every note, up to 12 (see /notes help)"},
            {"/note TEXT", "Keep a note for this project"},
            {"/generation", "Temperature, top P and the like, kept for the model in use (see /generation help)"},
            {"/policy", "This project's limits: tools switched off, the workflows that may run, and whether its hooks run (see /policy help)"},
            {"/network", "In Auto: no network for commands, or ask before one reaches a host over HTTPS (/network ask, /network off)"},
            {"/roles", "A separate reviewer model, a separate model for pictures, and a trace of every model call (see /roles help)"},
            {"/task", "Run a request in the background; it keeps going after you close Mason (see /task help)"},
            {"/deliver", "Build one commit from the hunks you choose and open a draft pull request; bring CI results back (see /deliver help)"},
            {"/experiment", "Try something in a copy of the project in its own worktree, then apply the hunks you want (see /experiment help)"},
            {"/review", "Have the model read what this session changed and pin what it finds as comments"},
            {"/accept", "Run the scenarios in mason.acceptance.json in a real browser, at phone and desktop widths"},
            {"/fork NUMBER [files]", "A new session from before an earlier request; \"files\" also puts the project back"},
            {"/export FILE [sanitized] · /import FILE", "Save this session as a file, or add an exported one to this project"},
            {"/window [NUMBER|off]", "List open windows, or attach one so Mason can read and (with your OK) operate it"},
            {"/embeddings [URL MODEL]", "Choose an embeddings server so Mason can search code by meaning"},
            {"/schedule", "List scheduled requests; add, switch, remove or run one (see /schedule help)"},
            {"/mcp", "List your MCP servers and the tools Mason may call"},
            {"/mcp add NAME URL|PROGRAM [ARGS]", "Register a server on this computer, then choose its tools"},
            {"/mcp allow NAME TOOL…|all", "Choose which of a server's tools Mason may call"},
            {"/mcp tools NAME · on|off|remove NAME", "See what a server offers; switch it on or off; remove it"},
            {"/rules [revoke NUMBER]", "Saved always-allow rules for this project"},
            {"/model [number]", "List running model servers, or switch to one"},
            {"/models", "List the models on this computer and what each is for"},
            {"/models start NUMBER|NAME [16k] [cpu]", "Choose a context and start a coding model here; a size skips the picker"},
            {"/models stop", "Stop the model this session started"},
            {"/models start FOLDER", "Serve a folder of Transformers weights through Python (PyTorch)"},
            {"/models check NUMBER|FOLDER · python [PATH]", "Check a Transformers folder; choose the Python that serves them"},
            {"/models search TEXT", "Search Hugging Face"},
            {"/models download REPO [BUILD]", "List a repository's builds, or download one"},
            {"/models setup", "Suggest coding models that fit this computer and download one"},
            {"/models runtime", "Install or update llama.cpp"},
            {"/status", "Session, model, permission and storage"},
            {"/theme [name]", "List the theme families, or choose one (shared with Mason Agent)"},
            {"/exit", "Leave (Ctrl+C twice also works)"},
        };
        term::line();
        for (const auto& [name, text] : rows) term::line("  " + paint(term::accent, term::pad(name, 36)) + " " + paint(term::dim, text));
        term::line();
        term::line(paint(term::dim, "  While Mason works: Esc or Ctrl+C stops the run. Type a message and press Enter to queue it for afterwards,"));
        term::line(paint(term::dim, "  or start it with ! to interrupt and steer. End a line with \\ to continue on the next line."));
    }

    void compact() {
        if (session_.messages.size() <= 2) { term::line(paint(term::dim, "  There is nothing to compact yet.")); return; }
        if (!endpoint) throw Error("Compaction needs a running model. /models lists the ones on this computer.");
        term::line(paint(term::dim, "  Compacting the conversation… the original messages are kept."));
        const size_t before = session_.messages.size();
        OpenAIClient model(*endpoint);
        Session working = session_;
        if (!compaction::compact(working, model, 0)) { term::line(paint(term::dim, "  There is nothing to compact yet.")); return; }
        session_ = working;
        store_.save(session_);
        term::line(paint(term::green, "  ✓ " + std::to_string(before - 1) + " messages replaced by a summary") + paint(term::dim, "  ·  it travels with your next request"));
    }

    void agent(const std::string& rest) {
        const std::vector<Definition> agents = customization::agents(store_.home(), session_.workspace);
        if (rest.empty()) {
            if (agents.empty()) { term::line(paint(term::dim, "  No custom agents. Add a Markdown file to .mason/agents (or .claude/agents) in this project.")); return; }
            term::line();
            for (const Definition& item : agents) {
                const bool current = to_lower(item.name) == to_lower(session_.agent);
                term::line("  " + paint(current ? term::bold : term::accent, term::pad(item.name, 20)) + " " + paint(term::dim, term::clip(item.description, std::max(20, term::width() - 40))) +
                           (current ? paint(term::dim, "  ← current") : ""));
            }
            term::line(paint(term::dim, "  Use one with /agent NAME · /agent none goes back to the default."));
            return;
        }
        const std::string wanted = to_lower(rest);
        if (wanted == "none" || wanted == "default" || wanted == "off") {
            session_.agent.clear();
            store_.save(session_);
            term::line(paint(term::dim, "  Default agent."));
            return;
        }
        for (const Definition& item : agents) {
            if (to_lower(item.name) != wanted) continue;
            session_.agent = item.name;
            store_.save(session_);
            term::line("  Agent: " + paint(term::bold, item.name) + paint(term::dim, "  ·  " + item.source));
            return;
        }
        throw Error("No agent named " + rest + ". /agent lists them.");
    }

    void commands() {
        const std::vector<Definition> found = customization::commands(store_.home(), session_.workspace);
        if (found.empty()) { term::line(paint(term::dim, "  No custom commands. Add a Markdown file to .mason/commands (or .claude/commands) in this project.")); return; }
        term::line();
        for (const Definition& item : found) term::line("  " + paint(term::accent, term::pad("/" + item.name, 22)) + " " + paint(term::dim, term::clip(item.description, std::max(20, term::width() - 30))));
        term::line(paint(term::dim, "  Run one by typing it, with any arguments after the name."));
    }

    /// MCP servers are registered in Mason's data folder, for every project, and can only do what is allowed here.
    void mcp_command(const std::string& rest) {
        const std::vector<std::string> words = split(rest, ' ', false);
        std::vector<McpServer> servers = mcp::all(store_.home());
        auto index = [&](size_t word) -> size_t {
            const std::string wanted = word < words.size() ? to_lower(words[word]) : "";
            for (size_t i = 0; i < servers.size(); i++) {
                if (to_lower(servers[i].name) == wanted) return i;
            }
            throw Error("No MCP server named " + wanted + ". /mcp lists them.");
        };
        auto show_offered = [&](const McpServer& server) {
            try {
                const std::vector<McpTool> offered = mcp::discover(server);
                if (offered.empty()) { term::line(paint(term::dim, "  " + server.name + " offers no tools.")); return; }
                for (const McpTool& tool : offered) {
                    const bool allowed = server.allows_tool(tool.name);
                    term::line("    " + (allowed ? paint(term::green, "✓") : paint(term::dim, "·")) + " " + (allowed ? paint(term::bold, term::pad(tool.name, 22)) : term::pad(tool.name, 22)) + " " +
                               paint(term::dim, term::clip(tool.description, std::max(20, term::width() - 34))));
                }
                term::line(paint(term::dim, "  ✓ marks what Mason may call. Change it with /mcp allow " + server.name + " TOOL… or /mcp allow " + server.name + " all."));
            } catch (const std::exception& error) {
                term::line(paint(term::yellow, "  " + server.name + " did not answer: ") + error.what());
            }
        };
        const std::string verb = words.empty() ? "list" : to_lower(words[0]);
        if (verb == "list") {
            if (servers.empty()) {
                term::line(paint(term::dim, "  No MCP servers. Add one with /mcp add NAME http://127.0.0.1:PORT/mcp, or /mcp add NAME /full/path/to/program [arguments]."));
                return;
            }
            term::line();
            for (const McpServer& server : servers) {
                term::line("  " + paint(server.enabled ? term::accent : term::dim, term::pad(server.name, 16)) + " " + term::clip(server.transport(), std::max(20, term::width() - 44)) + (server.enabled ? "" : paint(term::dim, "  off")));
                term::line(paint(term::dim, "    " + (server.tools.empty() ? std::string("no tools allowed yet") : "may call: " + join(server.tools, ", "))));
            }
            term::line(paint(term::dim, "  Every call asks first unless a saved rule allows it. /mcp tools NAME shows what a server offers."));
        } else if (verb == "add") {
            if (words.size() < 3) throw Error("Use /mcp add NAME http://127.0.0.1:PORT/mcp, or /mcp add NAME /full/path/to/program [arguments].");
            for (const McpServer& existing : servers) {
                if (to_lower(existing.name) == to_lower(words[1])) throw Error("An MCP server named " + words[1] + " is already registered.");
            }
            McpServer server;
            server.name = words[1];
            if (words[2].front() == '/') {
                server.executable = words[2];
                server.arguments.assign(words.begin() + 3, words.end());
            } else {
                server.endpoint = words[2];
            }
            mcp::validate(server);
            servers.push_back(server);
            mcp::save(servers, store_.home());
            term::line("  Registered " + paint(term::bold, server.name) + paint(term::dim, "  ·  " + server.transport() + "  ·  no tools allowed yet"));
            show_offered(server);
        } else if (verb == "tools") {
            show_offered(servers[index(1)]);
        } else if (verb == "allow") {
            McpServer& server = servers[index(1)];
            const std::vector<std::string> wanted(words.begin() + 2, words.end());
            if (wanted.empty()) throw Error("Name the tools to allow, or use all (or none).");
            if (wanted == std::vector<std::string>{"none"}) {
                server.tools.clear();
            } else {
                std::vector<std::string> offered;
                for (const McpTool& tool : mcp::discover(server)) offered.push_back(tool.name);
                if (wanted == std::vector<std::string>{"all"}) {
                    server.tools = offered;
                } else {
                    for (const std::string& tool : wanted) {
                        if (std::find(offered.begin(), offered.end(), tool) == offered.end()) throw Error(server.name + " does not offer a tool named " + tool + ".");
                    }
                    server.tools = wanted;
                }
            }
            mcp::save(servers, store_.home());
            term::line("  " + server.name + " may call: " + (server.tools.empty() ? paint(term::dim, "nothing") : join(server.tools, ", ")));
        } else if (verb == "on" || verb == "off") {
            McpServer& server = servers[index(1)];
            server.enabled = verb == "on";
            mcp::save(servers, store_.home());
            term::line(paint(term::dim, "  " + server.name + " is " + verb + "."));
        } else if (verb == "remove") {
            const size_t at = index(1);
            const std::string removed = servers[at].name;
            servers.erase(servers.begin() + static_cast<std::ptrdiff_t>(at));
            mcp::save(servers, store_.home());
            term::line(paint(term::dim, "  Removed " + removed + "."));
        } else {
            throw Error("Use /mcp, /mcp add, /mcp tools, /mcp allow, /mcp on, /mcp off or /mcp remove.");
        }
    }

    void status() {
        auto row = [](const std::string& label, const std::string& value) { term::line("  " + paint(term::dim, term::pad(label, 12)) + " " + value); };
        term::line();
        row("Project", workspace_.root());
        row("Session", session_.title + paint(term::dim, "  " + session_.id.substr(0, 8) + " · " + session_.state));
        row("Model", endpoint ? endpoint_name + paint(term::dim, "  " + endpoint->url) : paint(term::yellow, "none running"));
        row("Mode", mode_name());
        row("Shell", shell::path() + (shell::sandbox_available() ? paint(term::dim, "  ·  sandbox available for Auto") : paint(term::yellow, "  ·  no sandbox: Auto refuses commands")));
        if (!session_.agent.empty()) row("Agent", session_.agent);
        if (session_.prompt_tokens > 0) row("Context", std::to_string(session_.prompt_tokens) + " tokens in the last request");
        if (session_.compactions > 0) row("Compacted", std::to_string(session_.compactions) + (session_.compactions == 1 ? " time; " : " times; ") + std::to_string(session_.archived.size()) + " messages archived");
        row("Stored in", store_.folder().string());
    }

    void diff(const std::string& filter) {
        std::vector<FileChange> merged;
        for (const Turn& turn : session_.turns) {
            if (turn.undone) continue;
            for (const FileChange& change : turn.changes) {
                if (!filter.empty() && !contains_ci(change.path, filter)) continue;
                auto existing = std::find_if(merged.begin(), merged.end(), [&](const FileChange& item) { return item.path == change.path; });
                if (existing != merged.end()) { existing->after = change.after; existing->after_blob = change.after_blob; existing->after_missing = change.after_missing; }
                else merged.push_back(change);
            }
        }
        if (merged.empty()) { term::line(paint(term::dim, filter.empty() ? "  This session has not changed any files." : "  No changed file matches " + filter + ".")); return; }
        int budget = 400;
        for (const FileChange& change : merged) {
            if (change.opaque()) {
                term::line();
                term::line("  " + paint(term::bold, change.path) + paint(term::dim, std::string("  binary or large file, ") + (change.deleted() ? "deleted" : change.created() ? "created" : "changed")));
                continue;
            }
            const std::string after = change.deleted() ? "" : change.after;
            const std::vector<DiffLine> rows = diff_lines(change.before, after);
            const DiffCounts counts = diff_counts(change.before, after);
            term::line();
            term::line("  " + paint(term::bold, change.path) + "  " + paint(term::green, "+" + std::to_string(counts.added)) + " " + paint(term::red, "−" + std::to_string(counts.removed)) +
                       (change.deleted() ? paint(term::dim, "  deleted") : change.created() ? paint(term::dim, "  new file") : "") + (change.by_command ? paint(term::dim, "  by a command") : ""));
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
        else if (verb == "python") {
            if (!argument.empty()) {
                if (access(argument.c_str(), X_OK) != 0) throw Error(argument + " is not a program.");
                Preferences preferences = Preferences::load();
                preferences.python = argument;
                preferences.save();
            }
            const std::string found = transformers::python();
            term::line(paint(term::dim, "  Transformers models run with ") + (found.empty() ? paint(term::yellow, "no python3 found") : found) + paint(term::dim, "  ·  it needs PyTorch and Transformers installed"));
        }
        else if (verb == "check") {
            if (listed_.empty()) listed_ = scan_models();
            const int number = std::atoi(argument.c_str());
            const LocalModel model = number >= 1 && std::to_string(number) == argument && static_cast<size_t>(number) <= listed_.size() ? listed_[static_cast<size_t>(number) - 1] : transformers::model_at(argument);
            if (!model.transformers) throw Error(model.name + " is a GGUF file; /models check is for Transformers folders.");
            const std::string said = busy<std::string>("Checking " + model.name, [&](const auto&, const CancelToken& cancel) { return transformers::check(model, false, cancel); });
            term::line(paint(term::green, "  ✓ ") + said);
        }
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

    // ---- step 1: the list ----

    static const char* tone_style(Tone tone) {
        switch (tone) {
            case Tone::Good: return term::green;
            case Tone::Tight: return term::yellow;
            case Tone::Over: return term::red;
            case Tone::Muted: break;
        }
        return term::dim;
    }

    static std::string agent_use(const LocalModel& model) {
        const std::string role = model.role();
        if (role == "vision") return "coding · vision";
        if (role == "projector") return "projector";
        if (role == "embeddings" || role == "reranker") return "code search";
        return "coding";
    }

    static std::string gib(double bytes) { return gib_text(bytes / kGiB) + " GiB"; }

    const MemoryProfile& profile_of(const LocalModel& model) {
        auto found = profiles_.find(model.path);
        if (found == profiles_.end()) found = profiles_.emplace(model.path, read_profile(model)).first;
        return found->second;
    }

    /// "llama.cpp b12716", "llama.cpp", or "llama.cpp not installed".
    static std::string runtime_name(const std::string& runtime) {
        if (runtime.empty()) return "llama.cpp not installed";
        for (const fs::path& part : fs::path(runtime)) {
            const std::string text = part.string();
            if (text.size() > 1 && text[0] == 'b' && std::all_of(text.begin() + 1, text.end(), [](unsigned char c) { return std::isdigit(c); })) return "llama.cpp " + text;
        }
        return "llama.cpp";
    }

    /// One row of the list, highlighted when chosen with the arrow keys.
    std::string model_row(size_t i, size_t name_width, bool highlighted) {
        const LocalModel& model = listed_[i];
        const FitSummary fit = summarize_fit(model, profile_of(model), memory_);
        std::string marks, plain_marks;
        for (const FitMark& mark : fit.marks) {
            marks += paint(tone_style(mark.tone), mark.symbol);
            plain_marks += mark.symbol;
        }
        const size_t mark_room = utf8_length(plain_marks) < 7 ? 7 - utf8_length(plain_marks) : 1;
        const bool running = server_ && server_->running() && server_->model.path == model.path;
        const std::string name = term::pad(term::clip(model.name, static_cast<int>(name_width)), name_width);
        const std::string shown_name = highlighted && term::styled ? paint(term::inverse, name) : model.startable() ? paint(term::bold, name) : name;
        return std::string(highlighted ? " ›" : "  ") + paint(term::dim, term::left_pad(std::to_string(i + 1), 2)) + "  " + shown_name + term::right_align(gib(static_cast<double>(model.bytes)), 9) + "  " +
               term::pad(agent_use(model), 18) + marks + std::string(mark_room, ' ') + paint(term::dim, fit.note) + (running ? paint(term::green, "  ● running here") : "");
    }

    void list_models() {
        struct Found {
            std::vector<LocalModel> models;
            std::map<std::string, MemoryProfile> profiles;
            MemoryReading memory;
            std::string runtime;
        };
        Found found = busy<Found>("Looking for models on this computer", [](const auto&, const CancelToken& cancel) {
            Found result;
            result.models = scan_models();
            for (const LocalModel& model : result.models) {
                check_cancel(cancel);
                result.profiles[model.path] = read_profile(model);
            }
            result.memory = read_memory();
            result.runtime = llama::find();
            return result;
        });
        listed_ = std::move(found.models);
        profiles_ = std::move(found.profiles);
        memory_ = found.memory;
        if (listed_.empty()) {
            term::line(paint(term::dim, "  No model files were found on this computer."));
            term::line(paint(term::dim, "  /models setup suggests one that fits and downloads it · /models search TEXT looks on Hugging Face."));
            return;
        }
        const std::string device = !memory_.usable() ? "no memory reading" : memory_.gpu ? short_device_name(memory_.device) : memory_.device;
        term::line("  " + paint(term::bold, "On this computer") + paint(term::dim, " · " + runtime_name(found.runtime) + " · " + device) +
                   (memory_.usable() ? paint(term::dim, " · ") + gib_text(memory_.free_gib()) + paint(term::dim, " of " + gib_text(memory_.total_gib()) + " GiB free") : ""));
        term::line();
        const size_t shown = std::min<size_t>(listed_.size(), 40);
        size_t name_width = 26;
        for (size_t i = 0; i < shown; i++) name_width = std::max(name_width, std::min<size_t>(utf8_length(listed_[i].name) + 1, 40));
        term::line(paint(term::dim, "   #  " + term::pad("Model", name_width) + term::right_align("Size", 9) + "  " + term::pad("Agent use", 18) + "GPU fit"));
        for (size_t i = 0; i < shown; i++) term::line(model_row(i, name_width, false));
        const int after = listed_.size() > shown ? 3 : 2;  // lines below the last row
        if (listed_.size() > shown) term::line(paint(term::dim, "      … and " + std::to_string(listed_.size() - shown) + " more files"));
        term::line();
        const bool choose = term::interactive && static_cast<int>(shown) + after + 2 < term::height();
        term::line(choose ? paint(term::dim, "  ↑↓ choose  ⏎ open  · or type ") + "/models start 1 16k" + paint(term::dim, " to skip the picker")
                          : paint(term::dim, "  Start one with ") + "/models start NUMBER [16k] [cpu]" + paint(term::dim, "; without a size it suggests one."));
        if (!choose) return;

        // Arrow keys move a highlighted row; Enter opens the picker for it; anything else returns to the prompt.
        size_t at = 0;
        while (at + 1 < shown && !listed_[at].startable()) at++;
        auto up_lines = [&](size_t row) { return static_cast<int>(shown - row) + after; };
        term::raw(true);
        term::rewrite_above(up_lines(at), model_row(at, name_width, true));
        bool open = false;
        while (!g_ending) {
            const int pressed = term::read_key();
            if (pressed == term::kNoKey) continue;
            if (pressed == term::kUp || pressed == term::kDown) {
                const size_t next = pressed == term::kUp ? (at == 0 ? 0 : at - 1) : std::min(shown - 1, at + 1);
                if (next == at) continue;
                term::rewrite_above(up_lines(at), model_row(at, name_width, false));
                at = next;
                term::rewrite_above(up_lines(at), model_row(at, name_width, true));
                continue;
            }
            if (pressed == term::kEnter) open = true;
            else if (pressed >= 32 && pressed < 127) pending_input_ = std::string(1, static_cast<char>(pressed));
            break;
        }
        term::rewrite_above(up_lines(at), model_row(at, name_width, false));
        term::raw(false);
        if (!open) return;
        const LocalModel model = listed_[at];
        if (!model.startable()) {
            term::line(paint(term::yellow, "  " + model.name + " cannot be started here: ") + model.verdict() + ".");
            return;
        }
        picker(model, false);
    }

    // ---- step 2: the picker ----

    std::vector<std::string> picker_lines(const LocalModel& model, const MemoryProfile& profile, const std::vector<int>& steps, size_t chosen, bool cpu, bool explain) {
        std::vector<std::string> lines = {"", "  " + paint(term::bold, model.name)};
        std::vector<std::string> facts;
        if (const std::string quant = quant_in_name(fs::path(model.path).filename().string()); !quant.empty()) facts.push_back(quant);
        facts.push_back(model.transformers ? "Transformers" : "GGUF");
        if (!profile.architecture.empty()) facts.push_back(profile.architecture);
        facts.push_back("max " + context_label(profile.context_cap()));
        facts.push_back(gib(static_cast<double>(model.bytes)) + " on disk");
        lines.push_back("  " + paint(term::dim, join(facts, " · ")));
        lines.push_back("");
        std::string labels = "  " + paint(term::dim, term::pad("Context", 9)), memory = "  " + paint(term::dim, term::pad("GiB", 9)), fits = "  " + paint(term::dim, term::pad("Fit", 9));
        for (size_t i = 0; i < steps.size(); i++) {
            const FitAssessment fit = assess(profile, memory_, steps[i]);
            std::string gib_cell = "—", mark = "?";
            if (memory_.usable() && fit.estimated) gib_cell = gib_text(fit.low_gib);
            else if (memory_.usable() && fit.weight_gib > 0) gib_cell = gib_text(fit.weight_gib) + "+";
            if (fit.fit == Fit::Likely) mark = "●";
            else if (fit.fit == Fit::Tight) mark = "◐";
            else if (fit.fit == Fit::TooLarge) mark = fit.lower_bound ? "✕" : "○";
            const char* tone = cpu ? term::dim : tone_style(fit.fit == Fit::TooLarge && !fit.lower_bound ? Tone::Muted : tone_of(fit.fit));
            auto cell = [&](const std::string& text, const char* style) {
                if (i != chosen) return paint(style, term::right_align(text, 7) + " ");
                return term::styled ? paint(term::selected, term::right_align(text, 7) + " ") : term::right_align("[" + text + "]", 8);
            };
            labels += cell(context_label(steps[i]), "");
            memory += cell(gib_cell, tone);
            fits += cell(mark, tone);
        }
        lines.insert(lines.end(), {labels, memory, fits, ""});
        const FitAssessment fit = assess(profile, memory_, steps.empty() ? 2048 : steps[chosen]);
        if (cpu) {
            lines.push_back("  " + term::pad("CPU", 9) + paint(term::dim, "runs from system RAM · not checked here"));
        } else if (!memory_.usable()) {
            lines.push_back("  " + term::pad("GPU", 9) + paint(term::dim, "no memory reading yet · fit is checked when it starts"));
        } else {
            // 30 cells over all the memory: other apps, the model and its cache, the margin up to the high estimate, free, and the spare kept at the end.
            const double total = memory_.total_gib(), model_gib = fit.estimated ? fit.low_gib : fit.weight_gib, margin = fit.estimated ? fit.high_gib - fit.low_gib : 0;
            auto cells = [&](double gib_value) { return std::max(0, static_cast<int>(std::lround(gib_value / total * 30))); };
            const int other = std::min(30, cells(total - memory_.free_gib()));
            const int body = std::min(30 - other, cells(model_gib));
            const int extra = std::min(30 - other - body, cells(margin));
            auto repeat = [](const char* glyph, int count) { std::string out; for (int i = 0; i < count; i++) out += glyph; return out; };
            const std::string bar = paint(term::dim, repeat(term::styled ? "█" : "▓", other)) + paint(term::accent, repeat("█", body)) + paint(term::yellow, repeat("▒", extra)) +
                                    paint(term::dim, repeat("░", 30 - other - body - extra) + "│");
            const std::string verdict = to_lower(fit.label());
            const std::string numbers = fit.estimated ? " · " + gib_text(fit.low_gib) + "–" + gib_text(fit.high_gib) + " of " + gib_text(memory_.free_gib()) + " GiB"
                                                      : " · weights " + gib_text(fit.weight_gib) + " GiB of " + gib_text(memory_.free_gib()) + " GiB free";
            lines.push_back("  " + term::pad("GPU", 9) + bar + "  " + paint(std::string(term::bold) + tone_style(tone_of(fit.fit)), verdict) + paint(term::dim, numbers));
            lines.push_back("  " + std::string(9, ' ') + paint(term::dim, "other apps  model + cache   margin  spare"));
        }
        lines.push_back("");
        lines.push_back("  " + paint(term::accent, "←→") + paint(term::dim, " context   ") + paint(term::accent, "g") + paint(term::dim, " GPU  ") + paint(term::accent, "c") + paint(term::dim, " CPU   ") +
                        paint(term::accent, "?") + paint(term::dim, " how this is estimated   ") + paint(term::accent, "⏎") + paint(term::dim, " start   ") + paint(term::accent, "esc") + paint(term::dim, " back"));
        if (explain) {
            lines.push_back("");
            std::string current;
            const size_t room = static_cast<size_t>(std::max(30, term::width() - 6));
            for (const std::string& word : split(fit_explanation(profile), ' ', false)) {
                if (!current.empty() && utf8_length(current) + 1 + utf8_length(word) > room) {
                    lines.push_back("  " + paint(term::dim, current));
                    current.clear();
                }
                current += (current.empty() ? "" : " ") + word;
            }
            if (!current.empty()) lines.push_back("  " + paint(term::dim, current));
        }
        return lines;
    }

    /// Chooses the context and GPU or CPU, redrawn in place, then starts. Without a terminal the suggestion is used.
    void picker(const LocalModel& model, bool cpu_asked) {
        const MemoryProfile profile = profile_of(model);
        memory_ = read_memory();
        const Recommendation suggestion = recommend(profile, memory_);
        if (!term::interactive) {
            term::line(paint(term::dim, "  " + suggestion.text));
            start(model, suggestion.context, cpu_asked || !suggestion.gpu);
            return;
        }
        const std::vector<int> steps = ladder_steps(profile);
        size_t chosen = 0;
        for (size_t i = 0; i < steps.size(); i++) {
            if (std::abs(steps[i] - suggestion.context) < std::abs(steps[chosen] - suggestion.context)) chosen = i;
        }
        bool cpu = cpu_asked || !suggestion.gpu, explain = false;
        int drawn = 0;
        term::raw(true);
        while (!g_ending) {
            term::draw(picker_lines(model, profile, steps, chosen, cpu, explain), drawn);
            int pressed = term::kNoKey;
            while (pressed == term::kNoKey && !g_ending) pressed = term::read_key();
            if (pressed == term::kLeft && chosen > 0) chosen--;
            else if (pressed == term::kRight && chosen + 1 < steps.size()) chosen++;
            else if (pressed == 'g' || pressed == 'G') cpu = false;
            else if (pressed == 'c' || pressed == 'C') cpu = true;
            else if (pressed == '?') explain = true;
            else if (pressed == term::kEnter) {
                term::raw(false);
                start(model, steps.empty() ? suggestion.context : steps[chosen], cpu);
                return;
            } else if (pressed == term::kEscape || pressed == 3 || pressed == 'q') break;
        }
        term::raw(false);
    }

    // ---- step 4: a typed size that may not fit ----

    /// Asks before starting at a size that is tight or too large on the GPU. Returns the context and device to use,
    /// or nothing to cancel.
    std::optional<std::pair<int, bool>> check_typed_size(const LocalModel& model, int context, bool cpu) {
        if (cpu) return std::make_pair(context, cpu);
        const MemoryProfile profile = profile_of(model);
        memory_ = read_memory();
        const FitAssessment fit = assess(profile, memory_, context);
        if (fit.fit != Fit::Tight && fit.fit != Fit::TooLarge) return std::make_pair(context, cpu);
        const std::string label = context_label(context);
        const std::string mark = fit.fit == Fit::Tight ? paint(term::yellow, "◐ Tight fit at " + label) : paint(term::red, "✕ Too large at " + label);
        const std::string spare = gib_text(fit.free_gib) + " GiB free, " + gib_text(fit.spare_gib) + " kept spare";
        term::line("  " + mark + paint(term::dim, fit.estimated ? " · " + gib_text(fit.low_gib) + "–" + gib_text(fit.high_gib) + " GiB needed, " + spare
                                                                : " · the weights alone are " + gib_text(fit.weight_gib) + " GiB, " + spare));
        const Recommendation suggestion = recommend(profile, memory_);
        const bool smaller = suggestion.gpu && suggestion.context != context && assess(profile, memory_, suggestion.context).fit == Fit::Likely;
        if (!term::interactive) {
            term::line(paint(term::dim, "    Starting anyway, as asked. There is no terminal to ask."));
            return std::make_pair(context, cpu);
        }
        term::write("    " + paint(term::accent, "y") + " start anyway   " +
                    (smaller ? paint(term::accent, "s") + " use " + context_label(suggestion.context) + " instead   " : paint(term::accent, "c") + " use CPU   ") +
                    paint(term::accent, "n") + " cancel  " + paint(term::accent, "› "));
        term::raw(true);
        int pressed = term::kNoKey;
        while (!g_ending) {
            pressed = term::read_key();
            if (pressed == 'y' || pressed == 'n' || pressed == term::kEscape || pressed == 3 || (smaller && pressed == 's') || (!smaller && pressed == 'c')) break;
        }
        term::raw(false);
        if (pressed == 'y') { term::line("start anyway"); return std::make_pair(context, false); }
        if (pressed == 's') { term::line("use " + context_label(suggestion.context)); return std::make_pair(suggestion.context, false); }
        if (pressed == 'c') { term::line("use CPU"); return std::make_pair(context, true); }
        term::line("cancel");
        return std::nullopt;
    }

    /// The line above the prompt while this session's model runs.
    std::string running_footer() const {
        if (!server_ || !server_->running()) return "";
        std::string facts = context_label(server_->context) + " · " + (server_->cpu_only ? "CPU" : "GPU") + (run_gib_ > 0 ? " · ~" + gib_text(run_gib_) + " GiB" : "");
        std::string host = server_->endpoint.url;
        if (starts_with(host, "http://")) host = host.substr(7);
        if (!host.empty() && host.back() == '/') host.pop_back();
        return "  " + paint(term::green, "●") + " " + server_->model.name + "  " + paint(term::dim, facts) + "  " + paint(term::dim, host) + "  " + paint(term::dim, "/models stop");
    }

    LocalModel named_folder_;

    void start_named(const std::string& argument) {
        std::vector<std::string> words = split(argument, ' ', false);
        bool cpu = false;
        int context = 0;
        for (size_t i = words.size(); i-- > 1;) {
            const std::optional<int> tokens = parse_context_size(words[i]);  // 16384, 16k or 16K
            if (to_lower(words[i]) == "cpu") { cpu = true; words.erase(words.begin() + static_cast<std::ptrdiff_t>(i)); }
            else if (tokens) { context = *tokens; words.erase(words.begin() + static_cast<std::ptrdiff_t>(i)); }
        }
        const std::string wanted = join(words, " ");
        if (wanted.empty()) throw Error("Use /models start NUMBER or NAME. /models lists them.");
        if (listed_.empty()) listed_ = scan_models();
        const LocalModel* model = nullptr;
        const int number = std::atoi(wanted.c_str());
        if (number >= 1 && std::to_string(number) == wanted && static_cast<size_t>(number) <= listed_.size()) model = &listed_[static_cast<size_t>(number) - 1];
        else if (starts_with(wanted, "/") || starts_with(wanted, "~")) {
            named_folder_ = transformers::model_at(wanted);
            model = &named_folder_;
        } else {
            for (const LocalModel& candidate : listed_) {
                if (candidate.startable() && contains_ci(candidate.name, wanted)) { model = &candidate; break; }
            }
        }
        if (!model) throw Error("No model on this computer matches \"" + wanted + "\". /models lists them.");
        if (!model->startable()) throw Error(model->name + " cannot be started here: " + model->verdict() + ".");
        const LocalModel chosen = *model;
        // No size: the picker, or the suggestion without a terminal. A typed size that may not fit asks first.
        if (context == 0) { picker(chosen, cpu); return; }
        const auto settled = check_typed_size(chosen, context, cpu);
        if (settled) start(chosen, settled->first, settled->second);
    }

    /// The start steps on one line: done ones ticked, the current one spinning (or crossed when it failed).
    static std::string stage_line(int current, bool done, bool failed, int frame, long seconds) {
        std::string painted = "  ", plain = "  ";
        for (int i = 0; i < 4; i++) {
            const std::string name = kStartStages[i];
            std::string piece;
            const char* style = term::dim;
            if (done || i < current) { piece = "✓ " + name; style = term::green; }
            else if (i == current) { piece = (failed ? "✗ " : std::string(kSpinner[frame % 10]) + " ") + name; style = failed ? term::red : term::accent; }
            else piece = "· " + name;
            painted += (i ? "   " : "") + paint(style, piece);
            plain += (i ? "   " : "") + piece;
        }
        if (done || failed) return painted;
        char clock[32];
        std::snprintf(clock, sizeof clock, "%ld:%02ld", seconds / 60, seconds % 60);
        const size_t length = utf8_length(plain);
        return painted + std::string(length < 58 ? 58 - length : 2, ' ') + paint(term::dim, std::string(clock) + " · esc cancels");
    }

    /// Starts a model and makes it this session's model, showing each start step in place.
    bool start(const LocalModel& chosen, int context = 0, bool cpu_only = false) {
        const LocalModel model = chosen;  // `listed_` may change while this runs
        // Transformers weights run through Python, not llama.cpp.
        std::string runtime = model.transformers ? "python" : llama::find();
        if (runtime.empty()) {
            term::line(paint(term::yellow, "  llama.cpp is not installed yet; it runs GGUF models."));
            runtime = install_runtime(false);
        }
        if (runtime.empty()) return false;
        server_.reset();
        const MemoryProfile profile = profile_of(model);
        if (context <= 0) {
            memory_ = read_memory();
            const Recommendation suggestion = recommend(profile, memory_);
            context = suggestion.context;
            cpu_only = cpu_only || !suggestion.gpu;
        }
        term::line("  " + paint(term::bold, "Starting " + model.name) + paint(term::dim, " · " + context_label(context) + " · " + (cpu_only ? "CPU" : "GPU")));
        const CancelToken cancel = make_cancel();
        std::atomic<int> stage{0};
        std::future<std::unique_ptr<ModelServer>> pending = std::async(std::launch::async, [&] {
            return ModelServer::start(model, runtime, context, cpu_only, nullptr, cancel, [&](int step) { stage = step; });
        });
        g_running_cancel = cancel.get();
        const bool live = term::interactive;
        if (live) term::raw(true);
        const auto began = std::chrono::steady_clock::now();
        int frame = 0;
        while (pending.wait_for(std::chrono::milliseconds(live ? 0 : 100)) != std::future_status::ready) {
            if (!live) continue;
            const int pressed = term::read_key();
            if (pressed == 3 || pressed == term::kEscape) cancel->store(true);
            const long seconds = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - began).count();
            term::overwrite(stage_line(stage, false, false, frame++, seconds));
        }
        g_running_cancel = nullptr;
        auto finish = [&](const std::string& text) {
            if (live) { term::overwrite(text); term::raw(false); term::line(); }
            else term::line(text);
        };
        try {
            server_ = pending.get();
        } catch (const Cancelled&) {
            finish(stage_line(stage, false, true, 0, 0));
            throw;
        } catch (const Error& error) {
            finish(stage_line(stage, false, true, 0, 0));
            term::line(paint(term::red, "  ✗ " + model.name + " did not start. ") + error.what());
            return false;
        }
        finish(stage_line(4, true, false, 0, 0));
        use(server_->endpoint, model.name);
        const FitAssessment fit = assess(profile, read_memory(), server_->context);
        run_gib_ = fit.estimated ? fit.high_gib : 0;
        Preferences preferences = Preferences::load();
        preferences.last_model_path = model.path;
        preferences.last_context = server_->context;
        preferences.last_cpu = server_->cpu_only;
        preferences.save();
        std::string host = server_->endpoint.url;
        if (starts_with(host, "http://")) host = host.substr(7);
        if (!host.empty() && host.back() == '/') host.pop_back();
        term::line("  " + paint(term::green, "✓ Ready") + paint(term::dim, " · " + host + " · ") + "this session now codes with " + model.name +
                   paint(term::dim, "  ·  stops when you leave mason"));
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
                if (start(model, preferences.last_context, preferences.last_cpu)) return;
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
    "  New sessions ask before each edit and command (--review).\n"
    "mason --run-due\n"
    "  Runs the scheduled requests that are due, unattended, and exits. For cron or a systemd timer; the app does this itself while open.\n"
    "mason theme [blueprint|evergreen|graphite|kiln|retro-neon]\n"
    "  Lists the theme families, or chooses one for the CLI and Mason Agent.\n";

}  // namespace

int main(int argc, char** argv) {
    // Inside the sandbox this program is started as the forwarder to Mason's network relay.
    if (const auto forwarded = network::maybe_forward(argc, argv)) return *forwarded;
    std::vector<std::string> arguments(argv + 1, argv + argc);
    // The accent follows the theme family chosen in Mason Agent or with "mason theme".
    term::use_theme(theme::saved(data_home()));
    if (!arguments.empty() && arguments[0] == "theme") {
        try {
            theme_command(join(std::vector<std::string>(arguments.begin() + 1, arguments.end()), " "));
            return 0;
        } catch (const std::exception& error) {
            std::cerr << error.what() << "\n";
            return 1;
        }
    }
    if (!arguments.empty() && arguments[0] == "chat") arguments.erase(arguments.begin());
    // A script operation prints JSON Lines and exits; everything else opens the interactive session.
    if (!arguments.empty() && script::names().count(arguments[0])) {
        return script::run(arguments, [](const std::string& line) { std::cout << line << std::endl; });
    }
    std::string workspace_path, endpoint_url, model_id;
    std::optional<Permission> permission;
    bool resume = false;
    bool run_due = false;
    for (size_t i = 0; i < arguments.size(); i++) {
        const std::string& name = arguments[i];
        if (name == "--help" || name == "-h") { std::cout << kUsage << script::kUsage; return 0; }
        if (name == "--version") { std::cout << "mason " MASON_VERSION "\n"; return 0; }
        if (name == "--auto") permission = Permission::Auto;
        else if (name == "--edits") permission = Permission::WorkspaceEdits;
        else if (name == "--review") permission = Permission::ReviewEdits;
        else if (name == "--read-only") permission = Permission::ReadOnly;
        else if (name == "--resume") resume = true;
        else if (name == "--run-due") run_due = true;
        else if ((name == "--workspace" || name == "--endpoint" || name == "--model") && i + 1 < arguments.size()) {
            (name == "--workspace" ? workspace_path : name == "--endpoint" ? endpoint_url : model_id) = arguments[++i];
        } else {
            std::cerr << "Unknown option " << name << ", or it needs a value. Run mason --help.\n";
            return 1;
        }
    }
    try {
        if (run_due) {
            // For a system timer (cron, systemd): run whatever is due, say what happened, and leave.
            SessionStore store;
            const std::vector<Schedule> due = schedules::due(schedules::load(store.home()));
            if (due.empty()) std::cout << "No schedule is due.\n";
            for (const Schedule& schedule : due) {
                std::cout << "Running " << schedule.name << " in " << schedule.workspace << "…" << std::endl;
                schedules::run(schedule, store.home(), store);
                for (const Schedule& item : schedules::load(store.home())) {
                    if (item.id == schedule.id) std::cout << "  " << item.last_outcome << std::endl;
                }
            }
            return 0;
        }
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
            // Remembered, so the next plain "mason" and the app use the same server while it is running.
            cli.remember(ModelEndpoint{validated_endpoint(endpoint_url), model_id, ""}, model_id);
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
