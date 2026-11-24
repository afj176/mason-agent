using System.IO;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    FrameworkElement BackgroundTasksView(CodeSession session,CodeViewerEntry entry)
    {
        var tasks=new LocalTasks(Store.Root);
        var list=new ComboBox {Header="Background task",HorizontalAlignment=HorizontalAlignment.Stretch};
        var prompt=new TextBox {Header="New task",AcceptsReturn=true,MaxLength=16000,TextWrapping=TextWrapping.Wrap};
        var detail=CodeNote(""); var events=CodeNote(""); var message=CodeNote("");
        var line=new TextBox{Header="Terminal input",MaxLength=4000};
        var sendLine=Button("Send line",(_,_)=>{});
        var eof=Button("End input",(_,_)=>{});
        var terminalControls=Stack(line,Row(sendLine,eof));terminalControls.Visibility=Visibility.Collapsed;
        var start=Button("Start background task",(_,_)=>{});
        var cancel=Button("Cancel task",(_,_)=>{});
        var allow=Button("Review approval",(_,_)=>{});
        var selected=Guid.Empty;
        Guid[] ids=[];
        TaskDecision? pending=null;
        void Refresh()
        {
            try {
                var next=tasks.All(session.Workspace).Order().ToArray();
                if(!ids.SequenceEqual(next)) {
                    ids=next;
                    list.ItemsSource=next.Select(id=>{var text=tasks.Spec(id).Prompt;return text[..Math.Min(72,text.Length)]+" · "+id.ToString("N")[..8];}).ToArray();
                    list.SelectedIndex=Array.IndexOf(next,selected);
                    if(list.SelectedIndex<0 && ids.Length>0)list.SelectedIndex=0;
                }
                if(list.SelectedIndex<0) {selected=Guid.Empty;detail.Text="No background task selected.";events.Text="";pending=null;cancel.IsEnabled=allow.IsEnabled=false;return;}
                selected=ids[list.SelectedIndex];
                var state=tasks.Status(selected);
                pending=state.Decision;
                terminalControls.Visibility=state.Active && tasks.Spec(selected).Terminal!=null?Visibility.Visible:Visibility.Collapsed;
                detail.Text=state.State+" · "+state.Detail;
                events.Text=string.Join("\n",tasks.Tail(selected).Select(line=>{
                    using var doc=System.Text.Json.JsonDocument.Parse(line);
                    var item=doc.RootElement;
                    return item.GetProperty("type").GetString()+"  "+item.GetProperty("detail").ToString();
                }).TakeLast(10));
                cancel.IsEnabled=state.Active;allow.IsEnabled=pending!=null && state.Active;
            } catch(Exception e){message.Text=e.Message;}
        }
        list.SelectionChanged+=(_,_)=>{if(list.SelectedIndex>=0 && list.SelectedIndex<ids.Length)selected=ids[list.SelectedIndex];};
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        timer.Tick+=(_,_)=>Refresh();
        start.Click+=async (_,_)=>{
            try {
                if(session.Model is not {} id)throw new InvalidOperationException("Select a coding model for this session.");
                var choices=Store.Read<List<AiEndpointProfile>>("ai-connections.json")??[];
                TaskModelConnection Binding(Guid selectedId)
                {
                    var profile=choices.SingleOrDefault(p=>p.Id==selectedId)??throw new InvalidOperationException("A selected model connection was removed.");
                    var live=modelServing.Running.FirstOrDefault(s=>s.Profile.Id==selectedId && s.Owner.IsRunning);
                    var key=aiSessionKeys.TryGetValue(selectedId,out var sessionKey)?(string.IsNullOrEmpty(sessionKey)?"":Store.Protect(sessionKey)):profile.Secret;
                    return new(LocalAiClient.ValidateEndpoint((live?.Profile??profile).Endpoint).AbsoluteUri,profile.Model,key,live?.Owner.LifetimeId);
                }
                var main=Binding(id);
                var models=new TaskModelOptions(main,session.ReviewerModel is {} reviewer?Binding(reviewer):null,
                    session.VisualModel is {} visual?Binding(visual):null, session.Reasoning,session.RoleCallLimit,session.RecordModelDecisions);
                var permission=session.Mode=="ask"?CodePermission.ReadOnly:session.Permission;
                start.IsEnabled=false;
                selected=tasks.Start(Path.Combine(AppContext.BaseDirectory,"mason.exe"),session.Workspace,prompt.Text,
                    main.Endpoint,main.Model,permission,main.Lifetime,models:models);
                var started=selected; prompt.Text=""; message.Text="Starting worker…"; Refresh();
                for(int i=0;i<60 && tasks.Status(started).State=="Starting";i++)await Task.Delay(100,entry.Cancellation.Token);
                var state=tasks.Status(started);
                message.Text=state.State=="Running" || state.State=="Waiting for approval" ? "Task attached. You can close Mason and return here." : state.Detail;
            } catch(OperationCanceledException) when(entry.Cancellation.IsCancellationRequested){}
            catch(Exception e){message.Text=e.Message;} finally {start.IsEnabled=true;}
        };
        cancel.Click+=(_,_)=>{try{tasks.Cancel(selected);Refresh();}catch(Exception e){message.Text=e.Message;}};
        allow.Click+=async (_,_)=>{
            var request=pending;var task=selected;
            if(request==null)return;
            try {
                var body=Stack(CodeNote(request.Approval.Description),CodeNote(request.Approval.Detail));
                if(request.Approval.Jpeg is {} jpeg){
                    using var bytes=new MemoryStream(Convert.FromBase64String(jpeg));using var stream=bytes.AsRandomAccessStream();
                    var image=new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();await image.SetSourceAsync(stream);body.Children.Add(new Image{Source=image,MaxHeight=220});
                }
                var dialog=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Review "+request.Approval.Tool,Content=CodeScroll(body),
                    PrimaryButtonText="Allow once",SecondaryButtonText="Decline",CloseButtonText="Later"};
                var result=await dialog.ShowAsync();
                if(result!=ContentDialogResult.None)tasks.Decide(task,request.Id,result==ContentDialogResult.Primary);
                Refresh();
            } catch(Exception e){message.Text=e.Message;}
        };
        sendLine.Click+=(_,_)=>{try{tasks.SendLine(selected,line.Text);line.Text="";}catch(Exception ex){message.Text=ex.Message;}};
        eof.Click+=(_,_)=>{try{tasks.SendLine(selected,null);}catch(Exception ex){message.Text=ex.Message;}};
        var startTerminal=Button("Start workspace terminal",async (_,_)=>{
            try{
                if(session.Mode!="build" || session.Permission!=CodePermission.Auto)throw new InvalidOperationException("Select Auto in Build to start a workspace terminal.");
                var executable=new TextBox{Header="Executable",Text="cmd.exe"};
                var args=new TextBox{Header="Argument array",Text="[\"/D\",\"/Q\"]"};
                var dialog=new ContentDialog{Title="Start a sandboxed terminal",XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Content=Stack(executable,args),PrimaryButtonText="Start",CloseButtonText="Cancel"};
                if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
                var command=new TerminalLaunch(executable.Text.Trim(),System.Text.Json.JsonSerializer.Deserialize<string[]>(args.Text)??[],".");
                selected=tasks.Start(Path.Combine(AppContext.BaseDirectory,"mason.exe"),session.Workspace,"Terminal · "+command.Executable,"","terminal",CodePermission.Auto,terminal:command);
                Refresh();
            }catch(Exception ex){message.Text=ex.Message;}
        });
        var body=Stack(Heading("Background tasks"),list,detail,Row(allow,cancel),events,terminalControls,prompt,start,startTerminal,message);
        body.Padding=new Thickness(18);
        body.Loaded+=(_,_)=>{Refresh();timer.Start();};
        body.Unloaded+=(_,_)=>timer.Stop();
        return CodeScroll(body);
    }
}
