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
    static constexpr size_t kMaxImageBytes = 4 * 1024 * 1024, kMaxDocumentBytes = 20 * 1024 * 1024, kMaxExtracted = 200000;
    std::string name, content;
    std::string image = "";  ///< For a picture: its bytes as a data: address.
    /// Reads a file to attach. Text must be UTF-8 and at most 256 KiB; a picture at most 4 MiB; a PDF or Word document
    /// at most 20 MiB, and its text is what is attached. Anything else is refused with a reason.
    static ChatAttachment from_file(const fs::path& file);
    /// The text of a Word document, from its main part: paragraphs as lines, tabs kept.
    static std::string word_text(const std::string& xml);
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

/// A table from an answer, as cells to lay out in a grid and, where a column is all numbers, as a series to chart.
struct TableSeries {
    std::string name;
    std::vector<double> values;
};
struct TableData {
    std::vector<std::string> headers;
    std::vector<std::vector<std::string>> rows;
    std::vector<TableSeries> series;  ///< The numeric columns, each against the first column's labels.
};

namespace tables {
/// A cell as a number: digits with an optional sign, thousands separators, a currency sign before or a percent sign after.
std::optional<double> number(const std::string& cell);
/// The tables written in Markdown in `text`: a header row, a row of dashes, then 1 to 40 rows of the same width.
std::vector<TableData> find(const std::string& text);
/// `text` without the lines of its tables, for showing beside the tables laid out as grids.
std::string prose(const std::string& text);
}  // namespace tables

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
