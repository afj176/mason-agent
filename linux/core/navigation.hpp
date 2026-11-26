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

struct MapMatch {
    std::string path;
    int line = 0;
    std::string kind, name, text;
};

struct MapResult {
    std::vector<MapMatch> matches;
    size_t files = 0;
    bool partial = false;
};

namespace repository_map {
/// Where a name is defined, imported, used and tested, in the order a reader wants them: definitions, then imports,
/// callers and tests. `query` is an identifier or a qualified name such as `Cart.total`; the part before the last one
/// only ranks matches, by whether the file or the line mentions it. These are whole-word text matches.
MapResult query(const Workspace& workspace, const std::string& query, const std::string& directory = ".", size_t limit = 32, const CancelToken& cancel = nullptr);
/// The result as the model reads it.
std::string report(const MapResult& result, const std::string& query);
}  // namespace repository_map

/// Jedi and the TypeScript compiler, run as helper programs on a copy of the project's source.
namespace symbols {
/// The folder holding python-symbols.py and typescript-symbols.cjs: beside the program as installed, or `MASON_LANGUAGE_TOOLS`.
fs::path folder();
/// True when at least one language can be answered here, so the tool is worth offering.
bool available();
/// Definitions, references or diagnostics at a 1-based position, as the helper's JSON.
std::string query(const Workspace& workspace, const std::string& path, int line, int column, const std::string& mode, const CancelToken& cancel = nullptr);
/// True for the files Jedi or the TypeScript compiler answer for.
bool handles(const std::string& path);
}  // namespace symbols

/// For languages beyond Python and JavaScript, `inspect_symbol` asks the language server the user already has for that
/// language: csharp-ls or OmniSharp for C#, sourcekit-lsp for Swift, clangd for C and C++, gopls for Go, rust-analyzer
/// for Rust. The server is a program on this computer that reads the project, and some run the project's build, so it
/// is started the way a command is: inside the sandbox in Auto, and after asking otherwise.
struct LanguageServer {
    std::string name;
    std::vector<std::string> command;
    std::string language_id;
};

namespace language_servers {
/// The language server for a file with this extension (without the dot), when one is installed.
/// `MASON_LANGUAGE_SERVERS` can name one, as JSON such as {"cs": ["/opt/csharp-ls/csharp-ls"]}.
std::optional<LanguageServer> server_for(const std::string& extension);
/// True when some language server is installed, so the tool is worth offering.
bool available();
/// Everything the server is sent, in order: open the project and the file, ask the question, then leave.
std::string conversation(const std::string& root, const std::string& file, const std::string& text, const std::string& language_id, int line, int column, const std::string& mode);
/// The messages in what a server wrote.
std::vector<json> messages_in(const std::string& output);
/// Asks the server about one position. The server runs as a command does: `sandboxed` puts it in the sandbox.
std::string query(const Workspace& workspace, const LanguageServer& server, const std::string& path, int line, int column, const std::string& mode, bool sandboxed, const CancelToken& cancel = nullptr);
}  // namespace language_servers

}  // namespace mason
