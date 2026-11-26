// Notes the user keeps for a project: short facts worth telling the model every time ("the API is at /v2",
// "never edit the generated folder"). The six newest go to the model with each request, as background and not as
// instructions. A note can be tied to a file; once that file changes, the note is out of date and is no longer sent.
#pragma once

#include "util.hpp"
#include "workspace.hpp"

namespace mason {

struct ProjectNote {
    std::string id, text;
    std::string source;       ///< The project file the note is about, or empty.
    std::string source_hash;  ///< That file's fingerprint when the note was saved.
    std::string saved;        ///< When, as ISO 8601.
};

namespace project_notes {
constexpr size_t kLimit = 50, kTextLimit = 2000, kSent = 6;
/// The project's notes, newest first.
std::vector<ProjectNote> load(const fs::path& home, const std::string& workspace);
/// A note tied to a file is current while that file is as it was when the note was saved.
bool current(const ProjectNote& note, const Workspace& workspace);
/// What the model is told: the newest current notes, or an empty string when there are none.
std::string context(const fs::path& home, const Workspace& workspace);
ProjectNote add(const std::string& text, const std::string& source, const Workspace& workspace, const fs::path& home = data_home());
void remove(const std::string& id, const std::string& workspace, const fs::path& home = data_home());
/// The /notes command, shared by the command line and the app.
std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home = data_home());
extern const char* const kHelp;
}  // namespace project_notes

}  // namespace mason
