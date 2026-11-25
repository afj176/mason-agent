using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mason;

/// <summary>Status colours for the Models page. Each tone has a light and a dark value; high contrast uses system colours.</summary>
public enum Tone { Neutral, Accent, Good, Warn, Bad, Search }

public static class ModelTones
{
    static Color Rgb(int v, byte a = 255) => Color.FromArgb(a, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    public static Color Ink(Tone tone, bool dark) => tone switch
    {
        Tone.Accent => dark ? Rgb(0xC3C2FF) : Rgb(0x4849A8),
        Tone.Good => dark ? Rgb(0x7EE0A6) : Rgb(0x14734A),
        Tone.Warn => dark ? Rgb(0xF2C66D) : Rgb(0x8A5A00),
        Tone.Bad => dark ? Rgb(0xFF9D94) : Rgb(0xAF2323),
        Tone.Search => dark ? Rgb(0x8FD9E3) : Rgb(0x0F6E7A),
        _ => dark ? Rgb(0xC3C9E0) : Rgb(0x535B72)
    };
    public static Color Fill(Tone tone, bool dark) { var c = Ink(tone, dark); return Color.FromArgb(dark ? (byte)38 : (byte)26, c.R, c.G, c.B); }
    public static SolidColorBrush InkBrush(Tone tone, bool dark) => Contrast ? new(System(Windows.UI.ViewManagement.UIElementType.WindowText)) : new(Ink(tone, dark));
    public static SolidColorBrush FillBrush(Tone tone, bool dark) => Contrast ? new(System(Windows.UI.ViewManagement.UIElementType.Window)) : new(Fill(tone, dark));
    static bool Contrast => new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
    static Color System(Windows.UI.ViewManagement.UIElementType type) => new Windows.UI.ViewManagement.UISettings().UIElementColor(type);
    public static Tone For(ModelReadiness readiness, AgentRole role) => role is AgentRole.NotUsed or AgentRole.Unknown ? Tone.Neutral : readiness switch
    {
        ModelReadiness.Ready => role is AgentRole.Draft or AgentRole.Adapter or AgentRole.Projector ? Tone.Neutral : Tone.Good,
        ModelReadiness.NeedsRuntime or ModelReadiness.NeedsRuntimeUpdate or ModelReadiness.CheckFiles => Tone.Warn,
        _ => Tone.Neutral
    };
    public static Tone For(AgentRole role) => role switch
    {
        AgentRole.Implementer or AgentRole.Vision => Tone.Accent,
        AgentRole.CodeSearch or AgentRole.Reranker => Tone.Search,
        _ => Tone.Neutral
    };
}

public sealed partial class MainWindow
{
    /// <summary>A rounded status label. Re-tints itself when the theme changes.</summary>
    static Border Pill(string text, Tone tone, double size = 11)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        var pill = new Border { Child = label, Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        void Theme() { bool dark = pill.ActualTheme == ElementTheme.Dark; label.Foreground = ModelTones.InkBrush(tone, dark); pill.Background = ModelTones.FillBrush(tone, dark); pill.BorderThickness = new Thickness(BrandHighContrast ? 1 : 0); pill.BorderBrush = label.Foreground; }
        pill.Loaded += (_, _) => Theme(); pill.ActualThemeChanged += (_, _) => Theme(); Theme();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pill, text);
        return pill;
    }

    /// <summary>A small coloured dot for readiness lists.</summary>
    static Border Dot(Tone tone)
    {
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        void Theme() => dot.Background = ModelTones.InkBrush(tone, dot.ActualTheme == ElementTheme.Dark);
        dot.Loaded += (_, _) => Theme(); dot.ActualThemeChanged += (_, _) => Theme(); Theme();
        return dot;
    }

    sealed record RailEntry(string Label, string Glyph, Grid Row, TextBlock Count, Border Indicator, Button Button);

    /// <summary>The section rail: one button per section with a glyph, a label, a count and a selection bar.</summary>
    static (FrameworkElement View, RailEntry[] Entries, Action<int> Select, Action<bool> Compact) SectionRail(Action<int> chosen, params (string Label, string Glyph)[] sections)
    {
        var list = new StackPanel { Spacing = 2 };
        var entries = new List<RailEntry>();
        foreach (var (section, index) in sections.Select((s, i) => (s, i)))
        {
            var indicator = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            var glyph = new FontIcon { Glyph = section.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = section.Label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var count = new TextBlock { FontSize = 12, Opacity = .75, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(6, 9, 12, 9) };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) row.ColumnDefinitions.Add(new() { Width = width });
            row.Children.Add(indicator); Grid.SetColumn(glyph, 1); row.Children.Add(glyph); Grid.SetColumn(label, 2); row.Children.Add(label); Grid.SetColumn(count, 3); row.Children.Add(count);
            var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(RadiusSmall) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, section.Label);
            int at = index; button.Click += (_, _) => chosen(at);
            list.Children.Add(button); entries.Add(new(section.Label, section.Glyph, row, count, indicator, button));
        }
        int current = -1;
        void Paint()
        {
            foreach (var (entry, i) in entries.Select((e, i) => (e, i)))
            {
                bool on = i == current, dark = entry.Button.ActualTheme == ElementTheme.Dark;
                entry.Indicator.Opacity = on ? 1 : 0;
                entry.Indicator.Background = ModelTones.InkBrush(Tone.Accent, dark);
                entry.Button.Background = on && !BrandHighContrast ? new SolidColorBrush(dark ? Color.FromArgb(255, 0x2C, 0x30, 0x4A) : Color.FromArgb(255, 0xEE, 0xEE, 0xF7)) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                ((TextBlock)entry.Row.Children[2]).FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
        list.ActualThemeChanged += (_, _) => Paint();
        void Compact(bool compact) { foreach (var e in entries) { e.Row.Children[2].Visibility = e.Row.Children[3].Visibility = compact ? Visibility.Collapsed : Visibility.Visible; ToolTipService.SetToolTip(e.Button, compact ? e.Label : null); } }
        return (list, entries.ToArray(), i => { current = i; Paint(); }, Compact);
    }
}
