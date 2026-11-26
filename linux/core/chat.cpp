#include "chat.hpp"

#include <algorithm>

namespace mason {

namespace {

const char* const kSystemPrompt =
    "You are Mason, an assistant running on the user's own computer. In this chat you have no tools: you cannot read their files, "
    "run commands or browse. Answer from what is in the conversation, say so when you do not know, and format code in fenced blocks.";

json conversation_json(const ChatConversation& conversation) {
    json messages = json::array(), archived = json::array();
    for (const ChatMessage& message : conversation.messages) messages.push_back(message_json(message));
    for (const ChatMessage& message : conversation.archived) archived.push_back(message_json(message));
    json result = {{"id", conversation.id}, {"title", conversation.title}, {"updated", conversation.updated}, {"messages", messages}};
    if (!conversation.model.empty()) result["model"] = conversation.model;
    if (conversation.prompt_tokens > 0) result["promptTokens"] = conversation.prompt_tokens;
    if (!conversation.archived.empty()) result["archived"] = archived;
    if (conversation.compactions > 0) result["compactions"] = conversation.compactions;
    return result;
}

ChatConversation conversation_from_json(const json& value) {
    ChatConversation conversation;
    conversation.id = value.at("id").get<std::string>();
    conversation.title = value.value("title", "New chat");
    conversation.updated = value.value("updated", now_iso());
    conversation.model = value.value("model", "");
    conversation.prompt_tokens = value.value("promptTokens", 0);
    conversation.compactions = value.value("compactions", 0);
    for (const json& item : value.value("messages", json::array())) conversation.messages.push_back(message_from_json(item));
    for (const json& item : value.value("archived", json::array())) conversation.archived.push_back(message_from_json(item));
    return conversation;
}

}  // namespace

ChatAttachment ChatAttachment::from_file(const fs::path& file) {
    const std::string name = file.filename().string();
    std::error_code failure;
    const uintmax_t size = fs::file_size(file, failure);
    if (failure || !fs::is_regular_file(file, failure)) throw Error(name + " could not be read.");
    if (size > kMaxBytes) throw Error(name + " is larger than 256 KiB. Attach a smaller text file, or paste the part you need.");
    std::string content;
    try {
        content = read_file(file);
    } catch (const std::exception&) {
        throw Error(name + " could not be read.");
    }
    if (!is_valid_utf8(content) || content.find('\0') != std::string::npos) throw Error(name + " is not a text file. Chat can attach UTF-8 text only.");
    return {name, content};
}

ChatStore::ChatStore(const fs::path& home) : folder_(home / "chats") {
    fs::create_directories(folder_);
    for (const fs::directory_entry& entry : fs::directory_iterator(folder_)) {
        if (entry.path().extension() != ".json") continue;
        try {
            const json parsed = json::parse(read_file(entry.path()), nullptr, false);
            if (parsed.is_object()) conversations_.push_back(conversation_from_json(parsed));
        } catch (const std::exception&) {
            // A file that cannot be read is left where it is and not listed.
        }
    }
    std::sort(conversations_.begin(), conversations_.end(), [](const ChatConversation& a, const ChatConversation& b) { return a.updated > b.updated; });
}

ChatConversation ChatStore::create() {
    ChatConversation conversation;
    save(conversation);
    return conversation;
}

void ChatStore::save(ChatConversation& conversation) {
    std::lock_guard<std::mutex> guard(lock_);
    conversation.updated = now_iso();
    write_file_atomic(folder_ / (conversation.id + ".json"), conversation_json(conversation).dump(2));
    auto existing = std::find_if(conversations_.begin(), conversations_.end(), [&](const ChatConversation& item) { return item.id == conversation.id; });
    if (existing != conversations_.end()) *existing = conversation;
    else conversations_.insert(conversations_.begin(), conversation);
}

void ChatStore::remove(const std::string& id) {
    std::lock_guard<std::mutex> guard(lock_);
    std::error_code failure;
    fs::remove(folder_ / (id + ".json"), failure);
    conversations_.erase(std::remove_if(conversations_.begin(), conversations_.end(), [&](const ChatConversation& item) { return item.id == id; }), conversations_.end());
}

namespace chat {

std::string compose(const std::string& text, const std::vector<ChatAttachment>& attachments) {
    std::string composed = trim(text);
    for (const ChatAttachment& file : attachments) {
        const std::string fence = file.content.find("```") != std::string::npos ? "````" : "```";
        composed += (composed.empty() ? "" : "\n\n") + std::string("Attached file ") + file.name + ":\n" + fence + "\n" + file.content + "\n" + fence;
    }
    return composed;
}

void send(ChatConversation& conversation, const std::string& text, const std::vector<ChatAttachment>& attachments, ChatModel& model, ChatStore& store,
          const std::function<void(const std::string&)>& on_text, const CancelToken& cancel) {
    const std::string typed = trim(text);
    if (typed.empty() && attachments.empty()) throw Error("Type a message first.");
    if (attachments.size() > ChatAttachment::kMaxPerMessage) throw Error("Attach at most " + std::to_string(ChatAttachment::kMaxPerMessage) + " files to one message.");
    const std::string content = compose(typed, attachments);
    if (utf8_length(content) > 400000) throw Error("That message is too long for a chat.");
    if (conversation.messages.empty() || conversation.messages.front().role != "system") {
        conversation.messages.insert(conversation.messages.begin(), ChatMessage{"system", kSystemPrompt, {}, ""});
    }
    if (conversation.title == "New chat") {
        const std::string label = typed.empty() ? attachments.front().name : typed;
        conversation.title = utf8_length(label) > 55 ? utf8_prefix(label, 55) + "…" : label;
    }
    // A request that was stopped leaves a question without an answer; the next message joins it, so turns keep alternating.
    if (conversation.messages.back().role == "user") conversation.messages.back().content += "\n\n" + content;
    else conversation.messages.push_back({"user", content, {}, ""});
    conversation.model = model.name();
    store.save(conversation);

    if (compaction::needed(conversation.prompt_tokens, model.context_window())) {
        Session holder;
        holder.messages = conversation.messages;
        holder.archived = conversation.archived;
        if (compaction::compact(holder, model, 4, cancel)) {
            conversation.messages = holder.messages;
            conversation.archived = holder.archived;
            conversation.compactions++;
        }
        conversation.prompt_tokens = 0;
    }
    const std::function<void(const std::string&)> sink = on_text ? on_text : std::function<void(const std::string&)>([](const std::string&) {});
    const ModelReply reply = model.complete(conversation.messages, {}, sink, cancel);
    check_cancel(cancel);
    if (trim(reply.text).empty()) throw Error("The model returned an empty answer. Try again, or choose another model.");
    conversation.messages.push_back({"assistant", reply.text, {}, ""});
    if (reply.prompt_tokens > 0) conversation.prompt_tokens = reply.prompt_tokens;
    store.save(conversation);
}

}  // namespace chat

}  // namespace mason
