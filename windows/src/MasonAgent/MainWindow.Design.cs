using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Mason;

/// <summary>
/// Mason's component set on Fluent 2 (Styles/Mason.xaml holds the tokens). Patterns follow the Windows design guidance
/// and the WinUI Gallery / PowerToys / Files conventions: settings cards 4 px apart under Body Strong group headers,
/// segmented choices for 2–5 options, icon+label buttons with 16 px Segoe Fluent glyphs, detail lists instead of
/// read-only text boxes, Caption-header tables, status pills on the system status colours.
/// </summary>
public sealed partial class MainWindow
{
    static Style Token(string key) => (Style)Application.Current.Resources[key];

    /// <summary>One of a small set of views (Workbench, Review): checked is raised with an accent pill.</summary>
    static ToggleButton SegmentToggle(ToggleButton toggle) { toggle.Style = Token("MasonSegmentToggle"); return toggle; }
    /// <summary>Shows or hides a panel: checked takes the subtle fill and accent ink instead of a solid accent block.</summary>
    static ToggleButton ChipToggle(ToggleButton toggle) { toggle.Style = Token("MasonChipToggle"); return toggle; }

    /// <summary>A 16 px Segoe Fluent Icons glyph.</summary>
    static FontIcon Glyph(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A button with a leading glyph and a label, 8 px apart.</summary>
    static Button IconButton(string glyph, string label, RoutedEventHandler handler, bool accent = false)
    {
        var button = new Button { Content = Row(Glyph(glyph), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }), VerticalAlignment = VerticalAlignment.Center };
        if (accent) button.Style = Token("AccentButtonStyle");
        button.Click += handler;
        AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>A transparent 32×32 icon button that fills on hover; the tooltip doubles as its accessible name.</summary>
    static Button SubtleIconButton(string glyph, string tooltip, RoutedEventHandler handler)
    {
        var button = new Button { Content = Glyph(glyph), Style = Token("SubtleButtonStyle"), Width = 32, Height = 32, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += handler;
        ToolTipService.SetToolTip(button, tooltip); AutomationProperties.SetName(button, tooltip);
        return button;
    }

    /// <summary>A subtle text button (no fill until hover), for secondary row actions.</summary>
    static Button SubtleButton(string label, RoutedEventHandler handler, string? glyph = null)
    {
        var button = glyph == null ? new Button { Content = label } : new Button { Content = Row(Glyph(glyph), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }) };
        button.Style = Token("SubtleButtonStyle"); button.VerticalAlignment = VerticalAlignment.Center;
        button.Click += handler; AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>
    /// A Windows Settings row: 20 px glyph, header and description on the left, the control on the right. Below 520 px the
    /// control wraps under the text. With <paramref name="click"/> the whole card is a button with a chevron.
    /// </summary>
    static FrameworkElement SettingsCard(string glyph, string header, string? description, FrameworkElement? control = null, Action? click = null)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var icon = Glyph(glyph, 20); icon.Margin = new Thickness(2, 0, 4, 0); grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = header, Style = Token("MasonBody") });
        if (!string.IsNullOrWhiteSpace(description)) text.Children.Add(new TextBlock { Text = description, Style = Token("MasonCaptionSecondary") });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (control != null) { control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 2); grid.Children.Add(control); }
        if (click != null) { var chevron = Glyph("", 12); chevron.Style = Token("MasonGlyphSecondary"); chevron.FontSize = 12; Grid.SetColumn(chevron, control == null ? 2 : 3); if (control != null) grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.Children.Add(chevron); }
        grid.SizeChanged += (_, e) =>
        {
            if (control == null) return;
            bool narrow = e.NewSize.Width < 520;
            Grid.SetRow(control, narrow ? 1 : 0); Grid.SetColumn(control, narrow ? 1 : 2); Grid.SetColumnSpan(control, narrow ? 2 : 1);
            control.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
        if (click == null)
        {
            var card = new Border { Child = grid, Style = Token("MasonRowCard") };
            AutomationProperties.SetName(card, header);
            return card;
        }
        var button = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 12, 16, 12), MinHeight = 68 };
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, header);
        return button;
    }

    /// <summary>A Body Strong group header followed by cards 4 px apart (Windows Settings rhythm).</summary>
    static StackPanel SettingsGroup(string? title, params FrameworkElement[] cards)
    {
        var group = new StackPanel { Spacing = 4 };
        if (title != null) group.Children.Add(new TextBlock { Text = title, Style = Token("MasonBodyStrong"), Margin = new Thickness(1, 24, 0, 6) });
        foreach (var card in cards) group.Children.Add(card);
        return group;
    }

    /// <summary>A ToggleSwitch for a settings card: label lives in the card, so the switch shows no On/Off text.</summary>
    static ToggleSwitch Switch(bool on, Action<bool> changed, string name)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    /// <summary>
    /// A segmented choice for 2–5 options. The selected option is raised like a default button with an accent pill
    /// under its label; the rest are subtle. Arrow keys move the selection.
    /// </summary>
    sealed class Segmented
    {
        readonly Grid items = new() { ColumnSpacing = 2 };
        readonly List<Button> buttons = [];
        readonly string[] labels;
        public Border View { get; }
        public int Selected { get; private set; } = -1;
        public event Action<int>? Changed;
        /// <summary>ComboBox-style access so a segmented choice can replace a two- or three-option ComboBox.</summary>
        public int SelectedIndex { get => Selected; set => Select(value, true); }
        public bool IsEnabled { get => buttons.All(b => b.IsEnabled); set { foreach (var b in buttons) b.IsEnabled = value; View.Opacity = value ? 1 : .6; } }

        public Segmented(string name, IReadOnlyList<(string Label, string? Glyph)> options, int selected = 0, bool stretch = false)
        {
            labels = options.Select(o => o.Label).ToArray();
            View = new Border { Child = items, Style = Token("MasonSegmentTrack"), HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(View, name);
            foreach (var (option, index) in options.Select((o, i) => (o, i)))
            {
                items.ColumnDefinitions.Add(new() { Width = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                var button = new Button();
                int at = index; button.Click += (_, _) => Select(at, true);
                button.KeyDown += (_, e) =>
                {
                    int next = e.Key switch { Windows.System.VirtualKey.Left => at - 1, Windows.System.VirtualKey.Right => at + 1, _ => -1 };
                    if (next < 0 || next >= buttons.Count) return;
                    Select(next, true); buttons[next].Focus(FocusState.Keyboard); e.Handled = true;
                };
                AutomationProperties.SetName(button, option.Label);
                Grid.SetColumn(button, index); items.Children.Add(button); buttons.Add(button);
                button.Tag = option.Glyph;
            }
            Select(Math.Clamp(selected, 0, options.Count - 1), false);
        }

        public void Select(int index, bool raise)
        {
            if (index == Selected) return;
            Selected = index;
            foreach (var (button, i) in buttons.Select((b, i) => (b, i)))
            {
                bool on = i == index;
                button.Style = Token(on ? "MasonSegmentSelected" : "MasonSegmentItem");
                var label = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                FrameworkElement content = button.Tag is string glyph ? Row(Glyph(glyph, 14), label) : label;
                content.HorizontalAlignment = HorizontalAlignment.Center;
                var stack = new Grid(); stack.Children.Add(content);
                if (on) stack.Children.Add(new Border { Style = Token("MasonSegmentIndicator") });
                button.Content = stack;
                AutomationProperties.SetItemStatus(button, on ? "Selected" : "");
            }
            if (raise) Changed?.Invoke(index);
        }
    }

    /// <summary>Label/value pairs: Caption Secondary labels over Body values, Cascadia Mono for paths and identifiers.
    /// Replaces read-only text boxes used to show details.</summary>
    static FrameworkElement DetailList(params (string Label, string Value, bool Mono)[] rows)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (var (label, value, mono) in rows.Where(r => !string.IsNullOrEmpty(r.Value)))
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var key = new TextBlock { Text = label, Style = Token("MasonCaptionSecondary"), Margin = new Thickness(0, 2, 0, 0) };
            var text = new TextBlock { Text = value, Style = Token(mono ? "MasonMono" : "MasonBody"), IsTextSelectionEnabled = true };
            Grid.SetRow(key, row); Grid.SetRow(text, row); Grid.SetColumn(text, 1);
            grid.Children.Add(key); grid.Children.Add(text); row++;
        }
        grid.SizeChanged += (_, e) => grid.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 420 ? 96 : 140);
        return grid;
    }

    /// <summary>A page header: Title (Manrope) with a secondary line and right-aligned actions.</summary>
    static FrameworkElement PageHeader(string title, string? subtitle, params UIElement[] actions)
    {
        var grid = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        var heading = new TextBlock { Text = title, Style = Token("MasonTitle"), FontFamily = DisplayFont };
        text.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(subtitle)) text.Children.Add(new TextBlock { Text = subtitle, Style = Token("MasonBodySecondary") });
        grid.Children.Add(text);
        if (actions.Length > 0) { var commands = Row(actions); commands.VerticalAlignment = VerticalAlignment.Bottom; Grid.SetColumn(commands, 1); grid.Children.Add(commands); }
        return grid;
    }

    /// <summary>A path, hash, size or shortcut in Cascadia Mono on a control-fill badge.</summary>
    static Border CodeBadge(string text) => new() { Child = new TextBlock { Text = text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 }, Style = Token("MasonCodeBadge") };

    static Border Divider() => new() { Style = Token("MasonDivider") };

    /// <summary>An empty state: 48 px glyph in tertiary ink, Subtitle, one secondary sentence and up to two actions.</summary>
    static FrameworkElement EmptyPanel(string glyph, string title, string body, params UIElement[] actions)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 40, 24, 40) };
        var icon = Glyph(glyph, 48); icon.Foreground = null; icon.Style = Token("MasonGlyphSecondary"); icon.FontSize = 48; icon.Opacity = .7; icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = title, Style = Token("MasonSubtitle"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
        panel.Children.Add(new TextBlock { Text = body, Style = Token("MasonBodySecondary"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
        if (actions.Length > 0) { var row = Row(actions); row.HorizontalAlignment = HorizontalAlignment.Center; row.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(row); }
        return panel;
    }

    /// <summary>A scrollable settings-style page: 36 px side padding, 1064 px max width, groups stacked.</summary>
    static ScrollViewer SettingsPage(params FrameworkElement[] content)
    {
        var stack = new StackPanel { Spacing = 4, MaxWidth = 1064, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 36) };
        foreach (var item in content) stack.Children.Add(item);
        return new ScrollViewer { Content = stack, Padding = new Thickness(4, 0, 16, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
