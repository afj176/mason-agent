// What Mason may do in one project, set by the user and kept outside the project so nothing in it can change it:
// tools that are switched off, the workflows that may run, when the policy lapses, and whether the project's hooks run.
#pragma once
#include "workspace.hpp"

#include <ctime>

namespace mason {

struct ProjectPolicy {
    std::string name, workspace;
    std::vector<std::string> disabled_tools;
    std::optional<std::vector<std::string>> workflows;  ///< The only workflows that may run, or nothing for any.
    std::time_t expires = 0;                            ///< 0 for never.
    /// Whether workflows marked with events (afterEdit, beforeVerify) run by themselves. They only ever run in Auto, sandboxed.
    bool hooks = false;

    /// Refuses a tool the policy switches off, and a workflow it does not list.
    void check(const std::string& tool, const std::string& workflow = "") const;
};

namespace policies {

extern const char* const kUsage;

/// The project's policy while it is in force.
std::optional<ProjectPolicy> load(const fs::path& home, const std::string& workspace, bool include_expired = false);
/// Saves the policy, or removes it when given nothing.
void set(const std::optional<ProjectPolicy>& policy, const fs::path& home, const std::string& workspace);
/// The /policy command, shared by the command line and the app. Only the user can run it; the model has no tool for it.
std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home);

}  // namespace policies
}  // namespace mason
