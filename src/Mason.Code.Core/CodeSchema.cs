using System.Text.Json;

namespace Mason.Code;

/// <summary>Our action wire schema. Decoding constraints supplement, never replace, tool validation.</summary>
public static class CodeSchema
{
    public static IReadOnlySet<string>? Available(IReadOnlyList<CodeMessage> messages)=>messages.FirstOrDefault(m=>m.Role=="system")?.AvailableTools;
    public static IReadOnlySet<string> Available(CodeSession session)
    {
        var names=new HashSet<string>(["list_files","search_text","read_file","open_file","ask_user","navigate_code"]);
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
        "list_files"=>"List workspace files recursively, limit 1–1000. Use the supplied inventory first.",
        "search_text"=>"Search literal text in allowed workspace files. Use focused queries to locate existing behavior before reading and editing the relevant files.",
        "read_file"=>"Read the exact UTF-8 file, applicable repository instructions and current byte SHA-256. Use this fresh hash for edits or replacement.",
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
