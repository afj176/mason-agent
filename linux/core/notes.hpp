// Notes the user keeps for a project: short facts worth telling the model every time ("the API is at /v2",
// "never edit the generated folder"). Every current note, up to twelve, goes to the model with each request, as
// background the user saved and not as instructions. A note can be tied to a file; once that file changes, the note
// is out of date and is no longer sent. Mason may suggest one note per request; the user decides whether to keep it.
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
/// A project keeps at most twelve notes, each 1 to 280 characters. Every current one is sent, up to twelve.
/// Notes saved before these limits still load, show and send.
constexpr size_t kLimit = 12, kTextLimit = 280, kSent = 12, kDismissedLimit = 50;
/// The project's notes, newest first.
std::vector<ProjectNote> load(const fs::path& home, const std::string& workspace);
/// A note tied to a file is current while that file is as it was when the note was saved.
bool current(const ProjectNote& note, const Workspace& workspace);
/// How many of these notes go to the model: the current ones, up to twelve.
size_t sent(const std::vector<ProjectNote>& notes, const Workspace& workspace);
/// What the model is told: every current note, newest first and up to twelve, or an empty string when there are none.
std::string context(const fs::path& home, const Workspace& workspace);
ProjectNote add(const std::string& text, const std::string& source, const Workspace& workspace, const fs::path& home = data_home());
/// Changes a note's text. The new text is 1 to 280 characters.
void edit(const std::string& id, const std::string& text, const Workspace& workspace, const fs::path& home = data_home());
/// Saves a note again against its file as the file is now, so it is current and sent again.
void keep(const std::string& id, const Workspace& workspace, const fs::path& home = data_home());
void remove(const std::string& id, const std::string& workspace, const fs::path& home = data_home());
void clear(const std::string& workspace, const fs::path& home = data_home());
/// Puts back notes that were removed (to undo a removal), keeping their ids and times.
void restore(const std::vector<ProjectNote>& removed, const std::string& workspace, const fs::path& home = data_home());
/// When a note was saved, in words: "today", "yesterday", "2 days ago".
std::string when(const std::string& saved);
/// Texts the user dismissed when Mason suggested them, newest last, at most fifty.
std::vector<std::string> dismissed(const fs::path& home, const std::string& workspace);
void dismiss(const std::string& text, const std::string& workspace, const fs::path& home = data_home());
/// Why Mason may not suggest this note, or an empty string when it may: the length, a note the project already
/// has, or one the user dismissed before (compared trimmed and ignoring case).
std::string suggestion_problem(const std::string& text, const std::string& workspace, const fs::path& home = data_home());
/// What the /notes command is given for a command typed as `name rest`: "/note TEXT" is "/notes add TEXT".
std::string arguments(const std::string& name, const std::string& rest);
/// The /notes command, shared by the command line and the app.
std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home = data_home());
extern const char* const kHelp;
/// The first line of the list /notes shows, and the hint under it.
extern const char* const kListTitle;
extern const char* const kListHint;
}  // namespace project_notes

}  // namespace mason
