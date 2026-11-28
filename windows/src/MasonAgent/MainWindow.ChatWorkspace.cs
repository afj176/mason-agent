using System.Data;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace Mason;

public sealed partial class MainWindow
{
    [Microsoft.UI.Xaml.Data.Bindable]
    sealed class ChatModelChoice(AiEndpointProfile profile, string availability, string mark = "")
    {
        public Guid Id => profile.Id;
        public string DisplayName => (mark == "favourite" ? "★ " : "") + profile.DisplayName;
        public string ConnectionDetail => (mark == "recent" ? "Recent · " : "") + availability + " · " + profile.Endpoint;
        public AiEndpointProfile Unwrap() => profile;
        // Combo items are announced by ToString; without this a screen reader hears the type name.
        public override string ToString() => DisplayName + ", " + ConnectionDetail;
    }
    static AiEndpointProfile? SelectedChatProfile(ComboBox box) => (box.SelectedItem as ChatModelChoice)?.Unwrap();
    void PresentChatModels(ComboBox box, IEnumerable<AiEndpointProfile> profiles)
    {
        ChatModelChoice Choice(AiEndpointProfile profile)
        {
            var owned=modelServing.Running.FirstOrDefault(s=>s.Profile.Id==profile.Id);
            var availability=owned!=null?(owned.Owner.IsRunning?"Running · managed":"Stopped · managed; start in Models"):profile.ManagedCapability!=null||profile.Model.StartsWith("mason-",StringComparison.Ordinal)||profile.Model.StartsWith("cowboy-",StringComparison.Ordinal)?"Saved managed · checked on use":"External · checked on use";
            return new(profile,availability);
        }
        box.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Spacing='3' Padding='2,4'><TextBlock Text='{Binding DisplayName}' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding ConnectionDetail}' FontSize='11' Opacity='.65' TextTrimming='CharacterEllipsis'/></StackPanel></DataTemplate>");
        // X3: favourites first, then the most recently used, then the rest (OpenCode's model picker order).
        var picks=ModelPicks.Read();
        var ordered=profiles.OrderBy(p=>picks.Favourites.Contains(p.Id)?0:picks.Recent.Contains(p.Id)?1:2).ThenBy(p=>picks.Recent.IndexOf(p.Id) is var i&&i>=0?i:int.MaxValue).ToArray();
        var previous=(box.SelectedItem as ChatModelChoice)?.Id;
        box.ItemsSource=ordered.Select(p=>{var c=Choice(p);return new ChatModelChoice(p,c.ConnectionDetail[..c.ConnectionDetail.LastIndexOf(" · ",StringComparison.Ordinal)],picks.Favourites.Contains(p.Id)?"favourite":picks.Recent.Contains(p.Id)?"recent":"");}).ToArray();
        if(previous is {} keep)box.SelectedItem=box.Items.OfType<ChatModelChoice>().FirstOrDefault(c=>c.Id==keep);
        if(box.Tag as string=="picks")return;
        box.Tag="picks";
        box.SelectionChanged+=(_,_)=>{if(box.SelectedItem is ChatModelChoice chosen)ModelPicks.Use(chosen.Id);};
        var favourite=new MenuFlyoutItem{Text="Add to favourites",Icon=new FontIcon{Glyph=""}};
        favourite.Click+=(_,_)=>{if(box.SelectedItem is ChatModelChoice chosen){ModelPicks.ToggleFavourite(chosen.Id);PresentChatModels(box,box.Items.OfType<ChatModelChoice>().Select(c=>c.Unwrap()).ToArray());}};
        var menu=new MenuFlyout{Items={favourite}};
        menu.Opening+=(_,_)=>favourite.Text=box.SelectedItem is ChatModelChoice c&&ModelPicks.Read().Favourites.Contains(c.Id)?"Remove from favourites":"Add selected model to favourites";
        box.ContextFlyout=menu;
        ToolTipService.SetToolTip(box,"Favourites (★) and recent models come first. Right-click to favourite the selected model.");
    }
    Action<Guid>? openNativeChat;
    Action? refreshNativeChatProfiles;
    ChatLibrary? nativeChatLibrary;
    [Microsoft.UI.Xaml.Data.Bindable]
    sealed record ChatHistoryItem(ChatConversation Conversation, string Section)
    {
        public string Title => Conversation.Title;
        public string Preview => Conversation.Turns.FirstOrDefault(t => t.Role == "user")?.Text ?? "Start a conversation";
        public Visibility SectionVisibility => Section.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public override string ToString() => Title;
    }
    FrameworkElement BuildChatWorkspace()
    {
        try { nativeChatLibrary = new ChatLibrary(Store.Root); }
        catch (Exception error) { return TroubleNotice("Chat history is unavailable", error.Message, "Close any other Mason Agent window using this profile, or restore chat-library.json from a backup. The original history has not been replaced."); }
        var library = nativeChatLibrary; var saved = library.State;
        var conversation = saved.Conversations.FirstOrDefault(c => c.Id == saved.Selected) ?? saved.Conversations.OrderByDescending(c => c.Updated).FirstOrDefault() ?? library.New();
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(250) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(360) });
        var historySearch = new TextBox { PlaceholderText = "Search conversations…" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(historySearch, "Search conversations");
        var history = new ListView { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void UpdateHistorySelection()
        {
            foreach (var key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
            {
                if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) history.Resources.Remove(key);
                else history.Resources[key] = new SolidColorBrush(history.ActualTheme == ElementTheme.Dark ? ThemePalette.AccentAlpha(true, 46) : ThemePalette.AccentAlpha(false, 30));
            }
        }
        history.Loaded += (_, _) => UpdateHistorySelection(); history.ActualThemeChanged += (_, _) => UpdateHistorySelection();
        history.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Spacing='4' Padding='8,7'><TextBlock Text='{Binding Section}' Visibility='{Binding SectionVisibility}' FontSize='12' Opacity='.65'/><TextBlock Text='{Binding Title}' FontSize='14' FontWeight='SemiBold' MaxLines='1' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Preview}' FontSize='12' Opacity='.7' MaxLines='1' TextTrimming='CharacterEllipsis'/></StackPanel></DataTemplate>");
        var title = Text(conversation.Title, 25); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.MaxLines = 1; title.TextTrimming = TextTrimming.CharacterEllipsis;
        var profiles = new ComboBox { PlaceholderText = "Choose model", MinWidth = 170, MaxWidth = 290, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(profiles, "Chat model");
        var modelState = Text("", 12); modelState.Opacity = .75;
        var message = new TextBox { PlaceholderText = "Ask a question or attach a document…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 160, MaxLength = LocalAiClient.ChatTextLimit, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(message, "Chat message"); ComposerBox(message);
        var attachmentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var attachmentHint = Text("", 12); attachmentHint.Opacity = .7;
        var state = Text("Messages and conversation history stay on this computer.", 12); state.IsTextSelectionEnabled = true;
        var transcript = new StackPanel { Spacing = 26, MaxWidth = 940, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 8, 8, 18) };
        var transcriptScroll = new ScrollViewer { Content = transcript, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid? welcomeHost = null; // centred vertically while the conversation is empty
        var welcomeArt = MasonryBackdrop(transcriptScroll); // behind the welcome only
        transcriptScroll.SizeChanged += (_, _) => { transcript.Width = Math.Max(200, Math.Min(940, transcriptScroll.ActualWidth - 16)); if (welcomeHost != null) FitWelcome(welcomeHost, transcriptScroll.ActualHeight - 40); };
        var previewHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var artifacts = new ComboBox { PlaceholderText = "Choose a message or attachment", HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(artifacts, "Chat reference artifact");
        var previewMode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(previewMode, "Reference view");
        var previewNote = Text("", 12); previewNote.Opacity = .7;
        IDisposable? previewResource = null; int previewGeneration = 0; bool loading = false, refreshingHistory = false, busy = false, followTail = true, configuringPreview = false;
        Guid? editing = null; string? forcedPanel = null;
        Button libraryToggle = null!, previewToggle = null!, newChat = null!, attach = null!, send = null!, stop = null!;
        Grid libraryPanel = null!, previewPanel = null!;
        var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        void Save()
        {
            try { library.Save(); }
            catch (Exception error) { state.Text = "History was not saved: " + error.Message + " · Export this conversation before closing."; }
        }
        void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        var system = new TextBox { Header = "System instructions", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000, MinHeight = 100 };
        ChatServerInfo? serverInfo = null; int serverGeneration = 0; bool serverUnreachable = false;
        var contextStatus = Text("Choose a model to inspect its loaded context and controls.", 12); contextStatus.IsTextSelectionEnabled = true;
        var budgetStatus = Text("Token usage is checked before sending. Reasoning and the answer share the output budget.", 11); budgetStatus.Opacity = .7;
        var contextUsage = Text("Context usage · choose a model", 12); contextUsage.IsTextSelectionEnabled = true; contextUsage.Opacity = .8;
        var contextMeter = BrandControl(new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(contextMeter, "Prompt context usage percent");
        var compactionNote = Text("Automatic compaction at 80% · originals stay in the library", 11); compactionNote.Opacity = .7;
        var reviewSummary = Button("View summary", (_, _) =>
        {
            if (ChatCompactionHistory.Active(conversation) is {} summary) ShowArtifact(new("Conversation summary", "markdown", summary.Markdown));
        });
        reviewSummary.Visibility = Visibility.Collapsed;
        var autoCompact = new ToggleSwitch { Header = "Automatic conversation compaction", OnContent = "Summarize older messages when context fills", OffContent = "Use full history without summaries" };
        var usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        CancellationTokenSource? usageCancellation = null; int usageGeneration = 0;
        void SummaryIndicator()
        {
            var summary = ChatCompactionHistory.Active(conversation);
            reviewSummary.Visibility = summary == null ? Visibility.Collapsed : Visibility.Visible;
            compactionNote.Text = summary?.Description ?? (conversation.AutomaticCompaction ? "Automatic compaction at 80% · originals stay in the library" : "Automatic compaction off · full history is used");
            if (summary == null && conversation.Compactions.Count > 0 && conversation.AutomaticCompaction)
                compactionNote.Text += " · saved summaries require validation; original history is used";
            if (conversation.CompactionStatus.Length > 0) compactionNote.Text += "\n" + conversation.CompactionStatus;
        }
        void QueueContextUsage()
        {
            usageTimer.Stop(); usageCancellation?.Cancel(); usageGeneration++; SummaryIndicator();
            if (busy) return;
            if (serverUnreachable) { contextMeter.Value = 0; contextUsage.Text = "Context usage unavailable · the selected model is not reachable; start it or choose another connection"; return; }
            if (serverInfo?.ExactContext != true) { contextMeter.Value = 0; contextUsage.Text = "Context usage unavailable · this connection does not report exact prompt tokens; automatic compaction is unavailable" + (serverInfo?.RestrictedTextAdapter == true ? ". Restart the managed model to enable it." : ""); return; }
            contextUsage.Text = "Counting context…"; usageTimer.Start();
        }
        usageTimer.Tick += async (_, _) =>
        {
            usageTimer.Stop(); if (busy || SelectedChatProfile(profiles) is not {} profile || serverInfo is not {} info) return;
            var selected = conversation; int generation = usageGeneration;
            using var cancellation = new CancellationTokenSource(); usageCancellation = cancellation;
            try
            {
                var input = ChatRequests.UserContent(message.Text, selected.DraftAttachments);
                int? before = editing.HasValue ? selected.Turns.FindIndex(t => t.Id == editing) : null;
                var request = ChatRequests.BuildRaw(selected, input, before);
                if (!ChatCompactor.FitsWire(request)) { contextMeter.Value = 100; contextUsage.Text = "History needs compaction before sending · exact full-history count unavailable"; return; }
                using var client = new LocalAiClient();
                var measured = await client.MeasureChat(profile, aiSessionKeys.GetValueOrDefault(profile.Id) ?? Store.Unprotect(profile.Secret), request, selected.Generation, info, cancellation.Token, allowEmptyDraft: true);
                if (generation != usageGeneration || selected != conversation || measured == null) return;
                double percent = (double)measured.Prompt / measured.Context * 100;
                contextMeter.Value = Math.Min(100, percent);
                contextUsage.Text = $"Context{(message.Text.Length > 0 ? " with draft" : " before next message")} · {measured.Prompt:N0} / {measured.Context:N0} tokens ({percent:0.#}%)";
                if (info.DecoderContext is int decoder) contextUsage.Text += $" · separate output: {decoder:N0} tokens";
                if (percent >= ChatCompactor.TriggerPercent && selected.AutomaticCompaction) contextUsage.Text += " · compaction at Send";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (generation == usageGeneration) { contextMeter.Value = 0; contextUsage.Text = "Context count unavailable · " + error.Message; } }
            finally { if (usageCancellation == cancellation) usageCancellation = null; }
        };
        var tokens = new NumberBox { Header = "Maximum output tokens · blank = Automatic", PlaceholderText = "Automatic · use available output", Minimum = 1, Maximum = int.MaxValue, SmallChange = 256 };
        var automaticOutput = Button("Use Automatic output", (_, _) => { tokens.Value = double.NaN; });
        var answerSpace = new NumberBox { Header = "Answer headroom · % of output", Minimum = 1, Maximum = 100, SmallChange = 1 };
        var reasoningLevels = new[] { "default", "off", "low", "medium", "high", "max" };
        var reasoningChoice = new ComboBox { ItemsSource = new[] { "Default · automatic budget", "Off", "Low · up to 512 tokens", "Medium · up to 2,048 tokens", "High · up to 8,192 tokens", "Max · keeps answer space" }, SelectedIndex = 0, MinWidth = 130, MaxWidth = 260, IsEnabled = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reasoningChoice, "Reasoning level");
        ToolTipService.SetToolTip(reasoningChoice, "Reasoning and the answer share output. Budgets adapt to leave answer headroom (25% by default, adjustable in settings). Unused reasoning tokens remain available to the answer.");
        void BudgetPreview()
        {
            if (busy) return;
            if (conversation.MaxTokens is int total)
            {
                var plan = ChatBudgetPlan.Resolve(total, conversation.Generation, serverInfo?.Reasoning == true);
                budgetStatus.Text = $"Custom output: {total:N0} tokens" + (plan.Reasoning is int reasoning ? $" · reasoning ≤ {reasoning:N0} · answer headroom {plan.AnswerReserve:N0}" : "") +
                    (plan.ReasoningAdjusted ? " · reasoning reduced to leave answer space" : "") + " · exact context fit checked at Send";
            }
            else budgetStatus.Text = (serverInfo?.DecoderContext is int decoder ? $"Automatic output · up to {decoder:N0} tokens in the separate decoder window" : "Automatic output · uses remaining context at Send") + (serverInfo?.Reasoning == true ? $" · {conversation.Generation.AnswerReservePercent}% answer headroom; unused reasoning space stays available" : "");
        }
        var numeric = new List<(NumberBox Box, Func<ChatGenerationOptions,double?> Read, Action<ChatGenerationOptions,double?> Write, bool Llama)>();
        NumberBox Sampling(string label, double minimum, double maximum, double step, Func<ChatGenerationOptions,double?> read, Action<ChatGenerationOptions,double?> write, bool llama = false)
        {
            var box = new NumberBox { Header = label, PlaceholderText = "Model default", Minimum = minimum, Maximum = maximum, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            numeric.Add((box, read, write, llama));
            box.ValueChanged += (_, _) => { if (!loading) { write(conversation.Generation, double.IsNaN(box.Value) ? null : box.Value); SaveSoon(); } };
            return box;
        }
        var sampling = Stack(Text("Blank fields use the model's server defaults.", 12),
            Sampling("Temperature",0,2,.1,g=>g.Temperature,(g,v)=>g.Temperature=v),
            Sampling("Top P",0,1,.05,g=>g.TopP,(g,v)=>g.TopP=v),
            Sampling("Top K",0,int.MaxValue,1,g=>g.TopK,(g,v)=>g.TopK=v.HasValue?(int)v:null,true),
            Sampling("Min P",0,1,.01,g=>g.MinP,(g,v)=>g.MinP=v,true),
            Sampling("Typical P",0,1,.05,g=>g.TypicalP,(g,v)=>g.TypicalP=v,true),
            Sampling("Repeat penalty",0,2,.05,g=>g.RepeatPenalty,(g,v)=>g.RepeatPenalty=v,true),
            Sampling("Presence penalty",-2,2,.1,g=>g.PresencePenalty,(g,v)=>g.PresencePenalty=v),
            Sampling("Frequency penalty",-2,2,.1,g=>g.FrequencyPenalty,(g,v)=>g.FrequencyPenalty=v),
            Sampling("Seed · -1 for random",-1,int.MaxValue,1,g=>g.Seed,(g,v)=>g.Seed=v.HasValue?(int)v:null));
        var timeoutChoice = new NumberBox { Header = "Request timeout · minutes", Minimum = 1, Maximum = 120, SmallChange = 1 };
        var stops = new TextBox { Header = "Stop sequences · one per line, up to eight", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxLength = 2056 };
        var jsonOutput = new ToggleSwitch { Header = "Output format", OffContent = "Text / Markdown", OnContent = "JSON object" };
        var parseReasoning = new ToggleSwitch { Header = "Separate reasoning from the answer", OffContent = "Raw model output", OnContent = "Reasoning in its own section" };
        void LoadSettings()
        {
            bool wasLoading = loading; loading = true;
            try { tokens.Value = conversation.MaxTokens ?? double.NaN; answerSpace.Value = conversation.Generation.AnswerReservePercent; reasoningChoice.SelectedIndex = Math.Max(0, Array.IndexOf(reasoningLevels, conversation.Generation.Reasoning)); foreach(var item in numeric)item.Box.Value=item.Read(conversation.Generation)??double.NaN; timeoutChoice.Value=conversation.Generation.TimeoutMinutes; stops.Text=string.Join("\n",conversation.Generation.Stop); jsonOutput.IsOn=conversation.Generation.JsonOutput; parseReasoning.IsOn=conversation.Generation.ParseReasoning; }
            finally { loading = wasLoading; }
            autoCompact.IsOn = conversation.AutomaticCompaction;
            BudgetPreview();
        }
        var resetSettings = Button("Reset generation and use Automatic output", (_, _) => { conversation.Generation = new(); conversation.MaxTokens = null; LoadSettings(); SaveSoon(); });
        var options = new Flyout { Content = new ScrollViewer { Content = Stack(Text("Conversation settings", 21), contextStatus, system, tokens, automaticOutput, autoCompact,
            Text("Compaction protects source-quoted facts and separately checks the digest for omissions and contradictions before use. A failed or uncertain check discards it; unchanged history is used if it fits, otherwise Send is blocked with recovery guidance. Review the validation record beside the context meter. Originals stay saved.", 12),
            Text("Automatic uses the shared window left after the exact prompt, or the independent output window for encoder–decoder models, with no fixed app output ceiling. Servers without exact token discovery manage their own Automatic output. Reasoning caps leave answer headroom; unused thinking space is available for the answer.", 12),
            new Expander { Header="Sampling & penalties", Content=sampling, HorizontalAlignment=HorizontalAlignment.Stretch, HorizontalContentAlignment=HorizontalAlignment.Stretch },
            new Expander { Header="Output & advanced", Content=Stack(answerSpace,jsonOutput,stops,parseReasoning,timeoutChoice), HorizontalAlignment=HorizontalAlignment.Stretch, HorizontalContentAlignment=HorizontalAlignment.Stretch },
            resetSettings, Text("Settings are saved with this conversation. PDF and images are preview only for this text connection.", 12)), Width = 410, MaxHeight = 620 } };
        var settingsButton = ChatIcon("Conversation settings", "\uE713", (_, _) => { }); settingsButton.Flyout = Fluent.Acrylic(options);
        settingsButton.Content = MotifIcon(Fluent.Motif.Settings, 16);
        // Tools for this conversation: the web, skills and MCP, saved with it. Offered only to a model that can call tools.
        var webTools = new ToggleSwitch { Header = "Web", OnContent = "Read pages and search; every new host asks", OffContent = "Off" };
        var skillTools = new ToggleSwitch { Header = "Skills", OnContent = "Skills from extensions and this computer", OffContent = "Off" };
        var mcpTools = new ToggleSwitch { Header = "MCP", OnContent = "Your MCP servers' allowed tools; every call asks", OffContent = "Off" };
        var toolsNote = Text("", 12); toolsNote.Opacity = .75; toolsNote.TextWrapping = TextWrapping.Wrap; toolsNote.MaxWidth = 300;
        var toolsButton = ChatIcon("Tools", "", (_, _) => { });
        toolsButton.Flyout = Fluent.Acrylic(new Flyout { Content = Stack(Text("Tools", 18), webTools, skillTools, mcpTools, toolsNote) });
        void ToolSwitches()
        {
            bool was = loading; loading = true;
            try { webTools.IsOn = conversation.Tools.Web; skillTools.IsOn = conversation.Tools.Skills; mcpTools.IsOn = conversation.Tools.Mcp; }
            finally { loading = was; }
            bool supported = ChatTools.Supported(serverInfo);
            webTools.IsEnabled = skillTools.IsEnabled = mcpTools.IsEnabled = supported && !busy;
            toolsNote.Text = !supported ? ChatTools.Unsupported : ChatTools.InternetOn(Store.Root) ? "Saved with this conversation." : "Saved with this conversation. The internet is off on this computer, so Web stays off.";
            var on = new[] { conversation.Tools.Web ? "Web" : null, conversation.Tools.Skills ? "Skills" : null, conversation.Tools.Mcp ? "MCP" : null }.OfType<string>().ToArray();
            ToolTipService.SetToolTip(toolsButton, supported ? "Tools: " + (on.Length == 0 ? "none" : string.Join(", ", on)) : ChatTools.Unsupported);
            toolsButton.Opacity = supported ? 1 : .55;
        }
        webTools.Toggled += (_, _) => { if (!loading) { conversation.Tools.Web = webTools.IsOn; SaveSoon(); ToolSwitches(); } };
        skillTools.Toggled += (_, _) => { if (!loading) { conversation.Tools.Skills = skillTools.IsOn; SaveSoon(); ToolSwitches(); } };
        mcpTools.Toggled += (_, _) => { if (!loading) { conversation.Tools.Mcp = mcpTools.IsOn; SaveSoon(); ToolSwitches(); } };
        system.TextChanged += (_, _) => { if (!loading) { conversation.SystemPrompt = system.Text; QueueContextUsage(); SaveSoon(); } };
        autoCompact.Toggled += (_, _) => { if (!loading) { conversation.AutomaticCompaction = autoCompact.IsOn; RenderTranscript(); QueueContextUsage(); SaveSoon(); } };
        tokens.ValueChanged += (_, _) => { if (!loading && (double.IsNaN(tokens.Value) || double.IsFinite(tokens.Value) && tokens.Value is >= 1 and <= int.MaxValue && tokens.Value == Math.Truncate(tokens.Value))) { conversation.MaxTokens = double.IsNaN(tokens.Value) ? null : (int)tokens.Value; BudgetPreview(); SaveSoon(); } };
        answerSpace.ValueChanged += (_, _) => { if (!loading && double.IsFinite(answerSpace.Value) && answerSpace.Value is >= 1 and <= 100 && answerSpace.Value == Math.Truncate(answerSpace.Value)) { conversation.Generation.AnswerReservePercent = (int)answerSpace.Value; BudgetPreview(); SaveSoon(); } };
        reasoningChoice.SelectionChanged += (_, _) => { if (!loading && reasoningChoice.SelectedIndex >= 0) { conversation.Generation.Reasoning=reasoningLevels[reasoningChoice.SelectedIndex]; BudgetPreview(); QueueContextUsage(); SaveSoon(); } };
        timeoutChoice.ValueChanged += (_, _) => { if (!loading && double.IsFinite(timeoutChoice.Value)) { conversation.Generation.TimeoutMinutes=(int)timeoutChoice.Value; SaveSoon(); } };
        stops.TextChanged += (_, _) => { if (!loading) { conversation.Generation.Stop=stops.Text.Split('\n').Select(s=>s.TrimEnd('\r')).Where(s=>s.Length>0).ToArray(); SaveSoon(); } };
        jsonOutput.Toggled += (_, _) => { if (!loading) { conversation.Generation.JsonOutput=jsonOutput.IsOn; SaveSoon(); } };
        parseReasoning.Toggled += (_, _) => { if (!loading) { conversation.Generation.ParseReasoning=parseReasoning.IsOn; SaveSoon(); } };
        void ServerControls()
        {
            reasoningChoice.IsEnabled=!busy && serverInfo?.Llama==true && serverInfo.Reasoning;
            foreach(var item in numeric)item.Box.IsEnabled=serverInfo?.RestrictedTextAdapter==true?item.Box.Header as string=="Temperature":!item.Llama||serverInfo?.Llama==true;
            stops.IsEnabled=jsonOutput.IsEnabled=serverInfo?.RestrictedTextAdapter!=true;
            parseReasoning.IsEnabled=serverInfo?.Llama==true;
            answerSpace.IsEnabled=serverInfo?.Llama==true && serverInfo.Reasoning;
            ToolSwitches();
        }
        void ShowServerInfo(ChatServerInfo info)
        {
            contextStatus.Text=$"Model capacity: {(info.ModelCapacity is int max?$"{max:N0}":"not reported")} tokens · Loaded window: {(info.Context is int ctx?$"{ctx:N0}":"not reported")} tokens. "+(info.Reasoning?"Reasoning controls detected.":"Reasoning controls not detected for this model.");
            if(info.DecoderContext is int decoder) contextStatus.Text+=$" Separate output window: {decoder:N0} tokens (decoder start already reserved).";
            if(info.RestrictedTextAdapter)contextStatus.Text+=" "+info.Detail;
            modelState.Text=(info.IdentityVerified?"● Connected · ":"Connection unverified · ")+(info.Context is int window?$"{window:N0} context tokens":"context not reported");
        }
        async Task RefreshServer()
        {
            if(!layout.IsLoaded || layout.Visibility!=Visibility.Visible || busy)return;
            int generation=++serverGeneration; serverInfo=null; serverUnreachable=false; ServerControls();
            usageTimer.Stop(); usageCancellation?.Cancel(); usageGeneration++; contextMeter.Value=0;
            budgetStatus.Text="Token usage is checked before sending. Reasoning and the answer share the output budget.";
            if(SelectedChatProfile(profiles) is not AiEndpointProfile profile){contextUsage.Text="Choose a model to inspect context usage.";contextStatus.Text="Choose a model to inspect its loaded context and controls.";return;}
            contextUsage.Text="Inspecting model context…";
            contextStatus.Text="Inspecting the selected server…";
            try
            {
                using var client=new LocalAiClient(); var info=await client.ChatServer(profile,aiSessionKeys.GetValueOrDefault(profile.Id)??Store.Unprotect(profile.Secret),default);
                if(generation!=serverGeneration)return; serverInfo=info;
                ShowServerInfo(info);
            }
            catch(Exception error){if(generation==serverGeneration){serverUnreachable=true;contextStatus.Text=error.Message;modelState.Text="Connection unavailable · check Models";}}
            finally { if(generation==serverGeneration){ServerControls(); BudgetPreview(); QueueContextUsage();} }
        }
        void History()
        {
            refreshingHistory = true;
            var query = historySearch.Text.Trim(); string lastGroup = "";
            var items = saved.Conversations.Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Turns.Any(t => t.Text.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c => c.Updated).Select(c =>
                {
                    var date = c.Updated.LocalDateTime.Date; var group = date == DateTime.Today ? "Today" : date == DateTime.Today.AddDays(-1) ? "Yesterday" : date.ToString("MMM d");
                    var row = new ChatHistoryItem(c, group == lastGroup ? "" : group); lastGroup = group; return row;
                }).ToArray();
            history.ItemsSource = items; history.SelectedItem = items.FirstOrDefault(i => i.Conversation.Id == conversation.Id); refreshingHistory = false;
        }
        libraryToggle = ChatIcon("Toggle conversation library", "\uE8FD", (_, _) => { saved.LibraryOpen = layout.ColumnDefinitions[0].Width.Value == 0; forcedPanel = saved.LibraryOpen ? "library" : null; Arrange(); Save(); });
        previewToggle = ChatIcon("Toggle reference sidebar", "\uE8A5", (_, _) => { saved.PreviewOpen = layout.ColumnDefinitions[2].Width.Value == 0; forcedPanel = saved.PreviewOpen ? "preview" : null; Arrange(); Save(); });
        var libraryClose = ChatIcon("Collapse conversation library", "\uE8FC", (_, _) => { saved.LibraryOpen = false; forcedPanel = null; Arrange(); Save(); });
        var previewClose = ChatIcon("Collapse reference sidebar", "\uE711", (_, _) => { saved.PreviewOpen = false; forcedPanel = null; Arrange(); Save(); });
        void ShowArtifact(ChatArtifact artifact)
        {
            var items = artifacts.ItemsSource as List<ChatArtifact> ?? [];
            if (!items.Contains(artifact)) { items.Add(artifact); artifacts.ItemsSource = null; artifacts.ItemsSource = items; }
            artifacts.Visibility = previewMode.Visibility = Visibility.Visible;
            saved.PreviewOpen = true; forcedPanel = "preview"; artifacts.SelectedItem = artifact; Arrange(); SaveSoon();
        }
        // While a picture or PDF page is decoded, the reference panel shows its shape instead of standing empty.
        FrameworkElement PreviewSkeleton() => Scroller(Fluent.Skeleton(.45, .95, .9, .8, .6));
        FrameworkElement Scroller(FrameworkElement element) => new ScrollViewer { Content = element, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(6, 10, 6, 10) };
        void RefreshArtifacts()
        {
            var previous = artifacts.SelectedItem as ChatArtifact;
            var list = ChatContent.Artifacts(conversation); artifacts.ItemsSource = list;
            artifacts.SelectedItem = list.FirstOrDefault(a => a.Attachment?.Id == previous?.Attachment?.Id && a.Name == previous?.Name && a.Content == previous?.Content) ?? list.LastOrDefault();
            artifacts.Visibility = previewMode.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (list.Count == 0) { previewNote.Text = ""; previewHost.Content = Scroller(Stack(Text("Your reference space", 18), Muted("Open an attachment, a table, a chart, or a message to view it alongside your conversation.", 14))); previewMode.ItemsSource = null; }
        }
        void RenderDraft()
        {
            attachmentRow.Children.Clear();
            foreach (var file in conversation.DraftAttachments.ToArray())
            {
                var remove = ChatIcon("Remove " + file.Name, "\uE711", (_, _) => { conversation.DraftAttachments.Remove(file); RenderDraft(); RefreshArtifacts(); Save(); }); remove.IsEnabled = !busy;
                attachmentRow.Children.Add(ChatSurface(Row(Button(file.Label, (_, _) => ShowArtifact(new(file.Name, file.Kind, file.Text, file))), remove), new Thickness(3)));
            }
            attachmentRow.Visibility = conversation.DraftAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            int sendable = conversation.DraftAttachments.Count(a => a.Sendable), previewOnly = conversation.DraftAttachments.Count - sendable;
            attachmentHint.Text = editing.HasValue ? "Editing your last message · sending replaces its answer" : sendable + previewOnly == 0 ? "" : $"{sendable} text attachment(s) included with your next message" + (previewOnly > 0 ? $" · {previewOnly} preview only" : "");
            QueueContextUsage();
        }
        void RenderTranscript()
        {
            // A rebuild (another conversation, the finished answer replacing its streaming view) appears at once;
            // only the messages Send appends animate in (AppendMessages).
            transcript.ChildrenTransitions = null;
            transcript.Children.Clear(); welcomeHost = null;
            if (conversation.Turns.Count == 0)
            {
                var welcome = ChatWelcome(prompt => { message.Text = prompt; message.Focus(FocusState.Programmatic); message.SelectionStart = prompt.Length; });
                FitWelcome(welcome, transcriptScroll.ActualHeight - 40); welcomeHost = welcome;
                transcript.Children.Add(welcome); welcomeArt.Visibility = Visibility.Visible; return;
            }
            welcomeArt.Visibility = Visibility.Collapsed;
            void RenderTurn(ChatTurn turn, StackPanel host)
            {
                var name = Text(turn.Role == "user" ? "You" : turn.Model.Length == 0 ? "Assistant" : turn.Model, 13); name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                var time = Text(turn.At.ToLocalTime().ToString("t"), 12); time.Opacity = .6;
                var content = new StackPanel { Spacing = 12 };
                // The app icon speaks for the model; your own messages keep a plain person glyph.
                UIElement speaker = turn.Role == "user" ? new FontIcon { Glyph = "\uE77B", FontSize = 16, Width = 26 } : AppIconImage(26);
                content.Children.Add(Row(speaker, name, time));
                if (turn.Reasoning.Length > 0) content.Children.Add(new Expander { Header = "Reasoning", Content = ChatInline(turn.Reasoning, 14), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                foreach (var step in turn.Steps) content.Children.Add(ChatStepRow(step));
                content.Children.Add(ChatMarkdown(turn.Text, ShowArtifact));
                foreach (var file in turn.Attachments) content.Children.Add(Button(file.Label, (_, _) => ShowArtifact(new(file.Name, file.Kind, file.Text, file))));
                var actions = Row(ChatIcon("Copy message", "\uE8C8", (_, _) => ChatCopy(turn.Text)), ChatIcon("Open message in reference sidebar", "\uE8A5", (_, _) => ShowArtifact(new("Message", "markdown", turn.Text))));
                if (turn.Role == "assistant" && turn == conversation.Turns.LastOrDefault()) actions.Children.Add(Button("Retry", async (_, _) => await Retry()));
                if (turn.Role == "user" && turn == conversation.Turns.LastOrDefault(t => t.Role == "user")) actions.Children.Add(Button("Edit", (_, _) =>
                {
                    if (busy) return; editing = turn.Id; conversation.DraftAttachments = turn.Attachments.ToList(); message.Text = turn.Text; RenderDraft(); message.Focus(FocusState.Programmatic);
                }));
                foreach (var action in actions.Children.OfType<Control>()) action.IsEnabled = !busy;
                // Message actions are secondary to the message: compact and quiet until needed.
                foreach (var action in actions.Children.OfType<Button>()) { action.MinWidth = 0; action.MinHeight = 30; action.Padding = new Thickness(8, 4, 8, 4); action.FontSize = 12; }
                actions.Spacing = 4; actions.Opacity = .85;
                content.Children.Add(actions);
                if (turn.State != "Complete")
                {
                    var incomplete = Text(turn.State + " · incomplete answers are excluded from later context", 12); incomplete.VerticalAlignment = VerticalAlignment.Center;
                    // Failures get the "check failed" pose; a stop you asked for is not a failure.
                    if (turn.State is "Failed" or "No final answer") content.Children.Add(Row(MascotImage(PoseFailed, 40, turn.State.ToLowerInvariant()), incomplete));
                    else content.Children.Add(incomplete);
                }
                if (turn.StatusDetail.Length > 0) content.Children.Add(Text(turn.StatusDetail, 12));
                if (!string.IsNullOrWhiteSpace(turn.PerformanceSummary)) { var performance = Text(turn.PerformanceSummary, 11); performance.Opacity = .7; content.Children.Add(performance); }
                if (turn.BudgetSummary.Length > 0) { var summary = Text(turn.BudgetSummary, 11); summary.Opacity = .65; content.Children.Add(summary); }
                host.Children.Add(turn.Role == "user" ? ChatSurface(content) : new Border { Child = content, Padding = new Thickness(16, 0, 16, 0) });
            }
            int firstRecent = 0;
            if (ChatCompactionHistory.Active(conversation) is {} compacted)
            {
                firstRecent = conversation.Turns.FindIndex(t => t.Id == compacted.ThroughTurnId) + 1;
                var originals = new StackPanel { Spacing = 18 }; int displayed = 0;
                Button moreOriginals = null!;
                void MoreOriginals()
                {
                    originals.Children.Remove(moreOriginals);
                    int end = Math.Min(firstRecent, displayed + 50);
                    for (; displayed < end; displayed++) RenderTurn(conversation.Turns[displayed], originals);
                    if (displayed < firstRecent) originals.Children.Add(moreOriginals);
                }
                moreOriginals = Button("Show 50 more original messages", (_, _) => MoreOriginals());
                var expander = new Expander { Header = $"Original messages · {firstRecent:N0} retained", Content = originals, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                expander.Expanding += (_, _) => { if (originals.Children.Count == 0) MoreOriginals(); };
                transcript.Children.Add(ChatSurface(Stack(Text("Older messages summarized", 17), Text(compacted.Description, 12),
                    Button("Review the summary used by this model", (_, _) => ShowArtifact(new("Conversation summary", "markdown", compacted.Markdown))), expander)));
            }
            foreach (var turn in conversation.Turns.Skip(firstRecent)) RenderTurn(turn, transcript);
        }
        void ProfileState()
        {
            var profile = SelectedChatProfile(profiles);
            var owned = modelServing.Running.FirstOrDefault(s => s.Profile.Id == profile?.Id);
            modelState.Text = profile == null ? "Choose a model to begin" : owned != null ? owned.Owner.IsRunning ? "● Running locally" : "Model stopped · start it in Models" : "Local connection · checked when you send";
            if (profile != null) ToolTipService.SetToolTip(profiles, profile.Model + "\n" + profile.Endpoint);
        }
        refreshNativeChatProfiles = () =>
        {
            loading = true;
            try
            {
                // Same saved connections the Code page offers: any external endpoint with a model, or a managed chat model.
                var choices = (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).Where(p => p.Model.Length > 0 && p.ForChat).ToArray();
                PresentChatModels(profiles, choices);
                profiles.SelectedItem = (profiles.ItemsSource as IEnumerable<ChatModelChoice>)?.FirstOrDefault(p => p.Id == conversation.ProfileId);
            }
            catch (Exception error) { state.Text = "Models could not be loaded: " + error.Message; }
            finally { loading = false; ProfileState(); _ = RefreshServer(); }
        };
        void Select(ChatConversation selected)
        {
            conversation = selected; saved.Selected = selected.Id; editing = null; loading = true;
            state.Text = "Ready · draft context is counted by the selected local server; generation starts when you choose Send.";
            title.Text = conversation.Title; message.Text = conversation.Draft; system.Text = conversation.SystemPrompt; LoadSettings(); loading = false;
            refreshNativeChatProfiles(); RenderTranscript(); RenderDraft(); RefreshArtifacts(); History(); Save();
        }
        void New(Guid? id = null)
        {
            if (busy) { state.Text = "Stop the current answer before opening another conversation."; return; }
            try { Select(library.New(id ?? conversation.ProfileId)); message.Focus(FocusState.Programmatic); }
            catch (Exception error) { state.Text = error.Message; }
        }
        openNativeChat = id => { if (busy || aiOperation != null) { status.Text = "Finish or cancel the active AI operation before opening another chat."; return; } New(id); Navigate("Chat"); };
        // The title bar search opens conversations and starts new ones through these.
        openChatConversation = chosen => { if (busy) { status.Text = "Stop the current answer before opening another conversation."; return; } Navigate("Chat"); if (chosen != conversation) Select(chosen); };
        newNativeChat = () => New();
        profiles.SelectionChanged += async (_, _) => { if (!loading) { conversation.ProfileId = SelectedChatProfile(profiles)?.Id; SaveSoon(); ProfileState(); await RefreshServer(); } };
        history.SelectionChanged += (_, _) => { if (!refreshingHistory && !busy && history.SelectedItem is ChatHistoryItem item) Select(item.Conversation); };
        historySearch.TextChanged += (_, _) => History();
        message.TextChanged += (_, _) => { if (!loading) { conversation.Draft = message.Text; BudgetPreview(); QueueContextUsage(); SaveSoon(); } };
        newChat = Button("New chat", (_, _) => New(), true); newChat.HorizontalAlignment = HorizontalAlignment.Stretch;
        newChat.Content = Row(new FontIcon { Glyph = "\uE710", FontSize = 12 }, new TextBlock { Text = "New chat" });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(newChat, "New chat");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(history, "Conversations");
        var libraryHead = Toolbar(Text("Conversations", 18), libraryClose);
        libraryPanel = Vertical(Stack(libraryHead, newChat, historySearch), history); SurfaceBehind(libraryPanel, new Thickness(12, 12, 8, 8));
        layout.Children.Add(libraryPanel);
        var previewHead = Toolbar(Text("Reference", 18), previewClose);
        previewPanel = Vertical(Stack(previewHead, artifacts, previewMode, previewNote), previewHost); SurfaceBehind(previewPanel, new Thickness(14, 12, 14, 12));
        Grid.SetColumn(previewPanel, 2); // added after the conversation so Tab reaches it last
        void Arrange()
        {
            double width = layout.ActualWidth > 0 ? layout.ActualWidth : 1300;
            // The library is the way back to earlier conversations, so it stays at ordinary window sizes.
            // The reference panel needs a wide window unless it was opened on purpose.
            bool showLibrary = saved.LibraryOpen && (width >= 700 || forcedPanel == "library");
            bool showPreview = saved.PreviewOpen && (width >= 1100 || forcedPanel == "preview");
            if (width < 1100 && showLibrary && showPreview) { if (forcedPanel == "preview") showLibrary = false; else showPreview = false; }
            libraryPanel.Visibility = showLibrary ? Visibility.Visible : Visibility.Collapsed; previewPanel.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
            layout.ColumnDefinitions[0].Width = new GridLength(showLibrary ? width < 1300 ? 236 : 264 : 0);
            layout.ColumnDefinitions[2].Width = new GridLength(showPreview ? Math.Clamp(width * .27, 300, 420) : 0);
            ToolTipService.SetToolTip(libraryToggle, showLibrary ? "Collapse conversation library" : "Show conversation library");
            ToolTipService.SetToolTip(previewToggle, showPreview ? "Collapse reference sidebar" : "Show reference sidebar");
        }
        layout.SizeChanged += (_, _) => Arrange();
        artifacts.SelectionChanged += async (_, _) =>
        {
            if (artifacts.SelectedItem is not ChatArtifact artifact) return;
            configuringPreview = true;
            previewMode.ItemsSource = artifact.Kind is "table" or "csv" or "tsv" or "chart" ? new[] { "Preview", "Table", "Chart", "Source" } : artifact.Kind == "json" ? new[] { "Preview", "Table", "Chart", "Source" } : artifact.Kind is "code" or "text" or "html" ? new[] { "Source" } : new[] { "Preview", "Source" };
            previewMode.SelectedIndex = 0;
            configuringPreview = false;
            await Preview();
        };
        previewMode.SelectionChanged += async (_, _) => { if (!configuringPreview && previewMode.SelectedItem != null) await Preview(); };
        async Task Preview()
        {
            if (!layout.IsLoaded || layout.Visibility != Visibility.Visible) return;
            int generation = ++previewGeneration; previewResource?.Dispose(); previewResource = null;
            if (artifacts.SelectedItem is not ChatArtifact artifact) return;
            string mode = previewMode.SelectedItem as string ?? "Preview";
            previewNote.Text = artifact.Kind == "html" ? "HTML · shown as source; open it in your browser to see it rendered" : artifact.Attachment is { Sendable: false } ? "Preview only · not sent to this text model" : artifact.Kind + " · local reference";
            try
            {
                // HTML is only ever shown as source, with the option to open it in the browser.
                if (artifact.Kind == "html") { previewHost.Content = ChatHtml(artifact.Content, message => previewNote.Text = message); return; }
                if (mode == "Source" && artifact.Attachment?.Kind is not ("image" or "pdf")) { previewHost.Content = Scroller(ChatInline(artifact.Content, 14, true)); return; }
                if (artifact.Kind == "image" && artifact.Attachment != null)
                {
                    var image = new Image { Stretch = Stretch.Uniform }; previewHost.Content = PreviewSkeleton();
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, "Attached image " + artifact.Attachment.Name);
                    using var memory = new MemoryStream(await File.ReadAllBytesAsync(library.FilePath(artifact.Attachment)));
                    using var stream = memory.AsRandomAccessStream();
                    var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); if (generation == previewGeneration) { image.Source = bitmap; previewHost.Content = Scroller(image); FadeIn(image); } return;
                }
                if (artifact.Kind == "pdf" && artifact.Attachment != null)
                {
                    previewHost.Content = PreviewSkeleton();
                    var memory = new MemoryStream(await File.ReadAllBytesAsync(library.FilePath(artifact.Attachment)));
                    previewResource = memory;
                    var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(memory.AsRandomAccessStream());
                    if (generation != previewGeneration) return;
                    uint page = 0; var image = new Image { Stretch = Stretch.Uniform }; var pageLabel = Text("", 12);
                    Button? previous = null, next = null;
                    async Task Page()
                    {
                        using var item = pdf.GetPage(page); using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        await item.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1000 });
                        var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); if (generation != previewGeneration) return; image.Source = bitmap; pageLabel.Text = $"Page {page + 1} / {pdf.PageCount}";
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, $"{artifact.Attachment.Name}, page {page + 1} of {pdf.PageCount}");
                        previous!.IsEnabled = page > 0; next!.IsEnabled = page + 1 < pdf.PageCount;
                    }
                    previous = Button("Previous", async (_, _) => { if (page > 0) { page--; await Page(); } }); next = Button("Next", async (_, _) => { if (page + 1 < pdf.PageCount) { page++; await Page(); } });
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(previous, "Previous page"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(next, "Next page");
                    previewHost.Content = Vertical(ScrollTools(Row(previous, pageLabel, next)), Scroller(image)); await Page(); return;
                }
                if (mode is "Table" or "Chart" || artifact.Kind is "table" or "csv" or "tsv" or "chart")
                {
                    var data = ChatContent.Table(artifact.Content, artifact.Kind);
                    if (mode == "Chart" || artifact.Kind == "chart" && mode == "Preview")
                    {
                        var chart = new ChartView(ChatContent.Chart(artifact.Content, artifact.Kind, data)) { Compact = true, WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this) }; chart.Bind(data, false, data.Rows.Count);
                        chart.ShowRows += () => previewMode.SelectedItem = "Table"; previewHost.Content = chart;
                    }
                    else { var grid = new ResultGrid(); grid.Show(data); previewHost.Content = grid; }
                    return;
                }
                previewHost.Content = Scroller(artifact.Kind == "json" ? ChatInline(JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(artifact.Content), new JsonSerializerOptions { WriteIndented = true }), 14, true) : ChatMarkdown(artifact.Content));
            }
            catch (Exception error)
            {
                try { File.WriteAllText(Path.Combine(Store.Root, "chat-preview-error.txt"), error.ToString()); } catch (IOException) { }
                if (generation == previewGeneration) previewHost.Content = Scroller(TroubleNotice("Preview unavailable", error.Message, "Choose Source to inspect the original content."));
            }
        }
        attach = ChatIcon("Attach file", "\uE723", async (_, _) =>
        {
            if (busy) return;
            try
            {
                if (conversation.DraftAttachments.Count >= 6) throw new ArgumentException("Attach up to six files per message.");
                var picker = new FileOpenPicker(AppWindow.Id); foreach (var ext in new[] { ".md", ".txt", ".csv", ".tsv", ".json", ".html", ".docx", ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".cs", ".py", ".js", ".ts", ".sql", ".xml", ".log" }) picker.FileTypeFilter.Add(ext);
                var file = await picker.PickSingleFileAsync(); if (file == null) return;
                var attachment = await Task.Run(() => library.Import(file.Path)); conversation.DraftAttachments.Add(attachment);
                RenderDraft(); RefreshArtifacts(); ShowArtifact(new(attachment.Name, attachment.Kind, attachment.Text, attachment)); Save();
            }
            catch (Exception error) { state.Text = "Attachment: " + error.Message; }
        });
        send = ChatIcon("Send message", "\uE724", async (_, _) => await Send()); send.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; BrandAccent(send);
        stop = ChatIcon("Stop generating", "\uE71A", (_, _) => aiOperation?.Cancel()); stop.Visibility = Visibility.Collapsed;
        void Enabled(bool enabled)
        {
            busy = !enabled; profiles.IsEnabled = history.IsEnabled = historySearch.IsEnabled = newChat.IsEnabled = message.IsEnabled = attach.IsEnabled = settingsButton.IsEnabled = enabled;
            send.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed; stop.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            ServerControls();
        }
        async Task Send(string? retryInput = null, int? replaceAt = null)
        {
            if (busy || aiOperation != null) { state.Text = "Finish or cancel the active AI request first."; return; }
            if (SelectedChatProfile(profiles) is not AiEndpointProfile profile) { state.Text = "Choose a saved chat model, or start one from Models & endpoints."; return; }
            var input = retryInput ?? message.Text.Trim(); if (string.IsNullOrWhiteSpace(input)) return;
            var attached = conversation.DraftAttachments.ToList(); int before = replaceAt ?? (editing.HasValue ? conversation.Turns.FindIndex(t => t.Id == editing) : conversation.Turns.Count);
            AiMessage[] request; string content, key;
            try
            {
                if (!double.IsNaN(tokens.Value) && (!double.IsFinite(tokens.Value) || tokens.Value != Math.Truncate(tokens.Value) || tokens.Value is < 1 or > int.MaxValue)) throw new ArgumentException("Use Automatic (blank) or a positive whole output-token limit in Conversation settings.");
                if (!double.IsFinite(answerSpace.Value) || answerSpace.Value != Math.Truncate(answerSpace.Value) || answerSpace.Value is < 1 or > 100) throw new ArgumentException("Answer headroom must be a whole percentage between 1 and 100.");
                if(numeric.Any(n=>n.Box.SmallChange==1&&double.IsFinite(n.Box.Value)&&n.Box.Value!=Math.Truncate(n.Box.Value))||!double.IsFinite(timeoutChoice.Value)||timeoutChoice.Value!=Math.Truncate(timeoutChoice.Value))throw new ArgumentException("Top K, seed and timeout must be whole numbers.");
                conversation.Generation.Validate();
                conversation.MaxTokens = double.IsNaN(tokens.Value) ? null : (int)tokens.Value;
                content = ChatRequests.UserContent(input, attached); request = ChatRequests.BuildRaw(conversation, content, before);
                key = aiSessionKeys.GetValueOrDefault(profile.Id) ?? Store.Unprotect(profile.Secret);
            }
            catch (Exception error) { state.Text = error.Message; return; }
            usageTimer.Stop(); usageCancellation?.Cancel(); usageGeneration++;
            using var cancellation = new CancellationTokenSource(); aiOperation = cancellation; Enabled(false); serverGeneration++;
            using var client = new LocalAiClient(); ChatServerInfo verifiedServer; ChatBudgetPlan plan;
            var generationOptions=JsonSerializer.Deserialize<ChatGenerationOptions>(JsonSerializer.Serialize(conversation.Generation))!;
            try
            {
                state.Text="Checking model identity and context budget…";
                verifiedServer=await client.ChatServer(profile,key,cancellation.Token); serverInfo=verifiedServer; ShowServerInfo(verifiedServer);
                if(generationOptions.Reasoning!="default"&&!verifiedServer.Reasoning)throw new ArgumentException("The selected model has not reported reasoning controls. Choose Default or a model with reasoning support.");
                var prepared=await ChatCompactor.Prepare(client,profile,key,conversation,content,before,generationOptions,verifiedServer,
                    note => { state.Text=note; contextUsage.Text=note; },cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                plan=prepared.Plan; request=prepared.Messages;
                conversation.CompactionStatus=prepared.Notice;
                if(prepared.Summary is {} summary)
                {
                    conversation.Compactions.Add(summary);
                    try { library.Save(); }
                    catch { conversation.Compactions.Remove(summary); throw; }
                }
                SummaryIndicator();
                budgetStatus.Text=plan.Summary;
                if(plan.Prompt is int prompt && plan.Context is int context) { contextMeter.Value=Math.Min(100,(double)prompt/context*100); contextUsage.Text=$"Context · {prompt:N0} / {context:N0} tokens ({(double)prompt/context*100:0.#}%)"; }
            }
            catch(Exception error){state.Text=(error is OperationCanceledException?"Cancelled · ":"Request not sent · ")+error.Message; if(error is ChatCompactionQualityException){conversation.CompactionStatus=error.Message;Save();} aiOperation=null; Enabled(true); QueueContextUsage(); return;}
            if (before < conversation.Turns.Count) conversation.Turns.RemoveRange(before, conversation.Turns.Count - before);
            var user = new ChatTurn { Text = input, RequestText = content, Attachments = attached };
            var answer = new ChatTurn { Role = "assistant", State = "Streaming", Model = profile.DisplayName, BudgetSummary = plan.Summary };
            conversation.Turns.Add(user); conversation.Turns.Add(answer); conversation.Updated = DateTimeOffset.UtcNow;
            if (conversation.Title == "New conversation") conversation.Title = input.Length > 54 ? input[..54] + "…" : input;
            title.Text = conversation.Title; conversation.Draft = ""; message.Text = ""; conversation.DraftAttachments.Clear(); editing = null;
            RenderTranscript(); RenderDraft(); History(); Save();
            var liveAnswer = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, LineHeight = 24 };
            var liveReasoning = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var liveName = Text(profile.Name, 13); liveName.VerticalAlignment = VerticalAlignment.Center;
            var live = Stack(Row(MascotImage(PosePlanning, 34, "working on the answer"), liveName), new Expander { Header = "Reasoning", Content = liveReasoning, HorizontalAlignment = HorizontalAlignment.Stretch }, liveAnswer);
            // Your message and the answer's live view are the new containers: they animate in. Tokens then only
            // change the text inside the live view, which never re-triggers the panel's transitions.
            var sent = transcript.Children[^2];
            transcript.Children.RemoveAt(transcript.Children.Count - 1); transcript.Children.RemoveAt(transcript.Children.Count - 1);
            AppendMessages(transcript, sent, new Border { Child = live, Padding = new Thickness(16) }); followTail = true;
            var text = new StringBuilder(); var reasoning = new StringBuilder();
            var clock = System.Diagnostics.Stopwatch.StartNew(); long lastRender = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) =>
            {
                if (lastRender == text.Length + reasoning.Length) return;
                liveAnswer.Text = text.ToString(); liveReasoning.Text = reasoning.ToString(); lastRender = text.Length + reasoning.Length;
                if (followTail) transcriptScroll.ChangeView(null, transcriptScroll.ScrollableHeight, null, true);
            };
            timer.Start(); state.Text = "Waiting for the local model…";
            try
            {
                // Tool calls show as compact steps above the answer, and approvals as cards among them.
                var steps = new StackPanel { Spacing = 6 }; live.Children.Insert(1, steps);
                var tools = new ChatTools.Session(Store.Root, (ask, token) => ChatApproval(steps, ask, token));
                var result = await ChatTools.Run(client, profile, key, request, plan.Output, conversation.MaxTokens, generationOptions, verifiedServer, conversation.Tools, tools,
                    step => { answer.Steps.Add(step); steps.Children.Add(ChatStepRow(step)); state.Text = step.Label; },
                    chunk => { text.Append(chunk); state.Text = "Receiving local answer…"; }, chunk => { reasoning.Append(chunk); state.Text = "Reasoning…"; },
                    () => text.Clear(), cancellation.Token);
                var feedback = ChatCompletionFeedback.From(result, plan);
                answer.State = feedback.State; answer.StatusDetail = feedback.Detail; answer.PerformanceSummary = result.PerformanceSummary;
                state.Text = answer.State + " · " + result.PerformanceSummary + $" · {clock.Elapsed.TotalSeconds:0.0}s" + (feedback.Detail.Length > 0 ? " · See the message for details and recovery." : $" · {result.Characters:N0} characters");
            }
            catch (Exception error) { answer.State = error is OperationCanceledException ? "Cancelled" : error is ChatNoFinalAnswerException ? "No final answer" : "Failed"; answer.StatusDetail = error is OperationCanceledException && cancellation.IsCancellationRequested ? "Stopped before the answer finished." : error.Message; state.Text = answer.State + " · " + answer.StatusDetail; }
            finally
            {
                timer.Stop(); answer.Text = text.ToString(); answer.Reasoning = reasoning.ToString(); conversation.Updated = DateTimeOffset.UtcNow;
                // Rebuilding the transcript resets the scroll offset, which raises ViewChanged and clears followTail.
                // Read it first so a reader who stayed at the bottom still lands on the finished answer.
                bool stayAtEnd = followTail;
                aiOperation = null; Enabled(true); RenderTranscript(); RenderDraft(); RefreshArtifacts(); History(); Save(); QueueContextUsage();
                if (stayAtEnd) DispatcherQueue.TryEnqueue(() =>
                {
                    // The final Markdown/diagnostics can be taller than the streaming view.
                    transcriptScroll.UpdateLayout();
                    transcriptScroll.ChangeView(null, transcriptScroll.ScrollableHeight, null, true);
                });
            }
        }
        async Task Retry()
        {
            if (busy) return; int index = conversation.Turns.FindLastIndex(t => t.Role == "user"); if (index < 0) return;
            var turn = conversation.Turns[index]; conversation.DraftAttachments = turn.Attachments.ToList(); await Send(turn.Text, index);
        }
        message.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) { e.Handled = true; await Send(); }
        };
        // Growing content (streaming, final Markdown) moves the end away without the reader scrolling.
        // Only an upward scroll stops following; reaching the end resumes it.
        double lastOffset = 0;
        transcriptScroll.ViewChanged += (_, _) =>
        {
            double offset = transcriptScroll.VerticalOffset;
            if (transcriptScroll.ScrollableHeight - offset < 80) followTail = true;
            else if (offset < lastOffset - 1) followTail = false;
            lastOffset = offset;
        };
        // Final Markdown, the context strip and the reference panel settle after the answer is rendered;
        // keep a reader who is following the tail at the end while those size changes land.
        // The welcome reads from the top: in a short window its title stays in view and the prompts scroll beneath it.
        void KeepTail() { if (followTail) transcriptScroll.ChangeView(null, welcomeHost != null ? 0 : transcriptScroll.ScrollableHeight, null, true); }
        transcript.SizeChanged += (_, _) => KeepTail(); transcriptScroll.SizeChanged += (_, _) => KeepTail();
        var composerFooter = Toolbar(ScrollTools(Row(attach, attachmentRow)), Row(toolsButton, reasoningChoice, send, stop));
        // The composer floats above the transcript, with the window's shared soft shadow beneath it.
        var composer = Fluent.Float(ChatSurface(Stack(message, composerFooter), new Thickness(10)), 24);
        composer.CornerRadius = new CornerRadius(12);
        var footerHint = Text("Enter to send · Shift+Enter for a new line", 11); footerHint.Opacity = .6;
        var contextStrip = Stack(Toolbar(contextUsage, reviewSummary), contextMeter, compactionNote); contextStrip.Spacing = 4;
        var centerBody = new Grid { RowSpacing = 10 };
        centerBody.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); centerBody.RowDefinitions.Add(new() { Height = GridLength.Auto });
        // One line under the composer: what is happening on the left, the keyboard hint on the right.
        // Token budget detail belongs with the context meter above it.
        state.MaxLines = 2; state.TextTrimming = TextTrimming.CharacterEllipsis; state.Opacity = .8;
        state.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => ToolTipService.SetToolTip(state, state.Text));
        footerHint.TextWrapping = TextWrapping.NoWrap; footerHint.VerticalAlignment = VerticalAlignment.Top;
        contextStrip.Children.Add(budgetStatus);
        var composeFooter = new Grid { ColumnSpacing = 16 };
        composeFooter.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        composeFooter.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        composeFooter.Children.Add(state); Grid.SetColumn(footerHint, 1); composeFooter.Children.Add(footerHint);
        centerBody.Children.Add(welcomeArt); centerBody.Children.Add(transcriptScroll); var compose = Stack(contextStrip, attachmentHint, composer, composeFooter); compose.Spacing = 6; compose.MaxWidth = 940; Grid.SetRow(compose, 1); centerBody.Children.Add(compose);
        var more = new DropDownButton { Content = new FontIcon { Glyph = "\uE712", FontSize = 14 } }; var menu = Fluent.Acrylic(new MenuFlyout()); more.Flyout = menu;
        void Menu(string text, RoutedEventHandler action) { var item = new MenuFlyoutItem { Text = text }; item.Click += action; menu.Items.Add(item); }
        Menu("Rename conversation", async (_, _) =>
        {
            if (busy || dialogOpen) return; var input = new TextBox { Text = conversation.Title, MaxLength = 100 }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "Conversation title"); dialogOpen = true;
            try { if (await new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Rename conversation", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel" }.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text)) { conversation.Title = input.Text.Trim(); title.Text = conversation.Title; History(); Save(); } } finally { dialogOpen = false; }
        });
        Menu("Export conversation as Markdown", async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "conversation" }; picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" }); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync(); if (file == null) return;
                await Windows.Storage.FileIO.WriteTextAsync(file, "# " + conversation.Title + "\n\n" + string.Join("\n\n", conversation.Turns.Select(t => "## " + (t.Role == "user" ? "You" : t.Model) + " · " + t.State + "\n\n" + t.Text + (t.Attachments.Count > 0 ? "\n\nAttachments: " + string.Join(", ", t.Attachments.Select(a => a.Name)) : "")))); state.Text = "Conversation exported.";
            }
            catch (Exception error) { state.Text = "Export failed: " + error.Message; }
        });
        Menu("Delete conversation", async (_, _) =>
        {
            if (busy || dialogOpen) return; dialogOpen = true;
            try
            {
                if (await new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Delete this conversation?", Content = "Its messages and drafts will be removed from local chat history.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close }.ShowAsync() != ContentDialogResult.Primary) return;
                saved.Conversations.Remove(conversation); Select(saved.Conversations.OrderByDescending(c => c.Updated).FirstOrDefault() ?? library.New());
            }
            finally { dialogOpen = false; }
        });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "Conversation actions"); ToolTipService.SetToolTip(more, "Rename, export or delete this conversation");
        // Model first, its observed state beside it: the same reading order as the Code composer.
        modelState.VerticalAlignment = VerticalAlignment.Center;
        // The connection state wraps beside the model picker instead of being clipped in narrow windows.
        var modelRow = new Grid { ColumnSpacing = 12 }; modelRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); modelRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        modelState.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(modelState, 1); modelRow.Children.Add(profiles); modelRow.Children.Add(modelState);
        var header = Stack(Toolbar(Row(libraryToggle, title), Row(previewToggle, settingsButton, more)), modelRow); header.Spacing = 8;
        var center = Vertical(header, centerBody); center.SizeChanged += (_, _) => title.MaxWidth = Math.Max(120, center.ActualWidth - 210); Grid.SetColumn(center, 1); layout.Children.Add(center); layout.Children.Add(previewPanel);
        layout.Loaded += async (_, _) => { if (layout.Visibility == Visibility.Visible) { await Preview(); await RefreshServer(); } };
        layout.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, async (_, _) => { if (layout.IsLoaded && layout.Visibility == Visibility.Visible) { await Preview(); await RefreshServer(); } });
        Select(conversation); Arrange();
        Closed += (_, _) => { saveTimer.Stop(); usageTimer.Stop(); usageCancellation?.Cancel(); usageGeneration++; Save(); library.Dispose(); previewResource?.Dispose(); };
        return layout;
    }
}
