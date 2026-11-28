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
    Func<IEnumerable<TitleHit>>? codeSearchHits;
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
        var references = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var reviewButton = Button("Review comments", (_, _) => {});
        FrameworkElement stateRow = null!;
        CodeSession? session = store.SelectedSession;
        CodeEngine? liveEngine=null;
        CodeSession? liveOwner=null;
        string? submittedDraft = null;
        StackPanel followUpButtons=null!;
        CodeProject? project = store.SelectedProject;
        const double projectPanelWidth=232;
        ReviewDesk? reviewDesk = null;
        HyperlinkButton contextLink = null!;
        TextBox draft = null!;
        ComboBox reasoning = null!; Segmented agentMode = null!;
        var approvalHost = new ContentControl();
        TaskCompletionSource<bool>? pendingApproval = null;
        Grid transcriptBody = null!;
        ProgressRing spinner = null!;
        Button stop = null!, newSession = null!;
        var reviewHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        ComboBox viewPicker = null!;
        var viewModes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        CodeWorkspaceView View() => session?.WorkspaceView == CodeWorkspaceView.Review ? CodeWorkspaceView.Review : CodeWorkspaceView.Workbench;
        Action? viewerChanged = null; // assigned once the page exists; refreshes counts outside the viewer
        bool updating = false, refreshQueued = false, emptySidebarVisible = true; string[] allFiles = []; int fileGeneration = 0; string? forcedPanel = null; CodeGitStatus? gitStatus = null;
        Button send = null!, openFolder = null!, branch = null!, changeButton = null!, preview = null!, deliveryChip = null!, tasksChip = null!;
        ContentControl surfaceHome = null!; StackPanel surfaceShelf = null!;
        Button websiteShortcut=null!;
        FrameworkElement composer = null!, sessionHeader = null!;
        Grid toolbar = null!; TextBlock viewerLabel = null!; Image stateMood = null!; string shownMood = "";
        Grid? emptyHost = null; // onboarding or intro shown while there is no conversation
        Border projectSurface = null!;
        HyperlinkButton reference = null!, activityButton = null!;
        ToggleButton sideToggle = null!, viewerToggle = null!, pinned = null!;
        KeyboardAccelerator workspaceShortcut = null!, viewerShortcut = null!, notesShortcut = null!;
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
        Grid.SetRow(reviewHost, 1); Grid.SetColumn(reviewHost, 1); page.Children.Add(reviewHost);
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
        var networkModes=new ComboBox{ItemsSource=new[]{"Off: no internet","Ask for commands and web","Commands offline, web asks"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};AutomationProperties.SetName(networkModes,"Command network policy");
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
        var title = Heading("New coding session", 23); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.MaxLines = 1; title.TextTrimming = TextTrimming.CharacterEllipsis;
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
        StackPanel? resultActions = null;
        var transcriptScroll = CodeScroll(transcript); bool followTail = true, pendingTranscriptTail = true;
        // The shape of a file list while the project's files are being listed (added to the Files area below).
        var fileSkeleton = Fluent.Skeleton(.5, .72, .62, .8, .44, .68, .56); fileSkeleton.Margin = new Thickness(6, 8, 6, 0); fileSkeleton.Visibility = Visibility.Collapsed;
        // Behind the onboarding and intro states only, across the whole conversation area rather than the reading column.
        var welcomeArt = MasonryBackdrop(transcriptScroll); Grid.SetRow(welcomeArt, 1);
        // What the transcript showed last time, so only messages new to this session's view animate in (RenderTranscript).
        var evidenceViews = new Dictionary<CodeTurn, (AcceptanceCheck[] Checks, AcceptanceResult[] Results, string? Revision, EvidenceView View)>();
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
        transcriptScroll.SizeChanged += (_, _) => { transcript.Width = Math.Max(200, Math.Min(transcript.MaxWidth, transcriptScroll.ActualWidth - 56)); if (emptyHost != null) FitWelcome(emptyHost, transcriptScroll.ActualHeight - 60); };
        var state = CodeNote("");
        var context = CodeNote("Context measured before each action"); context.MaxLines = 2; context.TextTrimming = TextTrimming.CharacterEllipsis;
        contextLink = CodeLink("Context", (_, _) => OpenTab("context", "", true)); contextLink.FontSize = 12;
        AutomationProperties.SetName(contextLink, "Coding context and checkpoints");
        draft = new TextBox { PlaceholderText = "Describe a change, ask about the repo, or @ a file…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 170, MaxLength = 16000, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(draft,"Build keeps the current task’s requirements across follow-ups. Type /new followed by a request to start a separate task.");
        AutomationProperties.SetName(draft, "Coding message"); ComposerBox(draft);
        reasoning = new ComboBox { ItemsSource = new[] { "Default", "Off", "Low", "Medium", "High" }, SelectedIndex = 0, MinWidth = 95 }; AutomationProperties.SetName(reasoning, "Coding reasoning");
        agentMode = new Segmented("Coding mode", new (string Label, string? Glyph)[] { ("Build", ""), ("Ask", "") }, 0, stretch: true);
        ToolTipService.SetToolTip(agentMode.View, "Build performs workspace work. Ask explores without file writes or commands.");
        spinner = BrandControl(new ProgressRing { Width = 15, Height = 15, Visibility = Visibility.Collapsed });
        stop = Button("Stop", (_, _) => codeOperation?.Cancel()); stop.Visibility = Visibility.Collapsed;
        send = ChatIcon("Send coding message", "\uE724", async (_, _) => await Send()); send.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; BrandAccent(send);
        tabs = InterfaceTabs.Apply(new TabView { IsAddTabButtonVisible = false, TabWidthMode = TabViewWidthMode.SizeToContent, CanDragTabs = false, CanReorderTabs = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }, TabPresentation.Document);
        pinned = ChipToggle(new ToggleButton { Content = new FontIcon { Glyph = "\uE718", FontSize = 15 }, Padding = new Thickness(8) }); AutomationProperties.SetName(pinned, "Pin current viewer tab"); ToolTipService.SetToolTip(pinned, "Keep the agent from switching the selected viewer tab");
        pinned.Click += (_, _) => { if (session != null) { session.ViewerPinned = pinned.IsChecked == true; Save(); } };
        sideToggle = ChipToggle(new ToggleButton { Content = new FontIcon { Glyph = "\uE8FD", FontSize = 15 }, Padding = new Thickness(9), MinWidth = 36, MinHeight = 36 });
        viewerLabel = new TextBlock { Text = "Viewer" };
        viewerToggle = ChipToggle(new ToggleButton { Content = Row(new FontIcon { Glyph = "\uE8A5", FontSize = 14 }, viewerLabel), Padding = new Thickness(10, 7, 10, 7), MinHeight = 36 });
        AutomationProperties.SetName(sideToggle, "Toggle projects panel"); ToolTipService.SetToolTip(sideToggle, "Show or hide projects and files · Ctrl+B");
        AutomationProperties.SetName(viewerToggle, "Toggle artifact viewer");
        sideToggle.Click += (_, _) => ToggleWorkspace(); viewerToggle.Click += (_, _) => ToggleViewer();
        var closeViewer = ChatIcon("Close artifact viewer", "\uE711", (_, _) => { if (session != null) CodePanels.Hide(session); Arrange(); Save(); });
        changeButton = Button("Changes", (_, _) => OpenTab("changes", "", true));
        activityButton = CodeLink("Moments", (_, _) => OpenTab("activity", "", true));
        async Task OpenWebsite()
        {
            if (session == null || dialogOpen) return;
            var owner=session;
            var address = new TextBox { PlaceholderText = "HTML file or local server URL" }; AutomationProperties.SetName(address, "Website address");
            try
            {
                var site=new CodeWebsite(new CodeWorkspace(owner.Workspace));
                if (site.PreviewTarget(owner) is {} target) { OpenTab(target.Kind, target.Target, true); return; }
                dialogOpen=true;
                if(await new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Open website",Content=address,PrimaryButtonText="Open",CloseButtonText="Cancel"}.ShowAsync()!=ContentDialogResult.Primary || owner!=session)return;
                var value=address.Text.Trim();
                if(CodeWebsite.IsPage(value) && !Uri.TryCreate(value,UriKind.Absolute,out _)){site.Page(value);OpenTab("site",value,true);}
                else OpenTab("web",CodeProtocol.PreviewUri(value).AbsoluteUri,true);
            }
            catch (Exception error) { state.Text = error.Message; }
            finally { dialogOpen = false; }
        }
        preview=Button("Website",async (_,_)=>await OpenWebsite());
        websiteShortcut=ChatIcon("Open project website","\uE774",async (_,_)=>await OpenWebsite());
        ToolTipService.SetToolTip(websiteShortcut,"Open project website");
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
        var workspaceHeading = Text("Projects", 14); workspaceHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var closeWorkspace = ChatIcon("Close projects panel", "\uE711", (_, _) => ToggleWorkspace());closeWorkspace.Padding=new Thickness(5);closeWorkspace.MinWidth=28;
        projectsTab=SegmentToggle(new ToggleButton{Content="Projects",IsChecked=true});filesTab=SegmentToggle(new ToggleButton{Content="Files"});
        AutomationProperties.SetName(projectsTab,"Show project sessions");AutomationProperties.SetName(filesTab,"Show project files");
        var sideTabs=Row(projectsTab,filesTab);sideTabs.Spacing=2;sideBody.Children.Add(Toolbar(new Border{Child=sideTabs,Style=Token("MasonSegmentTrack")},closeWorkspace));
        newSession = Button("New session", (_, _) => { if (project != null) NewProjectSession(project); });
        AutomationProperties.SetName(newSession, "New session in selected project");
        projectList=Vertical(Row(openFolder,newSession),sessions);projectList.Children.Add(noProjects);Grid.SetRow(noProjects,1);Grid.SetRow(projectList,1);sideBody.Children.Add(projectList);
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
        foreach (var tool in new FrameworkElement[] { agentMode.View, profiles, permissions, environment })
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
        followUpButtons=Row(Button("Steer",(_,_)=>SubmitFollowUp(true)),Button("Queue",(_,_)=>SubmitFollowUp(false)));
        followUpButtons.Visibility=Visibility.Collapsed;
        var composerFooter = Toolbar(composerHint, contextLink, Row(followUpButtons,send));
        reviewButton.HorizontalAlignment = HorizontalAlignment.Left;
        reviewButton.Visibility = Visibility.Collapsed;
        var composerBody = Stack(references, reviewButton, draft, composerTools, composerFooter, autoNotice);
        composerBody.Spacing = 8;
        // The composer floats above the transcript, with the window's shared soft shadow beneath it.
        var composerSurface = Fluent.Float(ChatSurface(composerBody, new Thickness(12, 8, 10, 10)), 24);
        composerSurface.CornerRadius = new CornerRadius(12); composer = composerSurface;
        transcriptBody = new Grid { RowSpacing = 10, Padding = new Thickness(10, 0, 10, 8), MaxWidth = 960 };
        transcriptBody.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); transcriptBody.RowDefinitions.Add(new() { Height = GridLength.Auto }); transcriptBody.RowDefinitions.Add(new() { Height = GridLength.Auto }); transcriptBody.RowDefinitions.Add(new() { Height = GridLength.Auto });
        transcriptBody.Children.Add(transcriptScroll); Grid.SetRow(approvalHost, 1); transcriptBody.Children.Add(approvalHost);
        // The pose mirrors the session's recorded state. While it runs, the latest tool call says whether it is
        // reading, editing or running checks, and planning between calls. Hidden for states without a pose
        // and while the intro already shows one.
        stateMood = new Image { Height = 46, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        (string, string) RunningPose()
        {
            if (session!.RepairingFile != null) return (PoseEditing, "repairing " + session.RepairingFile);
            var latest = session.Activities.LastOrDefault();
            var pose = latest is { State: "Pending" or "Running" } ? PoseForTool(latest.Tool) : PosePlanning;
            return (pose, pose switch { PoseReading => "reading files", PoseEditing => "editing", PoseChecks => "running checks", _ => "planning" });
        }
        void ShowMood()
        {
            bool restored = (DateTimeOffset.Now - restoredAt).TotalSeconds < 30;
            var (mood, meaning) = session == null || session.Turns.Count == 0 ? ("", "") : session.State switch
            {
                "Running" => RunningPose(),
                _ when restored => (PoseRestored, "files restored"),
                "Awaiting review" => (PoseApproval, "waiting for your decision"),
                "Waiting for input" => (PoseListening, "waiting for your answer"),
                // Ready to review when the session left reviewable changes; connected and calm when it only answered.
                "Completed" => session.Changes.Any(c => !c.Restored) ? (PoseReview, "ready to review") : (PoseConnected, "finished"),
                _ when CodeRunEnding.Look(session.Ending?.Code) is { } look => look.Tone switch
                {
                    CodeEndingTone.Danger => (PoseFailed, look.Headline.ToLowerInvariant()),
                    _ when session.Ending!.Code == CodeRunEnding.Unverified => (PoseFailed, "a check failed"),
                    _ when session.Ending!.Code == CodeRunEnding.Question => (PoseListening, "waiting for your answer"),
                    _ => session.Changes.Any(c => !c.Restored) ? (PoseReview, look.Headline.ToLowerInvariant() + ", changes kept") : (PoseConnected, look.Headline.ToLowerInvariant())
                },
                "Failed" or "Blocked" or "Needs verification" => (PoseFailed, "a check failed"),
                _ => ("", "")
            };
            endingHost.Child = session?.State is "Running" or "Awaiting review" ? null : EndingPill(session?.Ending);
            stateMood.Visibility = mood.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (mood == shownMood) return;
            shownMood = mood;
            if (mood.Length == 0) return;
            stateMood.Source = PoseSource(mood, 46);
            AutomationProperties.SetName(stateMood, "Mason Agent: " + meaning);
            FadeIn(stateMood);
        }
        refreshStatusPose = ShowMood;
        state.VerticalAlignment = VerticalAlignment.Center;
        state.TextWrapping = TextWrapping.Wrap;
        var progressRow = new Grid { ColumnSpacing = 8 };
        progressRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        progressRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.Children.Add(Row(stateMood, spinner, endingHost));
        Grid.SetColumn(state, 1);
        progressRow.Children.Add(state);
        stateRow = Toolbar(progressRow, stop); Grid.SetRow(stateRow, 2); transcriptBody.Children.Add(stateRow);
        notesTray = new NotesTray(
            () => session == null ? null : new NotesPlace(store.SettingsRoot, new CodeWorkspace(session.Workspace), store.ProjectFor(session)?.Notes ?? []),
            () => codeOperation != null,
            () => session?.Tabs.FirstOrDefault(t => t.Key == session.SelectedTab && t.Kind == "file")?.Target);
        // Both span the conversation and the request box, anchored to the bottom: the tray tucks 14 px behind the
        // request box and the tab sits on its top edge. Drawing order puts the tray behind, the tab in front.
        foreach (var layer in new FrameworkElement[] { notesTray.Tray, notesTray.Tab }) { Grid.SetRowSpan(layer, 4); layer.VerticalAlignment = VerticalAlignment.Bottom; }
        // On the right, so it never covers the status pose that sits at the left above the request box.
        notesTray.Tab.HorizontalAlignment = HorizontalAlignment.Right;
        void PlaceNotes()
        {
            notesTray.Tray.Margin = new Thickness(10, 0, 10, Math.Max(0, composer.ActualHeight - 14));
            notesTray.Tray.MaxHeight = Math.Max(160, transcriptBody.ActualHeight - composer.ActualHeight - 24);
            notesTray.Tab.Margin = new Thickness(0, 0, 26, Math.Max(0, composer.ActualHeight - 1));
        }
        composer.SizeChanged += (_, _) => PlaceNotes(); transcriptBody.SizeChanged += (_, _) => PlaceNotes();
        transcriptBody.Children.Add(notesTray.Tray);
        Grid.SetRow(composer, 3); transcriptBody.Children.Add(composer);
        transcriptBody.Children.Add(notesTray.Tab);
        notesTray.Changed += () => notesTray.Tab.Visibility = session == null ? Visibility.Collapsed : Visibility.Visible;
        void UpdateStatusRow() => stateRow.Visibility = session?.State == "Completed" && state.Text == session.Detail
            ? Visibility.Collapsed : Visibility.Visible;
        state.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateStatusRow());
        var sessionTools=ChatIcon("Session settings","\uE713",(_,_)=>{});
        var attach=Button("Attach or detach a window",async (_,_)=>{
            if(session==null||codeOperation!=null||dialogOpen)return;
            sessionTools.Flyout.Hide();dialogOpen=true;
            try{
                var windows=CodeDesktop.Windows();
                var picker=new ComboBox{ItemsSource=windows,PlaceholderText="Choose an open window",MinWidth=280};
                var dialog=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Computer use",
                    Content=Stack(CodeNote(session.Desktop?.Target.Title??"No window attached"),CodeNote("Mason can read control names in the selected window. You review each click or text entry."),picker),
                    PrimaryButtonText="Attach",SecondaryButtonText="Detach",CloseButtonText="Cancel",IsPrimaryButtonEnabled=false};
                picker.SelectionChanged+=(_,_)=>dialog.IsPrimaryButtonEnabled=picker.SelectedItem!=null;
                var result=await dialog.ShowAsync();
                if(result==ContentDialogResult.Primary && picker.SelectedItem is DesktopTarget selected)session.Desktop=new CodeDesktop(selected);
                else if(result==ContentDialogResult.Secondary)session.Desktop=null;
            }catch(Exception e){state.Text=e.Message;}finally{dialogOpen=false;}
        });
        var rewind=Button("Rewind last edited turn",async (_,_)=>{
            if(session==null || codeOperation!=null)return;
            if(CodeRewind.Latest(session) is not {} turn){state.Text="No recorded file edits to rewind.";return;}
            sessionTools.Flyout.Hide();
            var list=string.Join("\n",turn.Edits.Select(e=>(e.Before==null?"Remove created file · ":"Restore · ")+e.Path));
            if(await new ContentDialog{XamlRoot=root.XamlRoot,Title="Rewind this turn’s file edits?",Content=list,PrimaryButtonText="Rewind files",CloseButtonText="Cancel"}.ShowAsync()!=ContentDialogResult.Primary)return;
            try{CodeRewind.Apply(store,session,turn.Id);restoredAt=DateTimeOffset.Now;state.Text=session.Detail;RenderTranscript();RenderTabs();}catch(Exception e){state.Text=e.Message;}
        });
        // G3: undo or redo everything a request changed, including files changed by commands (workspace snapshots).
        async Task UndoRequest(bool redo)
        {
            if(session==null||codeOperation!=null||dialogOpen)return;
            sessionTools.Flyout.Hide();
            var turn=redo?CodeRewind.LatestRedoable(session):CodeRewind.LatestUndoable(session);
            if(turn==null){state.Text=redo?"Nothing to redo.":"No request with recorded snapshots to undo yet. Requests record them from this version on.";return;}
            SnapshotChange[] plan;
            try{plan=redo?CodeSnapshots.Plan(store.SettingsRoot,session.Workspace,turn.SnapshotBefore!.Value,turn.SnapshotAfter!.Value):CodeRewind.PreviewUndo(store,session,turn);}
            catch(Exception e){state.Text=e.Message;return;}
            if(plan.Length==0){state.Text="That request did not change any files.";}
            var rows=new StackPanel{Spacing=6};
            foreach(var change in plan.Take(40)){
                var (glyph,verb)=change.Conflict?("\uE7BA","Changed since · kept"):(redo?change.To:change.From)==null?("\uE74D","Remove"):(redo?change.From:change.To)==null?("\uE710","Recreate"):("\uE777","Restore");
                var line=Row(Glyph(glyph,14),new TextBlock{Text=change.Path,Style=Token("MasonMono"),VerticalAlignment=VerticalAlignment.Center},new TextBlock{Text=verb,Style=Token("MasonCaptionSecondary"),VerticalAlignment=VerticalAlignment.Center});
                rows.Children.Add(line);
            }
            if(plan.Length>40)rows.Children.Add(Muted($"…and {plan.Length-40} more files",12));
            var content=Stack(Muted((redo?"Redo puts back what “":"Undo returns the workspace to before “")+(turn.Text.Length>70?turn.Text[..70]+"…":turn.Text)+"”. Files you changed since are left as they are.",14),new ScrollViewer{Content=rows,MaxHeight=280});
            dialogOpen=true;
            try{
                if(await new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title=redo?"Redo this request?":"Undo this request?",Content=content,PrimaryButtonText=redo?"Redo":"Undo",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Primary}.ShowAsync()!=ContentDialogResult.Primary)return;
                var result=redo?CodeRewind.Redo(store,session,turn.Id):CodeRewind.Undo(store,session,turn.Id);
                restoredAt=DateTimeOffset.Now;
                state.Text=session.Detail;RenderTranscript();RenderTabs();_=RefreshWorkspaceFiles(session);
            }catch(Exception e){state.Text=e.Message;}finally{dialogOpen=false;}
        }
        // G8: fork from an earlier request; export and import a session file.
        async Task ForkSession()
        {
            if(session==null||codeOperation!=null||dialogOpen)return;
            sessionTools.Flyout.Hide();
            var requests=session.Turns.Where(CodeSessionTransfer.CanFork).Reverse().ToArray();
            if(requests.Length==0){state.Text="Forking works for requests sent from this version on.";return;}
            var picker=new ComboBox{Header="Fork before",ItemsSource=requests.Select(t=>t.Text.Length>80?t.Text[..80]+"…":t.Text).ToArray(),SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
            var restore=new ToggleSwitch{Header="Also return the files to how they were before it",IsOn=false};
            dialogOpen=true;
            try{
                if(await new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Fork this session",Content=Stack(Muted("A new session keeps the conversation up to the chosen request and puts that request in the composer, so you can change it and send it again.",14),picker,restore),PrimaryButtonText="Fork",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Primary}.ShowAsync()!=ContentDialogResult.Primary)return;
                if(!TrySaveCurrent())return;
                var (fork,_)=CodeSessionTransfer.Fork(store,session,requests[picker.SelectedIndex].Id,restore.IsOn);
                Select(fork,store.ProjectFor(fork));state.Text=fork.Detail;
            }catch(Exception e){state.Text=e.Message;}finally{dialogOpen=false;}
        }
        async Task ExportSession()
        {
            if(session==null||dialogOpen)return;
            sessionTools.Flyout.Hide();
            var sanitize=new ToggleSwitch{Header="Remove the project path and anything that looks like a key or token",IsOn=true};
            dialogOpen=true;
            try{
                if(await new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Export session",Content=Stack(Muted("Saves the conversation and activity as a JSON file you can import on another computer. Undo history stays here.",14),sanitize),PrimaryButtonText="Choose file…",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Primary}.ShowAsync()!=ContentDialogResult.Primary)return;
                var picker=new Windows.Storage.Pickers.FileSavePicker{SuggestedFileName=string.Join("_",session.Title.Split(Path.GetInvalidFileNameChars()))};
                picker.FileTypeChoices.Add("Mason session",new List<string>{".mason-session.json"});
                WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file=await picker.PickSaveFileAsync();if(file==null)return;
                await File.WriteAllTextAsync(file.Path,CodeSessionTransfer.Export(session,sanitize.IsOn));
                state.Text="Exported to "+file.Path;
            }catch(Exception e){state.Text=e.Message;}finally{dialogOpen=false;}
        }
        async Task ImportSession()
        {
            if(session==null||codeOperation!=null)return;
            sessionTools.Flyout.Hide();
            try{
                var picker=new Windows.Storage.Pickers.FileOpenPicker();picker.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file=await picker.PickSingleFileAsync();if(file==null||!TrySaveCurrent())return;
                var imported=CodeSessionTransfer.Import(store,await File.ReadAllTextAsync(file.Path),session.Workspace);
                Select(imported,store.ProjectFor(imported));state.Text=imported.Detail;
            }catch(Exception e){state.Text="Could not import · "+e.Message;}
        }
        // G5: custom agents from .mason/agents, .agents/agents or .claude/agents.
        var agentPicker=new ComboBox{Header="Agent",PlaceholderText="Default",HorizontalAlignment=HorizontalAlignment.Stretch};
        ToolTipService.SetToolTip(agentPicker,"Custom agents are Markdown files in .mason/agents (also .agents/agents and .claude/agents). Their text becomes standing instructions; mode: plan makes the session Ask-only.");
        bool agentSyncing=false;
        void LoadAgents()
        {
            if(session==null)return;
            agentSyncing=true;
            try{
                var agents=CodeCustomization.Agents(store.SettingsRoot,session.Workspace);
                var names=new List<string>{"Default"};names.AddRange(agents.Select(a=>a.Name+(a.Description.Length>0?" · "+a.Description:"")));
                agentPicker.ItemsSource=names;
                var index=session.Agent==null?0:agents.ToList().FindIndex(a=>a.Name.Equals(session.Agent,StringComparison.OrdinalIgnoreCase))+1;
                agentPicker.SelectedIndex=Math.Max(0,index);agentPicker.Tag=agents;
            }catch(Exception e){agentPicker.ItemsSource=new[]{"Default"};agentPicker.SelectedIndex=0;ToolTipService.SetToolTip(agentPicker,"Agents could not be read: "+e.Message);}
            finally{agentSyncing=false;}
        }
        agentPicker.SelectionChanged+=(_,_)=>{
            if(agentSyncing||session==null||agentPicker.Tag is not IReadOnlyList<CodeDefinition> agents)return;
            var chosen=agentPicker.SelectedIndex<=0?null:agents[agentPicker.SelectedIndex-1];
            session.Agent=chosen?.Name;
            if(chosen?.Mode=="ask"){session.Mode="ask";agentMode.SelectedIndex=1;}
            Save();state.Text=chosen==null?"Default agent":"Agent · "+chosen.Name+(chosen.Mode=="ask"?" (Ask only)":"");
        };
        Button MenuItem(string glyph,string label,Func<Task> action){var b=SubtleButton(label,async(_,_)=>await action(),glyph);b.HorizontalAlignment=HorizontalAlignment.Stretch;b.HorizontalContentAlignment=HorizontalAlignment.Left;return b;}
        attach.Style=Token("SubtleButtonStyle");attach.HorizontalAlignment=HorizontalAlignment.Stretch;attach.HorizontalContentAlignment=HorizontalAlignment.Left;attach.Content=Row(Glyph("\uE7F4"),new TextBlock{Text="Attach or detach a window"});
        rewind.Style=Token("SubtleButtonStyle");rewind.HorizontalAlignment=HorizontalAlignment.Stretch;rewind.HorizontalContentAlignment=HorizontalAlignment.Left;rewind.Content=Row(Glyph("\uE81C"),new TextBlock{Text="Rewind edit-tool changes only"});
        var undoButton=MenuItem("\uE7A7","Undo last request",()=>UndoRequest(false));
        var redoButton=MenuItem("\uE7A6","Redo",()=>UndoRequest(true));
        var sessionMenu=new StackPanel{Spacing=2,Width=320};
        reasoning.Header="Reasoning";reasoning.HorizontalAlignment=HorizontalAlignment.Stretch;
        sessionMenu.Children.Add(new TextBlock{Text="Session",Style=Token("MasonBodyStrong"),Margin=new Thickness(0,0,0,8)});
        sessionMenu.Children.Add(reasoning);
        var agentHolder=new Border{Child=agentPicker,Margin=new Thickness(0,8,0,8)};sessionMenu.Children.Add(agentHolder);
        sessionMenu.Children.Add(Divider());
        foreach(var item in new FrameworkElement[]{undoButton,redoButton,rewind,MenuItem("\uE8C8","Fork from a request…",ForkSession),MenuItem("","Fork into a worktree…",ForkIntoWorktree),MenuItem("\uEDE1","Export session…",ExportSession),MenuItem("\uE8B5","Import a session…",ImportSession),attach})sessionMenu.Children.Add(item);
        sessionMenu.Children.Add(Divider());
        deleteSession.Style=Token("SubtleButtonStyle");deleteSession.HorizontalAlignment=HorizontalAlignment.Stretch;deleteSession.HorizontalContentAlignment=HorizontalAlignment.Left;deleteSession.Width=double.NaN;deleteSession.Content=Row(Glyph("\uE74D"),new TextBlock{Text="Delete session"});
        sessionMenu.Children.Add(deleteSession);
        var sessionFlyout=Fluent.Acrylic(new Flyout{Content=sessionMenu});
        sessionFlyout.Opening+=(_,_)=>{LoadAgents();undoButton.IsEnabled=session!=null&&CodeRewind.LatestUndoable(session)!=null;redoButton.IsEnabled=session!=null&&CodeRewind.LatestRedoable(session)!=null;};
        sessionTools.Flyout=sessionFlyout;
        sessionTools.Content=MotifIcon(Fluent.Motif.Settings,16);
        ToolTipService.SetToolTip(sessionTools,"Reasoning and session actions");
        var titleBlock = Stack(title, folderPath); titleBlock.Spacing = 1; titleBlock.VerticalAlignment = VerticalAlignment.Center;
        sessionHeader = Toolbar(titleBlock, activityButton, sessionTools);
        sessionHeader.Margin = new Thickness(10, 2, 10, 0);
        center = Vertical(sessionHeader, transcriptBody); center.Children.Insert(0, welcomeArt); layout.Children.Add(center);        preview.Content = Row(new FontIcon { Glyph = "\uE774", FontSize = 14 }, new TextBlock { Text = "Website" });
        preview.Padding = new Thickness(8);
        AutomationProperties.SetName(changeButton, "Review session file changes"); ToolTipService.SetToolTip(changeButton, "Review session file changes");
        AutomationProperties.SetName(preview, "Open local website preview"); ToolTipService.SetToolTip(preview, "Open local website preview");

        outputTitle=CodeNote("Command output",12);var outputText=ChatInline("",12,true);
        outputToggle=ChipToggle(new ToggleButton{Content="Output",IsChecked=true});AutomationProperties.SetName(outputToggle,"Toggle command output");
        outputScroll=CodeScroll(outputText);outputScroll.MaxHeight=180;
        outputPanel=Stack(outputTitle,outputScroll);outputPanel.Margin=new Thickness(4,6,4,2);outputPanel.Spacing=6;
        var workBody=new Grid{RowSpacing=4};workBody.RowDefinitions.Add(new(){Height=GridLength.Auto});workBody.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});workBody.RowDefinitions.Add(new(){Height=GridLength.Auto});
        // The viewer is one surface: a header (Moments, the shelf of what is open or made, and the viewer's buttons) over the content.
        // The TabView keeps hosting each view, selection and closing; its own strip is hidden once its template loads.
        var surfaceHeader=new Grid{ColumnSpacing=8,Padding=new Thickness(8,6,4,6)};
        surfaceHeader.ColumnDefinitions.Add(new(){Width=GridLength.Auto});surfaceHeader.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});surfaceHeader.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        workBody.Children.Add(surfaceHeader);Grid.SetRow(tabs,1);workBody.Children.Add(tabs);Grid.SetRow(outputPanel,2);workBody.Children.Add(outputPanel);
        tabs.Loaded+=(_,_)=>HideTabStrip(tabs);tabs.LayoutUpdated+=(_,_)=>HideTabStrip(tabs);
        outputToggle.Click+=(_,_)=>{if(session!=null){session.OutputVisible=outputToggle.IsChecked==true;RefreshOutput();Save();}};
        void RefreshOutput()
        {
            var last=session?.Activities.LastOrDefault(a=>a.Tool is "run_shell" or "run_command");
            // Moments already shows every command with its output, so the panel is for the other views.
            bool onMoments=((tabs.SelectedItem as TabViewItem)?.Tag as CodeTab)?.Kind=="activity";
            outputToggle.Visibility=last==null||onMoments?Visibility.Collapsed:Visibility.Visible;outputToggle.IsChecked=session?.OutputVisible==true;
            outputPanel.Visibility=last!=null&&!onMoments&&session?.OutputVisible==true?Visibility.Visible:Visibility.Collapsed;
            if(last!=null){outputTitle.Text="Command output · "+last.State+"\n"+CodeActivityText.Command(last);var text=CodeActivityText.Result(last);outputScroll.Content=ChatInline(text.Length>12000?text[^12000..]:text,12,true);}
        }
        var surfaceMore=ChatIcon("More viewer pages","\uE712",(_,_)=>{});
        var morePages=Fluent.Acrylic(new MenuFlyout());
        foreach(var (label,kind) in new[]{("Plan & repository map","plan"),("Model roles & decisions","roles"),("Background tasks","tasks"),("Context checkpoints","context")})
        {var item=new MenuFlyoutItem{Text=label};item.Click+=(_,_)=>OpenTab(kind,"",true);morePages.Items.Add(item);}
        surfaceMore.Flyout=morePages;
        preview.Content=new FontIcon{Glyph="",FontSize=15};preview.Padding=new Thickness(8);
        var surfaceRight=Row(preview,outputToggle,pinned,surfaceMore,closeViewer);surfaceRight.Spacing=4;surfaceRight.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(surfaceRight,2);surfaceHeader.Children.Add(surfaceRight);
        surfaceHome=new ContentControl{VerticalAlignment=VerticalAlignment.Center};surfaceHeader.Children.Add(surfaceHome);
        surfaceShelf=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
        var shelfScroll=new ScrollViewer{Content=surfaceShelf,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollMode=ScrollMode.Enabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollMode=ScrollMode.Disabled,VerticalAlignment=VerticalAlignment.Center};
        AutomationProperties.SetName(shelfScroll,"Open and made in this session");
        Grid.SetColumn(shelfScroll,1);surfaceHeader.Children.Add(shelfScroll);
        void CloseViewerTab(CodeTab tab)
        {
            if(session==null)return;
            if(viewerEntries.Remove(tab.Key,out var entry))entry.Dispose();CodePanels.Close(session,tab.Key);
            RenderTabs();RefreshResultActions();Arrange();Save();
        }
        void ShowMoments(string? location)
        {
            if(session==null)return;
            OpenTab("activity","",true);
            if(session.Tabs.FirstOrDefault(t=>t.Kind=="activity") is not {} moments)return;
            moments.Location=location;moments.Scroll=0;session.SelectedTab=moments.Key;
            if(viewerEntries.TryGetValue(moments.Key,out var live))live.Refresh?.Invoke();
            RenderTabs();Save();
        }
        // The header follows what the surface shows: Moments itself, or the way back to it from anything opened.
        void RenderSurfaceHeader()
        {
            surfaceShelf.Children.Clear();
            if(session==null){surfaceHome.Content=null;return;}
            var owner=session;
            var selectedTab=(tabs.SelectedItem as TabViewItem)?.Tag as CodeTab??owner.Tabs.FirstOrDefault(t=>t.Key==owner.SelectedTab)??owner.Tabs.FirstOrDefault();
            bool running=owner.State is "Running" or "Awaiting review";
            if(selectedTab?.Kind=="activity"&&string.IsNullOrEmpty(selectedTab.Location))
            {
                var title=Text("Moments",15);title.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;title.VerticalAlignment=VerticalAlignment.Center;
                var home=Row(title);home.Margin=new Thickness(6,0,4,0);if(running)home.Children.Add(Pill("● Live",Tone.Accent));
                surfaceHome.Content=home;
            }
            else
            {
                var back=Button("← Moments",(_,_)=>ShowMoments(null));
                if(running)back.Content=Row(new TextBlock{Text="← Moments"},Pill("●",Tone.Accent));
                AutomationProperties.SetName(back,"Back to moments");surfaceHome.Content=back;
            }
            var shown=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var tab in owner.Tabs.Where(t=>t.Kind!="activity").ToArray())
            {
                shown.Add(tab.Title);
                bool selected=tab.Key==selectedTab?.Key;
                surfaceShelf.Children.Add(ShelfChip(tab.Title,selected,()=>{owner.SelectedTab=tab.Key;RenderTabs();Save();},()=>CloseViewerTab(tab)));
            }
            foreach(var (label,act) in MadeSoFar(owner,(call,path)=>ShowMoments("open:"+call.Id+"|"+path),(kind,target)=>OpenTab(kind,target,true)).Take(6))
                if(shown.Add(label))surfaceShelf.Children.Add(ShelfChip(label,false,act,null));
        }
        liveSurfaceChanged=RenderSurfaceHeader;
        viewer = workBody;
        SurfaceBehind(viewer, new Thickness(5, 4, 5, 6));
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
        branch = Button("Repository", (_, _) => { }); branch.MaxWidth = 200;
        ToolTipService.SetToolTip(branch, "Repository information for the selected project; this does not switch folders.");
        // The branch chip holds repository and delivery; the pull request and background task chips appear only when there is something to show.
        var branchMenu = Fluent.Acrylic(new MenuFlyout()); branch.Flyout = branchMenu;
        branchMenu.Opening += (_, _) =>
        {
            branchMenu.Items.Clear();
            MenuFlyoutItem Item(string text, string glyph, string kind) { var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } }; item.Click += (_, _) => OpenTab(kind, "", true); return item; }
            branchMenu.Items.Add(Item("Repository status", "", "git"));
            branchMenu.Items.Add(Item(session?.Delivery?.Url != null ? "Pull request & CI" : "Commit & open pull request…", "", "delivery"));
            if (session?.Experiment != null) branchMenu.Items.Add(Item("Review experiment patch", "", "experiment"));
        };
        deliveryChip = Button("Pull request", (_, _) => OpenTab("delivery", "", true)); deliveryChip.Visibility = Visibility.Collapsed;
        deliveryChip.Content = Row(new FontIcon { Glyph = "", FontSize = 13 }, new TextBlock { Text = "Pull request" });
        AutomationProperties.SetName(deliveryChip, "Pull request and CI feedback");
        tasksChip = Button("Background", (_, _) => OpenTab("tasks", "", true)); tasksChip.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(tasksChip, "Background tasks");
        models.Visibility = Visibility.Collapsed;
        // One toolbar row at every width: where you are on the left, preview and viewer on the right.
        // Labels shrink to icons in Arrange instead of wrapping onto a second row.
        toolbarLeft = Row(sideToggle, projectSwitcher, branch, deliveryChip, tasksChip);
        viewPicker = new ComboBox { ItemsSource = CodeViews, MinWidth = 115 };
        AutomationProperties.SetName(viewPicker, "Coding workspace view");
        viewPicker.SelectionChanged += (_, _) => { if (!updating && viewPicker.SelectedItem is CodeWorkspaceView view) SelectView(view); };
        foreach (var view in CodeViews)
        {
            var choice = SegmentToggle(new ToggleButton { Content = view.ToString(), Tag = view });
            AutomationProperties.SetName(choice, view + " view");
            choice.Click += (_, _) => { SelectView(view); choice.IsChecked = View() == view; };
            viewModes.Children.Add(choice);
        }
        toolbarRight = Row(new Border { Child = viewModes, Style = Token("MasonSegmentTrack") }, viewPicker, websiteShortcut,viewerToggle);
        toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(toolbarRight, 1);
        toolbar.Children.Add(toolbarLeft); toolbar.Children.Add(toolbarRight);
        Grid.SetColumnSpan(toolbar, 2);
        codeSearchHits = WorkspaceSearch;
        // The title bar search opens projects and sessions, and adds projects, through these.
        openCodeSession = chosen => { if (codeOperation != null) { state.Text = "Finish or stop the current run before switching sessions."; return; } Navigate("Code"); if (chosen != session) Select(chosen); };
        // Review captures: open a sample project so the session, settings and harness pages can be rendered.
        void CaptureProject()
        {
            if (session != null || Environment.GetEnvironmentVariable("MASON_UI_CAPTURE_PROJECT") is not { Length: > 0 } folder || !Directory.Exists(folder)) return;
            var owner = store.AddProject(folder); Select(store.LastSession(owner.Id) ?? store.New(owner.Workspace), owner);
        }
        void CaptureViewer(string? kind)
        {
            Navigate("Code"); CaptureProject(); notesTray.Close(); if (session == null) return;
            // The empty-state capture adds a session with no moments; the others show the fixture session that has them.
            if (session.Activities.Count == 0 && store.Sessions.FirstOrDefault(s => s.Workspace == session.Workspace && s.Activities.Count > 0) is { } worked) Select(worked, project);
            if (kind != null) OpenTab(kind, "", true);
            else if (viewer.Visibility == Visibility.Visible) { CodePanels.Hide(session); forcedPanel = null; Arrange(); }
        }
        // The session as the README and site show it: the conversation with Moments open beside it.
        extraCaptures.Add(("code-session", () => { CaptureViewer("activity"); if (session?.Tabs.FirstOrDefault(t => t.Kind == "activity") is { } moments) { moments.Location = null; moments.Scroll = 0; if (viewerEntries.TryGetValue(moments.Key, out var shown)) shown.Refresh?.Invoke(); } }));
        extraCaptures.Add(("code-changes", () => CaptureViewer("changes")));
        extraCaptures.Add(("code-harness", () => CaptureViewer("harness")));
        // The Live Viewer: the receipt it follows to when a run has ended, then one scene of each kind.
        extraCaptures.Add(("code-live", () => { CaptureViewer("activity"); if (session?.Tabs.FirstOrDefault(t => t.Kind == "activity") is { } live) { live.Location = null; if (viewerEntries.TryGetValue(live.Key, out var shown)) shown.Refresh?.Invoke(); } }));
        foreach (var (name, kind) in new[] { ("code-live-read", CodeSceneKind.Read), ("code-live-edit", CodeSceneKind.Edit), ("code-live-run", CodeSceneKind.Command) })
            extraCaptures.Add((name, () => { CaptureViewer(null); if (session != null) OpenActivity(session.Activities.FindIndex(a => CodeScenes.KindOf(a.Tool) == kind), detail: true); }));
        // A session that has not run anything yet: the Live Viewer's empty state. Captured in a session of its own, kept apart from the fixture's.
        extraCaptures.Add(("code-live-empty", () =>
        {
            CaptureViewer(null); if (session == null) return;
            var empty = store.Sessions.FirstOrDefault(s => s.Workspace == session.Workspace && s.Activities.Count == 0 && s.Turns.Count == 0) ?? store.New(session.Workspace);
            Select(empty, project); OpenTab("activity", "", true);
        }));
        extraCaptures.Add(("code-notes", () => { CaptureViewer(null); notesTray.Open(); }));
        openCodeProject = owner => { if (codeOperation != null) { state.Text = "Finish or stop the current run before switching projects."; return; } Navigate("Code"); if (owner != project) SelectProject(owner); };
        addCodeProject = OpenWorkspace;
        compactCodeContext = CompactContext;
        page.Children.Add(toolbar);
        center.SizeChanged += (_, _) => {title.MaxWidth = folderPath.MaxWidth = Math.Max(100, center.ActualWidth - 190);ArrangeComposer();};
        workspaceShortcut = Shortcut(VirtualKey.B, VirtualKeyModifiers.Control, ToggleWorkspace);
        viewerShortcut = Shortcut(VirtualKey.V, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, ToggleViewer);
        notesShortcut = Shortcut(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => notesTray.Toggle());
        root.KeyboardAccelerators.Add(workspaceShortcut); root.KeyboardAccelerators.Add(viewerShortcut); root.KeyboardAccelerators.Add(notesShortcut);
        void Shortcuts() { workspaceShortcut.IsEnabled = page.Visibility == Visibility.Visible; viewerShortcut.IsEnabled = page.Visibility == Visibility.Visible && session?.Tabs.Count > 0; notesShortcut.IsEnabled = page.Visibility == Visibility.Visible && session != null; }
        deleteSession.Click += async (_, _) => await DeleteSession();
        projectMenu.Opening+=(_,_)=>RefreshProjectMenu();
        sessions.ContextRequested += (_, e) =>
        {
            bool pointer = e.TryGetPosition(sessions, out var point);
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source != sessions && source is not TreeViewItem)
                source = VisualTreeHelper.GetParent(source);
            var node = source is TreeViewItem row ? sessions.NodeFromContainer(row) : pointer ? null : sessions.SelectedNode;
            if (node?.Content is not CodeProjectItem { Session: null } item) return;
            var menu = Fluent.Acrylic(new MenuFlyout());
            menu.Items.Add(RemoveProjectItem(item.Project));
            if (pointer) menu.ShowAt(sessions, point);
            else menu.ShowAt(source as FrameworkElement ?? sessions);
            e.Handled = true;
        };
        sessions.ItemInvoked+=(_,e)=>
        {
            if(updating||codeOperation!=null||(e.InvokedItem as TreeViewNode)?.Content is not CodeProjectItem item)return;
            if(item.Session!=null)Select(item.Session);
            else SelectProject(item.Project);
        };
        sessions.Expanding+=(_,e)=>{if(!updating&&e.Node.Content is CodeProjectItem{Session:null} item)PersistExpansion(item.Project,true);};
        sessions.Collapsed+=(_,e)=>{if(!updating&&e.Node.Content is CodeProjectItem{Session:null} item)PersistExpansion(item.Project,false);};
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
        agentMode.Changed += _ => { if (!updating && session != null) { session.Mode = agentMode.SelectedIndex == 1 ? "ask" : "build"; Enable(); Save(); } };
        draft.TextChanged += (_, _) => { if (session != null && !updating) { session.Draft = draft.Text; draftSave.Stop(); draftSave.Start(); } };
        draftSave.Tick += (_, _) => { draftSave.Stop(); Save(); };
        draft.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) { e.Handled = true; await Send(); }
        };
        tabs.SelectionChanged += (_, _) => { if (!updating && session != null && tabs.SelectedItem is TabViewItem selected && selected.Tag is CodeTab tab) { session.SelectedTab = tab.Key; Save(); } RenderSurfaceHeader(); RefreshOutput(); };
        tabs.TabCloseRequested += (_, e) =>
        {
            if (session == null || e.Tab.Tag is not CodeTab tab) return;
            CloseViewerTab(tab);
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
        Closed += (_, _) => { draftSave.Stop(); pendingApproval?.TrySetResult(false); foreach (var entry in viewerEntries.Values) entry.Dispose(); reviewDesk?.Dispose(); Save(); store.Dispose(); };
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
        void SelectView(CodeWorkspaceView view)
        {
            if (session == null || View() == view) return;
            if (view == CodeWorkspaceView.Review && pendingApproval != null) { state.Text = "Respond to the pending approval first."; Arrange(); return; }
            var previous = session.WorkspaceView;
            session.WorkspaceView = view;
            if (!TrySaveCurrent()) { session.WorkspaceView = previous; Arrange(); return; }
            forcedPanel = null;
            evidenceViews.Clear();
            RenderTranscript(); Arrange();
            if (view == CodeWorkspaceView.Review) RefreshReviewDesk();
        }
        async Task SendReview()
        {
            if (session == null || session.ReviewComments.Count == 0) return;
            SelectView(CodeWorkspaceView.Workbench);
            if (View() == CodeWorkspaceView.Workbench) await Send();
        }
        void RefreshReviewDesk()
        {
            if (session == null || View() != CodeWorkspaceView.Review) return;
            if (reviewDesk == null)
            {
                reviewDesk = BuildReviewDesk(session, Save, () => { RenderTabs(); RenderTranscript(); RefreshReviewDesk(); Save(); },
                    (kind, target) => OpenTab(kind, target, true), RenderReferences, SendReview,
                    () => SelectedChatProfile(profiles)?.DisplayName ?? "Select a model");
                reviewHost.Content = reviewDesk.View;
            }
            else reviewDesk.Refresh();
        }
        // The project menu picks projects. Viewer pages open from the session bar, the Live Viewer or the command search;
        // settings sit under Project settings; forking into a worktree is a session action.
        void RefreshProjectMenu()
        {
            projectMenu.Items.Clear();
            foreach(var owner in store.Projects)
            {
                bool worktree=store.SessionsFor(owner.Id).Any(s=>s.Experiment!=null);
                var item=new MenuFlyoutItem{Text=ProjectLabel(owner,store.Projects)+(worktree?"  · worktree":""),Icon=new FontIcon{Glyph=owner==project?"":""},IsEnabled=codeOperation==null};
                ToolTipService.SetToolTip(item,owner.Workspace);item.Click+=(_,_)=>SelectProject(owner);projectMenu.Items.Add(item);
            }
            if(projectMenu.Items.Count>0)projectMenu.Items.Add(new MenuFlyoutSeparator());
            var add=new MenuFlyoutItem{Text="Add project…",Icon=new FontIcon{Glyph=""},IsEnabled=codeOperation==null};add.Click+=async(_,_)=>await OpenWorkspace();projectMenu.Items.Add(add);
            if(session!=null){var settingsItem=new MenuFlyoutItem{Text="Project settings…",Icon=new FontIcon{Glyph=""}};settingsItem.Click+=(_,_)=>OpenTab("harness","",true);projectMenu.Items.Add(settingsItem);}
            if (project != null) projectMenu.Items.Add(RemoveProjectItem(project));
        }
        async Task ForkIntoWorktree()
        {
            if(session==null||codeOperation!=null||dialogOpen)return;
            dialogOpen=true;
            try{
                var name=new TextBox{PlaceholderText="Task name"};
                var dlg=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Create a Git worktree",Content=Stack(CodeNote("Fork the current checkout, including pending file edits, and continue in a sibling worktree."),name),PrimaryButtonText="Create",CloseButtonText="Cancel"};
                if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
                var slug=name.Text.Trim();
                if(!System.Text.RegularExpressions.Regex.IsMatch(slug,@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,48}\z"))throw new ArgumentException("Use a short task name with letters, digits, hyphens or underscores.");
                var dest=session.Workspace+"-"+slug;
                var origin=await CodeExperiment.Fork(session,dest,"mason/"+slug,CancellationToken.None);
                var fork=store.New(dest);fork.Experiment=origin;fork.Model=session.Model;fork.ReviewerModel=session.ReviewerModel;fork.VisualModel=session.VisualModel;
                fork.Permission=session.Permission;fork.Mode=session.Mode;fork.Milestones=session.Milestones.ToArray();
                fork.Messages.AddRange(session.Messages);
                fork.Turns.AddRange(session.Turns.Where(t=>!t.Rewound).Select(t=>new CodeTurn{Role=t.Role,Text=t.Text,Evidence=t.Evidence}));
                fork.Shell=session.Shell;fork.ShellExecutable=session.ShellExecutable;fork.Reasoning=session.Reasoning;
                fork.ManageRoleMemory=session.ManageRoleMemory;fork.RoleCallLimit=session.RoleCallLimit;fork.RecordModelDecisions=session.RecordModelDecisions;
                fork.Title=slug;store.Save(fork);
                Select(fork,store.ProjectForWorkspace(dest));
            }catch(Exception e){state.Text=e.Message;}finally{dialogOpen=false;}
        }
        MenuFlyoutItem RemoveProjectItem(CodeProject target)
        {
            var item = new MenuFlyoutItem { Text = "Remove project from Mason…", IsEnabled = codeOperation == null };
            item.Click += async (_, _) => await RemoveProject(target);
            return item;
        }
        async Task RemoveProject(CodeProject target)
        {
            if (codeOperation != null || dialogOpen) return;
            dialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme,
                    Title = $"Remove {target.Name} from Mason?",
                    Content = Stack(CodeNote(target.Workspace),
                        Text("Your files stay on disk. Add this folder again to restore its sessions and project notes.", 14)),
                    PrimaryButtonText = "Remove from Mason", CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                bool current = project?.Id == target.Id;
                if (current && !TrySaveCurrent()) return;
                store.RemoveProject(target.Id);
                if (current) Select(store.SelectedSession, store.SelectedProject, selectionSaved: true);
                else RenderProjects();
            }
            catch (Exception error) { state.Text = "Could not remove the project from Mason · " + error.Message; }
            finally { dialogOpen = false; Enable(); }
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
            RefreshSessionChips(owner);
            footerText.Text = (project?.Name??Path.GetFileName(owner.Workspace))+" · "+observed.Branch + " · Session saved locally"; RenderFiles();
        }
        void RefreshSessionChips(CodeSession owner)
        {
            bool wide = page.ActualWidth >= 900;
            deliveryChip.Visibility = owner.Delivery?.Url != null && wide ? Visibility.Visible : Visibility.Collapsed;
            int running = 0;
            try { var tasks = new LocalTasks(Store.Root); running = tasks.All(owner.Workspace).Count(id => tasks.Status(id).Active); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { }
            tasksChip.Content = running + " background"; tasksChip.Visibility = running > 0 && wide ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(tasksChip, running == 1 ? "1 background task running" : running + " background tasks running");
        }
        IEnumerable<TitleHit> WorkspaceSearch()
        {
            TitleHit Command(string name, Action run) => new("\uE756", name, "Command", () => { Navigate("Code"); run(); });
            yield return Command("Toggle projects panel", ToggleWorkspace);
            if (project is {} owner && codeOperation == null)
            {
                yield return Command("New session in " + ProjectLabel(owner, store.Projects), () => NewProjectSession(owner));
                yield return Command("Remove project from Mason · " + ProjectLabel(owner, store.Projects), async () => await RemoveProject(owner));
            }
            if (session == null) yield break;
            IReadOnlyList<CodeDefinition> custom = [];
            try { custom = CodeCustomization.Commands(store.SettingsRoot, session.Workspace); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var command in custom)
                yield return new TitleHit("\uE756", "/" + command.Name + (command.Description.Length > 0 ? " · " + command.Description : ""), "Custom command", () => { Navigate("Code"); draft.Text = "/" + command.Name + " "; draft.Focus(FocusState.Programmatic); draft.SelectionStart = draft.Text.Length; });
            foreach (var view in CodeViews)
                yield return Command(view + " view", () => SelectView(view));
            yield return Command("Notes", () => notesTray.Open());
            yield return Command("Review session changes", () => OpenTab("changes", "", true));
            yield return Command("Moments", () => OpenTab("activity", "", true));
            yield return Command("View context checkpoints", () => OpenTab("context", "", true));
            yield return Command("Plan & repository map", () => OpenTab("plan", "", true));
            yield return Command("Repository status", () => OpenTab("git", "", true));
            yield return Command("Commit & open pull request", () => OpenTab("delivery", "", true));
            yield return Command("Model roles & decisions", () => OpenTab("roles", "", true));
            yield return Command("Background tasks", () => OpenTab("tasks", "", true));
            yield return Command("Project settings", () => OpenTab("harness", "", true));
            yield return Command("Fork session into a worktree", () => _ = ForkIntoWorktree());
            yield return Command("Open website", async () => await OpenWebsite());
            if (session.Tabs.Count > 0) yield return Command("Toggle task viewer", ToggleViewer);
            if (codeOperation == null)
            {
                yield return Command("Delete selected session", async () => await DeleteSession());
                if (session.Messages.Count > 0) yield return Command("Compact context now", async () => await CompactContext());
            }
            foreach (var path in allFiles)
                yield return new TitleHit("\uE8A5", path, "File", () => { Navigate("Code"); OpenTab("file", path, true); });
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
                var choices = (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).Where(p => p.Model.Length > 0 && p.ForChat).ToArray();
                updating = true; PresentChatModels(profiles, choices);
                profiles.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding DisplayName}' FontSize='13' MaxWidth='210' TextTrimming='CharacterEllipsis'/></DataTemplate>");
                profiles.SelectedItem = profiles.Items.OfType<ChatModelChoice>().FirstOrDefault(c => c.Unwrap().Id == current);
                if (profiles.SelectedIndex < 0 && choices.Length == 1) { profiles.SelectedIndex = 0; if (session != null) session.Model = choices[0].Id; }
            }
            catch (Exception error) { state.Text = "Models unavailable · " + error.Message; }
            finally { updating = false; }
        }
        async void Select(CodeSession? selected,CodeProject? selectedProject=null,bool selectionSaved=false)
        {
            if (codeOperation != null) return;
            draftSave.Stop();if(!selectionSaved && !TrySaveCurrent())return;
            var owner=selected==null?selectedProject??store.SelectedProject:store.ProjectFor(selected);
            try{if(!selectionSaved && owner!=null)store.ActivateProject(owner.Id,selected?.Id);}catch(Exception error){state.Text="Project switch failed · "+error.Message;return;}
            foreach (var entry in viewerEntries.Values) entry.Dispose(); viewerEntries.Clear();
            reviewDesk?.Dispose(); reviewDesk = null; reviewHost.Content = null;
            session = selected;project=owner; updating = true; pendingTranscriptTail = true; followTail = true;
            notesTray.Close(); notesTray.RefreshCount(); notesTray.Tab.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
            try
            {
                RenderProjects();
                title.Text = selected?.Title ?? project?.Name??"A project for your code"; draft.Text = selected?.Draft ?? "";
                folderLabel.Text=project?.Name??"Choose project";folderPath.Text=project?.Workspace??"";ToolTipService.SetToolTip(projectSwitcher,project?.Workspace??"Add or switch a coding project");
                fileProject.Text=project?.Name??"No project selected";ToolTipService.SetToolTip(fileProject,project?.Workspace??"");fileState.Text="";
                permissions.SelectedIndex = (int)(selected?.Permission ?? CodePermission.ReadOnly);
                shells.SelectedIndex = (int)(selected?.Shell ?? CodeShellKind.Recommended);networkModes.SelectedIndex=(int)(selected?.Network??CodeNetworkMode.Default); ShowShell();SidebarTab(selected?.SidebarFiles==true);
                agentMode.SelectedIndex = selected?.Mode == "ask" ? 1 : 0;
                reasoning.SelectedItem = (selected?.Reasoning ?? "default") switch { "off" => "Off", "low" => "Low", "medium" => "Medium", "high" => "High", _ => "Default" };
                forcedPanel = null;
                pinned.IsChecked = selected?.ViewerPinned == true; state.Text = selected == null ? "" : selected.Detail is { Length: > 0 } detail ? detail : "Ready · choose a model and describe the work";
                footerText.Text=project==null?"Sessions stay on this computer":project.Name+" · Sessions stay on this computer";
                context.Text = ContextCaption(selected); ContextLabel(); allFiles = []; gitStatus = null; RenderFiles();
            }
            finally { updating = false; }
            RefreshModels(); RenderReferences(); RenderTranscript(); RenderTabs(); Arrange(); Enable(); RefreshReviewDesk();
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
        void AttachEvidence(CodeSession owner, DeveloperEvidence item)
        {
            if (owner != session || codeOperation != null) throw new InvalidOperationException("Return to the idle session before attaching evidence.");
            if (owner.Evidence.Count >= 4) throw new InvalidOperationException("Send or remove an attachment first.");
            owner.Evidence.Add(item);
            try { store.Save(owner); } catch { owner.Evidence.Remove(item); throw; }
            RenderReferences();
        }
        void RenderReferences()
        {
            SetReviewMenu(reviewButton, session, RenderReferences);
            reviewDesk?.Refresh();
            references.Children.Clear();
            if (session == null) return;
            foreach (var item in session.Evidence.ToArray())
            {
                var chip = Button(item.Label + " ×", (_, _) => {
                    if (codeOperation != null) return;
                    session.Evidence.Remove(item); RenderReferences(); Save();
                });
                ToolTipService.SetToolTip(chip, item.Kind + "\n" + item.Details);
                references.Children.Add(chip);
            }
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
            ShowMood(); UpdateStatusRow();
            double offset = transcriptScroll.VerticalOffset; transcript.Children.Clear(); emptyHost = null; resultActions = null;
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
            if (!animateNew) { turnViews.Clear(); evidenceViews.Clear(); liveView = null; }
            var keptViews = new Dictionary<int, (string Key, UIElement View)>();
            string previousRole = ""; bool activityShown = false; int segmentStart = 0, segmentEnd = session.Activities.Count;
            CodeTurn? requestTurn = null;
            void AddTurnEvidence()
            {
                if (requestTurn?.Acceptance is not { Checks.Count: > 0 } plan) return;
                if (!evidenceViews.TryGetValue(requestTurn, out var cached) || cached.Revision != plan.VerifiedRevision ||
                    !plan.Checks.SequenceEqual(cached.Checks) || !plan.Results.SequenceEqual(cached.Results))
                    evidenceViews[requestTurn] = cached = (plan.Checks.ToArray(), plan.Results.ToArray(), plan.VerifiedRevision,
                        CheckEvidence(plan, cached.View?.Disclosure?.IsExpanded == true, View()));
                transcript.Children.Add(cached.View.Layout);
            }
            for (int index = 0; index < session.Turns.Count; index++)
            {
                var turn = session.Turns[index];
                if (turn.Role == "user") { AddTurnEvidence(); requestTurn = turn; activityShown = false; segmentStart = turn.FirstActivity; segmentEnd = session.Turns.Skip(index + 1).FirstOrDefault(t => t.Role == "user")?.FirstActivity ?? session.Activities.Count; }
                if (turn.Text.Length == 0 || (requestTurn?.Acceptance != null && turn.Role == "assistant" &&
                    System.Text.RegularExpressions.Regex.IsMatch(turn.Text, @"^Completed · \d+ feature checks? passed · \d+ files? changed$"))) continue;
                // Keep the opening intent; repeated file-read narration remains in activity/history.
                if (turn.Role == "assistant" && activityShown && turn.ActivityCount == 1 && turn.FirstActivity < session.Activities.Count && session.Activities[turn.FirstActivity].Tool is "read_file" or "list_files" or "search_text") continue;
                string viewKey = turn.Role + "|" + (turn.Role != previousRole) + "|" + turn.Text;
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
                            speaker = Row(AppIconImage(28), name); speaker.Spacing = 12;
                        }
                        var content = Stack(speaker, body); content.Spacing = 10; container = content;
                    }
                    if (turn.Evidence.Length > 0)
                    {
                        var captured = Stack(container);
                        foreach (var item in turn.Evidence)
                        {
                            var panel = Stack(CodeNote(item.Details));
                            if(item.HasScreenshot) panel.Children.Add(new Image{Source=new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(DeveloperEvidence.ImageFile(Store.Root,session.Id,item.Id))),MaxHeight=200});
                            captured.Children.Add(new Expander{Header=item.Kind+" · "+item.Label,Content=panel});
                        }
                        container=captured;
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
                    int start = segmentStart;
                    transcript.Children.Add(Toolbar(CodeNote(CodeProtocol.ActivitySummary(session.Activities.Skip(segmentStart).Take(segmentEnd - segmentStart))), CodeLink("Moments", (_, _) => OpenActivity(start)))); activityShown = true;
                }
            }
            AddTurnEvidence();
            if (session.State is not ("Running" or "Awaiting review"))
            {
                if (session.Ending is { } ending && CodeRunEnding.Explains(ending.Code)) transcript.Children.Add(EndingCard(session, ending));
                resultActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
                RefreshResultActions();
                transcript.Children.Add(resultActions);
                if (session.SuggestedNote is { Length: > 0 } suggested)
                {
                    var owner = session;
                    void Decided() { owner.SuggestedNote = null; TrySaveCurrent(); RenderTranscript(); }
                    transcript.Children.Add(NoteSuggestionCard(suggested, notesTray.Full,
                        remember: () => { try { state.Text = notesTray.Add(suggested); Decided(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException) { state.Text = e.Message; } },
                        edit: () => { Decided(); notesTray.Open(suggested); },
                        dismiss: () => { try { CodeNotes.Dismiss(store.SettingsRoot, owner.Workspace, suggested); } catch (IOException) { } Decided(); }));
                }
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
                        var identity = Row(AppIconImage(28), liveName); identity.Spacing = 12; container = Stack(identity, text);
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
        void RefreshResultActions()
        {
            if (resultActions == null || session == null) return;
            var actions = resultActions.Children;
            actions.Clear();
            int changed = session.Changes.Count(c => !c.Restored);
            if (changed > 0)
                actions.Add(Button($"Review changes ({changed})", (_, _) => SelectView(CodeWorkspaceView.Review), true));
            if (session.Tabs.Any(t => t.Kind is "web" or "site" || t.Kind == "file" && CodeWebsite.IsPage(t.Target)) ||
                session.Changes.Any(c => !c.Restored && CodeWebsite.IsPage(c.Path)))
                actions.Add(CodeLink("Open preview", async (_, _) => await OpenWebsite()));
            foreach (var file in session.Tabs.Where(t => t.Kind == "file").TakeLast(2))
                actions.Add(CodeLink(file.Title, (_, _) => OpenTab("file", file.Target, true)));
            resultActions.Visibility = actions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void OpenTextArtifact(ChatArtifact artifact)
        {
            if (session == null) return;
            // Generated fenced text is inspected as a local native document; it is never executed.
            var entry = new CodeTab { Kind = "answer", Target = artifact.Content };
            OpenTab(entry.Kind, entry.Target, true);
        }
        // Opens the Live Viewer on the scene holding this call, so a summary line in the chat shows its own steps.
        // Opens Moments. With detail, opens the moment holding this call, as Open on its card does (used by the review captures).
        void OpenActivity(int index, bool detail = false)
        {
            OpenTab("activity", "", true);
            if (session?.Tabs.FirstOrDefault(t => t.Kind == "activity") is not { } tab) return;
            tab.Location = detail && index >= 0 && index < session.Activities.Count ? "open:" + session.Activities[index].Id : null;
            tab.Scroll = 0;
            if (viewerEntries.TryGetValue(tab.Key, out var live)) live.Refresh?.Invoke();
            Save();
        }
        void OpenTab(string kind, string target, bool user)
        {
            if (session == null) return;
            if (user && kind == "changes") { SelectView(CodeWorkspaceView.Review); return; }
            if (user && View() != CodeWorkspaceView.Workbench) SelectView(CodeWorkspaceView.Workbench);
            // Clicking a transcript link is pointer input, not a request to stop following
            // the latest result. Preserve the bottom anchor while the viewer changes width.
            if (user && transcriptScroll.ScrollableHeight - transcriptScroll.VerticalOffset < 90) followTail = true;
            var tab = new CodeTab { Kind = kind, Target = target };
            int previousCount = session.Tabs.Count;
            if (!CodePanels.Open(session, tab, user)) { state.Text = "Close a viewer tab before opening more than 12."; return; }
            if (user) forcedPanel = "viewer"; RenderTabs();
            if (session.Tabs.Count != previousCount) RefreshResultActions();
            Arrange(); Save();
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
                        entry = BuildCodeViewer(session!, tab, Save, () => { RenderTabs(); viewerChanged?.Invoke(); Save(); }, (kind, target) => OpenTab(kind, target, true), RenderReferences, attach: AttachEvidence); viewerEntries[tab.Key] = entry;
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
            RenderSurfaceHeader(); RefreshOutput();
        }
        void Arrange()
        {
            double width = page.ActualWidth;
            bool anyProject = store.Projects.Count > 0;
            // Before the first project there is nothing to switch, search or view: onboarding gets the page.
            toolbar.Visibility = anyProject ? Visibility.Visible : Visibility.Collapsed;
            viewerLabel.Visibility = width < 620 ? Visibility.Collapsed : Visibility.Visible;
            branch.MaxWidth = 180; branch.Visibility = width < 700 ? Visibility.Collapsed : Visibility.Visible;
            profiles.MinWidth=130;permissions.MinWidth=140;
            ArrangeComposer();
            bool hasArtifacts = session?.Tabs.Count > 0;
            bool review = View() == CodeWorkspaceView.Review;
            bool sidebar = (!review || forcedPanel == "workspace") && anyProject && (session?.SidebarVisible ?? emptySidebarVisible) && (width >= 760 || forcedPanel == "workspace");
            bool showViewer = !review && hasArtifacts && session!.ViewerVisible && (width >= 1000 || forcedPanel == "viewer") && !(width <= 1120 && forcedPanel == "workspace");
            if (showViewer && width - (sidebar ? projectPanelWidth+12 : 0) < 1000) sidebar = false;
            bool projectsOnly=sidebar&&width<640;if(projectsOnly)showViewer=false;
            bool viewerOnly = showViewer && width < 1000;
            websiteShortcut.Visibility=session!=null && !showViewer?Visibility.Visible:Visibility.Collapsed;
            sideBody.Visibility = projectSurface.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
            page.ColumnDefinitions[0].Width = projectsOnly?new GridLength(1,GridUnitType.Star):new GridLength(sidebar?projectPanelWidth:0); page.ColumnSpacing = sidebar&&!projectsOnly ? 12 : 0;
            page.ColumnDefinitions[1].Width=projectsOnly?new GridLength(0):new GridLength(1,GridUnitType.Star);
            layout.Visibility=projectsOnly || review ? Visibility.Collapsed : Visibility.Visible;
            reviewHost.Visibility = review && !projectsOnly ? Visibility.Visible : Visibility.Collapsed;
            viewModes.Visibility = session != null && width >= 845 ? Visibility.Visible : Visibility.Collapsed;
            viewPicker.Visibility = session != null && width < 845 ? Visibility.Visible : Visibility.Collapsed;
            if (viewPicker.SelectedItem is not CodeWorkspaceView picked || picked != View()) viewPicker.SelectedItem = View();
            foreach (ToggleButton choice in viewModes.Children) choice.IsChecked = (CodeWorkspaceView)choice.Tag == View();
            title.FontSize = 24;
            center.MaxWidth = double.PositiveInfinity;
            center.Padding = new Thickness(0);
            transcriptBody.MaxWidth = 960;
            transcript.MaxWidth = 820;
            transcript.HorizontalAlignment = HorizontalAlignment.Center;
            viewerToggle.Visibility = review ? Visibility.Collapsed : Visibility.Visible;
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
            if (View() != CodeWorkspaceView.Workbench) { SelectView(CodeWorkspaceView.Workbench); CodePanels.Show(session); }
            else if (viewer.Visibility == Visibility.Visible) CodePanels.Hide(session); else CodePanels.Show(session);
            forcedPanel = session.ViewerVisible ? "viewer" : null; Arrange(); Save();
        }
        void Enable()
        {
            bool ready = codeOperation == null;
            bool workspaceReady = session != null;
            newSession.IsEnabled = ready && project != null;
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
            ToolTipService.SetToolTip(activityButton, workspaceReady ? "What Mason did, as moments you can open" : "Open a workspace folder to start a session.");
            // Until the session has done something there is no activity to view.
            activityButton.Visibility = session?.Activities.Count > 0 || session?.State is "Running" or "Awaiting review" ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(contextLink, workspaceReady ? ContextCaption(session) : "Open a workspace folder to start a session.");
            bool canGuide=liveEngine!=null && session==liveOwner;
            draft.IsEnabled = session != null && (ready||canGuide); send.IsEnabled = session != null && ready;
            followUpButtons.Visibility=canGuide?Visibility.Visible:Visibility.Collapsed;
            draft.PlaceholderText=canGuide?"Steer this run or queue a follow-up…":session?.PendingInput.Count>0?"Type /queue to resume pending messages…":"Describe a change, ask about the repo, or @ a file…";
            send.Visibility = ready ? Visibility.Visible : Visibility.Collapsed; stop.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            spinner.Visibility = ready ? Visibility.Collapsed : Visibility.Visible; spinner.IsActive = !ready;
        }
        void ContextLabel()
        {
            contextLink.Content = session?.PromptTokens is int used && session.ContextTokens is int limit ? $"{used:N0} / {limit:N0}" : "Context";
            ToolTipService.SetToolTip(contextLink, ContextCaption(session) + " · inspect retained checkpoints or compact manually");
        }

        void ReflectRunState(CodeSession current)
        {
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var endingLook = current.State is "Running" or "Awaiting review" ? null : CodeRunEnding.Look(current.Ending?.Code);
            var kind = current.State switch
            {
                "Running" => TaskbarStatus.Kind.Working, "Awaiting review" or "Waiting for input" => TaskbarStatus.Kind.NeedsYou,
                _ when endingLook != null => endingLook.Tone switch { CodeEndingTone.Danger => TaskbarStatus.Kind.Failed, CodeEndingTone.Warning or CodeEndingTone.Accent => TaskbarStatus.Kind.NeedsYou, _ => TaskbarStatus.Kind.None },
                "Failed" or "Blocked" => TaskbarStatus.Kind.Failed, _ => TaskbarStatus.Kind.None
            };
            TaskbarStatus.Show(window, kind);
            if (codeReflectedState == "Running" && endingLook != null && current.Ending!.Code != CodeRunEnding.Stopped)
                Notifications.Show(current.Ending.Code == CodeRunEnding.Done ? "Mason finished" : "Mason · " + endingLook.Headline,
                    current.Title + " · " + (current.Ending.KeptText is { } kept ? kept + " · " : "") + current.Detail, window,
                    endingLook.Tone == CodeEndingTone.Danger || current.Ending.Code == CodeRunEnding.Unverified ? PoseFailed : current.Changes.Any(c => !c.Restored) ? PoseReview : PoseConnected);
            else if (codeReflectedState == "Running" && endingLook == null && current.State is "Completed" or "Failed" or "Blocked" or "Needs verification")
                Notifications.Show(current.State == "Completed" ? "Mason finished" : "Mason stopped: " + current.State, current.Title + " · " + current.Detail, window,
                    current.State != "Completed" ? PoseFailed : current.Changes.Any(c => !c.Restored) ? PoseReview : PoseConnected);
            codeReflectedState = current.State;
        }
        void RefreshRun()
        {
            if (refreshQueued) return; refreshQueued = true;
            DispatcherQueue.TryEnqueue(() => { refreshQueued = false; if (session == null) return;
                ReflectRunState(session);
                // A first run renames "New session"; the project tree binds a snapshot, so rebuild it when the title changes.
                if (title.Text != session.Title) { title.Text = session.Title; RenderProjects(); }
                state.Text = session.Detail; context.Text = ContextCaption(session); ContextLabel();
                if (liveOwner == session && submittedDraft != null && session.Draft.Length == 0)
                {
                    if (draft.Text == submittedDraft) { updating = true; draft.Text = ""; updating = false; }
                    submittedDraft = null; RenderReferences();
                }
                RenderTranscript(); RenderTabs(); RenderFiles(); Arrange(); RefreshReviewDesk(); _ = RefreshWorkspaceFiles(session); });
        }
        async Task<bool> Approve(CodeApproval request, CancellationToken cancellation)
        {
            if (View() == CodeWorkspaceView.Review) SelectView(CodeWorkspaceView.Workbench);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); pendingApproval = completion;
            using var registration = cancellation.Register(() => completion.TrySetCanceled(cancellation));
            var owner = session;
            var detail = CodeScroll(Stack(new TextBlock { Text = request.Description, Style = Token("MasonBodyStrong") }, CodeNote(request.Detail))); detail.MaxHeight = 190;
            var toolName = request.Tool.Replace('_', ' ');
            var allow = IconButton("\uE73E", "Allow once", (_, _) => completion.TrySetResult(true), true);
            var decline = IconButton("\uE711", "Decline", (_, _) => completion.TrySetResult(false));
            // G4: "Always allow" saves a project rule; the suggested subject is the program and first argument, the folder or the server.
            var rule = new TextBox { Header = "Allow when it starts with", Text = CodeApprovalRules.Suggest(request.Tool, request.RuleSubject), FontFamily = new FontFamily("Cascadia Mono, Consolas"), MinWidth = 320 };
            var ruleNote = new TextBlock { Text = "Saved for this project in your Mason profile. Revoke it in Harness settings. Rules never widen the sandbox.", Style = Token("MasonCaptionSecondary"), MaxWidth = 340 };
            var always = new Button { Content = Row(Glyph("\uE8FB"), new TextBlock { Text = "Always allow…" }) };
            var saveRule = Button("Save rule and allow", (_, _) =>
            {
                try
                {
                    if (owner != null) CodeApprovalRules.Add(store.SettingsRoot, owner.Workspace, new(request.Tool, rule.Text.Trim(), true, DateTimeOffset.UtcNow));
                    always.Flyout?.Hide(); completion.TrySetResult(true);
                }
                catch (Exception e) { ruleNote.Text = e.Message; }
            }, true);
            always.Flyout = new Flyout { Content = Stack(new TextBlock { Text = "Always allow " + toolName, Style = Token("MasonBodyStrong") }, rule, ruleNote, saveRule) };
            // A host is kept exactly, not as a prefix: one list per project for commands and the web tools.
            if (request.Tool == "network")
            {
                always.Flyout = null;
                always.Content = new TextBlock { Text = "Always for this project" };
                always.Click += (_, _) =>
                {
                    try { if (owner != null) CodeNetworkSettings.Allow(store.SettingsRoot, owner.Workspace, request.RuleSubject); completion.TrySetResult(true); }
                    catch (Exception e) { ToolTipService.SetToolTip(always, e.Message); }
                };
                allow.Content = Row(Glyph(""), new TextBlock { Text = "Once" });
                decline.Content = Row(Glyph(""), new TextBlock { Text = "No" });
            }
            AutomationProperties.SetName(always, "Always allow " + toolName + " in this project");
            AutomationProperties.SetName(allow, "Allow " + toolName + " once"); AutomationProperties.SetName(decline, "Decline " + toolName);
            var icon = MascotImage(PoseApproval, 70, "waiting for your decision");
            var heading = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new TextBlock { Text = "Review " + toolName, Style = Token("MasonBodyLargeStrong") });
            heading.Children.Add(new TextBlock { Text = "Mason is waiting for your decision", Style = Token("MasonCaptionSecondary") });
            var approvalHead = Row(icon, heading); approvalHead.Spacing = 14; var approvalBody = Stack(approvalHead, detail);
            if (request.Jpeg != null)
            {
                using var bytes = new MemoryStream(Convert.FromBase64String(request.Jpeg));
                using var stream = bytes.AsRandomAccessStream();
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmap.SetSourceAsync(stream);
                approvalBody.Children.Add(new Image { Source = bitmap, MaxHeight = 200, Stretch = Stretch.Uniform });
            }
            approvalBody.Children.Add(Row(allow, always, decline));
            var approvalCard = ChatSurface(approvalBody, new Thickness(16, 14, 16, 14));
            approvalHost.Content = approvalCard; FadeIn(approvalCard);
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            TaskbarStatus.Show(window, TaskbarStatus.Kind.NeedsYou);
            Notifications.Show("Mason needs your review", (owner?.Title ?? "Coding session") + " · " + request.Description, window, PoseApproval);
            try { return await completion.Task; }
            finally { approvalHost.Content = null; pendingApproval = null; TaskbarStatus.Show(window, codeOperation != null ? TaskbarStatus.Kind.Working : TaskbarStatus.Kind.None); }
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
        void SubmitFollowUp(bool steer)
        {
            if(liveEngine==null || session!=liveOwner || session==null)return;
            try{
                liveEngine.SendWhileRunning(session,draft.Text,steer,includeReview: true);draft.Text="";RenderReferences();state.Text=steer?"Steering the current run":"Follow-up queued";
            }catch(Exception error){state.Text=error.Message;}
        }
        async Task Send()
        {
            if(liveEngine!=null && session==liveOwner){SubmitFollowUp(false);return;}
            if (codeOperation != null || aiOperation != null) { state.Text = "Finish or stop the active AI operation first."; return; }
            if (session == null) { state.Text = "Open a workspace folder first."; return; }
            if (SelectedChatProfile(profiles) is not AiEndpointProfile profile) { state.Text = "Select a coding model from Models & endpoints."; return; }
            string input = draft.Text.Trim(); if (input.Length == 0 && session.ReviewComments.Count == 0) return;
            if (input is "/note" or "/notes") { updating = true; draft.Text = ""; session.Draft = ""; updating = false; notesTray.Open(); return; }
            if (input.StartsWith("/note ", StringComparison.OrdinalIgnoreCase) || input.StartsWith("/notes add ", StringComparison.OrdinalIgnoreCase))
            {
                var words = input[(input.StartsWith("/notes add ", StringComparison.OrdinalIgnoreCase) ? 11 : 6)..];
                try { state.Text = notesTray.Add(words); updating = true; draft.Text = ""; session.Draft = ""; updating = false; }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { state.Text = e.Message; }
                return;
            }
            notesTray.Close();
            followTail = true; pendingTranscriptTail = true;
            var active = session; using var cancellation = new CancellationTokenSource(); codeOperation = cancellation; aiOperation = cancellation; draftSave.Stop(); Enable();
            try
            {
                var model = SessionModels(active, profile, value => context.Text = value, value => { active.Detail = value; if (active == session) state.Text = value; });
                var engine = new CodeEngine(store, model, Approve, RefreshRun) { ModelName = profile.DisplayName };
                liveEngine=engine;liveOwner=active;Enable();
                submittedDraft = draft.Text;
                var task = engine.Run(active, input, cancellation.Token, includeReview: true);
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
            finally { submittedDraft=null;liveEngine=null;liveOwner=null;RenderReferences();codeOperation = null; if (aiOperation == cancellation) aiOperation = null; Enable(); updating = true; draft.Text = active.Draft; RenderProjects(); updating = false; ShowMood(); footerText.Text = (project?.Name??Path.GetFileName(active.Workspace)) + " · Session saved locally"; Save(); }
        }
    }
}
