using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mason.Code;

// Mason speaks the Language Server Protocol to the language servers on this computer, the way editors do. A server
// is started the first time a request touches a file of its language and stays running until the request ends.
// After each edit the file is handed to it, and the errors it reports go back to the model with the edit's result.
// inspect_symbol asks the same server where a name is defined or used.
//
// A language server reads the project, and some run the project's build. The Windows sandbox admits only a few
// runtimes and gives no live pipe to a program inside it, so a server runs with the user's account and is asked
// about first, once a request, unless a saved rule already allows it. Mason ships none of them. /languages shows
// which are installed and installs the ones a language's own tools can fetch.

public sealed record CodeLanguageServer(string Name, string[] Command, string LanguageId);

/// <summary>A language and how its server is found and got. Tool is npm, go, dotnet, rustup or gem, or empty when only Hint applies.</summary>
public sealed record CodeLanguage(string Key, string Name, string[] Extensions, string LanguageId, string[][] Candidates, string Tool, string[] Packages, string Hint, Dictionary<string, string>? Others = null);

public sealed record CodeLanguageState(string? Installed, string Action);

public static class CodeLanguageServers
{
    static CodeLanguage With(string key, string name, string[] extensions, string id, string[][] candidates, string tool, string[] packages, Dictionary<string, string>? others = null) => new(key, name, extensions, id, candidates, tool, packages, "", others);
    static CodeLanguage Told(string key, string name, string[] extensions, string id, string[][] candidates, string hint, Dictionary<string, string>? others = null) => new(key, name, extensions, id, candidates, "", [], hint, others);
    static string Packaged(string what) => "Install " + what + " and put it on the PATH.";

    public static readonly CodeLanguage[] Known =
    [
        With("csharp", "C#", ["cs"], "csharp", [["csharp-ls"], ["OmniSharp", "-lsp"]], "dotnet", ["csharp-ls"]),
        With("fsharp", "F#", ["fs", "fsi", "fsx"], "fsharp", [["fsautocomplete"]], "dotnet", ["fsautocomplete"]),
        Told("swift", "Swift", ["swift"], "swift", [["sourcekit-lsp"]], "sourcekit-lsp comes with Swift: install the Swift toolchain."),
        Told("c", "C", ["c", "h"], "c", [["clangd"]], "Install clangd, which comes with LLVM, for example with `winget install LLVM.LLVM`."),
        Told("cpp", "C++ and Objective-C", ["cc", "cpp", "cxx", "hpp", "hh", "hxx", "m", "mm"], "cpp", [["clangd"]], "Install clangd, which comes with LLVM, for example with `winget install LLVM.LLVM`.", new() { ["m"] = "objective-c", ["mm"] = "objective-cpp" }),
        With("go", "Go", ["go"], "go", [["gopls"]], "go", ["golang.org/x/tools/gopls@latest"]),
        With("rust", "Rust", ["rs"], "rust", [["rust-analyzer"]], "rustup", ["rust-analyzer"]),
        With("python", "Python", ["py", "pyi"], "python", [["pyright-langserver", "--stdio"], ["basedpyright-langserver", "--stdio"]], "npm", ["pyright"]),
        Told("java", "Java", ["java"], "java", [["jdtls"]], Packaged("the Eclipse JDT language server (jdtls) and a JDK")),
        Told("kotlin", "Kotlin", ["kt", "kts"], "kotlin", [["kotlin-lsp", "--stdio"], ["kotlin-language-server"]], Packaged("kotlin-language-server")),
        With("php", "PHP", ["php"], "php", [["intelephense", "--stdio"], ["phpactor", "language-server"]], "npm", ["intelephense"]),
        With("ruby", "Ruby", ["rb", "rake", "gemspec"], "ruby", [["ruby-lsp"], ["solargraph", "stdio"]], "gem", ["ruby-lsp"]),
        Told("lua", "Lua", ["lua"], "lua", [["lua-language-server"]], Packaged("lua-language-server")),
        Told("zig", "Zig", ["zig"], "zig", [["zls"]], Packaged("zls")),
        Told("dart", "Dart", ["dart"], "dart", [["dart", "language-server", "--protocol=lsp"]], "The Dart language server comes with the Dart or Flutter SDK."),
        Told("elixir", "Elixir", ["ex", "exs"], "elixir", [["elixir-ls"], ["language_server"]], Packaged("elixir-ls")),
        Told("erlang", "Erlang", ["erl", "hrl"], "erlang", [["elp", "server"], ["erlang_ls"]], Packaged("erlang_ls")),
        Told("haskell", "Haskell", ["hs", "lhs"], "haskell", [["haskell-language-server-wrapper", "--lsp"]], "Install haskell-language-server with ghcup: `ghcup install hls`."),
        Told("ocaml", "OCaml", ["ml", "mli"], "ocaml", [["ocamllsp"]], "Install it with opam: `opam install ocaml-lsp-server`, and start Mason from a shell where `ocamllsp` is on the PATH."),
        Told("scala", "Scala", ["scala", "sbt", "sc"], "scala", [["metals"]], "Install Metals with Coursier: `cs install metals`."),
        Told("clojure", "Clojure", ["clj", "cljs", "cljc", "edn"], "clojure", [["clojure-lsp"]], Packaged("clojure-lsp")),
        With("shell", "Shell scripts", ["sh", "bash"], "shellscript", [["bash-language-server", "start"]], "npm", ["bash-language-server"]),
        With("yaml", "YAML", ["yaml", "yml"], "yaml", [["yaml-language-server", "--stdio"]], "npm", ["yaml-language-server"]),
        With("json", "JSON", ["json", "jsonc"], "json", [["vscode-json-language-server", "--stdio"]], "npm", ["vscode-langservers-extracted"], new() { ["jsonc"] = "jsonc" }),
        With("html", "HTML", ["html", "htm"], "html", [["vscode-html-language-server", "--stdio"]], "npm", ["vscode-langservers-extracted"]),
        With("css", "CSS, SCSS and Less", ["css", "scss", "less"], "css", [["vscode-css-language-server", "--stdio"]], "npm", ["vscode-langservers-extracted"], new() { ["scss"] = "scss", ["less"] = "less" }),
        With("svelte", "Svelte", ["svelte"], "svelte", [["svelteserver", "--stdio"]], "npm", ["svelte-language-server"]),
        With("docker", "Dockerfiles", ["dockerfile"], "dockerfile", [["docker-langserver", "--stdio"]], "npm", ["dockerfile-language-server-nodejs"]),
        Told("terraform", "Terraform", ["tf", "tfvars"], "terraform", [["terraform-ls", "serve"]], Packaged("terraform-ls"), new() { ["tfvars"] = "terraform-vars" }),
        Told("nix", "Nix", ["nix"], "nix", [["nixd"], ["nil"]], Packaged("nixd or nil")),
        Told("toml", "TOML", ["toml"], "toml", [["taplo", "lsp", "stdio"]], Packaged("taplo")),
        Told("latex", "LaTeX", ["tex", "bib"], "latex", [["texlab"]], Packaged("texlab"), new() { ["bib"] = "bibtex" }),
        Told("typst", "Typst", ["typ"], "typst", [["tinymist"]], Packaged("tinymist")),
        Told("gleam", "Gleam", ["gleam"], "gleam", [["gleam", "lsp"]], "The Gleam language server comes with Gleam."),
        Told("r", "R", ["r", "rmd"], "r", [["R", "--no-echo", "-e", "languageserver::run()"]], "In R, run `install.packages(\"languageserver\")`.", new() { ["rmd"] = "rmd" }),
    ];

    static readonly string[] Scripts = ["ts", "tsx", "js", "jsx", "mts", "cts"];

    /// <summary>Languages whose missing server is not mentioned to the model: data and markup, where a note with every request would be noise.</summary>
    internal static readonly HashSet<string> Quiet = ["json", "yaml", "toml", "html", "css", "docker", "latex", "typst"];

    /// <summary>What a path's language is looked up by: its extension in lower case without the dot, or dockerfile for a Dockerfile.</summary>
    public static string Kind(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name == "dockerfile" || name.StartsWith("dockerfile.", StringComparison.Ordinal) || name == "containerfile") return "dockerfile";
        return Path.GetExtension(name).TrimStart('.');
    }

    public static CodeLanguage? Language(string kind) => Known.FirstOrDefault(l => l.Extensions.Contains(kind));

    /// <summary>Where Mason puts the servers it installs.</summary>
    public static string Folder(string home) => Path.Combine(home, "language-servers");

    static IEnumerable<string> SearchFolders(string home, string? workspace)
    {
        string own = Folder(home), user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        folders.AddRange([Path.Combine(own, "bin"), Path.Combine(own, "npm", "node_modules", ".bin"), Path.Combine(user, ".cargo", "bin"), Path.Combine(user, "go", "bin"), Path.Combine(user, ".dotnet", "tools")]);
        foreach (var folder in folders)
        {
            string full;
            try { full = Path.GetFullPath(folder); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            // A program inside the project is the project's, not the user's choice of server.
            if (workspace != null && (full + Path.DirectorySeparatorChar).StartsWith(Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            yield return full;
        }
    }

    internal static string? Find(string name, string home, string? workspace = null)
    {
        string[] endings = [".exe", ".cmd", ".bat"];
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        foreach (var folder in SearchFolders(home, workspace))
            foreach (var ending in endings)
            {
                var candidate = Path.Combine(folder, Path.HasExtension(name) && ending.Length > 0 && name.EndsWith(ending, StringComparison.OrdinalIgnoreCase) ? name : name + ending);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    static string? Node(string home)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, ".playwright", "node", "win32_x64", "node.exe");
        return Find("node", home) ?? (File.Exists(bundled) ? bundled : null);
    }

    /// <summary>The script an npm package installed under Mason's folder runs for a command, so it can be started with node directly.</summary>
    static string? NpmScript(string name, string home)
    {
        var modules = Path.Combine(Folder(home), "npm", "node_modules");
        if (!Directory.Exists(modules)) return null;
        IEnumerable<string> packages = Directory.EnumerateDirectories(modules).SelectMany(d => Path.GetFileName(d).StartsWith('@') ? Directory.EnumerateDirectories(d) : [d]);
        foreach (var package in packages)
        {
            var manifest = Path.Combine(package, "package.json");
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1_000_000) continue;
            try
            {
                var bin = JsonNode.Parse(File.ReadAllText(manifest))?["bin"];
                string? relative = bin is JsonObject map ? map[name]?.GetValue<string>() : bin is JsonValue && Path.GetFileName(package) == name ? bin.GetValue<string>() : null;
                if (relative != null && File.Exists(Path.Combine(package, relative))) return Path.GetFullPath(Path.Combine(package, relative));
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException) { }
        }
        return null;
    }

    /// <summary>How a found program is started. A batch file needs the command interpreter; an npm command Mason installed is run with node.</summary>
    static string[]? Launch(string name, string program, IEnumerable<string> arguments, string home)
    {
        if (program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            if (NpmScript(name, home) is { } script && Node(home) is { } node && program.StartsWith(Folder(home), StringComparison.OrdinalIgnoreCase)) return [node, script, .. arguments];
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            return File.Exists(shell) ? [shell, "/d", "/c", program, .. arguments] : null;
        }
        return [program, .. arguments];
    }

    /// <summary>Servers the user names themselves, by extension: language-servers.json in Mason's data folder and MASON_LANGUAGE_SERVERS, each as JSON such as {"zig": ["C:\\tools\\zls.exe"]}. The variable wins.</summary>
    static Dictionary<string, string[]> Overrides(string home)
    {
        var all = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        void Take(string text)
        {
            try
            {
                if (JsonNode.Parse(text) is not JsonObject map) return;
                foreach (var (kind, value) in map)
                    if (value is JsonArray parts && parts.Count > 0 && parts.All(p => p is JsonValue v && v.TryGetValue<string>(out _))) all[kind] = parts.Select(p => p!.GetValue<string>()).ToArray();
            }
            catch (JsonException) { }
        }
        var file = Path.Combine(home, "language-servers.json");
        try { if (File.Exists(file) && new FileInfo(file).Length < 1_000_000) Take(File.ReadAllText(file)); } catch (IOException) { }
        if (Environment.GetEnvironmentVariable("MASON_LANGUAGE_SERVERS") is { Length: > 0 } named) Take(named);
        return all;
    }

    /// <summary>The language server for a file of this kind, when one is installed.</summary>
    public static CodeLanguageServer? Server(string kind, string home, string? workspace = null)
    {
        var entry = Language(kind);
        string id = entry == null ? kind : entry.Others != null && entry.Others.TryGetValue(kind, out var other) ? other : entry.LanguageId;
        if (Overrides(home).TryGetValue(kind, out var own) && Find(own[0], home, workspace) is { } chosen && Launch(Path.GetFileNameWithoutExtension(chosen), chosen, own.Skip(1), home) is { } named)
            return new(Path.GetFileNameWithoutExtension(chosen), named, id);
        if (entry == null) return null;
        foreach (var candidate in entry.Candidates)
            if (Find(candidate[0], home, workspace) is { } program && Launch(candidate[0], program, candidate.Skip(1), home) is { } command) return new(candidate[0], command, id);
        return null;
    }

    // ---- the wire ----

    internal static byte[] Frame(JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        return [.. Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n"), .. body];
    }

    /// <summary>The whole messages at the start of what a server wrote, and how many bytes they took. A message still arriving is left for the next call.</summary>
    internal static (List<JsonObject> Messages, int Used) Frames(ReadOnlySpan<byte> output)
    {
        var found = new List<JsonObject>(); int at = 0;
        ReadOnlySpan<byte> marker = "Content-Length:"u8, blank = "\r\n\r\n"u8;
        while (true)
        {
            int header = output[at..].IndexOf(marker); if (header < 0) break; header += at;
            int end = output[header..].IndexOf(blank); if (end < 0) break; end += header;
            var number = Encoding.ASCII.GetString(output[(header + marker.Length)..end]).Split('\r')[0].Trim();
            if (!int.TryParse(number, out int length) || length <= 0) { at = end + 4; continue; }
            if (end + 4 + length > output.Length) break;
            try { if (JsonNode.Parse(output.Slice(end + 4, length)) is JsonObject message) found.Add(message); } catch (JsonException) { }
            at = end + 4 + length;
        }
        return (found, at);
    }

    internal static string Uri(string file) => new Uri(Path.GetFullPath(file)).AbsoluteUri;

    /// <summary>The file a URI names, in one spelling, so the same file always has the same key.</summary>
    internal static string? PathOf(string uri)
    {
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        try { return Key(parsed.LocalPath); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static string Key(string file) { var full = Path.GetFullPath(file); return full.ToLowerInvariant(); }

    static string? Relative(string uri, CodeWorkspace scope)
    {
        if (PathOf(uri) is not { } path) return null;
        var root = Key(scope.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal)) return null;
        // The project's own spelling of the path, not the lower-cased key.
        try { return scope.Relative(scope.Resolve(path[root.Length..])); } catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return path[root.Length..].Replace('\\', '/'); }
    }

    static readonly string[] Severities = ["", "error", "warning", "information", "hint"];
    static int Whole(JsonNode? node) { try { return node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0; } catch (InvalidOperationException) { return 0; } }
    static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>One diagnostic as a line: path:line:column, how serious, and the message.</summary>
    static string Line(JsonNode? item, string path)
    {
        var start = item?["range"]?["start"];
        return path + ":" + (Whole(start?["line"]) + 1) + ":" + (Whole(start?["character"]) + 1) + "  " + Severities[Math.Clamp(Whole(item?["severity"]), 0, 4)] + "  " + Text(item?["message"]).Replace("\r", "").Replace("\n", " ");
    }

    static string? Note(List<string> lines, string name, string path)
    {
        if (lines.Count == 0) return null;
        var text = name + " reports " + lines.Count + (lines.Count == 1 ? " error in " : " errors in ") + path + " after this edit. Fix them unless they are what you intend:\n" + string.Join("\n", lines.Take(20));
        return lines.Count > 20 ? text + "\n[" + (lines.Count - 20) + " more.]" : text;
    }

    /// <summary>The errors a server reports for a file that was just written, as a note for the model, or null when it reports none or does not answer in time.</summary>
    internal static async Task<string?> Errors(CodeWorkspace scope, CodeLanguageServer server, string path, string text, CodeLanguageSessions sessions, CancellationToken ct)
    {
        if (text.Length > 1_000_000) return null;
        var file = scope.Resolve(path);
        var (session, fresh) = await sessions.Session(server, scope, ct);
        int before = session.Sync(file, text, server.LanguageId);
        var items = await session.Diagnostics(file, before, fresh ? 15 : 6, ct);
        if (items == null) return null;
        var lines = new List<string>();
        foreach (var item in items)
        {
            if (item?["severity"] != null && Whole(item["severity"]) != 1) continue;
            var entry = Line(item, path);
            if (!lines.Contains(entry)) lines.Add(entry);
        }
        return Note(lines, server.Name, path);
    }

    /// <summary>What Jedi or the TypeScript compiler, which ship with Mason, report for a file that was just written.</summary>
    internal static async Task<string?> BuiltInErrors(CodeWorkspace scope, string path, CancellationToken ct)
    {
        try
        {
            var answer = JsonNode.Parse(await CodeSemantic.Query(scope, path, 1, 1, "diagnostics", ct));
            var lines = new List<string>();
            foreach (var item in answer?["results"] as JsonArray ?? [])
            {
                if (Text(item?["file"]) is { Length: > 0 } file && !file.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                lines.Add(path + ":" + Math.Max(1, Whole(item?["line"])) + ":" + Math.Max(1, Whole(item?["column"])) + "  error  " + Text(item?["message"]).Replace("\n", " "));
            }
            var engine = Text(answer?["engine"]).Split(' ')[0];
            return Note(lines, engine.Length == 0 ? "The language service" : engine, path);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>Asks a server about one position, starting it if this request has not yet.</summary>
    internal static async Task<string> Query(CodeWorkspace scope, CodeLanguageServer server, string path, int line, int column, string mode, CodeLanguageSessions sessions, CancellationToken ct)
    {
        if (mode is not ("references" or "diagnostics" or "definitions") || line <= 0 || column <= 0) throw new ArgumentException("Use definitions, references or diagnostics and a 1-based position.");
        var main = scope.Read(path);
        if (main.Content.Length > 1_000_000) throw new ArgumentException(path + " is too large to hand to the language server (1 MB at most).");
        var file = scope.Resolve(main.Path);
        var (session, fresh) = await sessions.Session(server, scope, ct);
        int before = session.Sync(file, main.Content, server.LanguageId);
        if (mode == "diagnostics")
        {
            var items = await session.Diagnostics(file, before, fresh ? 20 : 8, ct);
            if (items == null) return server.Name + " reported nothing for " + main.Path + " in time. It may still be reading the project; try again.";
            var listed = items.Select(i => Line(i, main.Path)).Distinct().Take(150).ToList();
            return server.Name + ": " + listed.Count + (listed.Count == 1 ? " diagnostic for " : " diagnostics for ") + main.Path + "." + (listed.Count == 0 ? "" : "\n" + string.Join("\n", listed));
        }
        var parameters = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri(file) }, ["position"] = new JsonObject { ["line"] = line - 1, ["character"] = column - 1 } };
        if (mode == "references") parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        var method = mode == "definitions" ? "textDocument/definition" : "textDocument/references";
        var answer = await session.Request(method, parameters.DeepClone(), 60, ct);
        // A server that has only just started may answer with nothing while it is still reading the project.
        if (fresh && answer != null && answer["error"] == null && (answer["result"] == null || answer["result"] is JsonArray { Count: 0 }))
        {
            await Task.Delay(3000, ct);
            answer = await session.Request(method, parameters.DeepClone(), 60, ct);
        }
        if (answer == null) return server.Name + " did not answer. It may still have been reading the project; try again.";
        if (answer["error"] is JsonObject error) return server.Name + " could not answer: " + (Text(error["message"]) is { Length: > 0 } said ? said : "unknown error");
        IEnumerable<JsonNode?> places = answer["result"] switch { JsonArray many => many, JsonObject one => [one], _ => [] };
        int outside = 0; var lines = new List<string>();
        foreach (var place in places.Take(150))
        {
            // A location, or a location link whose target is the place.
            var uri = Text(place?["uri"]) is { Length: > 0 } direct ? direct : Text(place?["targetUri"]);
            var start = (place?["range"] ?? place?["targetSelectionRange"] ?? place?["targetRange"])?["start"];
            if (Relative(uri, scope) is not { } found) { outside++; continue; }
            int at = Whole(start?["line"]) + 1; string source = "";
            try { var all = scope.Read(found).Content.Split('\n'); if (at <= all.Length) source = all[at - 1].Trim(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { }
            lines.Add(found + ":" + at + ":" + (Whole(start?["character"]) + 1) + "  " + (source.Length > 200 ? source[..200] : source));
        }
        return server.Name + ": " + lines.Count + " " + mode + " in the project" + (outside > 0 ? ", and " + outside + " outside it (libraries)" : "") + "." + (lines.Count == 0 ? "" : "\n" + string.Join("\n", lines));
    }

    /// <summary>One question to a server started for it and stopped after.</summary>
    public static async Task<string> Query(CodeWorkspace scope, CodeLanguageServer server, string path, int line, int column, string mode, CancellationToken ct)
    {
        await using var sessions = new CodeLanguageSessions();
        return await Query(scope, server, path, line, column, mode, sessions, ct);
    }

    // ---- installing ----

    static readonly Dictionary<string, string> ToolNames = new() { ["npm"] = "Node.js (npm)", ["go"] = "Go", ["dotnet"] = "the .NET SDK", ["rustup"] = "rustup", ["gem"] = "Ruby (gem)" };

    /// <summary>What installing a language's server would run, or null when Mason cannot install it.</summary>
    static (string Needs, string[] Arguments, Dictionary<string, string> Environment)? Installer(CodeLanguage language, string home)
    {
        string own = Folder(home), bin = Path.Combine(own, "bin");
        if (language.Packages.Length == 0) return null;
        return language.Tool switch
        {
            "npm" => ("npm", ["install", "--prefix", Path.Combine(own, "npm"), "--no-fund", "--no-audit", "--loglevel=error", .. language.Packages], []),
            "go" => ("go", ["install", language.Packages[0]], new() { ["GOBIN"] = bin }),
            "dotnet" => ("dotnet", ["tool", "update", "--tool-path", bin, language.Packages[0]], []),
            "rustup" => ("rustup", ["component", "add", language.Packages[0]], []),
            "gem" => ("gem", ["install", "--user-install", "--no-document", "--bindir", bin, language.Packages[0]], []),
            _ => null
        };
    }

    /// <summary>One language's state: its server when installed, otherwise how to get one.</summary>
    public static CodeLanguageState State(CodeLanguage language, string home)
    {
        if (Server(language.Extensions[0], home) is { } server) return new(server.Name, "");
        // Python is still checked without a server, by the Jedi that ships with Mason.
        var before = language.Key == "python" ? "syntax errors only, from the Jedi that ships with Mason. For type errors: " : "";
        if (Installer(language, home) is { } plan)
            return new(null, before + (Find(plan.Needs, home) == null ? "needs " + ToolNames.GetValueOrDefault(plan.Needs, plan.Needs) + " first, then /languages install " + language.Key : "/languages install " + language.Key + " (uses " + plan.Needs + ")"));
        return new(null, language.Hint);
    }

    /// <summary>The same state in words for a settings row, and whether an Install button belongs beside it.</summary>
    public static (string Text, bool CanInstall) Described(CodeLanguage language, string home)
    {
        if (Server(language.Extensions[0], home) is { } server) return (server.Name + ", installed", false);
        bool python = language.Key == "python";
        var before = python ? "Checked for syntax errors by the Jedi that ships with Mason. Pyright adds type errors. " : "Not installed. ";
        if (Installer(language, home) is { } plan)
            return Find(plan.Needs, home) != null ? (python ? before.Trim() : "Not installed", true) : (before + "Installing it needs " + ToolNames.GetValueOrDefault(plan.Needs, plan.Needs) + " first.", false);
        return (language.Hint.Length == 0 ? "Not installed" : before + language.Hint, false);
    }

    /// <summary>The languages with files in the project.</summary>
    public static CodeLanguage[] Present(CodeWorkspace scope)
    {
        HashSet<string> kinds;
        try { kinds = scope.Files(".", 1000).Files.Select(Kind).ToHashSet(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { kinds = []; }
        return Known.Where(l => l.Extensions.Any(kinds.Contains)).ToArray();
    }

    /// <summary>Installs a language's server with the language's own tool. Nothing is installed without this being called.</summary>
    public static async Task<string> Install(CodeLanguage language, string home, CancellationToken ct)
    {
        if (Installer(language, home) is not { } plan) throw new InvalidOperationException("Mason cannot install the " + language.Name + " server itself." + (language.Hint.Length == 0 ? "" : " " + language.Hint));
        var program = Find(plan.Needs, home) ?? throw new InvalidOperationException("Installing the " + language.Name + " server needs " + ToolNames.GetValueOrDefault(plan.Needs, plan.Needs) + ", which was not found on this computer.");
        Directory.CreateDirectory(Path.Combine(Folder(home), "bin"));
        var command = Launch(plan.Needs, program, plan.Arguments, home) ?? throw new InvalidOperationException("The command interpreter was not found.");
        var (status, output) = await Run(command, plan.Environment, TimeSpan.FromMinutes(15), ct);
        if (status == 0 && Server(language.Extensions[0], home) is { } server) return "Installed " + server.Name + " for " + language.Name + ".";
        var tail = output.Trim(); if (tail.Length > 1500) tail = tail[^1500..];
        throw new InvalidOperationException("Installing the " + language.Name + " server did not work (" + plan.Needs + " exit " + status + ")." + (tail.Length == 0 ? "" : "\n" + tail));
    }

    /// <summary>Runs a program with this account and returns its exit code and what it printed.</summary>
    static async Task<(int Status, string Output)> Run(string[] command, Dictionary<string, string> environment, TimeSpan limit, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var child = Process.Start(start) ?? throw new IOException("The installer could not start.");
        child.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limit);
        var output = new StringBuilder();
        void Keep(string? line) { if (line != null) lock (output) { if (output.Length < 256_000) output.AppendLine(line); } }
        child.OutputDataReceived += (_, e) => Keep(e.Data); child.ErrorDataReceived += (_, e) => Keep(e.Data);
        child.BeginOutputReadLine(); child.BeginErrorReadLine();
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { try { child.Kill(true); } catch (InvalidOperationException) { } ct.ThrowIfCancellationRequested(); return (-1, output + "\nStopped: it ran longer than " + (int)limit.TotalMinutes + " minutes."); }
        child.WaitForExit();
        lock (output) return (child.ExitCode, output.ToString());
    }

    // ---- the /languages command, shared by the command line and the app ----

    public const string Help = """
        /languages                 The languages in this project and the language server for each
        /languages all             Every language Mason knows a server for
        /languages install NAME    Install a language's server with that language's own tool (npm, go, dotnet, rustup or gem)
        A server you installed yourself is found on the PATH. To name one for any file extension, put JSON such as
        {"zig": ["C:\\tools\\zls.exe"]} in language-servers.json in Mason's data folder.
        """;

    public static async Task<string> Command(string rest, CodeWorkspace? scope, string home, CancellationToken ct)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string first = words.Length == 0 ? "" : words[0].ToLowerInvariant();
        if (first == "help") return Help;
        if (first == "install")
        {
            var language = words.Length == 2 ? Known.FirstOrDefault(l => l.Key.Equals(words[1], StringComparison.OrdinalIgnoreCase) || l.Name.Equals(words[1], StringComparison.OrdinalIgnoreCase)) : null;
            return await Install(language ?? throw new ArgumentException("Use /languages install NAME, with a name from /languages all."), home, ct);
        }
        if (first is not ("" or "all")) throw new ArgumentException("Use /languages, /languages all or /languages install NAME.");
        bool everything = first == "all" || scope == null;
        var lines = new List<string>();
        foreach (var language in everything ? Known : Present(scope!))
        {
            var state = State(language, home);
            lines.Add(language.Name + " [" + language.Key + "]  ·  " + (state.Installed != null ? state.Installed + ", installed" : "not installed: " + state.Action));
        }
        if (!everything)
        {
            bool scripts;
            try { scripts = scope!.Files(".", 1000).Files.Any(f => Scripts.Contains(Kind(f))); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { scripts = false; }
            if (scripts) lines.Add("TypeScript and JavaScript  ·  the TypeScript compiler that ships with Mason");
        }
        foreach (var (kind, command) in Overrides(home).OrderBy(p => p.Key, StringComparer.Ordinal)) lines.Add("." + kind + " files  ·  " + string.Join(" ", command) + ", named by you");
        if (lines.Count == 0) return "No file in this project is in a language Mason knows a server for. /languages all lists them.";
        return string.Join("\n", lines) + (everything ? "" : "\nEdits to these files are checked by the installed servers. /languages all lists every language.");
    }

    internal static bool BuiltIn(string kind) => Scripts.Contains(kind) || kind is "py" or "pyi";
    internal static bool Script(string kind) => Scripts.Contains(kind);
}

/// <summary>One running language server: what it has been sent, and what it has said.</summary>
internal sealed class CodeLanguageSession(CodeLanguageServer server) : IAsyncDisposable
{
    public CodeLanguageServer Server { get; } = server;
    readonly object gate = new();
    readonly SemaphoreSlim writing = new(1, 1);
    readonly Dictionary<int, JsonObject> replies = [];
    readonly Dictionary<string, (int Count, long At, List<JsonNode?> Items)> published = [];
    readonly Dictionary<string, int> versions = [];
    readonly Dictionary<string, string> texts = [];
    readonly CancellationTokenSource stop = new();
    Process? child; Task? reading; int nextId = 1; bool ended, pulls;

    public bool Ended { get { lock (gate) return ended; } }

    /// <summary>Starts the server in the project folder and greets it. Throws when it does not answer.</summary>
    public async Task Start(CodeWorkspace scope, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Server.Command[0]) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = scope.Root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in Server.Command.Skip(1)) start.ArgumentList.Add(argument);
        try { child = Process.Start(start) ?? throw new IOException("no process"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { throw new InvalidOperationException(Server.Name + " did not start: " + e.Message); }
        _ = child.StandardError.BaseStream.CopyToAsync(Stream.Null, stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        reading = Task.Run(Read);
        var root = CodeLanguageServers.Uri(scope.Root);
        var capabilities = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["publishDiagnostics"] = new JsonObject(), ["diagnostic"] = new JsonObject(), ["synchronization"] = new JsonObject { ["didSave"] = true } },
            ["workspace"] = new JsonObject { ["workspaceFolders"] = true }
        };
        var parameters = new JsonObject
        {
            ["processId"] = Environment.ProcessId, ["rootUri"] = root, ["rootPath"] = scope.Root, ["capabilities"] = capabilities,
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = root, ["name"] = Path.GetFileName(scope.Root) })
        };
        var greeting = await Request("initialize", parameters, 60, ct);
        if (greeting == null || greeting["error"] != null)
        {
            await DisposeAsync(); ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(Server.Name + " did not start: it gave no answer to the first message.");
        }
        var offered = greeting["result"]?["capabilities"]?["diagnosticProvider"];
        lock (gate) pulls = offered != null && !(offered is JsonValue value && value.TryGetValue<bool>(out var on) && !on);
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized", ["params"] = new JsonObject() });
    }

    async Task Read()
    {
        var buffer = new List<byte>(); var chunk = new byte[65536];
        try
        {
            while (true)
            {
                int count = await child!.StandardOutput.BaseStream.ReadAsync(chunk, stop.Token);
                if (count <= 0) break;
                buffer.AddRange(chunk.AsSpan(0, count));
                var (messages, used) = CodeLanguageServers.Frames(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
                buffer.RemoveRange(0, used);
                // Output that never becomes a message is not kept.
                if (buffer.Count > 64_000_000) buffer.Clear();
                foreach (var message in messages) await Receive(message);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        lock (gate) ended = true;
    }

    async Task Receive(JsonObject message)
    {
        string? method = message["method"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : null;
        if (method != null && message["id"] is { } id)
        {
            // The server asks something of its own. It gets an empty answer, so it does not wait for one.
            JsonNode? result = null;
            if (method == "workspace/configuration") result = new JsonArray((message["params"]?["items"] as JsonArray ?? []).Select(_ => (JsonNode?)null).ToArray());
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
        }
        else if (message["id"] is JsonValue number && number.TryGetValue<int>(out var reply)) { lock (gate) replies[reply] = message; }
        else if (method == "textDocument/publishDiagnostics" && message["params"] is JsonObject parameters && CodeLanguageServers.PathOf(parameters["uri"] is JsonValue uri && uri.TryGetValue<string>(out var given) ? given : "") is { } path)
        {
            lock (gate)
            {
                int count = published.TryGetValue(path, out var earlier) ? earlier.Count : 0;
                published[path] = (count + 1, Environment.TickCount64, (parameters["diagnostics"] as JsonArray ?? []).ToList());
            }
        }
    }

    async Task Send(JsonNode message)
    {
        if (child == null || Ended) return;
        var bytes = CodeLanguageServers.Frame(message);
        await writing.WaitAsync();
        try { await child.StandardInput.BaseStream.WriteAsync(bytes); await child.StandardInput.BaseStream.FlushAsync(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { lock (gate) ended = true; }
        finally { writing.Release(); }
    }

    /// <summary>Sends a request and waits for its answer, or null when none comes in time or the server has gone.</summary>
    public async Task<JsonObject?> Request(string method, JsonNode? parameters, double seconds, CancellationToken ct)
    {
        int id; lock (gate) id = nextId++;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) message["params"] = parameters;
        await Send(message);
        long deadline = Environment.TickCount64 + (long)(seconds * 1000);
        while (Environment.TickCount64 < deadline)
        {
            lock (gate) { if (replies.Remove(id, out var reply)) return reply; if (ended) return null; }
            if (ct.IsCancellationRequested) return null;
            await Task.Delay(20, CancellationToken.None);
        }
        return null;
    }

    /// <summary>Hands the server a file's text as it is now, and returns how many times it had published for the file before.</summary>
    public int Sync(string file, string text, string languageId)
    {
        string key = CodeLanguageServers.Key(file), uri = CodeLanguageServers.Uri(file); int version, before;
        lock (gate)
        {
            before = published.TryGetValue(key, out var earlier) ? earlier.Count : 0;
            // A server says nothing new about text it already has, so what it last said stands.
            if (texts.TryGetValue(key, out var held) && held == text) return Math.Max(0, before - 1);
            texts[key] = text; version = versions[key] = versions.GetValueOrDefault(key) + 1;
        }
        JsonObject message = version == 1
            ? new() { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didOpen", ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = languageId, ["version"] = 1, ["text"] = text } } }
            : new() { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didChange", ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version }, ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }) } };
        // Sent in order, one after the other: the writes share one lock.
        Send(message).GetAwaiter().GetResult();
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didSave", ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } } }).GetAwaiter().GetResult();
        return before;
    }

    /// <summary>The file's diagnostics once the server has read it again, or null when it says nothing in time.</summary>
    public async Task<List<JsonNode?>?> Diagnostics(string file, int before, double seconds, CancellationToken ct)
    {
        string key = CodeLanguageServers.Key(file), uri = CodeLanguageServers.Uri(file);
        long deadline = Environment.TickCount64 + (long)(seconds * 1000);
        // A request for the file's symbols is answered once the server has read the file, which is when it publishes.
        await Request("textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, seconds, ct);
        List<JsonNode?>? pulled = null; bool asks; lock (gate) asks = pulls;
        if (asks)
        {
            var answer = await Request("textDocument/diagnostic", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, Math.Max(1, (deadline - Environment.TickCount64) / 1000.0), ct);
            if (answer?["result"]?["items"] is JsonArray items) pulled = items.ToList();
        }
        // What it publishes may come in more than one part; it is taken once it has been quiet for a moment.
        // A server that answered the request may publish nothing at all, so it is not waited on for long.
        long grace = Environment.TickCount64 + 1500;
        while (true)
        {
            List<JsonNode?>? fresh = null; bool quiet = false, gone;
            lock (gate)
            {
                if (published.TryGetValue(key, out var state) && state.Count > before) { fresh = [.. state.Items]; quiet = Environment.TickCount64 - state.At >= 400; }
                gone = ended;
            }
            bool late = Environment.TickCount64 >= deadline;
            if (fresh != null && (quiet || late)) { if (pulled != null) fresh.AddRange(pulled); return fresh; }
            if (gone || late || ct.IsCancellationRequested) return pulled;
            if (fresh == null && pulled != null && Environment.TickCount64 >= grace) return pulled;
            await Task.Delay(30, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (child == null) return;
        if (!Ended)
        {
            await Request("shutdown", null, 1, CancellationToken.None);
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "exit" });
        }
        try { child.StandardInput.Close(); } catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
        try { if (!child.WaitForExit(500)) child.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        stop.Cancel();
        if (reading != null) await reading;
        child.Dispose(); child = null; stop.Dispose();
    }
}

/// <summary>The language servers one request has started, and what the user said about starting each.</summary>
internal sealed class CodeLanguageSessions : IAsyncDisposable
{
    readonly Dictionary<string, CodeLanguageSession> sessions = [];
    readonly Dictionary<string, string> failed = [];
    public Dictionary<string, bool> Answers { get; } = [];
    public HashSet<string> Told { get; } = [];

    /// <summary>The running server, started now if this request has not used it yet. Fresh is true when it was started by this call, so the caller gives it longer.</summary>
    public async Task<(CodeLanguageSession Session, bool Fresh)> Session(CodeLanguageServer server, CodeWorkspace scope, CancellationToken ct)
    {
        var key = string.Join(" ", server.Command);
        if (failed.TryGetValue(key, out var problem)) throw new InvalidOperationException(problem);
        if (sessions.TryGetValue(key, out var running))
        {
            if (!running.Ended) return (running, false);
            sessions.Remove(key); await running.DisposeAsync();
        }
        var made = new CodeLanguageSession(server);
        try { await made.Start(scope, ct); }
        catch (InvalidOperationException e) { failed[key] = e.Message; throw; }
        sessions[key] = made;
        return (made, true);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in sessions.Values) await session.DisposeAsync();
        sessions.Clear(); failed.Clear();
    }
}
