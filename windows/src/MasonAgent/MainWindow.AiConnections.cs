using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Mason;
public sealed partial class MainWindow
{
    CancellationTokenSource? aiOperation;
    readonly Dictionary<Guid,string> aiSessionKeys = [];
    Action? refreshAiConnectionUsage;
    static readonly (string Label,AiCapability Capability)[] ConnectionUses = [("Coding agent and chat",AiCapability.Chat),("Code search · embeddings",AiCapability.Embeddings),("Code search · reranker",AiCapability.Reranking)];
    FrameworkElement BuildAiConnections()
    {
        var list = new ListView { SelectionMode=ListViewSelectionMode.Single, HorizontalContentAlignment=HorizontalAlignment.Stretch, MaxHeight=300 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list,"Saved local AI connections");
        list.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Spacing='4' Padding='8'><TextBlock Text='{Binding Name}' FontWeight='SemiBold'/><TextBlock Text='{Binding Endpoint}' FontSize='12' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Model}' FontSize='12' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Health}' FontSize='11' Opacity='0.8' TextTrimming='CharacterEllipsis'/></StackPanel></DataTemplate>");
        var name = new TextBox { Header="Name" };
        var endpoint = new TextBox { Header="Local server URL" };
        var model = new TextBox { Header="Exact model ID" };
        var picker = new ComboBox { Header="Models reported by this server", HorizontalAlignment=HorizontalAlignment.Stretch };
        endpoint.PlaceholderText="For example, http://127.0.0.1:11434/ for Ollama";model.PlaceholderText="Choose from the list above or type the server's model ID";
        var use = new ComboBox { Header="Use it for", ItemsSource=ConnectionUses.Select(u=>u.Label).ToArray(), SelectedIndex=0, HorizontalAlignment=HorizontalAlignment.Stretch };
        var key = new PasswordBox { Header="API key (optional)" };
        var remember = BrandControl(new CheckBox { Content="Remember API key for this Windows account" });
        var report = new TextBox { IsReadOnly=true, AcceptsReturn=true, TextWrapping=TextWrapping.Wrap, MinHeight=150 };
        // Shown once the saved connection has passed a check for some use.
        var connectedPose = MascotImage(PoseConnected, 72, "connection verified"); connectedPose.HorizontalAlignment = HorizontalAlignment.Left; connectedPose.Visibility = Visibility.Collapsed;
        List<AiEndpointProfile> saved;
        bool readable=true;
        try { saved=Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []; }
        catch { saved=[]; readable=false; }
        AiEndpointProfile? selected=null;
        CancellationTokenSource? running=null;
        var fields=Stack(name,endpoint,picker,model,use,key,remember);
        var mutations=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        var probes=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        var capabilityRows=new StackPanel{Spacing=4};var usedBy=Text("",12);var editHeading=Text("New connection",21);
        var editStatus=Text("",12);
        bool Dirty()=>selected==null||name.Text.Trim()!=selected.Name||endpoint.Text.Trim()!=selected.Endpoint||model.Text.Trim()!=selected.Model||key.Password!=aiSessionKeys.GetValueOrDefault(selected.Id,"")||remember.IsChecked!=(selected.Secret.Length>0);
        void Edited(){bool dirty=Dirty();editStatus.Text=dirty?"Unsaved changes · Test & save checks the server and saves in one step.":"Saved · the checks on the right are dated results, not a live status.";foreach(var button in probes.Children.OfType<Button>())button.IsEnabled=!dirty&&running==null;}
        foreach(var box in new[]{name,endpoint,model})box.TextChanged+=(_,_)=>Edited();key.PasswordChanged+=(_,_)=>Edited();remember.Checked+=(_,_)=>Edited();remember.Unchecked+=(_,_)=>Edited();
        string Purpose(AiCapability c) => c switch { AiCapability.Models => "Lists model IDs; this doesn't test inference", AiCapability.Chat => "Coding agent roles and chat", AiCapability.Embeddings => "The agent's semantic code search", _ => "Reorders the agent's code search results" };
        AiCapability[] Shown()=>[AiCapability.Models,..ConnectionUses.Select(u=>u.Capability).Where(c=>c==ConnectionUses[Math.Max(0,use.SelectedIndex)].Capability||selected?.Checks.Any(x=>x.Capability==c)==true)];
        void ShowChecks()
        {
            editHeading.Text=selected?.Name??"New connection";refreshAiConnectionUsage?.Invoke();
            capabilityRows.Children.Clear();
            foreach(var capability in Shown()){
                var observation=selected?.Checks.LastOrDefault(c=>c.Capability==capability);
                var row=SurfaceCard(Toolbar(Text(capability.ToString(),12),Badge(observation?.State??"Not checked"),Text(observation?.At.LocalDateTime.ToString("g")??"—",11)));row.Padding=new Thickness(10,7,10,7);ToolTipService.SetToolTip(row,observation==null?"No result yet":observation.At.LocalDateTime.ToString("g")+" · "+observation.Detail);capabilityRows.Children.Add(row);
            }
            if (!readable) { report.Text="AI settings could not be read. The existing file is preserved. Repair ai-connections.json before saving."; return; }
            var ready=selected?.Checks.Where(x=>x.State=="Passed" && x.Capability!=AiCapability.Models).Select(x=>Purpose(x.Capability)).ToArray() ?? [];
            connectedPose.Visibility=ready.Length>0?Visibility.Visible:Visibility.Collapsed;
            report.Text=(ready.Length>0 ? "Ready for: "+string.Join("; ",ready) : "Nothing verified yet. Test & save runs the checks for the use you chose.")+"\n\n"+
                string.Join("\n\n",Shown().Select(c=> {
                    var r=selected?.Checks.LastOrDefault(x=>x.Capability==c);
                    var state=r?.State switch { "Passed"=>"Works", "Unavailable"=>"Not offered by this server", null=>"Not checked", _=>"Needs attention: "+r.State };
                    return $"{c}: {state}\n{Purpose(c)}"+(r==null ? "" : $"\nChecked {r.At.ToLocalTime():g}\n{r.Detail}");
                }));
        }
        void Refresh(AiEndpointProfile? target) { list.ItemsSource=null; list.ItemsSource=saved.ToList(); list.SelectedItem=target; }
        void NotifyProfiles() { refreshModelLibrary?.Invoke(); refreshCodeProfiles?.Invoke(); }
        void Enable(bool enabled) { list.IsEnabled=enabled; foreach(var panel in new[]{fields,mutations,probes}) foreach(var c in panel.Children.OfType<Control>()) c.IsEnabled=enabled; }
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
            var capability=selected?.ManagedCapability??selected?.Checks.Where(c=>c.State=="Passed"&&c.Capability!=AiCapability.Models).Select(c=>(AiCapability?)c.Capability).LastOrDefault()??AiCapability.Chat;
            use.SelectedIndex=Math.Max(0,Array.FindIndex(ConnectionUses,u=>u.Capability==capability));
            picker.ItemsSource=selected?.Checks.LastOrDefault(x=>x.Capability==AiCapability.Models && x.State=="Passed")?.Models ?? [];
            picker.SelectedItem=model.Text;
            if(selected!=null) try { key.Password=aiSessionKeys.GetValueOrDefault(selected.Id) ?? Store.Unprotect(selected.Secret); aiSessionKeys[selected.Id]=key.Password; remember.IsChecked=selected.Secret.Length>0; }
            catch { aiSessionKeys[selected.Id]=""; report.Text="The saved API key couldn't be decrypted. Enter it again and save."; return; }
            ShowChecks();Edited();
        };
        picker.SelectionChanged+=(_,_)=> { if(picker.SelectedItem is string id) model.Text=id; };
        use.SelectionChanged+=(_,_)=>ShowChecks();
        // X1: per-connection generation settings for the coding agent (Goose's model settings panel).
        NumberBox Number(string header,double min,double max,double step,string help)=>new NumberBox{Header=header,Minimum=min,Maximum=max,SmallChange=step,LargeChange=step*10,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,PlaceholderText="Model default",Value=double.NaN,Description=help};
        var temperature=Number("Temperature",0,2,.05,"Lower is more deterministic. Coding usually works best at 0–0.3.");
        var topP=Number("Top P",0,1,.05,"Nucleus sampling cut-off.");var topK=Number("Top K",0,500,1,"Candidates per token; 0 turns it off.");
        var minP=Number("Min P",0,1,.01,"Drops tokens below this share of the best one.");var repeat=Number("Repeat penalty",0,3,.05,"Above 1 discourages loops.");
        var seed=Number("Seed",-1,int.MaxValue,1,"Fixed seed for repeatable runs.");
        double? Value(NumberBox box)=>double.IsNaN(box.Value)?null:box.Value;
        CodeSampling? SamplingFields(){var s=new CodeSampling(Value(temperature),Value(topP),Value(topK) is {} k?(int)k:null,Value(minP),Value(repeat),Value(seed) is {} sd?(int)sd:null);return s.Empty?null:s;}
        void ShowSampling(CodeSampling? s){temperature.Value=s?.Temperature??double.NaN;topP.Value=s?.TopP??double.NaN;topK.Value=s?.TopK??double.NaN;minP.Value=s?.MinP??double.NaN;repeat.Value=s?.RepeatPenalty??double.NaN;seed.Value=s?.Seed??double.NaN;}
        list.SelectionChanged+=(_,_)=>ShowSampling(selected?.Sampling);
        AiEndpointProfile SaveFields()
        {
            if(aiOperation!=null) throw new InvalidOperationException("Finish or cancel the active AI request first.");
            if(!readable) throw new InvalidOperationException("Repair ai-connections.json before saving; the existing file is preserved.");
            var uri=LocalAiClient.ValidateEndpoint(endpoint.Text);
            if(string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("Enter a connection name.");
            bool same=selected!=null&&selected.Endpoint==uri.AbsoluteUri&&selected.Model==model.Text.Trim();
            var p=new AiEndpointProfile { Id=selected?.Id ?? Guid.NewGuid(), Name=name.Text.Trim(), Endpoint=uri.AbsoluteUri, Model=model.Text.Trim(), RerankingRoute=selected?.RerankingRoute,Sampling=SamplingFields(),ManagedCapability=same?selected!.ManagedCapability:null,EmbeddingProcessing=same?selected!.EmbeddingProcessing:null,Secret=remember.IsChecked==true ? Store.Protect(key.Password) : "",
                Checks=selected!=null && selected.Endpoint==uri.AbsoluteUri && key.Password==(aiSessionKeys.GetValueOrDefault(selected.Id) ?? Store.Unprotect(selected.Secret)) ? selected.Checks.Where(x=>x.Capability==AiCapability.Models || selected.Model==model.Text.Trim()).ToList() : [] };
            var next=saved.Where(x=>x.Id!=p.Id).Append(p).ToList(); Store.Write("ai-connections.json",next); saved=next; aiSessionKeys[p.Id]=key.Password; Refresh(p); NotifyProfiles();Edited();
            return p;
        }
        async Task<AiCheck?> RunCheck(AiCapability capability)
        {
            if(selected is not {} p)return null;
            using var cancellation=new CancellationTokenSource(); running=aiOperation=cancellation; Enable(false); report.Text=$"Checking {capability} at {p.Endpoint}…";
            try {
                using var client=new LocalAiClient(); var result=await client.Check(p,capability,aiSessionKeys.GetValueOrDefault(p.Id,""),cancellation.Token);
                var updated=p with { Checks=p.Checks.Where(x=>x.Capability!=capability).Append(result).ToList() };
                var next=saved.Select(x=>x.Id==p.Id ? updated : x).ToList(); Store.Write("ai-connections.json",next); saved=next; Refresh(updated); ShowChecks(); NotifyProfiles();
                return result;
            } catch(OperationCanceledException) { report.Text="Check cancelled. Previous results are kept. The server may still be finishing the request."; return null; }
            catch(Exception e) { report.Text="The check couldn't complete: "+e.Message; return null; }
            finally { running=aiOperation=null; Enable(true);Edited(); }
        }
        // One pass: save, list the server's models, pick one if needed, then check the chosen use.
        var testAndSave=Button("Test & save",async(_,_)=> {
            try{SaveFields();}catch(Exception e){await Notice("AI connection",e.Message);return;}
            var listing=await RunCheck(AiCapability.Models);
            if(listing?.State=="Passed"){picker.ItemsSource=listing.Models??[];if(model.Text.Trim().Length==0&&listing.Models is {Length:>0} ids){model.Text=ids[0];picker.SelectedItem=ids[0];try{SaveFields();}catch(Exception e){await Notice("AI connection",e.Message);return;}}}
            if(model.Text.Trim().Length==0){report.Text="The server is reachable. Choose a model ID, then Test & save again.";return;}
            var result=await RunCheck(ConnectionUses[Math.Max(0,use.SelectedIndex)].Capability);
            if(result?.State=="Passed")report.Text=ConnectionUses[Math.Max(0,use.SelectedIndex)].Capability switch{AiCapability.Embeddings=>"Ready. The agent can now search code by meaning with this model.",AiCapability.Reranking=>"Ready. The agent's code search results will be reranked by this model.",_=>"Ready. Choose this connection in Code or Chat."}+"\n\n"+report.Text;
        },true);
        mutations.Children.Add(testAndSave);
        mutations.Children.Add(Button("Save only",async(_,_)=> { try{SaveFields();}catch(Exception e){await Notice("AI connection",e.Message);} }));
        mutations.Children.Add(Button("Remove",async(_,_)=> {
            if(selected==null) return;
            if(aiOperation!=null) { await Notice("AI request active","Finish or cancel the active request first."); return; }
            try { var next=saved.Where(x=>x.Id!=selected.Id).ToList(); Store.Write("ai-connections.json",next); aiSessionKeys.Remove(selected.Id); saved=next; Refresh(null); ShowChecks(); NotifyProfiles(); }
            catch(Exception e) { await Notice("AI connection",e.Message); }
        }));
        mutations.Children.Add(Button("Revert edits",(_,_)=>{var prior=selected;Refresh(null);Refresh(prior);Edited();}));
        // Two checks: the model list, and the use chosen above.
        Button Probe(Func<AiCapability> capability)=>Button("",async(_,_)=> {
            if(aiOperation!=null) { await Notice("AI request active","Finish or cancel the active request first."); return; }
            if(selected==null || Dirty()) { await Notice("Save changes","Use Test & save, or save before running a single check. Checks use saved settings."); return; }
            await RunCheck(capability());
        });
        var listProbe=Probe(()=>AiCapability.Models);listProbe.Content="Check model list";probes.Children.Add(listProbe);
        var useProbe=Probe(()=>ConnectionUses[Math.Max(0,use.SelectedIndex)].Capability);probes.Children.Add(useProbe);
        void UseLabel()=>useProbe.Content=ConnectionUses[Math.Max(0,use.SelectedIndex)].Capability switch{AiCapability.Embeddings=>"Check embeddings",AiCapability.Reranking=>"Check reranking",_=>"Check chat"};
        use.SelectionChanged+=(_,_)=>UseLabel();UseLabel();
        var stop=Button("Cancel check",(_,_)=>running?.Cancel());
        // Servers already running on this PC, found by asking each well-known port for its model list.
        var foundList=new StackPanel{Spacing=6};var foundStatus=Text("",12);
        async Task Detect()
        {
            foundStatus.Text="Looking for local servers…";foundList.Children.Clear();
            try
            {
                using var client=new LocalAiClient();var servers=await client.Detect(CancellationToken.None);
                foreach(var server in servers)
                {
                    bool known=saved.Any(p=>p.Endpoint==server.Endpoint);
                    var add=Button(known?"Saved":"Add",(_,_)=>{Refresh(null);name.Text=server.Name;endpoint.Text=server.Endpoint;picker.ItemsSource=server.Models;model.Text=server.Models.FirstOrDefault()??"";picker.SelectedItem=model.Text;use.SelectedIndex=0;ShowChecks();Edited();editStatus.Text="Prefilled from "+server.Name+". Choose the model and its use, then Test & save.";});
                    add.IsEnabled=!known;
                    var row=SurfaceCard(Toolbar(Stack(Text(server.Name,13),Text(server.Endpoint+" · "+server.Models.Length+(server.Models.Length==1?" model":" models"),11)),add));row.Padding=new Thickness(10,6,10,6);foundList.Children.Add(row);
                }
                foundStatus.Text=servers.Length==0?"No servers answered on the usual ports (Ollama, LM Studio, llama.cpp, vLLM, Jan and others).":"";
            }
            catch(Exception e){foundStatus.Text="Couldn't scan local ports: "+e.Message;}
        }
        root.Unloaded+=(_,_)=>running?.Cancel(); Refresh(null); ShowChecks();
        var resetSampling=Button("Use model defaults",(_,_)=>ShowSampling(null));
        var sampling=new Expander{Header="Coding generation",HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,
            Content=Stack(Muted("Applied to the coding agent's actions with this connection. Reviews and checks keep deterministic settings. Save the connection to keep changes.",12),FormGrid(temperature,topP,topK,minP,repeat,seed),resetSampling)};
        var editor=ScrollPage(Stack(editHeading,Text("A connection points the agent at a model server running on this PC. Mason doesn't start or stop servers it didn't launch.",13),fields,sampling,editStatus,ScrollTools(mutations)));
        var capabilityPanel=ScrollPage(Stack(Text("Dated results from short synthetic requests. No project content is sent.",12),connectedPose,capabilityRows,ScrollTools(probes),stop,new Expander{Header="Check details",Content=report,HorizontalAlignment=HorizontalAlignment.Stretch,IsExpanded=true},
            Text("Used by",15),usedBy));
        refreshAiConnectionUsage=()=>
        {
            if(selected==null){usedBy.Text="Save the connection to assign it to agent roles.";return;}
            var id=selected.Id;var uses=new List<string>();
            var sessions=codeStore?.Sessions.ToArray()??[];
            int main=sessions.Count(s=>s.Model==id),review=sessions.Count(s=>s.ReviewerModel==id),visual=sessions.Count(s=>s.VisualModel==id);
            if(main>0)uses.Add($"Implementer in {main} coding {(main==1?"session":"sessions")}");
            if(review>0)uses.Add($"Reviewer in {review} {(review==1?"session":"sessions")}");
            if(visual>0)uses.Add($"Visual verifier in {visual} {(visual==1?"session":"sessions")}");
            var search=CodeSearchSettings.Read();
            if(search.Embeddings==id)uses.Add("Code search embeddings (assigned)");
            if(search.Reranker==id)uses.Add("Code search reranker (assigned)");
            usedBy.Text=uses.Count==0?"Not assigned to an agent role yet. Choose it in Code (model picker or Model roles).":string.Join("\n",uses);
        };
        refreshAiConnectionUsage();
        var inventory=Stack(Text("Found on this PC",16),foundList,foundStatus,Button("Scan again",async(_,_)=>await Detect()),Text("Saved connections",16),list,Button("New connection",(_,_)=>{Refresh(null);ShowChecks();Edited();}));
        bool detected=false;inventory.Loaded+=async(_,_)=>{if(detected)return;detected=true;await Detect();};
        var content=InspectorLayout(editor,capabilityPanel,"Checks & usage");
        var layout=new Grid{ColumnSpacing=16,RowSpacing=12};layout.ColumnDefinitions.Add(new(){Width=new GridLength(280)});layout.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var inventoryScroll=ScrollPage(inventory);
        layout.Children.Add(inventoryScroll);layout.Children.Add(content);
        layout.SizeChanged+=(_,_)=>{bool narrow=layout.ActualWidth<980;layout.ColumnDefinitions[0].Width=narrow?new GridLength(0):new GridLength(280);Grid.SetColumn(inventoryScroll,narrow?1:0);Grid.SetRowSpan(inventoryScroll,narrow?1:2);Grid.SetColumn(content,1);Grid.SetRow(content,narrow?1:0);Grid.SetRowSpan(content,narrow?1:2);inventoryScroll.MaxHeight=narrow?260:double.PositiveInfinity;list.MaxHeight=narrow?140:500;};Edited();return layout;
    }
}
