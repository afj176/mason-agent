#include "model.hpp"
#include "generation.hpp"

#include <algorithm>
#include <cstdio>
#include <regex>
#include <curl/curl.h>

namespace mason {

namespace {

struct CurlGlobal {
    CurlGlobal() { curl_global_init(CURL_GLOBAL_DEFAULT); }
    ~CurlGlobal() { curl_global_cleanup(); }
};
void ensure_curl() { static CurlGlobal once; }

struct Handle {
    CURL* curl = curl_easy_init();
    curl_slist* headers = nullptr;
    ~Handle() {
        if (headers) curl_slist_free_all(headers);
        if (curl) curl_easy_cleanup(curl);
    }
    void set_headers(const std::vector<std::string>& values) {
        for (const std::string& value : values) headers = curl_slist_append(headers, value.c_str());
        headers = curl_slist_append(headers, "User-Agent: MasonAgent");
        curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);
    }
};

int cancel_callback(void* user, curl_off_t, curl_off_t, curl_off_t, curl_off_t) {
    const CancelToken* token = static_cast<const CancelToken*>(user);
    return is_cancelled(*token) ? 1 : 0;
}

void watch_cancel(CURL* curl, const CancelToken* token) {
    curl_easy_setopt(curl, CURLOPT_NOPROGRESS, 0L);
    curl_easy_setopt(curl, CURLOPT_XFERINFOFUNCTION, cancel_callback);
    curl_easy_setopt(curl, CURLOPT_XFERINFODATA, token);
}

size_t append_body(char* data, size_t size, size_t count, void* user) {
    std::string* body = static_cast<std::string*>(user);
    if (body->size() + size * count > 32 * 1024 * 1024) return 0;
    body->append(data, size * count);
    return size * count;
}

std::string host_of(const std::string& url) {
    std::smatch match;
    static const std::regex pattern(R"(^https?://([^/:?#]+))", std::regex::icase);
    return std::regex_search(url, match, pattern) ? to_lower(match[1].str()) : "";
}

}  // namespace

HttpResponse http_get(const std::string& url, const std::vector<std::string>& headers, double timeout_seconds, const CancelToken& cancel) {
    ensure_curl();
    Handle handle;
    if (!handle.curl) throw Error("The network library could not start.");
    HttpResponse response;
    char error[CURL_ERROR_SIZE] = "";
    handle.set_headers(headers);
    curl_easy_setopt(handle.curl, CURLOPT_URL, url.c_str());
    curl_easy_setopt(handle.curl, CURLOPT_TIMEOUT_MS, static_cast<long>(timeout_seconds * 1000));
    curl_easy_setopt(handle.curl, CURLOPT_NOSIGNAL, 1L);
    curl_easy_setopt(handle.curl, CURLOPT_FOLLOWLOCATION, 0L);
    curl_easy_setopt(handle.curl, CURLOPT_WRITEFUNCTION, append_body);
    curl_easy_setopt(handle.curl, CURLOPT_WRITEDATA, &response.body);
    curl_easy_setopt(handle.curl, CURLOPT_ERRORBUFFER, error);
    watch_cancel(handle.curl, &cancel);
    const CURLcode code = curl_easy_perform(handle.curl);
    if (code == CURLE_ABORTED_BY_CALLBACK && is_cancelled(cancel)) throw Cancelled();
    if (code != CURLE_OK) throw Error("No answer from " + host_of(url) + ": " + (error[0] ? error : curl_easy_strerror(code)));
    curl_easy_getinfo(handle.curl, CURLINFO_RESPONSE_CODE, &response.status);
    return response;
}

namespace {
struct Capped {
    std::string body;
    size_t limit;
    bool over = false;
};
size_t append_capped(char* data, size_t size, size_t count, void* user) {
    Capped* sink = static_cast<Capped*>(user);
    if (sink->body.size() + size * count > sink->limit) { sink->over = true; return 0; }
    sink->body.append(data, size * count);
    return size * count;
}
size_t collect_header(char* data, size_t size, size_t count, void* user) {
    auto* found = static_cast<std::map<std::string, std::string>*>(user);
    const std::string line(data, size * count);
    const size_t colon = line.find(':');
    if (colon != std::string::npos) (*found)[to_lower(trim(line.substr(0, colon)))] = trim(line.substr(colon + 1));
    return size * count;
}
}  // namespace

HttpResponse http_send(const std::string& method, const std::string& url, const std::vector<std::string>& headers, const std::string& body,
                       double timeout_seconds, size_t max_bytes, std::map<std::string, std::string>* reply_headers, const CancelToken& cancel) {
    ensure_curl();
    Handle handle;
    if (!handle.curl) throw Error("The network library could not start.");
    Capped sink{"", max_bytes};
    char error[CURL_ERROR_SIZE] = "";
    handle.set_headers(headers);
    curl_easy_setopt(handle.curl, CURLOPT_URL, url.c_str());
    curl_easy_setopt(handle.curl, CURLOPT_CUSTOMREQUEST, method.c_str());
    if (method != "DELETE") {
        curl_easy_setopt(handle.curl, CURLOPT_POSTFIELDS, body.c_str());
        curl_easy_setopt(handle.curl, CURLOPT_POSTFIELDSIZE_LARGE, static_cast<curl_off_t>(body.size()));
    }
    curl_easy_setopt(handle.curl, CURLOPT_TIMEOUT_MS, static_cast<long>(timeout_seconds * 1000));
    curl_easy_setopt(handle.curl, CURLOPT_NOSIGNAL, 1L);
    curl_easy_setopt(handle.curl, CURLOPT_FOLLOWLOCATION, 0L);
    curl_easy_setopt(handle.curl, CURLOPT_PROXY, "");
    curl_easy_setopt(handle.curl, CURLOPT_WRITEFUNCTION, append_capped);
    curl_easy_setopt(handle.curl, CURLOPT_WRITEDATA, &sink);
    if (reply_headers) {
        curl_easy_setopt(handle.curl, CURLOPT_HEADERFUNCTION, collect_header);
        curl_easy_setopt(handle.curl, CURLOPT_HEADERDATA, reply_headers);
    }
    curl_easy_setopt(handle.curl, CURLOPT_ERRORBUFFER, error);
    watch_cancel(handle.curl, &cancel);
    const CURLcode code = curl_easy_perform(handle.curl);
    if (code == CURLE_ABORTED_BY_CALLBACK && is_cancelled(cancel)) throw Cancelled();
    if (sink.over) throw Error("The reply from " + host_of(url) + " is larger than Mason reads.");
    if (code != CURLE_OK) throw Error("No answer from " + host_of(url) + ": " + (error[0] ? error : curl_easy_strerror(code)));
    HttpResponse response;
    response.body = std::move(sink.body);
    curl_easy_getinfo(handle.curl, CURLINFO_RESPONSE_CODE, &response.status);
    return response;
}

namespace {
struct LineSink {
    const std::function<void(const std::string&)>* on_line;
    std::string buffer, error_body;
    CURL* curl;
    std::exception_ptr failure;
};
size_t line_writer(char* data, size_t size, size_t count, void* user) {
    LineSink* sink = static_cast<LineSink*>(user);
    long status = 0;
    curl_easy_getinfo(sink->curl, CURLINFO_RESPONSE_CODE, &status);
    if (status != 200) {
        if (sink->error_body.size() < 4000) sink->error_body.append(data, size * count);
        return size * count;
    }
    sink->buffer.append(data, size * count);
    size_t newline;
    try {
        while ((newline = sink->buffer.find('\n')) != std::string::npos) {
            std::string line = sink->buffer.substr(0, newline);
            sink->buffer.erase(0, newline + 1);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            (*sink->on_line)(line);
        }
    } catch (...) {
        sink->failure = std::current_exception();
        return 0;
    }
    return size * count;
}
}  // namespace

long http_post_lines(const std::string& url, const std::vector<std::string>& headers, const std::string& body,
                     const std::function<void(const std::string&)>& on_line, const CancelToken& cancel, std::string* error_body) {
    ensure_curl();
    Handle handle;
    if (!handle.curl) throw Error("The network library could not start.");
    LineSink sink{&on_line, "", "", handle.curl, nullptr};
    char error[CURL_ERROR_SIZE] = "";
    handle.set_headers(headers);
    curl_easy_setopt(handle.curl, CURLOPT_URL, url.c_str());
    curl_easy_setopt(handle.curl, CURLOPT_POST, 1L);
    curl_easy_setopt(handle.curl, CURLOPT_POSTFIELDS, body.data());
    curl_easy_setopt(handle.curl, CURLOPT_POSTFIELDSIZE_LARGE, static_cast<curl_off_t>(body.size()));
    curl_easy_setopt(handle.curl, CURLOPT_NOSIGNAL, 1L);
    curl_easy_setopt(handle.curl, CURLOPT_CONNECTTIMEOUT, 10L);
    // A model can take a long time before its first token; give up only when nothing at all arrives for ten minutes.
    curl_easy_setopt(handle.curl, CURLOPT_LOW_SPEED_LIMIT, 1L);
    curl_easy_setopt(handle.curl, CURLOPT_LOW_SPEED_TIME, 600L);
    curl_easy_setopt(handle.curl, CURLOPT_WRITEFUNCTION, line_writer);
    curl_easy_setopt(handle.curl, CURLOPT_WRITEDATA, &sink);
    curl_easy_setopt(handle.curl, CURLOPT_ERRORBUFFER, error);
    watch_cancel(handle.curl, &cancel);
    const CURLcode code = curl_easy_perform(handle.curl);
    if (sink.failure) std::rethrow_exception(sink.failure);
    if (code == CURLE_ABORTED_BY_CALLBACK && is_cancelled(cancel)) throw Cancelled();
    if (code != CURLE_OK) throw ModelUnavailable("The model server did not answer: " + std::string(error[0] ? error : curl_easy_strerror(code)));
    if (!sink.buffer.empty()) on_line(sink.buffer);
    long status = 0;
    curl_easy_getinfo(handle.curl, CURLINFO_RESPONSE_CODE, &status);
    if (error_body) *error_body = sink.error_body;
    return status;
}

namespace {
struct DownloadSink {
    std::FILE* file;
    Sha256 hash;
    int64_t received = 0, expected = 0;
    const std::function<void(int64_t, int64_t)>* progress;
    CURL* curl;
    std::string failure;
};
size_t download_writer(char* data, size_t size, size_t count, void* user) {
    DownloadSink* sink = static_cast<DownloadSink*>(user);
    long status = 0;
    curl_easy_getinfo(sink->curl, CURLINFO_RESPONSE_CODE, &status);
    if (status != 200) return size * count;  // a redirect or error body is not the file
    const size_t bytes = size * count;
    if (std::fwrite(data, 1, bytes, sink->file) != bytes) {
        sink->failure = "Could not write the download to disk.";
        return 0;
    }
    sink->hash.update(data, bytes);
    sink->received += static_cast<int64_t>(bytes);
    if (sink->expected > 0 && sink->received > sink->expected) {
        sink->failure = "The download is larger than declared.";
        return 0;
    }
    if (*sink->progress) (*sink->progress)(sink->received, std::max(sink->expected, sink->received));
    return bytes;
}
bool host_allowed(const std::string& url, const std::vector<std::string>& allowed) {
    if (!starts_with(url, "https://")) return false;
    const std::string host = host_of(url);
    return std::any_of(allowed.begin(), allowed.end(), [&](const std::string& a) { return host == a || ends_with(host, "." + a); });
}
}  // namespace

std::string http_download(const std::string& first_url, const fs::path& file, int64_t expected_bytes, const std::vector<std::string>& allowed_hosts,
                          const std::function<void(int64_t, int64_t)>& progress, const CancelToken& cancel) {
    ensure_curl();
    std::string url = first_url;
    // Redirects are followed by hand so each hop can be checked against the allowed hosts.
    for (int hop = 0; hop < 8; hop++) {
        if (!host_allowed(url, allowed_hosts)) throw Error("Refusing to download from " + (host_of(url).empty() ? std::string("an unknown host") : host_of(url)) + ".");
        Handle handle;
        if (!handle.curl) throw Error("The network library could not start.");
        std::FILE* out = std::fopen(file.c_str(), "wb");
        if (!out) throw Error("Cannot write " + file.string() + ".");
        DownloadSink sink{out, Sha256(), 0, expected_bytes, &progress, handle.curl, ""};
        char error[CURL_ERROR_SIZE] = "";
        handle.set_headers({});
        curl_easy_setopt(handle.curl, CURLOPT_URL, url.c_str());
        curl_easy_setopt(handle.curl, CURLOPT_NOSIGNAL, 1L);
        curl_easy_setopt(handle.curl, CURLOPT_FOLLOWLOCATION, 0L);
        curl_easy_setopt(handle.curl, CURLOPT_CONNECTTIMEOUT, 30L);
        curl_easy_setopt(handle.curl, CURLOPT_LOW_SPEED_LIMIT, 1L);
        curl_easy_setopt(handle.curl, CURLOPT_LOW_SPEED_TIME, 60L);
        curl_easy_setopt(handle.curl, CURLOPT_WRITEFUNCTION, download_writer);
        curl_easy_setopt(handle.curl, CURLOPT_WRITEDATA, &sink);
        curl_easy_setopt(handle.curl, CURLOPT_ERRORBUFFER, error);
        watch_cancel(handle.curl, &cancel);
        const CURLcode code = curl_easy_perform(handle.curl);
        std::fclose(out);
        if (code == CURLE_ABORTED_BY_CALLBACK && is_cancelled(cancel)) throw Cancelled();
        if (!sink.failure.empty()) throw Error(sink.failure);
        if (code != CURLE_OK) throw Error("The download failed: " + std::string(error[0] ? error : curl_easy_strerror(code)));
        long status = 0;
        curl_easy_getinfo(handle.curl, CURLINFO_RESPONSE_CODE, &status);
        if (status >= 300 && status < 400) {
            char* next = nullptr;
            curl_easy_getinfo(handle.curl, CURLINFO_REDIRECT_URL, &next);
            if (!next) throw Error("The download was redirected without a destination.");
            url = next;
            continue;
        }
        if (status == 401 || status == 403) throw Error("Access was refused (HTTP " + std::to_string(status) + "). The repository may need you to accept its terms on its model page.");
        if (status != 200) throw Error("The download failed (HTTP " + std::to_string(status) + ").");
        if (expected_bytes > 0 && sink.received != expected_bytes) {
            throw Error("The download ended early (" + std::to_string(sink.received) + " of " + std::to_string(expected_bytes) + " bytes).");
        }
        return sink.hash.hex();
    }
    throw Error("Too many download redirects.");
}

// ---- endpoint --------------------------------------------------------------------------------------------------

std::string validated_endpoint(const std::string& text) {
    static const std::regex pattern(R"(^(https?)://(\[[0-9a-fA-F:]+\]|[A-Za-z0-9.\-]+)(:(\d{1,5}))?(/[^?#]*)?$)");
    std::smatch match;
    const std::string trimmed = trim(text);
    if (!std::regex_match(trimmed, match, pattern)) throw Error("Use an http URL on this computer, such as http://127.0.0.1:8080.");
    std::string host = match[2].str();
    const std::string bare = host.front() == '[' ? host.substr(1, host.size() - 2) : host;
    // A literal loopback address only: a name such as 127.0.0.1.example.com resolves wherever its owner likes.
    static const std::regex loopback(R"(127(\.\d{1,3}){3})");
    if (!(to_lower(bare) == "localhost" || bare == "::1" || std::regex_match(bare, loopback))) throw Error("Local model connections require localhost or a loopback address.");
    const std::string path = match[5].str();
    if (!(path.empty() || path == "/" || path == "/v1" || path == "/v1/")) throw Error("Use the server's root URL or its /v1 base URL.");
    if (to_lower(bare) == "localhost") host = "127.0.0.1";
    return match[1].str() + "://" + host + (match[4].matched ? ":" + match[4].str() : "") + "/";
}

// ---- streaming -------------------------------------------------------------------------------------------------

std::string StreamAssembler::feed(const std::string& line) {
    if (!starts_with(line, "data:")) return "";
    const std::string payload = trim(line.substr(5));
    if (payload == "[DONE]" || payload.empty()) return "";
    const json object = json::parse(payload, nullptr, false);
    if (!object.is_object()) return "";
    if (object.contains("error") && object["error"].is_object()) throw Error("The model server reported: " + object["error"].value("message", "an error"));
    if (object.contains("usage") && object["usage"].is_object() && object["usage"].contains("prompt_tokens") && object["usage"]["prompt_tokens"].is_number()) {
        reply_.prompt_tokens = object["usage"]["prompt_tokens"].get<int>();
    }
    if (!object.contains("choices") || !object["choices"].is_array() || object["choices"].empty()) return "";
    const json& choice = object["choices"][0];
    if (choice.contains("finish_reason") && choice["finish_reason"].is_string()) reply_.finish_reason = choice["finish_reason"].get<std::string>();
    if (!choice.contains("delta") || !choice["delta"].is_object()) return "";
    const json& delta = choice["delta"];
    if (delta.contains("tool_calls") && delta["tool_calls"].is_array()) {
        for (const json& call : delta["tool_calls"]) {
            const size_t index = call.value("index", size_t{0});
            if (index > 16) continue;
            if (partial_.size() <= index) partial_.resize(index + 1);
            ToolCall& current = partial_[index];
            if (call.contains("id") && call["id"].is_string() && !call["id"].get<std::string>().empty()) current.id = call["id"].get<std::string>();
            if (call.contains("function") && call["function"].is_object()) {
                const json& function = call["function"];
                if (function.contains("name") && function["name"].is_string()) current.name += function["name"].get<std::string>();
                if (function.contains("arguments") && function["arguments"].is_string()) current.arguments += function["arguments"].get<std::string>();
            }
        }
    }
    if (delta.contains("content") && delta["content"].is_string()) {
        const std::string text = delta["content"].get<std::string>();
        reply_.text += text;
        return text;
    }
    return "";
}

ModelReply StreamAssembler::finish() {
    for (size_t index = 0; index < partial_.size(); index++) {
        ToolCall call = partial_[index];
        if (call.name.empty()) continue;
        if (call.id.empty()) call.id = "call_" + std::to_string(index);
        if (trim(call.arguments).empty()) call.arguments = "{}";
        reply_.tool_calls.push_back(std::move(call));
    }
    return reply_;
}

OpenAIClient::OpenAIClient(ModelEndpoint endpoint) : endpoint_(std::move(endpoint)), base_(validated_endpoint(endpoint_.url)) {
    // The generation settings the user keeps for this connection go with every request, unless a caller sets its own.
    extra_body_ = generation::body(endpoint_);
}

ModelReply OpenAIClient::complete(const std::vector<ChatMessage>& messages, const std::vector<ToolSpec>& tools,
                                  const std::function<void(const std::string&)>& on_text, const CancelToken& cancel) {
    json wire = json::array();
    for (const ChatMessage& message : messages) {
        json item = {{"role", message.role}, {"content", message.content}};
        if (!message.images.empty()) {
            // Text and pictures travel as parts of one message.
            item["content"] = json::array({{{"type", "text"}, {"text", message.content}}});
            for (const std::string& image : message.images) item["content"].push_back({{"type", "image_url"}, {"image_url", {{"url", image}}}});
        }
        if (!message.tool_calls.empty()) {
            item["tool_calls"] = json::array();
            for (const ToolCall& call : message.tool_calls) {
                item["tool_calls"].push_back({{"id", call.id}, {"type", "function"}, {"function", {{"name", call.name}, {"arguments", call.arguments}}}});
            }
        }
        if (!message.tool_call_id.empty()) item["tool_call_id"] = message.tool_call_id;
        wire.push_back(std::move(item));
    }
    json body = {{"model", endpoint_.model}, {"stream", true}, {"stream_options", {{"include_usage", true}}}, {"messages", wire}};
    // What a caller asked for beyond the usual: an output limit, a reasoning level.
    for (auto field = extra_body_.begin(); field != extra_body_.end(); ++field) body[field.key()] = field.value();
    if (!tools.empty()) {
        body["tools"] = json::array();
        for (const ToolSpec& tool : tools) {
            body["tools"].push_back({{"type", "function"}, {"function", {{"name", tool.name}, {"description", tool.description}, {"parameters", tool.parameters}}}});
        }
    }
    std::vector<std::string> headers = {"Content-Type: application/json"};
    if (!endpoint_.key.empty()) headers.push_back("Authorization: Bearer " + endpoint_.key);
    StreamAssembler assembler;
    std::string error_body;
    const long status = http_post_lines(base_ + "v1/chat/completions", headers, body.dump(-1, ' ', false, json::error_handler_t::replace),
                                        [&](const std::string& line) {
                                            const std::string text = assembler.feed(line);
                                            if (!text.empty() && on_text) on_text(text);
                                        },
                                        cancel, &error_body);
    if (status != 200) {
        const std::string detail = trim(error_body);
        throw ModelUnavailable("The model server returned HTTP " + std::to_string(status) + "." + (detail.empty() ? "" : " " + utf8_prefix(detail, 600)), static_cast<int>(status));
    }
    return assembler.finish();
}

int OpenAIClient::context_window() {
    try {
        const HttpResponse response = http_get(base_ + "props", {}, 3);
        if (response.status != 200) return 0;
        const json object = json::parse(response.body, nullptr, false);
        if (!object.is_object()) return 0;
        if (object.contains("default_generation_settings") && object["default_generation_settings"].is_object()) {
            const json& settings = object["default_generation_settings"];
            if (settings.contains("n_ctx") && settings["n_ctx"].is_number_integer()) return std::max(0, settings["n_ctx"].get<int>());
        }
        if (object.contains("n_ctx") && object["n_ctx"].is_number_integer()) return std::max(0, object["n_ctx"].get<int>());
    } catch (const std::exception&) {
    }
    return 0;
}

std::optional<std::vector<std::string>> OpenAIClient::models(const std::string& url, const std::string& key, double timeout_seconds) {
    try {
        std::vector<std::string> headers;
        if (!key.empty()) headers.push_back("Authorization: Bearer " + key);
        const HttpResponse response = http_get(validated_endpoint(url) + "v1/models", headers, timeout_seconds);
        if (response.status != 200) return std::nullopt;
        const json object = json::parse(response.body, nullptr, false);
        std::vector<std::string> ids;
        if (object.is_object() && object.contains("data") && object["data"].is_array()) {
            for (const json& item : object["data"]) {
                if (item.is_object() && item.contains("id") && item["id"].is_string()) ids.push_back(item["id"].get<std::string>());
            }
        }
        return ids;
    } catch (const Error&) {
        return std::nullopt;
    }
}

// ---- tool call recovery ----------------------------------------------------------------------------------------

std::optional<ToolCall> recover_tool_call(const std::string& text, const std::vector<ToolSpec>& tools) {
    std::vector<std::string> candidates;
    static const std::regex tagged(R"(<tool_call>\s*([\s\S]*?)\s*</tool_call>)");
    static const std::regex fenced(R"(```[A-Za-z_]*\s*\n([\s\S]*?)```)");
    for (const std::regex* pattern : {&tagged, &fenced}) {
        for (std::sregex_iterator it(text.begin(), text.end(), *pattern), end; it != end; ++it) candidates.push_back(trim((*it)[1].str()));
    }
    const std::string whole = trim(text);
    if (!whole.empty() && whole.front() == '{' && whole.back() == '}') candidates.push_back(whole);

    for (const std::string& candidate : candidates) {
        if (candidate.empty() || candidate.front() != '{') continue;
        const json object = json::parse(candidate, nullptr, false);
        if (!object.is_object()) continue;
        // {"name": ..., "arguments": {...}} or the OpenAI shape {"function": {"name": ..., "arguments": ...}}.
        const json& inner = object.contains("function") && object["function"].is_object() ? object["function"] : object;
        std::string name;
        if (inner.contains("name") && inner["name"].is_string()) name = inner["name"].get<std::string>();
        else if (inner.contains("tool") && inner["tool"].is_string()) name = inner["tool"].get<std::string>();
        if (std::none_of(tools.begin(), tools.end(), [&](const ToolSpec& tool) { return tool.name == name; })) continue;
        json arguments = inner.contains("arguments") ? inner["arguments"] : inner.contains("parameters") ? inner["parameters"] : json::object();
        if (arguments.is_string()) arguments = json::parse(arguments.get<std::string>(), nullptr, false);
        if (!arguments.is_object()) continue;
        return ToolCall{"recovered_" + new_id().substr(0, 8), name, arguments.dump()};
    }
    return std::nullopt;
}

}  // namespace mason
