// MCP servers the user registered, and a client that connects, does one thing and disconnects.
#pragma once
#include "model.hpp"

namespace mason {

/// One MCP server the user registered. Servers and their tool allowlists live in Mason's data folder
/// (`mcp-servers.json`), never in a project, so a project cannot add a server or widen what one may do.
/// The file has the same shape as on Windows.
struct McpServer {
    std::string name;
    std::string endpoint;                 ///< For an HTTP server: its address on this computer, such as http://127.0.0.1:8088/mcp.
    std::vector<std::string> tools;       ///< The tools the agent may call.
    std::string executable;               ///< For a server started as a program: its full path. Empty for HTTP.
    std::vector<std::string> arguments;
    std::vector<std::string> resources;   ///< The resource URIs the agent may read.
    bool enabled = true;

    bool is_program() const { return !executable.empty(); }
    /// How the server is reached, for display and approvals.
    std::string transport() const;
    bool allows_tool(const std::string& tool) const;
    bool allows_resource(const std::string& uri) const;
};

struct McpTool {
    std::string name, description;
};

namespace mcp {

constexpr const char* kProtocolVersion = "2025-06-18";

/// Every registration, including servers that are switched off. Throws Error when the file cannot be read.
std::vector<McpServer> all(const fs::path& home);
/// The servers the agent may use: switched on and valid. A broken file gives no servers.
std::vector<McpServer> enabled(const fs::path& home);
void save(const std::vector<McpServer>& servers, const fs::path& home);
void validate(const McpServer& server);

enum class Operation { Tools, AllTools, Call, Resources, AllResources, Read };
/// Connects, does one thing and disconnects. `name` is the tool or the resource URI; `arguments` is the tool's
/// JSON object. Returns the result as JSON text.
std::string request(const McpServer& server, Operation operation, const std::string& name = "", const json& arguments = json::object(), const CancelToken& cancel = nullptr);
/// Every tool the server offers, whatever the allowlist says, so the user can choose.
std::vector<McpTool> discover(const McpServer& server, const CancelToken& cancel = nullptr);

}  // namespace mcp

}  // namespace mason
