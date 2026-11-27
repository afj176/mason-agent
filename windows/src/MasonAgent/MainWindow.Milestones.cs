using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    FrameworkElement BuildMilestoneViewer(CodeSession session, CodeWorkspace scope, CodeViewerEntry entry, Action<string, string> open)
    {
        var map = new CodeRepositoryMap(scope);
        var query = new TextBox { PlaceholderText = "Symbol, package or path" };
        AutomationProperties.SetName(query, "Repository map query");
        var matches = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 270 };
        AutomationProperties.SetName(matches, "Repository map matches");
        var milestones = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 190 };
        AutomationProperties.SetName(milestones, "Session milestones");
        var shown = Array.Empty<MilestoneStatus>();
        var message = CodeNote("");
        var details = CodeNote("");
        bool busy = false;
        Guid? CurrentId() => milestones.SelectedIndex is var i && i >= 0 && i < shown.Length ? shown[i].Item.Id : null;
        CodeMilestone? Current() => session.Milestones.FirstOrDefault(m => m.Id == CurrentId());
        string[] SelectedPaths() => matches.SelectedItems.OfType<MapRow>().Select(r => r.Hit.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        void Persist(CodeMilestone[] next)
        {
            var previous = session.Milestones;
            session.Milestones = next;
            try { codeStore!.Save(session); } catch { session.Milestones = previous; throw; }
        }
        void Render(Guid? preferred = null)
        {
            preferred ??= CurrentId();
            shown = CodeMilestones.Inspect(scope, session.Milestones).ToArray();
            milestones.ItemsSource = shown.Select(s => $"{s.State} · {s.Item.Title}").ToArray();
            int index = Array.FindIndex(shown, s => s.Item.Id == preferred);
            if (index < 0) index = Array.FindIndex(shown, s => s.State is "Ready" or "Needs recheck");
            milestones.SelectedIndex = index >= 0 ? index : shown.Length == 0 ? -1 : 0;
            Describe();
        }
        void Describe()
        {
            var i = milestones.SelectedIndex;
            details.Text = i < 0 || i >= shown.Length ? "" : string.Join("\n", new[] { shown[i].Reason, shown[i].Item.Decision,
                string.Join(", ", shown[i].Item.Files) }.Where(x => x.Length != 0));
        }
        async Task Guard(Func<Task> action, bool mutate = false)
        {
            if (busy) return;
            if (mutate && (codeOperation != null || session.State is "Running" or "Awaiting review"))
            { message.Text = "Finish the active turn before editing its plan."; return; }
            busy = true;
            try { await action(); }
            catch (OperationCanceledException) { message.Text = "Cancelled."; }
            catch (Exception e) { message.Text = e.Message; }
            finally { busy = false; }
        }
        async Task Search()
        {
            string terms = query.Text.Trim();
            if (terms.Length == 0) { message.Text = "Enter a symbol, package or path."; return; }
            message.Text = "Looking up workspace sources…";
            var ct = entry.Cancellation.Token;
            var result = await map.Query(terms,40,ct);
            ct.ThrowIfCancellationRequested();
            matches.ItemsSource = result.Matches.Select(h => new MapRow(h)).ToArray();
            message.Text = $"{result.Files} source files · {result.Reindexed} updated · {result.Matches.Count} matches" +
                (result.Partial ? " · partial inventory" : "");
        }
        async Task Edit(CodeMilestone? old)
        {
            var title = new TextBox { Header = "Milestone", Text = old?.Title ?? "", MaxLength = 240 };
            var decision = new TextBox { Header = "Decision or constraint", Text = old?.Decision ?? "", AcceptsReturn = true, MaxLength = 2000 };
            var paths = new TextBox { Header = "Workspace files, one per line", AcceptsReturn = true, MaxHeight = 120,
                Text = string.Join("\n", (old?.Files ?? []).Concat(SelectedPaths()).Distinct(StringComparer.OrdinalIgnoreCase)) };
            var workflow = new TextBox { Header = "Verification workflow (optional)", PlaceholderText = "Name from mason.workflows.json", Text = old?.Workflow ?? "" };
            var prereqs = new ListView { Header = "Prerequisites", SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 120 };
            var choices = session.Milestones.Where(m => m.Id != old?.Id).ToArray();
            foreach (var choice in choices) { var row = new ListViewItem { Content = choice.Title, Tag = choice.Id }; prereqs.Items.Add(row); if (old?.DependsOn.Contains(choice.Id) == true) prereqs.SelectedItems.Add(row); }
            var error = CodeNote("");
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = old == null ? "Add milestone" : "Edit milestone",
                Content = CodeScroll(Stack(title, decision, paths, workflow, prereqs, error)), PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    var next = new CodeMilestone(old?.Id ?? Guid.NewGuid(), title.Text, decision.Text,
                        paths.Text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        prereqs.SelectedItems.OfType<ListViewItem>().Select(x => (Guid)x.Tag).ToArray(), workflow.Text);
                    Persist(CodeMilestones.Put(scope, session.Milestones, next)); Render(next.Id);
                } catch (Exception e) { args.Cancel = true; error.Text = e.Message; }
            };
            await dialog.ShowAsync();
        }
        async Task Verify()
        {
            if (Current() is not {} item) { message.Text = "Select a milestone."; return; }
            string note = "";
            if (item.Workflow.Length == 0)
            {
                var observation = new TextBox { Header = "What did you verify?", AcceptsReturn = true, MaxLength = 2000 };
                var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Record human review",
                    Content = observation, PrimaryButtonText = "Record observation", CloseButtonText = "Cancel" };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                note = observation.Text;
            }
            var plan = session.Milestones;
            message.Text = item.Workflow.Length == 0 ? "Recording observation…" : $"Running {item.Workflow}…";
            bool auto = session.Mode == "build" && session.Permission == CodePermission.Auto;
            if (item.Workflow.Length != 0 && auto && item.Receipt != null)
            {
                plan = plan.Select(m => m.Id == item.Id ? m with { Receipt = null } : m).ToArray();
                Persist(plan); Render(item.Id);
            }
            if(item.Workflow.Length>0)CodeProjectPolicy.Load(Store.Root,scope.Root)?.Check("run_workflow",item.Workflow);
            var receipt = await CodeMilestones.Check(scope, item, plan, auto, note, entry.Cancellation.Token);
            if (session.Milestones != plan) throw new InvalidOperationException("The plan changed. Repeat verification.");
            Persist(plan.Select(m => m.Id == item.Id ? m with { Receipt = receipt } : m).ToArray());
            Render(item.Id); message.Text = receipt.Manual ? "Human review recorded." : "Workflow verified.";
        }
        milestones.SelectionChanged += (_, _) => Describe();
        var search = Button("Search map", async (_, _) => await Guard(Search));
        query.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await Guard(Search); } };
        var inspect = CodeLink("Open selected source", (_, _) => { if (matches.SelectedItem is MapRow row) open("file", row.Hit.Path); });
        var add = Button("Add milestone", async (_, _) => await Guard(() => Edit(null), true));
        var edit = Button("Edit", async (_, _) => { if (Current() is {} item) await Guard(() => Edit(item), true); });
        var verify = Button("Verify", async (_, _) => await Guard(Verify, true), true);
        var remove = CodeLink("Remove", async (_, _) => await Guard(async () =>
        {
            if (Current() is not {} item) return;
            if (session.Milestones.Any(m => m.DependsOn.Contains(item.Id))) throw new InvalidOperationException("Remove this prerequisite from dependent milestones first.");
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Remove milestone?", Content = item.Title, PrimaryButtonText = "Remove", CloseButtonText = "Cancel" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { Persist(session.Milestones.Where(m => m.Id != item.Id).ToArray()); Render(); }
        }, true));
        var refresh = CodeLink("Recheck inputs", (_, _) => { try { Render(); } catch (Exception e) { message.Text = e.Message; } });
        var body = Stack(Heading("Session plan"), milestones, details, Row(add, edit, verify, remove), refresh,
            Heading("Repository map", 19), query, Row(search, inspect), matches, message);
        body.Padding = new Thickness(18);
        Render();
        return CodeScroll(body);
    }

    sealed record MapRow(MapHit Hit)
    {
        public override string ToString() => $"{Hit.Path}:{Hit.Line} · {Hit.Kind}\n{Hit.Detail}";
    }
}
