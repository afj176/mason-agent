using System.Data;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace Mason;

public sealed partial class MainWindow
{
    static Button ChatIcon(string name, string glyph, RoutedEventHandler action)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Padding = new Thickness(9), MinWidth = 34, MinHeight = 34 };
        button.Click += action; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); return button;
    }
    /// <summary>
    /// Appends new message containers so they slide in (Fluent.Live: add/delete and reposition transitions) without
    /// replaying the messages already shown. The existing children are laid out first, so only these count as added.
    /// The caller clears <c>ChildrenTransitions</c> before its next full rebuild.
    /// </summary>
    static void AppendMessages(Panel panel, params UIElement[] items)
    {
        panel.UpdateLayout();
        Fluent.Live(panel);
        foreach (var item in items) panel.Children.Add(item);
    }
    static void ChatCopy(string value) { var data = new DataPackage(); data.SetText(value); Clipboard.SetContent(data); }
    // Chat and Code share the brand surface so message cards, code blocks and composers match.
    static Border ChatSurface(UIElement content, Thickness? padding = null) => BrandSurface(content, padding);
    static RichTextBlock ChatInline(string value, double size = 15, bool code = false)
    {
        var view = new RichTextBlock { FontSize = size, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.55 };
        var paragraph = new Paragraph(); view.Blocks.Add(paragraph);
        if (code)
        {
            view.FontFamily = new FontFamily("Cascadia Code, Consolas");
            var pattern = @"(""(?:\\.|[^""\\])*""|'[^'\r\n]*'|\b(?:using|async|await|return|yield|for|foreach|if|else|var|int|string|public|private|class|void|new|const|let|function|def|import|from|SELECT|FROM|WHERE|true|false|null)\b|//[^\n]*|\b\d+(?:\.\d+)?\b)";
            int start = 0;
            foreach (Match match in Regex.Matches(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                if (match.Index > start) paragraph.Inlines.Add(new Run { Text = value[start..match.Index] });
                var run = new Run { Text = match.Value };
                void Color() { run.Foreground = new SolidColorBrush(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText) : view.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 195, 194, 255) : Windows.UI.Color.FromArgb(255, 72, 73, 168)); }
                view.ActualThemeChanged += (_, _) => Color(); view.Loaded += (_, _) => Color(); paragraph.Inlines.Add(run); start = match.Index + match.Length;
            }
            paragraph.Inlines.Add(new Run { Text = value[start..] }); return view;
        }
        int offset = 0;
        foreach (Match match in Regex.Matches(value, @"(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\(https?://[^\s)]+\))", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            if (match.Index > offset) paragraph.Inlines.Add(new Run { Text = value[offset..match.Index] });
            string token = match.Value;
            if (token.StartsWith("**")) { var bold = new Bold(); bold.Inlines.Add(new Run { Text = token[2..^2] }); paragraph.Inlines.Add(bold); }
            else if (token.StartsWith('`')) paragraph.Inlines.Add(new Run { Text = token[1..^1], FontFamily = new FontFamily("Cascadia Code, Consolas") });
            else
            {
                int end = token.IndexOf("](", StringComparison.Ordinal); var link = new Hyperlink { NavigateUri = new Uri(token[(end + 2)..^1]) }; link.Inlines.Add(new Run { Text = token[1..end] }); paragraph.Inlines.Add(link);
            }
            offset = match.Index + match.Length;
        }
        paragraph.Inlines.Add(new Run { Text = value[offset..] }); return view;
    }
    FrameworkElement ChatMarkdown(string markdown, Action<ChatArtifact>? preview = null)
    {
        var body = new StackPanel { Spacing = 13 };
        foreach (var block in ChatContent.Blocks(markdown))
        {
            if (block.Kind == "code")
            {
                var commands = Row(ChatIcon("Copy code", "\uE8C8", (_, _) => ChatCopy(block.Text)));
                if (preview != null)
                {
                    // HTML and code open as source in the reference sidebar, so "Preview" would promise a rendering that no longer exists.
                    var open = Button("Open in reference", (_, _) => preview(new(block.Language.Length == 0 ? "Code" : block.Language, block.Language switch { "html" or "chart" or "json" or "csv" or "tsv" => block.Language, "md" or "markdown" => "markdown", _ => "code" }, block.Text)));
                    ToolTipService.SetToolTip(open, "Open this block in the reference sidebar");
                    commands.Children.Add(open);
                }
                var head = Toolbar(Text(block.Language.Length == 0 ? "Code" : block.Language, 12), commands);
                var code = ChatInline(block.Text, 14, true);
                body.Children.Add(ChatSurface(Stack(head, code), new Thickness(12))); continue;
            }
            if (block.Kind == "table")
            {
                try
                {
                    var data = ChatContent.Table(block.Text, "table"); var grid = new Grid { RowSpacing = 1, ColumnSpacing = 1 };
                    for (int c = 0; c < data.Columns.Count; c++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
                    for (int r = 0; r <= Math.Min(12, data.Rows.Count); r++)
                    {
                        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                        for (int c = 0; c < data.Columns.Count; c++)
                        {
                            var text = ChatInline(r == 0 ? "**" + data.Columns[c].ColumnName + "**" : Convert.ToString(data.Rows[r - 1][c]) ?? "", 14);
                            var cell = ChatSurface(text, new Thickness(9)); cell.CornerRadius = new CornerRadius(0); Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell);
                        }
                    }
                    body.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
                    if (preview != null) body.Children.Add(Button("Open table / chart", (_, _) => preview(new("Table", "table", block.Text))));
                    continue;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { body.Children.Add(Text("Table preview unavailable: " + e.Message, 12)); }
            }
            double font = block.Kind switch { "h1" => 27, "h2" => 22, "h3" => 19, "h4" or "h5" or "h6" => 17, _ => 15 };
            var content = ChatInline(block.Text, font);
            if (block.Kind.StartsWith('h')) content.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (block.Kind == "quote") body.Children.Add(new Border { Child = content, BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = AccentBrush(), Padding = new Thickness(14, 5, 0, 5) });
            else body.Children.Add(content);
        }
        return body;
    }
    /// <summary>
    /// HTML in a reply is shown as its source. Mason has no embedded browser: "Open in browser" writes the
    /// document, wrapped in the same content policy (no scripts, no network, no forms, images only from data
    /// URIs), to a temporary file and hands it to the default browser.
    /// </summary>
    FrameworkElement ChatHtml(string content, Action<string> report)
    {
        var open = Button("Open in browser", async (_, _) =>
        {
            try
            {
                var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MasonAgent", "html-previews");
                System.IO.Directory.CreateDirectory(folder);
                var file = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + ".html");
                await System.IO.File.WriteAllTextAsync(file, ChatContent.IsolatedHtml(content, root.ActualTheme == ElementTheme.Dark));
                if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(file))) report("Windows could not open a browser for this file.");
            }
            catch (Exception error) { report("The HTML could not be opened: " + error.Message); }
        }, true);
        ToolTipService.SetToolTip(open, "Open this HTML in your default browser with scripts, network and forms blocked");
        var note = Text("Shown as source. Your browser opens it with scripts, network and forms blocked.", 12);
        note.Opacity = .8;
        var source = ChatInline(content, 13, true);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Row(open));
        panel.Children.Add(note);
        panel.Children.Add(source);
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
