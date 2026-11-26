// Finding where a name is defined and used. `navigate_code` reads the project's source as text and labels what it
// finds as candidates. `inspect_symbol` asks a real language service about one position: Jedi for Python, the
// TypeScript compiler for JavaScript and TypeScript. Both run on a copy of the source and never run project code.
#pragma once
#include "workspace.hpp"

namespace mason {

struct NavigationHit {
    std::string path;
    int line = 0;
    std::string kind, text;
};

struct NavigationResult {
    std::vector<NavigationHit> hits;
    size_t files = 0;
    bool partial = false;
};

namespace navigation {
/// Lines of the project's source that define, use or test `symbol`. These are text matches on whole words, not a
/// compiler's answer, and are labelled so. `mode` is definitions, references or tests.
NavigationResult query(const Workspace& workspace, const std::string& directory, const std::string& symbol, const std::string& mode, const CancelToken& cancel = nullptr);
/// The result as the model reads it.
std::string report(const NavigationResult& result, const std::string& symbol, const std::string& mode);
}  // namespace navigation

/// Jedi and the TypeScript compiler, run as helper programs on a copy of the project's source.
namespace symbols {
/// The folder holding python-symbols.py and typescript-symbols.cjs: beside the program as installed, or `MASON_LANGUAGE_TOOLS`.
fs::path folder();
/// True when at least one language can be answered here, so the tool is worth offering.
bool available();
/// Definitions, references or diagnostics at a 1-based position, as the helper's JSON.
std::string query(const Workspace& workspace, const std::string& path, int line, int column, const std::string& mode, const CancelToken& cancel = nullptr);
}  // namespace symbols

}  // namespace mason
