using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace Mason;

public sealed partial class MainWindow
{
    readonly ModelServing modelServing = new();
    Action? refreshModelLibrary;
    Action? refreshAiConnectionList;
    Action<Guid>? selectManagedAiConnection;
    /// <summary>Opens Discover with a query, e.g. to find a GGUF build of a local model.</summary>
    Action<string>? findOnHub;
    RuntimeFacts runtimeFacts = RuntimeFacts.None;
    LlamaBuildInfo? llamaBuild;
    Action? runtimeFactsChanged;
    /// <summary>Selects a Discover "Agent use" filter by index.</summary>
    Action<int>? discoverRole;
    Action? updateModelsShell;


    static ModelLibrarySettings DiscoveredSettings()
    {
        var runtime=RuntimeDiscovery.Find();
        return new([],runtime.LlamaServer,runtime.Python);
    }

    static ModelLibrarySettings NormalizeRuntimePaths(ModelLibrarySettings selected)=>selected with
    {
        LlamaServer=selected.LlamaServer??"",
        Python=selected.Python??"",
        QuantizedPython=string.IsNullOrEmpty(selected.QuantizedPython) && QuantizedRuntime.IsPrepared
            ? QuantizedRuntime.ManagedPython : selected.QuantizedPython??""
    };

    FrameworkElement BuildModelLibrary(FrameworkElement connections)
    {
        ModelLibrarySettings settings; bool readable = true;
        try { settings = ModelLibrarySettings.ReadSaved(Path.Combine(Store.Root,"model-library.json")) ?? DiscoveredSettings();
            if(settings.Locations==null || settings.Locations.Count>30 || settings.Locations.Any(x=>x==null || string.IsNullOrWhiteSpace(x.Path) || !Path.IsPathFullyQualified(x.Path))) throw new InvalidDataException("Invalid model location settings.");
            settings=NormalizeRuntimePaths(settings);
        }
        catch { settings = new([],"",""); readable = false; }
        Action? refreshRuntimeInventory=null;Action<bool>? runtimeBusy=null;Action<int>? selectRuntime=null;Button? setupCancel=null;
        var setupStages=StageRail("Inspect","Prepare","Validate");
        var setupProgress=BrandControl(new ProgressBar{Height=4,IsIndeterminate=true,Visibility=Visibility.Collapsed});
        GpuSample? gpuSample=null;
        var memoryProfiles=new Dictionary<string,ModelMemoryProfile>(StringComparer.OrdinalIgnoreCase);
        var registeredDownloads=new Dictionary<Guid,LocalModel[]>();
        FrameworkElement? libraryTop=null;
        var discovered = Array.Empty<LocalModel>(); CancellationTokenSource? scan = null; bool scanned = false;
        modelScanBusy=()=>scan!=null;modelScanCancel=()=>scan?.Cancel();
        var views=new ViewSwitcher {UseSegments=true};
        // Tabs map to pages: My models (library and running), Discover, Downloads, Runtimes.
        int[] tabPages={0,3,5,4,2};
        void ShowView(int page)=>views.SelectedIndex=Math.Max(0,Array.IndexOf(tabPages,page==1?0:page));
        var title = Heading("My models",28);
        var caption = Text("Your local AI, ready for work. Choose a model to see what it can do.",14);
        var summary = Text("Ready to discover your local collection.",13);
        var activity = new InfoBar { IsOpen=true, IsClosable=true, Severity=InfoBarSeverity.Informational, Message="Scanning reads names, sizes and model metadata. It never downloads, moves or loads weights." };
        var search = new TextBox { PlaceholderText="Find a model or connection…", MinWidth=180, HorizontalAlignment=HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search,"Search local models by name, format or folder");
        // Every file is listed. The filter narrows by the job a model does for the agent.
        var roleFilters=new (string Label,AgentRole[] Roles)[]{("All models",[]),("Coding agent",[AgentRole.Implementer,AgentRole.Vision]),("Vision",[AgentRole.Vision]),("Code search",[AgentRole.CodeSearch,AgentRole.Reranker]),("Draft models",[AgentRole.Draft]),("Adapters",[AgentRole.Adapter]),("Vision files",[AgentRole.Projector]),("Not used by the agent",[AgentRole.NotUsed,AgentRole.Unknown])};
        var task = new ComboBox { ItemsSource=roleFilters.Select(r=>r.Label).ToArray(),SelectedIndex=0,MinWidth=150 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(task,"Filter models by agent role");
        var resourceKind=new ComboBox{ItemsSource=new[]{"All resources","Local weights","Connections"},SelectedIndex=0,MinWidth=140};
        Accessible(resourceKind,"Filter resources by kind");
        var sort = new ComboBox { ItemsSource=new[]{"Suggested order","Name","Size · largest first","Size · smallest first"},SelectedIndex=0,MinWidth=150 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sort,"Sort model collection");
        var list = new ListView { SelectionMode=ListViewSelectionMode.Single, HorizontalContentAlignment=HorizontalAlignment.Stretch };
        var resourceTable=ListTable(list,("Model","Title",2.2),("Agent use","RoleLabel",1.6),("Status","StatusLabel",2.2));
        list.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Border BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='0,0,0,1'><Grid MinHeight='64' Padding='12,10' ColumnSpacing='12'><Grid.ColumnDefinitions><ColumnDefinition Width='2.3*'/><ColumnDefinition Width='1.2*'/><ColumnDefinition Width='2.3*'/></Grid.ColumnDefinitions>"
            +"<Grid ColumnSpacing='12'><Grid.ColumnDefinitions><ColumnDefinition Width='32'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><Border Width='32' Height='32' CornerRadius='8' Background='{Binding RoleFill}' VerticalAlignment='Center'><SymbolIcon Symbol='{Binding Icon}' Foreground='{Binding RoleInk}'/></Border><StackPanel Grid.Column='1' VerticalAlignment='Center' Spacing='2'><TextBlock Text='{Binding Title}' FontSize='14' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Subtitle}' FontSize='11' Foreground='{ThemeResource TextFillColorSecondaryBrush}' TextTrimming='CharacterEllipsis'/></StackPanel></Grid>"
            +"<Border Grid.Column='1' CornerRadius='10' Padding='9,3,9,4' Background='{Binding RoleFill}' HorizontalAlignment='Left' VerticalAlignment='Center'><TextBlock Text='{Binding RoleLabel}' FontSize='11' FontWeight='SemiBold' Foreground='{Binding RoleInk}' TextTrimming='CharacterEllipsis'/></Border>"
            +"<Grid Grid.Column='2' ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><StackPanel VerticalAlignment='Center' Spacing='4'><Border CornerRadius='10' Padding='9,3,9,4' Background='{Binding StatusFill}' HorizontalAlignment='Left'><TextBlock Text='{Binding StatusLabel}' FontSize='11' FontWeight='SemiBold' Foreground='{Binding StatusInk}'/></Border><TextBlock Text='{Binding StatusDetail}' FontSize='11' Foreground='{ThemeResource TextFillColorSecondaryBrush}' TextWrapping='Wrap' MaxLines='2' TextTrimming='CharacterEllipsis'/></StackPanel><StackPanel Grid.Column='1' Orientation='Horizontal' Spacing='6' VerticalAlignment='Center'><Button x:Name='RowChat' Content='Code' AutomationProperties.Name='Use for coding' ToolTipService.ToolTip='Open Code with this model selected' Visibility='{Binding ChatVisibility}' Padding='10,5'/><Button x:Name='RowTalk' Content='Chat' AutomationProperties.Name='Open in chat' ToolTipService.ToolTip='Start a chat with this model' Visibility='{Binding ChatVisibility}' Padding='10,5'/></StackPanel></Grid></Grid></Border></DataTemplate>");
        // The model's row grows into the Code or Chat page it opens (connected animation, Fluent.Prepare/Connect).
        void RowChatClick(object sender, RoutedEventArgs args) { if (aiOperation == null && ((Button)sender).DataContext is LibraryEntry { ChatProfileId: {} id }) { if (((Button)sender).Tag is UIElement row) Fluent.Prepare("workspace-card", row); openCodeWithModel?.Invoke(id); } }
        void RowTalkClick(object sender, RoutedEventArgs args) { if (aiOperation == null && ((Button)sender).DataContext is LibraryEntry { ChatProfileId: {} id }) { if (((Button)sender).Tag is UIElement row) Fluent.Prepare("workspace-card", row); openNativeChat?.Invoke(id); } }
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not FrameworkElement element) return;
            if (element.FindName("RowChat") is Button code) { code.Click -= RowChatClick; code.Click += RowChatClick; code.Tag = element; }
            if (element.FindName("RowTalk") is Button talk) { talk.Click -= RowTalkClick; talk.Click += RowTalkClick; talk.Tag = element; }
        };
        var modelTitle=Text("Choose a model",23); modelTitle.MaxLines=2;modelTitle.TextTrimming=TextTrimming.CharacterEllipsis; var modelFacts=Text("Your local collection will appear here after scanning.",14); var modelPath=new TextBox { Header="Stored on this computer",IsReadOnly=true,TextWrapping=TextWrapping.Wrap };
        var device=new ComboBox { Header="Compute",ItemsSource=new[]{"GPU · CUDA","CPU"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch };
        var contextChoice=new NumberBox {Header="Loaded context tokens",Minimum=64,Maximum=int.MaxValue,Value=2048,SmallChange=1024,Width=140,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
        var fitTitle=Text("",15);fitTitle.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
        var fitDetail=Text("",12);var fitBasis=Text("",12);var recommendation=Text("",12);
        int Context()=>double.IsFinite(contextChoice.Value)&&contextChoice.Value==Math.Truncate(contextChoice.Value)&&contextChoice.Value>=64?(int)contextChoice.Value:throw new ArgumentException("Enter a whole context window of at least 64 tokens.");
        Button? applyRecommendation=null,connectionEdit=null;FrameworkElement? fileOptions=null;
        var fitPanel=Stack(fitTitle,fitDetail,recommendation);
        int startupStage=0;LocalModel? startupModel=null;var startupStages=StageRail("Inspect","Load","Check","Ready");var startupDetail=Text("",12);var startupElapsed=Text("",11);var startupProgress=BrandControl(new ProgressBar{Height=4,Visibility=Visibility.Collapsed});var startupClock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};DateTimeOffset? startupAt=null;
        var startupCancel=Button("Cancel startup",(_,_)=>aiOperation?.Cancel());startupCancel.IsEnabled=false;
        Guid? startupChatProfile = null;
        var startupChat = Button("Use for coding", (_, _) => { if (startupChatProfile is {} id && aiOperation == null) openCodeWithModel?.Invoke(id); }, true); startupChat.Visibility = Visibility.Collapsed;
        startupDetail.MaxLines=2;startupDetail.TextTrimming=TextTrimming.CharacterEllipsis;startupDetail.RegisterPropertyChangedCallback(TextBlock.TextProperty,(_,_)=>ToolTipService.SetToolTip(startupDetail,startupDetail.Text));
        var startupBody=Stack(startupStages.View,startupDetail,startupProgress,Toolbar(startupElapsed,startupCancel,startupChat,Button("Open running models",(_,_)=>selectModelView?.Invoke(1))));startupBody.Spacing=5;
        var startupHud=SurfaceCard(startupBody);startupHud.Padding=new Thickness(8);startupHud.Visibility=Visibility.Collapsed;
        startupClock.Tick+=(_,_)=>{if(startupAt is {} at)startupElapsed.Text="Elapsed · "+(DateTimeOffset.UtcNow-at).ToString(@"mm\:ss");};Closed+=(_,_)=>startupClock.Stop();
        var launchDetail=Text("Models run only when you choose Start. Existing external servers are never stopped or adopted.",12);
        var selectedStatus=Text("",12);
        var selectedPurpose=Text("",15);
        var exactName=new TextBox { Header="Model name on disk",IsReadOnly=true,TextWrapping=TextWrapping.Wrap };
        var verdictTitle=Text("",15);verdictTitle.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;var verdictDetail=Text("",12);
        ModelVerdict? shownVerdict=null;LocalModel? shownModel=null;
        var verdictAction=Button("",(_,_)=>
        {
            if(shownVerdict==null||shownModel==null)return;
            if(shownVerdict.Readiness is ModelReadiness.NeedsRuntime or ModelReadiness.NeedsRuntimeUpdate){ShowView(2);selectRuntime?.Invoke(ModelAdapters.UsesPython(shownModel)&&shownModel.Format!="GGUF"?(ModelAdapters.IsQuantized(shownModel)?2:1):0);}
            else if(shownVerdict.Readiness==ModelReadiness.UseGgufBuild)findOnHub?.Invoke(ModelLibraryCard.FriendlyName(shownModel.Name).Replace(" · ","-").Replace(" Instruct","-Instruct")+" GGUF");
        });
        var verdictPanel=Stack(verdictTitle,verdictDetail,verdictAction);
        var draftChoice=new ComboBox{Header="Draft model · faster agent turns",HorizontalAlignment=HorizontalAlignment.Stretch};
        var adapterChoice=new ComboBox{Header="Adapter · fine-tuned behaviour",HorizontalAlignment=HorizontalAlignment.Stretch};
        LocalModel?[] draftOptions=[],adapterOptions=[];
        Accessible(draftChoice,"Draft model for speculative decoding");Accessible(adapterChoice,"LoRA adapter to load on this model");
        var collectionStatus=Text("",12);
        var noMatchesTitle=Text("No models match",21);var noMatchesHelp=Text("Try a different name or capability, or clear your filters.",14);
        var clearFilters=Button("Clear filters",(_,_)=>{search.Text="";task.SelectedIndex=0;});
        var noMatches=Stack(noMatchesTitle,noMatchesHelp,clearFilters);
        var selectionHint=Stack(Text("Find the right model for the job.",20),Text("Choose a local language model for planning, code review and project work.",15),Text("1   Choose a model\n2   Start and check its capability\n3   Open its workflow with the connection selected",14));
        var servingEmpty=Stack(Text("Nothing running here yet",21),Text("Choose a model from your collection, then start it. Mason Agent checks its capability and saves a connection for Code and Chat.",15));
        FrameworkElement? servingControls=null;
        var scanProgress=BrandControl(new ProgressBar { IsIndeterminate=true,Visibility=Visibility.Collapsed,Height=3,HorizontalAlignment=HorizontalAlignment.Stretch });
        Button? cancelScan=null;
        // While the first scan of an empty collection runs, the table shows the shape of rows instead of the empty state.
        var scanSkeleton=SurfaceCard(Stack(Enumerable.Range(0,5).Select(i=>(UIElement)Fluent.Skeleton(i%2==0?.34:.42,.8)).ToArray()));
        ((StackPanel)scanSkeleton.Child).Spacing=22;scanSkeleton.Padding=new Thickness(18,16,18,16);scanSkeleton.VerticalAlignment=VerticalAlignment.Top;scanSkeleton.Visibility=Visibility.Collapsed;
        DateTimeOffset? lastScan=null;
        var llama=new TextBox { Header="llama-server.exe · for GGUF chat",Text=settings.LlamaServer,IsReadOnly=true };
        var python=new TextBox { Header="Python with PyTorch + Transformers",Text=settings.Python,IsReadOnly=true };
        var quantizedPython=new TextBox {Header="Quantized text runtime",Text=settings.QuantizedPython,IsReadOnly=true};
        Expander? quantizedSetup=null;
        var quantizedStatus=Text("Set up once, then start GPTQ and AWQ models from your library.",13);
        var selectedRuntime=Text("",12);
        var running=new ListView { SelectionMode=ListViewSelectionMode.Single,MaxHeight=220 };
        var runningCard=new Expander{Header="Running now",IsExpanded=true,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Visibility=Visibility.Collapsed};
        running.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        running.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Padding='16' Margin='0,4' CornerRadius='8' BorderThickness='1' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'><StackPanel Spacing='6'><TextBlock Text='{Binding Model.Name}' FontSize='17' FontWeight='SemiBold' TextWrapping='Wrap'/><TextBlock Text='{Binding Model.Role}'/><TextBlock Text='{Binding Profile.Endpoint}' FontSize='13' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/></StackPanel></Border></DataTemplate>");
        var serverSummary=Text("Choose a running server to inspect or stop it.",14);
        var serverDetail=new ContentControl { HorizontalContentAlignment=HorizontalAlignment.Stretch };
        var locations=new ListView { MaxHeight=340,SelectionMode=ListViewSelectionMode.Single };
        var common=new ToggleSwitch { Header="Include common model folders", IsOn=settings.IncludeCommon };
        var embedGguf=new ToggleSwitch { Header="Run GGUF models inside Mason", OnContent="No separate server process", OffContent="Each model in its own process", IsOn=settings.EmbedGguf };
        ToolTipService.SetToolTip(embedGguf,"One GGUF model at a time runs inside Mason; others start as owned processes. A native llama.cpp crash closes Mason, and an embedded model stops when Mason closes. Turn this off to run every model in its own process.");
        var folders=Text("",12);
        var emptyAdd=Button("Add local folder",(_,_)=>openModelLocations?.Invoke());noMatches.Children.Add(Button("Find models",(_,_)=>ShowView(3),true));noMatches.Children.Add(emptyAdd);
        var noMatchesMortar = EmptyState(noMatches, MoodChecking, "nothing here yet");
        var scanNotes=new TextBox { IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=200 };
        var runtimePaths=Text("",12);
        Border Card(UIElement child)
        {
            var card=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Padding='14' CornerRadius='8' BorderThickness='1' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'/>");
            card.Child=child;card.VerticalAlignment=VerticalAlignment.Top;return card;
        }
        void Save(int editedRuntime=-1)
        {
            if(!readable) throw new InvalidOperationException("Repair model-library.json before changing settings; the unreadable file is preserved.");
            var saved=ModelLibrarySettings.ReadSaved(Path.Combine(Store.Root,"model-library.json"))??settings;
            settings=(settings with { LlamaServer=llama.Text,Python=python.Text,IncludeCommon=common.IsOn,QuantizedPython=quantizedPython.Text,EmbedGguf=embedGguf.IsOn }).MergeRuntimeSelection(saved,editedRuntime);
            Store.Write("model-library.json",settings);
            llama.Text=settings.LlamaServer;python.Text=settings.Python;quantizedPython.Text=settings.QuantizedPython;
        }
        void ReloadRuntimePaths()
        {
            if(!readable)return;
            var selected = ModelLibrarySettings.ReadSaved(Path.Combine(Store.Root,"model-library.json")) ?? DiscoveredSettings();
            settings = settings.MergeRuntimeSelection(NormalizeRuntimePaths(selected));
            llama.Text=settings.LlamaServer;python.Text=settings.Python;quantizedPython.Text=settings.QuantizedPython;
            refreshRuntimeInventory?.Invoke();
        }
        async Task RefreshRuntimeFacts(bool render=false)
        {
            try
            {
                llamaBuild=await LlamaRuntime.Inspect(llama.Text,CancellationToken.None);
                runtimeFacts=new(llamaBuild!=null,llamaBuild?.Build,llamaBuild is {Architectures.Count:>0}?llamaBuild.Architectures:null,File.Exists(python.Text),File.Exists(quantizedPython.Text),ModelAdapters.TextFamilies);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){runtimeFacts=runtimeFacts with{LlamaServer=File.Exists(llama.Text)};}
            runtimeFactsChanged?.Invoke();
            if(render){Render();Selection();}
        }
        void ShowLocations()
        {
            runtimePaths.Text=$"GGUF runtime: {llama.Text}\nPython inference runtime: {python.Text}";
            quantizedStatus.Text=File.Exists(quantizedPython.Text)?"Runtime selected · compatibility is checked when you start a quantized model.":"Set up once, then start GPTQ and AWQ models from your library.";
            refreshRuntimeInventory?.Invoke();
            locations.ItemsSource=settings.Locations.Select(x=>{var state=Directory.Exists(x.Path)?"Included · folder found":"Included · folder unavailable";var item=new ListViewItem{Tag=x,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=Stack(Text(x.Name+" · Custom folder",14),Text(x.Path,12),Text(state,12))};Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item,x.Name+" · "+x.Path+" · "+state);return item;}).ToArray();
            folders.Text=string.Join("\n",ModelDiscovery.CommonLocations().Select(x=>$"{x.Name} · {(Directory.Exists(x.Path)?"Found":"Not found")}\n{x.Path}"));
        }
        void Render()
        {
            var path=(list.SelectedItem as LibraryEntry)?.Key; string q=search.Text.Trim();
            var roles=roleFilters[Math.Max(0,task.SelectedIndex)].Roles;
            var found=discovered.Where(x=>(roles.Length==0 || roles.Contains(x.Role)) && (q.Length==0 || (x.Name+" "+x.Source+" "+x.Path+" "+x.Format+" "+x.Architecture+" "+ModelLibraryCard.FriendlyName(x.Name)).Contains(q,StringComparison.OrdinalIgnoreCase)));
            int Order(LocalModel x)=>x.Role switch{AgentRole.Implementer or AgentRole.Vision=>0,AgentRole.CodeSearch or AgentRole.Reranker=>1,AgentRole.Draft or AgentRole.Adapter=>2,AgentRole.Projector=>3,_=>4};
            found=sort.SelectedIndex switch { 1=>found.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase),2=>found.OrderByDescending(x=>x.Bytes),3=>found.OrderBy(x=>x.Bytes),_=>found.OrderBy(Order).ThenByDescending(x=>ModelCompatibility.Assess(x,runtimeFacts).Readiness==ModelReadiness.Ready).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase) };
            LibraryEntry.Dark=root.ActualTheme==ElementTheme.Dark;
            var cards=found.Select(x=>Status(new ModelLibraryCard(x,State(x)){Verdict=VerdictText(x)})).ToArray();
            foreach(var card in cards)card.Fit=CardFit(card.Model);
            var endpoints=Store.Read<List<AiEndpointProfile>>("ai-connections.json")??[];
            AiCapability[] wanted=roles.Length==0?[]:roles.Select(r=>r switch{AgentRole.Implementer or AgentRole.Vision=>AiCapability.Chat,AgentRole.CodeSearch=>AiCapability.Embeddings,AgentRole.Reranker=>AiCapability.Reranking,_=>AiCapability.Models}).Distinct().ToArray();
            var entries=cards.Select(c=>new LibraryEntry(c,null)).Concat(endpoints.Where(p=>(q.Length==0||(p.Name+" "+p.Model+" "+p.Endpoint).Contains(q,StringComparison.OrdinalIgnoreCase))&&(roles.Length==0||p.Checks.Any(c=>c.State=="Passed"&&wanted.Contains(c.Capability)&&c.Capability!=AiCapability.Models))).Select(p=>new LibraryEntry(null,p))).Where(e=>resourceKind.SelectedIndex switch{1=>e.File!=null,2=>e.Connection!=null,_=>true}).ToArray();
            foreach (var entry in entries) entry.ChatProfileId = entry.Connection is {} connection && connection.Checks.Any(c => c.Capability == AiCapability.Chat && c.State == "Passed") ? connection.Id : modelServing.Running.FirstOrDefault(s => s.Owner.IsRunning && s.Model.Task == "Chat" && s.Model.Path == entry.File?.Model.Path && s.Model.AdapterPath == entry.File?.Model.AdapterPath && s.Profile.Checks.Any(c => c.Capability == AiCapability.Chat && c.State == "Passed"))?.Profile.Id;
            list.ItemsSource=entries; list.SelectedItem=entries.FirstOrDefault(x=>x.Key==path)??entries.FirstOrDefault();
            var liveCount=modelServing.Running.Count(x=>x.Owner.IsRunning);
            summary.Text=$"{discovered.Length} local models and files · {discovered.Count(x=>x.Role is AgentRole.Implementer or AgentRole.Vision)} for coding · {discovered.Count(x=>x.Role is AgentRole.CodeSearch or AgentRole.Reranker)} for code search · {liveCount} running";
            runningCard.Visibility=liveCount>0?Visibility.Visible:Visibility.Collapsed;runningCard.Header=$"Running now · {liveCount}";
            updateModelsShell?.Invoke();
            collectionStatus.Text=$"{cards.Length} local files · {entries.Length-cards.Length} saved connections shown"+(lastScan.HasValue?$" · Scanned {lastScan.Value.ToLocalTime():t}":"");
            noMatches.Visibility=entries.Length==0?Visibility.Visible:Visibility.Collapsed;resourceTable.Visibility=entries.Length==0?Visibility.Collapsed:Visibility.Visible;list.Visibility=entries.Length==0?Visibility.Collapsed:Visibility.Visible;
            noMatchesTitle.Text=discovered.Length==0?"Your collection starts here":"No models match";
            // An empty collection and an empty filter result are different moments.
            clearFilters.Visibility=discovered.Length==0?Visibility.Collapsed:Visibility.Visible;
            noMatchesMortar.Source=MascotSource(discovered.Length==0?MoodChecking:MoodUnsure,96);
            noMatchesHelp.Text=discovered.Length==0?"Scan common folders, or add the folder where your model weights live. Scan warnings appear above.":"Try a different name or capability, or clear your filters.";
            clearFilters.Visibility=discovered.Length==0?Visibility.Collapsed:Visibility.Visible;emptyAdd.Visibility=discovered.Length==0?Visibility.Visible:Visibility.Collapsed;
            servingEmpty.Visibility=modelServing.Running.Count==0?Visibility.Visible:Visibility.Collapsed;
            if(servingControls!=null)servingControls.Visibility=modelServing.Running.Count>0?Visibility.Visible:Visibility.Collapsed;
            var runningId=(running.SelectedItem as ServedModel)?.Id;
            running.ItemsSource=modelServing.Running.ToArray();running.SelectedItem=modelServing.Running.FirstOrDefault(x=>x.Id==runningId);
        }
        string State(LocalModel model)
        {
            var server=modelServing.Running.FirstOrDefault(x=>x.Model.Path.Equals(model.Path,StringComparison.OrdinalIgnoreCase)&&x.Model.AdapterPath==model.AdapterPath&&x.Owner.IsRunning);
            if(server!=null)return server.Profile.Checks.Any(x=>x.Capability.ToString()==server.Model.Task&&x.State=="Passed")?"Running · capability verified":"Running · check needs attention";
            if(startupModel?.Path==model.Path&&startupModel.AdapterPath==model.AdapterPath)return "Starting · "+new[]{"Inspect","Load","Check","Ready"}[Math.Clamp(startupStage,0,3)]+" · not yet verified";
            return model.Role switch{AgentRole.Draft=>"Attach to a larger model",AgentRole.Adapter=>"Attach to its base model",AgentRole.Projector=>"Companion file",AgentRole.NotUsed or AgentRole.Unknown=>"Not used by the agent",_=>model.Launchable?"Stopped":"Can't start here"};
        }
        ModelLibraryCard Status(ModelLibraryCard card)
        {
            var m=card.Model;var verdict=ModelCompatibility.Assess(m,runtimeFacts);
            var server=modelServing.Running.FirstOrDefault(x=>x.Model.Path.Equals(m.Path,StringComparison.OrdinalIgnoreCase)&&x.Owner.IsRunning);
            if(server!=null){card.StatusLabel="Running";card.StatusTone=Tone.Accent;card.StatusDetail="Serving the agent at "+server.Profile.Endpoint;return card;}
            if(startupModel?.Path==m.Path){card.StatusLabel="Starting";card.StatusTone=Tone.Accent;card.StatusDetail=card.State;return card;}
            card.StatusTone=ModelTones.For(verdict.Readiness,verdict.Role);
            card.StatusLabel=verdict.Readiness==ModelReadiness.Ready?m.Role switch{AgentRole.Draft=>"Attach to a model",AgentRole.Adapter=>"Attach to its base",AgentRole.Projector=>"Companion file",_=>"Ready to start"}:verdict.Badge;
            card.StatusDetail=verdict.Reason;
            return card;
        }
        string VerdictText(LocalModel model)
        {
            var verdict=ModelCompatibility.Assess(model,runtimeFacts);
            return verdict.Role is AgentRole.NotUsed or AgentRole.Unknown ? verdict.Reason : verdict.Badge+" · "+verdict.Reason;
        }
        string CardFit(LocalModel model)
        {
            if(!model.Launchable || modelServing.Running.Any(x=>x.Model.Path==model.Path&&x.Owner.IsRunning))return "";
            return ModelMemory.Assess(memoryProfiles.GetValueOrDefault(model.Path),gpuSample,2048).Summary+(model.Task=="Chat"?" · up to 2K context":"");
        }
        void UpdateFit()
        {
            if(list.ItemsSource is IEnumerable<LibraryEntry> entries)foreach(var card in entries.Select(e=>e.File).OfType<ModelLibraryCard>())card.Fit=CardFit(card.Model);
            var model=(list.SelectedItem as LibraryEntry)?.File?.Model;
            fitPanel.Visibility=model?.Launchable==true?Visibility.Visible:Visibility.Collapsed;
            contextChoice.Visibility=model?.Launchable==true?Visibility.Visible:Visibility.Collapsed;
            if(model==null || !model.Launchable)return;
            var profile=memoryProfiles.GetValueOrDefault(model.Path);
            bool live=modelServing.Running.Any(x=>x.Model.Path==model.Path&&x.Owner.IsRunning);
            if(model.Launchable&&(!double.IsFinite(contextChoice.Value)||contextChoice.Value<64||contextChoice.Value!=Math.Truncate(contextChoice.Value))){fitTitle.Text="Enter a valid context window";fitDetail.Text="Use a whole number of at least 64 tokens.";return;}
            var fit=ModelMemory.Assess(profile,gpuSample,model.Task=="Chat"?Context():512);
            fitTitle.Text=live?"Running · memory is already in use":device.SelectedIndex==1?"CPU selected · no GPU allocation":fit.State;
            fitDetail.Text=live?"The free VRAM reading already includes this server. Stop it before estimating a new load.":device.SelectedIndex==1?"CPU serving uses system RAM. Available system RAM is not checked here.":fit.Detail;
            var suggested=ModelMemory.Recommend(profile,gpuSample);
            recommendation.Text=live?"":suggested.Text;
            if(model.Task!="Chat"){fitDetail.Text=fitDetail.Text.Replace("context tokens","input tokens (planning cap)");recommendation.Text=recommendation.Text.Replace("context tokens","input tokens (planning cap)");}
            fitBasis.Text=(profile==null||!profile.ContextDeclared?"Model capacity unavailable. ":$"Model capacity: {profile.MaxContext:N0} tokens. ")+(profile?.Unknown??profile?.Basis??"Metadata unavailable")+". Based on metadata from the last scan and current driver readings. Estimates include runtime headroom, not model quality or speed. Other apps can change free memory before Start. GGUF prompt batch 256 / microbatch 128; one request at a time. Context includes prompt, reasoning and output. Restart the model to apply a different loaded window.";
            if(applyRecommendation!=null){applyRecommendation.IsEnabled=!live&&aiOperation==null;applyRecommendation.Visibility=live?Visibility.Collapsed:Visibility.Visible;}
        }
        void ProfilesChanged() { refreshAiConnectionList?.Invoke(); refreshCodeProfiles?.Invoke(); }
        void ResetStoppedProfile(ServedModel server)
        {
            var saved=Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [];
            if(saved.Any(x=>x.Id==server.Profile.Id && x.Endpoint==server.Profile.Endpoint && x.Model==server.Profile.Model)) Store.Write("ai-connections.json",saved.Select(x=>x.Id==server.Profile.Id && x.Endpoint==server.Profile.Endpoint && x.Model==server.Profile.Model ? x with { Checks=[] } : x).ToList());
            ProfilesChanged();
        }
        Action? adaptive=null;
        Button? start=null;
        void Selection()
        {
            var card=(list.SelectedItem as LibraryEntry)?.File;var m=card?.Model;
            modelTitle.Text=card?.Title ?? "Choose a model"; modelFacts.Text=m==null ? "Select a card to see serving options." : $"{card!.Task} · {m.Format} · {m.Size}";modelPath.Text=m?.Path ?? "";exactName.Text=m?.Name??"";
            var connection=(list.SelectedItem as LibraryEntry)?.Connection;
            if(connection!=null){modelTitle.Text=connection.Name;modelFacts.Text=connection.Model+"\n"+connection.Endpoint;modelPath.Text=connection.Endpoint;exactName.Text=connection.Model;}
            if(connectionEdit!=null)connectionEdit.Visibility=connection!=null?Visibility.Visible:Visibility.Collapsed;
            if(fileOptions!=null)fileOptions.Visibility=m!=null?Visibility.Visible:Visibility.Collapsed;
            shownModel=m;shownVerdict=m==null?null:ModelCompatibility.Assess(m,runtimeFacts);
            verdictPanel.Visibility=m==null?Visibility.Collapsed:Visibility.Visible;
            if(shownVerdict!=null&&m!=null)
            {
                verdictTitle.Text=shownVerdict.RoleLabel+" · "+shownVerdict.Badge;
                verdictDetail.Text=shownVerdict.Reason+(shownVerdict.Action.Length>0?"\n"+shownVerdict.Action:"")+(m.Role is not (AgentRole.NotUsed or AgentRole.Unknown)?"\nJobs: "+ModelVerdict.Jobs(m.Role):"")+(m.Architecture.Length>0?"\nArchitecture: "+m.Architecture:"");
                verdictAction.Content=shownVerdict.Readiness switch{ModelReadiness.NeedsRuntimeUpdate=>"Update llama.cpp",ModelReadiness.NeedsRuntime=>"Set up runtime",ModelReadiness.UseGgufBuild=>"Find a GGUF build",_=>""};
                verdictAction.Visibility=verdictAction.Content is string {Length:>0}?Visibility.Visible:Visibility.Collapsed;
            }
            // Drafts must share the target's tokenizer family; adapters must match the runtime that serves the model.
            bool gguf=m?.Format=="GGUF"&&m.Task=="Chat";
            draftOptions=gguf?new LocalModel?[]{null}.Concat(discovered.Where(x=>x.Format=="GGUF"&&x.Path!=m!.Path&&(x.Role==AgentRole.Draft||x.Role is AgentRole.Implementer&&x.Architecture.Length>0&&x.Architecture==m.Architecture&&x.Bytes*4<=m.Bytes)).OrderBy(x=>x.Bytes)).ToArray():[];
            adapterOptions=m?.Task=="Chat"?new LocalModel?[]{null}.Concat(discovered.Where(x=>x.Role==AgentRole.Adapter&&(x.Format=="GGUF")==gguf)).ToArray():[];
            draftChoice.ItemsSource=draftOptions.Select(x=>x==null?"No draft model":ModelLibraryCard.FriendlyName(x.Name)+" · "+x.Size+(x.Role==AgentRole.Draft?" · draft":"")).ToArray();draftChoice.SelectedIndex=draftOptions.Length>0?0:-1;
            adapterChoice.ItemsSource=adapterOptions.Select(x=>x==null?"No adapter":ModelLibraryCard.FriendlyName(x.Name)+" · "+x.Size).ToArray();adapterChoice.SelectedIndex=adapterOptions.Length>0?0:-1;
            draftChoice.Visibility=draftOptions.Length>1?Visibility.Visible:Visibility.Collapsed;adapterChoice.Visibility=adapterOptions.Length>1?Visibility.Visible:Visibility.Collapsed;
            selectedPurpose.Text=connection==null?card?.Purpose??"":string.Join("\n",connection.Checks.GroupBy(c=>c.Capability).Select(g=>g.Last()).Select(c=>c.Capability+" · "+c.State+" · "+c.At.LocalDateTime.ToString("g")));
            if(start!=null) { start.IsEnabled=m?.Launchable==true && aiOperation==null && !modelServing.Running.Any(x=>x.Model.Path==m.Path&&x.Model.AdapterPath==m.AdapterPath&&x.Owner.IsRunning);start.Visibility=m?.Launchable==true?Visibility.Visible:Visibility.Collapsed;start.Content=m!=null&&readable&&!ModelServing.HasRuntime(m,settings)?"Set up runtime":"Start & connect"; }
            device.Visibility=m?.Launchable==true?Visibility.Visible:Visibility.Collapsed;
            if(m?.Task=="Chat"&&memoryProfiles.TryGetValue(m.Path,out var capacity)){contextChoice.Maximum=capacity.ContextDeclared?capacity.MaxContext:int.MaxValue;if(contextChoice.Value>contextChoice.Maximum)contextChoice.Value=contextChoice.Maximum;}
            UpdateFit();adaptive?.Invoke();
            selectedStatus.Text=m==null?"":State(m);
            selectedRuntime.Text=m?.Launchable==true?(ModelAdapters.IsQuantized(m)?"Runs with GPT-QModel · packed 4-bit weights":ModelAdapters.UsesPython(m)?"Runs with Python · PyTorch + Transformers":"Runs with llama.cpp · GGUF")+(File.Exists(ModelAdapters.IsQuantized(m)?quantizedPython.Text:ModelAdapters.UsesPython(m)?python.Text:llama.Text)?" · runtime selected":" · choose a runtime in Runtime setup"):"";
            launchDetail.Text=m==null?"":m.Launchable?m.Support+"\nStart checks the model and saves a connection the agent can use. Models run locally.":m.Support;
        }
        async Task Scan()
        {
            if(scan!=null)return; activity.IsOpen=true; using var ct=new CancellationTokenSource();scan=ct;scanProgress.Visibility=Visibility.Visible;if(cancelScan!=null)cancelScan.Visibility=Visibility.Visible;activity.Severity=InfoBarSeverity.Informational;activity.Message="Discovering model files… You can cancel at any time.";
            if(discovered.Length==0){scanSkeleton.Visibility=Visibility.Visible;noMatches.Visibility=Visibility.Collapsed;}
            try
            {
                var roots=(common.IsOn ? ModelDiscovery.CommonLocations() : []).Concat(settings.Locations).Concat((modelDownloads?.Snapshot()??[]).Where(j=>j.State=="Ready in library").Select(j=>new ModelLocation("Downloaded · "+j.Repository,j.Folder))).DistinctBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).ToArray();
                var result=await Task.Run(()=>ModelDiscovery.Scan(roots,ct.Token));
                result=result with{Models=result.Models.Select(DownloadIdentity).ToArray()};
                var profiles=await Task.Run(()=>result.Models.Where(x=>x.Launchable).ToDictionary(x=>x.Path,x=>ModelMemory.Read(x,ct.Token),StringComparer.OrdinalIgnoreCase),ct.Token);
                memoryProfiles=profiles;discovered=result.Models.Concat(registeredDownloads.Values.SelectMany(m=>m)).DistinctBy(m=>m.Path+"|"+m.AdapterPath,StringComparer.OrdinalIgnoreCase).ToArray();lastScan=result.At;Render();Selection();
                activity.Severity=result.Limited || result.Notes.Length>0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
                activity.Message=$"Found {result.Models.Length} models · inspected {result.FilesSeen:N0} entries · {result.At.ToLocalTime():t}. "+(result.Limited?"Scan limit reached; add narrower folders for a complete inventory. ":"")+(result.Notes.Length>0?$"{result.Notes.Length} access/link notes. ":"")+"No weights loaded.";
                scanNotes.Text=$"Inspected {result.FilesSeen:N0} entries in {roots.Length} configured folders.\n"+string.Join("\n",roots.Select(x=>$"{x.Name}: {x.Path}"))+"\n"+string.Join("\n",result.Notes);
                activity.IsOpen=result.Limited || result.Notes.Length>0 || result.Models.Length==0;
                if(result.Models.Length==0)
                {
                    activity.Severity=result.Notes.Length>0 || result.Limited ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
                    activity.Message=roots.Length==0 ? "No scan locations enabled. Open Library → Model locations to include common folders or add a model folder."
                        : result.Notes.Length>0 ? $"No models discovered · {result.FilesSeen:N0} entries inspected. Some files could not be read. Open Library → Model locations → Scan details for the paths and errors."
                        : result.Limited ? "No models discovered before the scan limit. Add a narrower folder in Runtime setup and scan again."
                        : $"No model weights found · {result.FilesSeen:N0} entries inspected. Check Library → Model locations for found folders, or add the folder containing your weights.";
                }
                if(result.RedirectionBlocked)
                {
                    activity.IsOpen=true; activity.Severity=InfoBarSeverity.Warning;
                    activity.Message="Windows blocked access through local model links. Close Mason Agent and reopen it from the Windows Start menu, then scan again. This can happen when the app is opened by the installer. Details: Library → Model locations → Scan details.";
                }
            }
            catch(OperationCanceledException){activity.Message="Scan cancelled. Previous results retained.";}
            catch(Exception e){activity.Severity=InfoBarSeverity.Error;activity.Message="Discovery needs attention: "+e.Message;}
            finally{scan=null;scanProgress.Visibility=Visibility.Collapsed;if(cancelScan!=null)cancelScan.Visibility=Visibility.Collapsed;if(scanSkeleton.Visibility==Visibility.Visible){scanSkeleton.Visibility=Visibility.Collapsed;Render();}}
        }
        LocalModel DownloadIdentity(LocalModel model)
        {
            var job=(modelDownloads?.Snapshot()??[]).FirstOrDefault(j=>j.State=="Ready in library"&&(model.Path.Equals(j.Folder,StringComparison.OrdinalIgnoreCase)||model.Path.StartsWith(j.Folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)));
            return job==null?model:model with{Name=job.Name+(job.Format=="GGUF"?" · "+Path.GetFileNameWithoutExtension(model.Path):""),Source=job.Repository};
        }
        var runtimeStatus=Text("Choose existing runtimes here. GPTQ/AWQ has a separate optional setup.",12);
        async Task PickRuntime(bool isPython)
        {
            if(aiOperation!=null)return;
            try { var picker=new FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".exe");var file=await picker.PickSingleFileAsync();if(file==null)return;
                if(isPython)python.Text=file.Path;else llama.Text=file.Path;Save(isPython?1:0);ShowLocations();Selection();runtimeStatus.Text="Runtime path saved. Models remain stopped until you start one.";
            }catch(Exception e){await Notice("Runtime",e.Message);}
        }
        var runtimeSettings=new Expander { Header="Runtime setup",HorizontalAlignment=HorizontalAlignment.Stretch,
            Content=Stack(runtimeStatus,llama,Button("Choose llama-server.exe",async(_,_)=>await PickRuntime(false)),embedGguf,python,Button("Choose Python runtime",async(_,_)=>await PickRuntime(true)),Button("Quantized model setup",(_,_)=>{ShowView(2);if(quantizedSetup!=null){quantizedSetup.IsExpanded=true;selectRuntime?.Invoke(2);}})) };
        start=Button("Start & connect",async(_,_)=>
        {
            if((list.SelectedItem as LibraryEntry)?.File is not ModelLibraryCard card || aiOperation!=null)return;var m=card.Model;
            if(m.Format=="GGUF"&&runtimeFacts.Loads(m.Architecture)==false){activity.IsOpen=true;activity.Severity=InfoBarSeverity.Warning;activity.Message=$"llama.cpp {runtimeFacts.BuildLabel} can't load the “{m.Architecture}” architecture. Update llama.cpp in Runtimes, then start again.";ShowView(2);selectRuntime?.Invoke(0);return;}
            try{ReloadRuntimePaths();}catch(Exception error){await Notice("Model settings","Could not reload saved runtime paths: "+error.Message);return;}
            if(!readable){await Notice("Model settings","Repair the unreadable model settings before starting a server.");return;}
            if (!ModelServing.HasRuntime(m, settings))
            {
                bool quantized = ModelAdapters.IsQuantized(m);
                ShowView(2);
                selectRuntime?.Invoke(quantized ? 2 : ModelAdapters.UsesPython(m) ? 1 : 0);
                if (quantized) quantizedSetup!.IsExpanded = true;
                activity.Severity = InfoBarSeverity.Informational;
                activity.Message = "Choose or prepare a runtime for " + card.Title + ", then return to Library to start it.";
                activity.IsOpen = true;
                return;
            }
            else if(!File.Exists(ModelAdapters.UsesPython(m) ? python.Text : llama.Text)){runtimeSettings.IsExpanded=true;await PickRuntime(ModelAdapters.UsesPython(m));Selection();return;}
            using var ct=new CancellationTokenSource();activity.IsOpen=false;aiOperation=ct;start!.IsEnabled=false;scanProgress.Visibility=Visibility.Visible;if(cancelScan!=null)cancelScan.Visibility=Visibility.Visible;activity.Severity=InfoBarSeverity.Informational;activity.Message="Starting "+card.Title+"… Checking the server before saving its connection.";
            startupCancel.IsEnabled=true;startupAt=DateTimeOffset.UtcNow;startupElapsed.Text="Elapsed · 00:00";startupClock.Start();startupStage=0;foreach(var (label,index) in new[]{"Inspect","Load","Check","Ready"}.Select((label,index)=>(label,index)))startupStages.Label(index,label);startupStages.Set(0);startupHud.Visibility=Visibility.Visible;startupDetail.Text="Inspecting · "+card.Title;startupProgress.Visibility=Visibility.Visible;startupProgress.IsIndeterminate=Fluent.MotionOn;
            startupChatProfile = null; startupChat.Visibility = Visibility.Collapsed;
            device.IsEnabled=false;contextChoice.IsEnabled=false;if(applyRecommendation!=null)applyRecommendation.IsEnabled=false;startupModel=m;Render();Selection();
            try
            {
                // Starting a model is a read of runtime selection, never a settings write.
                var launchSettings = settings;
                var draft=draftChoice.SelectedIndex>0?draftOptions[draftChoice.SelectedIndex]:null;var adapter=adapterChoice.SelectedIndex>0?adapterOptions[adapterChoice.SelectedIndex]:null;
                var launchOptions = new ModelLaunchOptions(Context()){DraftPath=draft?.Path,LoraPath=adapter?.Format=="GGUF"?adapter.Path:null};
                if(adapter!=null&&adapter.Format!="GGUF")m=m with{AdapterPath=adapter.Path};
                Task<ServedModel> StartOnDevice(bool gpu) => modelServing.Start(
                    m, launchSettings, gpu, Path.Combine(AppContext.BaseDirectory, "model-runtime"), ct.Token,
                    message => { startupDetail.Text = message; activity.Message = card.Title + " · " + message; },
                    launchOptions, phase => { startupStage = (int)phase; startupStages.Set(phase == ModelStartupPhase.Ready ? 4 : startupStage); startupDetail.Text = phase + " · " + card.Title; Render(); Selection(); });

                ServedModel served;
                try { served = await StartOnDevice(device.SelectedIndex == 0); }
                catch (ModelCudaUnavailableException error)
                {
                    ct.Token.ThrowIfCancellationRequested();
                    if (!await OfferModelCpuRetry(error.Message)) throw;
                    ct.Token.ThrowIfCancellationRequested();
                    device.SelectedIndex = 1;
                    served = await StartOnDevice(gpu: false);
                }
                try
                {
                    var saved=Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [];
                    Store.Write("ai-connections.json",saved.Where(x=>x.Id!=served.Profile.Id).Append(served.Profile).ToList());
                }
                catch { modelServing.Stop(served);throw; }
                aiSessionKeys[served.Profile.Id]="";ProfilesChanged();Render();
                var probe=served.Profile.Checks.Last();activity.Severity=probe.State=="Passed"?InfoBarSeverity.Success:InfoBarSeverity.Warning;
                activity.Message=probe.State!="Passed"?$"Server started, but {m.Task.ToLowerInvariant()} needs attention: {probe.Detail}":m.Task switch{"Embeddings"=>"Code search ready · the agent can now search this project by meaning. Assign it in Code → Model roles if you run more than one.","Reranking"=>"Reranker ready · the agent's code search results are now reordered by relevance.",_=>$"Ready for the agent · {served.Profile.Endpoint}. Choose it in Code or Chat."};
                startupDetail.Text=activity.Message;selectedStatus.Text="Connection: "+served.Profile.Name;running.SelectedItem=served;
                if (m.Task == "Chat" && probe.State == "Passed") { startupChatProfile = served.Profile.Id; startupChat.Visibility = Visibility.Visible; startupDetail.Text = "Chat ready · " + card.Title + ". Use for coding to start a conversation."; }
            }
            catch(Exception e){startupStages.Set(startupStage);startupStages.Label(Math.Min(3,startupStage),(new[]{"Inspect","Load","Check","Ready"})[Math.Min(3,startupStage)]+(e is OperationCanceledException?" stopped":" failed"));activity.IsOpen=true;activity.Severity=e is OperationCanceledException?InfoBarSeverity.Warning:InfoBarSeverity.Error;activity.Message=e is OperationCanceledException?"Startup cancelled; the owned process tree was stopped.":"Could not start model: "+e.Message;startupDetail.Text=activity.Message;}
            finally{startupModel=null;startupCancel.IsEnabled=false;startupClock.Stop();startupProgress.Visibility=Visibility.Collapsed;aiOperation=null;device.IsEnabled=true;contextChoice.IsEnabled=true;scanProgress.Visibility=Visibility.Collapsed;if(cancelScan!=null)cancelScan.Visibility=Visibility.Collapsed;Render();Selection();}
        },true);
        var library=new Grid { ColumnSpacing=12,RowSpacing=12 };
        library.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});library.RowDefinitions.Add(new(){Height=new GridLength(0)});
        library.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});library.ColumnDefinitions.Add(new(){Width=new GridLength(370)});
        var searchRow=new Grid {ColumnSpacing=8};searchRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});searchRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});searchRow.Children.Add(search);Grid.SetColumn(task,1);searchRow.Children.Add(task);searchRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});Grid.SetColumn(resourceKind,2);searchRow.Children.Add(resourceKind);searchRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var sortDrawer=DrawerButton("Sort",sort);Grid.SetColumn(sortDrawer,3);searchRow.Children.Add(sortDrawer);
        var browseBody=new Grid();browseBody.Children.Add(resourceTable);browseBody.Children.Add(noMatches);browseBody.Children.Add(scanSkeleton);
        var browse=new Grid{RowSpacing=10};browse.RowDefinitions.Add(new(){Height=GridLength.Auto});browse.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});browse.RowDefinitions.Add(new(){Height=GridLength.Auto});browse.Children.Add(Stack(runningCard,searchRow,collectionStatus));Grid.SetRow(browseBody,1);browse.Children.Add(browseBody);Grid.SetRow(startupHud,2);browse.Children.Add(startupHud);
        var backToLibrary=Button("Back to library",(_,_)=>list.SelectedIndex=-1);
        backToLibrary.Content=Row(MotifIcon(Fluent.Motif.Back,14),new TextBlock{Text="Back to library",VerticalAlignment=VerticalAlignment.Center});Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(backToLibrary,"Back to library");
        device.Width=180;
        applyRecommendation=Button("Use recommended settings",(_,_)=>
        {
            var model=(list.SelectedItem as LibraryEntry)?.File?.Model;if(model==null || aiOperation!=null)return;
            var suggested=ModelMemory.Recommend(memoryProfiles.GetValueOrDefault(model.Path),gpuSample);
            device.SelectedIndex=suggested.Gpu?0:1;contextChoice.Value=suggested.Context;UpdateFit();
        });
        fitPanel.Children.Add(applyRecommendation);
        fitPanel.Children.Add(new Expander {Header="How this estimate works",Content=fitBasis,HorizontalAlignment=HorizontalAlignment.Stretch});
        device.SelectionChanged+=(_,_)=>UpdateFit();contextChoice.ValueChanged+=(_,_)=>UpdateFit();
        connectionEdit=Button("Edit saved connection",(_,_)=>{if((list.SelectedItem as LibraryEntry)?.Connection is {} profile){selectManagedAiConnection?.Invoke(profile.Id);Navigate("AI connections");}});
        contextChoice.Width=double.NaN;contextChoice.HorizontalAlignment=HorizontalAlignment.Stretch;
        fileOptions=Stack(FormGrid(300,device,contextChoice),draftChoice,adapterChoice,start,fitPanel,selectedRuntime,selectedStatus,launchDetail,new Expander { Header="Model files & compatibility",Content=Stack(exactName,modelPath),HorizontalAlignment=HorizontalAlignment.Stretch },runtimeSettings);
        var detailsChat = Button("Use for coding", (_, _) => { if (aiOperation == null && (list.SelectedItem as LibraryEntry)?.ChatProfileId is {} id) openCodeWithModel?.Invoke(id); }, true);
        detailsChat.Visibility = Visibility.Collapsed;
        list.SelectionChanged += (_, _) => detailsChat.Visibility = (list.SelectedItem as LibraryEntry)?.ChatProfileId != null ? Visibility.Visible : Visibility.Collapsed;
        // Chat-capable models can open either workspace with the connection already selected.
        var detailsTalk = Button("Open in chat", (_, _) => { if (aiOperation == null && (list.SelectedItem as LibraryEntry)?.ChatProfileId is {} id) openNativeChat?.Invoke(id); });
        var detailsOpen = Row(detailsChat, detailsTalk);
        detailsOpen.Visibility = Visibility.Collapsed;
        list.SelectionChanged += (_, _) => detailsOpen.Visibility = detailsChat.Visibility;
        var detailContents=Stack(backToLibrary,modelTitle,modelFacts,verdictPanel,detailsOpen,connectionEdit,selectedPurpose,fileOptions);
        var detail=Card(detailContents);
        var hint=Card(selectionHint);
        var detailBody=new Grid();detailBody.Children.Add(hint);detailBody.Children.Add(detail);
        var detailScroll=new ScrollViewer { Content=detailBody,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        library.Children.Add(browse);Grid.SetColumn(detailScroll,1);library.Children.Add(detailScroll);
        void Adaptive()
        {
            bool narrow=library.ActualWidth<900; bool chosen=list.SelectedItem is LibraryEntry;
            library.ColumnDefinitions[1].Width=narrow?new GridLength(0):new GridLength(370);
            Grid.SetColumn(detailScroll,narrow?0:1);
            browse.Visibility=narrow&&chosen?Visibility.Collapsed:Visibility.Visible;
            detailScroll.Visibility=narrow&&!chosen?Visibility.Collapsed:Visibility.Visible;
            backToLibrary.Visibility=narrow?Visibility.Visible:Visibility.Collapsed;
            caption.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
            detail.Visibility=chosen?Visibility.Visible:Visibility.Collapsed;hint.Visibility=chosen?Visibility.Collapsed:Visibility.Visible;
        }
        adaptive=Adaptive;
        library.SizeChanged+=(_,_)=>Adaptive();
        // X2: when a model is chosen, start from the context that fits its memory (Goose sizes context the same way).
        // A value the user typed for this model is kept until they choose another model.
        string? fittedFor=null;
        list.SelectionChanged+=(_,_)=>{
            if(aiOperation!=null||(list.SelectedItem as LibraryEntry)?.File?.Model is not {} chosen||chosen.Task!="Chat"||chosen.Path==fittedFor)return;
            if(memoryProfiles.GetValueOrDefault(chosen.Path) is not {} profile)return;
            var suggested=ModelMemory.Recommend(profile,gpuSample);fittedFor=chosen.Path;
            device.SelectedIndex=suggested.Gpu?0:1;contextChoice.Value=suggested.Context;
        };
        list.SelectionChanged+=(_,_)=>Selection();search.TextChanged+=(_,_)=>Render();task.SelectionChanged+=(_,_)=>Render();sort.SelectionChanged+=(_,_)=>Render();resourceKind.SelectionChanged+=(_,_)=>Render();
        running.SelectionChanged+=(_,_)=> { if(running.SelectedItem is ServedModel s) { serverSummary.Text=$"{s.Model.Task} · {s.Profile.Name}\n{s.Profile.Endpoint} · {(!s.Owner.IsRunning ? "Stopped unexpectedly" : "Running locally")}\n"+string.Join(" · ",s.Profile.Checks.Select(x=>$"{x.Capability}: {x.State}")); serverDetail.Content=DetailList(("Model",s.Model.Name,false),("Endpoint",s.Profile.Endpoint,true),("Model ID",s.Profile.Model,true),("Context",(s.Options?.ContextTokens??4096).ToString("N0")+" tokens requested",false),("Runs",s.Owner.Embedded?"Inside Mason Agent · no separate server process":"Own process · PID "+s.Owner.ProcessId,false),("Runtime log",s.Log,true),("Checks",string.Join("\n",s.Profile.Checks.Select(x=>$"{x.Capability}: {x.State} · {x.Detail}")),false)); } };
        var stop=Button("Stop selected model",async(_,_)=> { if(aiOperation!=null)return;if(running.SelectedItem is not ServedModel s)return;try{modelServing.Stop(s);ResetStoppedProfile(s);Render();Selection();activity.IsOpen=true;serverDetail.Content=Muted("Server stopped. The connection remains saved; its capability checks were cleared.",14);activity.Message="Model stopped. Its saved connection is kept for next time.";}catch(Exception e){await Notice("Stop server",e.Message);} });
        stop.IsEnabled=false;
        running.SelectionChanged+=(_,_)=>stop.IsEnabled=running.SelectedItem is ServedModel && aiOperation==null;
        var workflow=new ComboBox {Header="Use this model in",MinWidth=180};
        var useModel=Button("Open workflow",(_,_)=>
        {
            if(aiOperation!=null || running.SelectedItem is not ServedModel s)return;
            var page=(workflow.SelectedItem as ComboBoxItem)?.Tag as string??"AI connections";
            if (page == "AI chat") { openCodeWithModel?.Invoke(s.Profile.Id); return; }
            selectManagedAiConnection?.Invoke(s.Profile.Id);
            Navigate(page);
        },true);
        useModel.IsEnabled=false;
        running.SelectionChanged+=(_,_)=>
        {
            var s=running.SelectedItem as ServedModel;
            useModel.IsEnabled=s!=null&&s.Owner.IsRunning&&s.Profile.Checks.Any(x=>x.Capability.ToString()==s.Model.Task&&x.State=="Passed");
            var old=(workflow.SelectedItem as ComboBoxItem)?.Tag as string;
            var destinations=s?.Model.Task=="Chat"?new[]{"AI chat","AI connections"}:new[]{"AI connections"};
            string Label(string route)=>route switch{"AI chat"=>"Code · coding agent","AI connections"=>"Connection settings",_=>route};
            var choices=destinations.Select(route=>new ComboBoxItem{Content=Label(route),Tag=route}).ToArray();workflow.ItemsSource=choices;workflow.SelectedItem=choices.FirstOrDefault(item=>(string)item.Tag==old)??choices[0];
            useModel.Content="Open selected page";
        };
        servingEmpty.Children.Add(Button("Browse your models",(_,_)=>ShowView(0),true));
        servingEmpty.Children.Add(Button("Manage external AI connections",(_,_)=>Navigate("AI connections")));
        EmptyState(servingEmpty, MoodReady, "nothing running");
        var runningChat = Button("Use for coding", (_, _) => { if (aiOperation == null && running.SelectedItem is ServedModel s) openCodeWithModel?.Invoke(s.Profile.Id); }, true); runningChat.Visibility = Visibility.Collapsed;
        running.SelectionChanged += (_, _) => runningChat.Visibility = running.SelectedItem is ServedModel s && s.Owner.IsRunning && s.Profile.Checks.Any(c => c.Capability == AiCapability.Chat && c.State == "Passed") ? Visibility.Visible : Visibility.Collapsed;
        var runningTalk = Button("Open in chat", (_, _) => { if (aiOperation == null && running.SelectedItem is ServedModel s) openNativeChat?.Invoke(s.Profile.Id); }); runningTalk.Visibility = Visibility.Collapsed;
        running.SelectionChanged += (_, _) => runningTalk.Visibility = runningChat.Visibility;
        servingControls=Stack(running,serverSummary,Text("Open your workflow with this connection selected. You choose when to send a message.",13),ScrollTools(Row(runningChat,runningTalk,workflow,useModel,stop,Button("Refresh status",(_,_)=>{Render();Selection();}))),new Expander {Header="Diagnostics · model ID, process and logs",Content=serverDetail,HorizontalAlignment=HorizontalAlignment.Stretch});
        runningCard.Content=servingControls;
        var sessionPage=new ScrollViewer { Content=Stack(Text("Running in Mason Agent",20),servingEmpty),VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        var add=Button("Add model folder",async(_,_)=> {try{var picker=new FolderPicker(AppWindow.Id);var folder=await picker.PickSingleFolderAsync();if(folder==null)return;if(settings.Locations.Count>=30)throw new ArgumentException("Keep at most 30 custom folders.");var path=Path.GetFullPath(folder.Path);settings=settings with { Locations=settings.Locations.Append(new(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),path,true)).DistinctBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).ToList() };Save();ShowLocations();await Scan();}catch(Exception e){await Notice("Model folder",e.Message);} },true);
        var remove=Button("Stop scanning this folder",async(_,_)=> {if((locations.SelectedItem as ListViewItem)?.Tag is not ModelLocation location)return;try{settings=settings with {Locations=settings.Locations.Where(x=>x!=location).ToList()};Save();ShowLocations();await Scan();activity.Message="Folder excluded. Files, running models and saved connections are unchanged.";}catch(Exception e){await Notice("Model folder",e.Message);} });
        remove.IsEnabled=false;locations.SelectionChanged+=(_,_)=>remove.IsEnabled=locations.SelectedItem is ListViewItem;
        Button? prepareQuantized=null,chooseQuantized=null;
        prepareQuantized=Button("Set up quantized runtime",async(_,_)=>
        {
            if(aiOperation!=null)return;
            if(!readable){await Notice("Model settings","Repair model-library.json before preparing a runtime.");return;}
            if(modelServing.Running.Any(x=>ModelAdapters.IsQuantized(x.Model) && x.Owner.IsRunning)){await Notice("Quantized runtime","Stop running quantized models before updating their runtime.");return;}
            if(!File.Exists(python.Text)){await PickRuntime(true);if(!File.Exists(python.Text))return;}
            using var ct=new CancellationTokenSource();aiOperation=ct;runtimeBusy?.Invoke(true);setupStages.Set(1);setupProgress.Visibility=Visibility.Visible;setupProgress.IsIndeterminate=Fluent.MotionOn;prepareQuantized!.IsEnabled=false;chooseQuantized!.IsEnabled=false;activity.IsOpen=true;activity.Severity=InfoBarSeverity.Informational;scanProgress.Visibility=Visibility.Visible;cancelScan!.Visibility=Visibility.Visible;
            try
            {
                quantizedPython.Text=await QuantizedRuntime.Prepare(python.Text,Path.Combine(AppContext.BaseDirectory,"model-runtime"),ct.Token,message=>{quantizedStatus.Text=message;activity.Message=message;});
                setupStages.Set(3);Save(2);ShowLocations();Selection();activity.Severity=InfoBarSeverity.Success;activity.Message="Quantized runtime ready. Choose a GPTQ or AWQ model in Library, then Start & connect.";
            }
            catch(Exception error){activity.Severity=InfoBarSeverity.Error;activity.Message=error is OperationCanceledException?"Runtime setup cancelled. Your base Python and model files were not changed; retry setup when ready.":error.Message;quantizedStatus.Text=activity.Message;}
            finally{setupProgress.Visibility=Visibility.Collapsed;runtimeBusy?.Invoke(false);aiOperation=null;prepareQuantized.IsEnabled=true;chooseQuantized.IsEnabled=true;scanProgress.Visibility=Visibility.Collapsed;cancelScan.Visibility=Visibility.Collapsed;Selection();}
        },true);
        chooseQuantized=Button("Choose existing runtime",async(_,_)=>
        {
            if(aiOperation!=null)return;
            try{var picker=new FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".exe");var file=await picker.PickSingleFileAsync();if(file==null)return;quantizedPython.Text=file.Path;Save(2);ShowLocations();Selection();}
            catch(Exception error){await Notice("Quantized runtime",error.Message);}
        });
        quantizedSetup=new Expander {Header="Quantized models · GPTQ & AWQ",HorizontalAlignment=HorizontalAlignment.Stretch,Content=Stack(Text("One setup for both formats",20),Text("Downloads Python packages into an isolated Mason Agent runtime using your selected Python as its base. Your existing Python installation and model files stay unchanged. Internet is needed for setup; serving uses local model files.",14),Text("Validated setup: Python 3.10–3.13, Transformers 5.5.0 and CUDA-enabled PyTorch 2.8+. Uses GPT-QModel 7.0.0 with its Windows-safe PyTorch backend. Keep the base Python environment installed.",12),quantizedPython,Stack(prepareQuantized,chooseQuantized),quantizedStatus,Text("Supported: 4-bit GPTQ/GPTQ-v2 and AWQ GEMM, group sizes 32/64/128 or -1. CPU and GPU are explicit choices. Other packed formats, mixed quantization and adapter-only folders need their own adapters.",12))};
        var runtimeList=new ListView{SelectionMode=ListViewSelectionMode.Single,Height=132};var runtimeInspector=new ContentControl();
        var folderTools=Button("Model locations",(_,_)=>openModelLocations?.Invoke());
        var runtimeNotice=Text("Models load when you choose Start. GGUF GPU launches check CUDA before loading weights and offer a CPU retry if CUDA is unavailable.",12);
        ((StackPanel)quantizedSetup.Content).Children.Remove(quantizedStatus);
        setupCancel=Button("Cancel setup",(_,_)=>aiOperation?.Cancel());setupCancel.IsEnabled=false;
        var setupHud=SurfaceCard(Stack(setupStages.View,setupProgress,quantizedStatus,setupCancel));setupHud.Padding=new Thickness(12);
        runtimeBusy=busy=>{runtimeList.IsEnabled=folderTools.IsEnabled=!busy;setupCancel.IsEnabled=busy;};
        // llama.cpp: which build is selected, what it can load, and a verified one-click update.
        var llamaVersion=Text("",15);llamaVersion.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;var llamaDetail=Text("",12);var llamaLatest=Text("",12);var llamaProgress=Text("",12);
        var llamaBackend=new ComboBox{Header="Build for",ItemsSource=LlamaRuntime.Backends.Select(LlamaRuntime.BackendLabel).ToArray(),SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
        LlamaRelease? llamaRelease=null;Button? installLlama=null;
        void ShowLlama()
        {
            var blocked=discovered.Where(x=>x.Format=="GGUF"&&x.Architecture.Length>0&&runtimeFacts.Loads(x.Architecture)==false).GroupBy(x=>x.Architecture).Select(g=>g.Key+" ("+string.Join(", ",g.Select(x=>ModelLibraryCard.FriendlyName(x.Name)).Distinct().Take(2))+")").ToArray();
            llamaVersion.Text=llamaBuild==null?(File.Exists(llama.Text)?"llama.cpp · version unknown":"llama.cpp isn't set up"):$"llama.cpp {(llamaBuild.Build is {} b?"b"+b:"build unknown")} · {llamaBuild.Backend}";
            llamaDetail.Text=llamaBuild==null?"Install a build below, or choose an existing llama-server.exe.":(llamaBuild.Architectures.Count>0?"Mason reads which model architectures this build can load from its library, and flags models it can't run.":"Architecture support couldn't be read from this build; it is checked when a model starts.")+(blocked.Length>0?"\nCan't load: "+string.Join("; ",blocked)+". Update to run these.":"");
            llamaLatest.Text=llamaRelease==null?"":llamaRelease.Build>(llamaBuild?.Build??0)?$"Update available: {llamaRelease.Tag} · published {llamaRelease.Published.LocalDateTime:d}":$"Up to date with {llamaRelease.Tag}.";
            if(installLlama!=null){installLlama.Visibility=llamaRelease!=null&&llamaRelease.Build>(llamaBuild?.Build??0)?Visibility.Visible:Visibility.Collapsed;installLlama.Content=llamaRelease==null?"Install":"Install "+llamaRelease.Tag;}
        }
        runtimeFactsChanged+=()=>{ShowLlama();refreshRuntimeInventory?.Invoke();};
        async Task CheckLlama()
        {
            llamaProgress.Text="Checking ggml-org/llama.cpp releases…";
            try{using var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};llamaRelease=await LlamaRuntime.Latest(http,CancellationToken.None);llamaProgress.Text=llamaRelease==null?"No Windows release was found.":"";}
            catch(Exception e) when(e is IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException){llamaProgress.Text="Couldn't check for updates: "+e.Message;}
            if(gpuSample?.Adapters.Length>0&&llamaBackend.SelectedIndex<0)llamaBackend.SelectedIndex=0;
            ShowLlama();
        }
        installLlama=Button("Install",async(_,_)=>
        {
            if(aiOperation!=null||llamaRelease is not {} release)return;
            var backend=LlamaRuntime.Backends[Math.Max(0,llamaBackend.SelectedIndex)];
            var assets=release.For(backend);
            if(assets==null){await Notice("llama.cpp",$"{release.Tag} has no {LlamaRuntime.BackendLabel(backend)} build for Windows x64. Choose another build.");return;}
            var dialog=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Install llama.cpp "+release.Tag,PrimaryButtonText="Download and install",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Primary,
                Content=ScrollPage(Stack(Text(LlamaRuntime.BackendLabel(backend),15),Text(string.Join("\n",assets.Select(a=>a.Name+" · "+HubFormat.Size(a.Size))),12),Text("Source: github.com/ggml-org/llama.cpp. Each file is checked against the SHA-256 the release publishes before it is unpacked.",12),Text("Installs to "+LlamaRuntime.ManagedRoot+". Your current runtime stays on disk; running models keep using it until restarted.",12)))};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            using var ct=new CancellationTokenSource();aiOperation=ct;installLlama!.IsEnabled=false;runtimeBusy?.Invoke(true);setupStages.Set(1);
            try
            {
                using var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
                var path=await LlamaRuntime.Install(http,release,backend,message=>DispatcherQueue.TryEnqueue(()=>{llamaProgress.Text=message;}),ct.Token);
                llama.Text=path;Save(0);ShowLocations();await RefreshRuntimeFacts(true);setupStages.Set(3);
                llamaProgress.Text=$"Installed {release.Tag}. New starts use it; restart running GGUF models to switch.";
            }
            catch(Exception e){llamaProgress.Text=e is OperationCanceledException?"Install cancelled. Your previous runtime is unchanged.":"Install failed: "+e.Message+" Your previous runtime is unchanged.";}
            finally{aiOperation=null;installLlama.IsEnabled=true;runtimeBusy?.Invoke(false);ShowLlama();}
        },true);
        var llamaCard=Card(Stack(llamaVersion,llamaDetail,llamaLatest,llamaBackend,Row(Button("Check for updates",async(_,_)=>await CheckLlama()),installLlama),llamaProgress));
        bool llamaChecked=false;
        llamaCard.Loaded+=async(_,_)=>{if(llamaChecked)return;llamaChecked=true;ShowLlama();await CheckLlama();};
        FrameworkElement RuntimePanel(int index){var path=Text("",12);path.SetBinding(TextBlock.TextProperty,new Microsoft.UI.Xaml.Data.Binding{Source=index==0?llama:python,Path=new PropertyPath("Text")});var panel=Stack(Text(index==0?"GGUF serving runtime":"Transformers serving runtime",19),Text(runtimeNotice.Text,12),path,Button(index==0?"Choose llama-server.exe":"Choose Python runtime",async(_,_)=>await PickRuntime(index==1)),new Expander{Header="Supported adapters and conventions",Content=Text("GGUF uses llama-server.exe. Standard text models use local Transformers and PyTorch. A file path does not establish inference capability.",12),HorizontalAlignment=HorizontalAlignment.Stretch});if(index==0)panel.Children.Insert(1,llamaCard);return panel;}
        var basePath=new TextBox{Header="Base Python environment",IsReadOnly=true};basePath.SetBinding(TextBox.TextProperty,new Microsoft.UI.Xaml.Data.Binding{Source=python,Path=new PropertyPath("Text")});
        var changeBase=Button("Choose base Python",async(_,_)=>{if(aiOperation!=null)return;await PickRuntime(true);});
        var updateRuntimeBusy=runtimeBusy;runtimeBusy=busy=>{updateRuntimeBusy?.Invoke(busy);changeBase.IsEnabled=!busy;if(busy)quantizedSetup.IsExpanded=false;};
        var quantizedDetails=Stack(FormGrid(basePath,changeBase),quantizedSetup,setupHud);quantizedDetails.Visibility=Visibility.Collapsed;
        var setupScope=Stack(Text("Setup scope",19),Text("Downloads runtime packages into an isolated app-owned environment. Model weights are not downloaded, and the selected base Python environment is retained.",13),Text("Prerequisites",16),Text("Python 3.10–3.13\nCUDA-enabled PyTorch 2.8+\nTransformers 5.5.0\nGPT-QModel 7.0.0",13),Text("Compatibility is checked during setup and when the selected model starts. A saved path is not proof of readiness.",12),Text("When setup finishes",16),Text("Return to Library and choose Start & connect. Setup does not launch a model.",12));
        var runtimePages=new FrameworkElement[]{RuntimePanel(0),RuntimePanel(1),setupScope};var runtimeFrame=new Grid();foreach(var page in runtimePages)runtimeFrame.Children.Add(page);runtimeInspector.Content=runtimeFrame;

        bool refreshingRuntime=false;refreshRuntimeInventory=()=>{int selected=(runtimeList.SelectedItem as ListRow)?.Source is int index?index:0;refreshingRuntime=true;try{runtimeList.ItemsSource=new[]{new ListRow(0,"GGUF · llama.cpp",llama.Text,llamaBuild?.Build is {} build?"b"+build+" · "+llamaBuild.Backend:File.Exists(llama.Text)?"Path selected":"Install or choose"),new ListRow(1,"Transformers · PyTorch",python.Text,File.Exists(python.Text)?"Path selected":"Choose environment"),new ListRow(2,"Quantized · GPTQ / AWQ",quantizedPython.Text,File.Exists(quantizedPython.Text)?"Path selected":"Setup available")};runtimeList.SelectedIndex=selected;}finally{refreshingRuntime=false;}};
        runtimeList.SelectionChanged+=(_,_)=>{int selected=(runtimeList.SelectedItem as ListRow)?.Source is int index?index:0;for(int i=0;i<runtimePages.Length;i++)runtimePages[i].Visibility=i==selected?Visibility.Visible:Visibility.Collapsed;quantizedDetails.Visibility=selected==2?Visibility.Visible:Visibility.Collapsed;if(selected==2&&!refreshingRuntime)quantizedSetup.IsExpanded=true;};
        selectRuntime=index=>runtimeList.SelectedIndex=index;
        var locationPage=InspectorLayout(Vertical(Stack(Text("Serving runtimes",17),ListTable(runtimeList,("Runtime","Values[0]",1.3),("Selected local path","Values[1]",2),("Status","Values[2]",1))),ScrollPage(quantizedDetails)),ScrollPage(runtimeInspector),"Selected runtime");refreshRuntimeInventory();
        registerDownloadedModel=async(job,reveal)=>
        {
            if(!registeredDownloads.TryGetValue(job.Id,out var models))
            {
                var inventory=await Task.Run(()=>ModelDiscovery.Scan([new("Downloaded · "+job.Repository,job.Folder)],CancellationToken.None));
                models=inventory.Models.Select(DownloadIdentity).ToArray();
                if(models.Length==0)models=[new(job.Name,job.Folder,"Downloaded · "+job.Repository,job.Format,"Inventory only",job.Total,"Files verified. This bundle is not recognized by a local serving adapter; inspect the model card and files.")];
                registeredDownloads[job.Id]=models;
                foreach(var m in models.Where(m=>m.Launchable))memoryProfiles[m.Path]=await Task.Run(()=>ModelMemory.Read(m,CancellationToken.None));
            }
            discovered=models.Concat(discovered).DistinctBy(m=>m.Path+"|"+m.AdapterPath,StringComparer.OrdinalIgnoreCase).ToArray();if(discovered.Length>0&&activity.Severity==InfoBarSeverity.Informational)activity.IsOpen=false;Render();Selection();
            if(reveal){Navigate("Models");search.Text="";task.SelectedIndex=0;resourceKind.SelectedIndex=0;list.SelectedItem=(list.ItemsSource as IEnumerable<LibraryEntry>)?.FirstOrDefault(e=>e.File!=null&&models.Any(m=>m.Path==e.File.Model.Path));if(list.SelectedItem!=null)list.ScrollIntoView(list.SelectedItem);}
        };
        var acquisition=BuildModelAcquisition();
        var pages=new FrameworkElement[]{library,sessionPage,locationPage,acquisition.Discover,acquisition.Downloads,connections}.Select(page=>new ContentControl{Content=page,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch}).ToArray();var host=new Grid();foreach(var p in pages)host.Children.Add(p);
        var locationSummary=Text("",12);var managedLocations=Text("",12);
        var closeLocations=Button("Close",(_,_)=>{});
        var locationPanel=SurfaceCard(Vertical(Toolbar(Text("Model locations",23),closeLocations),ScrollPage(Stack(DownloadStorage(),new Expander{Header="Managed model folders",Content=managedLocations,HorizontalAlignment=HorizontalAlignment.Stretch},Text("Folders included in Library",20),Text("Existing folders are indexed in place. Adding a folder does not copy its models.",12),ScrollTools(Row(add,remove)),locations,common,new Expander{Header="Common folders · availability",Content=folders,HorizontalAlignment=HorizontalAlignment.Stretch},locationSummary,Button("Scan library",async(_,_)=>{await Scan();locationSummary.Text=lastScan.HasValue?"Last scan · "+lastScan.Value.ToLocalTime().ToString("g"):"Not scanned yet";}),new Expander{Header="Scan details",Content=scanNotes,HorizontalAlignment=HorizontalAlignment.Stretch}))));
        var locationOverlay=new Grid{Visibility=Visibility.Collapsed};var shade=new Border{Background=new SolidColorBrush(Windows.UI.Color.FromArgb(75,0,0,0))};locationOverlay.Children.Add(shade);locationOverlay.Children.Add(locationPanel);locationPanel.HorizontalAlignment=HorizontalAlignment.Right;
        void LocationSurface(){locationPanel.Background=new SolidColorBrush(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast?new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.Window):root.ActualTheme==ElementTheme.Dark?BrandRaised:BrandWhite);}
        root.ActualThemeChanged+=(_,_)=>LocationSurface();LocationSurface();
        void LocationWidth(){locationPanel.Width=host.ActualWidth<900?Math.Max(0,host.ActualWidth):Math.Min(560,host.ActualWidth);}
        host.SizeChanged+=(_,_)=>LocationWidth();host.Children.Add(locationOverlay);
        void CloseLocations(){locationOverlay.Visibility=Visibility.Collapsed;foreach(var page in pages)page.IsEnabled=true;folderTools.Focus(FocusState.Programmatic);}
        closeLocations.Click+=(_,_)=>CloseLocations();locationOverlay.KeyDown+=(_,e)=>{if(e.Key==Windows.System.VirtualKey.Escape){e.Handled=true;CloseLocations();}};
        openModelLocations=()=>{managedLocations.Text=string.Join("\n\n",(modelDownloads?.Snapshot()??[]).Where(j=>j.State=="Ready in library").Select(j=>j.Repository+" · Managed download\n"+j.Folder+"\n"+(Directory.Exists(j.Folder)?"Included · folder found":"Unavailable")));if(managedLocations.Text.Length==0)managedLocations.Text="Verified downloads are included automatically. No managed models yet.";ShowLocations();refreshDownloadStorage?.Invoke();locationSummary.Text="Managed downloads · "+(modelDownloads?.Snapshot().Count(j=>j.State=="Ready in library")??0)+" verified bundles\n"+(lastScan.HasValue?"Last scan · "+lastScan.Value.ToLocalTime().ToString("g"):"Not scanned yet");LocationWidth();foreach(var page in pages)page.IsEnabled=false;locationOverlay.Visibility=Visibility.Visible;closeLocations.Focus(FocusState.Programmatic);};
        // One page, five sections. The rail replaces the tabs and the separate Connections page.
        var sections=new (string Label,string Glyph,string Subtitle)[]{
            ("My models","",""),
            ("Discover","","Find models on Hugging Face. Every result says which agent job it can do and whether it runs on this PC."),
            ("Connections","","Model servers on this PC that the agent can use. Mason finds the common ones for you."),
            ("Downloads","","Pinned, SHA-256 verified downloads. Finished models appear in My models, stopped until you start them."),
            ("Runtimes","","The engines that run models for the agent: llama.cpp for GGUF, Transformers and the quantized runtime.")};
        foreach(var section in sections)views.Items.Add(new ViewOption{Header=section.Label});
        var rail=SectionRail(i=>views.SelectedIndex=i,sections.Select(s=>(s.Label,s.Glyph)).ToArray());
        var sectionSubtitle=Muted("",13);sectionSubtitle.MaxLines=2;sectionSubtitle.TextTrimming=TextTrimming.CharacterEllipsis;
        int Page()=>tabPages[Math.Clamp(views.SelectedIndex,0,tabPages.Length-1)];
        FrameworkElement? libraryActions=null;
        void Show()
        {
            int page=Page();rail.Select(views.SelectedIndex);
            title.Text=sections[Math.Clamp(views.SelectedIndex,0,sections.Length-1)].Label;
            sectionSubtitle.Text=sections[Math.Clamp(views.SelectedIndex,0,sections.Length-1)].Subtitle;
            summary.Visibility=page==0?Visibility.Visible:Visibility.Collapsed;sectionSubtitle.Visibility=page==0?Visibility.Collapsed:Visibility.Visible;
            if(libraryActions!=null)libraryActions.Visibility=page==0?Visibility.Visible:Visibility.Collapsed;
            for(int i=0;i<pages.Length;i++){pages[i].Visibility=page==i?Visibility.Visible:Visibility.Collapsed;pages[i].IsEnabled=true;}
            locationOverlay.Visibility=Visibility.Collapsed;activity.Visibility=page is 0 or 2?Visibility.Visible:Visibility.Collapsed;
            if(page==2)_=RefreshRuntimeFacts(true);
            if(page==5)refreshAiConnectionList?.Invoke();
        }
        views.SelectionChanged+=(_,_)=>Show();Show();ShowLocations();Render();Selection();
        selectModelView=ShowView;
        common.Toggled+=async(_,_)=>{try{Save();}catch(Exception e){await Notice("Model locations",e.Message);}};
        embedGguf.Toggled+=async(_,_)=>{try{Save();}catch(Exception e){await Notice("Runtime setup",e.Message);}};
        refreshModelLibrary=()=>{Render();Selection();};
        cancelScan=Button("Cancel operation",(_,_)=>{scan?.Cancel();aiOperation?.Cancel();});cancelScan.Visibility=Visibility.Collapsed;
        // G2: one-click local setup. Detect the GPU, install the matching llama.cpp build if needed, recommend the
        // best-quality coding model that fits, download it after confirmation, then start it inside Mason.
        async Task QuickSetupFlow()
        {
            if(aiOperation!=null)return;
            if(modelHub==null||modelDownloads==null){await Notice("Quick setup","Downloads are unavailable in this window. Another Mason window may own the download queue.");return;}
            using var ct=new CancellationTokenSource();aiOperation=ct;cancelScan!.Visibility=Visibility.Visible;
            void Progress(string text){activity.Severity=InfoBarSeverity.Informational;activity.Title="Quick setup";activity.Message=text;activity.IsOpen=true;}
            try
            {
                Progress("Checking this computer…");
                gpuSample??=await GpuTelemetry.Read(ct.Token);
                var backend=QuickSetup.Backend(gpuSample);var budget=QuickSetup.BudgetGiB(gpuSample);
                bool needRuntime=!File.Exists(llama.Text)||EmbeddedLlamaServer.Unavailable(llama.Text)?.Contains("llama-server-impl")==true;
                LlamaRelease? release=null;
                using(var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)})
                    if(needRuntime){Progress("Finding the current llama.cpp release…");release=await LlamaRuntime.Latest(http,ct.Token)??throw new InvalidOperationException("No llama.cpp release for Windows was found.");}
                Progress("Finding coding models that fit "+budget.ToString("0.0")+" GiB…");
                var found=await modelHub.Search("coder","","GGUF",ct.Token);
                var picks=new List<QuickSetup.Choice>();
                foreach(var candidate in found.Where(m=>m.Role==AgentRole.Implementer&&QuickSetup.Suggestible(m.Id)).OrderByDescending(m=>QuickSetup.Trusted(m.Id)).Take(10))
                {
                    try{var repo=await modelHub.Inspect(candidate.Id,ct.Token);if(!repo.Gated&&QuickSetup.Fit(repo,budget) is {} fit)picks.Add(fit);}
                    catch(Exception e) when(e is IOException or InvalidDataException or HttpRequestException){}
                    if(picks.Count>=4)break;
                }
                picks=QuickSetup.Rank(picks).ToList();
                if(picks.Count==0)throw new InvalidOperationException("No coding model on Hugging Face fits in "+budget.ToString("0.0")+" GiB. Free GPU memory or choose a model in Discover.");
                activity.IsOpen=false;
                var gpuName=gpuSample?.Adapters.FirstOrDefault()?.Name??"No GPU detected";
                var model=new ComboBox{ItemsSource=picks.Select(p=>p.Repository.Id.Split('/').Last()+" · "+p.Variant.Name+" · "+HubFormat.Size(p.Variant.Bytes)).ToArray(),SelectedIndex=0,MinWidth=300};
                var reason=new TextBlock{Text=picks[0].Reason,Style=Token("MasonCaptionSecondary")};
                model.SelectionChanged+=(_,_)=>reason.Text=picks[Math.Max(0,model.SelectedIndex)].Reason;
                var plan=SettingsPage(
                    SettingsCard("\uE7F8","This computer",gpuName+" · "+budget.ToString("0.0")+" GiB for the model"),
                    SettingsCard("\uE9F5","Runtime",needRuntime&&release!=null?$"Install llama.cpp {release.Tag} · {LlamaRuntime.BackendLabel(backend)} · {HubFormat.Size(release.For(backend)?.Sum(a=>a.Size)??0)}":"Use the installed llama.cpp build"),
                    SettingsCard("\uE943","Coding model",null,model),reason,
                    SettingsCard("\uE8B7","Saved to",downloadSettings.Folder),
                    SettingsCard("\uE7EF","Runs inside Mason","No separate server. The model starts when the download is verified."));
                plan.MaxHeight=440;plan.Padding=new Thickness(0);
                if(await new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Set up local AI",Content=plan,PrimaryButtonText="Download and start",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Primary}.ShowAsync()!=ContentDialogResult.Primary)return;
                if(needRuntime&&release!=null)
                {
                    using var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
                    var path=await LlamaRuntime.Install(http,release,backend,message=>DispatcherQueue.TryEnqueue(()=>Progress(message)),ct.Token);
                    llama.Text=path;Save(0);await RefreshRuntimeFacts();
                }
                var pick=picks[Math.Max(0,model.SelectedIndex)];
                var job=modelDownloads.Enqueue(pick.Repository,pick.Variant,downloadSettings.Folder);
                while(true)
                {
                    var now=modelDownloads.Snapshot().FirstOrDefault(j=>j.Id==job.Id)??throw new InvalidOperationException("The download was removed.");
                    if(now.State=="Ready in library")break;
                    if(now.State is "Needs attention" or "Paused")throw new InvalidOperationException("The download stopped ("+now.State+"). Resume it in Downloads.");
                    Progress("Downloading "+pick.Repository.Id.Split('/').Last()+" · "+now.ProgressText+(now.RateText.Length>0?" · "+now.RateText:""));
                    await Task.Delay(1000,ct.Token);
                }
                Progress("Adding the model to your library…");
                await Scan();
                var entry=list.Items.OfType<LibraryEntry>().FirstOrDefault(e=>e.File?.Model.Path.StartsWith(job.Folder,StringComparison.OrdinalIgnoreCase)==true);
                if(entry==null){Progress("Downloaded. Choose it in My models and select Start & connect.");return;}
                list.SelectedItem=entry;
                Progress("Starting "+pick.Repository.Id.Split('/').Last()+" inside Mason…");
                aiOperation=null;
                if(start!=null && new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(start) is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke)invoke.Invoke();
            }
            catch(OperationCanceledException){Progress("Quick setup cancelled. Anything already installed or downloaded is kept.");}
            catch(Exception e){activity.Severity=InfoBarSeverity.Error;activity.Title="Quick setup";activity.Message=e.Message;activity.IsOpen=true;}
            finally{if(aiOperation==ct)aiOperation=null;cancelScan!.Visibility=Visibility.Collapsed;}
        }
        var quickSetup=IconButton("\uE945","Quick setup",async(_,_)=>await QuickSetupFlow(),true);
        ToolTipService.SetToolTip(quickSetup,"Detect your GPU, install llama.cpp if needed, download the best coding model that fits and start it inside Mason.");
        var headerActions=ScrollTools(Row(quickSetup,Button("Scan library",async(_,_)=>await Scan()),folderTools,Button("Find models",(_,_)=>ShowView(3)),cancelScan));
        var header=new Grid {ColumnSpacing=12};header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        // Header: the page name with the collection summary under it; collection actions on the right.
        summary.Opacity = .8; summary.MaxLines = 2; summary.TextTrimming = TextTrimming.CharacterEllipsis;
        var titleBlock = Stack(title, summary, sectionSubtitle); titleBlock.Spacing = 2; titleBlock.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(titleBlock); headerActions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(headerActions,1);header.Children.Add(headerActions);
        var gpuName=Text("GPU · waiting for first reading",14);
        gpuName.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
        var gpuStats=Text("",13);
        var gpuMemory=BrandControl(new ProgressBar {Minimum=0,Maximum=100,Height=4,Visibility=Visibility.Collapsed});
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gpuMemory,"Dedicated GPU memory used");
        var gpuDetails=Text("",12);
        var gpuProcesses=Text("",13);
        var gpuPicker=new ComboBox {MinWidth=160,Visibility=Visibility.Collapsed};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gpuPicker,"GPU to monitor");
        void ShowGpu()
        {
            UpdateFit();
            if(gpuSample==null)return;
            if(gpuSample.Error!=null)
            {
                gpuName.Text="GPU telemetry unavailable";gpuStats.Text=gpuSample.Error;
                gpuMemory.Visibility=Visibility.Collapsed;gpuProcesses.Text="No current process reading.";gpuDetails.Text=$"Last attempt {gpuSample.At.ToLocalTime():T}";return;
            }
            var gpu=gpuSample.Adapters.ElementAtOrDefault(Math.Max(0,gpuPicker.SelectedIndex));if(gpu==null)return;
            string Memory(double? value)=>value.HasValue?$"{value.Value/1024:0.0} GiB":"Unavailable";
            gpuName.Text=gpu.Name;
            gpuStats.Text=$"GPU {(gpu.Load.HasValue?$"{gpu.Load:0}%":"unavailable")} · VRAM {Memory(gpu.UsedMiB)} used / {Memory(gpu.TotalMiB)} total · {Memory(gpu.FreeMiB)} free";
            bool memoryAvailable=gpu.UsedMiB.HasValue && gpu.TotalMiB>0;
            gpuMemory.Visibility=memoryAvailable?Visibility.Visible:Visibility.Collapsed;
            if(memoryAvailable)gpuMemory.Value=Math.Clamp(gpu.UsedMiB!.Value/gpu.TotalMiB!.Value*100,0,100);
            var processes=gpuSample.Processes.Where(x=>x.GpuId==gpu.Id).ToArray();
            gpuProcesses.Text=gpuSample.ProcessError??(processes.Length==0?"No compute processes reported.":string.Join("\n",processes.Select(x=>$"{x.Name} · PID {x.Pid} · {(x.MemoryMiB.HasValue?Memory(x.MemoryMiB):"VRAM unavailable")}")));
            gpuDetails.Text=$"Updated {gpuSample.At.ToLocalTime():T} · Refreshes every 5 seconds while Models is open.\nTotals include this app, other servers and desktop graphics. Compute processes exclude graphics-only apps. Windows may withhold per-process VRAM. Free memory is not a guarantee a model fits.";
        }
        gpuPicker.SelectionChanged+=(_,_)=>ShowGpu();
        var gpuDetailsButton=Button("GPU details",(_,_)=>{});
        var gpuFlyoutBody=Stack(Text("GPU & compute processes",20),gpuPicker,gpuProcesses,gpuDetails);
        gpuFlyoutBody.Width=360;
        gpuDetailsButton.Flyout=Fluent.Acrylic(new Flyout {Content=new ScrollViewer {Content=gpuFlyoutBody,MaxHeight=360,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        // Status band: the GPU on the left, the facts that decide what can run in the middle, details on the right.
        gpuName.VerticalAlignment = gpuStats.VerticalAlignment = VerticalAlignment.Center;
        gpuStats.Opacity = .8; gpuStats.MaxLines = 1; gpuStats.TextTrimming = TextTrimming.CharacterEllipsis;
        gpuStats.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => ToolTipService.SetToolTip(gpuStats, gpuStats.Text));
        var gpuBlock = Stack(gpuName, gpuStats, gpuMemory); gpuBlock.Spacing = 4; gpuBlock.MinWidth = 260; gpuBlock.VerticalAlignment = VerticalAlignment.Center;
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var band = new Grid { ColumnSpacing = 20 };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) band.ColumnDefinitions.Add(new() { Width = width });
        band.Children.Add(gpuBlock); var chipScroll = ScrollTools(chips); Grid.SetColumn(chipScroll, 1); band.Children.Add(chipScroll); Grid.SetColumn(gpuDetailsButton, 2); band.Children.Add(gpuDetailsButton);
        gpuDetailsButton.VerticalAlignment = VerticalAlignment.Center;
        var bandSurface = BrandSurface(band, new Thickness(18, 12, 12, 12), 12);
        // Agent readiness: what the coding agent can do right now, each line a shortcut to fix it.
        var readiness = new StackPanel { Spacing = 4 };
        Button ReadinessRow(Tone tone, string label, string detail, Action go)
        {
            var text = Stack(Text(label, 13), Muted(detail, 11)); text.Spacing = 1; ((TextBlock)text.Children[0]).FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; ((TextBlock)text.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis; ((TextBlock)text.Children[1]).MaxLines = 2;
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 7, 8, 7) }; grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var dot = Dot(tone); dot.VerticalAlignment = VerticalAlignment.Top; dot.Margin = new Thickness(0, 6, 0, 0); grid.Children.Add(dot); Grid.SetColumn(text, 1); grid.Children.Add(text);
            var button = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(RadiusSmall) };
            button.Click += (_, _) => go(); Accessible(button, label + " · " + detail); return button;
        }
        void Filter(int index) { ShowView(0); task.SelectedIndex = index; }
        void Find(int index) { ShowView(3); discoverRole?.Invoke(index); }
        void UpdateShell()
        {
            List<AiEndpointProfile> endpoints; try { endpoints = Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []; } catch { endpoints = []; }
            var live = modelServing.Running.Where(s => s.Owner.IsRunning).ToArray();
            var jobs = modelDownloads?.Snapshot() ?? [];
            bool update = llamaRelease != null && llamaRelease.Build > (llamaBuild?.Build ?? 0);
            rail.Entries[0].Count.Text = discovered.Length > 0 ? discovered.Length.ToString() : "";
            rail.Entries[2].Count.Text = endpoints.Count > 0 ? endpoints.Count.ToString() : "";
            rail.Entries[3].Count.Text = jobs.Count(j => !j.Terminal) is > 0 and var active ? active + " active" : "";
            rail.Entries[4].Count.Text = update ? "Update" : "";
            chips.Children.Clear();
            chips.Children.Add(Pill(llamaBuild?.Build is { } b ? $"llama.cpp b{b}" + (update ? " · update available" : "") : File.Exists(llama.Text) ? "llama.cpp" : "llama.cpp not set up", update || llamaBuild == null ? Tone.Warn : Tone.Neutral, 12));
            chips.Children.Add(Pill(live.Length == 0 ? "Nothing running" : $"{live.Length} running", live.Length > 0 ? Tone.Accent : Tone.Neutral, 12));
            chips.Children.Add(Pill(Mason.Code.CodeSemanticIndex.Available ? "Code search on" : "Code search off", Mason.Code.CodeSemanticIndex.Available ? Tone.Good : Tone.Neutral, 12));
            string Name(LocalModel m) => ModelLibraryCard.FriendlyName(m.Name);
            var chatLive = live.FirstOrDefault(s => s.Model.Task == "Chat"); var visionLive = live.FirstOrDefault(s => s.Model.Role == AgentRole.Vision);
            var chatConnection = endpoints.FirstOrDefault(p => p.ManagedCapability == null && p.Checks.Any(c => c.Capability == AiCapability.Chat && c.State == "Passed"));
            int coding = discovered.Count(m => m.Role is AgentRole.Implementer or AgentRole.Vision && m.Launchable), vision = discovered.Count(m => m.Role == AgentRole.Vision && m.Launchable);
            int search = discovered.Count(m => m.Role is AgentRole.CodeSearch or AgentRole.Reranker && m.Launchable), drafts = discovered.Count(m => m.Role == AgentRole.Draft);
            readiness.Children.Clear();
            readiness.Children.Add(ReadinessRow(chatLive != null || chatConnection != null ? Tone.Good : coding > 0 ? Tone.Accent : Tone.Warn, "Coding agent",
                chatLive != null ? "Running · " + Name(chatLive.Model) : chatConnection != null ? "Connected · " + chatConnection.DisplayName : coding > 0 ? $"{coding} {(coding == 1 ? "model" : "models")} ready to start" : "Find a coding model",
                () => { if (coding > 0) Filter(1); else Find(1); }));
            readiness.Children.Add(ReadinessRow(visionLive != null ? Tone.Good : vision > 0 ? Tone.Accent : Tone.Neutral, "Visual verifier",
                visionLive != null ? "Running · " + Name(visionLive.Model) : vision > 0 ? $"{vision} vision {(vision == 1 ? "model" : "models")} ready to start" : "Optional · checks UI work in screenshots",
                () => { if (vision > 0) Filter(2); else Find(2); }));
            readiness.Children.Add(ReadinessRow(Mason.Code.CodeSemanticIndex.Available ? Tone.Good : search > 0 ? Tone.Accent : Tone.Neutral, "Code search",
                Mason.Code.CodeSemanticIndex.Available ? "On · the agent can search by meaning" : search > 0 ? "Start an embeddings model to turn it on" : "Off · add an embeddings model",
                () => { if (search > 0) Filter(3); else Find(3); }));
            readiness.Children.Add(ReadinessRow(drafts > 0 ? Tone.Accent : Tone.Neutral, "Faster turns", drafts > 0 ? $"{drafts} draft {(drafts == 1 ? "model" : "models")} · attach when starting" : "Optional · add a small draft model",
                () => { if (drafts > 0) Filter(4); else Find(4); }));
        }
        updateModelsShell = UpdateShell;
        runtimeFactsChanged += UpdateShell;
        // Layout: section rail on the left, the selected section on the right under a shared header and status band.
        var railLabel = Text("AGENT READINESS", 11); railLabel.CharacterSpacing = 80; railLabel.Opacity = .7; railLabel.Margin = new Thickness(8, 14, 0, 2);
        var railDivider = new Border { Height = 1, Margin = new Thickness(8, 10, 8, 0), Opacity = .5 };
        void Divider() => railDivider.Background = new SolidColorBrush(BrandLine(railDivider.ActualTheme == ElementTheme.Dark)); railDivider.Loaded += (_, _) => Divider(); railDivider.ActualThemeChanged += (_, _) => Divider();
        var railBody = Stack(rail.View, railDivider, railLabel, readiness); railBody.Spacing = 2;
        var railSurface = BrandSurface(ScrollPage(railBody), new Thickness(8, 10, 8, 10), 12);
        libraryActions = headerActions;
        var intro = Stack(header, bandSurface, scanProgress, activity); intro.Spacing = 12;
        var main = Vertical(intro, host);
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(248) }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(railSurface); Grid.SetColumn(main, 1); layout.Children.Add(main);
        layout.SizeChanged += (_, _) =>
        {
            bool compact = layout.ActualWidth < 1100;
            layout.ColumnDefinitions[0].Width = new GridLength(compact ? 60 : 248); rail.Compact(compact);
            railDivider.Visibility = railLabel.Visibility = readiness.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            gpuStats.Visibility = layout.ActualWidth < 900 ? Visibility.Collapsed : Visibility.Visible;
        };
        root.ActualThemeChanged += (_, _) => { Render(); UpdateShell(); };
        libraryTop = null; Show(); UpdateShell();
        var gpuTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(5)};
        CancellationTokenSource? gpuRead=null;bool gpuClosed=false;
        async Task RefreshGpu()
        {
            if(gpuClosed || currentRoute!="Models and connections" || layout.Visibility!=Visibility.Visible || gpuRead!=null)return;
            using var ct=new CancellationTokenSource();gpuRead=ct;
            try
            {
                var sample=await GpuTelemetry.Read(ct.Token);
                if(gpuClosed || ct.IsCancellationRequested)return;
                var selectedId=gpuSample?.Adapters.ElementAtOrDefault(Math.Max(0,gpuPicker.SelectedIndex))?.Id;
                gpuSample=sample;
                gpuPicker.ItemsSource=sample.Adapters.Select(x=>x.Name).ToArray();
                gpuPicker.SelectedIndex=Math.Max(0,Array.FindIndex(sample.Adapters,x=>x.Id==selectedId));
                gpuPicker.Visibility=sample.Adapters.Length>1?Visibility.Visible:Visibility.Collapsed;
                ShowGpu();
            }
            catch(OperationCanceledException) { }
            finally {gpuRead=null;}
        }
        gpuTimer.Tick+=async(_,_)=>await RefreshGpu();
        layout.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,async(_,_)=>
        {
            if(layout.Visibility==Visibility.Visible && !gpuClosed){gpuTimer.Start();await RefreshGpu();}
            else {gpuTimer.Stop();gpuRead?.Cancel();}
        });
        layout.Loaded+=async(_,_)=>{if(layout.Visibility==Visibility.Visible){gpuTimer.Start();await RefreshGpu();}};
        Closed+=(_,_)=>{gpuClosed=true;gpuTimer.Stop();gpuRead?.Cancel();};
        layout.Loaded+=async(_,_)=>{if(scanned)return;scanned=true;await RefreshRuntimeFacts();await Scan();};
        root.Unloaded+=(_,_)=>{scan?.Cancel();modelServing.Dispose();};
        return layout;
    }
}




