using System.IO;
using System.Text.Json;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    RoleModels SessionModels(CodeSession owner, AiEndpointProfile main, Action<string> budget, Action<string> status)
    {
        var choices=Store.Read<List<AiEndpointProfile>>("ai-connections.json")??[];
        CodeLocalModel Connect(Guid? id)
        {
            var endpoint=id==null || id==main.Id ? main : choices.SingleOrDefault(p=>p.Id==id) ??
                throw new InvalidOperationException("A model role references a removed connection. Open Model roles and select a connection.");
            return new CodeLocalModel(endpoint,aiSessionKeys.GetValueOrDefault(endpoint.Id)??Store.Unprotect(endpoint.Secret),owner.Reasoning,budget,progress:status);
        }
        var assigned=new HashSet<Guid>{main.Id,owner.ReviewerModel??main.Id,owner.VisualModel??main.Id};
        async Task Prepare(CodeLocalModel requested,CancellationToken token)
        {
            if(modelServing.Running.FirstOrDefault(s=>s.Owner.IsRunning && s.Profile.Id==requested.ProfileId) is {} live){requested.RouteTo(live.Profile);return;}
            var path=ModelServing.RecipePath(requested.ProfileId);
            if(!File.Exists(path))return;
            var recipe=JsonSerializer.Deserialize<ModelServing.LaunchRecipe>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("Missing local launch recipe.");
            var settings=ModelLibrarySettings.ReadSaved(Path.Combine(Store.Root,"model-library.json"))??throw new InvalidDataException("Model runtime settings are unavailable.");
            if(recipe.Gpu){
                var memory=await Task.Run(()=>ModelMemory.Read(recipe.Model,token),token);
                async Task<ModelFit> Fit()=>ModelMemory.Assess(memory,await GpuTelemetry.Read(token),recipe.Options.ContextTokens);
                var fit=await Fit();
                foreach(var idle in modelServing.Running.Where(s=>assigned.Contains(s.Profile.Id)&&s.Profile.Id!=requested.ProfileId).ToArray()){
                    if(fit.State=="Likely fits")break;
                    status("Releasing "+idle.Model.Name+" for the next role");modelServing.ReleaseRole(idle);
                    for(int i=0;i<8 && fit.State!="Likely fits";i++){await Task.Delay(250,token);fit=await Fit();}
                }
                if(fit.State!="Likely fits")throw new InvalidOperationException("Model handoff paused: "+fit.Detail);
            }
            var server=await modelServing.Start(recipe.Model,settings,recipe.Gpu,Path.Combine(AppContext.BaseDirectory,"model-runtime"),token,status,recipe.Options);
            if(!server.Profile.Checks.Any(c=>c.Capability==AiCapability.Chat&&c.State=="Passed"))throw new InvalidOperationException("The selected role failed its live chat check.");
            requested.RouteTo(server.Profile);
        }
        var mainModel=Connect(null);
        return new(mainModel,owner.ReviewerModel==null?mainModel:Connect(owner.ReviewerModel),
            owner.VisualModel==null?mainModel:Connect(owner.VisualModel),Store.Root,status,
            owner.RecordModelDecisions?Path.Combine(Store.Root,"model-decisions",owner.Id.ToString("N")):null,owner.VisualModel.HasValue,owner.ManageRoleMemory?Prepare:null,owner.RoleCallLimit);
    }
    FrameworkElement ModelRolesView(CodeSession session,CodeViewerEntry entry)
    {
        var profiles=(Store.Read<List<AiEndpointProfile>>("ai-connections.json")??[]).Where(p=>p.Model.Length>0 && p.ManagedCapability is null or AiCapability.Chat).ToArray();
        var names=new[]{"Use coding model"}.Concat(profiles.Select(p=>p.DisplayName+" · "+p.Endpoint)).ToArray();
        ComboBox Combo(string name,Guid? selected)=>new(){Header=name,ItemsSource=names,SelectedIndex=selected.HasValue?Array.FindIndex(profiles,p=>p.Id==selected)+1:0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var review=Combo("Independent reviewer",session.ReviewerModel);
        var vision=Combo("Browser and visual verifier",session.VisualModel);
        var record=new CheckBox{Content="Keep the last 12 text inference requests for local comparison",IsChecked=session.RecordModelDecisions};
        var manage=new CheckBox{Content="Load and release assigned local models to fit GPU memory",IsChecked=session.ManageRoleMemory};
        var limit=new NumberBox{Header="Maximum calls per review or verification role",Minimum=1,Maximum=128,Value=session.RoleCallLimit};
        var status=CodeNote("");
        var save=Button("Save roles",(_,_)=>{
            if(codeOperation!=null){status.Text="Finish the current turn before changing roles.";return;}
            var prior=(session.ReviewerModel,session.VisualModel,session.RecordModelDecisions,session.ManageRoleMemory,session.RoleCallLimit);
            try {
                session.ReviewerModel=review.SelectedIndex<=0?null:profiles[review.SelectedIndex-1].Id;
                session.VisualModel=vision.SelectedIndex<=0?null:profiles[vision.SelectedIndex-1].Id;
                session.RecordModelDecisions=record.IsChecked==true;
                if(!double.IsFinite(limit.Value)||limit.Value<1||limit.Value>128)throw new ArgumentException("Choose a role budget from 1 to 128.");
                session.ManageRoleMemory=manage.IsChecked==true;session.RoleCallLimit=(int)limit.Value;
                codeStore!.Save(session);status.Text="Session model roles saved.";
            }catch(Exception e){(session.ReviewerModel,session.VisualModel,session.RecordModelDecisions,session.ManageRoleMemory,session.RoleCallLimit)=prior;status.Text=e.Message;}
        });
        string folder=Path.Combine(Store.Root,"model-decisions",session.Id.ToString("N"));
        var files=new ComboBox{Header="Recorded decision",HorizontalAlignment=HorizontalAlignment.Stretch};
        var comparison=Combo("Compare using",session.ReviewerModel);
        var output=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,MaxHeight=280};
        string[] paths=[];
        void Refresh()
        {
            paths=Directory.Exists(folder)?Directory.GetFiles(folder,"*.json").OrderDescending(StringComparer.Ordinal).ToArray():[];
            files.ItemsSource=paths.Select(Path.GetFileNameWithoutExtension).ToArray();files.SelectedIndex=paths.Length>0?0:-1;
        }
        files.SelectionChanged+=(_,_)=>{
            try {output.Text=files.SelectedIndex>=0?File.ReadAllText(paths[files.SelectedIndex]):"";}catch(Exception e){status.Text=e.Message;}
        };
        var compare=Button("Compare decision",(_,_)=>{});
        compare.Click+=async (_,_)=>{
            if(files.SelectedIndex<0)return;
            try {
                compare.IsEnabled=false;
                var profile=comparison.SelectedIndex<=0?profiles.Single(p=>p.Id==session.Model):profiles[comparison.SelectedIndex-1];
                var answer=await ModelTrace.Predict(paths[files.SelectedIndex],profile,aiSessionKeys.GetValueOrDefault(profile.Id)??Store.Unprotect(profile.Secret),entry.Cancellation.Token);
                output.Text=JsonSerializer.Serialize(answer,new JsonSerializerOptions{WriteIndented=true});
                status.Text="Comparison complete. Executed tools: 0.";
            }catch(Exception e){status.Text=e.Message;}finally{compare.IsEnabled=true;}
        };
        Refresh();
        var body=Stack(Heading("Model roles"),review,vision,manage,limit,record,save,status,Heading("Decision inspector",19),files,comparison,
            Row(Button("Refresh decisions",(_,_)=>Refresh()),compare),output);
        body.Padding=new Thickness(18);return CodeScroll(body);
    }
}
