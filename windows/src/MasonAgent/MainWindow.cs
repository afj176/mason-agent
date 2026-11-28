using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace Mason;

public sealed partial class MainWindow : Window
{
    readonly SelectorBar routeSwitcher = new() { VerticalAlignment = VerticalAlignment.Center };
    bool routeSyncing;

    readonly Grid root = new() { RequestedTheme = ElementTheme.Default };
    readonly NavigationView nav = new()
    {
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        IsPaneToggleButtonVisible = false,
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsPaneVisible = false
    };
    // One status line for the whole window. Pages may add their own context on the right.
    readonly TextBlock status = Muted("Ready", 12), pageContext = Muted("", 12);
    // Holds the page area; the hairline where it meets the sidebar is NavigationView's own content edge (ApplyShellSurface).
    readonly Border shellDivider = new();
    readonly InfoBar feedback = new() { IsOpen = false, IsClosable = true };
    readonly Dictionary<string, FrameworkElement> pages = [];
    bool dialogOpen;
    AppPreferences preferences = new();

    /// <summary>Body text on the Windows type ramp. Requested sizes snap to Caption 12, Body 14, Body Strong, Body Large 18,
    /// Subtitle 20, Title 28 or Title Large 40; the style carries the theme's text colour.</summary>
    string codeReflectedState = "";
    static TextBlock Text(string text, double size = 14) => new() { Text = text, Style = Ramp(size) };
    static Style Ramp(double size) => Token(size < 12.5 ? "MasonCaption" : size < 16 ? "MasonBody" : size < 18 ? "MasonBodyStrong" : size < 19.5 ? "MasonBodyLarge" : size < 25 ? "MasonSubtitle" : size < 34 ? "MasonTitle" : "MasonTitleLarge");
    static Button Button(string text, RoutedEventHandler handler, bool accent = false) { var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center }; b.Click += handler; if (accent) b.Style = Token("AccentButtonStyle"); return b; }
    static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var i in items) p.Children.Add(i); return p; }
    static StackPanel Stack(params UIElement[] items) { var p = new StackPanel { Spacing = 12 }; foreach (var i in items) p.Children.Add(i); return p; }
    static ScrollViewer ScrollTools(FrameworkElement content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    static Grid Vertical(FrameworkElement top, FrameworkElement body) { var g = new Grid { RowSpacing = 12 }; g.RowDefinitions.Add(new() { Height = GridLength.Auto }); g.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); g.Children.Add(top); Grid.SetRow(body, 1); g.Children.Add(body); return g; }

    public MainWindow()
    {
        Title = "Mason Agent";
        SystemBackdrop = backdrop; Content = root;
        try { preferences = Store.Read<AppPreferences>("appearance.json") ?? new(); }
        catch (Exception error) { status.Text = "Appearance could not be read: " + error.Message; }
        // Enum.TryParse also accepts numbers, so only a defined theme name is honoured; anything else means Auto.
        root.RequestedTheme = Enum.TryParse<ElementTheme>(preferences.Theme, out var theme) && Enum.IsDefined(theme) ? theme : ElementTheme.Default;
        // Every animation checks the Windows animation setting and this app preference (MainWindow.Chrome.cs, Fluent.cs).
        Fluent.ReducedMotion = () => preferences.ReducedMotion;
        RestorePlacement(); TrackPlacement();

        // Title bar above, sidebar and pages below; Mica shows through both except where the page paper sits.
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        BrandControl(nav);
        ApplyShellSurface();
        root.Children.Add(BuildTitleBar(BrandHeader()));
        ApplyWindowTheme(); root.ActualThemeChanged += (_, _) => ApplyWindowTheme();
        brandSystemColors.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyWindowTheme);
        Fluent.FollowContrast(root, ApplyWindowTheme);
        Grid.SetRow(nav, 1); root.Children.Add(nav);
        pages["Code"] = BuildCodeWorkspace();
        pages["Chat"] = BuildChatWorkspace();
        var endpoints = BuildAiConnections();
        // Models, Discover, Connections, Downloads and Runtimes share one page with a section rail.
        var resources = BuildModelLibrary(endpoints);
        selectResources = index => selectModelView?.Invoke(index == 2 ? 5 : 0);
        pages["Models and connections"] = resources;
        // "Use for coding" in the model library opens Code with that model selected;
        // "Open in chat" goes through openNativeChat, which the Chat page owns.
        openCodeWithModel = id => { Navigate("Code"); selectCodingModel?.Invoke(id); };
        // The agent's semantic_search tool is offered only while an embeddings connection is assigned and checked.
        Mason.Code.CodeSemanticIndex.Use(Store.Root, () =>
        {
            List<AiEndpointProfile> profiles; try { profiles = Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []; } catch { return null; }
            var settings = CodeSearchSettings.Read();
            var embeddings = CodeSearchModels.Pick(profiles, settings.Embeddings, AiCapability.Embeddings); if (embeddings == null) return null;
            var reranker = CodeSearchModels.Pick(profiles, settings.Reranker, AiCapability.Reranking);
            string Key(AiEndpointProfile p) { try { return aiSessionKeys.GetValueOrDefault(p.Id) ?? Store.Unprotect(p.Secret); } catch { return ""; } }
            return new CodeSearchModels(embeddings, Key(embeddings), reranker, reranker == null ? "" : Key(reranker));
        });

        var content = new Grid { Padding = new Thickness(24, 16, 24, 12) };
        // Lifted cards and the floating composers cast their shadow onto this empty layer behind the pages
        // (a ThemeShadow receiver may not contain the element that casts it).
        var shadowLayer = new Grid(); content.Children.Add(shadowLayer); Fluent.AddShadowReceiver(shadowLayer);
        foreach (var view in pages.Values) { view.Visibility = Visibility.Collapsed; content.Children.Add(view); }
        nav.Content = BuildShell(content);
        Navigate("Code");
        StartScheduler();

        root.KeyboardAccelerators.Add(Shortcut(VirtualKey.Escape, VirtualKeyModifiers.None, () => { codeOperation?.Cancel(); aiOperation?.Cancel(); }));
        root.KeyboardAccelerators.Add(Shortcut(VirtualKey.K, VirtualKeyModifiers.Control, FocusTitleSearch));
        AppWindow.Closing += async (_, e) =>
        {
            if(codeOperation != null || aiOperation != null || modelScanBusy?.Invoke() == true)
            {
                codeOperation?.Cancel(); aiOperation?.Cancel(); modelScanCancel?.Invoke();
                e.Cancel = true; status.Text = "Stopping active work. Close again when it finishes."; return;
            }
            if(!downloadsClosed && modelDownloads != null)
            {
                e.Cancel = true; if(downloadsClosing) return; downloadsClosing = true;
                try { await modelDownloads.DisposeAsync(); }
                catch(Exception error) { await Notice("Downloads paused", error.Message); }
                finally { downloadsClosed = true; }
                Close(); return;
            }
            modelServing.Dispose();
        };
    }

    Action<Guid>? openCodeWithModel;

    static readonly (string Route, string Label, string Glyph)[] Routes = [("Code", "Code", "\uE943"), ("Chat", "Chat", "\uE8BD"), ("Models and connections", "Models", "\uE964")];

    /// <summary>Code, Chat and Models as peer sections in the title bar (SelectorBar, Windows 11 pattern for 2–5 peers).</summary>
    FrameworkElement BuildRouteSwitcher()
    {
        foreach (var (route, label, glyph) in Routes)
            routeSwitcher.Items.Add(new SelectorBarItem { Text = label, Icon = new FontIcon { Glyph = glyph }, Tag = route });
        routeSwitcher.SelectionChanged += (_, _) => { if (!routeSyncing && routeSwitcher.SelectedItem?.Tag is string route) Navigate(route); };
        AutomationProperties.SetName(routeSwitcher, "Workspace");
        return routeSwitcher;
    }

    /// <summary>Appearance and motion, from the gear in the title bar.</summary>
    FrameworkElement BuildAppSettingsButton()
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 280 };
        panel.Children.Add(new TextBlock { Text = "Appearance", Style = Token("MasonBodyStrong") });
        panel.Children.Add(Muted("Theme", 12));
        panel.Children.Add(BuildThemeFamilyPicker(panel));
        panel.Children.Add(Muted("Mode", 12));
        panel.Children.Add(BuildThemeSwitch());
        var motion = new ToggleSwitch { Header = "Reduce motion", IsOn = preferences.ReducedMotion };
        motion.Toggled += async (_, _) =>
        {
            try { preferences = preferences with { ReducedMotion = motion.IsOn }; Store.Write("appearance.json", preferences); }
            catch (Exception error) { await Notice("Appearance could not be saved", error.Message); }
        };
        panel.Children.Add(motion);
        var button = SubtleIconButton("\uE713", "Appearance and motion", (_, _) => { });
        button.Flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        appearanceButton = button; appearancePanel = panel;
        return button;
    }

    /// <summary>Content area above a slim status strip: window status on the left, page context on the right.</summary>
    FrameworkElement BuildShell(FrameworkElement content)
    {
        status.MaxLines = pageContext.MaxLines = 1;
        status.TextWrapping = pageContext.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = pageContext.TextTrimming = TextTrimming.CharacterEllipsis;
        status.IsTextSelectionEnabled = true;
        AutomationProperties.SetName(status, "Status");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        // Long errors are trimmed in the strip; the tooltip carries the full text.
        status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => ToolTipService.SetToolTip(status, status.Text));
        pageContext.MaxWidth = 420;

        var strip = new Grid { ColumnSpacing = 16, Padding = new Thickness(20, 6, 20, 6) };
        strip.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        strip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        status.Style = Token("MasonCaptionSecondary"); pageContext.Style = Token("MasonCaptionSecondary");
        status.TextWrapping = pageContext.TextWrapping = TextWrapping.NoWrap;
        strip.Children.Add(status);
        Grid.SetColumn(pageContext, 1); strip.Children.Add(pageContext);
        var stripFrame = new Border { Child = strip, Style = Token("MasonStatusStrip") };

        var shell = new Grid();
        shell.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        shell.Children.Add(content);
        feedback.Margin = new Thickness(20, 0, 20, 8); Grid.SetRow(feedback, 1); shell.Children.Add(feedback);
        Grid.SetRow(stripFrame, 2); shell.Children.Add(stripFrame);
        shellDivider.Child = shell;
        return shellDivider;
    }

    /// <summary>The Auto / Light / Dark choice at the foot of the sidebar. It applies at once and is saved.</summary>
    ThemeSwitch BuildThemeSwitch()
    {
        var theme = new ThemeSwitch(root.RequestedTheme, segment => BrandControl(segment));
        theme.Changed += async chosen =>
        {
            try { Store.Write("appearance.json", preferences with { Theme = chosen.ToString() }); root.RequestedTheme = chosen; preferences = preferences with { Theme = chosen.ToString() }; }
            catch (Exception error)
            {
                // The window keeps its current theme, so the switch must show that one too.
                theme.Display(root.RequestedTheme);
                await Notice("Appearance could not be saved", error.Message);
            }
        };
        return theme;
    }

    KeyboardAccelerator Shortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action) { var a = new KeyboardAccelerator { Key = key, Modifiers = modifiers }; a.Invoked += (_, e) => { e.Handled = true; action(); }; return a; }

    void ShowPage(string name)
    {
        if(!pages.ContainsKey(name)) return;
        bool changed = currentRoute != name;
        string from = currentRoute;
        currentRoute = name;
        if(name == "Code") refreshCodeProfiles?.Invoke();
        if(name == "Chat") refreshNativeChatProfiles?.Invoke();
        if(name == "Models and connections") refreshModelLibrary?.Invoke();
        foreach(var view in pages) view.Value.Visibility = view.Key == name ? Visibility.Visible : Visibility.Collapsed;
        // The right side of the status strip belongs to the Code page (project, branch, save state).
        pageContext.Visibility = name == "Code" ? Visibility.Visible : Visibility.Collapsed;
        // A model row's Code or Chat button hands off to its page (connected animation); otherwise the page
        // enters the way navigation moved, like the stacked pages of Windows Settings.
        if (changed && !Fluent.Connect("workspace-card", pages[name])) Fluent.PageIn(pages[name], MotionFor(from, name));
    }

    void Navigate(string name)
    {
        if(name is "Models" or "AI connections") { selectResources?.Invoke(name == "AI connections" ? 2 : 0); name = "Models and connections"; }
        if(!pages.ContainsKey(name)) return;
        ShowPage(name);
        routeSyncing = true;
        routeSwitcher.SelectedItem = routeSwitcher.Items.FirstOrDefault(i => (string)i.Tag == name);
        routeSyncing = false;
    }
}
