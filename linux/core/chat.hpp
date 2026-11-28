// Chat: a conversation with a model that is not tied to a project. It never reads the project, edits files or runs
// commands. It may read the web, read skills from bundles and Mason's own skills folder, and call the user's MCP
// servers, each switched on or off per conversation, and every new host and every MCP call asks first.
#pragma once
#include <mutex>

#include "agent.hpp"
#include "harness.hpp"
#include "network.hpp"
#include "web.hpp"

namespace mason {

/// Conversations are saved on this computer, one JSON file each, apart from coding sessions.
struct ChatConversation {
    std::string id = new_id();
    std::string title = "New chat";
    std::string updated = now_iso();
    std::vector<ChatMessage> messages;
    std::string model;                  ///< The model that answered last, for display.
    std::string reasoning;              ///< off, low, medium or high; empty leaves it to the server.
    int max_tokens = 0;                 ///< The most tokens one answer may take; 0 leaves it to the server.
    std::string reference;              ///< Text the user pinned; the model is given it with every message.
    int prompt_tokens = 0;
    std::vector<ChatMessage> archived;  ///< Messages a compaction replaced with a summary, oldest first.
    int compactions = 0;
    /// Which tools this conversation offers: the web and skills by default, MCP servers only when switched on.
    bool web = true, skills = true, mcp = false;
    /// What each tool call did, by call id, shown as a compact step where the call was made.
    std::vector<Activity> steps;
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
    /// Reads the folder again, newest first, so chats another program saved (mason chat, or the app) appear.
    void reload();
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

/// An answer to a question Chat asks: a host to read, or an MCP call to make.
enum class ChatAnswer { No, Once, Always };

/// What Chat needs to offer tools. Without it a chat has none.
struct ChatTools {
    fs::path home = data_home();
    /// Asked on the thread that sends. "network" requests name a host; "mcp_call" and "mcp_tools" name SERVER/TOOL.
    std::function<ChatAnswer(const ApprovalRequest&)> approve;
    /// Each step as it starts (state "running") and as it ends.
    std::function<void(const Activity&)> step;
    /// Whether the connection can make native tool calls. Without it no tools are offered.
    bool calls_tools = true;
    /// For tests: how the web is reached instead of the network.
    web::Resolve resolve;
    web::Fetch fetch;
};

namespace chat {

constexpr size_t kMaxReference = 100000;
constexpr int kMaxToolRounds = 8;
/// Shown when the model cannot call tools.
constexpr const char* kNoTools = "This model can't call tools.";
/// Whether a connection makes native tool calls, the way the coding agent decides it. Every connection Mason makes on
/// Linux is an OpenAI-compatible server with native tool calls.
bool calls_tools(const ChatModel& model);
/// The tools a conversation offers now: what its switches allow, less the web when the computer's network setting is
/// off, skills when there are none and MCP when no server is in use.
std::vector<ToolSpec> offered(const ChatConversation& conversation, const ChatTools& tools);
/// Switches web, skills or mcp on or off for a conversation.
void set_tool(ChatConversation& conversation, const std::string& group, bool on);
/// One line per switch, for /tools in mason chat.
std::string describe_tools(const ChatConversation& conversation, const ChatTools& tools);
/// The request fields for a conversation's output limit and reasoning level. The reasoning fields are the ones
/// llama.cpp reads; another server may ignore them.
json extra_body(const ChatConversation& conversation);
/// The request fields for a reasoning level, shared by Chat and coding sessions. Empty for "" or "default".
json reasoning_body(const std::string& level);
/// Sets a conversation's reasoning level ("default" clears it), output limit (0 clears it) and pinned reference.
void configure(ChatConversation& conversation, const std::string& reasoning, int max_tokens, const std::string& reference);
/// The text sent for one message: what was typed, followed by each attached file in a fenced block.
std::string compose(const std::string& text, const std::vector<ChatAttachment>& attachments);
/// Sends one message and appends the answer. The conversation is saved before the model is asked and again after,
/// so a stopped or failed request keeps the question. A long conversation is compacted first when the model's
/// context is nearly full.
/// With `tools`, the model may call the conversation's tools, at most 8 rounds per message, and then must answer.
void send(ChatConversation& conversation, const std::string& text, const std::vector<ChatAttachment>& attachments, ChatModel& model, ChatStore& store,
          const std::function<void(const std::string&)>& on_text, const CancelToken& cancel = nullptr, const ChatTools* tools = nullptr);

}  // namespace chat

}  // namespace mason
