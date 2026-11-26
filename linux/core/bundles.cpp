#include "bundles.hpp"

#include <algorithm>
#include <cctype>
#include <regex>
#include <set>

namespace mason::bundles {

namespace {

constexpr size_t kMaxFiles = 200, kMaxBytes = 8 * 1024 * 1024;
const std::regex kName("^[A-Za-z0-9][A-Za-z0-9_.-]{0,47}$");

fs::path root(const fs::path& home) { return home / "bundles"; }

std::vector<std::string> names_in(const fs::path& folder) {
    std::vector<std::string> names;
    std::error_code failure;
    for (const fs::directory_entry& entry : fs::directory_iterator(folder, failure)) names.push_back(entry.path().filename().string());
    std::sort(names.begin(), names.end());
    return names;
}

bool markdown(const std::string& name) { return name.size() > 3 && to_lower(name.substr(name.size() - 3)) == ".md"; }

std::optional<InstalledBundle> manifest(const fs::path& folder) {
    std::error_code failure;
    const fs::path file = folder / "bundle.json";
    if (!fs::is_regular_file(file, failure) || fs::file_size(file, failure) > 64 * 1024) return std::nullopt;
    const json value = json::parse(read_file(file), nullptr, false);
    if (!value.is_object()) return std::nullopt;
    InstalledBundle found{value.value("name", ""), value.value("version", ""), value.value("description", ""), folder};
    if (!std::regex_match(found.name, kName) || !std::regex_match(found.version, std::regex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,39}$"))) return std::nullopt;
    if (utf8_length(found.description) > 300) found.description = utf8_prefix(found.description, 300);
    return found;
}

/// The text files a bundle is made of, relative to its folder.
std::vector<std::string> files_in(const fs::path& folder) {
    std::vector<std::string> found = {"bundle.json"};
    if (fs::exists(folder / "mcp.json")) found.push_back("mcp.json");
    for (const std::string& skill : names_in(folder / "skills")) {
        if (std::regex_match(skill, kName) && fs::exists(folder / "skills" / skill / "SKILL.md")) found.push_back("skills/" + skill + "/SKILL.md");
    }
    for (const std::string& document : names_in(folder / "docs")) {
        if (markdown(document) && std::regex_match(document, std::regex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,80}$"))) found.push_back("docs/" + document);
    }
    size_t total = 0;
    for (const std::string& path : found) {
        std::error_code failure;
        // A link could point anywhere on the computer, so only real files are taken.
        if (fs::is_symlink(folder / path, failure) || !fs::is_regular_file(folder / path, failure)) throw Error(path + " in the bundle is not an ordinary file.");
        total += fs::file_size(folder / path, failure);
    }
    if (found.size() > kMaxFiles || total > kMaxBytes) throw Error("A bundle holds at most 200 files and 8 MiB.");
    return found;
}

struct Document {
    std::string source, text;
};

/// Every document to look things up in: the project's `.mason/docs`, then each installed bundle's docs.
std::vector<Document> documents(const fs::path& home, const Workspace& workspace) {
    std::vector<Document> found;
    for (const std::string& name : names_in(fs::path(workspace.root()) / ".mason" / "docs")) {
        if (!markdown(name)) continue;
        try {
            found.push_back({".mason/docs/" + name, workspace.read(".mason/docs/" + name)});
        } catch (const std::exception&) {
        }
    }
    for (const InstalledBundle& bundle : list(home)) {
        for (const std::string& name : names_in(bundle.folder / "docs")) {
            std::error_code failure;
            if (!markdown(name) || fs::file_size(bundle.folder / "docs" / name, failure) > 1024 * 1024) continue;
            found.push_back({bundle.name + " " + bundle.version + "/docs/" + name, read_file(bundle.folder / "docs" / name)});
        }
    }
    return found;
}

}  // namespace

const char* const kUsage =
    "/bundle                    The bundles installed on this computer\n"
    "/bundle install FOLDER     Install a bundle: its skills and docs become available in every project\n"
    "/bundle servers NAME       The MCP servers a bundle names\n"
    "/bundle servers NAME add   Add them to your MCP servers, switched off; switch one on in Settings once you have looked at it\n"
    "/bundle remove NAME        Remove one\n"
    "A bundle is a folder with bundle.json ({\"name\": ..., \"version\": ...}), skills/NAME/SKILL.md and docs/*.md.";

std::vector<InstalledBundle> list(const fs::path& home) {
    std::vector<InstalledBundle> found;
    for (const std::string& name : names_in(root(home))) {
        const auto bundle = manifest(root(home) / name);
        if (bundle && bundle->name == name) found.push_back(*bundle);
    }
    return found;
}

InstalledBundle install(const std::string& given, const fs::path& home) {
    std::string path = given;
    if (path.rfind("~/", 0) == 0) path = home_dir() + path.substr(1);
    const fs::path source = fs::absolute(path).lexically_normal();
    const auto found = manifest(source);
    if (!found) throw Error("That folder has no valid bundle.json. It needs a name (letters, digits, - or _) and a version.");
    const std::vector<std::string> paths = files_in(source);
    if (paths.size() < 2) throw Error("The bundle " + found->name + " has no skills or docs.");
    const fs::path staging = root(home) / (".installing-" + new_id());
    std::error_code ignored;
    try {
        for (const std::string& relative : paths) {
            fs::create_directories((staging / relative).parent_path());
            fs::copy_file(source / relative, staging / relative);
        }
        const fs::path place = root(home) / found->name;
        fs::remove_all(place, ignored);
        fs::rename(staging, place);
        return InstalledBundle{found->name, found->version, found->description, place};
    } catch (...) {
        fs::remove_all(staging, ignored);
        throw;
    }
}

void remove(const std::string& name, const fs::path& home) {
    for (const InstalledBundle& bundle : list(home)) {
        if (to_lower(bundle.name) != to_lower(name)) continue;
        fs::remove_all(bundle.folder);
        return;
    }
    throw Error("No bundle named " + name + " is installed.");
}

std::vector<McpServer> servers(const InstalledBundle& bundle) {
    std::vector<McpServer> found;
    try {
        found = mcp::read_list(bundle.folder / "mcp.json");
    } catch (const Error&) {
        throw Error("The mcp.json of " + bundle.name + " is not a list of up to 16 MCP servers.");
    }
    if (found.size() > 16) throw Error("The mcp.json of " + bundle.name + " is not a list of up to 16 MCP servers.");
    return found;
}

std::vector<std::string> add_servers(const InstalledBundle& bundle, const fs::path& home) {
    std::vector<McpServer> registered = mcp::all(home);
    std::vector<std::string> added;
    for (McpServer server : servers(bundle)) {
        if (std::any_of(registered.begin(), registered.end(), [&](const McpServer& existing) { return existing.name == server.name; })) continue;
        mcp::validate(server);
        server.enabled = false;
        registered.push_back(server);
        added.push_back(server.name);
    }
    if (!added.empty()) mcp::save(registered, home);
    return added;
}

std::vector<Definition> skills(const fs::path& home) {
    std::vector<Definition> found;
    for (const InstalledBundle& bundle : list(home)) {
        for (const std::string& skill : names_in(bundle.folder / "skills")) {
            std::error_code failure;
            const fs::path file = bundle.folder / "skills" / skill / "SKILL.md";
            if (!std::regex_match(skill, kName) || !fs::is_regular_file(file, failure) || fs::file_size(file, failure) > 256 * 1024) continue;
            found.push_back(customization::parse(skill, read_file(file), "bundle " + bundle.name + " " + bundle.version));
        }
    }
    return found;
}

bool has_documents(const fs::path& home, const Workspace& workspace) { return !documents(home, workspace).empty(); }

std::string read(const std::string& query, const fs::path& home, const Workspace& workspace) {
    std::set<std::string> words;
    std::string word;
    for (unsigned char c : to_lower(query) + " ") {
        if (std::isalnum(c) || c >= 0x80) { word += static_cast<char>(c); continue; }
        if (word.size() >= 3) words.insert(word);
        word.clear();
    }
    if (words.empty()) return "Give a few words to look for.";
    struct Section {
        int score;
        std::string source;
        size_t line;
        std::string text;
    };
    std::vector<Section> sections;
    for (const Document& document : documents(home, workspace)) {
        // A section runs from one heading to the next.
        size_t start = 1;
        std::string current;
        auto close = [&] {
            const std::string text = trim(current);
            if (text.empty()) return;
            const std::string lowered = to_lower(text);
            int score = 0;
            for (const std::string& wanted : words) score += lowered.find(wanted) != std::string::npos ? 1 : 0;
            if (score > 0) sections.push_back({score, document.source, start, text});
        };
        const std::vector<std::string> lines = split(document.text, '\n');
        for (size_t index = 0; index < lines.size(); index++) {
            if (!lines[index].empty() && lines[index][0] == '#' && !current.empty()) {
                close();
                current.clear();
                start = index + 1;
            }
            current += lines[index] + "\n";
        }
        close();
    }
    if (sections.empty()) return "The documentation does not mention that. Try other words.";
    std::stable_sort(sections.begin(), sections.end(), [](const Section& a, const Section& b) { return a.score > b.score; });
    std::string answer;
    for (size_t i = 0; i < sections.size() && i < 3; i++) {
        const Section& section = sections[i];
        answer += "[" + section.source + ":" + std::to_string(section.line) + "]\n" + (utf8_length(section.text) > 1500 ? utf8_prefix(section.text, 1500) + "\n[Section clipped.]" : section.text) + "\n\n";
    }
    return answer + "Cite what you use by the name in brackets.";
}

std::string command(const std::string& rest, const fs::path& home) {
    const std::string trimmed = trim(rest);
    const size_t space = trimmed.find(' ');
    const std::string verb = to_lower(trimmed.substr(0, space)), argument = space == std::string::npos ? "" : trim(trimmed.substr(space + 1));
    if (verb.empty()) {
        const std::vector<InstalledBundle> installed = list(home);
        if (installed.empty()) return std::string("No bundles are installed.\n") + kUsage;
        std::string text;
        for (const InstalledBundle& bundle : installed) text += (text.empty() ? "" : "\n") + bundle.name + " " + bundle.version + (bundle.description.empty() ? "" : "  ·  " + bundle.description);
        return text;
    }
    if (verb == "install") {
        if (argument.empty()) throw Error("Use /bundle install FOLDER.");
        const InstalledBundle made = install(argument, home);
        const std::vector<McpServer> named = servers(made);
        const size_t count = files_in(made.folder).size() - 1 - (named.empty() ? 0 : 1);
        std::string said = "Installed " + made.name + " " + made.version + ": " + std::to_string(count) + (count == 1 ? " file" : " files") + " of skills and docs.";
        if (!named.empty()) {
            std::vector<std::string> names;
            for (const McpServer& server : named) names.push_back(server.name);
            said += "\nIt names " + std::to_string(named.size()) + (named.size() == 1 ? " MCP server (" : " MCP servers (") + join(names, ", ") + "). They are not in use: /bundle servers " + made.name + " shows them.";
        }
        return said;
    }
    if (verb == "servers") {
        const std::vector<std::string> words = split(argument, ' ', false);
        std::optional<InstalledBundle> bundle;
        for (const InstalledBundle& item : list(home)) {
            if (!words.empty() && to_lower(item.name) == to_lower(words[0])) bundle = item;
        }
        if (!bundle) throw Error("Use /bundle servers NAME, with the name of an installed bundle.");
        const std::vector<McpServer> named = servers(*bundle);
        if (named.empty()) return bundle->name + " names no MCP servers.";
        if (words.size() > 1 && to_lower(words[1]) == "add") {
            const std::vector<std::string> added = add_servers(*bundle, home);
            return added.empty() ? "Those servers are already in your list." : "Added, switched off: " + join(added, ", ") + ". Look at each and switch it on in Settings, under MCP servers.";
        }
        std::string text;
        for (const McpServer& server : named) text += server.name + "  ·  " + server.transport() + "  ·  tools: " + (server.tools.empty() ? std::string("none listed") : join(server.tools, ", ")) + "\n";
        return text + "/bundle servers " + bundle->name + " add puts them in your list, switched off.";
    }
    if (verb == "remove") {
        remove(argument, home);
        return "Removed " + argument + ".";
    }
    return kUsage;
}

}  // namespace mason::bundles
