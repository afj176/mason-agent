using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Mason;

public sealed partial class MainWindow
{
    sealed class CodeViewerEntry : IDisposable
    {
        public required TabViewItem Tab;
        public Action? Refresh;
        public Action? ReleaseTheme;
        public Action? ReleaseBrowser;
        public CancellationTokenSource Cancellation { get; } = new();
        public void Dispose() { ReleaseTheme?.Invoke();ReleaseTheme=null;Cancellation.Cancel(); ReleaseBrowser?.Invoke(); ReleaseBrowser=null; Cancellation.Dispose(); }
    }
    CodeViewerEntry BuildCodeViewer(CodeSession session, CodeTab model, Action save, Action refresh, Action<string, string> open, Action reviewChanged, Func<Task>? sendReview = null, Action<CodeSession, DeveloperEvidence>? attach = null)
    {
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var entry = new CodeViewerEntry { Tab = new TabViewItem { Header = TabLabel(model.Title), IconSource = new FontIconSource { Glyph = model.Kind switch { "web" or "site" => "\uE774", "context" => "\uE9D9", "git" => "\uE8A5", "changes" => "\uE8A5", "activity" => "\uE9D9", _ => "\uE8A5" } }, Tag = model, Content = host, IsClosable = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(entry.Tab, model.Title);
        // TabView retains detached content. Explicitly follow the shared app theme
        // so cached document surfaces and token colors update on a live theme switch.
        void Theme(FrameworkElement sender,object args){host.RequestedTheme=root.ActualTheme;entry.Tab.RequestedTheme=root.ActualTheme;}
        root.ActualThemeChanged+=Theme;entry.ReleaseTheme=()=>root.ActualThemeChanged-=Theme;Theme(root,EventArgs.Empty);
        try
        {
            var scope = new CodeWorkspace(session.Workspace);
            if(model.Kind=="delivery"){host.Content=DeliveryView(session,scope,entry,attach);return entry;}
            if (model.Kind == "harness") { host.Content=HarnessSettings(session,entry);return entry; }
            if (model.Kind == "experiment") { host.Content = ExperimentReview(session,scope,entry); return entry; }
            if (model.Kind == "roles") { host.Content = ModelRolesView(session,entry); return entry; }
            if (model.Kind == "tasks") { host.Content = BackgroundTasksView(session,entry); return entry; }
            if (model.Kind == "plan") { host.Content = BuildMilestoneViewer(session, scope, entry, open); return entry; }
            if (model.Kind == "context") { host.Content = CodeContextViewer(session, entry, save, open); return entry; }
            if (model.Kind == "git") { host.Content = CodeGitViewer(session, entry, open); return entry; }
            if (model.Kind is "web" or "site") { BuildCodeBrowser(entry, host, model, scope, save, open, session, attach); return entry; }
            if (model.Kind == "changes")
            {
                string revision = "", selected = "";
                entry.Refresh = () =>
                {
                    var next = string.Join('|', session.Changes.Select(c => c.Path + c.AfterHash + c.Restored)); if (revision == next && host.Content != null) return; revision = next;
                    var changes = session.Changes.Where(c => !c.Restored).ToArray();
                    if (changes.Length == 0) { TabTitle(entry.Tab, "Changes"); host.Content = CodeScroll(new Border { Child = Stack(Text("No session changes", 23), CodeNote("Changes made by this session appear here. Existing workspace edits are preserved and are not included in this list.")), Padding = new Thickness(24) }); return; }
                    TabTitle(entry.Tab, $"Changes ({changes.Length})");
                    var labels = changes.ToDictionary(c => c.Path, c => { var lines = CodeProtocol.Diff(c.Before, c.After).ToArray(); return c.Path + $"    +{lines.Count(l => l.Kind == '+')}  −{lines.Count(l => l.Kind == '-')}"; });
                    var choices = new ListView { IsItemClickEnabled = true, MaxHeight = 150, SelectionMode = ListViewSelectionMode.Single, ItemsSource = changes.Select(c => labels[c.Path]).ToArray() }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choices, "Files changed by this coding session");
                    var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                    var changesView = Vertical(Stack(Heading(sendReview == null ? "Session changes" : "Review changes", sendReview == null ? 19 : 27), choices), content);
                    changesView.Padding = new Thickness(14, 12, 14, 0);
                    host.Content = changesView;
                    void Show(string path)
                    {
                        selected = path; var change = changes.First(c => c.Path == path); var rows = new ListView { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rows, "Diff lines");
                        ScrollViewer.SetHorizontalScrollMode(rows, ScrollMode.Enabled);
                        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Auto);
                        bool IsCurrent()
                        {
                            try { return scope.Read(path).Sha256 == change.AfterHash; }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
                        }
                        async Task Comment(CodeDiffLine line, FrameworkElement anchor)
                        {
                            if (dialogOpen || line.Kind == '@') return;
                            var pending = CodeReviewComment.OnDiff(change, line, "");
                            if (sendReview != null && IsCurrent())
                                OpenLineComment(anchor, session, pending, reviewChanged, sendReview);
                            else await EditReviewComment(session, pending, true, reviewChanged);
                        }
                        rows.KeyDown += async (_, e) =>
                        {
                            if (e.Key == VirtualKey.Enter && rows.SelectedItem is ListViewItem { Tag: CodeDiffLine line } selectedLine)
                            { e.Handled = true; await Comment(line, selectedLine); }
                        };
                        model.Location = path;
                        bool current = IsCurrent();
                        var caption = CodeNote(current ? "Changes against this session's original file snapshot" : "Saved session snapshot · the file has changed or is unavailable. Later edits will not be overwritten.");
                        var mode = new ComboBox { ItemsSource = new[] { "Unified diff", "Split diff" }, SelectedIndex = model.ViewMode == "split" ? 1 : 0, MinWidth = 120 };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mode, "Diff layout");
                        void RenderDiff()
                        {
                            rows.Items.Clear(); int count = 0;
                            foreach (var line in CodeProtocol.Diff(change.Before, change.After))
                            {
                                if (++count > 800) { rows.Items.Add(CodeNote("Preview capped at 800 lines. Open the complete file in your editor.")); break; }
                                var text = CodeSource(line.Kind == '@' ? "⋯ " + line.Text : $"{line.Before?.ToString() ?? "",5} {line.After?.ToString() ?? "",5} {line.Kind}  {line.Text}", 13);
                                text.TextWrapping = TextWrapping.NoWrap; text.Padding = new Thickness(8, 1, 8, 1);
                                var row = new Border { Child = text };
                                if (mode.SelectedIndex == 1 && line.Kind != '@')
                                {
                                    var split = new Grid { ColumnSpacing = 12 };
                                    split.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); split.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                                    foreach (int side in new[] { 0, 1 })
                                    {
                                        var value = side == 0 && line.Kind != '+' || side == 1 && line.Kind != '-' ? $"{(side == 0 ? line.Before : line.After),5}  {line.Text}" : "";
                                        var label = CodeSource(value, 13); label.TextWrapping = TextWrapping.Wrap; label.Padding = new Thickness(8, 1, 8, 1); Grid.SetColumn(label, side); split.Children.Add(label);
                                    }
                                    row.Child = split;
                                }
                                void Theme()
                                {
                                    bool high = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
                                    row.Background = high || line.Kind is ' ' or '@' ? null : ModelTones.FillBrush(line.Kind == '+' ? Tone.Good : Tone.Bad, row.ActualTheme == ElementTheme.Dark);
                                }
                                row.Loaded += (_, _) => Theme(); row.ActualThemeChanged += (_, _) => Theme();
                                var grid = new Grid { ColumnSpacing = 4 };
                                grid.ColumnDefinitions.Add(new() { Width = new GridLength(30) });
                                grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                                Grid.SetColumn(row, 1); grid.Children.Add(row);
                                string location = (line.After.HasValue ? "updated line " : "original line ") + (line.After ?? line.Before);
                                if (line.Kind != '@')
                                {
                                    var add = ChatIcon("Comment on " + location, "\uE710", async (sender, _) => await Comment(line, (FrameworkElement)sender));
                                    add.Padding = new Thickness(0); add.MinHeight = 22; add.MinWidth = 24; add.IsTabStop = false;
                                    ToolTipService.SetToolTip(add, "Comment on " + location);
                                    grid.Children.Add(add);
                                }
                                var item = new ListViewItem { Content = grid, Tag = line, MinHeight = 24, Padding = new Thickness(0),
                                    HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = line.Kind != '@' };
                                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, location + " · " + line.Text);
                                rows.Items.Add(item);
                            }
                        }
                        mode.SelectionChanged += (_, _) => { model.ViewMode = mode.SelectedIndex == 1 ? "split" : "unified"; RenderDiff(); save(); }; RenderDiff();
                        rows.Loaded += (_, _) =>
                        {
                            ScrollViewer? FindScroll(DependencyObject node)
                            {
                                if (node is ScrollViewer viewer) return viewer;
                                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                                    if (FindScroll(VisualTreeHelper.GetChild(node, i)) is {} child) return child;
                                return null;
                            }
                            if (FindScroll(rows) is {} scroll)
                            {
                                scroll.ChangeView(null, model.Scroll, null, true);
                                scroll.ViewChanged += (_, _) => model.Scroll = scroll.VerticalOffset;
                            }
                        };
                        var external = CodeLink("Open in editor", async (_, _) => { try { await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(scope.Resolve(path)), new LauncherOptions { DisplayApplicationPicker = true }); } catch (Exception error) { caption.Text = error.Message; } });
                        var restore = CodeLink("Restore original", async (_, _) =>
                        {
                            if (codeOperation != null || dialogOpen) { caption.Text = "Stop the coding run before restoring files."; return; }
                            dialogOpen = true;
                            try
                            {
                                if (await new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Restore the session's original file?", Content = path + " will be restored only if its bytes still match this session's last edit. Later edits are protected.", PrimaryButtonText = "Restore", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close }.ShowAsync() != ContentDialogResult.Primary) return;
                                scope.Restore(change); restoredAt = DateTimeOffset.Now; refreshStatusPose?.Invoke(); revision = ""; entry.Refresh?.Invoke(); refresh(); // refresh also updates the toolbar's change count
                            }
                            catch (Exception error) { caption.Text = error.Message; }
                            finally { dialogOpen = false; }
                        });
                        restore.IsEnabled = current && change.Before != null;
                        content.Content = Vertical(Stack(Toolbar(Text(path, 14), Row(mode, external, restore)), caption), rows);
                    }
                    choices.ItemClick += (_, args) => Show(labels.First(p => p.Value == (string)args.ClickedItem).Key);
                    selected = changes.FirstOrDefault(c => c.Path == (selected.Length > 0 ? selected : model.Location))?.Path ?? changes[0].Path;
                    choices.SelectedItem = labels[selected]; Show(selected);
                };
                entry.Refresh(); return entry;
            }
            if (model.Kind == "activity") { BuildLiveViewer(session, model, entry, host, save, open); return entry; }
            if (model.Kind == "answer") { host.Content = CodeDocumentScroll(CodeMarkdown(model.Target), model); return entry; }
            var path = scope.Resolve(model.Target); var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".pdf") { BuildCodePdf(entry, host, path); return entry; }
            if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif")
            {
                if (new FileInfo(path).Length > 10 * 1024 * 1024) throw new ArgumentException("Image preview is limited to 10 MiB.");
                host.Content = CodeDocumentScroll(new Image { Source = new BitmapImage(new Uri(path)), Stretch = Stretch.Uniform }, model); return entry;
            }
            string text;
            if (extension == ".docx")
            {
                if (new FileInfo(path).Length > 10 * 1024 * 1024) throw new ArgumentException("Word preview is limited to 10 MiB.");
                using var zip = ZipFile.OpenRead(path); var document = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("The document has no Word body.");
                if (document.Length > 2 * 1024 * 1024) throw new ArgumentException("Document text exceeds 2 MiB.");
                using var stream = document.Open(); using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                var xml = XDocument.Load(reader); XNamespace ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                text = string.Join("\n\n", xml.Descendants(ns + "p").Select(p => string.Concat(p.Descendants(ns + "t").Select(t => t.Value))));
            }
            else text = scope.Read(model.Target).Content;
            var contentHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            bool previewSelected = !(model.ViewMode == "source" || model.ViewMode.Length == 0 && extension is not (".md" or ".markdown" or ".docx"));
            var previewMode = SegmentToggle(new ToggleButton { Content = "Preview", MinWidth = 72 });
            var sourceMode = SegmentToggle(new ToggleButton { Content = "Source", MinWidth = 72 });
            var modeButtons = Row(previewMode, sourceMode); modeButtons.Spacing = 0;
            modeButtons.Spacing = 2; var mode = new Border { Child = modeButtons, Style = Token("MasonSegmentTrack") };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mode, "Document preview or source");
            var note = CodeNote(extension == ".docx" ? "Word text preview · original layout and images are not reproduced" : model.Target);
            var find = new TextBox { PlaceholderText = "Find in source…", Width = 175, MaxLength = 200 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(find, "Find in document source");
            var matchCount = CodeNote(""); int matchIndex = 0; var matches = new List<int>(); RichEditBox? searchSource = null;
            string SearchText() => text.Replace("\r\n", "\r").Replace('\n', '\r');
            void Match(int step)
            {
                matches.Clear();
                var sourceText = SearchText();
                for (int at = 0; find.Text.Length > 0 && at <= sourceText.Length - find.Text.Length && matches.Count < 2000;)
                { at = sourceText.IndexOf(find.Text, at, StringComparison.OrdinalIgnoreCase); if (at < 0) break; matches.Add(at); at += find.Text.Length; }
                matchIndex = matches.Count == 0 ? 0 : (matchIndex + step + matches.Count) % matches.Count;
                matchCount.Text = matches.Count == 0 ? "No matches" : $"{matchIndex + 1} / {matches.Count}" + (matches.Count == 2000 ? "+" : "");
                if (searchSource?.IsLoaded == true && matches.Count > 0)
                {
                    try { searchSource.Document.Selection.SetRange(matches[matchIndex], matches[matchIndex] + find.Text.Length); searchSource.Document.Selection.ScrollIntoView(Microsoft.UI.Text.PointOptions.Start); }
                    catch (Exception error) { matchCount.Text = "Selection unavailable · " + error.Message; }
                }
            }
            var previousMatch = ChatIcon("Previous document match", "\uE70E", (_, _) => { Match(-1); searchSource?.Focus(FocusState.Programmatic); });
            var nextMatch = ChatIcon("Next document match", "\uE70D", (_, _) => { Match(1); searchSource?.Focus(FocusState.Programmatic); });
            var findRow = Row(find, previousMatch, nextMatch, matchCount); findRow.Visibility = Visibility.Collapsed;
            var findButton = ChatIcon("Find in document source", "\uE721", (_, _) => { findRow.Visibility = findRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; RenderDocument(); if (findRow.Visibility == Visibility.Visible) find.Focus(FocusState.Programmatic); });
            void RenderDocument()
            {
                searchSource = null;
                if (findRow.Visibility == Visibility.Visible && find.Text.Length > 0)
                {
                    // Search presents selectable source text; it never edits the workspace.
                    searchSource = new RichEditBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 13, Padding = new Thickness(20), BorderThickness = new Thickness(0) };
                    searchSource.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, SearchText());
                    searchSource.IsReadOnly = true; searchSource.Loaded += (_, _) => Match(0);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(searchSource, "Document source search results");
                    ScrollViewer.SetHorizontalScrollBarVisibility(searchSource, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(searchSource, ScrollBarVisibility.Auto);
                    contentHost.Content = searchSource; Match(0);
                }
                else if (previewSelected) contentHost.Content = CodeDocumentScroll(CodeMarkdown(text, document: true), model);
                else
                {
                    var source = CodeSource(text, 13); source.TextWrapping = TextWrapping.NoWrap;
                    var scroll = CodeDocumentScroll(source, model); scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; contentHost.Content = scroll;
                }
            }
            void ChooseMode(bool preview)
            {
                previewSelected = preview; previewMode.IsChecked = preview; sourceMode.IsChecked = !preview;
                if (preview) findRow.Visibility = Visibility.Collapsed;
                model.ViewMode = preview ? "preview" : "source"; RenderDocument(); save();
            }
            previewMode.Click += (_, _) => ChooseMode(true); sourceMode.Click += (_, _) => ChooseMode(false);
            previewMode.IsChecked = previewSelected; sourceMode.IsChecked = !previewSelected;
            find.TextChanged += (_, _) => { matchIndex = 0; if (find.Text.Length > 0 && previewSelected) ChooseMode(false); else RenderDocument(); };
            find.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Match(1); searchSource?.Focus(FocusState.Programmatic); } };
            RenderDocument();
            string lastHash = session.Changes.LastOrDefault(c => c.Path == model.Target)?.AfterHash ?? "";
            entry.Refresh = () => { var hash = session.Changes.LastOrDefault(c => c.Path == model.Target)?.AfterHash ?? ""; if (hash != lastHash && extension != ".docx") { try { text = scope.Read(model.Target).Content; lastHash = hash; RenderDocument(); } catch (IOException error) { note.Text = error.Message; } } };
            var externalFile = ChatIcon("Open document externally", "\uE8A7", async (_, _) => { try { await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(scope.Resolve(model.Target)), new LauncherOptions { DisplayApplicationPicker = true }); } catch (Exception error) { note.Text = error.Message; } });
            var reload = ChatIcon("Reload document", "\uE72C", (_, _) => { try { if (extension != ".docx") text = scope.Read(model.Target).Content; RenderDocument(); } catch (Exception error) { note.Text = error.Message; } });
            note.MaxLines = 1; note.TextTrimming = TextTrimming.CharacterEllipsis;
            var tools = Row(mode, findButton, reload, externalFile); tools.Spacing = 4;
            var documentToolbar = Toolbar(note, tools); documentToolbar.Margin = new Thickness(12, 3, 12, 3);
            host.Content = Vertical(Stack(documentToolbar, findRow), contentHost);
        }
        catch (Exception error) { host.Content = TroubleNotice("Preview unavailable", error.Message); }
        return entry;
    }
    /// <summary>A plain string header sits at the top of the tab; a centred text block lines up with the icon and close button.</summary>
    // The header is a TextBlock, so the tab needs its own accessible name.
    static void TabTitle(TabViewItem tab, string text) { tab.Header = TabLabel(text); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab, text); }
    static TextBlock TabLabel(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };

    static ScrollViewer CodeDocumentScroll(FrameworkElement content, CodeTab tab)
    {
        var scroll = CodeScroll(new Border { Child = content, Padding = new Thickness(24) });
        scroll.Loaded += (_, _) => scroll.ChangeView(null, tab.Scroll, null, true); scroll.ViewChanged += (_, _) => tab.Scroll = scroll.VerticalOffset; return scroll;
    }
    void BuildCodePdf(CodeViewerEntry entry, ContentControl host, string path)
    {
        if (new FileInfo(path).Length > 10 * 1024 * 1024) throw new ArgumentException("PDF preview is limited to 10 MiB.");
        Windows.Data.Pdf.PdfDocument? document = null; uint page = 0; bool busy = false, started = false;
        var image = new Image { Stretch = Stretch.Uniform }; var caption = CodeNote("Loading PDF…");
        Button previous = null!, next = null!;
        previous = Button("Previous", async (_, _) => { if (page > 0) { page--; await Render(); } });
        next = Button("Next", async (_, _) => { if (document != null && page + 1 < document.PageCount) { page++; await Render(); } });
        host.Content = Vertical(Toolbar(caption, Row(previous, next)), CodeScroll(image));
        image.Loaded += async (_, _) =>
        {
            if (started) return; started = true;
            try { document = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path)); await Render(); }
            catch (Exception error) { caption.Text = "PDF preview unavailable · " + error.Message; }
        };
        async Task Render()
        {
            if (document == null || busy || entry.Cancellation.IsCancellationRequested) return; busy = true; previous.IsEnabled = next.IsEnabled = false;
            try
            {
                using var pdfPage = document.GetPage(page); using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await pdfPage.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1400 });
                if (entry.Cancellation.IsCancellationRequested) return;
                var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); image.Source = bitmap; caption.Text = $"Page {page + 1} / {document.PageCount}";
            }
            catch (Exception error) { caption.Text = error.Message; }
            finally { busy = false; previous.IsEnabled = page > 0; next.IsEnabled = document != null && page + 1 < document.PageCount; }
        }
    }
}

