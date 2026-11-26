// A bundle is a folder of skills and documentation that the user installs once and every project can then use. It
// holds `bundle.json` (`{"name": ..., "version": ..., "description": ...}`), `skills/NAME/SKILL.md` and `docs/*.md`.
// Only those text files are copied. A project's own documentation lives in `.mason/docs`.
#pragma once
#include "harness.hpp"
#include "workspace.hpp"

namespace mason {

struct InstalledBundle {
    std::string name, version, description;
    fs::path folder;
};

namespace bundles {

extern const char* const kUsage;

std::vector<InstalledBundle> list(const fs::path& home);
/// Copies a bundle into Mason's own folder, replacing an earlier version of the same name.
InstalledBundle install(const std::string& path, const fs::path& home);
void remove(const std::string& name, const fs::path& home);
/// The skills installed bundles bring.
std::vector<Definition> skills(const fs::path& home);
/// True when there is documentation to look things up in: the project's `.mason/docs` or an installed bundle's docs.
bool has_documents(const fs::path& home, const Workspace& workspace);
/// The sections that mention most of the query's words, each with where it came from, so an answer can cite it.
std::string read(const std::string& query, const fs::path& home, const Workspace& workspace);
/// The /bundle command, shared by the command line and the app.
std::string command(const std::string& rest, const fs::path& home);

}  // namespace bundles
}  // namespace mason
