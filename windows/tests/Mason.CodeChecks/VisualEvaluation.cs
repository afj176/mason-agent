using System.Diagnostics;
using System.Text.Json;
using Mason;
using Mason.Code;

internal static class VisualEvaluation
{
    public static async Task Run(string endpoint,string id,string evidence)
    {
        var launch=new ProcessStartInfo(CodeCommand.ResolveExecutable("pwsh",AppContext.BaseDirectory)){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"-NoProfile","-STA","-File",Path.Combine(AppContext.BaseDirectory,"CanvasFixture.ps1")})launch.ArgumentList.Add(arg);
        using var process=Process.Start(launch)??throw new IOException("Unable to start canvas fixture.");
        try{
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var model=new CodeLocalModel(new AiEndpointProfile{Endpoint=endpoint,Model=id,ManagedCapability=AiCapability.Chat},"","default",_=>{});
            DesktopTarget? target=null;
            for(int i=0;i<50 && target==null;i++){await Task.Delay(150);target=CodeDesktop.Windows().SingleOrDefault(w=>w.ProcessId==process.Id);}
            var desktop=new CodeDesktop(target??throw new IOException("Fixture window unavailable."));
            var red=await desktop.Frame(timeout.Token);
            var click=await model.See("Click near the center of the red area.",red,false,timeout.Token);
            if(click.Action!="click")throw new IOException("Vision model did not propose the requested click: "+JsonSerializer.Serialize(click));
            await desktop.Apply(red,click,timeout.Token);
            var green=await desktop.Frame(timeout.Token);process.Refresh();
            if(green.Hash==red.Hash || process.MainWindowTitle!="Canvas left clicked")throw new IOException("Red target was missed: "+JsonSerializer.Serialize(click));
            bool stale=false;try{await desktop.Apply(red,click,timeout.Token);}catch(InvalidDataException e) when(e.Message.Contains("frame changed")){stale=true;}
            if(!stale)throw new IOException("A stale visual action was accepted.");
            var drag=await model.See("Drag from the center of the green area to the center of the blue area.",green,false,timeout.Token);
            if(drag.Action!="drag")throw new IOException("Vision model did not propose the requested drag: "+JsonSerializer.Serialize(drag));
            await desktop.Apply(green,drag,timeout.Token);
            var yellow=await desktop.Frame(timeout.Token);process.Refresh();
            if(yellow.Hash==green.Hash || process.MainWindowTitle!="Canvas drag completed")throw new IOException("The native drag did not reach its target: "+JsonSerializer.Serialize(drag));
            var final=await model.See("Describe the colors now visible.",yellow,true,timeout.Token);
            File.WriteAllText(evidence,JsonSerializer.Serialize(new{Model=id,Click=click,Drag=drag,StaleRejected=stale,Final=final.Observation,FrameHashes=new[]{red.Hash,green.Hash,yellow.Hash},NativeState=process.MainWindowTitle},new JsonSerializerOptions{WriteIndented=true}));
        }finally{if(!process.HasExited){process.CloseMainWindow();if(!process.WaitForExit(2500))process.Kill(true);}}
    }
}
