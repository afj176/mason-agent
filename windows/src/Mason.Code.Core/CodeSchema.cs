using System.Text.Json;

namespace Mason.Code;

/// <summary>Our action wire schema. Decoding constraints supplement, never replace, tool validation.</summary>
public static class CodeSchema
{
    public static IReadOnlySet<string>? Available(IReadOnlyList<CodeMessage> messages)=>messages.FirstOrDefault(m=>m.Role=="system")?.AvailableTools;
    public static IReadOnlySet<string> Available(CodeSession session)
    {
        var names=new HashSet<string>(["list_files","search_text","read_file","open_file","ask_user","navigate_code","suggest_note"]);
        names.Add("enable_tools");
        if(session.Desktop!=null)names.Add("desktop_visual");
        names.UnionWith(["search_next","read_lines"]);
        // Meaning-based code search is offered whenever the host has a code search model connected.
        if(CodeSemanticIndex.Available)names.Add("semantic_search");
        if(session.Desktop!=null){names.Add("desktop_observe");if(session.Mode=="build"&&session.Permission!=CodePermission.ReadOnly)names.Add("desktop_act");}
        if(session.Mode=="ask"||session.Permission==CodePermission.ReadOnly)return names;
        names.UnionWith(["edit_file","apply_edits","replace_file","create_file","create_directory","run_command"]);
        if(session.Shell!=CodeShellKind.Direct&&CodeShells.Unavailable(session)==null)names.Add("run_shell");
        if(session.Permission!=CodePermission.Auto)names.Add("open_preview");
        if(session.Permission==CodePermission.Auto && session.Turns.LastOrDefault(t=>t.Role=="user")?.Acceptance!=null)names.Add("verify_acceptance");
        return names;
    }
    public static JsonElement NativeTools(IReadOnlySet<string>? available=null, bool rootIsOnlyDirectory=false) => JsonSerializer.SerializeToElement(Action(true,available:available,rootIsOnlyDirectory:rootIsOnlyDirectory).GetProperty("properties").GetProperty("tool").GetProperty("anyOf").EnumerateArray().Select(tool=>new
    {
        type="function",function=new
        {
            name=tool.GetProperty("properties").GetProperty("name").GetProperty("const").GetString(),
            description=Description(tool.GetProperty("properties").GetProperty("name").GetProperty("const").GetString()!),
            parameters=tool.GetProperty("properties").GetProperty("arguments")
        }
    }).ToArray());
    static string Description(string name)=>name switch
    {
        "navigate_code"=>"Find symbol definitions, references, related tests or compiler diagnostics in a workspace directory. C# uses compiler binding. Other supported source languages return labeled text candidates.",
        "repository_map"=>"Find ranked definitions, callers, import and test references by identifier or qualified name. C# references are compiler bound. Exact JavaScript, TypeScript and Python declarations can include language-service references. Other matches are labelled as candidates.",
        "semantic_search"=>"Find code by meaning when you don't know the exact names, for example \"where are invoice totals calculated\". Uses the connected embeddings model, and its reranker when one is connected. Returns ranked line ranges to read next.",
        "inspect_symbol"=>"Resolve definitions, references or diagnostics at a 1-based source position. TypeScript, JavaScript and Python are answered by services that ship with Mason; other languages by the language server installed for them, which the user is asked about before it starts.",
        "enable_tools"=>"Load specialized tools for code (repository map, semantic code search when a search model is connected, and symbol navigation), workflows (named tasks and skills), jobs (background processes), browser (interactive project Edge), or mcp (registered tools).",
        "computer_use"=>"Operate the project page in disposable offline Edge using a fresh accessibility observation after each click or fill. Supply a workspace HTML path and task.",
        "desktop_observe"=>"Read the control names and available actions from the native window the user attached for this session.",
        "desktop_visual"=>"Inspect the attached window with a local vision model. For a canvas operation specify one requested click or drag. Every action needs approval and a fresh matching frame.",
        "desktop_act"=>"Ask the user to invoke or set a control from the most recent attached-window observation. Receives a fresh observation.",
        "start_job"=>"Start a long-running offline sandboxed executable for this run. Returns a job ID. Read output with poll_job, stop a service before finishing.",
        "poll_job"=>"Read a background job's current output or collect its exit status.",
        "stop_job"=>"Stop this run's owned background job and collect the result.",
        "run_workflow"=>"Run a named project workflow from the pinned mason.workflows.json in the workspace sandbox.",
        "read_skill"=>"Read a project's named .mason/skills/<name>/SKILL.md workflow on demand.",
        "start_terminal"=>"Start a reconnectable line-oriented workspace terminal in the offline sandbox.",
        "terminal_input"=>"Send one line to an owned terminal. Use eof=true to close input.",
        "terminal_status"=>"Read recent output and exit status from an owned terminal.",
        "terminal_stop"=>"Stop an owned workspace terminal.",
        "mcp_tools"=>"Discover allowed tools on a user-registered local MCP server.",
        "mcp_resources"=>"Discover user-registered resource URIs.",
        "read_documentation"=>"Find a literal phrase in a registered versioned documentation source. Cite the returned URL, line and excerpt.",
        "mcp_read"=>"Read one explicit resource from a registered MCP server.",
        "mcp_call"=>"Request individual user approval to call a specific registered MCP tool with JSON object arguments.",
        "list_files"=>"List workspace files recursively, limit 1–1000. Use the supplied inventory first.",
        "search_text"=>"Search literal text in allowed workspace files. Use focused queries to locate existing behavior before reading and editing the relevant files.",
        "read_file"=>"Read the exact UTF-8 file, applicable repository instructions and current byte SHA-256. Use this fresh hash for edits or replacement.",
        "search_next"=>"Continue the preceding search with its returned cursor, including later files and matches.",
        "git_status"=>"Request full Git index and working-tree status, including pre-existing changes.",
        "delegate_readonly"=>"Delegate one question over 1–8 named workspace files to a separate read-only model context. Returns line-grounded findings. The worker cannot edit, run commands or delegate.",
        "read_lines"=>"Read a range of 1 to 200 numbered lines from a workspace UTF-8 file, including large files.",
        "open_file"=>"Open a workspace file in the user's viewer and return its contents/hash. Opening a file does not change it or prove tests passed.",
        "create_directory"=>"Create missing workspace directory components before creating nested files.",
        "create_file"=>"Create a NEW UTF-8 file only; never overwrite an existing file. For an existing file use read_file then replace_file or edit_file. Supply complete working source text as content, not a draft or shell-escaped string.",
        "replace_file"=>"Rewrite an EXISTING file with complete working content. Supply expected_sha256 from the most recent read_file. Stale hashes are rejected; existing BOM and line-ending style are preserved.",
        "edit_file"=>"Replace exactly one unique old_text occurrence in an existing file. Requires the current read_file byte hash. Re-read after stale/ambiguous failures; do not retry identical rejected edits.",
        "apply_edits"=>"Supply an array of 1–32 {old_text,new_text} replacements against one current read_file byte hash. All edits validate before any write.",
        "run_shell"=>"Run a bounded noninteractive command in the selected shell from a workspace directory. Use for project tests and diagnostics; prefer file tools for source edits. Inspect exit code and actual output. Runtime availability and permissions are enforced by the host.",
        "run_command"=>"Run an existing .exe with separate exact argument strings from a workspace directory. No implicit shell or shell builtins. Inspect exit code and actual output; unsupported Auto runtimes fail closed.",
        "open_preview"=>"Request a loopback website preview. Unavailable to the agent in Auto. It does not inspect the browser or prove visual correctness.",
        "verify_acceptance"=>"Run the host's pinned feature checks against the current workspace. Web checks use isolated Edge. Use after implementing features. Finish also runs these checks.",
        "suggest_note"=>"Suggest one short, lasting fact about this project for the user to remember (a convention they corrected, the command that runs the tests, where something lives). The user decides whether to keep it; nothing is saved by this call. At most once per request; never secrets or one-off details.",
        "ask_user"=>"Pause with a specific question only when a missing decision or permission blocks progress. Do not use as a completion signal.",
        _=>throw new InvalidOperationException("Missing native tool description: "+name)
    };
    public static string ToolGuide(IReadOnlySet<string> available)
    {
        var definitions = NativeTools(available);
        return string.Join('\n', definitions.EnumerateArray().Select(tool =>
        {
            var function = tool.GetProperty("function");
            var names = function.GetProperty("parameters").GetProperty("properties").EnumerateObject().Select(field => field.Name);
            return function.GetProperty("name").GetString() + "(" + string.Join(", ", names) + ") " + function.GetProperty("description").GetString();
        }));
    }
    static object Text() => new { type = "string" };
    static object Object(Dictionary<string, object> properties) => new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false };
    static object Fields(params (string Key, object Value)[] fields) => Object(fields.ToDictionary(f => f.Key, f => f.Value));
    static object Tool(string name, object arguments) => Fields(("name", new { type = "string", @const = name }), ("arguments", arguments));
    public static JsonElement Action(bool requireTool = false, int maxMessageChars = 1600, IReadOnlySet<string>? available=null, bool rootIsOnlyDirectory=false, bool compactNewFile=false, string[]? observedHashes=null)
    {
        var tools = new List<object>();
        if (!requireTool)
        {
            tools.Add(new { type = "null" });
            tools.Add(Tool("finish", Fields(("summary", new { type = "string", minLength = 1, maxLength = maxMessageChars }))));
        }
        object existingDirectory = rootIsOnlyDirectory ? new { type = "string", @const = "." } : Text();
        tools.Add(Tool("list_files", Fields(("path", existingDirectory), ("limit", new { type = "integer", minimum = 1, maximum = 1000 }))));
        tools.Add(Tool("search_text", Fields(("query", Text()), ("path", existingDirectory))));
        tools.Add(Tool("navigate_code", Fields(("path",existingDirectory),("symbol",Text()),("mode",new {type="string",@enum=new[]{"definitions","references","tests","diagnostics"}}))));
        foreach (var name in new[] { "read_file", "open_file", "create_directory" }) tools.Add(Tool(name, Fields(("path", Text()))));
        object hash = observedHashes is { Length: >0 } ? new { type="string", @enum=observedHashes } : Text();
        tools.Add(Tool("edit_file", Fields(("path", Text()), ("expected_sha256", hash), ("old_text", Text()), ("new_text", Text()))));
        tools.Add(Tool("apply_edits", Fields(("path", Text()), ("expected_sha256", hash), ("edits", new { type = "array", items = Fields(("old_text", Text()), ("new_text", Text())), minItems = 1, maxItems = 32 }))));
        // A new file needs an existing parent. When the only parent is '.', a nested path cannot succeed.
        object newPath = rootIsOnlyDirectory ? new { type = "string", pattern = @"^(\.[/\\])?[^/\\:]+$" } : Text();
        object content = compactNewFile ? new { type = "string", maxLength = 2400 } : Text();
        tools.Add(Tool("create_file", Fields(("path", newPath), ("content", content))));
        tools.Add(Tool("replace_file", Fields(("path", Text()), ("expected_sha256", hash), ("content", Text()))));
        tools.Add(Tool("run_command", Fields(("executable", Text()), ("arguments", new { type = "array", items = Text(), maxItems = 40 }), ("directory", existingDirectory))));
        tools.Add(Tool("run_shell", Fields(("command", Text()), ("directory", existingDirectory))));
        tools.Add(Tool("open_preview", Fields(("url", Text()))));
        tools.Add(Tool("verify_acceptance", Fields()));
        tools.Add(Tool("ask_user", Fields(("question", Text()))));
        tools.Add(Tool("suggest_note", Fields(("text", new { type = "string", minLength = 1, maxLength = CodeNotes.TextLimit }))));
        object integer=new{type="integer",minimum=1,maximum=1000000};
        tools.Add(Tool("search_next",Fields(("cursor",Text()))));
        tools.Add(Tool("git_status",Fields()));
        tools.Add(Tool("repository_map",Fields(("query",Text()))));
        tools.Add(Tool("semantic_search",Fields(("query",Text()),("path",existingDirectory))));
        tools.Add(Tool("delegate_readonly",Fields(("question",Text()),("paths",new{type="array",minItems=1,maxItems=8,items=Text()}))));
        tools.Add(Tool("read_lines",Fields(("path",Text()),("start",integer),("count",new{type="integer",minimum=1,maximum=200}))));
        tools.Add(Tool("enable_tools",Fields(("group",new{type="string",@enum=new[]{"code","workflows","jobs","browser","mcp","docs"}}))));
        tools.Add(Tool("inspect_symbol",Fields(("path",Text()),("line",integer),("column",integer),("mode",new{type="string",@enum=new[]{"definitions","references","diagnostics"}}))));
        tools.Add(Tool("computer_use",Fields(("path",Text()),("task",Text()))));
        tools.Add(Tool("desktop_observe",Fields()));
        tools.Add(Tool("desktop_visual",Fields(("task",Text()))));
        tools.Add(Tool("desktop_act",Fields(("operation",new{type="string",@enum=new[]{"invoke","set"}}),("id",Text()),("value",Text()))));
        tools.Add(Tool("start_job",Fields(("executable",Text()),("arguments",new{type="array",items=Text(),maxItems=40}),("directory",existingDirectory))));
        tools.Add(Tool("poll_job",Fields(("id",Text()),("wait_ms",new{type="integer",minimum=0,maximum=10000}))));
        tools.Add(Tool("stop_job",Fields(("id",Text()))));
        tools.Add(Tool("run_workflow",Fields(("name",Text()))));
        tools.Add(Tool("read_skill",Fields(("name",Text()))));
        tools.Add(Tool("start_terminal",Fields(("executable",Text()),("arguments",new{type="array",items=Text(),maxItems=40}),("directory",existingDirectory))));
        tools.Add(Tool("terminal_input",Fields(("id",Text()),("line",Text()),("eof",new{type="boolean"}))));
        foreach(var terminal in new[]{"terminal_status","terminal_stop"})tools.Add(Tool(terminal,Fields(("id",Text()))));
        tools.Add(Tool("mcp_tools",Fields(("server",Text()))));
        tools.Add(Tool("read_documentation",Fields(("source",Text()),("path",Text()),("query",Text()))));
        tools.Add(Tool("mcp_resources",Fields(("server",Text()))));
        tools.Add(Tool("mcp_read",Fields(("server",Text()),("uri",Text()))));
        tools.Add(Tool("mcp_call",Fields(("server",Text()),("tool",Text()),("arguments_json",Text()))));
        if (available != null) tools = tools.Where(t =>
        {
            var item = JsonSerializer.SerializeToElement(t);
            if (item.TryGetProperty("type", out var type) && type.GetString() == "null") return true;
            var name = item.GetProperty("properties").GetProperty("name").GetProperty("const").GetString()!;
            return name == "finish" || available.Contains(name);
        }).ToList();
        return JsonSerializer.SerializeToElement(Fields(("version", new { type = "integer", @const = 1 }), ("message", new { type = "string", maxLength = maxMessageChars }), ("tool", new { anyOf = tools })));
    }
}
