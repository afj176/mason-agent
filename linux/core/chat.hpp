// Chat: a conversation with a model that has no tools. It cannot read the project, edit files or run anything.
#pragma once
#include <mutex>

#include "harness.hpp"

namespace mason {

/// Conversations are saved on this computer, one JSON file each, apart from coding sessions.
struct ChatConversation {
    std::string id = new_id();
    std::string title = "New chat";
    std::string updated = now_iso();
    std::vector<ChatMessage> messages;
    std::string model;                  ///< The model that answered last, for display.
    int prompt_tokens = 0;
    std::vector<ChatMessage> archived;  ///< Messages a compaction replaced with a summary, oldest first.
    int compactions = 0;
};

/// A text file added to a chat message. Its content travels inside the message.
struct ChatAttachment {
    static constexpr size_t kMaxBytes = 256 * 1024;
    static constexpr size_t kMaxPerMessage = 5;
    std::string name, content;
    /// Reads a UTF-8 text file. Binary files and files over 256 KiB are refused with a reason.
    static ChatAttachment from_file(const fs::path& file);
};

class ChatStore {
public:
    explicit ChatStore(const fs::path& home = data_home());
    const fs::path& folder() const { return folder_; }
    /// Newest first.
    const std::vector<ChatConversation>& conversations() const { return conversations_; }
    ChatConversation create();
    void save(ChatConversation& conversation);
    void remove(const std::string& id);

private:
    fs::path folder_;
    std::vector<ChatConversation> conversations_;
    mutable std::mutex lock_;
};

namespace chat {

/// The text sent for one message: what was typed, followed by each attached file in a fenced block.
std::string compose(const std::string& text, const std::vector<ChatAttachment>& attachments);
/// Sends one message and appends the answer. The conversation is saved before the model is asked and again after,
/// so a stopped or failed request keeps the question. A long conversation is compacted first when the model's
/// context is nearly full.
void send(ChatConversation& conversation, const std::string& text, const std::vector<ChatAttachment>& attachments, ChatModel& model, ChatStore& store,
          const std::function<void(const std::string&)>& on_text, const CancelToken& cancel = nullptr);

}  // namespace chat

}  // namespace mason
