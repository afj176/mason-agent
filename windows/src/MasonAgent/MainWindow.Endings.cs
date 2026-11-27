using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

// How a request ended, drawn from CodeRunEnding's single table: a pill in the status row, and for endings that need a
// word of explanation a calm card at the end of the conversation. Red is kept for things that broke (the model server,
// Mason itself); what the model did is the caution colour, and what the user did is neutral.
public sealed partial class MainWindow
{
    /// <summary>Holds the latest ending's pill in the status row, beside the pose.</summary>
    readonly Border endingHost = new() { VerticalAlignment = VerticalAlignment.Center };

    static Tone EndingTone(CodeEndingTone tone) => tone switch
    {
        CodeEndingTone.Success => Tone.Good, CodeEndingTone.Warning => Tone.Warn, CodeEndingTone.Accent => Tone.Accent,
        CodeEndingTone.Danger => Tone.Bad, _ => Tone.Neutral
    };

    /// <summary>Segoe Fluent glyphs for each ending; the terminal uses the table's text glyphs instead.</summary>
    static string EndingIcon(string code) => code switch
    {
        CodeRunEnding.Done => "", CodeRunEnding.Unverified => "", CodeRunEnding.Question => "",
        CodeRunEnding.ModelLoop => "", CodeRunEnding.ModelStuck or CodeRunEnding.Blocked => "",
        CodeRunEnding.StepLimit or CodeRunEnding.TimeLimit => "", CodeRunEnding.Stopped => "", _ => ""
    };

    /// <summary>The ending's pill for the status row, or null while running or for an ending this version does not know.</summary>
    static FrameworkElement? EndingPill(CodeRunEnding? ending)
    {
        if (ending == null || CodeRunEnding.Look(ending.Code) is not { } look) return null;
        var pill = Pill(look.Headline, EndingTone(look.Tone));
        pill.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(pill, look.Headline + ", caused by " + look.Cause);
        return pill;
    }

    static FrameworkElement EndingCard(CodeSession session, CodeRunEnding ending)
    {
        var look = CodeRunEnding.Look(ending.Code)!;
        string brush = look.Tone == CodeEndingTone.Danger ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush";
        var card = ThemedBorder("Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' " +
            "BorderThickness='1' CornerRadius='8' Padding='0,10,14,10'");
        var bar = ThemedBorder("Background='{ThemeResource " + brush + "}' Width='3' CornerRadius='2'");
        var glyph = (FontIcon)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<FontIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' FontSize='16' " +
            "Foreground='{ThemeResource " + brush + "}' VerticalAlignment='Top' Margin='0,2,0,0'/>");
        glyph.Glyph = EndingIcon(ending.Code);
        string title = ending.Code == CodeRunEnding.ModelLoop ? "The model repeated itself" : look.Headline;
        string body = ending.Code == CodeRunEnding.ModelLoop && session.RepeatStop is { } repeat ? repeat.Explanation(ending.Kept > 0) : session.Detail;
        var words = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, Style = Token("MasonBodyStrong") });
        void Line(string text, double size) { if (text.Length == 0) return; var t = Muted(text, size); t.TextWrapping = TextWrapping.Wrap; words.Children.Add(t); }
        Line(body, 13);
        Line(CodeRunEnding.Hint(ending.Code, terminal: false), 12);
        if (ending.KeptText is { } kept) Line(kept + " · review them below", 12);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(bar); Grid.SetColumn(glyph, 1); grid.Children.Add(glyph); Grid.SetColumn(words, 2); grid.Children.Add(words);
        card.Child = grid;
        card.Margin = new Thickness(40, 0, 0, 0);
        AutomationProperties.SetName(card, title + ". " + body + " " + CodeRunEnding.Hint(ending.Code, terminal: false));
        return card;
    }
}
