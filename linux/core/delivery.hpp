// A pull request made from hunks the user chose. The draft is one commit on top of the target branch, built without
// touching the checkout, its index or its branches. Publishing is a separate, explicit step that pushes that commit
// to a new branch (never with force) and opens a draft pull request through `gh`. Feedback brings failed checks,
// requested changes and review comments back.
#pragma once
#include "experiments.hpp"

#include <set>

namespace mason {

struct DeliveryDraft {
    std::string id, workspace, base, base_commit, commit, title, body;
    std::string repository;  ///< owner/name on GitHub, or empty when the checkout has no GitHub origin.
    std::string branch;
    std::vector<std::string> paths;
    std::string url;  ///< Empty until published.
};

struct DeliveryFeedback {
    std::string head, url;
    std::vector<std::string> observations;
};

struct ReviewedPatch {
    std::string path;
    ExperimentHunk hunk;
};

struct DeliveryReview {
    std::string revision;
    std::vector<ReviewedPatch> patches;
};

namespace delivery {

extern const char* const kUsage;

/// owner/name from a GitHub remote address.
std::string repository(const std::string& remote);
/// Everything the checkout has that the target branch does not, a hunk at a time, and the commit they were read against.
DeliveryReview review(const Workspace& workspace, const std::string& base);
/// Builds the commit for the chosen hunks on top of `pinned`, the revision the user reviewed against.
DeliveryDraft draft(const Workspace& workspace, const std::string& base, const std::string& pinned, const std::string& title, const std::set<std::string>& selected, const fs::path& home);
/// Pushes the draft's commit to its own new branch and opens a draft pull request. Returns the pull request's address.
std::string publish(const Workspace& workspace, const DeliveryDraft& draft, const fs::path& home);
/// Failed checks, requested changes and review comments on the draft's pull request.
DeliveryFeedback feedback(const Workspace& workspace, const DeliveryDraft& draft);
DeliveryFeedback parse_feedback(const std::string& state, const std::string& comments);
/// The project's current draft, if it has one.
std::optional<DeliveryDraft> current(const Workspace& workspace, const fs::path& home);

struct Said {
    std::string text;
    std::string request;  ///< For "feedback send" with something to act on: what to send the model.
};
/// The /deliver command, shared by the command line and the app.
Said command(const std::string& rest, const Workspace& workspace, const fs::path& home);

}  // namespace delivery
}  // namespace mason
