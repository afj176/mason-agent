using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mason;

// Notes: short facts about the project that Mason reads with every request. A tab with a count sits on the request
// box's top edge; the tray rises from behind the request box over the end of the conversation. The notes are kept by
// CodeNotes in the data folder, so the tray, the mason command and every session of the project share them.
public sealed partial class MainWindow
{
    /// <summary>Where the open project's notes live: the data folder, the project, and notes from the old project list.</summary>
    sealed record NotesPlace(string Home, CodeWorkspace Scope, IReadOnlyList<ProjectNote> Legacy);

    static Border ThemedBorder(string attributes) => (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " + attributes + "/>");
    static Button ThemedButton(string attributes) => (Button)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<Button xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " + attributes + "/>");

    /// <summary>The views a session can show. Notebook was retired; Notes took its place.</summary>
    static readonly CodeWorkspaceView[] CodeViews = [CodeWorkspaceView.Workbench, CodeWorkspaceView.Review];
    NotesTray notesTray = null!;

    static string NoteAge(DateTimeOffset saved)
    {
        var days = (DateTimeOffset.Now.Date - saved.ToLocalTime().Date).Days;
        return days <= 0 ? "today" : days == 1 ? "yesterday" : days < 7 ? $"{days} days ago" : saved.ToLocalTime().ToString("d MMM");
    }

    sealed class NotesTray
    {
        readonly Func<NotesPlace?> place;
        readonly Func<bool> running;
        readonly Func<string?> currentFile;
        readonly StackPanel content = new() { Spacing = 9 };
        readonly TextBlock count = new() { Style = Token("MasonCaptionStrong"), VerticalAlignment = VerticalAlignment.Center };
        readonly Border countBadge;
        TextBox? input;
        string about = "";
        Guid? editing;
        bool confirmClear;
        (string Message, ProjectNote[] Notes)? undo;
        string error = "";
        readonly DispatcherTimer undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };

        public Button Tab { get; }
        public Border Tray { get; }
        public bool IsOpen => Tray.Visibility == Visibility.Visible;
        public event Action? Changed;

        public NotesTray(Func<NotesPlace?> place, Func<bool> running, Func<string?> currentFile)
        {
            this.place = place; this.running = running; this.currentFile = currentFile;
            // The tab is drawn as part of the tray's surface: rounded on top, open at the bottom where the request box covers it.
            Tab = ThemedButton("Background='{ThemeResource LayerFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' " +
                "BorderThickness='1,1,1,0' CornerRadius='8,8,0,0' Padding='11,4,11,10' MinHeight='0'");
            countBadge = ThemedBorder("Background='{ThemeResource AccentFillColorDefaultBrush}' CornerRadius='9' MinWidth='18' Height='18' Padding='5,0,5,0'");
            var badgeText = new TextBlock { Style = Token("MasonCaptionStrong"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            badgeText.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Source = count, Path = new PropertyPath("Text") });
            badgeText.Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
            countBadge.Child = badgeText;
            var tabLabel = new TextBlock { Text = "Notes", Style = Token("MasonBodyStrong"), VerticalAlignment = VerticalAlignment.Center };
            var tabContent = Row(new FontIcon { Glyph = "", FontSize = 13 }, tabLabel, countBadge); tabContent.Spacing = 7;
            Tab.Content = tabContent;
            AutomationProperties.SetName(Tab, "Notes");
            ToolTipService.SetToolTip(Tab, "Notes Mason reads with every request (Ctrl+Shift+N)");
            Tab.Click += (_, _) => Toggle();

            Tray = ThemedBorder("Background='{ThemeResource LayerFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' " +
                "BorderThickness='1,1,1,0' CornerRadius='12,12,0,0' Padding='14,12,14,26'");
            Tray.Child = content;
            Tray.Visibility = Visibility.Collapsed;
            Tray.ActualThemeChanged += (_, _) => { if (IsOpen) Render(); };
            Tray.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape && editing == null) { e.Handled = true; Close(); } };
            AutomationProperties.SetName(Tray, "Notes");
            undoTimer.Tick += (_, _) => { undoTimer.Stop(); undo = null; Render(); };
            RefreshCount();
        }

        NotesPlace? Place() { try { return place(); } catch { return null; } }
        ProjectNote[] Notes(NotesPlace at) => CodeNotes.Load(at.Home, at.Scope.Root, at.Legacy);

        public void RefreshCount()
        {
            int n = 0;
            if (Place() is { } at) { try { n = Notes(at).Length; } catch { } }
            count.Text = n.ToString();
            countBadge.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
            Tab.Opacity = n == 0 && !IsOpen ? .75 : 1;
        }

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        public void Open(string? prefill = null)
        {
            if (Place() == null) return;
            confirmClear = false; editing = null; error = "";
            bool wasOpen = IsOpen;
            Tray.Visibility = Visibility.Visible;
            Render();
            if (prefill != null && input != null) { input.Text = prefill; input.SelectionStart = prefill.Length; }
            input?.Focus(FocusState.Programmatic);
            if (!wasOpen) Fluent.Appear(Tray, 18);
            RefreshCount(); Changed?.Invoke();
        }

        public void Close()
        {
            if (!IsOpen) return;
            Tray.Visibility = Visibility.Collapsed; confirmClear = false; editing = null;
            RefreshCount(); Changed?.Invoke();
        }

        /// <summary>Adds a note from the request box or a suggestion; returns what to tell the user.</summary>
        public string Add(string text, string source = "")
        {
            var at = Place() ?? throw new InvalidOperationException("Open a project first.");
            CodeNotes.Add(at.Home, at.Scope, text, source);
            if (IsOpen) Render();
            RefreshCount(); Changed?.Invoke();
            return "Kept. Mason reads it with every request in this project.";
        }

        public bool Full => Place() is { } at && Notes(at).Length >= CodeNotes.Limit;

        void Act(Action action)
        {
            try { error = ""; action(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException) { error = e.Message; }
            Render(); RefreshCount(); Changed?.Invoke();
        }

        void Offer(string message, ProjectNote[] notes)
        {
            undo = (message, notes); undoTimer.Stop(); undoTimer.Start();
        }

        public void Render()
        {
            content.Children.Clear();
            if (Place() is not { } at) return;
            ProjectNote[] notes;
            try { notes = Notes(at); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            { content.Children.Add(Muted("The notes could not be read · " + e.Message)); return; }

            var title = new TextBlock { Text = "Notes", Style = Token("MasonBodyLargeStrong"), VerticalAlignment = VerticalAlignment.Center };
            var subtitle = Muted("Mason reads these with every request", 12); subtitle.VerticalAlignment = VerticalAlignment.Center;
            var header = Row(title, subtitle); header.Spacing = 10;
            content.Children.Add(header);
            if (running()) content.Children.Add(Muted("Changes apply from the next request.", 12));

            // Add a note: the words, optionally the file it is about, and Add.
            bool full = notes.Length >= CodeNotes.Limit;
            input = new TextBox
            {
                PlaceholderText = full ? $"{CodeNotes.Limit} of {CodeNotes.Limit}. Remove one to add another." : "Something Mason should always know about this project",
                MaxLength = CodeNotes.TextLimit, IsEnabled = !full, VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(input, "New note");
            var addButton = Button("Add", (_, _) => AddFromInput(), true); addButton.IsEnabled = !full;
            input.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter) { e.Handled = true; AddFromInput(); }
                else if (e.Key == VirtualKey.Escape) { e.Handled = true; Close(); }
            };
            var aboutButton = new Button { Content = about.Length == 0 ? "About a file…" : about + "  ✕", VerticalAlignment = VerticalAlignment.Center, IsEnabled = !full };
            if (about.Length > 0) { aboutButton.FontFamily = new FontFamily("Cascadia Mono, Consolas"); ToolTipService.SetToolTip(aboutButton, "The note stops being sent when this file changes. Click to remove."); }
            aboutButton.Click += (_, _) =>
            {
                if (about.Length > 0) { about = ""; Render(); input?.Focus(FocusState.Programmatic); return; }
                var path = new TextBox { Header = "File in this project", Text = currentFile() ?? "", MinWidth = 260 };
                var flyout = new Flyout();
                var use = Button("Use", (_, _) => { about = path.Text.Trim(); flyout.Hide(); Render(); }, true);
                path.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; about = path.Text.Trim(); flyout.Hide(); Render(); } };
                flyout.Content = Stack(path, Muted("The note stops being sent when this file changes.", 12), use);
                flyout.ShowAt(aboutButton);
            };
            var addRow = new Grid { ColumnSpacing = 8 };
            addRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            addRow.Children.Add(input); Grid.SetColumn(aboutButton, 1); addRow.Children.Add(aboutButton); Grid.SetColumn(addButton, 2); addRow.Children.Add(addButton);
            content.Children.Add(addRow);
            if (error.Length > 0) { var e = Muted(error, 12); e.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]; content.Children.Add(e); }

            if (notes.Length == 0) content.Children.Add(Empty());
            else
            {
                var list = new StackPanel { Spacing = 6 };
                foreach (var note in notes) list.Children.Add(Card(at, note));
                content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 236, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }

            if (undo is { } offer)
            {
                var bar = new Border { Style = Token("MasonInsetCard"), Padding = new Thickness(10, 5, 10, 5) };
                var link = new HyperlinkButton { Content = "Undo", Padding = new Thickness(4, 0, 4, 0) };
                link.Click += (_, _) => Act(() => { CodeNotes.Restore(at.Home, at.Scope.Root, offer.Notes); undo = null; undoTimer.Stop(); });
                var words = Text(offer.Message, 13); words.VerticalAlignment = VerticalAlignment.Center;
                bar.Child = Row(words, link);
                content.Children.Add(bar);
            }

            if (confirmClear && notes.Length > 0)
            {
                var ask = ThemedBorder("Background='{ThemeResource SystemFillColorCriticalBackgroundBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' CornerRadius='8' Padding='10,6,8,6'");
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var question = Text($"Clear all {notes.Length} notes? Mason stops reading them.", 13); question.TextWrapping = TextWrapping.Wrap; question.VerticalAlignment = VerticalAlignment.Center;
                var clear = ThemedButton("Content='Clear' Background='{ThemeResource SystemFillColorCriticalBrush}' Foreground='{ThemeResource TextOnAccentFillColorPrimaryBrush}'");
                clear.Click += (_, _) => Act(() => { var gone = CodeNotes.Clear(at.Home, at.Scope.Root); confirmClear = false; Offer($"{gone.Length} notes cleared", gone); });
                var keep = Button("Keep", (_, _) => { confirmClear = false; Render(); });
                grid.Children.Add(question); Grid.SetColumn(clear, 1); grid.Children.Add(clear); Grid.SetColumn(keep, 2); grid.Children.Add(keep);
                ask.Child = grid; content.Children.Add(ask);
            }
            else if (notes.Length > 0)
            {
                int sent = notes.Where(n => CodeNotes.Current(at.Scope, n)).Take(CodeNotes.Limit).Count();
                // Left-aligned: the Notes tab covers the tray's bottom-right corner.
                var summary = Muted($"{sent} of {notes.Length} sent · up to {CodeNotes.Limit}", 12); summary.VerticalAlignment = VerticalAlignment.Center;
                var clearAll = new HyperlinkButton { Content = "Clear all", Padding = new Thickness(4, 0, 4, 0), FontSize = 12 };
                clearAll.Click += (_, _) => { confirmClear = true; Render(); };
                var footer = Row(summary, clearAll); footer.Spacing = 10;
                content.Children.Add(footer);
            }

            void AddFromInput()
            {
                if (input == null || string.IsNullOrWhiteSpace(input.Text)) return;
                var text = input.Text; var source = about;
                Act(() => { CodeNotes.Add(at.Home, at.Scope, text, source); about = ""; });
                input?.Focus(FocusState.Programmatic);
            }
        }

        FrameworkElement Empty()
        {
            var pose = new Image { Source = PoseSource(PosePlanning, 76), Height = 76, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(pose, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var examples = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var example in new[] { "We use pnpm, not npm", "Tests live in tests/", "Keep the public API stable" })
            {
                var chip = new HyperlinkButton { Content = example, Padding = new Thickness(8, 1, 8, 2) };
                chip.Click += (_, _) => { if (input != null) { input.Text = example; input.SelectionStart = example.Length; input.Focus(FocusState.Programmatic); } };
                examples.Children.Add(chip);
            }
            var words = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = "Nothing to remember yet", Style = Token("MasonBodyStrong") });
            var line = Muted("Notes are facts Mason should know every time: how to run things, where things live, what not to touch.", 12); line.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(line); words.Children.Add(examples);
            var grid = new Grid { ColumnSpacing = 14 };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(pose); Grid.SetColumn(words, 1); grid.Children.Add(words);
            return grid;
        }

        FrameworkElement Card(NotesPlace at, ProjectNote note)
        {
            bool current = CodeNotes.Current(at.Scope, note);
            var card = new Border { Style = Token("MasonRowCard"), MinHeight = 0, Padding = new Thickness(0, 7, 6, 7) };
            // The status palette's amber reads on both the light and the dark tray.
            var caution = new SolidColorBrush(ThemePalette.Caution(Tray.ActualTheme == ElementTheme.Dark));
            if (!current) card.BorderBrush = caution;
            var pin = current ? ThemedBorder("Background='{ThemeResource AccentFillColorDefaultBrush}' Width='4' CornerRadius='2' Margin='8,1,0,1'")
                              : ThemedBorder("Background='{ThemeResource ControlStrongFillColorDefaultBrush}' Width='4' CornerRadius='2' Margin='8,1,0,1'");
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.Children.Add(pin);

            var body = new StackPanel { Spacing = 2 };
            if (editing == note.Id)
            {
                var edit = new TextBox { Text = note.Text, MaxLength = Math.Max(CodeNotes.TextLimit, note.Text.Length), AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetName(edit, "Edit note");
                edit.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Enter) { e.Handled = true; var text = edit.Text; Act(() => { CodeNotes.Edit(at.Home, at.Scope.Root, note.Id, text); editing = null; }); }
                    else if (e.Key == VirtualKey.Escape) { e.Handled = true; editing = null; Render(); }
                };
                edit.Loaded += (_, _) => { edit.Focus(FocusState.Programmatic); edit.SelectionStart = edit.Text.Length; };
                body.Children.Add(edit);
            }
            else
            {
                var words = new TextBlock { Text = note.Text, Style = Token("MasonBody"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
                ToolTipService.SetToolTip(words, "Click to edit");
                words.Tapped += (_, _) => { editing = note.Id; Render(); };
                body.Children.Add(words);
            }
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (note.Source.Length > 0) meta.Children.Add(new TextBlock { Text = note.Source, Style = Token("MasonCaptionSecondary"), FontFamily = new FontFamily("Cascadia Mono, Consolas") });
            if (current) meta.Children.Add(Muted(NoteAge(note.Saved), 12));
            else
            {
                var warn = new TextBlock { Text = $"{note.Source} changed · not sent", Style = Token("MasonCaption"), Foreground = caution };
                var keep = new HyperlinkButton { Content = "Keep anyway", Padding = new Thickness(2, 0, 2, 0), FontSize = 12 };
                keep.Click += (_, _) => Act(() => CodeNotes.Refresh(at.Home, at.Scope, note.Id));
                var drop = new HyperlinkButton { Content = "Remove", Padding = new Thickness(2, 0, 2, 0), FontSize = 12 };
                drop.Click += (_, _) => Act(() => { if (CodeNotes.Remove(at.Home, at.Scope.Root, note.Id) is { } gone) Offer("Note removed", [gone]); });
                meta.Children.Add(warn); meta.Children.Add(keep); meta.Children.Add(drop);
            }
            body.Children.Add(meta);
            Grid.SetColumn(body, 1); grid.Children.Add(body);

            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(6), MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(remove, "Remove note");
            ToolTipService.SetToolTip(remove, "Remove note");
            remove.Click += (_, _) => Act(() => { if (CodeNotes.Remove(at.Home, at.Scope.Root, note.Id) is { } gone) Offer("Note removed", [gone]); });
            Grid.SetColumn(remove, 2); grid.Children.Add(remove);
            card.Child = grid;
            return card;
        }
    }

    /// <summary>The card under an answer when Mason suggested a note: Remember, Edit or Dismiss.</summary>
    static FrameworkElement NoteSuggestionCard(string text, bool full, Action remember, Action edit, Action dismiss)
    {
        var card = ThemedBorder("Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' " +
            "BorderThickness='1' CornerRadius='8' Padding='0,8,10,8'");
        var bar = ThemedBorder("Background='{ThemeResource AccentFillColorDefaultBrush}' Width='3' CornerRadius='2' Margin='0,0,0,0'");
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = "Remember this for next time?", Style = Token("MasonBodyStrong") });
        var quote = Muted("“" + text + "”", 13); quote.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(quote);
        var keep = Button("Remember", (_, _) => remember(), true); keep.IsEnabled = !full;
        if (full) ToolTipService.SetToolTip(keep, $"{CodeNotes.Limit} of {CodeNotes.Limit} notes kept. Remove one to add another.");
        var buttons = Row(keep, Button("Edit", (_, _) => edit()), Button("Dismiss", (_, _) => dismiss()));
        buttons.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(bar); Grid.SetColumn(words, 1); grid.Children.Add(words); Grid.SetColumn(buttons, 2); grid.Children.Add(buttons);
        card.Child = grid;
        AutomationProperties.SetName(card, "Mason suggests a note");
        return card;
    }
}
