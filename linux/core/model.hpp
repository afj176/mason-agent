// Talking to a local model server, and the small HTTP layer under it.
#pragma once
#include "session.hpp"

namespace mason {

// ---- HTTP ------------------------------------------------------------------------------------------------------

struct HttpResponse {
    long status = 0;
    std::string body;
};
/// A small GET. Throws Error when nothing answers.
HttpResponse http_get(const std::string& url, const std::vector<std::string>& headers = {}, double timeout_seconds = 30, const CancelToken& cancel = nullptr);
/// POSTs a body and hands each line of the reply to `on_line` as it arrives. Returns the status; a non-200 body goes to `error_body`.
long http_post_lines(const std::string& url, const std::vector<std::string>& headers, const std::string& body,
                     const std::function<void(const std::string&)>& on_line, const CancelToken& cancel, std::string* error_body);
/// Streams an https file to disk and returns its SHA-256. Redirects are followed only to the hosts given.
std::string http_download(const std::string& url, const fs::path& file, int64_t expected_bytes, const std::vector<std::string>& allowed_hosts,
                          const std::function<void(int64_t done, int64_t total)>& progress, const CancelToken& cancel);

// ---- models ----------------------------------------------------------------------------------------------------

struct ToolSpec {
    std::string name, description;
    json parameters;  ///< JSON Schema of the arguments object.
};

struct ModelReply {
    std::string text;
    std::vector<ToolCall> tool_calls;
    int prompt_tokens = 0;
    std::string finish_reason;
};

/// Anything that can produce the next assistant message. The engine and its tests depend only on this.
class ChatModel {
public:
    virtual ~ChatModel() = default;
    virtual std::string name() const = 0;
    virtual ModelReply complete(const std::vector<ChatMessage>& messages, const std::vector<ToolSpec>& tools,
                                const std::function<void(const std::string&)>& on_text, const CancelToken& cancel) = 0;
    /// How many tokens the model can take in one request, or 0 when the server does not say. Automatic compaction needs it.
    virtual int context_window() { return 0; }
};

struct ModelEndpoint {
    std::string url, model, key;
    bool operator==(const ModelEndpoint& other) const { return url == other.url && model == other.model; }
};
/// Mason only talks to models on this computer: http(s) on localhost or a literal loopback address.
/// Returns the server root, such as "http://127.0.0.1:8080/".
std::string validated_endpoint(const std::string& text);

/// Assembles a streamed chat completion from server-sent events. Separate from the network so it can be tested with recorded lines.
class StreamAssembler {
public:
    /// Feeds one line of the event stream. Returns newly received assistant text, if any.
    std::string feed(const std::string& line);
    ModelReply finish();
private:
    ModelReply reply_;
    std::vector<ToolCall> partial_;
};

/// An OpenAI-compatible chat completions client (llama.cpp, LM Studio, Ollama and others) with streaming and native tool calls.
class OpenAIClient : public ChatModel {
public:
    explicit OpenAIClient(ModelEndpoint endpoint);
    std::string name() const override { return endpoint_.model; }
    ModelReply complete(const std::vector<ChatMessage>& messages, const std::vector<ToolSpec>& tools,
                        const std::function<void(const std::string&)>& on_text, const CancelToken& cancel) override;
    /// llama.cpp reports the loaded context size at /props. Other servers do not, and then compaction stays manual.
    int context_window() override;
    /// Model IDs the server lists, or nothing when it does not answer.
    static std::optional<std::vector<std::string>> models(const std::string& url, const std::string& key = "", double timeout_seconds = 2);
private:
    ModelEndpoint endpoint_;
    std::string base_;
};

/// Some local models write a tool call as text (fenced JSON, or inside <tool_call> tags) instead of using the server's
/// tool-call channel. When the text names one of the offered tools, that call is recovered so the request can continue.
std::optional<ToolCall> recover_tool_call(const std::string& text, const std::vector<ToolSpec>& tools);

}  // namespace mason
