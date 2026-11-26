#include "roles.hpp"

#include <chrono>

namespace mason {

ModelReply RoleModel::complete(const std::vector<ChatMessage>& messages, const std::vector<ToolSpec>& tools, const std::function<void(const std::string&)>& on_text, const CancelToken& cancel) {
    int number = 0;
    {
        std::lock_guard<std::mutex> guard(lock_);
        number = ++calls_;
    }
    if (limit_ > 0 && number > limit_) throw Error("The " + role_ + " has been asked " + std::to_string(limit_) + " times, which is its limit (/roles limit N changes it).");
    const ModelReply reply = base_.complete(messages, tools, on_text, cancel);
    if (!trace_.empty()) {
        try {
            json sent = json::array();
            for (const ChatMessage& message : messages) {
                json calls = json::array();
                for (const ToolCall& call : message.tool_calls) calls.push_back({{"name", call.name}, {"arguments", call.arguments}});
                sent.push_back({{"role", message.role}, {"content", message.content}, {"toolCalls", calls}});
            }
            json offered = json::array();
            for (const ToolSpec& tool : tools) offered.push_back(tool.name);
            json made = json::array();
            for (const ToolCall& call : reply.tool_calls) made.push_back({{"name", call.name}, {"arguments", call.arguments}});
            const long long stamp = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
            const json record = {{"role", role_}, {"model", base_.name()}, {"call", number}, {"at", stamp}, {"messages", sent}, {"tools", offered},
                                 {"reply", {{"text", reply.text}, {"toolCalls", made}, {"finishReason", reply.finish_reason}}}};
            fs::create_directories(trace_);
            write_file_atomic(trace_ / (std::to_string(stamp) + "-" + role_ + "-" + std::to_string(number) + ".json"), record.dump(2, ' ', false, json::error_handler_t::replace));
        } catch (const std::exception&) {
            // A trace that cannot be written never stops the work.
        }
    }
    return reply;
}

namespace roles {

namespace {

std::string real(const std::string& path) {
    std::error_code ignored;
    const fs::path resolved = fs::weakly_canonical(path, ignored);
    return resolved.empty() ? path : resolved.string();
}

void save(const ModelRoles& roles, const fs::path& home) {
    json value = {{"limit", roles.limit}};
    if (roles.reviewer) value["reviewer"] = {{"url", roles.reviewer->url}, {"model", roles.reviewer->model}, {"key", roles.reviewer->key}};
    if (roles.visual) value["visual"] = {{"url", roles.visual->url}, {"model", roles.visual->model}, {"key", roles.visual->key}};
    if (!roles.trace.empty()) value["trace"] = roles.trace;
    fs::create_directories(home);
    write_file_atomic(home / "roles.json", value.dump(2));
}

std::optional<ModelEndpoint> endpoint_in(const json& value, const char* name) {
    if (!value.contains(name) || !value[name].is_object()) return std::nullopt;
    const json& item = value[name];
    ModelEndpoint endpoint{item.value("url", ""), item.value("model", ""), item.value("key", "")};
    if (endpoint.url.empty() || endpoint.model.empty()) return std::nullopt;
    return endpoint;
}

}  // namespace

const char* const kUsage =
    "/roles                          The models in each role\n"
    "/roles reviewer URL MODEL       Use another local model for /review (or: /roles reviewer off)\n"
    "/roles visual URL MODEL         Use another local model to look at pictures of a window (or: /roles visual off)\n"
    "/roles limit N                  How many times one review may ask the reviewer (1 to 500)\n"
    "/roles trace FOLDER             Write every model call down in a folder outside your projects (or: /roles trace off)";

ModelRoles load(const fs::path& home) {
    ModelRoles found;
    if (!fs::exists(home / "roles.json")) return found;
    const json value = json::parse(read_file(home / "roles.json"), nullptr, false);
    if (!value.is_object()) return found;
    found.reviewer = endpoint_in(value, "reviewer");
    found.visual = endpoint_in(value, "visual");
    found.limit = value.value("limit", 64);
    if (found.limit < 1 || found.limit > 500) found.limit = 64;
    found.trace = value.value("trace", "");
    return found;
}

fs::path trace_folder(const ModelRoles& roles, const Workspace* workspace) {
    if (roles.trace.empty()) return {};
    if (workspace && (real(roles.trace) + "/").rfind(real(workspace->root()) + "/", 0) == 0) return {};
    return roles.trace;
}

std::unique_ptr<RoleModel> implementer(ChatModel& main, const Workspace* workspace, const fs::path& home) {
    const fs::path trace = trace_folder(load(home), workspace);
    if (trace.empty()) return nullptr;
    return std::make_unique<RoleModel>(main, "implementer", 0, trace);
}

std::unique_ptr<RoleModel> reviewer(ChatModel& main, const Workspace* workspace, const fs::path& home) {
    const ModelRoles chosen = load(home);
    const fs::path trace = trace_folder(chosen, workspace);
    if (chosen.reviewer) {
        try {
            return std::make_unique<RoleModel>(std::unique_ptr<ChatModel>(new OpenAIClient(*chosen.reviewer)), "reviewer", chosen.limit, trace);
        } catch (const std::exception&) {
        }
    }
    return std::make_unique<RoleModel>(main, "reviewer", chosen.limit, trace);
}

std::optional<ModelEndpoint> endpoint_of(ChatModel& model) {
    if (const auto* client = dynamic_cast<OpenAIClient*>(&model)) return client->endpoint();
    if (auto* role = dynamic_cast<RoleModel*>(&model)) {
        if (const auto* client = dynamic_cast<OpenAIClient*>(&role->base())) return client->endpoint();
    }
    return std::nullopt;
}

std::string command(const std::string& rest, const fs::path& home) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    ModelRoles chosen = load(home);
    auto shown = [&] {
        auto named = [](const std::optional<ModelEndpoint>& endpoint) { return endpoint ? endpoint->model + " at " + endpoint->url : std::string("the model doing the work"); };
        return "reviewer  " + named(chosen.reviewer) + "\nvisual    " + named(chosen.visual) + "\nlimit     " + std::to_string(chosen.limit) + " reviewer calls in one review\ntrace     " +
               (chosen.trace.empty() ? std::string("off") : chosen.trace);
    };
    if (words.empty()) return shown();
    const std::string verb = to_lower(words[0]);
    if (verb == "reviewer" || verb == "visual") {
        std::optional<ModelEndpoint> picked;
        if (words.size() == 2 && to_lower(words[1]) == "off") picked.reset();
        else if (words.size() == 3) picked = ModelEndpoint{validated_endpoint(words[1]), words[2], ""};
        else throw Error("Use /roles " + verb + " URL MODEL, or /roles " + verb + " off.");
        (verb == "reviewer" ? chosen.reviewer : chosen.visual) = picked;
    } else if (verb == "limit") {
        if (words.size() != 2 || words[1].empty() || words[1].size() > 3 || words[1].find_first_not_of("0123456789") != std::string::npos || std::stoi(words[1]) < 1 || std::stoi(words[1]) > 500) {
            throw Error("Use /roles limit N, from 1 to 500.");
        }
        chosen.limit = std::stoi(words[1]);
    } else if (verb == "trace") {
        if (words.size() < 2) throw Error("Use /roles trace FOLDER, or /roles trace off.");
        std::string folder;
        for (size_t i = 1; i < words.size(); i++) folder += (i > 1 ? " " : "") + words[i];
        if (to_lower(folder) == "off") {
            chosen.trace.clear();
        } else {
            if (folder.rfind("~/", 0) == 0) folder = home_dir() + folder.substr(1);
            const fs::path path = fs::absolute(folder).lexically_normal();
            fs::create_directories(path);
            chosen.trace = real(path.string());
        }
    } else {
        return kUsage;
    }
    save(chosen, home);
    return shown();
}

}  // namespace roles
}  // namespace mason
