using System.IO;
using System.Text.Json;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace Mason;

/// <summary>
/// Harness settings for a project, laid out like Windows Settings: approval rules (G4), custom agents and commands (G5),
/// MCP servers (G6), scheduled runs (G7), the project policy and extension bundles.
/// </summary>
public sealed partial class MainWindow
{
    FrameworkElement HarnessSettings(CodeSession session, CodeViewerEntry entry)
    {
        var scope = new CodeWorkspace(session.Workspace);
        var status = new InfoBar { IsClosable = true, IsOpen = false };
        void Say(string message, InfoBarSeverity severity = InfoBarSeverity.Success) { status.Message = message; status.Severity = severity; status.IsOpen = true; }
        void Fail(Exception e) => Say(e.Message, InfoBarSeverity.Error);

        // ── G4 approval rules ──────────────────────────────────────────────────────────────────────
        var rulesHost = new StackPanel { Spacing = 4 };
        void RenderRules()
        {
            rulesHost.Children.Clear();
            var rules = CodeApprovalRules.Load(Store.Root, scope.Root);
            if (rules.Count == 0) { rulesHost.Children.Add(SettingsCard("\uE8FB", "No saved approvals", "Choose Always allow on an approval to stop being asked for that command, folder or MCP tool in this project.")); return; }
            foreach (var rule in rules)
            {
                var revoke = SubtleButton("Revoke", (_, _) => { try { CodeApprovalRules.Remove(Store.Root, scope.Root, rule); RenderRules(); Say("Rule revoked."); } catch (Exception e) { Fail(e); } }, "\uE711");
                rulesHost.Children.Add(SettingsCard(rule.Allow ? "\uE73E" : "\uE711", (rule.Allow ? "Allow " : "Deny ") + rule.Tool.Replace('_', ' '), (rule.Subject.Length == 0 ? "Any request" : "Starts with " + rule.Subject) + " · saved " + rule.Created.ToLocalTime().ToString("d"), revoke));
            }
        }
        RenderRules();

        // ── G5 agents and commands ─────────────────────────────────────────────────────────────────
        var definitionsHost = new StackPanel { Spacing = 4 };
        void RenderDefinitions()
        {
            definitionsHost.Children.Clear();
            IReadOnlyList<CodeDefinition> agents = [], commands = [];
            try { agents = CodeCustomization.Agents(Store.Root, scope.Root); commands = CodeCustomization.Commands(Store.Root, scope.Root); } catch (Exception e) { Fail(e); }
            foreach (var agent in agents)
                definitionsHost.Children.Add(SettingsCard("\uE77B", agent.Name, (agent.Description.Length > 0 ? agent.Description + " · " : "") + (agent.Mode == "ask" ? "Ask only · " : "") + agent.Source, Pill("Agent", Tone.Accent)));
            foreach (var command in commands)
                definitionsHost.Children.Add(SettingsCard("\uE756", "/" + command.Name, (command.Description.Length > 0 ? command.Description + " · " : "") + command.Source, Pill("Command", Tone.Neutral)));
            if (agents.Count + commands.Count == 0)
                definitionsHost.Children.Add(SettingsCard("\uE8A5", "No custom agents or commands yet", "Add Markdown files to .mason/agents or .mason/commands. Files in .claude/agents, .claude/commands and .opencode/command are read too."));
        }
        async Task CreateDefinition(bool agent)
        {
            var name = new TextBox { Header = "Name", PlaceholderText = agent ? "reviewer" : "fix-issue", MaxLength = 48 };
            var description = new TextBox { Header = "Description", PlaceholderText = agent ? "Reviews changes and reports risks" : "Fix a numbered issue" };
            var body = new TextBox { Header = agent ? "Instructions" : "Request template", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, PlaceholderText = agent ? "Never edit files. Read the change and list risks with file and line." : "Fix issue $1. Extra context: $ARGUMENTS" };
            var askOnly = new ToggleSwitch { Header = "Ask only (no edits or commands)", IsOn = agent };
            var content = Stack(Muted("Saved as " + Path.Combine(".mason", agent ? "agents" : "commands", "NAME.md") + " in the project, so it travels with the repository.", 14), name, description, body);
            if (agent) content.Children.Add(askOnly);
            if (await new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = agent ? "New agent" : "New command", Content = new ScrollViewer { Content = content, MaxHeight = 460 }, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary }.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                var path = await Task.Run(() => CodeCustomization.Create(scope.Root, agent, name.Text, description.Text, body.Text, askOnly.IsOn));
                RenderDefinitions(); Say("Created " + Path.GetRelativePath(scope.Root, path) + ".");
            }
            catch (Exception e) { Fail(e); }
        }
        RenderDefinitions();

        // ── G6 MCP servers ─────────────────────────────────────────────────────────────────────────
        var mcpHost = new StackPanel { Spacing = 4 };
        void RenderMcp()
        {
            mcpHost.Children.Clear();
            List<McpRegistration> servers;
            try { servers = CodeMcpRegistry.ReadAll(Store.Root); } catch (Exception e) { Fail(e); return; }
            if (servers.Count == 0) mcpHost.Children.Add(SettingsCard("\uE968", "No MCP servers", "Add a local stdio server (an .exe) or a streamable HTTP server on 127.0.0.1. Mason shows each call for approval unless a rule allows it."));
            foreach (var server in servers)
            {
                var enabled = Switch(server.Enabled, on =>
                {
                    try { var all = CodeMcpRegistry.ReadAll(Store.Root); var i = all.FindIndex(s => s.Name == server.Name); all[i] = all[i] with { Enabled = on }; CodeMcpRegistry.Save(Store.Root, all); Say(server.Name + (on ? " enabled." : " disabled.")); }
                    catch (Exception e) { Fail(e); RenderMcp(); }
                }, "Enable " + server.Name);
                var edit = SubtleIconButton("\uE70F", "Edit " + server.Name, async (_, _) => await EditMcp(server));
                var remove = SubtleIconButton("\uE74D", "Remove " + server.Name, (_, _) =>
                {
                    try { CodeMcpRegistry.Save(Store.Root, CodeMcpRegistry.ReadAll(Store.Root).Where(s => s.Name != server.Name).ToList()); RenderMcp(); Say("Removed " + server.Name + "."); } catch (Exception e) { Fail(e); }
                });
                var transport = server.Executable != null ? "stdio · " + Path.GetFileName(server.Executable) : "HTTP · " + server.Endpoint;
                mcpHost.Children.Add(SettingsCard(server.Executable != null ? "\uE756" : "\uE774", server.Name, transport + " · " + server.Tools.Length + " allowed tools" + (server.Tools.Length > 0 ? ": " + string.Join(", ", server.Tools.Take(4)) + (server.Tools.Length > 4 ? "…" : "") : ""), Row(edit, remove, enabled)));
            }
        }
        async Task EditMcp(McpRegistration? existing)
        {
            var name = new TextBox { Header = "Name", Text = existing?.Name ?? "", PlaceholderText = "docs", IsEnabled = existing == null };
            var kind = new Segmented("Transport", new (string Label, string? Glyph)[] { ("Local program (stdio)", "\uE756"), ("HTTP on this computer", "\uE774") }, existing?.Executable == null && existing != null ? 1 : 0);
            var executable = new TextBox { Header = "Program", Text = existing?.Executable ?? "", PlaceholderText = @"C:\Tools\docs-mcp.exe" };
            var arguments = new TextBox { Header = "Arguments (one per line)", Text = string.Join("\n", existing?.Arguments ?? []), AcceptsReturn = true, MinHeight = 64 };
            var endpoint = new TextBox { Header = "Endpoint", Text = existing?.Executable == null ? existing?.Endpoint ?? "" : "", PlaceholderText = "http://127.0.0.1:8088/mcp" };
            var browse = Button("Browse…", async (_, _) => { var picker = new FileOpenPicker(AppWindow.Id); picker.FileTypeFilter.Add(".exe"); var file = await picker.PickSingleFileAsync(); if (file != null) executable.Text = file.Path; });
            var tools = new StackPanel { Spacing = 2 };
            var toolNote = Muted("Choose which tools the agent may call. Discover lists what the server offers.", 12);
            var chosen = new HashSet<string>(existing?.Tools ?? [], StringComparer.Ordinal);
            void ShowTools(IEnumerable<McpToolInfo> found)
            {
                tools.Children.Clear();
                foreach (var tool in found)
                {
                    var box = new CheckBox { Content = new StackPanel { Children = { new TextBlock { Text = tool.Name, Style = Token("MasonBody") }, new TextBlock { Text = tool.Description, Style = Token("MasonCaptionSecondary"), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis } } }, IsChecked = chosen.Contains(tool.Name) };
                    box.Checked += (_, _) => chosen.Add(tool.Name); box.Unchecked += (_, _) => chosen.Remove(tool.Name);
                    tools.Children.Add(box);
                }
            }
            ShowTools(chosen.Select(t => new McpToolInfo(t, "")));
            McpRegistration Draft() => kind.Selected == 0
                ? new(name.Text.Trim(), "", chosen.ToArray(), executable.Text.Trim(), arguments.Text.Split('\r', '\n').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray(), existing?.Resources, existing?.Enabled ?? true)
                : new(name.Text.Trim(), endpoint.Text.Trim(), chosen.ToArray(), null, null, existing?.Resources, existing?.Enabled ?? true);
            var discover = IconButton("\uE721", "Discover tools", async (_, _) =>
            {
                try { toolNote.Text = "Connecting…"; var found = await CodeMcpRegistry.Discover(Draft() with { Tools = [] }, entry.Cancellation.Token); ShowTools(found); toolNote.Text = found.Length + " tools offered."; }
                catch (Exception e) { toolNote.Text = "Could not connect: " + e.Message; }
            });
            var stdioPanel = Stack(Row(executable, browse), arguments); executable.MinWidth = 320;
            void Arrange() { stdioPanel.Visibility = kind.Selected == 0 ? Visibility.Visible : Visibility.Collapsed; endpoint.Visibility = kind.Selected == 1 ? Visibility.Visible : Visibility.Collapsed; }
            kind.Changed += _ => Arrange(); Arrange();
            var content = Stack(name, kind.View, stdioPanel, endpoint, Divider(), Row(new TextBlock { Text = "Allowed tools", Style = Token("MasonBodyStrong"), VerticalAlignment = VerticalAlignment.Center }, discover), toolNote, tools);
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = existing == null ? "Add MCP server" : "Edit " + existing.Name, Content = new ScrollViewer { Content = content, MaxHeight = 520 }, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                var all = CodeMcpRegistry.ReadAll(Store.Root).Where(s => s.Name != (existing?.Name ?? name.Text.Trim())).ToList();
                all.Add(Draft()); CodeMcpRegistry.Save(Store.Root, all); RenderMcp(); Say("Saved " + name.Text.Trim() + ".");
            }
            catch (Exception e) { Fail(e); }
        }
        RenderMcp();

        // ── G7 schedules ───────────────────────────────────────────────────────────────────────────
        var schedulesHost = new StackPanel { Spacing = 4 };
        void RenderSchedules()
        {
            schedulesHost.Children.Clear();
            var all = CodeSchedules.Load(Store.Root).Where(s => s.Workspace.Equals(scope.Root, StringComparison.OrdinalIgnoreCase)).ToList();
            if (all.Count == 0) schedulesHost.Children.Add(SettingsCard("\uE823", "No scheduled runs", "Run a request on a schedule as a background task, for example nightly tests or a weekly dependency review. Runs happen while Mason is open; a missed run starts once when it next opens."));
            foreach (var schedule in all)
            {
                var toggle = Switch(schedule.Enabled, on => { UpdateSchedule(schedule.Id, s => s with { Enabled = on }); }, "Enable " + schedule.Name);
                var run = SubtleIconButton("\uE768", "Run now", (_, _) => { RunSchedule(schedule); RenderSchedules(); });
                var remove = SubtleIconButton("\uE74D", "Remove " + schedule.Name, (_, _) => { try { CodeSchedules.Save(Store.Root, CodeSchedules.Load(Store.Root).Where(s => s.Id != schedule.Id).ToList()); RenderSchedules(); } catch (Exception e) { Fail(e); } });
                var last = schedule.LastRun is { } at ? " · last " + at.ToLocalTime().ToString("g") + (schedule.LastOutcome.Length > 0 ? " (" + schedule.LastOutcome + ")" : "") : "";
                schedulesHost.Children.Add(SettingsCard("\uE823", schedule.Name, schedule.Describe() + (schedule.Auto ? " · Auto (sandboxed)" : " · Read-only") + last, Row(run, remove, toggle)));
            }
        }
        void UpdateSchedule(Guid id, Func<CodeSchedule, CodeSchedule> change)
        {
            try { var all = CodeSchedules.Load(Store.Root); var i = all.FindIndex(s => s.Id == id); if (i < 0) return; all[i] = change(all[i]); CodeSchedules.Save(Store.Root, all); RenderSchedules(); }
            catch (Exception e) { Fail(e); }
        }
        async Task AddSchedule()
        {
            var connections = (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).Where(p => p.ManagedCapability is null or AiCapability.Chat).ToList();
            if (connections.Count == 0) { Say("Save a chat model connection in Models first; schedules run with a saved connection.", InfoBarSeverity.Warning); return; }
            var name = new TextBox { Header = "Name", PlaceholderText = "Nightly tests" };
            var prompt = new TextBox { Header = "Request", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, PlaceholderText = "Run the test suite and summarise any failures with file and line." };
            var cadence = new Segmented("Cadence", new (string Label, string? Glyph)[] { ("Hourly", null), ("Daily", null), ("Weekdays", null), ("Weekly", null) }, 2);
            var time = new TimePicker { Header = "At", Time = new TimeSpan(9, 0, 0), MinuteIncrement = 5 };
            var day = new ComboBox { Header = "Day", ItemsSource = Enum.GetNames<DayOfWeek>(), SelectedIndex = 1 };
            var model = new ComboBox { Header = "Model connection", ItemsSource = connections.Select(c => c.DisplayName).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var auto = new ToggleSwitch { Header = "Allow edits and commands (Auto, sandboxed to this project)", IsOn = false };
            cadence.Changed += i => day.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed; day.Visibility = Visibility.Collapsed;
            var content = Stack(name, prompt, new TextBlock { Text = "Cadence", Style = Token("MasonBody") }, cadence.View, Row(time, day), model, auto);
            if (await new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Schedule a run", Content = new ScrollViewer { Content = content, MaxHeight = 520 }, PrimaryButtonText = "Schedule", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary }.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                var schedule = new CodeSchedule(Guid.NewGuid(), name.Text.Trim(), scope.Root, prompt.Text.Trim(), connections[model.SelectedIndex].Id, auto.IsOn, (ScheduleCadence)cadence.Selected, TimeOnly.FromTimeSpan(time.Time), (DayOfWeek)Math.Max(0, day.SelectedIndex)) { LastRun = DateTimeOffset.Now };
                CodeSchedules.Save(Store.Root, CodeSchedules.Load(Store.Root).Append(schedule).ToList());
                RenderSchedules(); Say("Scheduled “" + schedule.Name + "” · next " + schedule.Next(DateTimeOffset.Now).ToString("g") + ".");
            }
            catch (Exception e) { Fail(e); }
        }
        RenderSchedules();

        // ── Project policy and bundles (existing) ──────────────────────────────────────────────────
        var current = CodeProjectPolicy.Load(Store.Root, scope.Root, true);
        var name = new TextBox { Header = "Policy name", Text = current?.Name ?? "", MaxLength = 80, PlaceholderText = "Release freeze" };
        var workflows = new TextBox { Header = "Allowed model workflows", Text = current?.Workflows == null ? "*" : string.Join(", ", current.Workflows), PlaceholderText = "* for all, empty for none" };
        var expiry = new TextBox { Header = "Expires (optional, UTC)", Text = current?.Expires?.UtcDateTime.ToString("O") ?? "" };
        var hooks = new ToggleSwitch { Header = "Run project workflow events in Auto", IsOn = current?.EnableHooks == true };
        string[] List(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
        var apply = Button("Apply policy", (_, _) =>
        {
            try
            {
                DateTimeOffset? until = null; if (expiry.Text.Trim().Length > 0) until = DateTimeOffset.Parse(expiry.Text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
                // Tools and skills are switched in their own sections; the policy keeps whatever they set.
                var latest = CodeProjectPolicy.Load(Store.Root, scope.Root, true);
                CodeProjectPolicy.Set(Store.Root, scope.Root, new CodeProjectPolicy(name.Text.Trim(), scope.Root, latest?.DisabledTools ?? [], workflows.Text.Trim() == "*" ? null : List(workflows.Text), until, hooks.IsOn, latest?.DisabledSkills));
                Say("Policy saved for " + scope.Root);
            }
            catch (Exception e) { Fail(e); }
        }, true);
        var revoke = Button("Revoke policy", (_, _) => { try { CodeProjectPolicy.Set(Store.Root, scope.Root, null); name.Text = expiry.Text = ""; workflows.Text = "*"; hooks.IsOn = false; Say("Policy revoked."); } catch (Exception e) { Fail(e); } });
        var policyForm = new Border { Style = Token("MasonCard"), Child = Stack(FormGrid(name, expiry, workflows), hooks, Row(apply, revoke)) };

        var bundles = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.Single };
        var bundleInfo = new TextBlock { Style = Token("MasonMono"), MaxLines = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        InstalledBundle[] packages = [];
        void Refresh() { packages = CodeBundles.List(Store.Root); bundles.ItemsSource = packages.Select(b => b.Package.Name + " " + b.Package.Version + " · " + (b.Enabled ? "Enabled" : "Disabled")).ToArray(); bundleInfo.Text = ""; }
        bundles.SelectionChanged += (_, _) => { if (bundles.SelectedIndex >= 0 && bundles.SelectedIndex < packages.Length) bundleInfo.Text = JsonSerializer.Serialize(packages[bundles.SelectedIndex].Package, new JsonSerializerOptions { WriteIndented = true }); };
        var install = IconButton("\uE8B5", "Inspect package…", async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(AppWindow.Id); picker.FileTypeFilter.Add(".json"); var f = await picker.PickSingleFileAsync(); if (f == null) return;
                if (new FileInfo(f.Path).Length > 512000) throw new InvalidDataException("Extension package is too large.");
                var p = CodeBundles.Inspect(await File.ReadAllTextAsync(f.Path, entry.Cancellation.Token));
                var dlg = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = p.Name + " " + p.Version, Content = CodeScroll(CodeNote(JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }))), PrimaryButtonText = "Install disabled", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary) { CodeBundles.Save(Store.Root, p, false); Refresh(); Say("Installed " + p.Name + " " + p.Version + " (disabled)."); }
            }
            catch (Exception e) { Fail(e); }
        });
        var toggleBundle = Button("Enable or disable", (_, _) => { try { if (bundles.SelectedIndex < 0) return; var b = packages[bundles.SelectedIndex]; CodeBundles.Save(Store.Root, b.Package, !b.Enabled); Refresh(); } catch (Exception e) { Fail(e); } });
        var uninstall = Button("Uninstall", async (_, _) =>
        {
            try
            {
                if (bundles.SelectedIndex < 0) return; var b = packages[bundles.SelectedIndex];
                if (await new ContentDialog { Title = "Uninstall " + b.Package.Name + "?", XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, PrimaryButtonText = "Uninstall", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary) { CodeBundles.Remove(Store.Root, b.Package.Name); Refresh(); }
            }
            catch (Exception e) { Fail(e); }
        });
        Refresh();

        // Language servers: which are installed for this project's languages, and installing the ones a language's own tool can fetch.
        var languagesHost = new StackPanel { Spacing = 4 };
        void RenderLanguages()
        {
            languagesHost.Children.Clear();
            var listed = CodeLanguageServers.Present(scope);
            if (listed.Length == 0) { languagesHost.Children.Add(SettingsCard("\uE943", "No language servers apply", "No file in this project is in a language Mason knows a server for. Type /languages all in the Mason command line for the full list.")); return; }
            foreach (var language in listed)
            {
                var (described, canInstall) = CodeLanguageServers.Described(language, Store.Root);
                Button? install = null;
                if (canInstall)
                {
                    install = Button("Install", async (sender, _) =>
                    {
                        var pressed = (Button)sender; pressed.IsEnabled = false;
                        try { Say("Installing the " + language.Name + " server. This can take a few minutes.", InfoBarSeverity.Informational); Say(await Task.Run(() => CodeLanguageServers.Install(language, Store.Root, entry.Cancellation.Token))); }
                        catch (Exception e) when (e is InvalidOperationException or IOException or OperationCanceledException) { Fail(e); }
                        RenderLanguages();
                    });
                }
                languagesHost.Children.Add(SettingsCard("\uE943", language.Name, described, install));
            }
        }
        RenderLanguages();
        var languagesNote = CodeNote("After each edit Mason hands the file to its language's server, and the errors the server reports go back to the model. A server runs with your Windows account, so Mason asks before starting one. Mason installs one only when you choose Install, using that language's own tool.");

        var (skillActions, skillsHost) = SkillsSection(scope, message => Say(message), Fail);
        var toolsHost = ToolCatalogSection(scope, message => Say(message), Fail);
        var internetHost = InternetSection(scope, message => Say(message), Fail);
        var mcpHeading = new TextBlock { Text = "MCP servers", Style = Token("MasonBodyStrong"), Margin = new Thickness(1, 12, 0, 2) };

        var page = SettingsPage(
            PageHeader("Harness settings", "Approvals, agents, skills, tools and schedules for " + Path.GetFileName(scope.Root)),
            status,
            SettingsGroup("Approvals you saved"), rulesHost,
            SettingsGroup("Agents and commands"), Row(IconButton("\uE77B", "New agent", async (_, _) => await CreateDefinition(true)), IconButton("\uE756", "New command", async (_, _) => await CreateDefinition(false)), Button("Refresh", (_, _) => RenderDefinitions())), definitionsHost,
            SettingsGroup("Skills"), skillActions, skillsHost,
            SettingsGroup("Tools"), Muted("Switch a tool off and Mason will not use it in this project.", 12), toolsHost,
            mcpHeading, Row(IconButton("\uE710", "Add server", async (_, _) => await EditMcp(null), true)), mcpHost,
            SettingsGroup("Internet"), internetHost,
            SettingsGroup("Scheduled runs"), Row(IconButton("\uE710", "Schedule a run", async (_, _) => await AddSchedule(), true)), schedulesHost,
            SettingsGroup("Project policy"), policyForm,
            SettingsGroup("Language servers"), languagesNote, languagesHost,
            SettingsGroup("Extensions"), new Border { Style = Token("MasonCard"), Child = Stack(bundles, bundleInfo, Row(install, toggleBundle, uninstall)) });
        page.Padding = new Thickness(24, 16, 24, 0);
        return page;
    }
}
