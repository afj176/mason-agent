// Scenes: consecutive tool calls of one kind, shown together as one step of a run in the app's Activity panel.
// The grouping, the labels a person reads, and what a command's output says (exit code, test counts, failing lines).
#pragma once
#include "session.hpp"

namespace mason::scenes {

/// What kind of work a run of tool calls was, which picks how the Activity panel shows it.
enum class Kind { Read, Edit, Command, Page, Other };

/// Consecutive calls of one kind. `first` is the index of the first call in the session's activities.
struct Scene {
    Kind kind = Kind::Other;
    size_t first = 0, count = 0;
    size_t last() const { return first + count - 1; }
    bool contains(size_t index) const { return index >= first && index <= last(); }
    bool operator==(const Scene&) const = default;
};

/// A command's result, read for the summary first: exit code, test counts and the lines that name failures.
struct CommandSummary {
    std::optional<int> exit_code, passed, failed;
    std::vector<std::string> failures;
    std::string output;      ///< The output without the "exit N" line Mason puts first.
    bool truncated = false;  ///< The output was clipped, by the shell or when it was kept with the session.
};

/// The longest run of calls kept in one scene; longer runs start a new scene so none grows without bound.
inline constexpr size_t kMaxSceneLength = 40;

Kind kind_of(const std::string& tool);
/// A call that looks for something (search_files, search_next, semantic_search, list_files) rather than reading one file.
bool is_search(const std::string& tool);
/// Groups consecutive calls of the same kind. Every call belongs to exactly one scene, in order.
std::vector<Scene> group(const std::vector<Activity>& activities);
/// A path written the same way each time: forward slashes, no leading "./" or "/".
std::string normalize(const std::string& path);
/// The project path a call worked on, normalised, or nothing when the call is not about one file.
std::optional<std::string> path_of(const Activity& activity);
/// The call failed or was declined. A skipped repeat is not a failure.
bool failed(const Activity& activity);
/// The command a call ran as a person would type it: run_command's quoted words unquoted, a workflow by its name.
std::string command_text(const Activity& activity);
/// A short label for a scene, written the way a person would say it: "Read 2 files, searched 1 time", "Edited app.py".
std::string label(const Scene& scene, const std::vector<Activity>& activities);
/// Reads a command call's result for its exit code, test counts and the lines that name failures.
CommandSummary command(const Activity& activity);
/// A command call that failed: its state, or an exit code other than 0.
bool command_failed(const Activity& activity);
/// How many calls touched each top folder ("src/") or top-level file, most first.
std::vector<std::pair<std::string, int>> folders(const std::vector<Activity>& activities);
/// Index of the first call of the latest request, so a run's summary covers only what that request did.
size_t request_start(const Session& session);

}  // namespace mason::scenes
