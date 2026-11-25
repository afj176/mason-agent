using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Mason;

/// <summary>
/// The theme choice shown at the foot of the sidebar: Auto, Light or Dark, always visible and one click
/// to change. When the sidebar collapses to icons it becomes a single button that steps through the three.
/// </summary>
public sealed class ThemeSwitch : UserControl
{
    static readonly (ElementTheme Theme, string Label, string Glyph, string Tip)[] Options =
    [
        (ElementTheme.Default, "Auto", "", "Follow the Windows light or dark setting"),
        (ElementTheme.Light, "Light", "", "Always use the light theme"),
        (ElementTheme.Dark, "Dark", "", "Always use the dark theme"),
    ];

    readonly List<ToggleButton> segments = [];
    readonly Grid row = new() { ColumnSpacing = 4 };
    // The side margin the owner gives suits the open sidebar; in the 48px icon rail the button pulls back to stay centred and unclipped.
    readonly Button cycle = new() { Padding = new Thickness(0), Width = 40, Height = 36, Margin = new Thickness(-8, 0, -8, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    readonly FontIcon cycleIcon = new() { FontSize = 16 };
    ElementTheme value;

    /// <summary>Raised when the user picks a theme. The owner applies and saves it.</summary>
    public event Action<ElementTheme>? Changed;

    /// <param name="brand">Applies the app's accent to a segment before its template loads.</param>
    public ThemeSwitch(ElementTheme initial, Func<ToggleButton, ToggleButton>? brand = null)
    {
        value = Known(initial);
        for (int i = 0; i < Options.Length; i++)
        {
            var option = Options[i];
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new FontIcon { Glyph = option.Glyph, FontSize = 14 });
            content.Children.Add(new TextBlock { Text = option.Label, FontSize = 13 });
            var segment = new ToggleButton { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 6, 6, 7), MinWidth = 0 };
            segment = brand?.Invoke(segment) ?? segment;
            AutomationProperties.SetName(segment, option.Label + " theme");
            ToolTipService.SetToolTip(segment, option.Tip);
            // A toggle button would switch itself off when its own checked segment is clicked; one of
            // the three is always chosen, so every click re-applies the selection instead.
            segment.Click += (_, _) => Choose(option.Theme);
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(segment, i); row.Children.Add(segment); segments.Add(segment);
        }

        cycle.Content = cycleIcon;
        cycle.Click += (_, _) => Choose(Options[(IndexOf(value) + 1) % Options.Length].Theme);

        var host = new Grid();
        host.Children.Add(row); host.Children.Add(cycle);
        Content = host;
        AutomationProperties.SetName(this, "Theme");
        // The sidebar gives this control its full width when open and about 48px when collapsed to icons.
        SizeChanged += (_, _) =>
        {
            bool compact = ActualWidth < 150;
            row.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            cycle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        };
        Show();
    }

    /// <summary>Shows a theme without raising <see cref="Changed"/>, for example to undo a choice that could not be saved.</summary>
    public void Display(ElementTheme theme)
    {
        value = Known(theme); Show();
    }

    /// <summary>Any value other than the three offered (for example a number in a hand-edited profile) means Auto.</summary>
    static ElementTheme Known(ElementTheme theme) => Array.Exists(Options, o => o.Theme == theme) ? theme : ElementTheme.Default;
    static int IndexOf(ElementTheme theme) => Math.Max(0, Array.FindIndex(Options, o => o.Theme == theme));

    void Choose(ElementTheme theme)
    {
        bool changed = theme != value;
        value = theme; Show();
        if (changed) Changed?.Invoke(theme);
    }

    void Show()
    {
        for (int i = 0; i < Options.Length; i++) segments[i].IsChecked = Options[i].Theme == value;
        var current = Options[IndexOf(value)];
        var next = Options[(IndexOf(value) + 1) % Options.Length];
        cycleIcon.Glyph = current.Glyph;
        ToolTipService.SetToolTip(cycle, $"Theme: {current.Label}. Select to switch to {next.Label}.");
        AutomationProperties.SetName(cycle, $"Theme: {current.Label}");
    }
}
