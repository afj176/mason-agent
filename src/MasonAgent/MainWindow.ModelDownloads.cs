using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Mason;

public sealed partial class MainWindow
{
    ModelDownloads? modelDownloads;
    HubClient? modelHub;
    DownloadSettings downloadSettings = new(Path.Combine(Store.Root,"Models"));
    readonly ContentControl downloadTray = new() { HorizontalContentAlignment=HorizontalAlignment.Stretch,Visibility=Visibility.Collapsed };
    Action? openModelLocations, refreshDownloadStorage;
    Action<Guid>? openModelDownload;
    Func<ModelDownload,bool,Task>? registerDownloadedModel;
    bool downloadsClosing, downloadsClosed;
    string hubToken="";

    static void Accessible(DependencyObject control,string name)=>AutomationProperties.SetName(control,name);
    static TextBlock DownloadText(string value,int size=13)
    {
        var text=Text(value,size);if(size>=14)text.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;text.MaxLines=2;text.TextTrimming=TextTrimming.CharacterEllipsis;return text;
    }
    static FrameworkElement HubIdentity(string title,string subtitle)
    {
        var logo=new Image{Source=new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri("ms-appx:///Assets/huggingface.svg")),Width=25,Height=25,VerticalAlignment=VerticalAlignment.Top};Accessible(logo,"Hugging Face");
        var grid=new Grid{ColumnSpacing=12};grid.ColumnDefinitions.Add(new(){Width=new GridLength(25)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.Children.Add(logo);
        var labels=Stack(DownloadText(title,14),DownloadText(subtitle,11));labels.Spacing=3;Grid.SetColumn(labels,1);grid.Children.Add(labels);return grid;
    }
    static string StorageSpace(string folder)
    {
        try {var drive=new DriveInfo(Path.GetPathRoot(folder)!);return drive.IsReady?HubFormat.Size(drive.AvailableFreeSpace)+" free on "+drive.Name:"Drive unavailable";}
        catch{return "Free space unavailable";}
    }
    FrameworkElement DownloadStorage()
    {
        var path=new TextBox{Header="Default download folder",IsReadOnly=true,TextWrapping=TextWrapping.Wrap};
        var space=Text("",12);
        void Refresh(){path.Text=downloadSettings.Folder;space.Text=StorageSpace(path.Text);}
        refreshDownloadStorage+=Refresh;Refresh();
        return Stack(Text("Download storage",20),path,space,Button("Change download folder",async(_,_)=>
        {
            try
            {
                var folder=await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();if(folder==null)return;
                HubFormat.NoLinks(folder.Path);
                var next=downloadSettings with{Folder=Path.GetFullPath(folder.Path)};
                Store.Write("model-download-settings.json",next);downloadSettings=next;refreshDownloadStorage?.Invoke();
            }
            catch(Exception error){await Notice("Download storage",error.Message);}
        }),Text("Applies to future downloads. Existing models and captured download destinations stay where they are.",12));
    }
    async Task HubCredentials()
    {
        var token=new PasswordBox{Header="Hugging Face read token",PlaceholderText=hubToken.Length>0?"A token is saved · enter to replace":"hf_…"};
        var dialog=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Hugging Face access",PrimaryButtonText="Save token",SecondaryButtonText="Remove token",CloseButtonText="Close",Content=Stack(Text("Public repositories do not require a token. For gated models, accept access terms on the model page first.",14),token,Text("Stored with Windows user encryption. Used only for Hugging Face acquisition, separately from inference connection keys.",12))};
        var result=await dialog.ShowAsync();if(result==ContentDialogResult.None)return;
        try
        {
            var secret=result==ContentDialogResult.Secondary?"":token.Password.Trim();
            if(result==ContentDialogResult.Primary&&!System.Text.RegularExpressions.Regex.IsMatch(secret,"^hf_[A-Za-z0-9]+$"))throw new ArgumentException("Enter a Hugging Face token beginning with hf_.");
            var next=downloadSettings with{ProtectedToken=secret.Length==0?"":Store.Protect(secret)};Store.Write("model-download-settings.json",next);downloadSettings=next;hubToken=secret;
        }
        catch(Exception error){await Notice("Hugging Face access",error.Message);}
        finally{token.Password="";}
    }
    (FrameworkElement Discover,FrameworkElement Downloads) BuildModelAcquisition()
    {
        try
        {
            downloadSettings=Store.Read<DownloadSettings>("model-download-settings.json")??new(Path.Combine(Store.Root,"Models"));
            if(!Path.IsPathFullyQualified(downloadSettings.Folder))throw new InvalidDataException("Download folder must be an absolute path.");
            hubToken=Store.Unprotect(downloadSettings.ProtectedToken);
            modelHub=new(()=>hubToken);
            modelDownloads=new(Store.Root,modelHub,ModelDiscovery.CommonLocations().Where(x=>x.Name.StartsWith("Hugging Face")).Select(x=>x.Path));
        }
        catch(Exception error)
        {
            modelHub?.Dispose();modelHub=null;
            FrameworkElement Unavailable()=>TroubleNotice("Downloads unavailable","Another Mason Agent instance may own the queue, or saved download settings need repair. Existing files and settings have been preserved.",error.Message);
            return(Unavailable(),Unavailable());
        }
        var service=modelDownloads;var hub=modelHub;
        var downloads=new ListView{SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};Accessible(downloads,"Model downloads");
        var downloadTable=ListTable(downloads,("Model","",2.1),("State and progress","",2),("Actions","",1));downloads.ItemTemplate=null;
        var count=Text("No downloads yet",13);var query=new TextBox{PlaceholderText="Search downloads…"};Accessible(query,"Search downloads");
        var statusFilter=new ComboBox{ItemsSource=new[]{"All statuses","Downloading","Queued","Verifying files","Paused","Needs attention","Ready in library","Cancelled"},SelectedIndex=0,MinWidth=155};Accessible(statusFilter,"Filter downloads by status");
        var empty=Stack(Text("Your downloads will appear here",21),Text("Find a model in Discover, choose its files, then review the download.",14),Button("Find models",(_,_)=>selectModelView?.Invoke(3),true));
        EmptyState(empty, MoodReady, "no downloads yet");
        var downloadBody=new Grid();downloadBody.Children.Add(downloadTable);downloadBody.Children.Add(empty);
        var dTitle=Text("Select a download",20);var dRepo=Text("Progress, verified files and destination appear here.",13);
        var stages=StageRail("Download","Verify","Ready");var dState=Text("",23);var dBytes=Text("",13);var dRate=Text("",12);var dDetail=Text("",12);
        dTitle.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;dState.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
        var dProgress=BrandControl(new ProgressBar{Minimum=0,Maximum=100,Height=6});Accessible(dProgress,"Selected download progress");
        var dFiles=new TextBox{Header="Files",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=150};
        var dPath=new TextBox{Header="Destination folder",IsReadOnly=true,TextWrapping=TextWrapping.Wrap};
        var dFacts=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap};
        Guid? selected=null;var rowControls=new Dictionary<Guid,(ListViewItem Item,TextBlock State,TextBlock Detail,ProgressBar Progress,Button Action)>();
        async Task Act(ModelDownload job)
        {
            try
            {
                if(job.State=="Ready in library"){if(registerDownloadedModel!=null)await registerDownloadedModel(job,true);}
                else if(job.State is "Paused" or "Needs attention")service.Resume(job.Id);
                else if(job.State is "Downloading" or "Queued" or "Verifying files")service.Pause(job.Id);
                else if(job.State=="Cancelled")selectModelView?.Invoke(3);
            }
            catch(Exception error){await Notice("Download",error.Message);}
        }
        static string ActionLabel(ModelDownload j)=>j.State switch{"Paused"=>"Resume","Needs attention"=>"Retry","Ready in library"=>"View in library","Cancelled"=>"Find models","Pausing"=>"Pausing…",_=>"Pause"};
        ModelDownload? Selected()=>service.Snapshot().FirstOrDefault(j=>j.Id==selected);
        var dAction=Button("Pause",async(_,_)=>{if(Selected() is {} job)await Act(job);},true);
        var cancel=Button("Cancel download",async(_,_)=>{if(Selected() is {} job)try{service.Pause(job.Id,true);}catch(Exception error){await Notice("Download",error.Message);}});
        var modelCard=Button("View model card ↗",async(_,_)=>{if(Selected() is {} job)await Windows.System.Launcher.LaunchUriAsync(HubClient.ModelPage(job.Repository));});
        var selectedContent=Stack(dTitle,dRepo,modelCard,stages.View,dState,dProgress,dBytes,dRate,FormGrid(170,dAction,cancel),dDetail,dFiles,dPath,new Expander{Header="Details",Content=dFacts,HorizontalAlignment=HorizontalAlignment.Stretch},Text("Models remain stopped until you choose Start & connect in Library.",12));
        var downloadsPage=InspectorLayout(Vertical(Stack(Toolbar(Text("Downloads",20),Button("Model locations",(_,_)=>openModelLocations?.Invoke())),count,Toolbar(query,statusFilter)),downloadBody),ScrollPage(selectedContent),"Selected download");
        void Detail(ModelDownload? j)
        {
            foreach(var control in new FrameworkElement[]{modelCard,stages.View,dState,dProgress,dBytes,dRate,dAction,cancel,dDetail,dFiles,dPath})control.Visibility=j==null?Visibility.Collapsed:Visibility.Visible;
            if(j==null)return;
            dTitle.Text=j.Name;dRepo.Text=j.Repository+" · "+j.Variant;
            dState.Text=j.State=="Downloading"?j.Percent.ToString("0")+"%":j.State;
            dState.Foreground=DownloadStateBrush(j.State,dState.ActualTheme);dBytes.Text=j.State=="Ready in library"?$"{j.Files.Length} {(j.Files.Length==1?"file":"files")} verified · "+HubFormat.Size(j.Total):j.ProgressText;dRate.Text=j.RateText;dRate.Visibility=j.RateText.Length==0?Visibility.Collapsed:Visibility.Visible;dProgress.Visibility=j.Terminal?Visibility.Collapsed:Visibility.Visible;dProgress.Value=j.State=="Verifying files"?j.VerifiedFiles*100d/j.Files.Length:j.Percent;
            Accessible(dProgress,j.State+" · "+j.ProgressText);stages.Set(j.State=="Ready in library"?3:j.State=="Verifying files"?1:0);
            dAction.Content=ActionLabel(j);dAction.IsEnabled=j.State!="Pausing";cancel.Visibility=j.Terminal?Visibility.Collapsed:Visibility.Visible;cancel.IsEnabled=j.State!="Pausing";
            dDetail.Text=j.Detail+(j.State is "Paused" or "Cancelled"?"\nRetained partial files: "+j.Staging:"");dPath.Text=j.Folder;
            long preceding=0;var lines=new List<string>();for(int i=0;i<j.Files.Length;i++){var f=j.Files[i];long bytes=Math.Clamp(j.Received-preceding,0,f.Size);preceding+=f.Size;lines.Add(f.Path+"\n"+(i<j.VerifiedFiles?"Verified · ":"")+HubFormat.Size(bytes)+" / "+HubFormat.Size(f.Size));}
            dFiles.Text=string.Join("\n\n",lines);dFacts.Text="Source: Hugging Face\nPublisher: "+j.Repository.Split('/')[0]+"\nLicense: "+j.License+"\nPinned commit: "+j.Revision+"\nCreated: "+j.Created.LocalDateTime.ToString("g")+(j.Completed is {} at?"\nVerified: "+at.LocalDateTime.ToString("g"):"")+"\nIntegrity: SHA-256 for LFS; Git blob SHA-1 for repository files.";
        }
        downloads.SelectionChanged+=(_,_)=>{selected=(downloads.SelectedItem as ListViewItem)?.Tag as Guid?;Detail(Selected());};
        openModelDownload=id=>{Navigate("Models and connections");selectModelView?.Invoke(4);query.Text="";statusFilter.SelectedIndex=0;selected=id;if(rowControls.TryGetValue(id,out var row)){downloads.SelectedItem=row.Item;downloads.ScrollIntoView(row.Item);}Detail(Selected());};

        // Discovery remains mounted while downloads and other work continue. Every result is shown;
        // each one says which agent job it can do and what, if anything, is missing to run it here.
        var search=new TextBox{PlaceholderText="Search by name, owner/model or paste a Hugging Face link…",MinWidth=180};Accessible(search,"Search Hugging Face models");
        var publisher=new ComboBox{Header="Publisher",ItemsSource=new[]{"All publishers"}.Concat(HubClient.Publishers).ToArray(),SelectedIndex=0,MinWidth=145};
        var format=new ComboBox{Header="Format",ItemsSource=new[]{"All formats","GGUF","Safetensors"},SelectedIndex=0,MinWidth=130};
        var roleFilters=new (string Label,AgentRole[] Roles)[]{("All models",[]),("Coding agent",[AgentRole.Implementer,AgentRole.Vision]),("Vision",[AgentRole.Vision]),("Code search",[AgentRole.CodeSearch,AgentRole.Reranker]),("Draft models",[AgentRole.Draft]),("Adapters",[AgentRole.Adapter]),("Not for the agent",[AgentRole.NotUsed,AgentRole.Unknown])};
        var roleFilter=new ComboBox{Header="Agent use",ItemsSource=roleFilters.Select(r=>r.Label).ToArray(),SelectedIndex=0,MinWidth=145};Accessible(roleFilter,"Filter results by agent use");
        var sorts=new (string Label,string Key)[]{("Most downloaded","downloads"),("Trending","trendingScore"),("Most liked","likes"),("Recently updated","lastModified")};
        var sort=new ComboBox{Header="Sort",ItemsSource=sorts.Select(x=>x.Label).ToArray(),SelectedIndex=0,MinWidth=165};
        var found=new ListView{SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};Accessible(found,"Hugging Face search results");
        var catalog=ListTable(found,("Model","",2.1),("Agent use","",1.1),("Runs here?","",1.7),("Popularity","",1));found.ItemTemplate=null;
        var resultStatus=Text("Loading what's trending for agent work…",13);
        var cTitle=Text("Find your next model",20);cTitle.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;var cRepo=Text("Select a repository to inspect its downloadable files.",13);var cFacts=Text("",13);var cNote=Text("",12);
        var cVerdictTitle=Text("",15);cVerdictTitle.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;var cVerdictText=Text("",12);var cFit=Text("",13);cFit.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
        var variants=new ComboBox{Header="Variant / files",HorizontalAlignment=HorizontalAlignment.Stretch};var cPath=new TextBox{Header="Download storage",IsReadOnly=true,TextWrapping=TextWrapping.Wrap};
        var fixedVariant=new TextBox{Header="Selected variant",IsReadOnly=true,TextWrapping=TextWrapping.Wrap};
        var cState=Text("",20);var cBytes=Text("",12);var cProgress=BrandControl(new ProgressBar{Minimum=0,Maximum=100,Height=6});Accessible(cProgress,"Catalog download progress");
        var card=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=420,FontSize=12};Accessible(card,"Model card text");
        var cardExpander=new Expander{Header="Model card",Content=card,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        HubRepository? inspected=null;HubModel? model=null;CancellationTokenSource? searchCancel=null,inspectCancel=null;Guid? pinned=null;HubModel[] results=[];GpuSample? hubGpu=null;HubVariant? recommended=null;
        var catalogStatus=new Dictionary<string,TextBlock>();
        Func<Task> runSearch=()=>Task.CompletedTask;Action renderResults=()=>{},showVerdict=()=>{},refreshNow=()=>{};
        double SystemGiB()=>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes/1073741824d;
        GpuReading? Gpu()=>hubGpu?.Adapters.Length==1?hubGpu.Adapters[0]:null;
        string FitFor(HubVariant v)=>ModelCompatibility.Fit(v.Bytes,Gpu()?.TotalMiB/1024,Gpu()?.FreeMiB/1024,SystemGiB());
        var source=Button("View model card ↗",async(_,_)=>{if(model!=null)await Windows.System.Launcher.LaunchUriAsync(HubClient.ModelPage(model.Id));});
        var verdictAction=Button("",async(_,_)=>
        {
            if(model==null)return;
            var verdict=ModelCompatibility.Assess(model,runtimeFacts);
            if(verdict.Readiness is ModelReadiness.NeedsRuntime or ModelReadiness.NeedsRuntimeUpdate||variants.SelectedItem is HubVariant{Format:"GGUF"}&&RuntimeBlocks()){selectModelView?.Invoke(2);return;}
            if(verdict.Readiness==ModelReadiness.UseGgufBuild){search.Text=model.Id;format.SelectedIndex=1;await runSearch();}
        });
        bool RuntimeBlocks()=>model!=null&&model.Format=="GGUF"&&runtimeFacts.Loads(model.Architecture)==false;
        var cPrimary=Button("Review download",async(_,_)=>
        {
            if(pinned is {} id){openModelDownload?.Invoke(id);return;}
            if(inspected is not {} repo||variants.SelectedItem is not HubVariant variant)return;
            if(service.Snapshot().FirstOrDefault(j=>j.Repository==repo.Id&&j.Revision==repo.Revision&&j.Variant==variant.Name&&j.State!="Cancelled") is {} existing){pinned=existing.Id;openModelDownload?.Invoke(existing.Id);return;}
            var destination=downloadSettings.Folder;
            var reviewedId=Guid.NewGuid();var resolved=ModelDownload.FolderFor(destination,reviewedId);
            var verdict=model==null?null:ModelCompatibility.Assess(model,runtimeFacts);
            var preview=Stack(Text(repo.Id,18),Text(variant.Name,14),Text(HubFormat.Size(variant.Bytes)+" · "+variant.Files.Length+(variant.Files.Length==1?" file":" files")+(variant.Quality.Length>0?" · "+variant.Quality:""),14),Text(FitFor(variant)+" · estimate",13),Text("Agent use: "+ModelVerdict.Label(variant.Role)+(verdict!=null?" · "+verdict.Badge:""),13),Text("License: "+repo.License,13),Text(variant.Note,13),Text("Destination: "+resolved,13),Text(StorageSpace(destination),12),Text("Pinned revision: "+repo.Revision,12),new Expander{Header="Review files",Content=Text(string.Join("\n",variant.Files.Select(f=>f.Path+" · "+HubFormat.Size(f.Size))),12),HorizontalAlignment=HorizontalAlignment.Stretch},Text("Files are downloaded and verified before appearing in My models. The model stays stopped.",13));
            var dialog=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Review download",Content=ScrollPage(preview),PrimaryButtonText="Download model",CloseButtonText="Back",DefaultButton=ContentDialogButton.Primary};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            try{var job=service.Enqueue(repo,variant,destination,reviewedId);pinned=job.Id;}
            catch(Exception error){await Notice("Download",error.Message);}
        },true);
        var cPause=Button("Pause",async(_,_)=>{if(service.Snapshot().FirstOrDefault(j=>j.Id==pinned) is {} job)await Act(job);});
        var another=Button("Choose another variant",(_,_)=>{pinned=null;variants.IsEnabled=true;});
        var storage=Button("Change download storage",(_,_)=>openModelLocations?.Invoke());
        var access=Button("Hugging Face access",async(_,_)=>await HubCredentials());
        var cStages=StageRail("Download","Verify","Ready");
        var verdictBlock=Stack(cVerdictTitle,cVerdictText,verdictAction);
        var inspectBody=Stack(cTitle,cRepo,verdictBlock,source,cFacts,variants,cFit,fixedVariant,cPath,storage,cNote,cStages.View,cState,cProgress,cBytes,FormGrid(170,cPrimary,cPause),another,cardExpander,access);
        search.Header="Search models";search.VerticalAlignment=VerticalAlignment.Bottom;publisher.HorizontalAlignment=format.HorizontalAlignment=roleFilter.HorizontalAlignment=sort.HorizontalAlignment=HorizontalAlignment.Stretch;
        var searchButton=Button("Search",async(_,_)=>await Search(),true);searchButton.VerticalAlignment=VerticalAlignment.Bottom;
        var searchTools=new Grid{ColumnSpacing=12,RowSpacing=10};foreach(var control in new FrameworkElement[]{search,roleFilter,format,publisher,sort,searchButton})searchTools.Children.Add(control);
        void SearchLayout()
        {
            bool compact=searchTools.ActualWidth<980;searchTools.ColumnDefinitions.Clear();searchTools.RowDefinitions.Clear();
            int columns=compact?3:6;
            foreach(var width in compact?new[]{new GridLength(1,GridUnitType.Star),new GridLength(1,GridUnitType.Star),new GridLength(1,GridUnitType.Star)}:new[]{new GridLength(1,GridUnitType.Star),new GridLength(145),new GridLength(130),new GridLength(150),new GridLength(165),GridLength.Auto})searchTools.ColumnDefinitions.Add(new(){Width=width});
            for(int r=0;r<(compact?2:1);r++)searchTools.RowDefinitions.Add(new(){Height=GridLength.Auto});
            int i=0;foreach(FrameworkElement child in searchTools.Children){Grid.SetRow(child,i/columns);Grid.SetColumn(child,i%columns);i++;}
        }
        searchTools.SizeChanged+=(_,_)=>SearchLayout();SearchLayout();
        var discoverPage=InspectorLayout(Vertical(Stack(searchTools,resultStatus),catalog),ScrollPage(inspectBody),"Selected model");
        void ShowVerdict()
        {
            if(model==null){verdictBlock.Visibility=Visibility.Collapsed;return;}
            verdictBlock.Visibility=Visibility.Visible;
            var verdict=ModelCompatibility.Assess(model,runtimeFacts);
            if(inspected!=null&&verdict.Readiness==ModelReadiness.CheckFiles&&inspected.Variants.Length>0)verdict=verdict with{Readiness=ModelReadiness.Ready,Reason="Downloadable files were found below."};
            cVerdictTitle.Text=verdict.RoleLabel+" · "+verdict.Badge;
            cVerdictText.Text=verdict.Reason+(verdict.Action.Length>0?"\n"+verdict.Action:"")+(verdict.Role is not (AgentRole.NotUsed or AgentRole.Unknown)?"\nJobs: "+ModelVerdict.Jobs(verdict.Role):"");
            verdictAction.Content=verdict.Readiness switch{ModelReadiness.NeedsRuntimeUpdate=>"Update llama.cpp",ModelReadiness.NeedsRuntime=>"Set up runtime",ModelReadiness.UseGgufBuild=>"Show GGUF builds",_=>""};
            verdictAction.Visibility=verdictAction.Content is string{Length:>0}?Visibility.Visible:Visibility.Collapsed;
        }
        runSearch=Search;renderResults=RenderResults;showVerdict=ShowVerdict;
        runtimeFactsChanged+=()=>{showVerdict();renderResults();};
        async Task Inspect(HubModel entry)
        {
            inspectCancel?.Cancel();inspectCancel=new();var ct=inspectCancel.Token;model=entry;inspected=null;pinned=null;variants.ItemsSource=null;recommended=null;card.Text="";cardExpander.IsExpanded=false;cFit.Text="";
            cTitle.Text=entry.Name;cRepo.Text=entry.Id;ShowVerdict();
            cFacts.Text="Publisher: "+entry.Publisher+" · "+entry.Signals+(entry.Size.Length>0?"\nSize: "+entry.Size+" parameters":"")+(entry.Architecture.Length>0?"\nArchitecture: "+entry.Architecture:"")+"\nInputs: "+entry.Inputs;cNote.Text="Reading pinned files and integrity metadata…";
            try
            {
                var gpu=GpuTelemetry.Read(ct);
                var repo=await hub.Inspect(entry.Id,ct);if(ct.IsCancellationRequested)return;inspected=repo;
                try{hubGpu=await gpu;}catch(Exception e) when(e is not OperationCanceledException){hubGpu=null;}
                // Only recommend files Mason can run for the agent here.
                var runnable=ModelCompatibility.Assess(entry,runtimeFacts).Readiness is not (ModelReadiness.UseGgufBuild or ModelReadiness.NotSupported);
                recommended=runnable?ModelCompatibility.Recommend(repo.Variants,Gpu()?.TotalMiB/1024,SystemGiB()):null;
                variants.ItemsSource=repo.Variants.Select(v=>v==recommended?v with{Note="Recommended for this PC. "+v.Note}:v).ToArray();
                variants.SelectedIndex=recommended==null?(repo.Variants.Length>0?0:-1):Array.IndexOf(repo.Variants,recommended);
                pinned=service.Snapshot().LastOrDefault(j=>j.Repository==repo.Id&&j.State!="Cancelled")?.Id;
                cFacts.Text+="\nLicense: "+repo.License+(repo.Gated?" · Access terms may apply":"");cNote.Text=repo.Variants.Length==0?"No complete GGUF or supported Safetensors bundle was found. Review the model card for other formats.":"";
                ShowVerdict();
            }
            catch(OperationCanceledException){if(!ct.IsCancellationRequested)cNote.Text="Request timed out. Select the repository again to retry.";}
            catch(Exception error){if(!ct.IsCancellationRequested)cNote.Text=error is IOException?error.Message:"Could not read this repository. Check access and retry.";}
        }
        cardExpander.Expanding+=async(_,_)=>
        {
            if(card.Text.Length>0||inspected is not {} repo||model==null)return;
            card.Text="Loading the model card…";
            try{card.Text=await hub.Readme(repo.Id,repo.Revision,inspectCancel?.Token??default);if(card.Text.Length==0)card.Text="This repository has an empty model card.";}
            catch(Exception error){card.Text=error is IOException?"No model card: "+error.Message:"The model card could not be read.";}
        };
        variants.SelectionChanged+=(_,_)=>{if(variants.SelectedItem is HubVariant v)cFit.Text=(v.Name==recommended?.Name?"Recommended · ":"")+FitFor(v)+(Gpu() is {} g?$" · {g.Name}, {g.TotalMiB/1024:0} GiB":" · no GPU reading")+" · estimate";};
        found.SelectionChanged+=async(_,_)=>{if((found.SelectedItem as ListViewItem)?.Tag is HubModel entry)await Inspect(entry);};
        void RenderResults()
        {
            var roles=roleFilters[Math.Max(0,roleFilter.SelectedIndex)].Roles;
            var shown=results.Where(r=>roles.Length==0||roles.Contains(r.Role)).ToArray();
            var selectedId=((found.SelectedItem as ListViewItem)?.Tag as HubModel)?.Id;
            found.Items.Clear();catalogStatus.Clear();
            foreach(var entry in shown)
            {
                var verdict=ModelCompatibility.Assess(entry,runtimeFacts);
                var local=DownloadText("",11);catalogStatus[entry.Id]=local;
                var reason=DownloadText(verdict.Reason,11);reason.Opacity=.8;var compat=Stack(Pill(verdict.Badge,ModelTones.For(verdict.Readiness,verdict.Role)),reason,local);compat.Spacing=4;
                var signals=DownloadText(entry.Signals,11);signals.Opacity=.8;
                var item=new ListViewItem{Tag=entry,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(0),Content=DownloadColumns([HubIdentity(entry.Name,entry.Publisher+(entry.Size.Length>0?" · "+entry.Size:"")+" · "+entry.Format),Pill(verdict.RoleLabel,ModelTones.For(verdict.Role)),compat,signals],[2.1,1.1,1.7,1])};
                Accessible(item,entry.Id+" · "+verdict.RoleLabel+" · "+verdict.Summary);found.Items.Add(item);
            }
            var counts=results.GroupBy(r=>r.Role switch{AgentRole.Implementer or AgentRole.Vision=>"coding agent",AgentRole.CodeSearch or AgentRole.Reranker=>"code search",AgentRole.Draft=>"draft",AgentRole.Adapter=>"adapter",_=>"not for the agent"}).OrderByDescending(g=>g.Count()).Select(g=>g.Count()+" "+g.Key);
            if(results.Length>0)resultStatus.Text=$"{shown.Length} of {results.Length} shown · "+string.Join(" · ",counts)+(roles.Length>0?" · change Agent use to see the rest":"");
            var keep=found.Items.OfType<ListViewItem>().FirstOrDefault(i=>(i.Tag as HubModel)?.Id==selectedId);
            if(keep!=null)found.SelectedItem=keep;else if(found.Items.Count>0)found.SelectedIndex=0;
            refreshNow();
        }
        async Task Search()
        {
            searchCancel?.Cancel();searchCancel=new();var ct=searchCancel.Token;resultStatus.Text="Searching Hugging Face…";
            try
            {
                var q=search.Text.Trim();var sortKey=sorts[Math.Max(0,sort.SelectedIndex)].Key;
                results=q.Length==0&&publisher.SelectedIndex==0&&format.SelectedIndex==0?await Landing(ct):await hub.Search(q,publisher.SelectedItem as string??"",format.SelectedItem as string??"",ct,sortKey);
                if(ct.IsCancellationRequested)return;
                inspectCancel?.Cancel();inspected=null;model=null;pinned=null;variants.ItemsSource=null;cTitle.Text="Choose a model";cRepo.Text="Select a repository to inspect its files.";cFacts.Text=cNote.Text=cFit.Text="";ShowVerdict();
                if(results.Length==0){found.Items.Clear();resultStatus.Text="Hugging Face returned no repositories for this search. Try fewer words, an owner/model ID, or a pasted link.";return;}
                RenderResults();
                if(q.Length==0)resultStatus.Text="Trending for agent work: coding, vision, embeddings and rerankers · "+resultStatus.Text;
            }
            catch(OperationCanceledException){if(!ct.IsCancellationRequested)resultStatus.Text="Search timed out. Try again.";}
            catch(Exception error){if(!ct.IsCancellationRequested)resultStatus.Text=error is IOException?error.Message:"Search unavailable. Check your connection and try again.";}
        }
        // Landing: trending models for each agent job, interleaved so every job is visible.
        async Task<HubModel[]> Landing(CancellationToken ct)
        {
            var lists=await Task.WhenAll(new[]{"text-generation","image-text-to-text","feature-extraction","text-ranking"}.Select(tag=>hub.Trending(tag,ct)));
            var merged=new List<HubModel>();
            for(int i=0;i<24;i++)foreach(var list in lists)if(i<list.Length&&(i<12||list==lists[0]||list==lists[1]))merged.Add(list[i]);
            return merged.DistinctBy(m=>m.Id).ToArray();
        }
        findOnHub=q=>{Navigate("Models and connections");selectModelView?.Invoke(3);search.Text=q;_=Search();};
        search.KeyDown+=async(_,e)=>{if(e.Key==Windows.System.VirtualKey.Enter){e.Handled=true;await Search();}};
        publisher.SelectionChanged+=async(_,_)=>await Search();format.SelectionChanged+=async(_,_)=>await Search();sort.SelectionChanged+=async(_,_)=>await Search();
        roleFilter.SelectionChanged+=(_,_)=>RenderResults();
        discoverRole=index=>roleFilter.SelectedIndex=Math.Clamp(index,0,roleFilters.Length-1);
        bool landed=false;discoverPage.Loaded+=async(_,_)=>{if(landed)return;landed=true;await Search();};
        var trayText=DownloadText("",12);var trayProgress=BrandControl(new ProgressBar{Minimum=0,Maximum=100,Width=110,Height=4,VerticalAlignment=VerticalAlignment.Center});
        var trayPause=Button("Pause",async(_,_)=>{if(service.Snapshot().FirstOrDefault(j=>j.Active) is {} job)await Act(job);});
        downloadTray.Content=Toolbar(trayText,trayProgress,trayPause,Button("View downloads",(_,_)=>{Navigate("Models and connections");selectModelView?.Invoke(4);}));downloadTray.Margin=new Thickness(0);
        var registered=new HashSet<Guid>();var registering=new HashSet<Guid>();
        async void Register(ModelDownload job)
        {
            if(registerDownloadedModel==null||!registering.Add(job.Id))return;
            try{await registerDownloadedModel(job,false);registered.Add(job.Id);}
            catch(Exception error){status.Text="Library registration needs attention · "+error.Message;}
            finally{registering.Remove(job.Id);}
        }
        void Refresh()
        {
            var jobs=service.Snapshot();count.Text=jobs.Length==0?"No downloads yet":string.Join(" · ",jobs.GroupBy(j=>j.State).Select(g=>g.Count()+" "+g.Key.ToLowerInvariant()));
            foreach(var j in jobs)
            {
                if(!rowControls.TryGetValue(j.Id,out var row))
                {
                    var state=DownloadText("",13);var detail=DownloadText("",11);var progress=BrandControl(new ProgressBar{Minimum=0,Maximum=100,Height=4});
                    var action=Button("",async(_,_)=>{var current=service.Snapshot().First(x=>x.Id==j.Id);await Act(current);});
                    var item=new ListViewItem{Tag=j.Id,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(0),Content=DownloadColumns([HubIdentity(j.Name,j.Repository+" · "+j.Variant),Stack(state,progress,detail),action],[2.1,2,1])};
                    row=(item,state,detail,progress,action);rowControls.Add(j.Id,row);downloads.Items.Add(item);
                }
                row.State.Foreground=DownloadStateBrush(j.State,row.State.ActualTheme);row.State.Text=j.State+(j.State=="Downloading"?" · "+j.Percent.ToString("0")+"%":"");row.Detail.Text=j.Active||j.State=="Paused"?j.ProgressText+(j.RateText.Length>0?" · "+j.RateText:""):j.State=="Ready in library"?"Files verified · "+HubFormat.Size(j.Total):j.Detail;
                row.Progress.Value=j.State=="Verifying files"?j.VerifiedFiles*100d/j.Files.Length:j.Percent;row.Progress.Visibility=j.Active||j.State=="Paused"?Visibility.Visible:Visibility.Collapsed;Accessible(row.Progress,j.State+" · "+j.ProgressText);
                row.Action.Content=ActionLabel(j);row.Action.IsEnabled=j.State!="Pausing";
                row.Item.Visibility=(statusFilter.SelectedIndex==0||j.State==statusFilter.SelectedItem as string)&&(j.Repository+" "+j.Variant).Contains(query.Text.Trim(),StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed;
                Accessible(row.Item,j.Repository+" · "+j.Variant+" · "+row.State.Text);
                if(j.State=="Ready in library"&&!registered.Contains(j.Id))Register(j);
            }
            empty.Visibility=jobs.Length==0?Visibility.Visible:Visibility.Collapsed;downloadTable.Visibility=jobs.Length==0?Visibility.Collapsed:Visibility.Visible;
            if(selected==null&&downloads.Items.Count>0)downloads.SelectedIndex=0;Detail(Selected());
            foreach(var entry in catalogStatus){var job=jobs.LastOrDefault(j=>j.Repository==entry.Key&&j.State!="Cancelled");entry.Value.Text=job==null?"":"This PC: "+job.State+(job.State=="Downloading"?" · "+job.Percent.ToString("0")+"%":"");}
            source.IsEnabled=model!=null;
            var pinnedJob=jobs.FirstOrDefault(j=>j.Id==pinned);var variant=variants.SelectedItem as HubVariant;
            variants.Visibility=pinnedJob==null?Visibility.Visible:Visibility.Collapsed;fixedVariant.Visibility=pinnedJob==null?Visibility.Collapsed:Visibility.Visible;fixedVariant.Text=pinnedJob?.Variant??"";cPrimary.IsEnabled=pinnedJob!=null||variant!=null;cPrimary.Content=pinnedJob==null?"Review download":"Open download";
            cPath.Text=pinnedJob?.Folder??downloadSettings.Folder;storage.Visibility=pinnedJob==null?Visibility.Visible:Visibility.Collapsed;
            if(pinnedJob!=null){variants.SelectedItem=(variants.ItemsSource as HubVariant[])?.FirstOrDefault(v=>v.Name==pinnedJob.Variant);cNote.Text="Selected download: "+pinnedJob.Variant+"\nDestination and files are fixed for this job.";}
            else if(variant!=null)cNote.Text=variant.Note+"\n"+HubFormat.Size(variant.Bytes)+" · "+variant.Files.Length+(variant.Files.Length==1?" file · ":" files · ")+StorageSpace(downloadSettings.Folder);
            foreach(var control in new FrameworkElement[]{cStages.View,cState,cBytes,cProgress,cPause,another})control.Visibility=pinnedJob==null?Visibility.Collapsed:Visibility.Visible;
            if(pinnedJob!=null){cState.Text=pinnedJob.State;cState.Foreground=DownloadStateBrush(pinnedJob.State,cState.ActualTheme);cBytes.Text=pinnedJob.ProgressText+"\n"+pinnedJob.RateText;cProgress.Value=pinnedJob.State=="Verifying files"?pinnedJob.VerifiedFiles*100d/pinnedJob.Files.Length:pinnedJob.Percent;cProgress.Visibility=pinnedJob.Terminal?Visibility.Collapsed:Visibility.Visible;cStages.Set(pinnedJob.State=="Ready in library"?3:pinnedJob.State=="Verifying files"?1:0);cPause.Content=ActionLabel(pinnedJob);cPause.IsEnabled=pinnedJob.State!="Pausing";}
            var active=jobs.FirstOrDefault(j=>j.Active)??jobs.FirstOrDefault(j=>!j.Terminal);downloadTray.Visibility=jobs.Any(j=>!j.Terminal||j.Completed is {} at&&DateTimeOffset.UtcNow-at<TimeSpan.FromMinutes(2))?Visibility.Visible:Visibility.Collapsed;
            trayText.Text=active==null?"Downloads · "+jobs.Count(j=>j.State=="Ready in library")+" ready in library":$"Downloads · {jobs.Count(j=>!j.Terminal)} unfinished · {active.Name} · {active.State} · {active.ProgressText}";
            trayProgress.Visibility=active?.Active==true?Visibility.Visible:Visibility.Collapsed;trayProgress.Value=active?.Percent??0;trayPause.Visibility=active?.Active==true?Visibility.Visible:Visibility.Collapsed;trayPause.IsEnabled=active?.State!="Pausing";
        }
        refreshNow=Refresh;
        query.TextChanged+=(_,_)=>Refresh();statusFilter.SelectionChanged+=(_,_)=>Refresh();
        var clock=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(750)};clock.Tick+=(_,_)=>Refresh();root.Loaded+=(_,_)=>clock.Start();Closed+=(_,_)=>{clock.Stop();searchCancel?.Cancel();inspectCancel?.Cancel();hub.Dispose();};Refresh();
        return(discoverPage,downloadsPage);
    }
    static Grid DownloadColumns(FrameworkElement[] children,double[] weights)
    {
        var grid=new Grid{ColumnSpacing=14,Padding=new Thickness(14,12,14,12),MinHeight=76};
        for(int i=0;i<children.Length;i++){grid.ColumnDefinitions.Add(new(){Width=new GridLength(weights[i],GridUnitType.Star)});Grid.SetColumn(children[i],i);children[i].VerticalAlignment=VerticalAlignment.Center;grid.Children.Add(children[i]);}
        return grid;
    }
    static Microsoft.UI.Xaml.Media.SolidColorBrush DownloadStateBrush(string state,ElementTheme theme)
    {
        var ui=new Windows.UI.ViewManagement.UISettings();
        if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)return new(ui.UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText));
        bool dark=theme==ElementTheme.Dark;
        return new(state switch{"Ready in library"=>ModelTones.Ink(Tone.Good,dark),"Needs attention"=>ModelTones.Ink(Tone.Bad,dark),_=>dark?Windows.UI.Color.FromArgb(255,248,246,242):Windows.UI.Color.FromArgb(255,32,38,60)});
    }
}
