using System.IO;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Mason;

// The Live Viewer: the Activity tab shown as scenes. Consecutive calls of one kind form a scene, and each kind has its own
// lens: a diff for edits, a summary-first terminal for commands, a map for reads, the page for browser checks, the plan,
// and a receipt when the run ends. The viewer follows the newest scene until the user picks another one.
// Every fact the old list showed (authorization, arguments, results, skipped repeats) is still under Step details or Raw.
public sealed partial class MainWindow
{
    const string PlanScene = "plan", EndScene = "end";

    void BuildLiveViewer(CodeSession session, CodeTab model, CodeViewerEntry entry, ContentControl host, Action save, Action<string, string> open)
    {
        string revision = "", shownScene = "";
        bool themeHooked = false;
        entry.Refresh = () =>
        {
            var activities = session.Activities;
            bool running = session.State is "Running" or "Awaiting review";
            var next = string.Join('|', activities.Select(a => a.Id.ToString("N")[..8] + a.State + a.Result.Length)) + "#" + session.State + session.Ending?.Code +
                model.Location + model.ViewMode + session.Milestones.Length + "#" + string.Join(',', session.Changes.Select(c => c.AfterHash.Length > 6 ? c.AfterHash[..6] : c.AfterHash + c.Restored));
            if (revision == next && host.Content != null) return;
            revision = next;
            if (!themeHooked) { themeHooked = true; host.ActualThemeChanged += (_, _) => { revision = ""; entry.Refresh?.Invoke(); }; }
            bool dark = host.ActualTheme == ElementTheme.Dark;

            var scenes = CodeScenes.Group(activities);
            bool hasPlan = session.Milestones.Length > 0;
            bool hasEnd = !running && session.Ending != null && activities.Count > 0;
            string Key(CodeScene scene) => "s" + scene.First;
            string Live() => running ? (scenes.Count > 0 ? Key(scenes[^1]) : hasPlan ? PlanScene : "") : hasEnd ? EndScene : scenes.Count > 0 ? Key(scenes[^1]) : hasPlan ? PlanScene : "";
            bool following = string.IsNullOrEmpty(model.Location);
            string selected = Live();
            if (!following)
            {
                if (model.Location == EndScene && hasEnd) selected = EndScene;
                else if (model.Location == PlanScene && hasPlan) selected = PlanScene;
                else if (Guid.TryParse(model.Location, out var id) && activities.FindIndex(a => a.Id == id) is >= 0 and var at) selected = Key(scenes.First(s => s.Contains(at)));
                else { model.Location = null; following = true; }
            }
            if (selected == Live()) following = true;
            var scene = scenes.FirstOrDefault(s => Key(s) == selected);
            // Before the first step there is nothing to follow, pick or show raw: the panel explains what will appear instead.
            if (selected == "") { host.Content = LiveViewerEmpty(running, dark); shownScene = ""; return; }

            void Choose(string key)
            {
                model.Location = key == Live() ? null : key is PlanScene or EndScene ? key : scenes.FirstOrDefault(s => Key(s) == key) is { } chosen ? activities[chosen.First].Id.ToString() : null;
                revision = ""; save(); entry.Refresh?.Invoke();
            }

            // Header: following state, where we are, raw switch, and the pages that used to live in the project menu.
            var where = Text(selected switch
            {
                PlanScene => "Plan",
                EndScene => CodeRunEnding.Look(session.Ending?.Code)?.Headline ?? "Run ended",
                "" => "Live",
                _ => CodeScenes.Label(scene!, activities) + (scene!.Count > 1 ? $" · steps {scene.First + 1}–{scene.Last + 1} of {activities.Count}" : $" · step {scene.First + 1} of {activities.Count}")
            }, 14);
            where.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; where.MaxLines = 1; where.TextTrimming = TextTrimming.CharacterEllipsis; where.VerticalAlignment = VerticalAlignment.Center;
            FrameworkElement state = following
                ? Pill(running ? "● Following" : selected == EndScene ? "Run ended" : "Latest", running ? Tone.Accent : Tone.Neutral)
                : Button("Jump to latest", (_, _) => Choose(Live()));
            state.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(state, following ? (running ? "Following the run as it happens" : "Showing the latest step") : "Jump to the latest step and follow the run");
            var raw = ChipToggle(new ToggleButton { Content = "Raw", IsChecked = model.ViewMode == "raw", Padding = new Thickness(10, 4, 10, 4) });
            AutomationProperties.SetName(raw, "Show raw tool arguments and results"); ToolTipService.SetToolTip(raw, "Show the exact arguments and results the model sent and received");
            raw.Click += (_, _) => { model.ViewMode = raw.IsChecked == true ? "raw" : ""; revision = ""; save(); entry.Refresh?.Invoke(); };
            var more = ChatIcon("More run pages", "", (_, _) => { });
            var pages = Fluent.Acrylic(new MenuFlyout());
            foreach (var (label, kind) in new[] { ("Plan & repository map", "plan"), ("Model roles & decisions", "roles"), ("Background tasks", "tasks"), ("Context checkpoints", "context") })
            { var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => open(kind, ""); pages.Items.Add(item); }
            more.Flyout = pages;
            var header = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 10, 10, 10) };
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(state); Grid.SetColumn(where, 1); header.Children.Add(where); Grid.SetColumn(raw, 2); header.Children.Add(raw); Grid.SetColumn(more, 3); header.Children.Add(more);

            // Lens
            var body = new StackPanel { Spacing = 14, Margin = new Thickness(16, 4, 16, 16) };
            try
            {
                if (selected == PlanScene) PlanLens(session, body, open);
                else if (selected == EndScene) ReceiptLens(session, body, open);
                else if (model.ViewMode == "raw") foreach (int i in Indices(scene!)) body.Children.Add(ActivityCard(session, i, true));
                else
                {
                    if (Why(session, scene!) is { Length: > 0 } why) body.Children.Add(WhyLine(why));
                    switch (scene!.Kind)
                    {
                        case CodeSceneKind.Edit: EditLens(session, scene, body, open, dark); break;
                        case CodeSceneKind.Command: CommandLens(session, scene, body); break;
                        case CodeSceneKind.Read: ReadLens(session, scene, body); break;
                        case CodeSceneKind.Page: PageLens(session, scene, body, open); break;
                        default: foreach (int i in Indices(scene)) body.Children.Add(ActivityCard(session, i, false)); break;
                    }
                    if (scene.Kind != CodeSceneKind.Other)
                    {
                        var details = Stack(Indices(scene).Select(i => (UIElement)ActivityCard(session, i, false)).ToArray());
                        body.Children.Add(new Expander { Header = scene.Count == 1 ? "Step details" : $"Step details ({scene.Count})", Content = details, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
            { body.Children.Add(TroubleNotice("This step can't be shown", error.Message)); }
            var lens = CodeScroll(body);
            double restore = selected == shownScene ? model.Scroll : 0;
            lens.Loaded += (_, _) => lens.ChangeView(null, restore, null, true);
            lens.ViewChanged += (_, _) => model.Scroll = lens.VerticalOffset;
            shownScene = selected;

            // Scene strip: the shape of the run, tinted by kind and outcome.
            var ticks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            Button? current = null;
            Button Tick(string key, string text, Tone tone, string tip)
            {
                var tick = new Button
                {
                    Content = new TextBlock { Text = text, FontSize = 11, FontFamily = new FontFamily("Cascadia Mono, Consolas") },
                    Padding = new Thickness(7, 2, 7, 2), MinHeight = 24, MinWidth = 28, CornerRadius = new CornerRadius(4),
                    Background = ModelTones.FillBrush(tone, dark), Foreground = ModelTones.InkBrush(tone, dark),
                    BorderThickness = new Thickness(key == selected ? 2 : 1)
                };
                if (key == selected) { tick.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]; current = tick; }
                ToolTipService.SetToolTip(tick, tip); AutomationProperties.SetName(tick, tip + (key == selected ? ", shown" : ""));
                tick.Click += (_, _) => Choose(key);
                return tick;
            }
            if (hasPlan) ticks.Children.Add(Tick(PlanScene, "plan", Tone.Neutral, "Plan · " + session.Milestones.Length + " steps"));
            const int shownTicks = 150;
            if (scenes.Count > shownTicks) ticks.Children.Add(Muted($"{scenes.Count - shownTicks} earlier", 11));
            foreach (var item in scenes.Skip(Math.Max(0, scenes.Count - shownTicks)))
            {
                var calls = Indices(item).Select(i => activities[i]).ToArray();
                bool failed = calls.Any(CodeScenes.Failed) || item.Kind == CodeSceneKind.Command && calls.Any(a => CodeScenes.Command(a).ExitCode is not (null or 0));
                bool skipped = calls.Any(a => a.State == "Skipped");
                var (text, tone) = item.Kind switch
                {
                    CodeSceneKind.Read => (item.Count > 1 ? "read×" + item.Count : "read", Tone.Neutral),
                    CodeSceneKind.Edit => ("edit", Tone.Accent),
                    CodeSceneKind.Command => ("run", Tone.Good),
                    CodeSceneKind.Page => ("page", Tone.Search),
                    _ => ("step", Tone.Neutral)
                };
                if (failed) tone = Tone.Bad; else if (skipped) tone = Tone.Warn;
                ticks.Children.Add(Tick(Key(item), text, tone, CodeScenes.Label(item, activities) + (failed ? " · failed" : skipped ? " · repeat skipped" : "")));
            }
            if (hasEnd && session.Ending is { } ending) ticks.Children.Add(Tick(EndScene, "end", CodeRunEnding.Look(ending.Code) is { } look ? EndingTone(look.Tone) : Tone.Neutral, CodeRunEnding.Look(ending.Code)?.Headline ?? "Run ended"));
            var strip = new ScrollViewer { Content = ticks, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled, Padding = new Thickness(14, 8, 14, 10) };
            AutomationProperties.SetName(strip, "Steps of this run");
            strip.Loaded += (_, _) => current?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });

            var layout = new Grid();
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            layout.Children.Add(header);
            Grid.SetRow(lens, 1); layout.Children.Add(lens);
            if (ticks.Children.Count > 0) { Grid.SetRow(strip, 2); layout.Children.Add(strip); }
            host.Content = layout;
        };
        entry.Refresh();
    }

    /// <summary>The Live Viewer before any step: a pose, what the panel will show, and a key to the step strip's colours.</summary>
    static FrameworkElement LiveViewerEmpty(bool running, bool dark)
    {
        var page = new StackPanel { Spacing = 14, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        var pose = MascotImage(running ? PosePlanning : PoseReading, 132, running ? "planning the first step" : "waiting to read the project");
        pose.HorizontalAlignment = HorizontalAlignment.Center;
        page.Children.Add(pose);
        var title = Heading(running ? "Working out the first step" : "Mason's work shows up here", 20); title.HorizontalAlignment = HorizontalAlignment.Center; title.TextAlignment = TextAlignment.Center; title.TextWrapping = TextWrapping.Wrap;
        var lead = Muted(running ? "The first file Mason reads, edit it makes or command it runs appears here as it happens."
            : "Each file Mason reads, edit it makes and check it runs appears here as it happens. When the run ends you get a summary of what changed.", 13);
        lead.TextAlignment = TextAlignment.Center; lead.TextWrapping = TextWrapping.Wrap; lead.HorizontalAlignment = HorizontalAlignment.Center;
        page.Children.Add(title); page.Children.Add(lead);
        // A key to the step strip: the same tick shapes and colours the run will use, with what each one opens.
        var key = new Grid { ColumnSpacing = 12, RowSpacing = 8, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        key.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); key.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var rows = new (string Tick, Tone Tone, string Meaning)[]
        {
            ("read", Tone.Neutral, "Files read and searches, and where in the project"),
            ("edit", Tone.Accent, "The change to each file, line by line"),
            ("run", Tone.Good, "Commands and tests, failures first"),
            ("page", Tone.Search, "Pages and windows Mason checked"),
            ("end", Tone.Neutral, "How the run ended and what to do next"),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            key.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var tick = new Border { Child = new TextBlock { Text = rows[i].Tick, FontSize = 11, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Foreground = ModelTones.InkBrush(rows[i].Tone, dark) },
                Background = ModelTones.FillBrush(rows[i].Tone, dark), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
            ((TextBlock)tick.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var meaning = Muted(rows[i].Meaning, 12); meaning.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(tick, i); Grid.SetRow(meaning, i); Grid.SetColumn(meaning, 1);
            key.Children.Add(tick); key.Children.Add(meaning);
        }
        AutomationProperties.SetName(key, "What the step strip will show");
        page.Children.Add(key);
        var scroll = CodeScroll(page); scroll.VerticalContentAlignment = VerticalAlignment.Center;
        return scroll;
    }

    static IEnumerable<int> Indices(CodeScene scene) => Enumerable.Range(scene.First, scene.Count);

    /// <summary>Mason's own words for why it started this scene: the update it sent with the scene's first call.</summary>
    static string Why(CodeSession session, CodeScene scene)
    {
        var text = session.Turns.FirstOrDefault(t => t.Role == "assistant" && t.ActivityCount > 0 && scene.Contains(t.FirstActivity))?.Text.Trim() ?? "";
        return text.Length > 360 ? text[..359] + "…" : text;
    }

    static FrameworkElement WhyLine(string text)
    {
        var bar = ThemedBorder("Background='{ThemeResource AccentFillColorDefaultBrush}' Width='3' CornerRadius='2'");
        var words = Muted(text, 13); words.TextWrapping = TextWrapping.Wrap;
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(bar); Grid.SetColumn(words, 1); grid.Children.Add(words);
        return grid;
    }

    /// <summary>One tool call as the old Activity list showed it: what ran, how it was authorised, and what came back.</summary>
    static FrameworkElement ActivityCard(CodeSession session, int index, bool raw)
    {
        var activity = session.Activities[index];
        var result = raw ? activity.Result : CodeActivityText.Result(activity);
        var value = result.Length > 12000 ? result[..12000] + "\n[Viewer clipped to 12,000 characters; full result remains in the session.]" : result;
        var toolName = Text(activity.Tool.Replace('_', ' '), 15); toolName.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var card = Stack(Toolbar(toolName, activity.State == "Skipped" ? Pill("↻ Skipped · repeated", Tone.Warn) : Badge(activity.State)), CodeNote(activity.Authorization));
        card.Spacing = 8;
        card.Children.Add(ChatInline(raw ? activity.Arguments : CodeActivityText.Command(activity), 12, true));
        if (value.Length > 0) card.Children.Add(ChatInline(value, 13, true));
        return ChatSurface(card, new Thickness(14, 10, 14, 12));
    }

    static Tone StateTone(string state) => state switch { "Completed" => Tone.Good, "Running" or "Pending" => Tone.Accent, "Skipped" => Tone.Warn, "Failed" or "Denied" or "Outcome unknown" => Tone.Bad, _ => Tone.Neutral };
    static string StateWords(string state) => state switch { "Denied" => "Declined", "Skipped" => "Repeat skipped", _ => state };

    static StackPanel PillRow(params FrameworkElement?[] pills)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var pill in pills) if (pill != null) row.Children.Add(pill);
        return row;
    }

    static (int Added, int Removed) LineCounts(CodeChange change)
    {
        int added = 0, removed = 0;
        foreach (var line in CodeProtocol.Diff(change.Before, change.After)) { if (line.Kind == '+') added++; else if (line.Kind == '-') removed++; }
        return (added, removed);
    }

    static FrameworkElement DiffBlock(CodeChange change, bool dark, int limit = 160)
    {
        var rows = new StackPanel();
        int count = 0;
        bool contrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        foreach (var line in CodeProtocol.Diff(change.Before, change.After))
        {
            if (++count > limit) { rows.Children.Add(Muted("More lines below · open the file or Review to see all of it", 12)); break; }
            if (line.Kind == '@') { var gap = Muted("⋯ " + line.Text, 12); gap.Margin = new Thickness(8, 3, 8, 3); rows.Children.Add(gap); continue; }
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 1, 10, 1) };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(40) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var number = new TextBlock { Text = (line.After ?? line.Before)?.ToString() ?? "", FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Opacity = .6, HorizontalTextAlignment = TextAlignment.Right };
            var text = new TextBlock { Text = (line.Kind == ' ' ? "  " : line.Kind + " ") + line.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, IsTextSelectionEnabled = true };
            grid.Children.Add(number); Grid.SetColumn(text, 1); grid.Children.Add(text);
            if (line.Kind != ' ' && !contrast) grid.Background = ModelTones.FillBrush(line.Kind == '+' ? Tone.Good : Tone.Bad, dark);
            AutomationProperties.SetName(grid, (line.Kind == '+' ? "Added line " : line.Kind == '-' ? "Removed line " : "Line ") + (line.After ?? line.Before) + ": " + line.Text);
            rows.Children.Add(grid);
        }
        var scroll = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled };
        var frame = ThemedBorder("BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' CornerRadius='6' Padding='0,4,0,4'");
        frame.Child = scroll;
        return frame;
    }

    void EditLens(CodeSession session, CodeScene scene, StackPanel body, Action<string, string> open, bool dark)
    {
        var calls = Indices(scene).Select(i => session.Activities[i]).ToArray();
        var paths = calls.Select(CodeScenes.PathOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in paths)
        {
            var mine = calls.Where(a => string.Equals(CodeScenes.PathOf(a), path, StringComparison.OrdinalIgnoreCase)).ToArray();
            var change = session.Changes.LastOrDefault(c => !c.Restored && string.Equals(CodeScenes.Normalize(c.Path), path, StringComparison.OrdinalIgnoreCase));
            var name = Text(path, 14); name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.TextTrimming = TextTrimming.CharacterEllipsis;
            FrameworkElement? counts = null, extra = null;
            if (change != null)
            {
                var (added, removed) = LineCounts(change);
                counts = PillRow(change.Before == null ? Badge("new file") : null, Pill("+" + added, Tone.Good), removed > 0 ? Pill("−" + removed, Tone.Bad) : null);
            }
            if (mine.LastOrDefault(CodeScenes.Failed) is { } failure) extra = Pill(StateWords(failure.State), StateTone(failure.State));
            else if (mine.Any(a => a.State is "Running" or "Pending")) extra = Pill("Writing…", Tone.Accent);
            var target = change?.Path ?? path;
            var actions = Row(CodeLink("Open file", (_, _) => open("file", target)));
            body.Children.Add(Toolbar(Row(name, PillRow(counts, extra)), actions));
            if (change != null)
            {
                body.Children.Add(DiffBlock(change, dark));
                body.Children.Add(Muted(change.Before == null ? "The whole file, as this session wrote it." : "Everything this session has changed in the file so far, against its original.", 12));
            }
            else if (mine.LastOrDefault() is { } last && CodeActivityText.Result(last) is { Length: > 0 } result)
                body.Children.Add(CodeNote(result.Length > 800 ? result[..800] + "…" : result));
        }
        foreach (var other in calls.Where(a => CodeScenes.PathOf(a) == null)) body.Children.Add(ActivityCard(session, session.Activities.IndexOf(other), false));
        if (session.Changes.Any(c => !c.Restored)) body.Children.Add(Row(CodeLink("Review all changes", (_, _) => open("changes", ""))));
    }

    static void CommandLens(CodeSession session, CodeScene scene, StackPanel body)
    {
        var indices = Indices(scene).ToArray();
        const int shown = 6;
        if (indices.Length > shown) body.Children.Add(Muted($"{indices.Length - shown} earlier commands are under Step details.", 12));
        foreach (int i in indices.Skip(Math.Max(0, indices.Length - shown)))
        {
            var call = session.Activities[i];
            var summary = CodeScenes.Command(call);
            var command = CodeActivityText.Command(call);
            var line = CodeBadge(command.Length > 160 ? command[..159] + "…" : command);
            var pills = PillRow(
                summary.ExitCode is { } exit ? Pill("exit " + exit, exit == 0 ? Tone.Good : Tone.Bad) : Pill(StateWords(call.State), StateTone(call.State)),
                summary.Passed is { } passed ? Pill(passed + " passed", Tone.Good) : null,
                summary.Failed is { } failedCount && failedCount > 0 ? Pill(failedCount + " failed", Tone.Bad) : null,
                summary.Truncated ? Pill("output clipped", Tone.Warn) : null,
                call.State == "Skipped" ? Pill("Repeat skipped", Tone.Warn) : null);
            var card = Stack(line, pills);
            card.Spacing = 8;
            if (call.Authorization.Length > 0) card.Children.Add(Muted(call.Authorization, 12));
            foreach (var failure in summary.Failures)
            {
                var bar = ThemedBorder("Background='{ThemeResource SystemFillColorCriticalBrush}' Width='3' CornerRadius='2'");
                var words = CodeSource(failure, 12); words.TextWrapping = TextWrapping.Wrap;
                var grid = new Grid { ColumnSpacing = 10 };
                grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                grid.Children.Add(bar); Grid.SetColumn(words, 1); grid.Children.Add(words);
                card.Children.Add(grid);
            }
            var output = summary.Output.TrimEnd();
            if (output.Length > 0)
            {
                int lines = output.Count(c => c == '\n') + 1;
                var tail = output.Length > 8000 ? "[earlier output omitted · full output is under Raw]\n" + output[^8000..] : output;
                var text = CodeSource(tail, 12); text.TextWrapping = TextWrapping.NoWrap;
                var scroll = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 };
                bool open = summary.Failures.Length == 0 && (summary.ExitCode is not (null or 0) || call.State is "Running" or "Pending");
                card.Children.Add(new Expander { Header = lines == 1 ? "Output · 1 line" : $"Output · {lines} lines", Content = scroll, IsExpanded = open, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            body.Children.Add(ChatSurface(card, new Thickness(14, 12, 14, 12)));
        }
    }

    static void ReadLens(CodeSession session, CodeScene scene, StackPanel body)
    {
        var calls = Indices(scene).Select(i => session.Activities[i]).ToArray();
        bool Search(CodeActivity a) => a.Tool is "search_text" or "search_next" or "semantic_search" or "list_files";
        int files = calls.Where(a => !Search(a)).Select(CodeScenes.PathOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int searches = calls.Count(Search);
        body.Children.Add(PillRow(files > 0 ? Badge(files == 1 ? "1 file" : files + " files") : null, searches > 0 ? Badge(searches == 1 ? "1 search" : searches + " searches") : null,
            calls.Count(CodeScenes.Failed) is > 0 and var failed ? Pill(failed + " failed", Tone.Bad) : null));
        var folders = CodeScenes.Folders(calls);
        if (folders.Length > 1)
        {
            body.Children.Add(Text("Where Mason looked", 14));
            var grid = new Grid { ColumnSpacing = 10, RowSpacing = 4 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(160) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            int max = folders.Max(f => f.Count), row = 0;
            foreach (var (folder, count) in folders.Take(8))
            {
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var name = CodeSource(folder, 12); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
                var bar = ThemedBorder("Background='{ThemeResource AccentFillColorDefaultBrush}' Height='8' CornerRadius='4' HorizontalAlignment='Left' VerticalAlignment='Center'");
                bar.Width = Math.Max(8, 220.0 * count / max);
                var number = Muted(count.ToString(), 12);
                Grid.SetRow(name, row); Grid.SetRow(bar, row); Grid.SetColumn(bar, 1); Grid.SetRow(number, row); Grid.SetColumn(number, 2);
                grid.Children.Add(name); grid.Children.Add(bar); grid.Children.Add(number);
                AutomationProperties.SetName(bar, $"{folder}: {count}");
                row++;
            }
            body.Children.Add(grid);
        }
        var list = new StackPanel { Spacing = 6 };
        foreach (var call in calls)
        {
            string subject = CodeScenes.Argument(call, "query") ?? CodeScenes.Argument(call, "pattern") ?? CodeScenes.Argument(call, "symbol") ?? CodeActivityText.Command(call);
            var verb = Muted(call.Tool.Replace('_', ' '), 12); verb.VerticalAlignment = VerticalAlignment.Center;
            var what = CodeSource(subject.Length > 140 ? subject[..139] + "…" : subject, 12); what.TextWrapping = TextWrapping.Wrap;
            var line = new Grid { ColumnSpacing = 10 };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(verb); Grid.SetColumn(what, 1); line.Children.Add(what);
            if (call.State != "Completed") { var pill = Pill(StateWords(call.State), StateTone(call.State)); Grid.SetColumn(pill, 2); line.Children.Add(pill); }
            list.Children.Add(line);
            if (Search(call) && call.State == "Completed")
            {
                var hits = CodeActivityText.Result(call).Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
                if (hits.Length > 0)
                {
                    var preview = CodeSource(string.Join('\n', hits.Take(5)) + (hits.Length > 5 ? $"\n… {hits.Length - 5} more" : ""), 11); preview.Opacity = .8; preview.Margin = new Thickness(120, 0, 0, 4);
                    preview.TextWrapping = TextWrapping.NoWrap;
                    list.Children.Add(preview);
                }
            }
        }
        body.Children.Add(list);
    }

    static void PageLens(CodeSession session, CodeScene scene, StackPanel body, Action<string, string> open)
    {
        foreach (int i in Indices(scene))
        {
            var call = session.Activities[i];
            var url = CodeScenes.Argument(call, "url");
            var title = Text(call.Tool.Replace('_', ' '), 14); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var card = Stack(Toolbar(Row(title, Pill(StateWords(call.State), StateTone(call.State))), url is { Length: > 0 } ? CodeLink("Open page", (_, _) => { try { open("web", CodeProtocol.PreviewUri(url).AbsoluteUri); } catch (ArgumentException) { } }) : new Border()));
            card.Spacing = 8;
            var subject = url ?? CodeActivityText.Command(call);
            if (subject.Length > 0) card.Children.Add(CodeBadge(subject.Length > 160 ? subject[..159] + "…" : subject));
            var result = CodeActivityText.Result(call).Trim();
            if (result.Length > 0) { var text = CodeSource(result.Length > 2400 ? result[..2400] + "…" : result, 12); text.TextWrapping = TextWrapping.Wrap; card.Children.Add(text); }
            body.Children.Add(ChatSurface(card, new Thickness(14, 12, 14, 12)));
        }
    }

    static void PlanLens(CodeSession session, StackPanel body, Action<string, string> open)
    {
        body.Children.Add(Text("Plan", 19));
        var statuses = CodeMilestones.Inspect(new CodeWorkspace(session.Workspace), session.Milestones).ToArray();
        foreach (var status in statuses)
        {
            bool done = status.State.Equals("Done", StringComparison.OrdinalIgnoreCase) || status.State.Equals("Complete", StringComparison.OrdinalIgnoreCase) || status.State.Equals("Completed", StringComparison.OrdinalIgnoreCase);
            var mark = new FontIcon { Glyph = done ? "" : "", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            var words = new StackPanel { Spacing = 2 };
            var title = Text(status.Item.Title, 14); title.TextWrapping = TextWrapping.Wrap; words.Children.Add(title);
            if (status.Reason.Length > 0) { var reason = Muted(status.Reason, 12); reason.TextWrapping = TextWrapping.Wrap; words.Children.Add(reason); }
            var pill = Badge(status.State); pill.VerticalAlignment = VerticalAlignment.Top;
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.Children.Add(mark); Grid.SetColumn(words, 1); grid.Children.Add(words); Grid.SetColumn(pill, 2); grid.Children.Add(pill);
            body.Children.Add(ChatSurface(grid, new Thickness(12, 10, 12, 10)));
        }
        body.Children.Add(Row(CodeLink("Open plan & repository map", (_, _) => open("plan", "")), CodeLink("Model roles & decisions", (_, _) => open("roles", ""))));
    }

    static void ReceiptLens(CodeSession session, StackPanel body, Action<string, string> open)
    {
        var ending = session.Ending!;
        var look = CodeRunEnding.Look(ending.Code);
        int start = CodeScenes.RequestStart(session);
        var calls = session.Activities.Skip(start).ToArray();
        var request = session.Turns.LastOrDefault(t => t.Role == "user")?.Text.Trim() ?? session.Title;
        if (request.Length > 120) request = request[..119] + "…";
        var span = calls.Length > 1 ? calls[^1].At - calls[0].At : TimeSpan.Zero;
        string duration = span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s" : $"{Math.Max(1, (int)span.TotalSeconds)} s";

        var title = Text(request, 18); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(Stack(PillRow(EndingPill(ending), Badge(duration), Badge(calls.Length == 1 ? "1 step" : calls.Length + " steps")), title));

        var changes = session.Changes.Where(c => !c.Restored).ToArray();
        var counts = changes.Take(40).Select(LineCounts).ToArray();
        int commands = calls.Count(a => CodeScenes.KindOf(a.Tool) == CodeSceneKind.Command);
        int failedCommands = calls.Count(a => CodeScenes.KindOf(a.Tool) == CodeSceneKind.Command && (CodeScenes.Failed(a) || CodeScenes.Command(a).ExitCode is not (null or 0)));
        var stats = new Grid { ColumnSpacing = 1 };
        var tiles = new (string Value, string Label)[]
        {
            (changes.Length.ToString(), changes.Length == 1 ? "file changed" : "files changed"),
            ("+" + counts.Sum(c => c.Added) + " −" + counts.Sum(c => c.Removed), "lines"),
            (commands.ToString(), commands == 1 ? "command run" : "commands run"),
            (failedCommands.ToString(), failedCommands == 1 ? "command failed" : "commands failed"),
        };
        for (int i = 0; i < tiles.Length; i++)
        {
            stats.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var value = Heading(tiles[i].Value, 22); var label = Muted(tiles[i].Label, 12);
            var tile = new StackPanel { Spacing = 0, Padding = new Thickness(12, 8, 12, 8) }; tile.Children.Add(value); tile.Children.Add(label);
            Grid.SetColumn(tile, i); stats.Children.Add(tile);
        }
        body.Children.Add(ChatSurface(stats, new Thickness(4)));

        if (changes.Length > 0)
        {
            var list = new StackPanel { Spacing = 6 };
            for (int i = 0; i < changes.Length && i < 40; i++)
            {
                var path = changes[i].Path;
                var name = CodeSource(path, 12); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Center;
                list.Children.Add(Toolbar(Row(name, PillRow(changes[i].Before == null ? Badge("new") : null, Pill("+" + counts[i].Added, Tone.Good), counts[i].Removed > 0 ? Pill("−" + counts[i].Removed, Tone.Bad) : null)),
                    CodeLink("Open", (_, _) => open("file", path))));
            }
            if (changes.Length > 40) list.Children.Add(Muted($"{changes.Length - 40} more in Review", 12));
            body.Children.Add(list);
        }

        var notes = new List<string>();
        if (ending.Code == CodeRunEnding.ModelLoop && session.RepeatStop is { } repeat) notes.Add(repeat.Explanation(ending.Kept > 0));
        else if (CodeRunEnding.Explains(ending.Code) && session.Detail.Length > 0) notes.Add(session.Detail);
        if (CodeRunEnding.Hint(ending.Code, terminal: false) is { Length: > 0 } hint) notes.Add(hint);
        if (calls.Count(a => a.State == "Skipped") is > 0 and var skipped && ending.Code != CodeRunEnding.ModelLoop) notes.Add(skipped == 1 ? "Mason skipped one repeated call." : $"Mason skipped {skipped} repeated calls.");
        if (calls.Any(a => a.State == "Outcome unknown")) notes.Add("Some steps were interrupted and their outcome is unknown. Check Step details before continuing.");
        foreach (var note in notes) { var words = Muted(note, 13); words.TextWrapping = TextWrapping.Wrap; body.Children.Add(words); }

        if (changes.Length > 0)
        {
            var review = Button("Review all changes", (_, _) => open("changes", ""));
            var ship = Button(session.Delivery?.Url != null ? "Pull request & CI" : "Commit & open pull request…", (_, _) => open("delivery", ""), true);
            body.Children.Add(Row(review, ship));
        }
    }
}
