using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mason;

public sealed partial class MainWindow
{
    sealed record CodeExplorerItem(string Name, string Path, bool Directory, string Status)
    {
        public string Glyph => Directory ? "\uE8B7" : System.IO.Path.GetExtension(Path).ToLowerInvariant() switch { ".md" or ".docx" or ".pdf" => "\uE8A5", ".png" or ".jpg" => "\uE91B", _ => "\uE943" };
        public override string ToString() => Name;
    }
    static TreeView CodeExplorer()
    {
        var tree = new TreeView { SelectionMode = TreeViewSelectionMode.Single };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tree, "Workspace files");
        tree.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid ColumnSpacing="7" ToolTipService.ToolTip="{Binding Content.Path}">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                  <FontIcon Glyph="{Binding Content.Glyph}" FontSize="14" Opacity=".8"/>
                  <TextBlock Grid.Column="1" Text="{Binding Content.Name}" FontSize="13" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                  <TextBlock Grid.Column="2" Text="{Binding Content.Status}" FontSize="11" Margin="4,0" Opacity=".8" VerticalAlignment="Center"/>
                </Grid>
            </DataTemplate>
            """);
        return tree;
    }
    static CodeExplorerItem? CodeExplorerSelection(TreeView tree) => tree.SelectedNode?.Content as CodeExplorerItem;
    FrameworkElement CodeContextViewer(CodeSession session, CodeViewerEntry entry, Action save, Action<string, string> open)
    {
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        string revision = "";
        entry.Refresh = () =>
        {
            string next = $"{session.Checkpoints.Count}|{session.State}|{session.AutomaticCompaction}|{codeOperation != null}";
            if (revision == next && host.Content != null) return; revision = next;
            bool running = codeOperation != null;
            var automatic = new ToggleSwitch { Header = "Automatic compaction", IsOn = session.AutomaticCompaction, IsEnabled = !running };
            automatic.Toggled += (_, _) => { session.AutomaticCompaction = automatic.IsOn; save(); };
            var compact = Button("Compact now", async (_, _) => { if (compactCodeContext != null) await compactCodeContext(); }); compact.IsEnabled = !running && session.Messages.Count > 0;
            var body = Stack(Text("Coding context", 19), CodeNote("Checkpoints shorten model context. The full conversation, original requests, tool receipts, approvals and file snapshots remain in this session."), automatic, Row(compact, CodeLink("View full tool receipts", (_, _) => open("activity", ""))));
            var active = CodeContext.Active(session);
            if (active == null) body.Children.Add(CodeNote("No verified checkpoint yet. Context is measured with the selected model's tokenizer before every action."));
            foreach (var checkpoint in session.Checkpoints.AsEnumerable().Reverse())
            {
                body.Children.Add(Text((checkpoint == active ? "Current checkpoint" : "Retained checkpoint") + " · " + checkpoint.At.LocalDateTime.ToString("g"), 17));
                body.Children.Add(CodeNote($"{checkpoint.BeforeTokens?.ToString("N0") ?? "Unmeasured"} → {checkpoint.AfterTokens:N0} / {checkpoint.ContextTokens:N0} tokens · {checkpoint.ThroughMessages} source messages · {checkpoint.Batches} digest " + (checkpoint.Batches == 1 ? "batch" : "batches")));
                if (checkpoint == active) body.Children.Add(CodeNote($"These notes cover the older prefix. {session.Messages.Count - checkpoint.ThroughMessages} newer messages remain verbatim after it and may resolve open work listed below."));
                body.Children.Add(CodeNote("Model: " + checkpoint.Model + "\nSource SHA-256: " + checkpoint.SourceHash));
                body.Children.Add(CodeMarkdown(checkpoint.Summary));
                if (checkpoint == active)
                {
                    body.Children.Add(Text("Retained requests and receipts", 18));
                    var ledger = CodeSource(CodeContext.Ledger(session, checkpoint.ThroughMessages), 12); ledger.TextWrapping = TextWrapping.Wrap; body.Children.Add(ledger);
                }
            }
            body.Margin = new Thickness(22); host.Content = CodeScroll(body);
        };
        entry.Refresh(); return host;
    }
    static void CodeExplorerPopulate(TreeView tree, IEnumerable<string> paths, string filter, CodeSession? session, CodeGitStatus? git)
    {
        bool initial = tree.RootNodes.Count == 0 || tree.RootNodes.Count == 1 && tree.RootNodes[0].Children.Count == 0;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Remember(IEnumerable<TreeViewNode> nodes) { foreach (var node in nodes) { if (node.IsExpanded && node.Content is CodeExplorerItem item) expanded.Add(item.Path); Remember(node.Children); } }
        Remember(tree.RootNodes); string? selected = CodeExplorerSelection(tree)?.Path;
        tree.SelectedNode = null; tree.RootNodes.Clear(); var nodes = new Dictionary<string, TreeViewNode>(StringComparer.OrdinalIgnoreCase);
        var rootNode = new TreeViewNode { Content = new CodeExplorerItem(session == null ? "Workspace" : System.IO.Path.GetFileName(session.Workspace), ".", true, ""), IsExpanded = initial || expanded.Contains(".") || filter.Length > 0 };
        TreeViewNode? selectedNode = null;
        var changed = session?.Changes.Where(c => !c.Restored).GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Before == null ? "A" : "M", StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var file in git?.Files ?? []) changed.TryAdd(file.Path, file.Status.Trim());
        foreach (var path in paths.Where(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var parts = path.Split('/'); string key = ""; TreeViewNode? parent = null;
            for (int i = 0; i < parts.Length; i++)
            {
                key = key.Length == 0 ? parts[i] : key + "/" + parts[i]; bool directory = i < parts.Length - 1;
                if (!nodes.TryGetValue(key, out var node))
                {
                    node = new TreeViewNode { Content = new CodeExplorerItem(parts[i], key, directory, directory ? "" : changed.GetValueOrDefault(key, "")), IsExpanded = directory && (expanded.Contains(key) || filter.Length > 0 || initial && parts.Length <= 3) };
                    nodes[key] = node; if (parent == null) rootNode.Children.Add(node); else parent.Children.Add(node);
                }
                parent = node;
                if (key == selected && !directory) selectedNode = node;
            }
        }
        // Mount a complete graph, then restore selection. Restoring a selected descendant
        // while its root is still being populated can retain a stale flattened filter view.
        tree.RootNodes.Add(rootNode); tree.SelectedNode = selectedNode;
    }
    FrameworkElement CodeGitViewer(CodeSession session, CodeViewerEntry entry, Action<string, string> open)
    {
        var message = CodeNote("Reading repository status…"); var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Button reload = null!; reload = Button("Refresh", async (_, _) => await Load());
        var body = Vertical(Toolbar(Text("Repository", 19), reload), CodeScroll(Stack(message, content)));
        body.Padding = new Thickness(14, 12, 14, 0);
        body.Loaded += async (_, _) => await Load();
        async Task Load()
        {
            reload.IsEnabled = false;
            try
            {
                var status = await CodeGit.Inspect(session.Workspace, entry.Cancellation.Token);
                message.Text = status.Branch + " · " + status.Detail;
                var list = new StackPanel { Spacing = 7, Margin = new Thickness(12) };
                foreach (var file in status.Files)
                {
                    string target = file.Path;
                    list.Children.Add(CodeLink(file.Status + "  " + file.Path + (file.PreviousPath == null ? "" : " ← " + file.PreviousPath), (_, _) => open("file", target)));
                }
                if (!string.IsNullOrWhiteSpace(status.Worktrees)) { list.Children.Add(Text("Worktrees", 18)); list.Children.Add(ChatInline(status.Worktrees, 12, true)); }
                list.Children.Add(CodeNote("Safe metadata-only observation. Full working-tree status and linked worktree discovery are unavailable. Session changes are shown separately; repository-configured programs are never run automatically."));
                content.Content = list;
            }
            catch (OperationCanceledException) { }
            finally { reload.IsEnabled = true; }
        }
        return body;
    }
}
