using System.IO;
using System.Text.Json;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace Mason;

public sealed partial class MainWindow
{
    CodeStore? codeStore;
    CancellationTokenSource? codeOperation;
    Action? refreshCodeProfiles;
    Action<Guid>? selectCodingModel;
    Action? openCodeSearch;
    Func<Task>? compactCodeContext;

    static TextBlock CodeNote(string text, double size = 12)
    {
        var label = Text(text, size); label.Opacity = .68; label.IsTextSelectionEnabled = true; return label;
    }
    static string ContextCaption(CodeSession? session) => session?.PromptTokens is int prompt && session.ContextTokens is int window
        ? $"{prompt:N0} / {window:N0} context" + (session.Checkpoints.Count > 0 ? $" · {session.Checkpoints.Count} checkpoint" + (session.Checkpoints.Count == 1 ? "" : "s") : "")
        : "Context measured before each action";
    static ScrollViewer CodeScroll(UIElement child) => new() { Content = child, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    static HyperlinkButton CodeLink(string label, RoutedEventHandler action)
    {
        var button = BrandLink(new HyperlinkButton { Content = label, Padding = new Thickness(0, 4, 12, 4) }); button.Click += action; return button;
    }
    FrameworkElement BuildCodeWorkspace()
    {
        try { codeStore = new CodeStore(Store.Root); }
        catch (Exception error) { return TroubleNotice("Code sessions are unavailable", error.Message, "Close another Mason Agent instance using this profile, or inspect the saved session files. Existing sessions have not been replaced."); }
        var store = codeStore!;
        CodeSession? session = store.SelectedSession;
        CodeProject? project = store.SelectedProject;
        const double projectPanelWidth=260;
        Action? viewerChanged = null; // assigned once the page exists; refreshes counts outside the viewer
        bool updating = false, refreshQueued = false, emptySidebarVisible = true; string[] allFiles = []; int fileGeneration = 0; string? forcedPanel = null; CodeGitStatus? gitStatus = null;
        Button send = null!, openFolder = null!, branch = null!, search = null!, changeButton = null!, preview = null!;
        FrameworkElement composer = null!, sessionHeader = null!;
        Grid toolbar = null!; TextBlock viewerLabel = null!; Image stateMood = null!; string shownMood = "";
        Grid? emptyHost = null; // onboarding or intro shown while there is no conversation
        Border projectSurface = null!;
        HyperlinkButton reference = null!, activityButton = null!;
        ToggleButton sideToggle = null!, viewerToggle = null!, pinned = null!;
        KeyboardAccelerator workspaceShortcut = null!, viewerShortcut = null!;
        TextBlock viewerHeading = null!;
        TabView tabs = null!;
        Grid sideBody = null!, viewer = null!, center = null!;
        StackPanel toolbarLeft=null!,toolbarRight=null!,outputPanel=null!; Grid composerTools=null!;
        Grid projectList=null!,fileArea=null!; ToggleButton projectsTab=null!,filesTab=null!,outputToggle=null!;
        TextBlock outputTitle=null!; ScrollViewer outputScroll=null!;
        Thumb splitter = null!;
        var draftSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        var viewerEntries = new Dictionary<string, CodeViewerEntry>();
        var page = new Grid { RowSpacing = 12, ColumnSpacing = 12 };
        page.ColumnDefinitions.Add(new() { Width = new GridLength(projectPanelWidth) }); page.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new() { Height = GridLength.Auto }); page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        // Project, branch and save state are shown on the right of the window's status strip.
        var footerText = pageContext; footerText.Text = "Sessions stay on this computer";
        var layout = new Grid { ColumnSpacing = 0 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(layout, 1); Grid.SetColumn(layout, 1); page.Children.Add(layout);
        var folderLabel = Text("Choose project", 14); folderLabel.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; folderLabel.MaxLines = 1; folderLabel.MaxWidth=155; folderLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        var folderPath = CodeNote(""); folderPath.MaxLines = 1; folderPath.TextTrimming = TextTrimming.CharacterEllipsis;
        var projectSwitcher=Button("Choose project",(_,_)=>{}); projectSwitcher.MaxWidth=225;
        var switcherGlyph=new FontIcon{Glyph="\uE8B7",FontSize=15};
        var switcherChevron=MotifIcon(Fluent.Motif.ChevronDown,12);switcherChevron.VerticalAlignment=VerticalAlignment.Center;
        projectSwitcher.Content=Row(switcherGlyph,folderLabel,switcherChevron);
        projectSwitcher.Padding=new Thickness(11,7,11,7);
        AutomationProperties.SetName(projectSwitcher,"Switch coding project");
        var projectMenu=Fluent.Acrylic(new MenuFlyout());projectSwitcher.Flyout=projectMenu;
        var profiles = new ComboBox { PlaceholderText = "Choose model", MinWidth = 130, MaxWidth = 210 };
        AutomationProperties.SetName(profiles, "Coding model");
        var permissions = new ComboBox { ItemsSource = new[] { "Read-only", "Review edits", "Workspace edits", "Auto" }, SelectedIndex = 0, MinWidth = 150 };
        AutomationProperties.SetName(permissions, "Coding permission mode");
        ToolTipService.SetToolTip(permissions, "Auto keeps commands inside the project's Windows sandbox. Installed x64 .NET, Python, Node and CMD are supported; Git Bash requires runtime admission. PowerShell 7 requires optional administrator logging setup. Network access is configured separately in Environment. New sessions start Read-only.");
        var shells = new ComboBox { ItemsSource = Enum.GetValues<CodeShellKind>().Select(CodeShells.Name).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(shells, "Workspace command shell");
        var shellPath = CodeNote("Direct executables · no implicit shell", 11); shellPath.TextWrapping = TextWrapping.Wrap;
        var environment = Button("Environment", (_,_)=>{});
        var networkModes=new ComboBox{ItemsSource=new[]{"Offline","Ask when needed"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};AutomationProperties.SetName(networkModes,"Command network policy");
        AutomationProperties.SetName(environment,"Project coding environment");
        var environmentHint=CodeNote("Any source language · execution depends on installed, sandbox-compatible toolchains.");environmentHint.MaxWidth=330;environmentHint.TextWrapping=TextWrapping.Wrap;
        var networkHint=CodeNote("Auto keeps direct sockets blocked. Node/npm, pip HTTPS and .NET SDK NuGet restore can ask for public HTTPS destinations. Other clients remain offline. Reviewed commands run with your Windows account.");networkHint.MaxWidth=330;networkHint.TextWrapping=TextWrapping.Wrap;
        var setupPowerShell=Button("Set up PowerShell Auto…",(_,_)=>{});
        setupPowerShell.Click+=async(_,_)=>
        {
            setupPowerShell.IsEnabled=false;
            try
            {
                var helper=Path.Combine(AppContext.BaseDirectory,"sandbox-setup","Mason.SandboxSetup.exe");
                if(!File.Exists(helper))throw new IOException("The optional setup helper is missing. Install the complete Mason Agent release.");
                using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helper,"install-powershell-logging"){UseShellExecute=true,Verb="runas",WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden})??throw new IOException("Setup did not start.");
                await process.WaitForExitAsync();
                environmentHint.Text=process.ExitCode==0&&CodePowerShellLogging.Provisioned?"PowerShell logging setup is ready. Select PowerShell 7 to use Auto.":"Setup did not complete. PowerShell Auto remains unavailable; other shells are unchanged.";
                ShowShell();
            }
            catch(Exception error){environmentHint.Text="PowerShell setup · "+error.Message;}
            finally{setupPowerShell.IsEnabled=true;}
        };
        shellPath.TextWrapping=TextWrapping.Wrap;shellPath.HorizontalAlignment=HorizontalAlignment.Left;
        networkHint.HorizontalAlignment=HorizontalAlignment.Left;environmentHint.HorizontalAlignment=HorizontalAlignment.Left;
        var setupHint=CodeNote("PowerShell setup requests administrator approval for logging registration only. Shell choice is remembered for new sessions.");
        setupHint.TextWrapping=TextWrapping.Wrap;setupHint.HorizontalAlignment=HorizontalAlignment.Left;
        var environmentContent=Stack(Text("Project environment",18),Text("Command shell",12),shells,shellPath,setupPowerShell,Text("Network",12),networkModes,networkHint,environmentHint,setupHint);
        environmentContent.Width=340;
        environment.Flyout=Fluent.Acrylic(new Flyout{Content=environmentContent});
        var autoNotice = CodeNote("", 11); autoNotice.TextWrapping = TextWrapping.Wrap; autoNotice.Visibility = Visibility.Collapsed;
        CodeSession? fileRefreshOwner = null; int fileRefreshRevision = -1, fileRefreshSequence = 0;
        var models = Button("Models", (_, _) => Navigate("Models and connections"));
        var title = Text("New coding session", 21); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.MaxLines = 1; title.TextTrimming = TextTrimming.CharacterEllipsis;
        var sessions = BrandControl(CodeProjectNavigation());
        var noProjects=CodeNote("Add a project folder to keep its sessions together.");noProjects.Margin=new Thickness(4,12,4,0);
        var deleteSession = ChatIcon("Delete selected coding session", "\uE74D", (_, _) => { });
        ToolTipService.SetToolTip(deleteSession, "Delete the selected session history. Workspace files are preserved.");
        var files = BrandControl(CodeExplorer());
        var fileSearch = new TextBox { PlaceholderText = "Find a file…", Visibility = Visibility.Collapsed }; AutomationProperties.SetName(fileSearch, "Find workspace file");
        fileSearch.TextChanged += (_, _) => RenderFiles();
        var fileState = CodeNote("");
        var fileProject=CodeNote("",11);fileProject.MaxWidth=150;fileProject.MaxLines=1;fileProject.TextTrimming=TextTrimming.CharacterEllipsis;
        var transcript = new StackPanel { Spacing = 20, Margin = new Thickness(28, 32, 28, 20), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Center };
        var transcriptScroll = CodeScroll(transcript); bool followTail = true, pendingTranscriptTail = true;
        // Built once: the magnifier is an animated icon, and Arrange only swaps between the two layouts.
        var searchCompact = MotifIcon(Fluent.Motif.Find, 15);
        var searchWide = Row(MotifIcon(Fluent.Motif.Find, 15), new TextBlock { Text = "Search files or commands", VerticalAlignment = VerticalAlignment.Center });
        // The shape of a file list while the project's files are being listed (added to the Files area below).
        var fileSkeleton = Fluent.Skeleton(.5, .72, .62, .8, .44, .68, .56); fileSkeleton.Margin = new Thickness(6, 8, 6, 0); fileSkeleton.Visibility = Visibility.Collapsed;
        // Behind the onboarding and intro states only, across the whole conversation area rather than the reading column.
        var welcomeArt = MasonryBackdrop(transcriptScroll); Grid.SetRow(welcomeArt, 1);
        // What the transcript showed last time, so only messages new to this session's view animate in (RenderTranscript).
        CodeSession? shownSession = null; int shownTurns = 0; bool shownLive = false;
        var turnViews = new Dictionary<int, (string Key, UIElement View)>();
        (bool Continues, UIElement View, Border Body, string Text)? liveView = null;
        transcriptScroll.ViewChanged += (_, _) => { if (transcriptScroll.ScrollableHeight - transcriptScroll.VerticalOffset < 90) followTail = true; };
        transcriptScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => followTail = false), true);
        transcriptScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => followTail = false), true);
        transcriptScroll.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End or VirtualKey.Space) followTail = false; }), true);
        double TranscriptTail() => session?.Turns.Count > 0 ? transcriptScroll.ScrollableHeight : 0;
        transcript.SizeChanged += (_, _) => { if (followTail) DispatcherQueue.TryEnqueue(() => { if (followTail) transcriptScroll.ChangeView(null, TranscriptTail(), null, true); }); };
        void RestoreTranscriptTail()
        {
            if (!pendingTranscriptTail || !transcriptScroll.IsLoaded || page.Visibility != Visibility.Visible) return;
            pendingTranscriptTail = false; followTail = true; transcriptScroll.UpdateLayout(); transcriptScroll.ChangeView(null, TranscriptTail(), null, true);
        }
        transcriptScroll.Loaded += (_, _) => DispatcherQueue.TryEnqueue(RestoreTranscriptTail);
        // Size the transcript from the scroller, as Chat does. Left to stretch, the scroller centres it by its natural width first and the stretched panel then overflows to the right by that margin.
        transcriptScroll.SizeChanged += (_, _) => { transcript.Width = Math.Max(200, Math.Min(820, transcriptScroll.ActualWidth - 56)); if (emptyHost != null) FitWelcome(emptyHost, transcriptScroll.ActualHeight - 60); };
        var state = CodeNote("");
        var context = CodeNote("Context measured before each action"); context.MaxLines = 2; context.TextTrimming = TextTrimming.CharacterEllipsis;
        var contextLink = CodeLink("Context", (_, _) => OpenTab("context", "", true)); contextLink.FontSize = 12;
        AutomationProperties.SetName(contextLink, "Coding context and checkpoints");
        var draft = new TextBox { PlaceholderText = "Describe a change, ask about the repo, or @ a file…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 170, MaxLength = 16000, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(draft,"Build keeps the current task’s requirements across follow-ups. Type /new followed by a request to start a separate task.");
        AutomationProperties.SetName(draft, "Coding message"); ComposerBox(draft);
        var references = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var reasoning = new ComboBox { ItemsSource = new[] { "Default", "Off", "Low", "Medium", "High" }, SelectedIndex = 0, MinWidth = 95 }; AutomationProperties.SetName(reasoning, "Coding reasoning");
        var agentMode = new ComboBox { ItemsSource = new[] { "Build", "Ask" }, SelectedIndex = 0, MinWidth = 85 };
        AutomationProperties.SetName(agentMode, "Coding mode"); ToolTipService.SetToolTip(agentMode, "Build performs workspace work. Ask explores without file writes or commands.");
        var approvalHost = new ContentControl(); TaskCompletionSource<bool>? pendingApproval = null;
        var spinner = BrandControl(new ProgressRing { Width = 15, Height = 15, Visibility = Visibility.Collapsed });
        var stop = Button("Stop", (_, _) => codeOperation?.Cancel()); stop.Visibility = Visibility.Collapsed;
        send = ChatIcon("Send coding message", "\uE724", async (_, _) => await Send()); send.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; BrandAccent(send);
        tabs = InterfaceTabs.Apply(new TabView { IsAddTabButtonVisible = false, TabWidthMode = TabViewWidthMode.SizeToContent, CanDragTabs = false, CanReorderTabs = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }, TabPresentation.Document);
        pinned = BrandControl(new ToggleButton { Content = new FontIcon { Glyph = "\uE718", FontSize = 15 }, Padding = new Thickness(8) }); AutomationProperties.SetName(pinned, "Pin current viewer tab"); ToolTipService.SetToolTip(pinned, "Keep the agent from switching the selected viewer tab");
        pinned.Click += (_, _) => { if (session != null) { session.ViewerPinned = pinned.IsChecked == true; Save(); } };
        sideToggle = BrandControl(new ToggleButton { Content = new FontIcon { Glyph = "\uE8FD", FontSize = 15 }, Padding = new Thickness(9), MinWidth = 36, MinHeight = 36 });
        viewerLabel = new TextBlock { Text = "Viewer" };
        viewerToggle = BrandControl(new ToggleButton { Content = Row(new FontIcon { Glyph = "\uE8A5", FontSize = 14 }, viewerLabel), Padding = new Thickness(10, 7, 10, 7), MinHeight = 36 });
        AutomationProperties.SetName(sideToggle, "Toggle projects panel"); ToolTipService.SetToolTip(sideToggle, "Show or hide projects and files · Ctrl+B");
        AutomationProperties.SetName(viewerToggle, "Toggle artifact viewer");
        sideToggle.Click += (_, _) => ToggleWorkspace(); viewerToggle.Click += (_, _) => ToggleViewer();
        var closeViewer = ChatIcon("Close artifact viewer", "\uE711", (_, _) => { if (session != null) CodePanels.Hide(session); Arrange(); Save(); });
        changeButton = Button("Changes", (_, _) => OpenTab("changes", "", true));
        activityButton = CodeLink("View activity", (_, _) => OpenTab("activity", "", true));
        preview = Button("Open preview", async (_, _) =>
        {
            if (session == null || dialogOpen) return;
            var address = new TextBox { PlaceholderText = "http://localhost:5173", Text = "http://localhost:5173" }; AutomationProperties.SetName(address, "Local preview URL"); dialogOpen = true;
            try
            {
                if (await new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Open local website preview", Content = Stack(CodeNote("Start the site's development server, then enter its loopback URL. The viewer checks that the server answers and can open the page in your browser; the agent does not inspect it automatically."), address), PrimaryButtonText = "Open", CloseButtonText = "Cancel" }.ShowAsync() == ContentDialogResult.Primary)
                    OpenTab("web", CodeProtocol.PreviewUri(address.Text).AbsoluteUri, true);
            }
            catch (Exception error) { state.Text = error.Message; }
            finally { dialogOpen = false; }
        });
        openFolder = Button("Add project", async (_, _) => await OpenWorkspace());
        openFolder.Content=Row(new FontIcon{Glyph="\uE710",FontSize=12},new TextBlock{Text="Add project",FontSize=12});
        openFolder.Padding=new Thickness(8,7,8,7);openFolder.HorizontalAlignment=HorizontalAlignment.Stretch;AutomationProperties.SetName(openFolder,"Add coding project");
        ToolTipService.SetToolTip(openFolder,"Open an existing folder, or use New folder in the picker. Existing projects restore their sessions.");
        reference = CodeLink("Reference selected file", (_, _) =>
        {
            if (session == null || CodeExplorerSelection(files) is not { Directory: false } chosen || codeOperation != null) return;
            string path = chosen.Path;
            try { _ = new CodeWorkspace(session.Workspace).Read(path); if (!session.References.Contains(path)) { if (session.References.Count >= 6) throw new ArgumentException("Reference up to six files."); session.References.Add(path); RenderReferences(); Save(); } }
            catch (Exception error) { state.Text = error.Message; }
        });
        sideBody = new Grid { RowSpacing = 8, Padding = new Thickness(12, 16, 12, 0) };
        sideBody.RowDefinitions.Add(new() { Height = GridLength.Auto }); sideBody.RowDefinitions.Add(new() { Height = new GridLength(1,GridUnitType.Star) });
        var workspaceHeading = Text("Projects", 18); workspaceHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var closeWorkspace = ChatIcon("Close projects panel", "\uE711", (_, _) => ToggleWorkspace());closeWorkspace.Padding=new Thickness(5);closeWorkspace.MinWidth=28;
        projectsTab=BrandControl(new ToggleButton{Content="Projects",IsChecked=true});filesTab=BrandControl(new ToggleButton{Content="Files"});
        AutomationProperties.SetName(projectsTab,"Show project sessions");AutomationProperties.SetName(filesTab,"Show project files");
        sideBody.Children.Add(Toolbar(Row(projectsTab,filesTab),closeWorkspace));
        projectList=Vertical(openFolder,sessions);projectList.Children.Add(noProjects);Grid.SetRow(noProjects,1);Grid.SetRow(projectList,1);sideBody.Children.Add(projectList);
        var filterFiles = ChatIcon("Filter workspace files", "\uE721", (_, _) =>
        {
            if (fileSearch.Visibility == Visibility.Visible)
            {
                // A collapsed TextBox can defer TextChanged until it is loaded again.
                // Restore the unfiltered graph explicitly before hiding the field.
                fileSearch.Text = ""; RenderFiles(); fileSearch.Visibility = Visibility.Collapsed;
            }
            else { fileSearch.Visibility = Visibility.Visible; fileSearch.Focus(FocusState.Programmatic); }
        });
        filterFiles.MinWidth = filterFiles.MinHeight = 28; filterFiles.Padding = new Thickness(5); filterFiles.Content = MotifIcon(Fluent.Motif.Find, 16);
        reference.Content = new FontIcon { Glyph = "\uE710", FontSize = 15 }; reference.Padding = new Thickness(5); reference.MinWidth = reference.MinHeight = 28; AutomationProperties.SetName(reference, "Reference selected file"); ToolTipService.SetToolTip(reference, "Reference selected file");
        var refreshFiles = ChatIcon("Refresh workspace files", "\uE72C", async (_, _) => { if (session != null) await RefreshWorkspaceFiles(session, true); });
        refreshFiles.MinWidth = refreshFiles.MinHeight = 28; refreshFiles.Padding = new Thickness(5);
        var fileHeader = Toolbar(Stack(Row(Text("Files", 16), fileState),fileProject), refreshFiles, filterFiles, reference);
        fileArea = Vertical(Stack(fileHeader, fileSearch), files); fileArea.RowSpacing = 6; fileArea.Padding = new Thickness(0, 12, 0, 0); Grid.SetRow(fileArea, 1); sideBody.Children.Add(fileArea); Grid.SetRow(fileSkeleton, 1); fileArea.Children.Add(fileSkeleton);
        projectSurface=SurfaceCard(sideBody);projectSurface.Padding=new Thickness(0);projectSurface.CornerRadius=new CornerRadius(8);Grid.SetRow(projectSurface,1);page.Children.Add(projectSurface);
        void SidebarTab(bool showFiles){if(session!=null)session.SidebarFiles=showFiles;projectList.Visibility=showFiles?Visibility.Collapsed:Visibility.Visible;fileArea.Visibility=showFiles?Visibility.Visible:Visibility.Collapsed;projectsTab.IsChecked=!showFiles;filesTab.IsChecked=showFiles;}
        projectsTab.Click+=(_,_)=>{SidebarTab(false);Save();};filesTab.Click+=(_,_)=>{SidebarTab(true);Save();};
// Composer: the message, then how the agent will work (mode, model, permission, environment),
        // then a quiet footer with the keyboard hint, context use and Send.
        // The four choices sit on one line when there is room and fold into a 2 x 2 grid when there is not.
        composerTools = new Grid { ColumnSpacing = 8, RowSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var tool in new FrameworkElement[] { agentMode, profiles, permissions, environment })
        {
            tool.HorizontalAlignment = HorizontalAlignment.Stretch;
            composerTools.Children.Add(tool);
        }
        void ArrangeComposer()
        {
            bool folded = center.ActualWidth < 640;
            int columns = folded ? 2 : 4;
            composerTools.HorizontalAlignment = folded ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            composerTools.ColumnDefinitions.Clear(); composerTools.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) composerTools.ColumnDefinitions.Add(new() { Width = folded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            for (int i = 0; i < 4 / columns; i++) composerTools.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int i = 0; i < composerTools.Children.Count; i++)
            {
                var tool = (FrameworkElement)composerTools.Children[i];
                Grid.SetColumn(tool, i % columns); Grid.SetRow(tool, i / columns);
            }
            // Folded cells share the width; on one line each control keeps its natural size.
            profiles.MaxWidth = permissions.MaxWidth = folded ? double.PositiveInfinity : 210;
        }
        var composerHint = CodeNote("Enter to send · Shift+Enter for a new line", 11);
        composerHint.IsTextSelectionEnabled = false; composerHint.VerticalAlignment = VerticalAlignment.Center;
        composerHint.MaxLines = 1; composerHint.TextTrimming = TextTrimming.CharacterEllipsis;
        var composerFooter = Toolbar(composerHint, contextLink, send);
        var composerBody = Stack(references, draft, composerTools, composerFooter, autoNotice);
        composerBody.Spacing = 8;
        // The composer floats above the transcript, with the window's shared soft shadow beneath it.
        var composerSurface = Fluent.Float(ChatSurface(composerBody, new Thickness(12, 8, 10, 10)), 24);
        composerSurface.CornerRadius = new CornerRadius(12); composer = composerSurface;
        var transcriptBody = new Grid { RowSpacing = 10, Padding = new Thickness(10, 0, 10, 8), MaxWidth = 960 };
        transcriptBody.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); transcriptBody.RowDefinitions.Add(new() { Height = GridLength.Auto }); transcriptBody.RowDefinitions.Add(new() { Height = GridLength.Auto }); transcriptBody.RowDefinitions.Add(new() { Height = GridLength.Auto });
        transcriptBody.Children.Add(transcriptScroll); Grid.SetRow(approvalHost, 1); transcriptBody.Children.Add(approvalHost);
        // Mortar mirrors the session's recorded state (working, waiting on you, done, in trouble).
        // He is hidden for states without a matching still and while the intro already shows him.
        stateMood = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        void ShowMood()
        {
            var (mood, meaning) = session == null || session.Turns.Count == 0 ? ("", "") : session.State switch
            {
                "Running" => (MoodWorking, "working on it"),
                "Awaiting review" or "Waiting for input" => (MoodChecking, "waiting for you"),
                // "Well played" when the session left reviewable changes, "we're good" when it only answered.
                "Completed" => session.Changes.Any(c => !c.Restored) ? (MoodPleased, "finished with changes") : (MoodDone, "finished"),
                "Failed" or "Blocked" or "Needs verification" => (MoodTrouble, "ran into a problem"),
                _ => ("", "")
            };
            stateMood.Visibility = mood.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (mood == shownMood) return;
            shownMood = mood;
            if (mood.Length == 0) return;
            stateMood.Source = MascotSource(mood, 40);
            AutomationProperties.SetName(stateMood, "Mortar: " + meaning);
            FadeIn(stateMood);
        }
        state.VerticalAlignment = VerticalAlignment.Center;
        state.TextWrapping = TextWrapping.Wrap;
        var progressRow = new Grid { ColumnSpacing = 8 };
        progressRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        progressRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.Children.Add(Row(stateMood, spinner));
        Grid.SetColumn(state, 1);
        progressRow.Children.Add(state);
        var stateRow = Toolbar(progressRow, stop); Grid.SetRow(stateRow, 2); transcriptBody.Children.Add(stateRow); Grid.SetRow(composer, 3); transcriptBody.Children.Add(composer);
        var sessionTools=ChatIcon("Session settings","\uE713",(_,_)=>{});
        sessionTools.Flyout=Fluent.Acrylic(new Flyout{Content=Stack(Text("Session settings",18),Text("Reasoning",12),reasoning,deleteSession)});
        sessionTools.Content=MotifIcon(Fluent.Motif.Settings,16);
        ToolTipService.SetToolTip(sessionTools,"Reasoning and session actions");
        var titleBlock = Stack(title, folderPath); titleBlock.Spacing = 1; titleBlock.VerticalAlignment = VerticalAlignment.Center;
        sessionHeader = Toolbar(titleBlock, activityButton, sessionTools);
        sessionHeader.Margin = new Thickness(10, 2, 10, 0);
        center = Vertical(sessionHeader, transcriptBody); center.Children.Insert(0, welcomeArt); layout.Children.Add(center);        preview.Content = Row(new FontIcon { Glyph = "\uE774", FontSize = 14 }, new TextBlock { Text = "Website" });
        preview.Padding = new Thickness(8);
        AutomationProperties.SetName(changeButton, "Review session file changes"); ToolTipService.SetToolTip(changeButton, "Review session file changes");
        AutomationProperties.SetName(preview, "Open local website preview"); ToolTipService.SetToolTip(preview, "Open local website preview");
        tabs.TabStripFooter = null;
        viewerHeading = CodeNote("Artifacts", 13);
        outputTitle=CodeNote("Command output",12);var outputText=ChatInline("",12,true);
        outputToggle=BrandControl(new ToggleButton{Content="Output",IsChecked=true});AutomationProperties.SetName(outputToggle,"Toggle command output");
        outputScroll=CodeScroll(outputText);outputScroll.MaxHeight=180;
        outputPanel=Stack(outputTitle,outputScroll);outputPanel.Margin=new Thickness(4,6,4,2);outputPanel.Spacing=6;
        var workBody=new Grid{RowSpacing=8};workBody.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});workBody.RowDefinitions.Add(new(){Height=GridLength.Auto});workBody.Children.Add(tabs);Grid.SetRow(outputPanel,1);workBody.Children.Add(outputPanel);
        outputToggle.Click+=(_,_)=>{if(session!=null){session.OutputVisible=outputToggle.IsChecked==true;RefreshOutput();Save();}};
        void RefreshOutput()
        {
            var last=session?.Activities.LastOrDefault(a=>a.Tool is "run_shell" or "run_command");outputToggle.Visibility=last==null?Visibility.Collapsed:Visibility.Visible;outputToggle.IsChecked=session?.OutputVisible==true;
            outputPanel.Visibility=last!=null&&session?.OutputVisible==true?Visibility.Visible:Visibility.Collapsed;
            if(last!=null){outputTitle.Text="Command output · "+last.State+"\n"+CodeActivityText.Command(last);var text=CodeActivityText.Result(last);outputScroll.Content=ChatInline(text.Length>12000?text[^12000..]:text,12,true);}
        }
        viewer = Vertical(Toolbar(viewerHeading, changeButton, preview,outputToggle, pinned, closeViewer), workBody);
        SurfaceBehind(viewer, new Thickness(10, 8, 10, 10));
        Grid.SetColumn(viewer, 2); layout.Children.Add(viewer);
        splitter = new Thumb { Width = 8, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(splitter, "Resize conversation and task viewer"); ToolTipService.SetToolTip(splitter, "Drag to resize the viewer"); Grid.SetColumn(splitter, 1); layout.Children.Add(splitter);
        splitter.DragDelta += (_, e) =>
        {
            if (session == null || viewer.Visibility != Visibility.Visible) return;
            double available = layout.ColumnDefinitions[0].ActualWidth + layout.ColumnDefinitions[2].ActualWidth;
            if (available < 1) return;
            session.ViewerFraction = Math.Clamp((layout.ColumnDefinitions[2].ActualWidth - e.HorizontalChange) / available, .28, .67); Arrange();
        };
        splitter.DragCompleted += (_, _) => Save();
        branch = Button("Repository", (_, _) => OpenTab("git", "", true)); branch.MaxWidth = 200;
        ToolTipService.SetToolTip(branch, "Repository information for the selected project; this does not switch folders.");
        search = Button("Search files or commands", async (_, _) => await Search()); search.MaxWidth = 290;
        models.Visibility = Visibility.Collapsed;
        // One toolbar row at every width: where you are on the left, find and viewer on the right.
        // Labels shrink to icons in Arrange instead of wrapping onto a second row.
        toolbarLeft = Row(sideToggle, projectSwitcher, branch);
        toolbarRight = Row(search, viewerToggle);
        toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(toolbarRight, 1);
        toolbar.Children.Add(toolbarLeft); toolbar.Children.Add(toolbarRight);
        Grid.SetColumnSpan(toolbar, 2);
        openCodeSearch = async () => await Search();
        // The title bar search opens projects and sessions, and adds projects, through these.
        openCodeSession = chosen => { if (codeOperation != null) { state.Text = "Finish or stop the current run before switching sessions."; return; } Navigate("Code"); if (chosen != session) Select(chosen); };
        openCodeProject = owner => { if (codeOperation != null) { state.Text = "Finish or stop the current run before switching projects."; return; } Navigate("Code"); if (owner != project) SelectProject(owner); };
        addCodeProject = OpenWorkspace;
        compactCodeContext = CompactContext;
        page.Children.Add(toolbar);
        center.SizeChanged += (_, _) => {title.MaxWidth = folderPath.MaxWidth = Math.Max(100, center.ActualWidth - 190);ArrangeComposer();};
        workspaceShortcut = Shortcut(VirtualKey.B, VirtualKeyModifiers.Control, ToggleWorkspace);
        viewerShortcut = Shortcut(VirtualKey.V, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, ToggleViewer);
        root.KeyboardAccelerators.Add(workspaceShortcut); root.KeyboardAccelerators.Add(viewerShortcut);
        void Shortcuts() { workspaceShortcut.IsEnabled = page.Visibility == Visibility.Visible; viewerShortcut.IsEnabled = page.Visibility == Visibility.Visible && session?.Tabs.Count > 0; }
        deleteSession.Click += async (_, _) => await DeleteSession();
        projectMenu.Opening+=(_,_)=>RefreshProjectMenu();
        sessions.ItemInvoked+=(_,e)=>
        {
            if(updating||codeOperation!=null||(e.InvokedItem as TreeViewNode)?.Content is not CodeProjectItem item)return;
            if(item.NewSession)NewProjectSession(item.Project);
            else if(item.Session!=null)Select(item.Session);
            else SelectProject(item.Project);
        };
        sessions.Expanding+=(_,e)=>{if(!updating&&e.Node.Content is CodeProjectItem{Session:null,NewSession:false} item)PersistExpansion(item.Project,true);};
        sessions.Collapsed+=(_,e)=>{if(!updating&&e.Node.Content is CodeProjectItem{Session:null,NewSession:false} item)PersistExpansion(item.Project,false);};
        files.ItemInvoked += (_, e) => { if ((e.InvokedItem as TreeViewNode)?.Content is CodeExplorerItem { Directory: false } item) OpenTab("file", item.Path, true); };
        files.SelectionChanged += (_, _) => reference.IsEnabled = codeOperation == null && CodeExplorerSelection(files) is { Directory: false };
        profiles.SelectionChanged += (_, _) => { if (!updating && session != null) { session.Model = SelectedChatProfile(profiles)?.Id; Save(); } };
        permissions.SelectionChanged += (_, _) => { if (!updating && session != null) { session.Permission = (CodePermission)Math.Max(0, permissions.SelectedIndex); ShowShell(); Enable(); Save(); } };
        networkModes.SelectionChanged+=(_,_)=>{if(!updating&&session!=null&&codeOperation==null){session.Network=(CodeNetworkMode)Math.Max(0,networkModes.SelectedIndex);Enable();Save();}};
        shells.SelectionChanged += (_, _) =>
        {
            if (updating || session == null || codeOperation != null) return;
            var kind = (CodeShellKind)Math.Max(0, shells.SelectedIndex); var executable = CodeShells.Find(kind);
            if (kind != CodeShellKind.Direct && executable == null)
            {
                state.Text = CodeShells.Name(kind) + " was not found. Install it or choose another shell; the previous selection is retained.";
                updating = true; shells.SelectedIndex = (int)session.Shell; updating = false; return;
            }
            var candidate=new CodeSession{Shell=kind,ShellExecutable=executable,Permission=session.Permission};
            if(CodeShells.Unavailable(candidate) is {} reason){state.Text=reason;updating=true;shells.SelectedIndex=(int)session.Shell;updating=false;return;}
            try{if(project!=null)store.SetProjectShell(project.Id,kind);session.Shell = kind; session.ShellExecutable = executable; ShowShell(); Enable(); Save();}catch(Exception error){state.Text=error.Message;updating=true;shells.SelectedIndex=(int)session.Shell;updating=false;}
        };
        reasoning.SelectionChanged += (_, _) => { if (!updating && session != null) { session.Reasoning = (reasoning.SelectedItem?.ToString() ?? "Default").ToLowerInvariant(); Save(); } };
        agentMode.SelectionChanged += (_, _) => { if (!updating && session != null) { session.Mode = agentMode.SelectedIndex == 1 ? "ask" : "build"; Enable(); Save(); } };
        draft.TextChanged += (_, _) => { if (session != null && !updating) { session.Draft = draft.Text; draftSave.Stop(); draftSave.Start(); } };
        draftSave.Tick += (_, _) => { draftSave.Stop(); Save(); };
        draft.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) { e.Handled = true; await Send(); }
        };
        tabs.SelectionChanged += (_, _) => { if (!updating && session != null && tabs.SelectedItem is TabViewItem selected && selected.Tag is CodeTab tab) { session.SelectedTab = tab.Key; Save(); } };
        tabs.TabCloseRequested += (_, e) =>
        {
            if (session == null || e.Tab.Tag is not CodeTab tab) return;
            if (viewerEntries.Remove(tab.Key, out var entry)) entry.Dispose(); CodePanels.Close(session, tab.Key);
            RenderTabs(); Arrange(); Save();
        };
        page.SizeChanged += (_, _) => Arrange();
        refreshCodeProfiles = RefreshModels;
        selectCodingModel = id => { RefreshModels(); profiles.SelectedItem = profiles.Items.OfType<ChatModelChoice>().FirstOrDefault(c => c.Id == id); };
        page.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => { Shortcuts(); if (page.Visibility == Visibility.Visible) { RefreshModels(); DispatcherQueue.TryEnqueue(RestoreTranscriptTail); } });
        var args = Environment.GetCommandLineArgs(); int workspaceArg = Array.IndexOf(args, "--code-workspace");
        if (workspaceArg >= 0 && workspaceArg + 1 < args.Length)
        {
            try { project=store.AddProject(args[workspaceArg+1]);session=store.LastSession(project.Id)??store.New(project.Workspace); }
            catch (Exception error) { state.Text = error.Message; }
        }
        viewerChanged = () => { Arrange(); RenderTranscript(); };
        Select(session);
        Closed += (_, _) => { draftSave.Stop(); pendingApproval?.TrySetResult(false); foreach (var entry in viewerEntries.Values) entry.Dispose(); Save(); store.Dispose(); };
        return page;

        void RenderProjects()
        {
            bool previous=updating;updating=true;
            try{PopulateCodeProjects(sessions,store,session);noProjects.Visibility=store.Projects.Count==0?Visibility.Visible:Visibility.Collapsed;}
            finally{updating=previous;}
        }
        void PersistExpansion(CodeProject owner,bool expanded)
        {
            try{store.SetProjectExpanded(owner.Id,expanded);}catch(Exception error){state.Text="Project list save failed · "+error.Message;}
        }
        void RefreshProjectMenu()
        {
            projectMenu.Items.Clear();
            foreach(var owner in store.Projects)
            {
                bool ambiguous=store.Projects.Count(p=>p.Name.Equals(owner.Name,StringComparison.OrdinalIgnoreCase))>1;
                var item=new MenuFlyoutItem{Text=ambiguous?owner.Name+" · "+Path.GetDirectoryName(owner.Workspace):owner.Name,Icon=new FontIcon{Glyph="\uE8B7"},IsEnabled=codeOperation==null};
                ToolTipService.SetToolTip(item,owner.Workspace);item.Click+=(_,_)=>SelectProject(owner);projectMenu.Items.Add(item);
            }
            if(projectMenu.Items.Count>0)projectMenu.Items.Add(new MenuFlyoutSeparator());
            var add=new MenuFlyoutItem{Text="Add project…",Icon=new FontIcon{Glyph="\uE710"},IsEnabled=codeOperation==null};add.Click+=async(_,_)=>await OpenWorkspace();projectMenu.Items.Add(add);
        }
        void SelectProject(CodeProject owner)=>Select(store.LastSession(owner.Id),owner);
        void NewProjectSession(CodeProject owner)
        {
            if(codeOperation!=null||!TrySaveCurrent())return;
            try{Select(store.New(owner.Workspace),owner);}catch(Exception error){state.Text="Could not start a session · "+error.Message;}
        }
        async Task OpenWorkspace()
        {
            if (codeOperation != null) return;
            try
            {
                var folder=await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
                if(folder==null||!TrySaveCurrent())return;
                var owner=store.AddProject(folder.Path);
                Select(store.LastSession(owner.Id)??store.New(owner.Workspace),owner);
            }
            catch (Exception error) { state.Text = error.Message; }
        }
        async Task DeleteSession()
        {
            if (session == null || codeOperation != null || dialogOpen) return;
            var selected = session; dialogOpen = true;
            try
            {
                var content = Stack(Text(selected.Title, 17), CodeNote(selected.Workspace), Text("Deletes this session's conversation, activity, checkpoints and saved change history. Workspace files stay on disk; deleting a session does not undo its edits.", 14));
                var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Delete coding session?", Content = content, PrimaryButtonText = "Delete session", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                draftSave.Stop();
                var owner=store.ProjectFor(selected);
                if (!store.Delete(selected.Id)) return;
                session = null; // Select normally saves the previous session; never resurrect a deleted journal.
                Select(owner==null?null:store.LastSession(owner.Id),owner);
            }
            catch (Exception error) { state.Text = "Session deletion failed · " + error.Message; }
            finally { dialogOpen = false; Enable(); }
        }
        void RenderFiles() => CodeExplorerPopulate(files, allFiles, fileSearch.Text, session, gitStatus);
        void ShowShell()
        {
            environment.Content=session==null?"Environment":CodeShells.Name(CodeShells.Effective(session));
            shellPath.Text = session?.Shell == CodeShellKind.Direct || session == null ? "Shell disabled · direct executable routing only" : (session.Shell==CodeShellKind.Recommended?CodeShells.Find(CodeShells.Effective(session)):session.ShellExecutable) ?? "Selected shell is unavailable";
            setupPowerShell.Visibility=session?.Shell==CodeShellKind.PowerShell&&!CodePowerShellLogging.Provisioned?Visibility.Visible:Visibility.Collapsed;
            if (session?.Permission == CodePermission.Auto && session.Shell == CodeShellKind.PowerShell) shellPath.Text += "\n"+(CodeShells.Unavailable(session)??"Auto ready · logging preserved · workspace writes only");
            if (session?.Permission == CodePermission.Auto && session.Shell == CodeShellKind.GitBash && !CodeSandboxTools.GitBashAutoAvailable) shellPath.Text += "\n" + CodeSandboxTools.GitBashAutoUnavailableReason;
            ToolTipService.SetToolTip(shells, session == null ? "Open a workspace to choose its shell." : CodeShells.Describe(session));
        }
        async Task RefreshWorkspaceFiles(CodeSession owner, bool force = false)
        {
            int revision = owner.Activities.Count(a => a.State == "Completed" && a.Tool is "create_file" or "create_directory" or "edit_file" or "replace_file" or "apply_edits" or "run_command" or "run_shell");
            if (!force && fileRefreshOwner == owner && fileRefreshRevision == revision) return;
            fileRefreshOwner = owner; fileRefreshRevision = revision;
            int sequence = ++fileRefreshSequence, generation = fileGeneration;
            try
            {
                var listing = await Task.Run(() => new CodeWorkspace(owner.Workspace).Files(".", 1000));
                if (owner != session || sequence != fileRefreshSequence || generation != fileGeneration) return;
                allFiles = listing.Files; RenderFiles();
                fileState.Text = $"{allFiles.Length} files" + (listing.Truncated ? " · bounded listing" : "");
            }
            catch (Exception error) { if (owner == session && sequence == fileRefreshSequence) { fileRefreshRevision = -1; fileState.Text = "Refresh failed · " + error.Message; } }
        }
        async Task RefreshGit(CodeSession owner)
        {
            var observed = await CodeGit.Inspect(owner.Workspace, CancellationToken.None);
            if (owner != session) return;
            gitStatus = observed; branch.Content = observed.Branch=="Folder workspace"?"Repository":observed.Branch; ToolTipService.SetToolTip(branch, observed.Detail);
            footerText.Text = (project?.Name??Path.GetFileName(owner.Workspace))+" · "+observed.Branch + " · Session saved locally"; RenderFiles();
        }
        async Task Search()
        {
            if (dialogOpen) return; dialogOpen = true; Action? chosen = null;
            var input = new TextBox { PlaceholderText = "Search files, sessions or commands…", MinWidth = 420 };
            AutomationProperties.SetName(input, "Search coding workspace");
            var results = new ListView { MaxHeight = 420, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single };
            var commands = new Dictionary<string, Action> {
                ["Command · Add project"] = async () => await OpenWorkspace(),
                ["Command · Toggle projects panel"] = ToggleWorkspace,
                ["Command · Models and connections"] = () => Navigate("Models and connections") };
            if (session != null)
            {
                commands["Command · Delete selected session"] = async () => await DeleteSession();
                commands["Command · Review session changes"] = () => OpenTab("changes", "", true);
                commands["Command · View activity"] = () => OpenTab("activity", "", true);
                commands["Command · View context checkpoints"] = () => OpenTab("context", "", true);
                commands["Command · Repository status"] = () => OpenTab("git", "", true);
                if (session.Tabs.Count > 0) commands["Command · Toggle task viewer"] = ToggleViewer;
            }
            if (project != null && codeOperation == null) commands["Command · New session in "+project.Name] = () => NewProjectSession(project);
            if (session?.Messages.Count > 0 && codeOperation == null) commands["Command · Compact context now"] = async () => await CompactContext();
            foreach (var path in allFiles) commands["File · " + path] = () => OpenTab("file", path, true);
            if(codeOperation==null)
            {
                foreach(var owner in store.Projects)commands["Project · "+owner.Name+" · "+owner.Workspace]=()=>SelectProject(owner);
                foreach(var item in store.Sessions)commands["Session · "+(store.ProjectFor(item)?.Name??Path.GetFileName(item.Workspace))+" · "+item.Title+" · "+item.Id.ToString("N")[..6]]=()=>Select(item);
            }
            void Filter() { results.ItemsSource = commands.Keys.Where(k => k.Contains(input.Text, StringComparison.OrdinalIgnoreCase)).Take(80).ToArray(); results.SelectedIndex = 0; }
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Search files and commands", Content = Stack(input, results), CloseButtonText = "Close" };
            input.TextChanged += (_, _) => Filter();
            results.ItemClick += (_, e) => { chosen = commands[e.ClickedItem.ToString()!]; dialog.Hide(); };
            input.KeyDown += (_, e) => { if (e.Key == VirtualKey.Down) { e.Handled = true; results.Focus(FocusState.Keyboard); } else if (e.Key == VirtualKey.Enter && results.SelectedItem is string key) { e.Handled = true; chosen = commands[key]; dialog.Hide(); } };
            results.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter && results.SelectedItem is string key) { e.Handled = true; chosen = commands[key]; dialog.Hide(); } };
            dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic); Filter();
            try { await dialog.ShowAsync(); } finally { dialogOpen = false; } chosen?.Invoke();
        }
        bool TrySaveCurrent()
        {
            if (session == null) return true;
            try { store.Save(session);return true; } catch (Exception error) { state.Text = "Session save failed · " + error.Message;return false; }
        }
        void Save()=>TrySaveCurrent();
        void RefreshModels()
        {
            try
            {
                var current = session?.Model;
                var choices = (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).Where(p => p.Model.Length > 0 && (p.ManagedCapability is null or AiCapability.Chat)).ToArray();
                updating = true; PresentChatModels(profiles, choices);
                profiles.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding DisplayName}' FontSize='13' MaxWidth='210' TextTrimming='CharacterEllipsis'/></DataTemplate>");
                profiles.SelectedItem = profiles.Items.OfType<ChatModelChoice>().FirstOrDefault(c => c.Unwrap().Id == current);
                if (profiles.SelectedIndex < 0 && choices.Length == 1) { profiles.SelectedIndex = 0; if (session != null) session.Model = choices[0].Id; }
            }
            catch (Exception error) { state.Text = "Models unavailable · " + error.Message; }
            finally { updating = false; }
        }
        async void Select(CodeSession? selected,CodeProject? selectedProject=null)
        {
            if (codeOperation != null) return;
            draftSave.Stop();if(!TrySaveCurrent())return;
            var owner=selected==null?selectedProject??store.SelectedProject:store.ProjectFor(selected);
            try{if(owner!=null)store.ActivateProject(owner.Id,selected?.Id);}catch(Exception error){state.Text="Project switch failed · "+error.Message;return;}
            foreach (var entry in viewerEntries.Values) entry.Dispose(); viewerEntries.Clear();
            session = selected;project=owner; updating = true; pendingTranscriptTail = true; followTail = true;
            try
            {
                RenderProjects();
                title.Text = selected?.Title ?? project?.Name??"A project for your code"; draft.Text = selected?.Draft ?? "";
                folderLabel.Text=project?.Name??"Choose project";folderPath.Text=project?.Workspace??"";ToolTipService.SetToolTip(projectSwitcher,project?.Workspace??"Add or switch a coding project");
                fileProject.Text=project?.Name??"No project selected";ToolTipService.SetToolTip(fileProject,project?.Workspace??"");fileState.Text="";
                permissions.SelectedIndex = (int)(selected?.Permission ?? CodePermission.ReadOnly);
                shells.SelectedIndex = (int)(selected?.Shell ?? CodeShellKind.Recommended);networkModes.SelectedIndex=(int)(selected?.Network??CodeNetworkMode.Offline); ShowShell();SidebarTab(selected?.SidebarFiles==true);
                agentMode.SelectedIndex = selected?.Mode == "ask" ? 1 : 0;
                reasoning.SelectedItem = (selected?.Reasoning ?? "default") switch { "off" => "Off", "low" => "Low", "medium" => "Medium", "high" => "High", _ => "Default" };
                forcedPanel = null;
                pinned.IsChecked = selected?.ViewerPinned == true; state.Text = selected == null ? "" : selected.Detail is { Length: > 0 } detail ? detail : "Ready · choose a model and describe the work";
                footerText.Text=project==null?"Sessions stay on this computer":project.Name+" · Sessions stay on this computer";
                context.Text = ContextCaption(selected); ContextLabel(); allFiles = []; gitStatus = null; RenderFiles();
            }
            finally { updating = false; }
            RefreshModels(); RenderReferences(); RenderTranscript(); RenderTabs(); Arrange(); Enable();
            DispatcherQueue.TryEnqueue(RestoreTranscriptTail);
            int generation = ++fileGeneration;
            if (selected != null)
            {
                fileSkeleton.Visibility = Visibility.Visible;
                try
                {
                    var listing = await Task.Run(() => new CodeWorkspace(selected.Workspace).Files(".", 1000));
                    if (generation != fileGeneration || selected != session) return;
                    fileSkeleton.Visibility = Visibility.Collapsed;
                    allFiles = listing.Files; RenderFiles();
                    fileState.Text = $"{allFiles.Length} files" + (listing.Truncated ? " · bounded listing" : "");
                    await RefreshGit(selected);
                }
                catch (Exception error) { if (selected == session) state.Text = error.Message; }
                finally { if (generation == fileGeneration) fileSkeleton.Visibility = Visibility.Collapsed; }
            }
            else fileSkeleton.Visibility = Visibility.Collapsed;
        }
        void RenderReferences()
        {
            references.Children.Clear();
            if (session == null) return;
            foreach (var path in session.References.ToArray())
            {
                var button = Button("@" + Path.GetFileName(path) + " ×", (_, _) => { if (codeOperation == null) { session.References.Remove(path); RenderReferences(); Save(); } });
                button.FontSize = 12; button.Padding = new Thickness(6, 3, 6, 3); ToolTipService.SetToolTip(button, path + " · Remove reference"); references.Children.Add(button);
            }
        }
        void RenderTranscript()
        {
            // The composer cannot be used until a session exists. Give the empty
            // project state the space instead of showing a disabled input panel.
            composer.Visibility = sessionHeader.Visibility = session == null ? Visibility.Collapsed : Visibility.Visible;
            ShowMood();
            double offset = transcriptScroll.VerticalOffset; transcript.Children.Clear(); emptyHost = null;
            if (session == null || session.Turns.Count == 0)
            {
                if (session != null)
                {
                    var current = session;
                    emptyHost = CodeSessionIntro(
                        prompt => { draft.Text = prompt; draft.Focus(FocusState.Programmatic); draft.SelectionStart = prompt.Length; },
                        () => { current.SidebarVisible = true; SidebarTab(true); forcedPanel = "workspace"; Arrange(); Save(); });
                }
                else if (project != null) { var owner = project; emptyHost = CodeProjectIntro(owner, () => NewProjectSession(owner), OpenWorkspace); }
                else emptyHost = CodeOnboarding(OpenWorkspace);
                // Centre the intro in the space the transcript would otherwise fill.
                FitWelcome(emptyHost, transcriptScroll.ActualHeight - 60);
                transcript.Children.Add(emptyHost);
                welcomeArt.Visibility = Visibility.Visible;
                shownSession = session; shownTurns = 0; shownLive = false; turnViews.Clear(); liveView = null;
                return;
            }
            welcomeArt.Visibility = Visibility.Collapsed;
            // The engine refreshes this transcript many times a second while it works. Message containers are kept
            // between refreshes, so a message only animates in when it is new to this session's view, and streaming
            // only replaces the text inside the live message (its container, and its entrance, stay put).
            bool animateNew = shownSession == session;
            if (!animateNew) { turnViews.Clear(); liveView = null; }
            var keptViews = new Dictionary<int, (string Key, UIElement View)>();
            string previousRole = ""; bool activityShown = false; int segmentStart = 0, segmentEnd = session.Activities.Count;
            for (int index = 0; index < session.Turns.Count; index++)
            {
                var turn = session.Turns[index];
                if (turn.Role == "user") { activityShown = false; segmentStart = turn.FirstActivity; segmentEnd = session.Turns.Skip(index + 1).FirstOrDefault(t => t.Role == "user")?.FirstActivity ?? session.Activities.Count; }
                if (turn.Text.Length == 0) continue;
                // Keep the opening intent; repeated file-read narration remains in activity/history.
                if (turn.Role == "assistant" && activityShown && turn.ActivityCount == 1 && turn.FirstActivity < session.Activities.Count && session.Activities[turn.FirstActivity].Tool is "read_file" or "list_files" or "search_text") continue;
                string viewKey = turn.Role + "|" + (turn.Role != previousRole) + "|" + turn.Text;
                if (turn.Acceptance is {} plan) viewKey += $"|{plan.Checks.Count}|{plan.Results.Count}|{plan.Results.Count(r=>r.Passed)}";
                if (!turnViews.TryGetValue(index, out var view) || view.Key != viewKey)
                {
                    var body = CodeMarkdown(turn.Text, a => OpenTextArtifact(a));
                    body.Margin = new Thickness(40, 0, 0, 0);
                    UIElement container = body;
                    if (turn.Role != previousRole)
                    {
                        var you = CodeNote("You", 14); you.VerticalAlignment = VerticalAlignment.Center;
                        var speaker = Row(new FontIcon { Glyph = "\uE77B", FontSize = 16, Width = 28 }, you); speaker.Spacing = 12;
                        if (turn.Role == "assistant")
                        {
                            var name = Text("Mason Agent", 15); name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.VerticalAlignment = VerticalAlignment.Center;
                            speaker = Row(MortarImage(28), name); speaker.Spacing = 12;
                        }
                        var content = Stack(speaker, body); content.Spacing = 10; container = content;
                    }
                    if (turn.Acceptance is {} contract)
                    {
                        var lines = new StackPanel { Spacing=5 };
                        foreach(var criterion in contract.Checks)
                        {
                            var observed=contract.Results.FirstOrDefault(r=>r.Check==criterion || System.Text.Json.JsonSerializer.Serialize(r.Check)==System.Text.Json.JsonSerializer.Serialize(criterion));
                            var status=observed==null ? "Pending" : observed.Passed ? "Passed" : "Unverified";
                            var item=CodeNote($"{status} · {criterion.Feature}\n{criterion.Kind} · {criterion.Path} · {criterion.Target} {criterion.Expected}\n{contract.RequirementIds.ElementAtOrDefault(criterion.Unit-1)} · {contract.VerifiedRevision?[..12] ?? "awaiting verification"}".Trim(),12);
                            item.TextWrapping=TextWrapping.Wrap; lines.Children.Add(item);
                        }
                        var details=new Expander { Header=$"Feature checks · {contract.Results.Count(r=>r.Passed)}/{contract.Checks.Count} passed", Content=lines,
                            HorizontalAlignment=HorizontalAlignment.Stretch, HorizontalContentAlignment=HorizontalAlignment.Stretch, Margin=new Thickness(40,8,0,0) };
                        container=Stack(container,details);
                    }
                    // A finished answer takes over from its live view in place, so it does not enter a second time.
                    if (animateNew && index >= shownTurns && !(turn.Role == "assistant" && shownLive)) FadeIn(container);
                    view = (viewKey, container);
                }
                keptViews[index] = view; transcript.Children.Add(view.View);
                if (turn.PerformanceSummary.Length > 0) transcript.Children.Add(CodeNote(turn.PerformanceSummary, 11));
                previousRole = turn.Role;
                if (turn.Role == "assistant" && !activityShown && segmentEnd > segmentStart)
                {
                    transcript.Children.Add(Toolbar(CodeNote(CodeProtocol.ActivitySummary(session.Activities.Skip(segmentStart).Take(segmentEnd - segmentStart))), CodeLink("View activity", (_, _) => OpenTab("activity", "", true)))); activityShown = true;
                }
            }
            if (session.State is not ("Running" or "Awaiting review"))
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
                int changed = session.Changes.Count(c => !c.Restored);
                if (changed > 0) actions.Children.Add(CodeLink(changed == 1 ? "1 changed file" : $"{changed} changed files", (_, _) => OpenTab("changes", "", true)));
                foreach (var artifact in session.Tabs.Where(t => t.Kind is "file" or "web").TakeLast(2))
                    actions.Children.Add(CodeLink(artifact.Kind == "web" ? "Open preview" : artifact.Title, (_, _) => OpenTab(artifact.Kind, artifact.Target, true)));
                if (actions.Children.Count > 0) transcript.Children.Add(actions);
            }
            if (session.LiveMessage.Length > 0)
            {
                bool continues = previousRole == "assistant";
                if (liveView is not { } frame || frame.Continues != continues)
                {
                    var text = new Border { Margin = new Thickness(40, 0, 0, 0) };
                    UIElement container = text;
                    if (!continues)
                    {
                        var liveName = Text("Mason Agent", 15); liveName.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; liveName.VerticalAlignment = VerticalAlignment.Center;
                        var identity = Row(MortarImage(28), liveName); identity.Spacing = 12; container = Stack(identity, text);
                    }
                    if (animateNew && !shownLive) FadeIn(container);
                    frame = (continues, container, text, "");
                    liveView = frame;
                }
                if (frame.Text != session.LiveMessage) { frame.Body.Child = CodeMarkdown(session.LiveMessage); liveView = frame with { Text = session.LiveMessage }; }
                transcript.Children.Add(frame.View);
            }
            else liveView = null;
            turnViews = keptViews; shownSession = session; shownTurns = session.Turns.Count; shownLive = session.LiveMessage.Length > 0;
            transcriptScroll.UpdateLayout(); transcriptScroll.ChangeView(null, followTail ? TranscriptTail() : offset, null, true);
        }
        void OpenTextArtifact(ChatArtifact artifact)
        {
            if (session == null) return;
            // Generated fenced text is inspected as a local native document; it is never executed.
            var entry = new CodeTab { Kind = "answer", Target = artifact.Content };
            OpenTab(entry.Kind, entry.Target, true);
        }
        void OpenTab(string kind, string target, bool user)
        {
            if (session == null) return;
            // Clicking a transcript link is pointer input, not a request to stop following
            // the latest result. Preserve the bottom anchor while the viewer changes width.
            if (user && transcriptScroll.ScrollableHeight - transcriptScroll.VerticalOffset < 90) followTail = true;
            var tab = new CodeTab { Kind = kind, Target = target };
            if (!CodePanels.Open(session, tab, user)) { state.Text = "Close a viewer tab before opening more than 12."; return; }
            if (user) forcedPanel = "viewer"; RenderTabs(); Arrange(); Save();
        }
        void RenderTabs()
        {
            RefreshOutput();
            updating = true;
            try
            {
                var desired = new List<TabViewItem>();
                foreach (var tab in session?.Tabs ?? [])
                {
                    if (!viewerEntries.TryGetValue(tab.Key, out var entry))
                    {
                        entry = BuildCodeViewer(session!, tab, Save, () => { RenderTabs(); viewerChanged?.Invoke(); Save(); }, (kind, target) => OpenTab(kind, target, true)); viewerEntries[tab.Key] = entry;
                    }
                    entry.Refresh?.Invoke(); desired.Add(entry.Tab);
                }
                // Keep mounted content alive. Clearing/readding all native items can leave the
                // selected content presenter on the previous tab and resets embedded browser state.
                for (int index = tabs.TabItems.Count - 1; index >= 0; index--)
                    if (tabs.TabItems[index] is not TabViewItem existing || !desired.Contains(existing)) tabs.TabItems.RemoveAt(index);
                foreach (var tab in desired) if (!tabs.TabItems.Contains(tab)) tabs.TabItems.Add(tab);
                var selected = desired.FirstOrDefault(t => t.Tag is CodeTab model && model.Key == session?.SelectedTab) ?? desired.FirstOrDefault();
                tabs.SelectedItem = selected;
            }
            finally { updating = false; }
        }
        void Arrange()
        {
            double width = page.ActualWidth;
            bool anyProject = store.Projects.Count > 0;
            // Before the first project there is nothing to switch, search or view: onboarding gets the page.
            toolbar.Visibility = anyProject ? Visibility.Visible : Visibility.Collapsed;
            UIElement searchContent = width < 1100 ? searchCompact : searchWide;
            if (search.Content != searchContent) search.Content = searchContent;
            search.MinHeight = 36; ToolTipService.SetToolTip(search, "Search files, sessions or commands");
            AutomationProperties.SetName(search, "Search files or commands");
            viewerLabel.Visibility = width < 620 ? Visibility.Collapsed : Visibility.Visible;
            branch.MaxWidth = 180; branch.Visibility = width < 700 ? Visibility.Collapsed : Visibility.Visible;
            profiles.MinWidth=130;permissions.MinWidth=140;
            ArrangeComposer();
            bool hasArtifacts = session?.Tabs.Count > 0;
            bool sidebar = anyProject && (session?.SidebarVisible ?? emptySidebarVisible) && (width >= 760 || forcedPanel == "workspace");
            bool showViewer = hasArtifacts && session!.ViewerVisible && (width >= 1000 || forcedPanel == "viewer") && !(width <= 1120 && forcedPanel == "workspace");
            if (showViewer && width - (sidebar ? projectPanelWidth+12 : 0) < 1000) sidebar = false;
            bool projectsOnly=sidebar&&width<640;if(projectsOnly)showViewer=false;
            bool viewerOnly = showViewer && width < 1000;
            sideBody.Visibility = projectSurface.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
            page.ColumnDefinitions[0].Width = projectsOnly?new GridLength(1,GridUnitType.Star):new GridLength(sidebar?projectPanelWidth:0); page.ColumnSpacing = sidebar&&!projectsOnly ? 12 : 0;
            page.ColumnDefinitions[1].Width=projectsOnly?new GridLength(0):new GridLength(1,GridUnitType.Star);
            layout.Visibility=projectsOnly?Visibility.Collapsed:Visibility.Visible;
            double ratio = Math.Clamp(session?.ViewerFraction ?? .53, .28, .67);
            center.Visibility = viewerOnly ? Visibility.Collapsed : Visibility.Visible;
            layout.ColumnDefinitions[0].MinWidth = viewerOnly ? 0 : showViewer ? 450 : 300;
            layout.ColumnDefinitions[0].Width = new GridLength(viewerOnly ? 0 : showViewer ? 1 - ratio : 1, GridUnitType.Star);
            layout.ColumnDefinitions[1].Width = new GridLength(showViewer && !viewerOnly ? 8 : 0);
            layout.ColumnDefinitions[2].Width = new GridLength(showViewer ? viewerOnly ? 1 : ratio : 0, GridUnitType.Star);
            viewer.Visibility = showViewer ? Visibility.Visible : Visibility.Collapsed;
            splitter.Visibility = showViewer && !viewerOnly ? Visibility.Visible : Visibility.Collapsed;
            sideToggle.IsChecked = sidebar; viewerToggle.IsChecked = showViewer; viewerToggle.IsEnabled = hasArtifacts;
            ToolTipService.SetToolTip(viewerToggle, hasArtifacts ? "Show or hide artifacts · Ctrl+Alt+V" : "Files, changes and previews appear here when available.");
            viewerHeading.Text = (session?.Tabs.Count ?? 0) == 1 ? "1 tab open" : $"{session?.Tabs.Count ?? 0} tabs open";
            changeButton.Content = session?.Changes.Count(c => !c.Restored) is int changed && changed > 0 ? $"Changes ({changed})" : "Changes";
            pinned.IsEnabled = hasArtifacts;
            Shortcuts();
        }
        void ToggleWorkspace()
        {
            bool show = sideBody.Visibility != Visibility.Visible;
            if (session != null) session.SidebarVisible = show; else emptySidebarVisible = show;
            forcedPanel = show ? "workspace" : null; Arrange(); Save();
        }
        void ToggleViewer()
        {
            if (session == null || session.Tabs.Count == 0) return;
            if (viewer.Visibility == Visibility.Visible) CodePanels.Hide(session); else CodePanels.Show(session);
            forcedPanel = session.ViewerVisible ? "viewer" : null; Arrange(); Save();
        }
        void Enable()
        {
            bool ready = codeOperation == null;
            bool workspaceReady = session != null;
            sessions.IsEnabled = openFolder.IsEnabled = projectSwitcher.IsEnabled=ready;
            shells.IsEnabled = environment.IsEnabled = ready && workspaceReady;
            networkModes.IsEnabled=ready&&workspaceReady;
            deleteSession.IsEnabled = ready && workspaceReady;
            autoNotice.Text = "Workspace sandbox · "+(session?.Network==CodeNetworkMode.Ask?"network asks when needed":"network offline");
            autoNotice.Visibility = session?.Permission == CodePermission.Auto && session.Mode != "ask" ? Visibility.Visible : Visibility.Collapsed;
            profiles.IsEnabled = permissions.IsEnabled = reasoning.IsEnabled = ready && workspaceReady;
            reference.IsEnabled = ready && CodeExplorerSelection(files) is { Directory: false };
            agentMode.IsEnabled = ready && workspaceReady; permissions.IsEnabled = ready && workspaceReady && session?.Mode != "ask";
            activityButton.IsEnabled = contextLink.IsEnabled = branch.IsEnabled = changeButton.IsEnabled = preview.IsEnabled = workspaceReady;
            ToolTipService.SetToolTip(activityButton, workspaceReady ? "Inspect actual tool requests and results" : "Open a workspace folder to start a session.");
            ToolTipService.SetToolTip(contextLink, workspaceReady ? ContextCaption(session) : "Open a workspace folder to start a session.");
            draft.IsEnabled = session != null && ready; send.IsEnabled = session != null && ready;
            send.Visibility = ready ? Visibility.Visible : Visibility.Collapsed; stop.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            spinner.Visibility = ready ? Visibility.Collapsed : Visibility.Visible; spinner.IsActive = !ready;
        }
        void ContextLabel()
        {
            contextLink.Content = session?.PromptTokens is int used && session.ContextTokens is int limit ? $"{used:N0} / {limit:N0}" : "Context";
            ToolTipService.SetToolTip(contextLink, ContextCaption(session) + " · inspect retained checkpoints or compact manually");
        }
        void RefreshRun()
        {
            if (refreshQueued) return; refreshQueued = true;
            DispatcherQueue.TryEnqueue(() => { refreshQueued = false; if (session == null) return;
                // A first run renames "New session"; the project tree binds a snapshot, so rebuild it when the title changes.
                if (title.Text != session.Title) { title.Text = session.Title; RenderProjects(); }
                state.Text = session.Detail; context.Text = ContextCaption(session); ContextLabel(); RenderTranscript(); RenderTabs(); RenderFiles(); Arrange(); _ = RefreshWorkspaceFiles(session); });
        }
        async Task<bool> Approve(CodeApproval request, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); pendingApproval = completion;
            using var registration = cancellation.Register(() => completion.TrySetCanceled(cancellation));
            var detail = CodeScroll(Stack(Text(request.Description, 14), CodeNote(request.Detail))); detail.MaxHeight = 190;
            var allow = Button("Allow once", (_, _) => completion.TrySetResult(true), true);
            var decline = Button("Decline", (_, _) => completion.TrySetResult(false));
            var approvalTitle = Text("Review " + request.Tool.Replace('_', ' '), 17); approvalTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; approvalTitle.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(allow, "Allow " + request.Tool.Replace('_', ' ') + " once"); AutomationProperties.SetName(decline, "Decline " + request.Tool.Replace('_', ' '));
            var approvalCard = ChatSurface(Stack(Row(MascotImage(MoodChecking, 44, "needs your review"), approvalTitle), detail, Row(allow, decline)), new Thickness(14, 10, 14, 12));
            approvalHost.Content = approvalCard; FadeIn(approvalCard);
            try { return await completion.Task; } finally { approvalHost.Content = null; pendingApproval = null; }
        }
        async Task CompactContext()
        {
            if (session == null || codeOperation != null || aiOperation != null) return;
            if (SelectedChatProfile(profiles) is not AiEndpointProfile profile) { state.Text = "Select a coding model before compacting."; return; }
            using var cancellation = new CancellationTokenSource(); codeOperation = aiOperation = cancellation; Enable();
            try
            {
                var key = aiSessionKeys.GetValueOrDefault(profile.Id) ?? Store.Unprotect(profile.Secret);
                var model = new CodeLocalModel(profile, key, session.Reasoning, value => context.Text = value);
                await new CodeEngine(store, model, Approve, RefreshRun).Compact(session, cancellation.Token);
                context.Text = ContextCaption(session); state.Text = session.Detail; RenderTabs();
            }
            catch (Exception error) { state.Text = error.Message; }
            finally { codeOperation = aiOperation = null; Enable(); RenderTabs(); }
        }
        async Task Send()
        {
            if (codeOperation != null || aiOperation != null) { state.Text = "Finish or stop the active AI operation first."; return; }
            if (session == null) { state.Text = "Open a workspace folder first."; return; }
            if (SelectedChatProfile(profiles) is not AiEndpointProfile profile) { state.Text = "Select a coding model from Models & endpoints."; return; }
            string input = draft.Text.Trim(); if (input.Length == 0) return;
            followTail = true; pendingTranscriptTail = true;
            var active = session; using var cancellation = new CancellationTokenSource(); codeOperation = cancellation; aiOperation = cancellation; draftSave.Stop(); Enable();
            try
            {
                var key = aiSessionKeys.GetValueOrDefault(profile.Id) ?? Store.Unprotect(profile.Secret);
                var model = new CodeLocalModel(profile, key, active.Reasoning, value => { context.Text = value; }, progress: value => { active.Detail = value; if (active == session) state.Text = value; });
                var engine = new CodeEngine(store, model, Approve, RefreshRun);
                var task = engine.Run(active, input, cancellation.Token); updating = true; draft.Text = ""; updating = false;
                await task; updating = true; draft.Text = active.Draft; updating = false; RenderTranscript(); RenderTabs(); Arrange(); state.Text = active.Detail; title.Text = active.Title;
                try
                {
                    var listing = await Task.Run(() => new CodeWorkspace(active.Workspace).Files(".", 1000));
                    if (active == session)
                    {
                        allFiles = listing.Files; RenderFiles();
                        fileState.Text = $"{allFiles.Length} files" + (listing.Truncated ? " · bounded listing" : "");
                        await RefreshGit(active);
                    }
                }
                catch (Exception error) { fileState.Text = "File listing refresh failed · " + error.Message; }
            }
            catch (Exception error) { state.Text = error.Message; }
            finally { codeOperation = null; if (aiOperation == cancellation) aiOperation = null; Enable(); updating = true; draft.Text = active.Draft; RenderProjects(); updating = false; ShowMood(); footerText.Text = (project?.Name??Path.GetFileName(active.Workspace)) + " · Session saved locally"; Save(); }
        }
    }
}
