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
            // The names, and the full definitions so the same call can be put to another model later.
            json offered = json::array(), specs = json::array();
            for (const ToolSpec& tool : tools) {
                offered.push_back(tool.name);
                specs.push_back({{"name", tool.name}, {"description", tool.description}, {"parameters", tool.parameters}});
            }
            json made = json::array();
            for (const ToolCall& call : reply.tool_calls) made.push_back({{"name", call.name}, {"arguments", call.arguments}});
            const long long stamp = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
            const json record = {{"role", role_}, {"model", base_.name()}, {"call", number}, {"at", stamp}, {"messages", sent}, {"tools", offered}, {"toolSpecs", specs},
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

json compare(const fs::path& trace, ChatModel& model, const CancelToken& cancel) {
    std::error_code failure;
    if (!fs::is_regular_file(trace, failure) || fs::file_size(trace, failure) > 32 * 1024 * 1024) throw Error("That file is not a recorded model call.");
    const json record = json::parse(read_file(trace), nullptr, false);
    if (!record.is_object() || !record.contains("messages") || !record["messages"].is_array() || !record.contains("reply") || !record["reply"].is_object()) throw Error("That file is not a recorded model call.");
    auto calls = [](const json& holder) {
        json found = json::array();
        if (holder.is_object() && holder.contains("toolCalls") && holder["toolCalls"].is_array()) {
            for (const json& item : holder["toolCalls"]) found.push_back({{"name", item.value("name", "")}, {"arguments", item.value("arguments", "")}});
        }
        return found;
    };
    // Recorded calls carry no ids, so each is given one and its result, which follows, is matched to it in order.
    std::vector<ChatMessage> messages;
    std::vector<std::string> waiting;
    int number = 0;
    for (const json& item : record["messages"]) {
        ChatMessage message{item.value("role", "user"), item.value("content", ""), {}, ""};
        if (message.role == "tool") {
            message.tool_call_id = waiting.empty() ? "call-0" : waiting.front();
            if (!waiting.empty()) waiting.erase(waiting.begin());
        } else {
            for (const json& made : calls(item)) {
                const std::string id = "call-" + std::to_string(++number);
                message.tool_calls.push_back({id, made.value("name", ""), made.value("arguments", "{}")});
                waiting.push_back(id);
            }
        }
        messages.push_back(std::move(message));
    }
    std::vector<ToolSpec> tools;
    if (record.contains("toolSpecs") && record["toolSpecs"].is_array()) {
        for (const json& item : record["toolSpecs"]) tools.push_back({item.value("name", ""), item.value("description", ""), item.contains("parameters") ? item["parameters"] : json::object()});
    }
    const ModelReply reply = model.complete(messages, tools, [](const std::string&) {}, cancel);
    json now_calls = json::array();
    for (const ToolCall& made : reply.tool_calls) now_calls.push_back({{"name", made.name}, {"arguments", made.arguments}});
    const json before_calls = calls(record["reply"]);
    const std::string before_text = record["reply"].value("text", "");
    // The same decision: the same tools called with the same arguments, or, with no calls, the same words.
    const bool same = before_calls.empty() && now_calls.empty() ? trim(before_text) == trim(reply.text) : before_calls == now_calls;
    auto names = [](const json& list) {
        std::vector<std::string> found;
        for (const json& item : list) found.push_back(item.value("name", ""));
        return found;
    };
    return {{"role", record.value("role", "")}, {"recordedModel", record.value("model", "")}, {"model", model.name()}, {"recorded", {{"text", before_text}, {"toolCalls", before_calls}}},
            {"now", {{"text", reply.text}, {"toolCalls", now_calls}}}, {"same", same}, {"sameTools", names(before_calls) == names(now_calls)}};
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
