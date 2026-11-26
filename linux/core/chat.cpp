#include "chat.hpp"

#include "shell.hpp"

#include <cctype>
#include <cstdio>
#include <map>

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

namespace {

std::string base64(const std::string& bytes) {
    static const char* alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string text;
    text.reserve((bytes.size() + 2) / 3 * 4);
    for (size_t i = 0; i < bytes.size(); i += 3) {
        const unsigned a = static_cast<unsigned char>(bytes[i]), b = i + 1 < bytes.size() ? static_cast<unsigned char>(bytes[i + 1]) : 0, c = i + 2 < bytes.size() ? static_cast<unsigned char>(bytes[i + 2]) : 0;
        text += alphabet[a >> 2];
        text += alphabet[((a & 3) << 4) | (b >> 4)];
        text += i + 1 < bytes.size() ? alphabet[((b & 15) << 2) | (c >> 6)] : '=';
        text += i + 2 < bytes.size() ? alphabet[c & 63] : '=';
    }
    return text;
}

/// What a program prints, up to 32 MiB. The program is found on the PATH.
std::string output_of(const std::string& command, const std::string& missing) {
    FILE* stream = popen((command + " 2>/dev/null").c_str(), "r");
    if (!stream) throw Error(missing);
    std::string text;
    char buffer[8192];
    size_t count = 0;
    while (text.size() < 32 * 1024 * 1024 && (count = fread(buffer, 1, sizeof buffer, stream)) > 0) text.append(buffer, count);
    pclose(stream);
    if (text.empty()) throw Error(missing + " (Or the file is not what its name says.)");
    return text;
}

}  // namespace

std::string ChatAttachment::word_text(const std::string& xml) {
    std::string marked = replace_all(replace_all(replace_all(xml, "</w:p>", "\n"), "<w:tab/>", "\t"), "<w:br/>", "\n");
    std::string text;
    bool inside = false;
    for (char c : marked) {
        if (c == '<') inside = true;
        else if (c == '>') inside = false;
        else if (!inside) text += c;
    }
    for (const auto& [entity, character] : std::vector<std::pair<std::string, std::string>>{{"&lt;", "<"}, {"&gt;", ">"}, {"&quot;", "\""}, {"&apos;", "'"}, {"&amp;", "&"}}) text = replace_all(text, entity, character);
    return text;
}

ChatAttachment ChatAttachment::from_file(const fs::path& file) {
    const std::string name = file.filename().string();
    std::error_code failure;
    const uintmax_t size = fs::file_size(file, failure);
    if (failure || !fs::is_regular_file(file, failure)) throw Error(name + " could not be read.");
    const std::string kind = to_lower(file.extension().string());
    static const std::map<std::string, std::string> pictures = {{".png", "image/png"}, {".jpg", "image/jpeg"}, {".jpeg", "image/jpeg"}, {".gif", "image/gif"}, {".webp", "image/webp"}};
    if (const auto type = pictures.find(kind); type != pictures.end()) {
        if (size > kMaxImageBytes) throw Error(name + " is larger than 4 MiB. Attach a smaller picture.");
        return {name, "", "data:" + type->second + ";base64," + base64(read_file(file))};
    }
    if (kind == ".pdf" || kind == ".docx") {
        if (size > kMaxDocumentBytes) throw Error(name + " is larger than 20 MiB.");
        std::string text = kind == ".pdf" ? output_of("pdftotext -layout " + shell::quote(file.string()) + " -", "Reading a PDF needs the pdftotext program (poppler-utils).")
                                          : word_text(output_of("unzip -p " + shell::quote(file.string()) + " word/document.xml", "Reading a Word document needs the unzip program."));
        if (!is_valid_utf8(text)) {
            for (char& c : text) {
                if (static_cast<unsigned char>(c) >= 0x80) c = '?';
            }
        }
        text = trim(text);
        if (text.empty()) throw Error("No text could be taken from " + name + ". A scanned document has none; attach a picture of the page for a model that can see.");
        if (utf8_length(text) > kMaxExtracted) text = utf8_prefix(text, kMaxExtracted) + "\n[The rest of the document was left out.]";
        return {name, text, ""};
    }
    if (size > kMaxBytes) throw Error(name + " is larger than 256 KiB. Attach a smaller text file, or paste the part you need.");
    std::string content;
    try {
        content = read_file(file);
    } catch (const std::exception&) {
        throw Error(name + " could not be read.");
    }
    if (!is_valid_utf8(content) || content.find('\0') != std::string::npos) throw Error(name + " is not text, a picture (PNG, JPEG, GIF, WebP), a PDF or a Word document (.docx).");
    return {name, content, ""};
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
        if (!file.image.empty()) { composed += (composed.empty() ? "" : "\n\n") + std::string("Attached picture: ") + file.name; continue; }
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
    for (const ChatAttachment& file : attachments) {
        if (!file.image.empty()) conversation.messages.back().images.push_back(file.image);
    }
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

namespace tables {

namespace {
std::optional<std::vector<std::string>> cells(const std::string& line) {
    const std::string trimmed = trim(line);
    if (trimmed.size() < 2 || trimmed.front() != '|' || trimmed.back() != '|') return std::nullopt;
    std::vector<std::string> found;
    for (const std::string& cell : split(trimmed.substr(1, trimmed.size() - 2), '|')) found.push_back(trim(cell));
    return found;
}
}  // namespace

std::optional<double> number(const std::string& cell) {
    std::string text = replace_all(trim(cell), ",", "");
    for (const char* sign : {"$", "€", "£"}) {
        if (text.rfind(sign, 0) == 0) { text = text.substr(std::string(sign).size()); break; }
    }
    if (!text.empty() && text.back() == '%') text.pop_back();
    if (text.empty()) return std::nullopt;
    size_t at = text[0] == '-' || text[0] == '+' ? 1 : 0, digits = 0, dots = 0;
    for (size_t i = at; i < text.size(); i++) {
        if (std::isdigit(static_cast<unsigned char>(text[i]))) digits++;
        else if (text[i] == '.' && dots == 0) dots++;
        else return std::nullopt;
    }
    if (digits == 0) return std::nullopt;
    try {
        return std::stod(text);
    } catch (const std::exception&) {
        return std::nullopt;
    }
}

namespace {
/// Each table, and (when asked) which lines belong to one.
std::vector<TableData> scan(const std::string& text, std::vector<bool>* taken);
}  // namespace

std::vector<TableData> find(const std::string& text) { return scan(text, nullptr); }

std::string prose(const std::string& text) {
    std::vector<bool> taken;
    scan(text, &taken);
    const std::vector<std::string> lines = split(text, '\n');
    std::string kept;
    bool first = true;
    for (size_t i = 0; i < lines.size(); i++) {
        if (i < taken.size() && taken[i]) continue;
        kept += (first ? "" : "\n") + lines[i];
        first = false;
    }
    return kept;
}

namespace {
std::vector<TableData> scan(const std::string& text, std::vector<bool>* taken) {
    const std::vector<std::string> lines = split(text, '\n');
    if (taken) taken->assign(lines.size(), false);
    std::vector<TableData> found;
    bool fenced = false;
    size_t index = 0;
    while (index < lines.size() && found.size() < 6) {
        if (trim(lines[index]).rfind("```", 0) == 0) { fenced = !fenced; index++; continue; }
        const auto headers = fenced || index + 2 >= lines.size() ? std::nullopt : cells(lines[index]);
        const auto rule = headers ? cells(lines[index + 1]) : std::nullopt;
        bool ruled = headers && headers->size() >= 2 && rule && rule->size() == headers->size();
        for (const std::string& cell : ruled ? *rule : std::vector<std::string>{}) {
            ruled = ruled && !cell.empty() && cell.find('-') != std::string::npos && cell.find_first_not_of("-: ") == std::string::npos;
        }
        if (!ruled) { index++; continue; }
        TableData table;
        table.headers = *headers;
        size_t next = index + 2;
        while (next < lines.size() && table.rows.size() < 40) {
            const auto row = cells(lines[next]);
            if (!row || row->size() != headers->size()) break;
            table.rows.push_back(*row);
            next++;
        }
        if (!table.rows.empty()) {
            for (size_t column = 1; column < table.headers.size() && table.series.size() < 4; column++) {
                TableSeries series{table.headers[column], {}};
                for (const auto& row : table.rows) {
                    if (const auto value = number(row[column])) series.values.push_back(*value);
                }
                if (series.values.size() == table.rows.size()) table.series.push_back(series);
            }
            found.push_back(std::move(table));
            if (taken) {
                for (size_t line = index; line < next; line++) (*taken)[line] = true;
            }
        }
        index = next;
    }
    return found;
}
}  // namespace

}  // namespace tables

}  // namespace mason
