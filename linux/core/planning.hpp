// Two ways the user directs the work beyond typing a request: comments pinned to lines of the project, which travel
// with the next request, and milestones, a saved plan whose steps are checked against the files they cover.
#pragma once
#include "session.hpp"
#include "tools.hpp"

namespace mason {

std::string sha256_hex(const std::string& data);

namespace review {
constexpr size_t kLimit = 40;
std::string location(const ReviewComment& comment);
/// A comment on a line of the file as it is now, or (with `removed`) on a line a change took out.
ReviewComment comment(const Workspace& workspace, const std::string& path, int line, const std::string& note, const std::optional<std::string>& removed = std::nullopt);
/// The request as the model reads it: what the user typed, then each comment with the line it is about.
std::string request(const std::string& draft, const std::vector<ReviewComment>& comments);
/// A comment is only sent while the file is the one it was written on. Throws Error otherwise.
void validate(const Workspace& workspace, const std::vector<ReviewComment>& comments);
/// The typed form: what followed "/comment". Everything except "send", which needs a model.
std::string command(const std::string& rest, const Workspace& workspace, Session& session);
extern const char* const kHelp;
}  // namespace review

struct MilestoneStatus {
    Milestone item;
    std::string state;  ///< Blocked, Ready, Verified, Reviewed or Needs recheck
    std::string reason;
    bool done() const { return state == "Verified" || state == "Reviewed"; }
};

namespace milestones {
constexpr size_t kLimit = 64;
/// Adds or replaces a milestone. Changing one clears its receipt, since the evidence was for the old version.
std::vector<Milestone> put(const Workspace& workspace, const std::vector<Milestone>& current, Milestone item);
/// Removes a milestone, and it from the prerequisites of the others.
std::vector<Milestone> remove(const std::string& id, const std::vector<Milestone>& plan);
std::vector<MilestoneStatus> inspect(const Workspace& workspace, const std::vector<Milestone>& plan);
/// Checks one milestone and returns its receipt: the workflow must pass, or (with no workflow) the user records what
/// they saw. `allowed` says whether Mason may run the workflow, and `sandboxed` whether it runs in the sandbox.
MilestoneReceipt check(const Workspace& workspace, const Milestone& item, const std::vector<Milestone>& plan, bool allowed, bool sandboxed, const std::string& observation,
                       const CancelToken& cancel = nullptr);
/// The plan as the model is told it, with the milestone to work on next first.
std::string context(const Workspace& workspace, const std::vector<Milestone>& plan);
std::string list(const Workspace& workspace, const std::vector<Milestone>& plan);
/// The typed form: what followed "/milestone". `home` is where saved approval rules live.
std::string command(const std::string& rest, const Workspace& workspace, Session& session, const fs::path& home, const CancelToken& cancel = nullptr);
extern const char* const kHelp;
}  // namespace milestones

}  // namespace mason
