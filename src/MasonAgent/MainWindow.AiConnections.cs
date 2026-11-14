using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Mason;
public sealed partial class MainWindow
{
    CancellationTokenSource? aiOperation;
    readonly Dictionary<Guid,string> aiSessionKeys = [];
    Action? refreshAiConnectionUsage;
    FrameworkElement BuildAiConnections()
    {
        var list = new ListView { SelectionMode=ListViewSelectionMode.Single, HorizontalContentAlignment=HorizontalAlignment.Stretch, MaxHeight=300 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list,"Saved local AI connections");
        list.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Spacing='4' Padding='8'><TextBlock Text='{Binding Name}' FontWeight='SemiBold'/><TextBlock Text='{Binding Endpoint}' FontSize='12' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Model}' FontSize='12' TextTrimming='CharacterEllipsis'/></StackPanel></DataTemplate>");
        var name = new TextBox { Header="Name" };
        var endpoint = new TextBox { Header="Local server URL" };
        var model = new TextBox { Header="Exact model ID" };
        var picker = new ComboBox { Header="Models reported by this endpoint", HorizontalAlignment=HorizontalAlignment.Stretch };
        endpoint.PlaceholderText="Your server URL, including its port";model.PlaceholderText="Discover models or enter the exact server model ID";
        var key = new PasswordBox { Header="API key (optional)" };
        var remember = BrandControl(new CheckBox { Content="Remember API key for this Windows account" });
        var report = new TextBox { IsReadOnly=true, AcceptsReturn=true, TextWrapping=TextWrapping.Wrap, MinHeight=190 };
        List<AiEndpointProfile> saved;
        bool readable=true;
        try { saved=Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []; }
        catch { saved=[]; readable=false; }
        AiEndpointProfile? selected=null;
        CancellationTokenSource? running=null;
        var fields=Stack(name,endpoint,picker,model,key,remember);
        var mutations=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        var probes=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        var capabilityRows=new StackPanel{Spacing=4};var usedBy=Text("",12);var editHeading=Text("New connection",21);var dirtyNotice=new InfoBar{IsOpen=true,IsClosable=false,Severity=InfoBarSeverity.Warning,Title="Unsaved changes",Message="Save this connection before running capability checks."};Button? cancelCheck=null;
        var editStatus=Text("New connection · save settings before checking",12);
        bool Dirty()=>selected==null||name.Text.Trim()!=selected.Name||endpoint.Text.Trim()!=selected.Endpoint||model.Text.Trim()!=selected.Model||key.Password!=aiSessionKeys.GetValueOrDefault(selected.Id,"")||remember.IsChecked!=(selected.Secret.Length>0);
        void Edited(){bool dirty=Dirty();dirtyNotice.IsOpen=dirty;editStatus.Text=dirty?"Unsaved changes · previous observations describe saved settings. Save before checking.":"Saved settings · checks below are dated observations, not live liveness.";foreach(var button in probes.Children.OfType<Button>())button.IsEnabled=!dirty&&running==null;}
        foreach(var box in new[]{name,endpoint,model})box.TextChanged+=(_,_)=>Edited();key.PasswordChanged+=(_,_)=>Edited();remember.Checked+=(_,_)=>Edited();remember.Unchecked+=(_,_)=>Edited();
        string Purpose(AiCapability c) => c == AiCapability.Models ? "Discover model IDs; this does not test inference" : "Coding sessions and chat";
        void ShowChecks()
        {
            editHeading.Text=selected?.Name??"New connection";refreshAiConnectionUsage?.Invoke();
            capabilityRows.Children.Clear();
            foreach(var capability in new[]{AiCapability.Models,AiCapability.Chat}){
                var observation=selected?.Checks.LastOrDefault(c=>c.Capability==capability);
                var row=SurfaceCard(Toolbar(Text(capability.ToString(),12),Badge(observation?.State??"Not checked"),Text(observation?.At.LocalDateTime.ToString("g")??"—",11)));row.Padding=new Thickness(10,7,10,7);ToolTipService.SetToolTip(row,observation==null?"No observation":observation.At.LocalDateTime.ToString("g")+" · "+observation.Detail);capabilityRows.Children.Add(row);
            }
            if (!readable) { report.Text="AI settings could not be read. Existing file preserved. Repair ai-connections.json before saving."; return; }
            var ready=selected?.Checks.Where(x=>x.State=="Passed" && x.Capability!=AiCapability.Models).Select(x=>x.Capability.ToString()).ToArray() ?? [];
            report.Text=(ready.Length>0 ? "Ready for: "+string.Join(", ",ready) : "No inference capability verified yet. Save, then check the capability you need.")+
                "\n\n"+
                string.Join("\n\n",new[]{AiCapability.Models,AiCapability.Chat}.Select(c=> {
                    var r=selected?.Checks.LastOrDefault(x=>x.Capability==c);
                    var state=r?.State switch { "Passed"=>"Available", "Unavailable"=>"Not available on this endpoint", null=>"Not checked", _=>"Needs attention: "+r.State };
                    return $"{c}: {state}\n{Purpose(c)}"+(r==null ? "" : $"\nChecked {r.At.ToLocalTime():g}\n{r.Detail}");
                }));

        }
        void Refresh(AiEndpointProfile? target) { list.ItemsSource=null; list.ItemsSource=saved.ToList(); list.SelectedItem=target; }
        void NotifyProfiles() { refreshModelLibrary?.Invoke(); refreshCodeProfiles?.Invoke(); }
        void Enable(bool enabled) { if(cancelCheck!=null)cancelCheck.IsEnabled=!enabled;list.IsEnabled=enabled; foreach(var panel in new[]{fields,mutations,probes}) foreach(var c in panel.Children.OfType<Control>()) c.IsEnabled=enabled; }
        refreshAiConnectionList=()=> {
            var id=selected?.Id;
            try { saved=Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []; Refresh(saved.FirstOrDefault(x=>x.Id==id)); ShowChecks(); }
            catch { readable=false; ShowChecks(); }
        };
        selectManagedAiConnection=id=>list.SelectedItem=saved.FirstOrDefault(p=>p.Id==id);
        list.SelectionChanged+=(_,_)=> {
            selected=list.SelectedItem as AiEndpointProfile;
            name.Text=selected?.Name ?? "Local AI connection"; endpoint.Text=selected?.Endpoint ?? ""; model.Text=selected?.Model ?? "";
            key.Password=""; remember.IsChecked=false;
            picker.ItemsSource=selected?.Checks.LastOrDefault(x=>x.Capability==AiCapability.Models && x.State=="Passed")?.Models ?? [];
            picker.SelectedItem=model.Text;
            if(selected!=null) try { key.Password=aiSessionKeys.GetValueOrDefault(selected.Id) ?? Store.Unprotect(selected.Secret); aiSessionKeys[selected.Id]=key.Password; remember.IsChecked=selected.Secret.Length>0; }
            catch { aiSessionKeys[selected.Id]=""; report.Text="Saved API key could not be decrypted. Enter it again and save."; return; }
            ShowChecks();Edited();
        };
        picker.SelectionChanged+=(_,_)=> { if(picker.SelectedItem is string id) model.Text=id; };
        mutations.Children.Add(Button("New",(_,_)=> { Refresh(null); ShowChecks(); }));
        mutations.Children.Add(Button("Save",async(_,_)=> {
            try {
                if(aiOperation!=null) throw new InvalidOperationException("Finish or cancel the active AI request first.");
                if(!readable) throw new InvalidOperationException("Repair ai-connections.json before saving; existing file preserved.");
                var uri=LocalAiClient.ValidateEndpoint(endpoint.Text);
                if(string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("Enter a connection name.");
                var p=new AiEndpointProfile { Id=selected?.Id ?? Guid.NewGuid(), Name=name.Text.Trim(), Endpoint=uri.AbsoluteUri, Model=model.Text.Trim(), RerankingRoute=selected?.RerankingRoute,ManagedCapability=selected!=null&&selected.Endpoint==uri.AbsoluteUri&&selected.Model==model.Text.Trim()?selected.ManagedCapability:null,EmbeddingProcessing=selected!=null&&selected.Endpoint==uri.AbsoluteUri&&selected.Model==model.Text.Trim()?selected.EmbeddingProcessing:null,Secret=remember.IsChecked==true ? Store.Protect(key.Password) : "",
                    Checks=selected!=null && selected.Endpoint==uri.AbsoluteUri && key.Password==(aiSessionKeys.GetValueOrDefault(selected.Id) ?? Store.Unprotect(selected.Secret)) ? selected.Checks.Where(x=>x.Capability==AiCapability.Models || selected.Model==model.Text.Trim()).ToList() : [] };
                var next=saved.Where(x=>x.Id!=p.Id).Append(p).ToList(); Store.Write("ai-connections.json",next); saved=next; aiSessionKeys[p.Id]=key.Password; Refresh(p); NotifyProfiles();Edited();
            } catch(Exception e) { await Notice("AI connection",e.Message); }
        },true));
        mutations.Children.Add(Button("Remove",async(_,_)=> {
            if(selected==null) return;
            if(aiOperation!=null) { await Notice("AI request active","Finish or cancel the active request first."); return; }
            try { var next=saved.Where(x=>x.Id!=selected.Id).ToList(); Store.Write("ai-connections.json",next); aiSessionKeys.Remove(selected.Id); saved=next; Refresh(null); ShowChecks(); NotifyProfiles(); }
            catch(Exception e) { await Notice("AI connection",e.Message); }
        }));
        foreach(var capability in new[]{AiCapability.Models,AiCapability.Chat}) probes.Children.Add(Button("Check "+capability.ToString().ToLowerInvariant(),async(_,_)=> {
            if(aiOperation!=null) { await Notice("AI request active","Finish or cancel the active request first."); return; }
            if(selected==null) { await Notice("Save connection","Save the connection before checking it."); return; }
            var p=selected;
            if(name.Text.Trim()!=p.Name || endpoint.Text.Trim()!=p.Endpoint || model.Text.Trim()!=p.Model || key.Password!=aiSessionKeys.GetValueOrDefault(p.Id,"")) { await Notice("Save changes","Save or reselect this connection first. Checks use saved settings."); return; }
            using var cancellation=new CancellationTokenSource(); running=aiOperation=cancellation; Enable(false); report.Text=$"Checking {capability} at {p.Endpoint}...";
            try {
                using var client=new LocalAiClient(); var result=await client.Check(p,capability,aiSessionKeys.GetValueOrDefault(p.Id,""),cancellation.Token);
                var updated=p with { Checks=p.Checks.Where(x=>x.Capability!=capability).Append(result).ToList() };
                var next=saved.Select(x=>x.Id==p.Id ? updated : x).ToList(); Store.Write("ai-connections.json",next); saved=next; Refresh(updated); ShowChecks();
            } catch(OperationCanceledException) { report.Text="Check cancelled. Previous results retained. The server may still be finishing the request."; }
            catch(Exception e) { report.Text="Check could not complete: "+e.Message; }
            finally { running=aiOperation=null; Enable(true);Edited(); }
        }));
        var stop=Button("Cancel check",(_,_)=>running?.Cancel());cancelCheck=stop;stop.IsEnabled=false;
        var checkChoice=new ComboBox{Header="Check capability",ItemsSource=new[]{AiCapability.Models,AiCapability.Chat},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
        void ChooseCheck(){for(int i=0;i<probes.Children.Count;i++)((FrameworkElement)probes.Children[i]).Visibility=i==checkChoice.SelectedIndex?Visibility.Visible:Visibility.Collapsed;}
        checkChoice.SelectionChanged+=(_,_)=>ChooseCheck();ChooseCheck();
        root.Unloaded+=(_,_)=>running?.Cancel(); Refresh(null); ShowChecks();
        mutations.Children.Add(Button("Revert edits",(_,_)=>{var prior=selected;Refresh(null);Refresh(prior);Edited();}));
        var editor=ScrollPage(Stack(editHeading,Text("A connection points to a local server. Its process is managed separately.",13),fields,dirtyNotice,editStatus,ScrollTools(mutations)));
        var capabilityPanel=ScrollPage(Stack(Text("Independent dated observations of saved settings. Checks send a synthetic prompt only.",12),capabilityRows,checkChoice,ScrollTools(probes),stop,new Expander{Header="Check details",Content=report,HorizontalAlignment=HorizontalAlignment.Stretch},
            Text("Where this connection is used",15),usedBy,new Expander{Header="Setup help",HorizontalAlignment=HorizontalAlignment.Stretch,Content=Text("Start a compatible local server. Save its loopback URL, check Models, choose an exact model ID and save again. Then check the capability you need. Removing a connection never stops its server or deletes weights.",13)}));
        refreshAiConnectionUsage=()=>usedBy.Text="Coding sessions retain their selected model connection. Changes apply to future requests.";
        var inventory=Stack(Text("Saved connections",16),list,Button("New connection",(_,_)=>{Refresh(null);ShowChecks();Edited();}));
        var content=InspectorLayout(editor,capabilityPanel,"Checks & observations");
        var layout=new Grid{ColumnSpacing=16,RowSpacing=12};layout.ColumnDefinitions.Add(new(){Width=new GridLength(240)});layout.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        layout.Children.Add(inventory);layout.Children.Add(content);
        layout.SizeChanged+=(_,_)=>{bool narrow=layout.ActualWidth<1150;layout.ColumnDefinitions[0].Width=narrow?new GridLength(0):new GridLength(240);Grid.SetColumn(inventory,narrow?1:0);Grid.SetRowSpan(inventory,narrow?1:2);Grid.SetColumn(content,1);Grid.SetRow(content,narrow?1:0);Grid.SetRowSpan(content,narrow?1:2);list.MaxHeight=narrow?140:500;};Edited();return layout;
    }
}
