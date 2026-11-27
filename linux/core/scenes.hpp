// Scenes: consecutive tool calls of one kind, shown together as one step of a run in the app's Moments panel.
// The grouping, the labels a person reads, and what a command's output says (exit code, test counts, failing lines).
#pragma once
#include "session.hpp"

namespace mason::scenes {

/// What kind of work a run of tool calls was, which picks how Moments shows it.
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
/// The same, with a new scene started at each of `breaks` (call indices, such as where each request began), so no
/// scene spans two requests.
std::vector<Scene> group(const std::vector<Activity>& activities, const std::vector<size_t>& breaks);
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

// ---- Moments: the side panel's feed of what the run did, newest first ----

/// Each file as the session changed it, from its original to now. Requests that were undone are left out.
std::vector<FileChange> kept_changes(const Session& session);

/// Something a run made, for the shelf beside Moments: a file it created or changed, or a page it checked.
struct Made {
    std::string label;   ///< The file's name, or the page followed by " page".
    std::string target;  ///< The file's path as its change keeps it, or the page as the call named it.
    bool page = false, created = false;
    size_t activity = 0;  ///< Index of the latest call that made it.
    bool operator==(const Made&) const = default;
};
/// What the calls from index `from` on made, newest first and each once: edited files that have a kept change (not
/// deleted ones), and pages checked or previewed. At most `limit`.
std::vector<Made> made_so_far(const std::vector<Activity>& activities, size_t from, const std::vector<FileChange>& changes, size_t limit = 6);
/// How a page is named on the shelf: "about.html page", "/about.html page", or the host for a site's root.
std::string page_label(const std::string& page);

/// Where a request began in the calls, and what it said.
struct Asked {
    size_t position = 0;
    std::string text;
    bool operator==(const Asked&) const = default;
};
/// Each request of the session that said something, with the index of its first call (clamped to `count` calls).
std::vector<Asked> requests(const Session& session, size_t count);

/// One entry of the feed: a "You asked" line (an index into the requests) or a scene (an index into the scenes).
struct FeedEntry {
    bool asked = false;
    size_t index = 0;
    bool operator==(const FeedEntry&) const = default;
};
/// The feed, newest first. A request's line sits just below its first scene, so it reads as where that request began.
std::vector<FeedEntry> feed(const std::vector<Scene>& scenes, const std::vector<Asked>& asked);

/// The facts line of the end-of-run card: "2 files changed · +18 −2 · 1 command · 8 steps".
/// The line counts are left out when no file changed, and the commands when none ran.
std::string end_facts(size_t files, int added, int removed, int commands, size_t steps);

}  // namespace mason::scenes
