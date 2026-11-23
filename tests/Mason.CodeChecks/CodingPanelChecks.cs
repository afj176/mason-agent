using Mason.Code;
using System.Text.Json;

static class CodingPanelChecks
{
    public static async Task Run(string root, string workspace, Action<bool, string> check)
    {
        var session = new CodeSession();
        check(!session.ViewerVisible && session.SidebarVisible, "new sessions reserve the viewer until an artifact exists");
        CodePanels.Show(session);
        check(!session.ViewerVisible, "show cannot create an empty artifact panel");
        var file = new CodeTab { Kind = "file", Target = "README.md" };
        var changes = new CodeTab { Kind = "changes", Target = "" };
        var emptyPinned = new CodeSession { ViewerPinned = true };
        CodePanels.Open(emptyPinned, file, false);
        check(emptyPinned.ViewerVisible && !emptyPinned.ViewerPinned, "a legacy empty-placeholder pin cannot suppress the first real artifact");
        CodePanels.Open(session, file, false);
        check(session.ViewerVisible && session.SelectedTab == file.Key, "a model artifact opens and selects the dynamic viewer");
        CodePanels.Hide(session); CodePanels.Open(session, changes, false);
        check(!session.ViewerVisible && session.Tabs.Count == 2 && session.ViewerDismissed, "later artifacts are retained without reopening a dismissed viewer");
        session.ViewerPinned = true; session.SelectedTab = file.Key;
        CodePanels.Open(session, changes, false);
        check(session.SelectedTab == file.Key && !session.ViewerVisible, "a pinned artifact retains selection while hidden");
        CodePanels.Open(session, changes, true);
        check(session.SelectedTab == changes.Key && session.ViewerVisible && !session.ViewerDismissed, "explicit artifact inspection reopens the viewer even when pinned");
        CodePanels.Close(session, changes.Key);
        check(session.SelectedTab == file.Key && session.ViewerVisible, "closing the selected tab restores a remaining artifact");
        CodePanels.Close(session, file.Key);
        check(!session.ViewerVisible && session.Tabs.Count == 0 && session.SelectedTab == null && !session.ViewerPinned, "closing the last artifact hides the viewer and clears an obsolete pin");
        session.Tabs.Add(file); CodePanels.Hide(session);
        var roundTrip = JsonSerializer.Deserialize<CodeSession>(JsonSerializer.Serialize(session))!;
        check(roundTrip.ViewerDismissed && !roundTrip.ViewerVisible && roundTrip.Tabs.Count == 1, "dismissal and retained tabs survive serialization");
        var legacy = JsonSerializer.Deserialize<CodeSession>("{\"ViewerVisible\":true,\"SidebarVisible\":false}")!;
        check(!legacy.ViewerDismissed && !legacy.SidebarVisible, "legacy sessions load without a dismissal migration");

        using var store = new CodeStore(Path.Combine(root, "panel-lifecycle"));
        var run = store.New(workspace); run.Mode="ask"; bool dismissed = false;
        await new CodeEngine(store, new PanelModel("README.md", "AGENTS.md"), (_, _) => Task.FromResult(false), () =>
        {
            if (!dismissed && run.ViewerVisible) { dismissed = true; CodePanels.Hide(run); }
        }).Run(run, "Inspect README and repository instructions.", default);
        check(run.State == "Completed" && dismissed && !run.ViewerVisible && run.Tabs.Count == 2,
            "production engine does not reopen a viewer dismissed between two real file artifacts");
        await new CodeEngine(store, new PanelModel("README.md"), (_, _) => Task.FromResult(false), () => { })
            .Run(run, "Inspect README again in this new request.", default);
        check(run.State == "Completed" && run.ViewerVisible && !run.ViewerDismissed,
            "a new request permits its next artifact to open the viewer again");
    }

    sealed class PanelModel(params string[] paths) : ICodeModel
    {
        int index;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
            => Task.FromResult(index < paths.Length
                ? new CodeAction("Inspecting the file.", "open_file", JsonSerializer.SerializeToElement(new { path = paths[index++] }))
                : new CodeAction("Inspected the files.", null, default));
    }
}
