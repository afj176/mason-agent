// The one list of every built-in tool the agent (or Chat) can be offered: its group, a plain line on what it does,
// and whether it changes files or runs commands, or uses the internet. The app and the command line switch tools off
// for a project through this list, so nobody has to type a tool's name.
#pragma once
#include "workspace.hpp"

namespace mason {

struct CatalogueTool {
    std::string name;
    std::string group;  ///< core, code, docs, workflows, jobs, browser, desktop, mcp or web
    std::string line;   ///< What it does, in plain words.
    bool changes = false;   ///< Changes files or runs commands.
    bool internet = false;  ///< Uses the internet.
    bool always_on = false; ///< The loop cannot run without it, so it cannot be switched off.
};

namespace tool_catalogue {

/// The groups in the order they are shown, each with a title.
const std::vector<std::pair<std::string, std::string>>& groups();
const std::vector<CatalogueTool>& all();
const CatalogueTool* find(const std::string& name);
/// /tools: the tools by group with on or off, then the MCP servers. /tools on NAME and /tools off NAME change the
/// project's disabledTools.
std::string command(const std::string& rest, const std::string& workspace, const fs::path& home);

}  // namespace tool_catalogue
}  // namespace mason
