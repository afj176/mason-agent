#include "catalogue.hpp"

#include "mcp.hpp"
#include "policy.hpp"

#include <algorithm>

namespace mason::tool_catalogue {

const std::vector<std::pair<std::string, std::string>>& groups() {
    static const std::vector<std::pair<std::string, std::string>> list = {
        {"core", "Core"}, {"code", "Code"}, {"docs", "Docs"}, {"workflows", "Workflows and skills"}, {"jobs", "Jobs and terminals"},
        {"browser", "Browser"}, {"desktop", "Desktop"}, {"mcp", "MCP"}, {"web", "Web"},
    };
    return list;
}

const std::vector<CatalogueTool>& all() {
    // name, group, line, changes, internet, always on
    static const std::vector<CatalogueTool> list = {
        {"list_files", "core", "List the project's files.", false, false, false},
        {"read_file", "core", "Read a text file with line numbers.", false, false, false},
        {"read_lines", "core", "Read part of a large file by line numbers.", false, false, false},
        {"search_files", "core", "Search the project's files for text.", false, false, false},
        {"search_next", "core", "Continue a search from where it stopped.", false, false, false},
        {"edit_file", "core", "Replace one passage in a file.", true, false, false},
        {"apply_edits", "core", "Make several replacements in one file at once.", true, false, false},
        {"write_file", "core", "Create a file or replace its content.", true, false, false},
        {"run_shell", "core", "Run a shell command in the project folder.", true, false, false},
        {"run_command", "core", "Run one program with exact arguments, without a shell.", true, false, false},
        {"navigate_code", "core", "Find where a name is defined, used or tested.", false, false, false},
        {"semantic_search", "core", "Find code by meaning, with an embeddings server.", false, false, false},
        {"ask_user", "core", "Ask you a question when it cannot go on without your answer.", false, false, false},
        {"suggest_note", "core", "Suggest a note about the project for you to keep.", false, false, false},
        {"open_file", "core", "Show you a file of the project at a line.", false, false, false},
        {"open_preview", "core", "Show you one of the project's pages in the Website tab.", false, false, false},
        {"enable_tools", "core", "Load a group of specialised tools when they are needed.", false, false, true},
        {"repository_map", "code", "An outline of the project, or where a name lives.", false, false, false},
        {"inspect_symbol", "code", "Ask a language server about one position in a file.", false, false, false},
        {"git_status", "code", "Show the Git branch and changed files.", false, false, false},
        {"delegate_readonly", "code", "Ask about a few files in a separate, read-only context.", false, false, false},
        {"read_documentation", "docs", "Look things up in the project's and bundles' documentation.", false, false, false},
        {"read_skill", "workflows", "Read the instructions of one skill.", false, false, false},
        {"run_workflow", "workflows", "Run one of the project's named workflows.", true, false, false},
        {"start_job", "jobs", "Start a long-running command in the background.", true, false, false},
        {"poll_job", "jobs", "Read a background job's output.", false, false, false},
        {"stop_job", "jobs", "Stop a background job.", false, false, false},
        {"start_terminal", "jobs", "Start a program that is answered line by line.", true, false, false},
        {"terminal_input", "jobs", "Send a line to a terminal.", true, false, false},
        {"terminal_status", "jobs", "Read a terminal's output.", false, false, false},
        {"terminal_stop", "jobs", "Stop a terminal.", false, false, false},
        {"check_page", "browser", "Load one of the project's pages in a real browser.", true, false, false},
        {"verify_acceptance", "browser", "Run the project's acceptance scenarios in a browser.", true, false, false},
        {"desktop_observe", "desktop", "Read the controls of a window you attached.", false, false, false},
        {"desktop_visual", "desktop", "Look at a picture of the attached window.", false, false, false},
        {"desktop_act", "desktop", "Press or set a control in the attached window, asking first.", true, false, false},
        {"mcp_tools", "mcp", "List what an MCP server offers.", false, false, false},
        {"mcp_call", "mcp", "Call an allowed tool on an MCP server, asking first.", true, false, false},
        {"mcp_resources", "mcp", "List the resources an MCP server offers.", false, false, false},
        {"mcp_read", "mcp", "Read an allowed resource from an MCP server.", false, false, false},
        {"web_fetch", "web", "Read a web page over HTTPS, asking before each new host.", false, true, false},
        {"web_search", "web", "Search the web with DuckDuckGo, asking first.", false, true, false},
    };
    return list;
}

const CatalogueTool* find(const std::string& name) {
    for (const CatalogueTool& tool : all()) {
        if (tool.name == name) return &tool;
    }
    return nullptr;
}

std::string command(const std::string& rest, const std::string& workspace, const fs::path& home) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    const std::string verb = words.empty() ? "" : to_lower(words[0]);
    if (verb == "on" || verb == "off") {
        if (words.size() < 2) throw Error("Use /tools " + verb + " NAME. /tools lists the names.");
        const CatalogueTool* tool = find(to_lower(words[1]));
        if (!tool) throw Error("No tool named " + words[1] + ". /tools lists them.");
        if (tool->always_on) throw Error(tool->name + " is always on: Mason cannot work without it.");
        policies::set_tool(tool->name, verb == "on", workspace, home);
        return tool->name + " is " + verb + " for this project.";
    }
    if (!verb.empty() && verb != "list") return "/tools                 Every tool, by group, on or off for this project\n/tools on NAME         Switch a tool back on\n/tools off NAME        Switch a tool off for this project";
    const std::vector<std::string> off = policies::disabled_tools(home, workspace);
    size_t width = 0;
    for (const CatalogueTool& tool : all()) width = std::max(width, tool.name.size());
    std::string text;
    for (const auto& [group, title] : groups()) {
        text += (text.empty() ? "" : "\n") + group;
        for (const CatalogueTool& tool : all()) {
            if (tool.group != group) continue;
            const bool is_off = !tool.always_on && std::find(off.begin(), off.end(), tool.name) != off.end();
            text += "\n  " + std::string(is_off ? "off" : "on ") + "  " + tool.name + std::string(width + 2 - tool.name.size(), ' ') + tool.line + (tool.always_on ? " (always on)" : "");
        }
    }
    std::vector<McpServer> servers;
    try {
        servers = mcp::all(home);
    } catch (const std::exception&) {
    }
    for (const McpServer& server : servers) {
        text += "\nmcp: " + server.name + " (" + std::to_string(server.tools.size()) + (server.tools.size() == 1 ? " tool allowed" : " tools allowed") + (server.enabled ? ")" : ", switched off)");
    }
    return text;
}

}  // namespace mason::tool_catalogue
