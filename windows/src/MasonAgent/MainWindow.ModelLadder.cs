using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mason;

public sealed partial class MainWindow
{
    /// <summary>
    /// The Library's context ladder: one cell per context size with the memory it needs under the label, tinted by
    /// fit. One selection at a time; arrow keys move it. The cells are rebuilt only when the model or reading changes.
    /// </summary>
    sealed class ContextLadder
    {
        readonly Grid cells = new() { ColumnSpacing = 2 };
        readonly List<Button> buttons = [];
        (int Tokens, string Memory, FitTone Tone, string Name)[] steps = [];
        public Border View { get; }
        public int Tokens { get; private set; } = 2048;
        public event Action<int>? Changed;
        public bool IsEnabled { get => buttons.All(b => b.IsEnabled); set { foreach (var b in buttons) b.IsEnabled = value; View.Opacity = value ? 1 : .6; } }

        public ContextLadder()
        {
            View = new Border { Child = cells, Style = Token("MasonSegmentTrack"), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(3), CornerRadius = new CornerRadius(8) };
            AutomationProperties.SetName(View, "Context length");
        }

        public void Set(IReadOnlyList<(int Tokens, string Memory, FitTone Tone, string Name)> next, int selected, bool muted)
        {
            steps = next.ToArray();
            cells.Children.Clear(); cells.ColumnDefinitions.Clear(); buttons.Clear();
            foreach (var (step, index) in steps.Select((s, i) => (s, i)))
            {
                cells.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var label = new TextBlock { Text = ModelLadder.Label(step.Tokens), FontFamily = DisplayFont, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
                var memory = new TextBlock { Text = step.Memory, HorizontalAlignment = HorizontalAlignment.Center, Style = Token(muted ? "MasonCaptionSecondary" : ToneText(step.Tone)), FontWeight = Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.NoWrap };
                var body = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
                body.Children.Add(label); body.Children.Add(memory);
                var button = new Button { Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(2, 7, 2, 6), MinHeight = 44 };
                int at = index;
                button.Click += (_, _) => Select(steps[at].Tokens, true);
                button.KeyDown += (_, e) =>
                {
                    int move = e.Key switch { Windows.System.VirtualKey.Left => at - 1, Windows.System.VirtualKey.Right => at + 1, _ => -1 };
                    if (move < 0 || move >= buttons.Count) return;
                    Select(steps[move].Tokens, true); buttons[move].Focus(FocusState.Keyboard); e.Handled = true;
                };
                AutomationProperties.SetName(button, step.Name);
                Grid.SetColumn(button, index); cells.Children.Add(button); buttons.Add(button);
            }
            Tokens = 0;
            Select(steps.Any(s => s.Tokens == selected) ? selected : steps.LastOrDefault(s => s.Tokens <= selected).Tokens is > 0 and var below ? below : steps.FirstOrDefault().Tokens, false);
        }

        public void Select(int tokens, bool raise)
        {
            if (tokens == Tokens && raise) return;
            Tokens = tokens;
            foreach (var (button, i) in buttons.Select((b, i) => (b, i)))
            {
                bool on = steps[i].Tokens == tokens;
                button.Style = Token(on ? "MasonSegmentSelected" : "MasonSegmentItem");
                button.Padding = new Thickness(2, 7, 2, 6);
                AutomationProperties.SetItemStatus(button, on ? "Selected" : "");
            }
            if (raise) Changed?.Invoke(tokens);
        }
    }

    static string ToneText(FitTone tone) => tone switch { FitTone.Fits => "MasonPillTextSuccess", FitTone.Tight => "MasonPillTextCaution", FitTone.TooLarge => "MasonPillTextCritical", _ => "MasonCaptionSecondary" };
    static string ToneDot(FitTone tone) => tone switch { FitTone.Fits => "MasonDotSuccess", FitTone.Tight => "MasonDotCaution", FitTone.TooLarge => "MasonDotCritical", _ => "MasonDotNeutral" };
    static string ToneFill(FitTone tone) => tone switch { FitTone.Fits => "MasonFitFillSuccess", FitTone.Tight => "MasonFitFillCaution", FitTone.TooLarge => "MasonFitFillCritical", _ => "MasonFitFillNeutral" };

    /// <summary>A full-width action row for the model panel: glyph, label and an optional trailing note.</summary>
    static (Button Button, TextBlock Note) ActionRow(string glyph, string label, RoutedEventHandler handler)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = Glyph(glyph, 16); icon.Style = Token("MasonGlyphSecondary");
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var note = new TextBlock { Style = Token("MasonCaptionSecondary"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        Grid.SetColumn(text, 1); Grid.SetColumn(note, 2);
        grid.Children.Add(icon); grid.Children.Add(text); grid.Children.Add(note);
        var button = new Button { Content = grid, Style = Token("SubtleButtonStyle"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 40, Padding = new Thickness(10, 6, 10, 6) };
        button.Click += handler;
        AutomationProperties.SetName(button, label);
        return (button, note);
    }

    static string? RepositoryOf(LocalModel model) => ModelLadder.Repository(model);
    static string? QuantOf(LocalModel model) => ModelLadder.Quant(model);

    /// <summary>Every file a model occupies on disk: split GGUF parts, or the Transformers folder.</summary>
    static string[] ModelFiles(LocalModel model)
    {
        if (Directory.Exists(model.Path)) return [model.Path];
        var split = Regex.Match(Path.GetFileName(model.Path), @"^(?<stem>.+)-00001-of-(?<count>\d{5})\.gguf$", RegexOptions.IgnoreCase);
        if (!split.Success) return [model.Path];
        int count = int.Parse(split.Groups["count"].Value);
        return Enumerable.Range(1, count).Select(i => Path.Combine(Path.GetDirectoryName(model.Path) ?? "", $"{split.Groups["stem"].Value}-{i:00000}-of-{split.Groups["count"].Value}.gguf")).Where(File.Exists).ToArray();
    }

    async Task<bool> ConfirmRecycle(string name, string size)
    {
        if (dialogOpen) return false;
        dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                RequestedTheme = root.RequestedTheme,
                Title = "Move " + name + " to the Recycle Bin?",
                Content = new TextBlock { Text = size + " will be freed. Mason stops listing it. You can restore it from the Recycle Bin.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Move to Recycle Bin",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { dialogOpen = false; }
    }
}
