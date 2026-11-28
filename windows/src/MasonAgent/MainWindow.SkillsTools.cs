using System.Diagnostics;
using System.IO;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

/// <summary>
/// Harness settings: Skills (on/off per project, Open, Show in folder, View, New skill…) and Tools (the catalogue with a
/// switch per tool, then the Internet block). The same behaviour as /skills, /tools and /network in the command line.
/// </summary>
public sealed partial class MainWindow
{
    static void OpenWithDefault(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true }); }
    }
    static void ShowInFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });

    /// <summary>The Skills section: a card per skill and a New skill… button.</summary>
    (FrameworkElement Actions, StackPanel List) SkillsSection(CodeWorkspace scope, Action<string> say, Action<Exception> fail)
    {
        var host = new StackPanel { Spacing = 4 };
        void Render()
        {
            host.Children.Clear();
            IReadOnlyList<CodeSkill> skills;
            try { skills = CodeSkills.List(Store.Root, scope.Root); } catch (Exception e) { fail(e); return; }
            if (skills.Count == 0) { host.Children.Add(SettingsCard("\uE82D", "No skills yet", "A skill is a SKILL.md the agent reads before work it covers. New skill makes one in .mason/skills. Skills in .claude/skills, .agents/skills and installed extensions are read too.")); return; }
            foreach (var skill in skills)
            {
                string? file = skill.Path == null ? null : Path.Combine(scope.Root, skill.Path.Replace('/', Path.DirectorySeparatorChar));
                var open = SubtleIconButton("\uE8A7", "Open " + skill.Name, (_, _) => { try { OpenWithDefault(file!); } catch (Exception e) { fail(e); } });
                var folder = SubtleIconButton("\uE838", "Show " + skill.Name + " in folder", (_, _) => { try { ShowInFolder(file!); } catch (Exception e) { fail(e); } });
                open.IsEnabled = folder.IsEnabled = file != null;
                var view = SubtleIconButton("\uE7B3", "View " + skill.Name, async (_, _) =>
                {
                    try
                    {
                        var text = file != null ? await File.ReadAllTextAsync(file) : skill.Body;
                        var note = CodeNote(text); note.IsTextSelectionEnabled = true; note.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
                        var content = CodeScroll(Stack(Muted(file ?? skill.Source, 12), note)); content.MaxHeight = 480;
                        await new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = skill.Name, Content = content, CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close }.ShowAsync();
                    }
                    catch (Exception e) { fail(e); }
                });
                // A hidden skill has nothing to switch: the note says which skill wins.
                FrameworkElement state = skill.Hidden ? Pill("Hidden by " + skill.ShadowedBy, Tone.Neutral) : Switch(skill.Enabled, on =>
                {
                    try { CodeSkills.Switch(Store.Root, scope.Root, skill.Name, on); say(skill.Name + (on ? " is on." : " is off.") + " For this project only."); }
                    catch (Exception e) { fail(e); Render(); }
                }, "Use " + skill.Name + " in this project");
                host.Children.Add(SettingsCard(skill.Path == null ? "\uE7B8" : "\uE82D", skill.Name, (skill.Source == "project" ? ".mason/skills" : skill.Source) + " · " + skill.Summary, Row(view, open, folder, state)));
            }
        }
        async Task Create()
        {
            var name = new TextBox { Header = "Name", PlaceholderText = "release-notes", MaxLength = 48 };
            var description = new TextBox { Header = "Description", PlaceholderText = "Write release notes from the merged pull requests" };
            var content = Stack(Muted("Saved as .mason/skills/NAME/SKILL.md in the project, so it travels with the repository. The agent sees the name and description, and reads the rest when the work calls for it.", 14), name, description);
            if (await new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "New skill", Content = content, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary }.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                var file = CodeSkills.Create(scope.Root, name.Text.Trim(), description.Text.Trim());
                Render(); say("Created " + Path.GetRelativePath(scope.Root, file) + ".");
                OpenWithDefault(file);
            }
            catch (Exception e) { fail(e); }
        }
        Render();
        return (Row(IconButton("\uE710", "New skill…", async (_, _) => await Create(), true), Button("Refresh", (_, _) => Render())), host);
    }

    /// <summary>The tool catalogue, one expander per group with a switch per tool.</summary>
    StackPanel ToolCatalogSection(CodeWorkspace scope, Action<string> say, Action<Exception> fail)
    {
        var host = new StackPanel { Spacing = 4 };
        void Render()
        {
            host.Children.Clear();
            HashSet<string> off;
            try { off = new(CodeProjectPolicy.Load(Store.Root, scope.Root)?.DisabledTools ?? [], StringComparer.Ordinal); } catch (Exception e) { fail(e); return; }
            foreach (var group in CodeToolCatalog.Groups)
            {
                var tools = CodeToolCatalog.InGroup(group).ToArray();
                int offCount = tools.Count(t => !t.AlwaysOn && off.Contains(t.Name));
                var cards = new StackPanel { Spacing = 4 };
                foreach (var tool in tools)
                {
                    FrameworkElement control = tool.AlwaysOn ? Pill("Always on", Tone.Neutral) : Switch(!off.Contains(tool.Name), on =>
                    {
                        try { CodeProjectPolicy.SwitchTool(Store.Root, scope.Root, tool.Name, on); say(tool.Name + (on ? " is on" : " is off") + " for this project."); }
                        catch (Exception e) { fail(e); }
                        Render();
                    }, "Allow " + tool.Name);
                    var notes = (tool.Changes ? " · changes files or runs commands" : "") + (tool.Internet ? " · uses the internet" : "");
                    cards.Children.Add(SettingsCard(tool.Changes ? "\uE70F" : tool.Internet ? "\uE774" : "\uE8A5", tool.Name, tool.Line + notes, control));
                }
                var header = Row(new TextBlock { Text = char.ToUpperInvariant(group[0]) + group[1..], Style = Token("MasonBody"), VerticalAlignment = VerticalAlignment.Center },
                    Muted(tools.Length + " tool" + (tools.Length == 1 ? "" : "s") + (offCount > 0 ? " · " + offCount + " off" : ""), 12));
                var expander = new Expander { Header = header, Content = cards, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(expander, group + " tools");
                host.Children.Add(expander);
            }
        }
        Render();
        return host;
    }

    /// <summary>The Internet block: the computer's setting, the hosts always allowed for this project and for Chat.</summary>
    StackPanel InternetSection(CodeWorkspace scope, Action<string> say, Action<Exception> fail)
    {
        var host = new StackPanel { Spacing = 4 };
        void Render()
        {
            host.Children.Clear();
            var modes = new ComboBox { ItemsSource = CodeNetworkSettings.ModeLabels, SelectedIndex = Array.IndexOf(CodeNetworkSettings.Modes, CodeNetworkSettings.Computer(Store.Root)), MinWidth = 260 };
            AutomationProperties.SetName(modes, "Internet for new sessions and Chat");
            modes.SelectionChanged += (_, _) =>
            {
                if (modes.SelectedIndex < 0) return;
                var mode = CodeNetworkSettings.Modes[modes.SelectedIndex];
                try { CodeNetworkSettings.SetComputer(Store.Root, mode); say(CodeNetworkSettings.Describe(mode)); }
                catch (Exception e) { fail(e); Render(); }
            };
            host.Children.Add(SettingsCard("\uE774", "Internet for new sessions and Chat", "Sandboxed commands in Auto and web_fetch/web_search. Each session can change its own with /network.", modes));
            foreach (var allowed in CodeNetworkSettings.AlwaysAllowed(Store.Root, scope.Root))
                host.Children.Add(SettingsCard("\uE73E", allowed, "Always allowed in " + Path.GetFileName(scope.Root),
                    SubtleButton("Remove", (_, _) => { try { CodeNetworkSettings.Forget(Store.Root, scope.Root, allowed); Render(); say(allowed + " will ask again."); } catch (Exception e) { fail(e); } }, "\uE74D")));
            foreach (var allowed in CodeNetworkSettings.ChatHosts(Store.Root))
                host.Children.Add(SettingsCard("\uE8BD", allowed, "Always allowed in Chat",
                    SubtleButton("Remove", (_, _) => { try { CodeNetworkSettings.SetChatHost(Store.Root, allowed, false); Render(); say(allowed + " will ask again in Chat."); } catch (Exception e) { fail(e); } }, "\uE74D")));
            var notice = Muted(CodeNetworkSettings.Notice, 12); notice.Margin = new Thickness(2, 4, 0, 0); notice.TextWrapping = TextWrapping.Wrap;
            host.Children.Add(notice);
        }
        Render();
        return host;
    }
    /// <summary>One tool call in a chat answer: what it was for and how it ended, as a compact step.</summary>
    static FrameworkElement ChatStepRow(ChatToolStep step)
    {
        var mark = Glyph(step.Failed ? "\uE7BA" : "\uE73E", 12); mark.Opacity = .8;
        var label = new TextBlock { Text = step.Label, Style = Token("MasonCaptionSecondary"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var row = Row(mark, label); row.Spacing = 6;
        AutomationProperties.SetName(row, (step.Failed ? "Step failed: " : "Step: ") + step.Label);
        return row;
    }

    /// <summary>An inline card in the chat for a new host or an MCP call: Once, Always or No.</summary>
    Task<ChatApprovalAnswer> ChatApproval(Panel host, ChatApprovalRequest request, CancellationToken cancellation)
    {
        var answer = new TaskCompletionSource<ChatApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        FrameworkElement card = null!;
        void Answer(ChatApprovalAnswer value) { host.Children.Remove(card); answer.TrySetResult(value); }
        var once = IconButton("\uE73E", "Once", (_, _) => Answer(ChatApprovalAnswer.Once), true);
        var always = IconButton("\uE8FB", request.Kind == "host" ? "Always in Chat" : "Always", (_, _) => Answer(ChatApprovalAnswer.Always));
        var no = IconButton("\uE711", "No", (_, _) => Answer(ChatApprovalAnswer.No));
        AutomationProperties.SetName(once, "Allow once: " + request.Title); AutomationProperties.SetName(always, "Always allow: " + request.Title); AutomationProperties.SetName(no, "Decline: " + request.Title);
        var detail = CodeNote(request.Detail); detail.TextWrapping = TextWrapping.Wrap;
        var heading = Row(MascotImage(PoseApproval, 40, "waiting for your decision"), new TextBlock { Text = request.Title, Style = Token("MasonBodyStrong"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        card = ChatSurface(Stack(heading, detail, Row(once, always, no)), new Thickness(14, 12, 14, 12));
        host.Children.Add(card); FadeIn(card);
        cancellation.Register(() => DispatcherQueue.TryEnqueue(() => { host.Children.Remove(card); answer.TrySetCanceled(cancellation); }));
        return answer.Task;
    }
}
