using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mason;

public sealed partial class MainWindow
{
    static readonly FontFamily CodeFont = new("Segoe UI Variable, Segoe UI");
    FrameworkElement CodeMarkdown(string value, Action<ChatArtifact>? preview = null, bool document = false)
    {
        var body = ChatMarkdown(value, preview);
        var codeBlocks = new Queue<string>(ChatContent.Blocks(value).Where(b => b.Kind == "code").Select(b => b.Text));
        if (body is StackPanel stack)
            foreach (var border in stack.Children.OfType<Border>())
                if (border.Child is StackPanel block && block.Children.LastOrDefault() is RichTextBlock code && code.FontFamily.Source.Contains("Cascadia", StringComparison.Ordinal) && codeBlocks.Count > 0)
                {
                    var source = CodeSource(codeBlocks.Dequeue(), 14); source.TextWrapping = TextWrapping.Wrap;
                    block.Children.Remove(code); block.Children.Add(source);
                }
        void Style(DependencyObject item)
        {
            if (item is RichTextBlock text)
            {
                text.FontFamily = text.FontFamily.Source.Contains("Cascadia", StringComparison.Ordinal) ? text.FontFamily : CodeFont;
                if (text.FontSize == 15) text.FontSize = 16;
                if (document) text.FontSize = text.FontSize switch { 27 => 32, 22 => 25, 19 => 21, _ => text.FontSize };
                text.LineHeight = text.FontSize * 1.45;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) Style(VisualTreeHelper.GetChild(item, i));
        }
        body.Loaded += (_, _) => Style(body); return body;
    }
    static RichTextBlock CodeSource(string value, double size = 13)
    {
        var text = new RichTextBlock { FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = size, LineHeight = size * 1.5, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap };
        var paragraph = new Paragraph(); text.Blocks.Add(paragraph);
        const string pattern = "(\"(?:\\\\.|[^\"\\\\])*\"|'[^'\\r\\n]*'|//[^\\n]*|#[^\\n]*|\\b(?:using|async|await|return|yield|for|foreach|if|else|var|int|string|public|private|class|void|new|const|let|function|def|import|from|raise|with|try|except|True|False|None|null|true|false)\\b|\\b\\d+(?:\\.\\d+)?\\b)";
        int offset = 0;
        foreach (Match match in Regex.Matches(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            if (match.Index > offset) paragraph.Inlines.Add(new Run { Text = value[offset..match.Index] });
            var run = new Run { Text = match.Value }; paragraph.Inlines.Add(run);
            void Theme()
            {
                if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { run.ClearValue(TextElement.ForegroundProperty); return; }
                bool dark = text.ActualTheme == ElementTheme.Dark; string token = match.Value;
                var color = token.StartsWith('#') || token.StartsWith("//") ? dark ? "8DA2A9" : "61717B" : token.StartsWith('"') || token.StartsWith('\'') ? dark ? "E2B995" : "9E481A" : char.IsDigit(token[0]) ? dark ? "BBA6E3" : "7753A1" : dark ? "62CFF1" : "006D92";
                run.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, Convert.ToByte(color[..2], 16), Convert.ToByte(color[2..4], 16), Convert.ToByte(color[4..], 16)));
            }
            text.Loaded += (_, _) => Theme(); text.ActualThemeChanged += (_, _) => Theme(); offset = match.Index + match.Length;
        }
        paragraph.Inlines.Add(new Run { Text = value[offset..] }); return text;
    }
}
