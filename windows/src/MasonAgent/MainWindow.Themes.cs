using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mason;

// Theme families (Blueprint, Evergreen, Graphite, Kiln, Retro Neon): the picker in the Appearance flyout and
// applying a family live. The mode (Auto / Light / Dark) stays with ThemeSwitch.
public sealed partial class MainWindow
{
    /// <summary>Five cards, each split into the family's light and dark window colours with its accent.</summary>
    FrameworkElement BuildThemeFamilyPicker(FrameworkElement flyoutContent)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 2; r++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var cards = new List<(Button Card, ThemeFamily Family)>();

        // The chosen card has a 2 px border in the active family's accent; colours are read for the picker's own
        // light or dark theme, and again whenever that theme or the family changes.
        void Mark()
        {
            bool dark = grid.ActualTheme == ElementTheme.Dark;
            foreach (var (card, family) in cards)
            {
                bool on = family == ThemePalette.Current;
                card.BorderThickness = new Thickness(on ? 2 : 1);
                if (BrandHighContrast) card.ClearValue(Control.BorderBrushProperty);
                else card.BorderBrush = new SolidColorBrush(on ? ThemePalette.Accent(dark) : ThemePalette.Line(dark));
                AutomationProperties.SetItemStatus(card, on ? "Selected" : "");
            }
        }
        grid.Loaded += (_, _) => Mark();
        grid.ActualThemeChanged += (_, _) => Mark();

        for (int i = 0; i < ThemeFamilies.All.Count; i++)
        {
            var family = ThemeFamilies.All[i];
            Border Half(ThemeTokens t, CornerRadius corner)
            {
                var accent = new Border { Width = 16, Height = 8, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(ThemePalette.C(t.Accent)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 7) };
                return new Border { Background = new SolidColorBrush(ThemePalette.C(t.Background)), Height = 34, CornerRadius = corner, Child = accent };
            }
            var preview = new Grid();
            preview.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            preview.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            preview.Children.Add(Half(family.Light, new CornerRadius(5, 0, 0, 0)));
            var darkHalf = Half(family.Dark, new CornerRadius(0, 5, 0, 0)); Grid.SetColumn(darkHalf, 1); preview.Children.Add(darkHalf);
            var body = new StackPanel { Spacing = 0 };
            body.Children.Add(preview);
            body.Children.Add(new TextBlock { Text = family.Name, Style = Token("MasonCaption"), Margin = new Thickness(8, 5, 8, 6), TextTrimming = TextTrimming.CharacterEllipsis });
            var card = new Button
            {
                Content = body, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            AutomationProperties.SetName(card, family.Name + " theme");
            ToolTipService.SetToolTip(card, $"{family.Name}: light and dark colours");
            card.Click += async (_, _) =>
            {
                if (family == ThemePalette.Current) return;
                try
                {
                    Store.Write("appearance.json", preferences with { Family = family.Name });
                    preferences = preferences with { Family = family.Name };
                }
                catch (Exception error) { await Notice("Appearance could not be saved", error.Message); return; }
                ApplyThemeFamily(family, flyoutContent);
                Mark();
            };
            Grid.SetColumn(card, i % 3); Grid.SetRow(card, i / 3);
            grid.Children.Add(card); cards.Add((card, family));
        }
        Mark();
        AutomationProperties.SetName(grid, "Theme");
        return grid;
    }

    /// <summary>
    /// Swaps the palette, then steps the window's theme away and back so every {ThemeResource} lookup and every
    /// ActualThemeChanged handler that paints from ThemePalette runs again with the new family.
    /// </summary>
    void ApplyThemeFamily(ThemeFamily family, params FrameworkElement[] alsoRefresh)
    {
        ThemePalette.Install(family);
        foreach (var element in alsoRefresh.Prepend(root))
        {
            var chosen = element.RequestedTheme;
            element.RequestedTheme = element.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            element.RequestedTheme = chosen;
        }
        ApplyWindowTheme();
    }
}
