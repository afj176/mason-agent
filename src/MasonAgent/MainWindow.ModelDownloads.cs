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

        // Discovery remains mounted while downloads and other work continue.
        var search=new TextBox{PlaceholderText="Search Hugging Face models…",MinWidth=180};Accessible(search,"Search Hugging Face models");
        var publisher=new ComboBox{Header="Publisher",ItemsSource=new[]{"All publishers","Unsloth"},SelectedIndex=0,MinWidth=145};
        var format=new ComboBox{Header="Format",ItemsSource=new[]{"All formats","GGUF","Safetensors"},SelectedIndex=0,MinWidth=145};
        var found=new ListView{SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};Accessible(found,"Hugging Face search results");
        var catalog=ListTable(found,("Model","",2.2),("Capability","",1),("Format","",.85),("Local status","",1.2));found.ItemTemplate=null;
        var resultStatus=Text("Search Hugging Face to find models. Unsloth is a publisher on Hugging Face.",13);
        var cTitle=Text("Find your next model",20);cTitle.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;var cRepo=Text("Select a repository to inspect its downloadable files.",13);var cFacts=Text("",13);var cNote=Text("",12);
        var variants=new ComboBox{Header="Variant / files",HorizontalAlignment=HorizontalAlignment.Stretch};var cPath=new TextBox{Header="Download storage",IsReadOnly=true,TextWrapping=TextWrapping.Wrap};
        var fixedVariant=new TextBox{Header="Selected variant",IsReadOnly=true,TextWrapping=TextWrapping.Wrap};
        var cState=Text("",20);var cBytes=Text("",12);var cProgress=BrandControl(new ProgressBar{Minimum=0,Maximum=100,Height=6});Accessible(cProgress,"Catalog download progress");
        HubRepository? inspected=null;HubModel? model=null;CancellationTokenSource? searchCancel=null,inspectCancel=null;Guid? pinned=null;
        var catalogStatus=new Dictionary<string,TextBlock>();
        var source=Button("View model card ↗",async(_,_)=>{if(model!=null)await Windows.System.Launcher.LaunchUriAsync(HubClient.ModelPage(model.Id));});
        var cPrimary=Button("Review download",async(_,_)=>
        {
            if(pinned is {} id){openModelDownload?.Invoke(id);return;}
            if(inspected is not {} repo||variants.SelectedItem is not HubVariant variant)return;
            if(service.Snapshot().FirstOrDefault(j=>j.Repository==repo.Id&&j.Revision==repo.Revision&&j.Variant==variant.Name&&j.State!="Cancelled") is {} existing){pinned=existing.Id;openModelDownload?.Invoke(existing.Id);return;}
            var destination=downloadSettings.Folder;
            var reviewedId=Guid.NewGuid();var resolved=ModelDownload.FolderFor(destination,reviewedId);
            var preview=Stack(Text(repo.Id,18),Text(variant.Name,14),Text(HubFormat.Size(variant.Bytes)+" · "+variant.Files.Length+(variant.Files.Length==1?" file":" files"),14),Text("License: "+repo.License,13),Text(variant.Note,13),Text("Destination: "+resolved,13),Text(StorageSpace(destination),12),Text("Pinned revision: "+repo.Revision,12),new Expander{Header="Review files",Content=Text(string.Join("\n",variant.Files.Select(f=>f.Path+" · "+HubFormat.Size(f.Size))),12),HorizontalAlignment=HorizontalAlignment.Stretch},Text("Files are downloaded and verified before appearing in Library. The model stays stopped.",13));
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
        var inspectBody=Stack(cTitle,cRepo,source,cFacts,variants,fixedVariant,cPath,storage,cNote,cStages.View,cState,cProgress,cBytes,FormGrid(170,cPrimary,cPause),another,access);
        search.Header="Search models";search.VerticalAlignment=VerticalAlignment.Bottom;publisher.HorizontalAlignment=format.HorizontalAlignment=HorizontalAlignment.Stretch;
        var searchButton=Button("Search",async(_,_)=>await Search(),true);searchButton.VerticalAlignment=VerticalAlignment.Bottom;
        var searchTools=new Grid{ColumnSpacing=12,RowSpacing=10};foreach(var control in new FrameworkElement[]{search,publisher,format,searchButton})searchTools.Children.Add(control);
        void SearchLayout()
        {
            bool compact=searchTools.ActualWidth<690;searchTools.ColumnDefinitions.Clear();searchTools.RowDefinitions.Clear();
            foreach(var width in compact?new[]{new GridLength(1,GridUnitType.Star),new GridLength(1,GridUnitType.Star)}:new[]{new GridLength(1,GridUnitType.Star),new GridLength(145),new GridLength(145),GridLength.Auto})searchTools.ColumnDefinitions.Add(new(){Width=width});
            for(int r=0;r<(compact?2:1);r++)searchTools.RowDefinitions.Add(new(){Height=GridLength.Auto});
            int i=0;foreach(FrameworkElement child in searchTools.Children){Grid.SetRow(child,compact?i/2:0);Grid.SetColumn(child,compact?i%2:i);i++;}
        }
        searchTools.SizeChanged+=(_,_)=>SearchLayout();SearchLayout();
        var discoverPage=InspectorLayout(Vertical(Stack(searchTools,resultStatus),catalog),ScrollPage(inspectBody),"Selected model");
        async Task Inspect(HubModel entry)
        {
            inspectCancel?.Cancel();inspectCancel=new();var ct=inspectCancel.Token;model=entry;inspected=null;pinned=null;variants.ItemsSource=null;
            cTitle.Text=entry.Name;cRepo.Text=entry.Id;cFacts.Text="Publisher: "+entry.Publisher+" · Hosted on Hugging Face";cNote.Text="Reading pinned files and integrity metadata…";
            try
            {
                var repo=await hub.Inspect(entry.Id,ct);if(ct.IsCancellationRequested)return;inspected=repo;variants.ItemsSource=repo.Variants;variants.SelectedIndex=repo.Variants.Length>0?0:-1;
                pinned=service.Snapshot().LastOrDefault(j=>j.Repository==repo.Id&&j.State!="Cancelled")?.Id;
                cFacts.Text+="\nLicense: "+repo.License+(repo.Gated?" · Access terms may apply":"");cNote.Text=repo.Variants.Length==0?"No complete GGUF or supported Safetensors bundle was found. Review the model card for other formats.":"";
            }
            catch(OperationCanceledException){if(!ct.IsCancellationRequested)cNote.Text="Request timed out. Select the repository again to retry.";}
            catch(Exception error){if(!ct.IsCancellationRequested)cNote.Text=error is IOException?error.Message:"Could not read this repository. Check access and retry.";}
        }
        found.SelectionChanged+=async(_,_)=>{if((found.SelectedItem as ListViewItem)?.Tag is HubModel entry)await Inspect(entry);};
        async Task Search()
        {
            searchCancel?.Cancel();searchCancel=new();var ct=searchCancel.Token;resultStatus.Text="Searching Hugging Face…";
            try
            {
                var results=await hub.Search(search.Text,publisher.SelectedItem as string??"",format.SelectedItem as string??"",ct);if(ct.IsCancellationRequested)return;results=results.Where(x=>x.Capability=="Chat").ToArray();
                inspectCancel?.Cancel();inspected=null;model=null;pinned=null;variants.ItemsSource=null;cTitle.Text="Choose a model";cRepo.Text="Select a repository to inspect its files.";cFacts.Text=cNote.Text="";
                found.Items.Clear();catalogStatus.Clear();resultStatus.Text=results.Length==0?"No models found. Try a different search or filter.":$"{results.Length} {(results.Length==1?"model":"models")} from Hugging Face · most downloaded first · refine search for more";
                foreach(var entry in results)
                {
                    var local=DownloadText("Not downloaded here");catalogStatus[entry.Id]=local;
                    var item=new ListViewItem{Tag=entry,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(0),Content=DownloadColumns([HubIdentity(entry.Name,entry.Id),DownloadText(entry.Capability),DownloadText(entry.Format),local],[2.2,1,.85,1.2])};Accessible(item,entry.Id+" · "+entry.Capability+" · "+entry.Format);found.Items.Add(item);
                }
                if(found.Items.Count>0)found.SelectedIndex=0;
            }
            catch(OperationCanceledException){if(!ct.IsCancellationRequested)resultStatus.Text="Search timed out. Try again.";}
            catch(Exception error){if(!ct.IsCancellationRequested)resultStatus.Text=error is IOException?error.Message:"Search unavailable. Check your connection and try again.";}
        }
        search.KeyDown+=async(_,e)=>{if(e.Key==Windows.System.VirtualKey.Enter){e.Handled=true;await Search();}};
        publisher.SelectionChanged+=async(_,_)=>await Search();format.SelectionChanged+=async(_,_)=>await Search();
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
            foreach(var entry in catalogStatus){var job=jobs.LastOrDefault(j=>j.Repository==entry.Key&&j.State!="Cancelled");entry.Value.Text=job==null?"Not downloaded here":job.State+(job.State=="Downloading"?" · "+job.Percent.ToString("0")+"%":"");}
            source.IsEnabled=model!=null;
            var pinnedJob=jobs.FirstOrDefault(j=>j.Id==pinned);var variant=variants.SelectedItem as HubVariant;
            variants.Visibility=pinnedJob==null?Visibility.Visible:Visibility.Collapsed;fixedVariant.Visibility=pinnedJob==null?Visibility.Collapsed:Visibility.Visible;fixedVariant.Text=pinnedJob?.Variant??"";cPrimary.IsEnabled=pinnedJob!=null||variant!=null;cPrimary.Content=pinnedJob==null?"Review download":"Open download";
            cPath.Text=pinnedJob?.Folder??downloadSettings.Folder;storage.Visibility=pinnedJob==null?Visibility.Visible:Visibility.Collapsed;
            if(pinnedJob!=null){variants.SelectedItem=inspected?.Variants.FirstOrDefault(v=>v.Name==pinnedJob.Variant);cNote.Text="Selected download: "+pinnedJob.Variant+"\nDestination and files are fixed for this job.";}
            else if(variant!=null)cNote.Text=variant.Note+"\n"+HubFormat.Size(variant.Bytes)+" · "+variant.Files.Length+(variant.Files.Length==1?" file · ":" files · ")+StorageSpace(downloadSettings.Folder);
            foreach(var control in new FrameworkElement[]{cStages.View,cState,cBytes,cProgress,cPause,another})control.Visibility=pinnedJob==null?Visibility.Collapsed:Visibility.Visible;
            if(pinnedJob!=null){cState.Text=pinnedJob.State;cState.Foreground=DownloadStateBrush(pinnedJob.State,cState.ActualTheme);cBytes.Text=pinnedJob.ProgressText+"\n"+pinnedJob.RateText;cProgress.Value=pinnedJob.State=="Verifying files"?pinnedJob.VerifiedFiles*100d/pinnedJob.Files.Length:pinnedJob.Percent;cProgress.Visibility=pinnedJob.Terminal?Visibility.Collapsed:Visibility.Visible;cStages.Set(pinnedJob.State=="Ready in library"?3:pinnedJob.State=="Verifying files"?1:0);cPause.Content=ActionLabel(pinnedJob);cPause.IsEnabled=pinnedJob.State!="Pausing";}
            var active=jobs.FirstOrDefault(j=>j.Active)??jobs.FirstOrDefault(j=>!j.Terminal);downloadTray.Visibility=jobs.Any(j=>!j.Terminal||j.Completed is {} at&&DateTimeOffset.UtcNow-at<TimeSpan.FromMinutes(2))?Visibility.Visible:Visibility.Collapsed;
            trayText.Text=active==null?"Downloads · "+jobs.Count(j=>j.State=="Ready in library")+" ready in library":$"Downloads · {jobs.Count(j=>!j.Terminal)} unfinished · {active.Name} · {active.State} · {active.ProgressText}";
            trayProgress.Visibility=active?.Active==true?Visibility.Visible:Visibility.Collapsed;trayProgress.Value=active?.Percent??0;trayPause.Visibility=active?.Active==true?Visibility.Visible:Visibility.Collapsed;trayPause.IsEnabled=active?.State!="Pausing";
        }
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
        return new(state switch{"Ready in library"=>dark?Windows.UI.Color.FromArgb(255,108,215,149):Windows.UI.Color.FromArgb(255,20,115,65),"Needs attention"=>dark?Windows.UI.Color.FromArgb(255,255,150,145):Windows.UI.Color.FromArgb(255,175,35,35),_=>dark?Microsoft.UI.Colors.White:Windows.UI.Color.FromArgb(255,24,34,54)});
    }
}
