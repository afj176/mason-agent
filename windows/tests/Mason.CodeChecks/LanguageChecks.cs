using Mason.Code;
using System.Text.Json;
using System.Text.Json.Nodes;

// The language server client: the table of languages, the wire format, a stand-in server spoken to for real, and
// the engine handing an edited file to it. Run with: --language-checks <temporary folder>
static class LanguageChecks
{
    static CodeAction Action(string name, object arguments) => new("Executing the requested fixture step.", name, JsonSerializer.SerializeToElement(arguments));

    public static async Task Run(string root, Action<bool, string> check)
    {
        root = Path.Combine(root, "language-" + Guid.NewGuid().ToString("N")[..8]);
        string home = Path.Combine(root, "profile"), workspace = Path.Combine(root, "project");
        Directory.CreateDirectory(home); Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("MASON_LANGUAGE_SERVERS", null);

        check(CodeLanguageServers.Kind("deploy/Dockerfile") == "dockerfile" && CodeLanguageServers.Kind("src/App.SCSS") == "scss" && CodeLanguageServers.Language("scss")?.Key == "css", "a path's language comes from its extension, or its name for a Dockerfile");
        var kinds = CodeLanguageServers.Known.SelectMany(l => l.Extensions).ToArray();
        check(kinds.Distinct().Count() == kinds.Length && CodeLanguageServers.Known.Select(l => l.Key).Distinct().Count() == CodeLanguageServers.Known.Length && CodeLanguageServers.Known.All(l => l.Candidates.Length > 0), "every extension belongs to one language and every language names a server");

        byte[] one = CodeLanguageServers.Frame(JsonNode.Parse("{\"id\":1,\"result\":\"a\"}")!), two = [.. one, .. CodeLanguageServers.Frame(JsonNode.Parse("{\"method\":\"note\"}")!)];
        var part = CodeLanguageServers.Frames(two.AsSpan(0, two.Length - 5));
        var whole = CodeLanguageServers.Frames(two);
        check(part.Messages.Count == 1 && part.Used == one.Length && whole.Messages.Count == 2 && whole.Used == two.Length, "what a server writes is read message by message, and one still arriving is left for later");

        File.WriteAllText(Path.Combine(workspace, "Main.hs"), "module Main where\n");
        File.WriteAllText(Path.Combine(workspace, "Dockerfile"), "FROM scratch\n");
        var scope = new CodeWorkspace(workspace);
        var listed = await CodeLanguageServers.Command("", scope, home, default);
        check(listed.Contains("Haskell [haskell]") && listed.Contains("Dockerfiles [docker]") && !listed.Contains("Zig"), "/languages lists the languages in the project: " + listed);
        check((await CodeLanguageServers.Command("all", scope, home, default)).Contains("Zig [zig]"), "/languages all lists every language");
        bool hinted = false, unknown = false;
        try { await CodeLanguageServers.Command("install haskell", scope, home, default); } catch (InvalidOperationException e) { hinted = e.Message.Contains("ghcup"); }
        try { await CodeLanguageServers.Command("install klingon", scope, home, default); } catch (ArgumentException e) { unknown = e.Message.Contains("/languages all"); }
        check(hinted && unknown, "a server Mason cannot install says how to get it, and an unknown name is refused");

        string python;
        try { python = CodeCommand.ResolveExecutable("python", workspace); } catch (ArgumentException) { Console.WriteLine("SKIP the stand-in language server: no python on this computer"); return; }
        string? fake = null;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null && fake == null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "macos", "Tests", "fake-language-server.py");
            if (File.Exists(candidate)) fake = candidate;
        }
        if (fake == null) { Console.WriteLine("SKIP the stand-in language server: macos/Tests/fake-language-server.py was not found"); return; }

        // A server the user names in their own file is used for that extension, known to Mason or not.
        File.WriteAllText(Path.Combine(home, "language-servers.json"), JsonSerializer.Serialize(new Dictionary<string, string[]> { ["klingon"] = [python, fake] }));
        check(CodeLanguageServers.Server("klingon", home, workspace)?.LanguageId == "klingon" && (await CodeLanguageServers.Command("", scope, home, default)).Contains(".klingon files"), "a server named in language-servers.json is used for its extension");
        File.Delete(Path.Combine(home, "language-servers.json"));

        const string bad = "int total(int a, int b) {\n    return a + b;\n}\nint main() { return total(1, count); }\n", good = "int total(int a, int b) {\n    return a + b;\n}\nint main() { return total(1, 2); }\n";
        Environment.SetEnvironmentVariable("MASON_LANGUAGE_SERVERS", JsonSerializer.Serialize(new Dictionary<string, string[]> { ["c"] = [python, fake] }));
        try
        {
            var server = CodeLanguageServers.Server("c", home, workspace) ?? throw new Exception("the stand-in server was not found");
            check(server.LanguageId == "c", "the server named for .c files is found");
            File.WriteAllText(Path.Combine(workspace, "sum.c"), bad);
            await using (var sessions = new CodeLanguageSessions())
            {
                var found = await CodeLanguageServers.Errors(scope, server, "sum.c", bad, sessions, default) ?? "";
                check(found.Contains("reports 1 error in sum.c after this edit") && found.Contains("sum.c:3:12  error  use of undeclared identifier 'count'") && !found.Contains("unused function"), "a file's errors come back as a note, and its warnings do not: " + found);
                check(await CodeLanguageServers.Errors(scope, server, "sum.c", good, sessions, default) == null, "the same running server is told of a change, and a clean file gives no note");
                File.WriteAllText(Path.Combine(workspace, "sum.c"), bad);
                var definition = await CodeLanguageServers.Query(scope, server, "sum.c", 4, 21, "definitions", sessions, default);
                check(definition == server.Name + ": 1 definitions in the project.\nsum.c:1:5  int total(int a, int b) {", "a definition is reported as a place in the project: " + definition);
                var references = await CodeLanguageServers.Query(scope, server, "sum.c", 4, 21, "references", sessions, default);
                check(references.StartsWith(server.Name + ": 2 references in the project, and 1 outside it (libraries).\nsum.c:1:5") && references.Contains("sum.c:4:21  int main() { return total(1, count); }"), "references inside and outside the project are told apart: " + references);
                var diagnostics = await CodeLanguageServers.Query(scope, server, "sum.c", 1, 1, "diagnostics", sessions, default);
                check(diagnostics == server.Name + ": 2 diagnostics for sum.c.\nsum.c:3:12  error  use of undeclared identifier 'count'\nsum.c:1:1  warning  unused function", "asked for diagnostics, every severity is listed: " + diagnostics);
            }

            using (var store = new CodeStore(Path.Combine(root, "state"), home))
            {
                // Through the engine: the server is not sandboxed, so starting it is asked about once for the request.
                var session = store.New(workspace); session.Permission = CodePermission.WorkspaceEdits;
                var asked = new List<string>();
                await new CodeEngine(store, new Actions(Action("create_file", new { path = "bad.c", content = bad }), Action("create_file", new { path = "good.c", content = good })), (approval, _) => { asked.Add(approval.Description); return Task.FromResult(true); }, () => { }).Run(session, "Write two C files in this disposable workspace.", default);
                var written = session.Activities.Where(a => a.Tool == "create_file").ToArray();
                check(asked.Count == 1 && asked[0] == "Start " + server.Name + " to check this request's edits?", "starting the server is asked about once in a request: " + string.Join(" | ", asked));
                check(written.Length == 2 && written[0].State == "Completed" && written[0].Result.Contains("language_server") && written[0].Result.Contains("reports 1 error in bad.c after this edit") && written[0].Result.Contains("bad.c:3:12  error  use of undeclared identifier"), "the errors a server finds in an edit go back with its result: " + (written.Length > 0 ? written[0].Result : "no activity"));
                check(written.Length == 2 && !written[1].Result.Contains("language_server"), "a clean file adds nothing to the result");

                var declined = store.New(workspace); declined.Permission = CodePermission.WorkspaceEdits; int questions = 0;
                await new CodeEngine(store, new Actions(Action("create_file", new { path = "third.c", content = bad }), Action("create_file", new { path = "fourth.c", content = bad })), (_, _) => { questions++; return Task.FromResult(false); }, () => { }).Run(declined, "Write two more C files.", default);
                var refused = declined.Activities.Where(a => a.Tool == "create_file").ToArray();
                check(questions == 1 && refused.Length == 2 && refused.All(a => a.State == "Completed" && !a.Result.Contains("language_server")) && File.Exists(Path.Combine(workspace, "fourth.c")), "a declined server is not started, not asked about again, and the edits still land");

                var looking = store.New(workspace); looking.Permission = CodePermission.WorkspaceEdits; var lookups = new List<string>();
                var ask = Action("inspect_symbol", new { path = "sum.c", line = 4, column = 21, mode = "definitions" });
                await new CodeEngine(store, new Actions(Action("enable_tools", new { group = "code" }), ask, ask), (approval, _) => { lookups.Add(approval.RuleSubject); return Task.FromResult(true); }, () => { }).Run(looking, "Where is total defined?", default);
                var answers = looking.Activities.Where(a => a.Tool == "inspect_symbol").ToArray();
                check(lookups.Count == 1 && lookups[0] == "language server " + server.Name && answers.Length == 2 && answers.All(a => a.State == "Completed" && a.Result.Contains("sum.c:1:5")), "inspect_symbol asks the server for other languages, after one approval in a request: " + string.Join(" | ", answers.Select(a => a.State + " " + a.Result)));

                if (CodeLanguageServers.Server("hs", home, workspace) == null && CodeLanguageServers.Server("json", home, workspace) == null)
                {
                    Environment.SetEnvironmentVariable("MASON_LANGUAGE_SERVERS", null);
                    var missing = store.New(workspace); missing.Permission = CodePermission.WorkspaceEdits;
                    await new CodeEngine(store, new Actions(Action("create_file", new { path = "One.hs", content = "module One where\n" }), Action("create_file", new { path = "Two.hs", content = "module Two where\n" }), Action("create_file", new { path = "data.json", content = "{}\n" })),
                        (_, _) => throw new Exception("nothing to ask about"), () => { }).Run(missing, "Write three files.", default);
                    var made = missing.Activities.Where(a => a.Tool == "create_file").ToArray();
                    check(made.Length == 3 && made[0].Result.Contains("No language server for Haskell is installed") && !made[1].Result.Contains("No language server") && !made[2].Result.Contains("language_server"), "a missing server is said once in a request, and not at all for data files: " + string.Join(" | ", made.Select(a => a.Result)));
                }
            }

            // A server that only answers when asked is asked for the file's diagnostics.
            Environment.SetEnvironmentVariable("MASON_LANGUAGE_SERVERS", JsonSerializer.Serialize(new Dictionary<string, string[]> { ["c"] = [python, fake, "--pull"] }));
            var pulling = CodeLanguageServers.Server("c", home, workspace) ?? throw new Exception("the pulling stand-in was not found");
            await using (var sessions = new CodeLanguageSessions())
            {
                var found = await CodeLanguageServers.Errors(scope, pulling, "sum.c", "int total() {\n\n    return count;\n}\n", sessions, default) ?? "";
                check(found.Contains("sum.c:3:12  error  use of undeclared identifier 'count'") && await CodeLanguageServers.Errors(scope, pulling, "sum.c", "int total() { return 1; }\n", sessions, default) == null, "a server that publishes nothing is asked for the file's diagnostics: " + found);
            }
        }
        finally { Environment.SetEnvironmentVariable("MASON_LANGUAGE_SERVERS", null); }

        // A real server, where one is installed: clangd.
        if (CodeLanguageServers.Server("c", home, workspace) is { Name: "clangd" } clangd)
        {
            File.WriteAllText(Path.Combine(workspace, "sum.c"), bad);
            await using var sessions = new CodeLanguageSessions();
            var found = await CodeLanguageServers.Errors(scope, clangd, "sum.c", bad, sessions, default) ?? "";
            var definition = await CodeLanguageServers.Query(scope, clangd, "sum.c", 4, 21, "definitions", sessions, default);
            check(found.Contains("sum.c:4:30  error") && definition.Contains("sum.c:1:5  int total(int a, int b) {"), "a real clangd reports the error and the definition: " + found + " / " + definition);
        }
        else Console.WriteLine("SKIP a real language server: no clangd on this computer");

        // Downloads from the npm registry, so it runs only when asked for: MASON_TEST_INSTALL=1.
        if (Environment.GetEnvironmentVariable("MASON_TEST_INSTALL") != "1") { Console.WriteLine("SKIP installing a real server: set MASON_TEST_INSTALL=1"); return; }
        const string wrong = "name: demo\nitems: [one, two\n";
        File.WriteAllText(Path.Combine(workspace, "config.yaml"), wrong);
        var before = await CodeLanguageServers.Command("", scope, home, default);
        check(before.Contains("YAML [yaml]  ·  not installed: /languages install yaml (uses npm)"), "a server npm can fetch is offered: " + before);
        var said = await CodeLanguageServers.Command("install yaml", scope, home, default);
        var yaml = CodeLanguageServers.Server("yaml", home, workspace);
        check(said == "Installed yaml-language-server for YAML." && yaml != null && yaml.Command.Any(part => part.StartsWith(home, StringComparison.OrdinalIgnoreCase)), "the server is installed under Mason's data folder: " + said + " / " + string.Join(" ", yaml?.Command ?? []));
        await using (var sessions = new CodeLanguageSessions())
        {
            var found = await CodeLanguageServers.Errors(scope, yaml!, "config.yaml", wrong, sessions, default) ?? "";
            check(found.Contains("config.yaml:"), "the installed server reports the error in a broken file: " + found);
        }
    }

    sealed class Actions(params CodeAction[] actions) : ICodeModel
    {
        int index;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
            => Task.FromResult(index < actions.Length ? actions[index++] : new CodeAction("Fixture steps finished; inspect receipts for any failures.", null, default));
    }
}
