using Mason.Code;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Mason;

/// <summary>
/// Window chrome: content drawn into the title bar (WinUI TitleBar with Mortar and the wordmark, a search across
/// pages, projects, sessions and chats, and the running model), Mica showing through the sidebar and caption area,
/// the rounded Windows 11 content edge, page transitions and window placement.
/// </summary>
public sealed partial class MainWindow
{
    TitleBar? titleBar;
    readonly AutoSuggestBox titleSearch = new() { PlaceholderText = "Search Mason (Ctrl+K)", QueryIcon = new SymbolIcon(Symbol.Find), MinWidth = 240, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    List<TitleHit> titleHits = [];
    readonly Button titleModel = new() { Padding = new Thickness(10, 4, 12, 4), MinHeight = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), BorderThickness = new Thickness(1) };
    readonly DispatcherTimer titleModelTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    string titleModelShown = "";

    // Hooks the Code and Chat pages fill in, so the title bar search can open their items.
    Action<CodeSession>? openCodeSession;
    Action<CodeProject>? openCodeProject;
    Func<Task>? addCodeProject;
    Action<ChatConversation>? openChatConversation;
    Action? newNativeChat;

    [Microsoft.UI.Xaml.Data.Bindable]
    sealed class TitleHit(string glyph, string label, string kind, Action run)
    {
        public string Glyph => glyph;
        public string Label => label;
        public string Kind => kind;
        public Action Run => run;
        // Suggestions are announced by ToString.
        public override string ToString() => label + ", " + kind;
    }

    /// <summary>
    /// Extends the window into the caption area and puts a TitleBar there: workspace switcher, Mortar and the wordmark,
    /// the search in the middle and the running model on the right. The caption buttons stay the system's own.
    /// </summary>
    FrameworkElement BuildTitleBar(FrameworkElement brand)
    {
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar = new TitleBar
        {
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = false,
            LeftHeader = Row(brand, BuildRouteSwitcher()),
            Content = BrandControl(titleSearch),
            RightHeader = titleModel,
            Height = 48
        };
        SetTitleBar(titleBar);

        AutomationProperties.SetName(titleSearch, "Search Mason");
        ToolTipService.SetToolTip(titleSearch, "Search projects, sessions, files, commands and chats (Ctrl+K)");
        titleSearch.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid ColumnSpacing='12' Padding='0,6'><Grid.ColumnDefinitions><ColumnDefinition Width='20'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
            "<FontIcon Glyph='{Binding Glyph}' FontSize='14' Opacity='.8'/><TextBlock Grid.Column='1' Text='{Binding Label}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/><TextBlock Grid.Column='2' Text='{Binding Kind}' FontSize='12' Opacity='.6' VerticalAlignment='Center'/></Grid></DataTemplate>");
        // Open the list once focus has settled; the box closes it again while it is still taking focus.
        // Open the full list when the text field itself takes focus; focus moving onto a suggestion must not reopen it.
        titleSearch.GotFocus += (_, e) => { if (e.OriginalSource is not TextBox || titleSearchClosing) return; titleHits = TitleHits(); FilterTitleSearch(); DispatcherQueue.TryEnqueue(() => titleSearch.IsSuggestionListOpen = true); };
        titleSearch.TextChanged += (_, e) => { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) FilterTitleSearch(); };
        titleSearch.SuggestionChosen += (_, _) => { /* keep the typed text; QuerySubmitted runs the choice */ };
        titleSearch.QuerySubmitted += (_, e) =>
        {
            var hit = e.ChosenSuggestion as TitleHit ?? (titleSearch.ItemsSource as IEnumerable<TitleHit>)?.FirstOrDefault();
            if (hit == null) return;
            CloseTitleSearch();
            hit.Run();
        };

        BuildTitleModel();
        return titleBar;
    }

    /// <summary>Everything the title bar search can open: pages, commands, projects, recent sessions and chats.</summary>
    List<TitleHit> TitleHits()
    {
        var hits = new List<TitleHit>
        {
            new("", "Code", "Page", () => Navigate("Code")),
            new("", "Chat", "Page", () => Navigate("Chat")),
            new("", "Models", "Page", () => Navigate("Models")),
            new("", "Connections", "Page", () => Navigate("AI connections")),
            new("", "New chat", "Command", () => { Navigate("Chat"); newNativeChat?.Invoke(); }),
            new("", "Add project", "Command", () => { Navigate("Code"); _ = addCodeProject?.Invoke(); })
        };
        if (codeStore is { } store)
        {
            foreach (var project in store.Projects)
                hits.Add(new("", ProjectLabel(project, store.Projects), "Project", () => openCodeProject?.Invoke(project)));
            foreach (var session in store.Sessions.OrderByDescending(s => s.Updated))
            {
                var owner = store.ProjectFor(session) is {} project ? ProjectLabel(project, store.Projects) : System.IO.Path.GetFileName(session.Workspace);
                hits.Add(new("", session.Title + " · " + owner, "Session", () => openCodeSession?.Invoke(session)));
            }
        }
        if (nativeChatLibrary is { } chats)
            foreach (var chat in chats.State.Conversations.OrderByDescending(c => c.Updated).Take(60))
                hits.Add(new("", chat.Title, "Chat", () => openChatConversation?.Invoke(chat)));
        if (codeSearchHits != null) hits.AddRange(codeSearchHits());
        return hits;
    }

    bool titleSearchClosing;

    /// <summary>
    /// Clears and closes the title bar search and hands focus back to the workspace switcher, so the
    /// list cannot linger and a command that opens a dialog does not fight the popup.
    /// </summary>
    void CloseTitleSearch()
    {
        titleSearchClosing = true;
        titleSearch.Text = ""; titleSearch.ItemsSource = null; titleSearch.IsSuggestionListOpen = false;
        routeSwitcher.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() => { titleSearch.IsSuggestionListOpen = false; titleSearchClosing = false; });
    }

    void FilterTitleSearch()
    {
        var text = titleSearch.Text.Trim();
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        titleSearch.ItemsSource = titleHits.Where(h => terms.All(word => h.Label.Contains(word, StringComparison.OrdinalIgnoreCase) || h.Kind.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(h => text.Length > 0 && h.Label.Equals(text, StringComparison.OrdinalIgnoreCase))
            .Take(40).ToList();
    }

    /// <summary>Ctrl+K lands in the title bar search.</summary>
    void FocusTitleSearch() => titleSearch.Focus(FocusState.Keyboard);

    /// <summary>
    /// The model Mason is serving rides in the title bar, so it stays in view on every page. It is a live
    /// observation of the managed servers, and a way to the Models page where they are started and stopped.
    /// </summary>
    void BuildTitleModel()
    {
        AutomationProperties.SetName(titleModel, "Running models");
        titleModel.Click += (_, _) => Navigate("Models");
        void Theme()
        {
            if (BrandHighContrast) { titleModel.ClearValue(Control.BackgroundProperty); titleModel.ClearValue(Control.BorderBrushProperty); titleModelShown = ""; UpdateTitleModel(); return; }
            bool dark = root.ActualTheme == ElementTheme.Dark;
            titleModel.Background = new SolidColorBrush(dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255));
            titleModel.BorderBrush = new SolidColorBrush(BrandLine(dark));
            titleModelShown = ""; UpdateTitleModel();
        }
        root.ActualThemeChanged += (_, _) => Theme();
        Theme();
        titleModelTimer.Tick += (_, _) => UpdateTitleModel();
        titleModelTimer.Start();
        Closed += (_, _) => titleModelTimer.Stop();
    }

    void UpdateTitleModel()
    {
        var live = modelServing.Running.Where(s => s.Owner.IsRunning).ToArray();
        string text = live.Length switch { 0 => "No model running", 1 => live[0].Profile.DisplayName, _ => $"{live.Length} models running" };
        string key = text + "|" + root.ActualTheme;
        if (key == titleModelShown) return;
        titleModelShown = key;
        bool dark = root.ActualTheme == ElementTheme.Dark;
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        if (BrandHighContrast) { dot.Fill = new SolidColorBrush(brandSystemColors.UIElementColor(live.Length > 0 ? UIElementType.Highlight : UIElementType.GrayText)); }
        else if (live.Length > 0) dot.Fill = new SolidColorBrush(BrandAccentColor(dark));
        else { dot.Stroke = new SolidColorBrush(dark ? BrandSilver : BrandMuted); dot.StrokeThickness = 1.2; }
        var label = new TextBlock { Text = text, FontSize = 12, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (live.Length == 0 && !BrandHighContrast) label.Foreground = new SolidColorBrush(dark ? BrandSilver : BrandMuted);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(dot); content.Children.Add(label);
        titleModel.Content = content;
        ToolTipService.SetToolTip(titleModel, live.Length == 0 ? "No managed model is running. Select to open Models." : string.Join("\n", live.Select(s => s.Profile.DisplayName + " · " + s.Profile.Endpoint)) + "\nSelect to open Models.");
        AutomationProperties.SetName(titleModel, live.Length == 0 ? "No model running. Open Models" : text + " running. Open Models");
        Fluent.Appear(content, 4);
    }

    /// <summary>
    /// Lets Mica show through the sidebar and title bar while the page keeps the brand paper and night surfaces.
    /// High contrast keeps solid system colours and the system caption buttons.
    /// </summary>
    void ApplyWindowMaterial(bool dark)
    {
        var chrome = AppWindow.TitleBar;
        if (BrandHighContrast)
        {
            root.Background = new SolidColorBrush(brandSystemColors.GetColorValue(UIColorType.Background));
            chrome.ButtonBackgroundColor = chrome.ButtonInactiveBackgroundColor = null;
            chrome.ButtonForegroundColor = chrome.ButtonInactiveForegroundColor = chrome.ButtonHoverBackgroundColor = chrome.ButtonHoverForegroundColor = null;
            chrome.ButtonPressedBackgroundColor = chrome.ButtonPressedForegroundColor = null;
            return;
        }
        root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var ink = dark ? BrandPaper : BrandInk;
        chrome.ButtonBackgroundColor = chrome.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        chrome.ButtonForegroundColor = chrome.ButtonHoverForegroundColor = chrome.ButtonPressedForegroundColor = ink;
        chrome.ButtonInactiveForegroundColor = dark ? BrandSilver : BrandMuted;
        chrome.ButtonHoverBackgroundColor = dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        chrome.ButtonPressedBackgroundColor = dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0);
    }

    /// <summary>
    /// The sidebar is clear so Mica shows through; the page area keeps the brand paper (night in dark) with the
    /// Windows 11 content edge: a hairline and a rounded top-left corner where it meets the sidebar.
    /// </summary>
    void ApplyShellSurface()
    {
        // BrandControl(nav) created these dictionaries; NavigationView reads its pane and content brushes from them.
        foreach (var (theme, dark) in new[] { ("Light", false), ("Default", true) })
        {
            var dictionary = (ResourceDictionary)nav.Resources.ThemeDictionaries[theme];
            dictionary["NavigationViewDefaultPaneBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            // The pane that slides over the page in the narrow icon-rail layout keeps a material of its own.
            var surface = dark ? BrandNight : BrandPaper;
            dictionary["NavigationViewExpandedPaneBackground"] = new AcrylicBrush { TintColor = surface, TintOpacity = .82, TintLuminosityOpacity = .9, FallbackColor = surface };
            dictionary["NavigationViewContentBackground"] = new SolidColorBrush(surface);
            dictionary["NavigationViewContentGridBorderBrush"] = new SolidColorBrush(BrandLine(dark));
        }
        nav.Resources["NavigationViewContentGridBorderThickness"] = new Thickness(1, 1, 0, 0);
        nav.Resources["NavigationViewContentGridCornerRadius"] = new CornerRadius(8, 0, 0, 0);
    }

    /// <summary>Which way a page enters: moving down the navigation it rises from below, moving up it settles from above.</summary>
    Fluent.PageMotion MotionFor(string from, string to)
    {
        var order = new List<string> { "Code", "Chat", "Models and connections" };
        int a = order.IndexOf(from), b = order.IndexOf(to);
        return a >= 0 && b >= 0 && b < a ? Fluent.PageMotion.EntranceFromAbove : Fluent.PageMotion.Entrance;
    }

    // ── Window placement ────────────────────────────────────────────────────────────────────────────

    WindowPlacement? placement;

    /// <summary>Reopens the window where it was, at its size, maximized if it was; centred on first run.</summary>
    void RestorePlacement()
    {
        var saved = preferences.Window;
        try
        {
            if (saved is { Width: > 400, Height: > 300 })
            {
                var rect = new Windows.Graphics.RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
                // Only restore onto a display that still exists; otherwise fall back to the primary one.
                if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) != null)
                {
                    AppWindow.MoveAndResize(rect);
                    if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
                    placement = saved; return;
                }
            }
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min(1640, work.Width - 80), height = Math.Min(1060, work.Height - 60);
            AppWindow.MoveAndResize(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        }
        catch { /* placement is a convenience */ }
    }

    /// <summary>Follows moves and resizes, and saves the last placement with the appearance preferences on close.</summary>
    void TrackPlacement()
    {
        AppWindow.Changed += (sender, args) =>
        {
            if (!(args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)) return;
            bool maximized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            bool minimized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            if (minimized) return;
            // While maximized keep the restored bounds, so un-maximizing next time lands in the old place.
            placement = maximized && placement != null
                ? placement with { Maximized = true }
                : new WindowPlacement(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height, maximized);
        };
        Closed += (_, _) =>
        {
            if (placement == null || placement == preferences.Window) return;
            try { preferences = preferences with { Window = placement }; Store.Write("appearance.json", preferences); }
            catch { /* placement is a convenience; the theme choice was saved when it was made */ }
        };
    }
}
