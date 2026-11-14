using System.Text.Json;
using Mason;
using Mason.Code;

static class UiFixture
{
    public static async Task Prepare(string destination)
    {
        var root = Path.GetFullPath(destination); Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "storefront-web"); Directory.CreateDirectory(workspace);
        var source = """
            def catalog(items, limit=12, offset=0):
                return items[offset:offset + limit]
            """;
        File.WriteAllText(Path.Combine(workspace, "catalog.py"), source);
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "Keep the existing offset API. Use the Python standard library only.");
        File.WriteAllText(Path.Combine(workspace, "README.md"), "# Catalog API\n\nThe catalog supports limit and offset.\n");
        var scope = new CodeWorkspace(workspace); var file = scope.Read("catalog.py");
        using var store = new CodeStore(Path.Combine(root, "qa-data")); var session = store.New(workspace);
        session.Title = "Add catalog pagination"; session.Permission = CodePermission.WorkspaceEdits;
        session.Model = Guid.Parse("401bf4ab-ef31-47a0-8506-7d937e86eca1");
        var updated = """
            def catalog(items, limit=12, offset=0, cursor=None):
                if cursor is not None:
                    if cursor not in items:
                        raise ValueError("Invalid cursor")
                    offset = items.index(cursor) + 1
                return items[offset:offset + limit]
            """;
        var tests = """
            import unittest
            from catalog import catalog

            class CatalogTests(unittest.TestCase):
                def test_default(self): self.assertEqual(catalog([1, 2, 3]), [1, 2, 3])
                def test_limit(self): self.assertEqual(catalog([1, 2, 3], limit=2), [1, 2])
                def test_offset(self): self.assertEqual(catalog([1, 2, 3], offset=1), [2, 3])
                def test_cursor(self): self.assertEqual(catalog([1, 2, 3], cursor=1), [2, 3])
                def test_last(self): self.assertEqual(catalog([1, 2, 3], cursor=3), [])
                def test_invalid(self):
                    with self.assertRaises(ValueError): catalog([1, 2, 3], cursor=4)

            if __name__ == "__main__": unittest.main()
            """;
        var docs = """
            # Catalog API

            The catalog keeps its existing offset API and accepts an optional cursor.

            ## Cursor pagination

            Pass the last visible item as the next cursor. The returned list starts after that item.

            ```python
            catalog([1, 2, 3, 4], limit=2)            # [1, 2]
            catalog([1, 2, 3, 4], limit=2, cursor=2)  # [3, 4]
            ```

            ## Compatibility

            Existing offset requests keep their behavior. A cursor takes precedence over offset.

            ## Validation

            An unknown cursor raises ValueError. Six standard-library unit tests cover defaults, limits, offsets, valid cursors, final pages, and invalid cursors.

            ## Local preview

            The companion website is served by an isolated QA helper on loopback. This fixture uses scripted agent actions; it is not model-quality evidence.
            """;
        CodeAction T(string name, object arguments, string message = "") => new(message, name, JsonSerializer.SerializeToElement(arguments));
        var model = new ScriptedModel(
            T("read_file", new { path = "catalog.py" }, "I will inspect the catalog contract and preserve the existing offset behavior."),
            T("edit_file", new { path = "catalog.py", expected_sha256 = file.Sha256, old_text = source, new_text = updated }, "The route can accept an optional cursor without changing existing offset calls."),
            T("create_file", new { path = "test_catalog.py", content = tests }),
            T("edit_file", new { path = "README.md", expected_sha256 = scope.Read("README.md").Sha256, old_text = scope.Read("README.md").Content, new_text = docs }),
            T("run_command", new { executable = CodeCommand.ResolveExecutable("python.exe", workspace), arguments = new[] { "-m", "unittest", "-v" }, directory = "." }, "The code and usage notes are updated. I am running the compatibility and cursor tests."),
            T("open_file", new { path = "README.md" }),
            new("## Pagination and usage notes updated\n\nCursor requests now start after the last visible item. Existing offset requests keep their behavior.\n\nThe README includes first-page and next-page examples, compatibility notes, and invalid-cursor handling.\n\nAll six Python unit tests passed. Review the three changed files or open the local preview alongside this conversation.", null, default));
        await new CodeEngine(store, model, (_, _) => Task.FromResult(true), () => { }).Run(session, "Add cursor pagination to the catalog, preserve existing clients, and document how to use it.", default);
        if (session.State != "Completed" || session.Activities.Any(a => a.State != "Completed") || !session.Activities.Any(a => a.Tool == "run_command" && a.Result.Contains("Ran 6 tests"))) throw new Exception("The actual native fixture run failed: " + session.Detail + "\n" + string.Join('\n', session.Activities.Select(a => a.Result)));
        session.Tabs.Add(new() { Kind = "web", Target = "http://127.0.0.1:51796/" });
        session.SelectedTab = "file:README.md"; session.References.Add("catalog.py"); session.References.Add("AGENTS.md");
        store.Save(session);
        File.WriteAllText(Path.Combine(root, "qa-data", "ai-connections.json"), JsonSerializer.Serialize(new[] { new AiEndpointProfile { Id = session.Model.Value, Name = "Scripted QA model", Model = "code-qa", Endpoint = "http://127.0.0.1:51796" } }));
        Console.WriteLine("Native UI fixture ready: " + root);
    }
}
