// Scheduled requests: things Mason starts by itself at set times.
#pragma once
#include <ctime>

#include "agent.hpp"

namespace mason {

/// Schedules live in `schedules.json` in Mason's data folder, never in a project. The app checks them while it is
/// open, and `mason --run-due` runs whatever is due from a terminal or a system timer. A run missed while nothing
/// was checking starts once at the next check, never repeatedly.
struct Schedule {
    enum class Cadence { Hourly, Daily, Weekdays, Weekly };

    std::string id = new_id();
    std::string name, workspace, prompt;
    /// The model server to use. When it is not answering at run time, the run is recorded as skipped.
    ModelEndpoint endpoint;
    /// Auto: edits apply and commands run sandboxed. Otherwise the run is read-only. Nothing is ever approved for it.
    bool automatic = false;
    Cadence cadence = Cadence::Daily;
    int hour = 9, minute = 0;
    int weekday = 1;  ///< 0 is Sunday. Only used by a weekly schedule.
    bool enabled = true;
    /// When the schedule was made or last switched on. Run times are counted from here or from the last run, whichever is later.
    std::time_t created = std::time(nullptr);
    std::time_t last_run = 0;  ///< 0 when it has not run.
    std::string last_session, last_outcome;

    /// The first run time strictly after `after`, in local time.
    std::time_t next(std::time_t after) const;
    std::string summary() const;
};

namespace schedules {

constexpr size_t kLimit = 32;
extern const char* const kUsage;

std::vector<Schedule> load(const fs::path& home);
void save(const std::vector<Schedule>& schedules, const fs::path& home);
/// Enabled schedules whose next run after their last run (or creation) has come.
std::vector<Schedule> due(const std::vector<Schedule>& schedules, std::time_t now = std::time(nullptr));
/// Runs one schedule now as its own session, with nobody to ask: every approval is declined. Auto runs edits and
/// sandboxed commands; otherwise the run is read-only. The schedule's last run and outcome are saved.
/// Returns the session, or nothing when the run was skipped. `model` replaces the schedule's endpoint in tests.
std::optional<Session> run(const Schedule& schedule, const fs::path& home, SessionStore& store, ChatModel* model = nullptr, const CancelToken& cancel = nullptr);
/// The /schedule command, shared by the command line and the app: everything except "run". Returns what to tell the user.
/// `workspace` and `endpoint` are where a new schedule will work and which model it will use.
std::string command(const std::string& arguments, const fs::path& home, const std::string& workspace, const std::optional<ModelEndpoint>& endpoint, std::time_t now = std::time(nullptr));

}  // namespace schedules

}  // namespace mason
