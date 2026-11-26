// Forking a session from an earlier request, and moving a session between computers or projects as a file.
#pragma once
#include "session.hpp"

namespace mason {

struct SessionFork {
    Session session;
    std::string request;  ///< The request the fork stops before, for the user to edit and send again.
    std::vector<std::string> restored, skipped;
};

namespace transfer {
constexpr int kFormat = 1;
constexpr size_t kMaxBytes = 24 * 1024 * 1024;
extern const char* const kHelp;
/// A new session holding the conversation before request number `index` (from 0). With `restore_files`, the project
/// goes back to how it was before that request: each later request's changes are undone, newest first. A file changed
/// since is left alone and reported.
SessionFork fork(Session& source, SessionStore& store, size_t index, bool restore_files);
/// The session as a file. With `sanitize`, the project's path and likely secrets are removed, and so are the contents
/// of the files it changed.
std::string export_session(const Session& session, bool sanitize);
/// Adds an exported session to a project under a new identity. File history and undo stay with the original.
Session import_session(const std::string& data, SessionStore& store, const std::string& workspace);
}  // namespace transfer

}  // namespace mason
