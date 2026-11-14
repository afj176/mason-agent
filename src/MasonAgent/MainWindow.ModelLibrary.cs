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


    FrameworkElement BuildModelLibrary()
    {
        var runtimes=RuntimeDiscovery.Find();
        ModelLibrarySettings settings; bool readable = true;
        try { settings = ModelLibrarySettings.ReadSaved(Path.Combine(Store.Root,"model-library.json")) ?? new([],runtimes.LlamaServer,runtimes.Python);
            if(settings.Locations==null || settings.Locations.Count>30 || settings.Locations.Any(x=>x==null || string.IsNullOrWhiteSpace(x.Path) || !Path.IsPathFullyQualified(x.Path))) throw new InvalidDataException("Invalid model location settings.");
            settings=settings with { LlamaServer=settings.LlamaServer ?? "",Python=settings.Python ?? "",QuantizedPython=string.IsNullOrEmpty(settings.QuantizedPython)&&QuantizedRuntime.IsPrepared?QuantizedRuntime.ManagedPython:settings.QuantizedPython??"" };
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
        var title = Text("Models",26);
        var caption = Text("Your local AI, ready for work. Choose a model to see what it can do.",14);
        var summary = Text("Ready to discover your local collection.",13);
        var activity = new InfoBar { IsOpen=true, IsClosable=true, Severity=InfoBarSeverity.Informational, Message="Scanning reads names, sizes and model metadata. It never downloads, moves or loads weights." };
        var search = new TextBox { PlaceholderText="Find a model or connection…", MinWidth=180, HorizontalAlignment=HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search,"Search local models by name, format or folder");
        var task = new ComboBox { ItemsSource=new[]{"All chat models","Chat"},SelectedIndex=0,MinWidth=130 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(task,"Filter models by capability");
        var resourceKind=new ComboBox{ItemsSource=new[]{"All resources","Local weights","Connections"},SelectedIndex=0,MinWidth=140};
        Accessible(resourceKind,"Filter resources by kind");
        var sort = new ComboBox { ItemsSource=new[]{"Suggested order","Name","Size · largest first","Size · smallest first"},SelectedIndex=0,MinWidth=150 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sort,"Sort model collection");
        var list = new ListView { SelectionMode=ListViewSelectionMode.Single, HorizontalContentAlignment=HorizontalAlignment.Stretch };
        var resourceTable=ListTable(list,("Resource","Title",2.1),("Kind","Kind",1.6),("State / observation","State",1.8));
        list.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Border BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='0,0,0,1'><Grid MinHeight='58' Padding='12,8' ColumnSpacing='12'><Grid.ColumnDefinitions><ColumnDefinition Width='2.1*'/><ColumnDefinition Width='1.6*'/><ColumnDefinition Width='1.8*'/></Grid.ColumnDefinitions><Grid ColumnSpacing='10'><Grid.ColumnDefinitions><ColumnDefinition Width='24'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><SymbolIcon Symbol='{Binding Icon}' Width='20'/><StackPanel Grid.Column='1' VerticalAlignment='Center' Spacing='3'><TextBlock Text='{Binding Title}' FontSize='13' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Task}' FontSize='11' Foreground='{ThemeResource TextFillColorSecondaryBrush}' TextTrimming='CharacterEllipsis'/></StackPanel></Grid><StackPanel Grid.Column='1' VerticalAlignment='Center' Spacing='3'><TextBlock Text='{Binding Kind}' FontSize='12' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Facts}' FontSize='11' TextTrimming='CharacterEllipsis' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/></StackPanel><Grid Grid.Column='2' ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><TextBlock Text='{Binding State}' VerticalAlignment='Center' FontSize='12' TextWrapping='Wrap' MaxLines='2' TextTrimming='CharacterEllipsis'/><StackPanel Grid.Column='1' Orientation='Horizontal' Spacing='6' VerticalAlignment='Center'><Button x:Name='RowChat' Content='Code' AutomationProperties.Name='Use for coding' ToolTipService.ToolTip='Open Code with this model selected' Visibility='{Binding ChatVisibility}' Padding='10,5'/><Button x:Name='RowTalk' Content='Chat' AutomationProperties.Name='Open in chat' ToolTipService.ToolTip='Start a chat with this model' Visibility='{Binding ChatVisibility}' Padding='10,5'/></StackPanel></Grid></Grid></Border></DataTemplate>");
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
        var running=new ListView { SelectionMode=ListViewSelectionMode.Single,MaxHeight=340 };
        running.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        running.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Padding='16' Margin='0,4' CornerRadius='8' BorderThickness='1' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'><StackPanel Spacing='6'><TextBlock Text='{Binding Model.Name}' FontSize='17' FontWeight='SemiBold' TextWrapping='Wrap'/><TextBlock Text='{Binding Model.Task}'/><TextBlock Text='{Binding Profile.Endpoint}' FontSize='13' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/></StackPanel></Border></DataTemplate>");
        var serverSummary=Text("Choose a running server to inspect or stop it.",14);
        var serverDetail=new TextBox { Header="Server details",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=140 };
        var locations=new ListView { MaxHeight=340,SelectionMode=ListViewSelectionMode.Single };
        var common=BrandControl(new CheckBox { Content="Include common model folders", IsChecked=settings.IncludeCommon });
        var folders=Text("",12);
        var views=new ViewSwitcher {UseSegments=true};
        var emptyAdd=Button("Add local folder",(_,_)=>openModelLocations?.Invoke());noMatches.Children.Add(Button("Find models",(_,_)=>views.SelectedIndex=3,true));noMatches.Children.Add(emptyAdd);
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
            settings=(settings with { LlamaServer=llama.Text,Python=python.Text,IncludeCommon=common.IsChecked==true,QuantizedPython=quantizedPython.Text }).MergeRuntimeSelection(saved,editedRuntime);
            Store.Write("model-library.json",settings);
            llama.Text=settings.LlamaServer;python.Text=settings.Python;quantizedPython.Text=settings.QuantizedPython;
        }
        void ReloadRuntimePaths()
        {
            if(!readable)return;
            var saved=ModelLibrarySettings.ReadSaved(Path.Combine(Store.Root,"model-library.json"));
            if(saved==null)throw new InvalidOperationException("No saved runtime selection was found at "+Path.Combine(Store.Root,"model-library.json")+". Choose a runtime in Runtime setup before starting this model.");
            settings=settings.MergeRuntimeSelection(saved);
            llama.Text=settings.LlamaServer;python.Text=settings.Python;quantizedPython.Text=settings.QuantizedPython;
            refreshRuntimeInventory?.Invoke();
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
            var selectedTask=task.SelectedItem as string;
            var found=discovered.Where(x=>x.Task=="Chat"&&(task.SelectedIndex==0 || x.Task==selectedTask) && (q.Length==0 || (x.Name+" "+x.Source+" "+x.Path+" "+x.Format+" "+ModelLibraryCard.FriendlyName(x.Name)).Contains(q,StringComparison.OrdinalIgnoreCase)));
            found=sort.SelectedIndex switch { 1=>found.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase),2=>found.OrderByDescending(x=>x.Bytes),3=>found.OrderBy(x=>x.Bytes),_=>found.OrderByDescending(x=>x.Launchable).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase) };
            var cards=found.Select(x=>new ModelLibraryCard(x,State(x))).ToArray();
            foreach(var card in cards)card.Fit=CardFit(card.Model);
            var endpoints=Store.Read<List<AiEndpointProfile>>("ai-connections.json")??[];
            var entries=cards.Select(c=>new LibraryEntry(c,null)).Concat(endpoints.Where(p=>(p.ManagedCapability is null or AiCapability.Chat)&&(q.Length==0||(p.Name+" "+p.Model+" "+p.Endpoint).Contains(q,StringComparison.OrdinalIgnoreCase))&&(task.SelectedIndex==0||p.Checks.Any(c=>c.State=="Passed"&&c.Capability.ToString()==selectedTask))).Select(p=>new LibraryEntry(null,p))).Where(e=>resourceKind.SelectedIndex switch{1=>e.File!=null,2=>e.Connection!=null,_=>true}).ToArray();
            foreach (var entry in entries) entry.ChatProfileId = entry.Connection is {} connection && connection.Checks.Any(c => c.Capability == AiCapability.Chat && c.State == "Passed") ? connection.Id : modelServing.Running.FirstOrDefault(s => s.Owner.IsRunning && s.Model.Task == "Chat" && s.Model.Path == entry.File?.Model.Path && s.Model.AdapterPath == entry.File?.Model.AdapterPath && s.Profile.Checks.Any(c => c.Capability == AiCapability.Chat && c.State == "Passed"))?.Profile.Id;
            list.ItemsSource=entries; list.SelectedItem=entries.FirstOrDefault(x=>x.Key==path)??entries.FirstOrDefault();
            var liveCount=modelServing.Running.Count(x=>x.Owner.IsRunning);
            summary.Text=$"{discovered.Length} local models and files · {discovered.Count(x=>x.Launchable)} can be started here · {liveCount} running";
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
            return model.Launchable?"Stopped · check on start":"Not available for local serving";
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
            contextChoice.Visibility=model?.Task=="Chat"?Visibility.Visible:Visibility.Collapsed;
            if(model==null || !model.Launchable)return;
            var profile=memoryProfiles.GetValueOrDefault(model.Path);
            bool live=modelServing.Running.Any(x=>x.Model.Path==model.Path&&x.Owner.IsRunning);
            if(model.Task=="Chat"&&(!double.IsFinite(contextChoice.Value)||contextChoice.Value<64||contextChoice.Value!=Math.Truncate(contextChoice.Value))){fitTitle.Text="Enter a valid context window";fitDetail.Text="Use a whole number of at least 64 tokens.";return;}
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
            selectedPurpose.Text=connection==null?card?.Purpose??"":string.Join("\n",connection.Checks.GroupBy(c=>c.Capability).Select(g=>g.Last()).Select(c=>c.Capability+" · "+c.State+" · "+c.At.LocalDateTime.ToString("g")));
            if(start!=null) { start.IsEnabled=m?.Launchable==true && aiOperation==null && !modelServing.Running.Any(x=>x.Model.Path==m.Path&&x.Model.AdapterPath==m.AdapterPath&&x.Owner.IsRunning);start.Visibility=m?.Launchable==true?Visibility.Visible:Visibility.Collapsed;start.Content=m!=null&&ModelAdapters.IsQuantized(m)&&!File.Exists(quantizedPython.Text)?"Set up runtime":"Start & connect"; }
            device.Visibility=m?.Launchable==true?Visibility.Visible:Visibility.Collapsed;
            if(m?.Task=="Chat"&&memoryProfiles.TryGetValue(m.Path,out var capacity)){contextChoice.Maximum=capacity.ContextDeclared?capacity.MaxContext:int.MaxValue;if(contextChoice.Value>contextChoice.Maximum)contextChoice.Value=contextChoice.Maximum;}
            UpdateFit();adaptive?.Invoke();
            selectedStatus.Text=m==null?"":State(m);
            selectedRuntime.Text=m?.Launchable==true?(ModelAdapters.IsQuantized(m)?"Runs with GPT-QModel · packed 4-bit weights":ModelAdapters.UsesPython(m)?"Runs with Python · PyTorch + Transformers":"Runs with llama.cpp · GGUF")+(File.Exists(ModelAdapters.IsQuantized(m)?quantizedPython.Text:ModelAdapters.UsesPython(m)?python.Text:llama.Text)?" · runtime selected":" · choose a runtime in Runtime setup"):"";
            launchDetail.Text=m==null?"":m.Launchable?m.Support+"\nStart checks capability and saves a connection. Models run locally.":m.Support;
        }
        async Task Scan()
        {
            if(scan!=null)return; activity.IsOpen=true; using var ct=new CancellationTokenSource();scan=ct;scanProgress.Visibility=Visibility.Visible;if(cancelScan!=null)cancelScan.Visibility=Visibility.Visible;activity.Severity=InfoBarSeverity.Informational;activity.Message="Discovering model files… You can cancel at any time.";
            if(discovered.Length==0){scanSkeleton.Visibility=Visibility.Visible;noMatches.Visibility=Visibility.Collapsed;}
            try
            {
                var roots=(common.IsChecked==true ? ModelDiscovery.CommonLocations() : []).Concat(settings.Locations).Concat((modelDownloads?.Snapshot()??[]).Where(j=>j.State=="Ready in library").Select(j=>new ModelLocation("Downloaded · "+j.Repository,j.Folder))).DistinctBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).ToArray();
                var result=await Task.Run(()=>ModelDiscovery.Scan(roots,ct.Token));
                result=result with{Models=result.Models.Where(x=>x.Task=="Chat").Select(DownloadIdentity).ToArray()};
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
                if(isPython)python.Text=file.Path;else llama.Text=file.Path;Save(isPython?1:0);ShowLocations();runtimeStatus.Text="Runtime path saved. Models remain stopped until you start one.";
            }catch(Exception e){await Notice("Runtime",e.Message);}
        }
        var runtimeSettings=new Expander { Header="Runtime setup",HorizontalAlignment=HorizontalAlignment.Stretch,
            Content=Stack(runtimeStatus,llama,Button("Choose llama-server.exe",async(_,_)=>await PickRuntime(false)),python,Button("Choose Python runtime",async(_,_)=>await PickRuntime(true)),Button("Quantized model setup",(_,_)=>{views.SelectedIndex=2;if(quantizedSetup!=null){quantizedSetup.IsExpanded=true;selectRuntime?.Invoke(2);}})) };
        start=Button("Start & connect",async(_,_)=>
        {
            if((list.SelectedItem as LibraryEntry)?.File is not ModelLibraryCard card || aiOperation!=null)return;var m=card.Model;
            try{ReloadRuntimePaths();}catch(Exception error){await Notice("Model settings","Could not reload saved runtime paths: "+error.Message);return;}
            if(!readable){await Notice("Model settings","Repair the unreadable model settings before starting a server.");return;}
            if(ModelAdapters.IsQuantized(m)) {if(!File.Exists(quantizedPython.Text)){views.SelectedIndex=2;quantizedSetup!.IsExpanded=true;selectRuntime?.Invoke(2);return;}}
            else if(!File.Exists(ModelAdapters.UsesPython(m) ? python.Text : llama.Text)){runtimeSettings.IsExpanded=true;await PickRuntime(ModelAdapters.UsesPython(m));Selection();return;}
            using var ct=new CancellationTokenSource();activity.IsOpen=false;aiOperation=ct;start!.IsEnabled=false;scanProgress.Visibility=Visibility.Visible;if(cancelScan!=null)cancelScan.Visibility=Visibility.Visible;activity.Severity=InfoBarSeverity.Informational;activity.Message="Starting "+card.Title+"… Checking the server before saving its connection.";
            startupCancel.IsEnabled=true;startupAt=DateTimeOffset.UtcNow;startupElapsed.Text="Elapsed · 00:00";startupClock.Start();startupStage=0;foreach(var (label,index) in new[]{"Inspect","Load","Check","Ready"}.Select((label,index)=>(label,index)))startupStages.Label(index,label);startupStages.Set(0);startupHud.Visibility=Visibility.Visible;startupDetail.Text="Inspecting · "+card.Title;startupProgress.Visibility=Visibility.Visible;startupProgress.IsIndeterminate=Fluent.MotionOn;
            startupChatProfile = null; startupChat.Visibility = Visibility.Collapsed;
            device.IsEnabled=false;contextChoice.IsEnabled=false;if(applyRecommendation!=null)applyRecommendation.IsEnabled=false;startupModel=m;Render();Selection();
            try
            {
                // Starting a model is a read of runtime selection, never a settings write.
                var served=await modelServing.Start(m,settings,device.SelectedIndex==0,Path.Combine(AppContext.BaseDirectory,"model-runtime"),ct.Token,message=>{startupDetail.Text=message;activity.Message=card.Title+" · "+message;},new ModelLaunchOptions(Context()),phase=>{startupStage=(int)phase;startupStages.Set(phase==ModelStartupPhase.Ready?4:startupStage);startupDetail.Text=phase+" · "+card.Title;Render();Selection();});
                try
                {
                    var saved=Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [];
                    Store.Write("ai-connections.json",saved.Where(x=>x.Id!=served.Profile.Id).Append(served.Profile).ToList());
                }
                catch { modelServing.Stop(served);throw; }
                aiSessionKeys[served.Profile.Id]="";ProfilesChanged();Render();
                var probe=served.Profile.Checks.Last();activity.Severity=probe.State=="Passed"?InfoBarSeverity.Success:InfoBarSeverity.Warning;
                activity.Message=probe.State=="Passed"?$"{m.Task} ready · {served.Profile.Endpoint}. Connection added; choose it in your workflow.":$"Server started, but {m.Task.ToLowerInvariant()} needs attention: {probe.Detail}";
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
        var browse=new Grid{RowSpacing=10};browse.RowDefinitions.Add(new(){Height=GridLength.Auto});browse.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});browse.RowDefinitions.Add(new(){Height=GridLength.Auto});browse.Children.Add(Stack(searchRow,collectionStatus));Grid.SetRow(browseBody,1);browse.Children.Add(browseBody);Grid.SetRow(startupHud,2);browse.Children.Add(startupHud);
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
        fileOptions=Stack(FormGrid(300,device,contextChoice),start,fitPanel,selectedRuntime,selectedStatus,launchDetail,new Expander { Header="Model files & compatibility",Content=Stack(exactName,modelPath),HorizontalAlignment=HorizontalAlignment.Stretch },runtimeSettings);
        var detailsChat = Button("Use for coding", (_, _) => { if (aiOperation == null && (list.SelectedItem as LibraryEntry)?.ChatProfileId is {} id) openCodeWithModel?.Invoke(id); }, true);
        detailsChat.Visibility = Visibility.Collapsed;
        list.SelectionChanged += (_, _) => detailsChat.Visibility = (list.SelectedItem as LibraryEntry)?.ChatProfileId != null ? Visibility.Visible : Visibility.Collapsed;
        // Chat-capable models can open either workspace with the connection already selected.
        var detailsTalk = Button("Open in chat", (_, _) => { if (aiOperation == null && (list.SelectedItem as LibraryEntry)?.ChatProfileId is {} id) openNativeChat?.Invoke(id); });
        var detailsOpen = Row(detailsChat, detailsTalk);
        detailsOpen.Visibility = Visibility.Collapsed;
        list.SelectionChanged += (_, _) => detailsOpen.Visibility = detailsChat.Visibility;
        var detailContents=Stack(backToLibrary,modelTitle,modelFacts,detailsOpen,connectionEdit,selectedPurpose,fileOptions);
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
        list.SelectionChanged+=(_,_)=>Selection();search.TextChanged+=(_,_)=>Render();task.SelectionChanged+=(_,_)=>Render();sort.SelectionChanged+=(_,_)=>Render();resourceKind.SelectionChanged+=(_,_)=>Render();
        running.SelectionChanged+=(_,_)=> { if(running.SelectedItem is ServedModel s) { serverSummary.Text=$"{s.Model.Task} · {s.Profile.Name}\n{s.Profile.Endpoint} · {(!s.Owner.IsRunning ? "Stopped unexpectedly" : "Running locally")}\n"+string.Join(" · ",s.Profile.Checks.Select(x=>$"{x.Capability}: {x.State}")); serverDetail.Text=$"{s.Model.Name}\n{s.Profile.Endpoint}\nExact model ID: {s.Profile.Model}\nRequested context: {s.Options?.ContextTokens??4096} tokens (model/tokenizer cap also applies)\nPID: {s.Owner.Process.Id}\nRuntime log: {s.Log}\n"+string.Join("\n",s.Profile.Checks.Select(x=>$"{x.Capability}: {x.State} · {x.Detail}")); } };
        var stop=Button("Stop selected model",async(_,_)=> { if(aiOperation!=null)return;if(running.SelectedItem is not ServedModel s)return;try{modelServing.Stop(s);ResetStoppedProfile(s);Render();Selection();activity.IsOpen=true;serverDetail.Text="Server stopped. The connection remains saved; its capability checks were cleared.";activity.Message="Model stopped. Its saved connection is kept for next time.";}catch(Exception e){await Notice("Stop server",e.Message);} });
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
        servingEmpty.Children.Add(Button("Browse your models",(_,_)=>views.SelectedIndex=0,true));
        servingEmpty.Children.Add(Button("Manage external AI connections",(_,_)=>Navigate("AI connections")));
        EmptyState(servingEmpty, MoodReady, "nothing running");
        var runningChat = Button("Use for coding", (_, _) => { if (aiOperation == null && running.SelectedItem is ServedModel s) openCodeWithModel?.Invoke(s.Profile.Id); }, true); runningChat.Visibility = Visibility.Collapsed;
        running.SelectionChanged += (_, _) => runningChat.Visibility = running.SelectedItem is ServedModel s && s.Owner.IsRunning && s.Profile.Checks.Any(c => c.Capability == AiCapability.Chat && c.State == "Passed") ? Visibility.Visible : Visibility.Collapsed;
        var runningTalk = Button("Open in chat", (_, _) => { if (aiOperation == null && running.SelectedItem is ServedModel s) openNativeChat?.Invoke(s.Profile.Id); }); runningTalk.Visibility = Visibility.Collapsed;
        running.SelectionChanged += (_, _) => runningTalk.Visibility = runningChat.Visibility;
        servingControls=Stack(running,serverSummary,Text("Open your workflow with this connection selected. You choose when to send a message.",13),ScrollTools(Row(runningChat,runningTalk,workflow,useModel,stop,Button("Refresh status",(_,_)=>{Render();Selection();}))),new Expander {Header="Diagnostics · model ID, process and logs",Content=serverDetail,HorizontalAlignment=HorizontalAlignment.Stretch});
        var sessionPage=new ScrollViewer { Content=Stack(Text("Running in Mason Agent",20),Text("Models started here stay private to this computer and stop when you close the app. Models served by other apps are managed under Connections.",14),servingEmpty,servingControls),VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
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
        var runtimeNotice=Text("Selected paths are settings. Compatibility is validated when a model starts; setup does not start a model.",12);
        ((StackPanel)quantizedSetup.Content).Children.Remove(quantizedStatus);
        setupCancel=Button("Cancel setup",(_,_)=>aiOperation?.Cancel());setupCancel.IsEnabled=false;
        var setupHud=SurfaceCard(Stack(setupStages.View,setupProgress,quantizedStatus,setupCancel));setupHud.Padding=new Thickness(12);
        runtimeBusy=busy=>{runtimeList.IsEnabled=folderTools.IsEnabled=!busy;setupCancel.IsEnabled=busy;};
        FrameworkElement RuntimePanel(int index){var path=Text("",12);path.SetBinding(TextBlock.TextProperty,new Microsoft.UI.Xaml.Data.Binding{Source=index==0?llama:python,Path=new PropertyPath("Text")});return Stack(Text(index==0?"GGUF serving runtime":"Transformers serving runtime",19),Text(runtimeNotice.Text,12),path,Button(index==0?"Choose llama-server.exe":"Choose Python runtime",async(_,_)=>await PickRuntime(index==1)),new Expander{Header="Supported adapters and conventions",Content=Text("GGUF uses llama-server.exe. Standard text models use local Transformers and PyTorch. A file path does not establish inference capability.",12),HorizontalAlignment=HorizontalAlignment.Stretch});}
        var basePath=new TextBox{Header="Base Python environment",IsReadOnly=true};basePath.SetBinding(TextBox.TextProperty,new Microsoft.UI.Xaml.Data.Binding{Source=python,Path=new PropertyPath("Text")});
        var changeBase=Button("Choose base Python",async(_,_)=>{if(aiOperation!=null)return;await PickRuntime(true);});
        var updateRuntimeBusy=runtimeBusy;runtimeBusy=busy=>{updateRuntimeBusy?.Invoke(busy);changeBase.IsEnabled=!busy;if(busy)quantizedSetup.IsExpanded=false;};
        var quantizedDetails=Stack(FormGrid(basePath,changeBase),quantizedSetup,setupHud);quantizedDetails.Visibility=Visibility.Collapsed;
        var setupScope=Stack(Text("Setup scope",19),Text("Downloads runtime packages into an isolated app-owned environment. Model weights are not downloaded, and the selected base Python environment is retained.",13),Text("Prerequisites",16),Text("Python 3.10–3.13\nCUDA-enabled PyTorch 2.8+\nTransformers 5.5.0\nGPT-QModel 7.0.0",13),Text("Compatibility is checked during setup and when the selected model starts. A saved path is not proof of readiness.",12),Text("When setup finishes",16),Text("Return to Library and choose Start & connect. Setup does not launch a model.",12));
        var runtimePages=new FrameworkElement[]{RuntimePanel(0),RuntimePanel(1),setupScope};var runtimeFrame=new Grid();foreach(var page in runtimePages)runtimeFrame.Children.Add(page);runtimeInspector.Content=runtimeFrame;

        bool refreshingRuntime=false;refreshRuntimeInventory=()=>{int selected=(runtimeList.SelectedItem as ListRow)?.Source is int index?index:0;refreshingRuntime=true;try{runtimeList.ItemsSource=new[]{new ListRow(0,"GGUF · llama.cpp",llama.Text,File.Exists(llama.Text)?"Path selected":"Choose executable"),new ListRow(1,"Transformers · PyTorch",python.Text,File.Exists(python.Text)?"Path selected":"Choose environment"),new ListRow(2,"Quantized · GPTQ / AWQ",quantizedPython.Text,File.Exists(quantizedPython.Text)?"Path selected":"Setup available")};runtimeList.SelectedIndex=selected;}finally{refreshingRuntime=false;}};
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
        var pages=new FrameworkElement[]{library,sessionPage,locationPage,acquisition.Discover,acquisition.Downloads}.Select(page=>new ContentControl{Content=page,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch}).ToArray();var host=new Grid();foreach(var p in pages)host.Children.Add(p);
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
        foreach(var label in new[]{"Library","Running","Setup","Discover","Downloads"})views.Items.Add(new ViewOption{Header=label});
        void Show(){if(libraryTop!=null)libraryTop.Visibility=views.SelectedIndex is 3 or 4?Visibility.Collapsed:Visibility.Visible;for(int i=0;i<pages.Length;i++){pages[i].Visibility=views.SelectedIndex==i?Visibility.Visible:Visibility.Collapsed;pages[i].IsEnabled=true;}locationOverlay.Visibility=Visibility.Collapsed;activity.Visibility=views.SelectedIndex is 3 or 4?Visibility.Collapsed:Visibility.Visible;}
        views.SelectionChanged+=(_,_)=>Show();Show();ShowLocations();Render();Selection();
        selectModelView=index=>views.SelectedIndex=index;
        common.Checked+=async(_,_)=>{try{Save();}catch(Exception e){await Notice("Model locations",e.Message);}};
        common.Unchecked+=async(_,_)=>{try{Save();}catch(Exception e){await Notice("Model locations",e.Message);}};
        refreshModelLibrary=()=>{Render();Selection();};
        cancelScan=Button("Cancel operation",(_,_)=>{scan?.Cancel();aiOperation?.Cancel();});cancelScan.Visibility=Visibility.Collapsed;
        var headerActions=ScrollTools(Row(Button("Scan library",async(_,_)=>await Scan()),folderTools,Button("Find models",(_,_)=>views.SelectedIndex=3),cancelScan));
        var header=new Grid {ColumnSpacing=12};header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        // Header: the page name with the collection summary under it; collection actions on the right.
        summary.Opacity = .8; summary.MaxLines = 2; summary.TextTrimming = TextTrimming.CharacterEllipsis;
        var titleBlock = Stack(title, summary); titleBlock.Spacing = 2; titleBlock.VerticalAlignment = VerticalAlignment.Center;
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
        // GPU strip: one line (adapter, live reading, details) with the memory meter under it.
        var gpuHeader = new Grid { ColumnSpacing = 14 };
        gpuHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        gpuHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        gpuHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        gpuName.VerticalAlignment = gpuStats.VerticalAlignment = VerticalAlignment.Center;
        gpuStats.Opacity = .8; gpuStats.MaxLines = 2; gpuStats.TextTrimming = TextTrimming.CharacterEllipsis;
        gpuStats.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => ToolTipService.SetToolTip(gpuStats, gpuStats.Text));
        gpuHeader.Children.Add(gpuName);
        Grid.SetColumn(gpuStats, 1); gpuHeader.Children.Add(gpuStats);
        Grid.SetColumn(gpuDetailsButton, 2); gpuHeader.Children.Add(gpuDetailsButton);
        var gpuBody = Stack(gpuHeader, gpuMemory); gpuBody.Spacing = 6;
        var gpuPanel = Card(gpuBody);
        gpuPanel.Padding = new Thickness(14, 6, 8, 8);
        // The view tabs lead and stay visible on every view, so Discover and Downloads always have a way
        // back. The intro (name, summary, GPU, notices) belongs to Library, Running and Setup.
        var intro = Stack(header, gpuPanel, scanProgress, activity);
        var top = Stack(views, intro);
        libraryTop=intro;Show();
        var layout=Vertical(top,host);Show();
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
        layout.Loaded+=async(_,_)=>{if(scanned)return;scanned=true;await Scan();};
        root.Unloaded+=(_,_)=>{scan?.Cancel();modelServing.Dispose();};
        return layout;
    }
}




