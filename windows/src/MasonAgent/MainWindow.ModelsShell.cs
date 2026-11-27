using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mason;

/// <summary>Status colours for the Models page. Each tone has a light and a dark value; high contrast uses system colours.</summary>
public enum Tone { Neutral, Accent, Good, Warn, Bad, Search }

public static class ModelTones
{
    public static Color Ink(Tone tone, bool dark) => tone switch
    {
        Tone.Accent => ThemePalette.Accent(dark),
        Tone.Good => ThemePalette.Success(dark),
        Tone.Warn => ThemePalette.Caution(dark),
        Tone.Bad => ThemePalette.Critical(dark),
        Tone.Search => ThemePalette.Search(dark),
        _ => ThemePalette.Muted(dark)
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
    static string ToneKey(Tone tone) => tone switch { Tone.Good => "Success", Tone.Warn => "Caution", Tone.Bad => "Critical", Tone.Accent or Tone.Search => "Attention", _ => "" };

    /// <summary>A status pill on the system status colour pairs (success, caution, critical, attention, neutral).</summary>
    static Border Pill(string text, Tone tone, double size = 11)
    {
        var label = new TextBlock { Text = text, Style = Token("MasonPillText" + ToneKey(tone)), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        var pill = new Border { Child = label, Style = Token("MasonPill" + ToneKey(tone)), HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pill, text);
        return pill;
    }

    /// <summary>A small status dot.</summary>
    static Border Dot(Tone tone) => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Microsoft.UI.Xaml.Shapes.Ellipse { Style = Token(tone switch { Tone.Good => "MasonDotSuccess", Tone.Warn => "MasonDotCaution", Tone.Bad => "MasonDotCritical", Tone.Accent or Tone.Search => "MasonDotAccent", _ => "MasonDotNeutral" }) }
    };

    sealed record RailEntry(string Label, string Glyph, Grid Row, TextBlock Count, Border Indicator, Button Button);

    /// <summary>The section rail: subtle buttons with a glyph, label and count; the selected one gets the subtle fill and the
    /// accent pill on its left edge, like a NavigationView item.</summary>
    static (FrameworkElement View, RailEntry[] Entries, Action<int> Select, Action<bool> Compact) SectionRail(Action<int> chosen, params (string Label, string Glyph)[] sections)
    {
        var list = new StackPanel { Spacing = 4 };
        var entries = new List<RailEntry>();
        foreach (var (section, index) in sections.Select((s, i) => (s, i)))
        {
            var indicator = new Border { Style = Token("MasonRailIndicator"), Opacity = 0 };
            var glyph = new FontIcon { Glyph = section.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = section.Label, Style = Token("MasonBody"), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var count = new TextBlock { Style = Token("MasonCaptionSecondary"), VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 8, 12, 8) };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) row.ColumnDefinitions.Add(new() { Width = width });
            row.Children.Add(indicator); Grid.SetColumn(glyph, 1); row.Children.Add(glyph); Grid.SetColumn(label, 2); row.Children.Add(label); Grid.SetColumn(count, 3); row.Children.Add(count);
            var fill = new Border { Style = Token("MasonRailSelectedFill"), Opacity = 0 };
            var holder = new Grid(); holder.Children.Add(fill); holder.Children.Add(row);
            var button = new Button { Content = holder, Style = Token("SubtleButtonStyle"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, section.Label);
            int at = index; button.Click += (_, _) => chosen(at);
            list.Children.Add(button); entries.Add(new(section.Label, section.Glyph, row, count, indicator, button));
        }
        int current = -1;
        void Paint()
        {
            foreach (var (entry, i) in entries.Select((e, i) => (e, i)))
            {
                bool on = i == current;
                entry.Indicator.Opacity = on ? 1 : 0;
                ((Grid)entry.Button.Content).Children[0].Opacity = on ? 1 : 0;
                ((TextBlock)entry.Row.Children[2]).FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
        void Compact(bool compact) { foreach (var e in entries) { e.Row.Children[2].Visibility = e.Row.Children[3].Visibility = compact ? Visibility.Collapsed : Visibility.Visible; ToolTipService.SetToolTip(e.Button, compact ? e.Label : null); } }
        return (list, entries.ToArray(), i => { current = i; Paint(); }, Compact);
    }
}
