#include "mcp.hpp"

#include <algorithm>
#include <chrono>
#include <csignal>
#include <fcntl.h>
#include <poll.h>
#include <regex>
#include <set>
#include <sys/wait.h>
#include <unistd.h>

extern char** environ;

namespace mason {

std::string McpServer::transport() const {
    if (!is_program()) return endpoint;
    std::string text = executable;
    for (const std::string& argument : arguments) text += " " + argument;
    return text;
}
bool McpServer::allows_tool(const std::string& tool) const { return std::find(tools.begin(), tools.end(), tool) != tools.end(); }
bool McpServer::allows_resource(const std::string& uri) const { return std::find(resources.begin(), resources.end(), uri) != resources.end(); }

namespace mcp {
namespace {

constexpr size_t kReplyLimit = 128 * 1024;
constexpr double kDeadlineSeconds = 20;

fs::path file(const fs::path& home) { return home / "mcp-servers.json"; }

/// Keys are matched without regard to case, as the Windows app does.
const json* field(const json& object, const char* name) {
    for (auto item = object.begin(); item != object.end(); ++item) {
        if (to_lower(item.key()) == name) return &item.value();
    }
    return nullptr;
}

std::vector<std::string> strings(const json* value) {
    std::vector<std::string> found;
    if (value && value->is_array()) {
        for (const json& item : *value) {
            if (item.is_string()) found.push_back(item.get<std::string>());
        }
    }
    return found;
}

/// The reply with this id out of one JSON-RPC message; nothing when the message is something else.
std::optional<json> match(const std::string& text, int id) {
    const json object = json::parse(text, nullptr, false);
    if (!object.is_object() || !object.contains("id") || !object["id"].is_number_integer() || object["id"].get<int>() != id) return std::nullopt;
    if (object.contains("error")) throw Error("The MCP server answered with an error: " + object["error"].dump());
    if (!object.contains("result")) throw Error("The MCP server sent a reply without a result.");
    return object["result"];
}

struct Link {
    virtual ~Link() = default;
    /// Sends one message. With an id it waits for and returns that reply's result; id 0 is a notification.
    virtual json send(const std::string& method, const json& parameters, int id) = 0;
};

/// Streamable HTTP: each message is a POST, answered with JSON or a short event stream.
struct HttpLink : Link {
    const McpServer& server;
    const CancelToken& cancel;
    std::string session;
    HttpLink(const McpServer& s, const CancelToken& c) : server(s), cancel(c) {}

    ~HttpLink() override {
        if (session.empty()) return;
        try {
            http_send("DELETE", server.endpoint, {"Mcp-Session-Id: " + session, std::string("MCP-Protocol-Version: ") + kProtocolVersion}, "", 5, kReplyLimit);
        } catch (const std::exception&) {
        }
    }

    json send(const std::string& method, const json& parameters, int id) override {
        json message = {{"jsonrpc", "2.0"}, {"method", method}, {"params", parameters}};
        if (id) message["id"] = id;
        std::vector<std::string> headers = {"Content-Type: application/json", "Accept: application/json, text/event-stream"};
        if (!session.empty()) headers.push_back("Mcp-Session-Id: " + session);
        if (method != "initialize") headers.push_back(std::string("MCP-Protocol-Version: ") + kProtocolVersion);
        std::map<std::string, std::string> reply_headers;
        const HttpResponse response = http_send("POST", server.endpoint, headers, message.dump(), kDeadlineSeconds, kReplyLimit, &reply_headers, cancel);
        if (response.status < 200 || response.status >= 300) throw Error(server.name + " answered " + method + " with HTTP " + std::to_string(response.status) + ".");
        if (method == "initialize" && reply_headers.count("mcp-session-id")) session = reply_headers["mcp-session-id"];
        if (!id) return nullptr;
        if (starts_with(to_lower(reply_headers["content-type"]), "text/event-stream")) {
            const std::string body = replace_all(response.body, "\r\n", "\n");
            size_t from = 0;
            while (from <= body.size()) {
                const size_t end = body.find("\n\n", from);
                const std::string event = body.substr(from, end == std::string::npos ? std::string::npos : end - from);
                std::string data;
                for (const std::string& line : split(event, '\n', false)) {
                    if (starts_with(line, "data:")) data += (data.empty() ? "" : "\n") + trim(line.substr(5));
                }
                if (!data.empty()) {
                    if (auto result = match(data, id)) return *result;
                }
                if (end == std::string::npos) break;
                from = end + 2;
            }
        } else if (auto result = match(response.body, id)) {
            return *result;
        }
        throw Error(server.name + " did not answer " + method + ".");
    }
};

/// A server started as a program: one JSON message per line on its standard input and output.
struct ProgramLink : Link {
    const McpServer& server;
    const CancelToken& cancel;
    pid_t child = -1;
    int to_child = -1, from_child = -1;
    std::string pending;
    size_t received = 0;
    std::chrono::steady_clock::time_point deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(static_cast<int>(kDeadlineSeconds * 1000));

    ProgramLink(const McpServer& s, const CancelToken& c) : server(s), cancel(c) {
        int in[2], out[2];
        if (pipe2(in, O_CLOEXEC) != 0) throw Error(server.name + " could not be started.");
        if (pipe2(out, O_CLOEXEC) != 0) { close(in[0]); close(in[1]); throw Error(server.name + " could not be started."); }
        std::vector<std::string> arguments = {server.executable};
        arguments.insert(arguments.end(), server.arguments.begin(), server.arguments.end());
        std::vector<char*> argv;
        for (std::string& argument : arguments) argv.push_back(argument.data());
        argv.push_back(nullptr);
        // The same variables the Windows app removes: they would let the environment change what the server loads.
        std::vector<std::string> kept;
        for (char** variable = environ; variable && *variable; variable++) {
            const std::string entry = *variable;
            if (starts_with(entry, "NODE_OPTIONS=") || starts_with(entry, "NODE_PATH=") || starts_with(entry, "PYTHONPATH=") || starts_with(entry, "PYTHONHOME=")) continue;
            kept.push_back(entry);
        }
        std::vector<char*> envp;
        for (std::string& entry : kept) envp.push_back(entry.data());
        envp.push_back(nullptr);
        const std::string folder = fs::path(server.executable).parent_path().string();
        child = fork();
        if (child < 0) { close(in[0]); close(in[1]); close(out[0]); close(out[1]); throw Error(server.name + " could not be started."); }
        if (child == 0) {
            setsid();
            dup2(in[0], STDIN_FILENO);
            dup2(out[1], STDOUT_FILENO);
            const int null = open("/dev/null", O_WRONLY);
            if (null >= 0) dup2(null, STDERR_FILENO);
            if (chdir(folder.c_str()) != 0) _exit(126);
            execve(server.executable.c_str(), argv.data(), envp.data());
            _exit(127);
        }
        close(in[0]);
        close(out[1]);
        to_child = in[1];
        from_child = out[0];
    }

    ~ProgramLink() override {
        if (to_child >= 0) close(to_child);  // a well-behaved server exits when its input ends
        if (child > 0) {
            int status = 0;
            for (int waited = 0; waited < 25 && waitpid(child, &status, WNOHANG) == 0; waited++) usleep(10 * 1000);
            if (waitpid(child, &status, WNOHANG) == 0) {
                kill(-child, SIGKILL);
                waitpid(child, &status, 0);
            }
        }
        if (from_child >= 0) close(from_child);
    }

    json send(const std::string& method, const json& parameters, int id) override {
        json message = {{"jsonrpc", "2.0"}, {"method", method}, {"params", parameters}};
        if (id) message["id"] = id;
        const std::string line = message.dump() + "\n";
        if (line.size() > 32768) throw Error("The request to " + server.name + " is larger than 32 KiB.");
        // A server that has already gone must not end Mason with SIGPIPE.
        struct sigaction ignore {}, previous {};
        ignore.sa_handler = SIG_IGN;
        sigaction(SIGPIPE, &ignore, &previous);
        size_t written = 0;
        while (written < line.size()) {
            const ssize_t count = write(to_child, line.data() + written, line.size() - written);
            if (count <= 0) break;
            written += static_cast<size_t>(count);
        }
        sigaction(SIGPIPE, &previous, nullptr);
        if (written < line.size()) throw Error(server.name + " stopped before answering " + method + ".");
        if (!id) return nullptr;
        while (true) {
            size_t end;
            while ((end = pending.find('\n')) != std::string::npos) {
                const std::string text = trim(pending.substr(0, end));
                pending.erase(0, end + 1);
                if (text.empty()) continue;
                if (auto result = match(text, id)) return *result;
            }
            check_cancel(cancel);
            const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count();
            if (left <= 0) throw Error(server.name + " did not answer " + method + " within 20 seconds.");
            pollfd wait{from_child, POLLIN, 0};
            if (poll(&wait, 1, static_cast<int>(std::min<long long>(left, 200))) <= 0) continue;
            char buffer[4096];
            const ssize_t count = read(from_child, buffer, sizeof buffer);
            if (count <= 0) throw Error(server.name + " stopped before answering " + method + ".");
            received += static_cast<size_t>(count);
            if (received > kReplyLimit) throw Error("The reply from " + server.name + " is larger than 128 KiB.");
            pending.append(buffer, static_cast<size_t>(count));
        }
    }
};

}  // namespace

std::vector<McpServer> all(const fs::path& home) {
    const fs::path path = file(home);
    std::error_code failure;
    if (!fs::exists(path, failure)) return {};
    if (fs::file_size(path, failure) > 32768) throw Error("The MCP configuration is larger than 32 KiB.");
    const json parsed = json::parse(read_file(path), nullptr, false);
    if (!parsed.is_array()) throw Error("The MCP configuration in " + path.string() + " is not a list of servers.");
    std::vector<McpServer> servers;
    for (const json& item : parsed) {
        if (!item.is_object()) continue;
        McpServer server;
        const json* name = field(item, "name");
        if (!name || !name->is_string()) throw Error("An MCP registration has no name.");
        server.name = name->get<std::string>();
        if (const json* value = field(item, "endpoint"); value && value->is_string()) server.endpoint = value->get<std::string>();
        if (const json* value = field(item, "executable"); value && value->is_string()) server.executable = value->get<std::string>();
        if (const json* value = field(item, "enabled"); value && value->is_boolean()) server.enabled = value->get<bool>();
        server.tools = strings(field(item, "tools"));
        server.arguments = strings(field(item, "arguments"));
        server.resources = strings(field(item, "resources"));
        servers.push_back(std::move(server));
    }
    return servers;
}

static bool distinct(const std::vector<McpServer>& servers) {
    std::set<std::string> names;
    for (const McpServer& server : servers) names.insert(server.name);
    return names.size() == servers.size();
}

std::vector<McpServer> enabled(const fs::path& home) {
    std::vector<McpServer> usable;
    try {
        const std::vector<McpServer> servers = all(home);
        if (servers.size() > 16 || !distinct(servers)) return {};
        for (const McpServer& server : servers) {
            if (!server.enabled) continue;
            try {
                validate(server);
                usable.push_back(server);
            } catch (const Error&) {
            }
        }
    } catch (const std::exception&) {
    }
    return usable;
}

void save(const std::vector<McpServer>& servers, const fs::path& home) {
    if (servers.size() > 16 || !distinct(servers)) throw Error("Register up to 16 MCP servers with distinct names.");
    json list = json::array();
    for (const McpServer& server : servers) {
        if (server.enabled) validate(server);
        json item = {{"Name", server.name}, {"Endpoint", server.endpoint}, {"Tools", server.tools}, {"Enabled", server.enabled}};
        if (server.is_program()) {
            item["Executable"] = server.executable;
            item["Arguments"] = server.arguments;
        }
        if (!server.resources.empty()) item["Resources"] = server.resources;
        list.push_back(std::move(item));
    }
    const std::string text = list.dump(2);
    if (text.size() > 32768) throw Error("The MCP configuration is larger than 32 KiB.");
    fs::create_directories(home);
    write_file_atomic(file(home), text);
}

void validate(const McpServer& server) {
    const std::string name = trim(server.name);
    if (name.empty() || utf8_length(name) > 64 || name.find('/') != std::string::npos || server.tools.size() > 64) {
        throw Error("An MCP server needs a short name without a slash and at most 64 tools.");
    }
    if (server.resources.size() > 64) throw Error("The resource list of " + server.name + " is not valid.");
    for (const std::string& uri : server.resources) {
        if (trim(uri).empty() || uri.size() > 2000) throw Error("The resource list of " + server.name + " is not valid.");
    }
    if (server.is_program()) {
        bool long_argument = false;
        for (const std::string& argument : server.arguments) long_argument = long_argument || argument.size() > 4000;
        if (server.executable.front() != '/' || access(server.executable.c_str(), X_OK) != 0 || fs::is_directory(server.executable) || server.arguments.size() > 40 || long_argument) {
            throw Error("Register a program by its full path. " + server.executable + " is not a program Mason can start.");
        }
        return;
    }
    // A literal loopback address only: a name could be pointed somewhere else.
    static const std::regex pattern(R"(^http://(127(\.\d{1,3}){3}|\[::1\])(:\d{1,5})?(/[^#\s]*)?$)");
    if (!std::regex_match(server.endpoint, pattern)) {
        throw Error("Register an MCP server by its http address on this computer, such as http://127.0.0.1:8088/mcp.");
    }
}

std::string request(const McpServer& server, Operation operation, const std::string& name, const json& arguments, const CancelToken& cancel) {
    validate(server);
    if (operation == Operation::Call && !server.allows_tool(name)) throw Error(name + " is not in the tools allowed for " + server.name + ".");
    if (operation == Operation::Read && !server.allows_resource(name)) throw Error(name + " is not in the resources allowed for " + server.name + ".");
    std::unique_ptr<Link> link;
    if (server.is_program()) link = std::make_unique<ProgramLink>(server, cancel);
    else link = std::make_unique<HttpLink>(server, cancel);
    const json hello = link->send("initialize", {{"protocolVersion", kProtocolVersion}, {"capabilities", json::object()}, {"clientInfo", {{"name", "Mason Agent"}, {"version", "2.0"}}}}, 1);
    if (!hello.is_object() || hello.value("protocolVersion", "") != kProtocolVersion) throw Error(server.name + " does not speak MCP " + kProtocolVersion + ".");
    link->send("notifications/initialized", json::object(), 0);
    if (operation == Operation::Read) return link->send("resources/read", {{"uri", name}}, 2).dump();
    if (operation == Operation::Call) {
        const json result = link->send("tools/call", {{"name", name}, {"arguments", arguments}}, 2);
        if (result.is_object() && result.value("isError", false)) throw Error("The MCP tool reported a failure: " + result.dump());
        return result.dump();
    }
    const bool resources = operation == Operation::Resources || operation == Operation::AllResources;
    const bool everything = operation == Operation::AllTools || operation == Operation::AllResources;
    json found = json::array();
    std::string cursor;
    for (int page = 0; page < 16; page++) {
        const json listed = link->send(resources ? "resources/list" : "tools/list", cursor.empty() ? json::object() : json{{"cursor", cursor}}, 2 + page);
        const char* key = resources ? "resources" : "tools";
        if (listed.is_object() && listed.contains(key) && listed[key].is_array()) {
            for (const json& item : listed[key]) {
                const std::string id = item.is_object() ? item.value(resources ? "uri" : "name", "") : "";
                if (everything || (resources ? server.allows_resource(id) : server.allows_tool(id))) found.push_back(item);
            }
        }
        cursor = listed.is_object() && listed.contains("nextCursor") && listed["nextCursor"].is_string() ? listed["nextCursor"].get<std::string>() : "";
        if (cursor.empty()) return found.dump();
    }
    throw Error(server.name + " listed more pages than Mason reads.");
}

std::vector<McpTool> discover(const McpServer& server, const CancelToken& cancel) {
    std::vector<McpTool> tools;
    const json listed = json::parse(request(server, Operation::AllTools, "", json::object(), cancel), nullptr, false);
    if (listed.is_array()) {
        for (const json& item : listed) {
            if (item.is_object()) tools.push_back({item.value("name", ""), item.contains("description") && item["description"].is_string() ? item["description"].get<std::string>() : ""});
        }
    }
    return tools;
}

}  // namespace mcp

}  // namespace mason
