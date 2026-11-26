// Models beside the one that does the work. A reviewer reads what a session changed for `/review`; a visual model
// looks at pictures of a window. Either can be a different model on a different local server. Roles only make
// proposals: the engine keeps the workspace, the permissions and the checks.
#pragma once
#include "model.hpp"
#include "workspace.hpp"

#include <mutex>

namespace mason {

struct ModelRoles {
    std::optional<ModelEndpoint> reviewer, visual;
    int limit = 64;     ///< How many times one review may ask the reviewer.
    std::string trace;  ///< A folder outside any project where every model call is written down; empty when off.
};

/// A model in a role: it counts its calls against a limit and, when tracing is on, writes each call down.
class RoleModel : public ChatModel {
public:
    /// `limit` 0 means no limit; an empty `trace` means nothing is written.
    RoleModel(ChatModel& base, std::string role, int limit = 0, fs::path trace = {}) : base_(base), role_(std::move(role)), limit_(limit), trace_(std::move(trace)) {}
    /// Owns the model it wraps.
    RoleModel(std::unique_ptr<ChatModel> owned, std::string role, int limit = 0, fs::path trace = {}) : owned_(std::move(owned)), base_(*owned_), role_(std::move(role)), limit_(limit), trace_(std::move(trace)) {}
    std::string name() const override { return base_.name(); }
    int context_window() override { return base_.context_window(); }
    ModelReply complete(const std::vector<ChatMessage>& messages, const std::vector<ToolSpec>& tools, const std::function<void(const std::string&)>& on_text, const CancelToken& cancel) override;
    ChatModel& base() { return base_; }

private:
    std::unique_ptr<ChatModel> owned_;
    ChatModel& base_;
    std::string role_;
    int limit_;
    fs::path trace_;
    std::mutex lock_;
    int calls_ = 0;
};

namespace roles {

extern const char* const kUsage;

ModelRoles load(const fs::path& home);
/// Where traces go for work in `workspace`: nowhere when tracing is off or the folder is inside the project.
fs::path trace_folder(const ModelRoles& roles, const Workspace* workspace);
/// The model that does the work, written down when tracing is on. Returns nothing when tracing is off.
std::unique_ptr<RoleModel> implementer(ChatModel& main, const Workspace* workspace, const fs::path& home);
/// The model that reviews: the reviewer when one is chosen, otherwise the main model, held to the call limit.
std::unique_ptr<RoleModel> reviewer(ChatModel& main, const Workspace* workspace, const fs::path& home);
/// Puts a recorded call (one of the files a trace folder holds) to a model again and sets its answer beside the recorded one.
json compare(const fs::path& trace, ChatModel& model, const CancelToken& cancel = nullptr);
/// The endpoint of the model behind `model`, through any role wrapper.
std::optional<ModelEndpoint> endpoint_of(ChatModel& model);
/// The /roles command, shared by the command line and the app. Returns what to tell the user.
std::string command(const std::string& rest, const fs::path& home);

}  // namespace roles
}  // namespace mason
