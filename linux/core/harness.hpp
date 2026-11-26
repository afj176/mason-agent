// Pieces the agent loop builds on: what a command changed, custom agents and commands, and conversation compaction.
#pragma once
#include <map>

#include "model.hpp"
#include "workspace.hpp"

namespace mason {

// ---- what a command changed ------------------------------------------------------------------------------------

/// The project's files as they were at one moment, held in memory while a command runs, so that what the command
/// created, changed or deleted can be recorded and undone. Excluded folders are not followed.
namespace snapshot {

struct Entry {
    uintmax_t size = 0;
    fs::file_time_type modified;
    std::shared_ptr<const std::string> data;
};
using Map = std::map<std::string, Entry>;

struct Change {
    std::string path;
    std::shared_ptr<const std::string> before, after;  ///< Null when the file did not exist on that side.
};

constexpr uintmax_t kMaxFileBytes = 8 * 1024 * 1024;
constexpr uintmax_t kMaxTotalBytes = 256 * 1024 * 1024;
constexpr size_t kMaxFiles = 5000;

/// Reads the project. A file whose size and modification time match `previous` is taken from there instead of being
/// read again. `complete` is set to false when a limit left some files out; those are not tracked.
Map capture(const Workspace& workspace, const Map& previous = {}, bool* complete = nullptr);
/// What differs between two captures, in path order.
std::vector<Change> changes(const Map& before, const Map& after);

}  // namespace snapshot

/// File contents that cannot live in the session as text (binary, or larger than the text limit), kept by SHA-256.
class BlobStore {
public:
    explicit BlobStore(const fs::path& home) : folder_(home / "blobs") {}
    std::string put(const std::string& data) const;
    std::optional<std::string> get(const std::string& name) const;
private:
    fs::path folder_;
};

// ---- custom agents and commands --------------------------------------------------------------------------------

/// A Markdown definition with simple "key: value" front matter.
struct Definition {
    std::string name, description, body;
    std::string source;  ///< Where it came from, relative to the project when it is a project file.
    std::map<std::string, std::string> fields;
};

/// Custom agents and slash commands defined as Markdown, in the layouts other harnesses use:
/// agents in .mason/agents, .agents/agents or .claude/agents; commands in .mason/commands, .claude/commands,
/// .opencode/command or .opencode/commands; plus `agents` and `commands` in Mason's data folder for every project.
/// Project files win. A command body may use $ARGUMENTS and $1–$9. An agent body becomes standing instructions.
namespace customization {

std::vector<Definition> agents(const fs::path& home, const std::string& workspace);
std::vector<Definition> commands(const fs::path& home, const std::string& workspace);
Definition parse(const std::string& name, std::string text, const std::string& source);
/// Expands "/name arguments" when a custom command has that name; otherwise returns nothing.
std::optional<std::string> expand(const std::vector<Definition>& commands, const std::string& input);
/// The request sent to the model: a custom command expanded, framed by the session agent's instructions.
std::string prepare(const fs::path& home, const Session& session, const std::string& input);

}  // namespace customization

// ---- compaction ------------------------------------------------------------------------------------------------

/// Replaces the older part of a conversation with a summary the model writes, so a long session keeps fitting in the
/// model's context. The messages it replaces are kept in the session's archive; nothing is thrown away.
namespace compaction {

/// Compaction starts by itself when the last request used this share of the model's context.
constexpr double kThreshold = 0.75;
bool needed(const Session& session, int window);
/// Where the kept tail begins when `keep` recent messages stay as they are. A tool result is never separated from
/// the assistant message that asked for it.
size_t cut(const std::vector<ChatMessage>& messages, size_t keep);
/// Summarises everything before the most recent `keep` messages. Returns false when there was nothing to compact.
bool compact(Session& session, ChatModel& model, size_t keep, const CancelToken& cancel = nullptr);

}  // namespace compaction

}  // namespace mason
