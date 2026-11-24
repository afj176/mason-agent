using System.Diagnostics;
using System.Text.Json;
using Mason.Code;

internal static class DesktopIntegration
{
    internal static async Task Run()
    {
        string exe=CodeCommand.ResolveExecutable("pwsh",AppContext.BaseDirectory);
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        foreach(string a in new[]{"-NoProfile","-STA","-File",Path.Combine(AppContext.BaseDirectory,"WindowFixture.ps1")})start.ArgumentList.Add(a);
        using var process=Process.Start(start)??throw new Exception("Unable to launch test window.");
        try{
            DesktopTarget? target=null;
            for(int i=0;i<50&&target==null;i++){await Task.Delay(200);target=CodeDesktop.Windows().SingleOrDefault(w=>w.ProcessId==process.Id);}
            var desktop=new CodeDesktop(target??throw new Exception("Test window never appeared. "+(process.HasExited?process.StandardError.ReadToEnd():"")));
            async Task<JsonElement> Act(string op,string id="",string? value=null)=>JsonSerializer.Deserialize<JsonElement>(await desktop.Act(op,id,value,default)).GetProperty("controls");
            var fields=await Act("observe");
            string Id(JsonElement nodes,string name)=>nodes.EnumerateArray().Single(n=>n.GetProperty("Name").GetString()==name).GetProperty("Id").GetString()!;
            var next=await Act("set",Id(fields,"Receipt input"),"MASON-42");
            var final=await Act("invoke",Id(next,"Record receipt"));
            if(!final.EnumerateArray().Any(n=>n.GetProperty("Name").GetString()=="Recorded MASON-42")){
                await Task.Delay(250);final=await Act("observe");
                if(!final.EnumerateArray().Any(n=>n.GetProperty("Name").GetString()=="Recorded MASON-42"))throw new Exception("Native UI result missing.");
            }
            Console.WriteLine("PASS: observe, set, invoke and re-observe the selected native Windows window.");
        }finally{if(!process.HasExited){process.CloseMainWindow();if(!process.WaitForExit(2500))process.Kill(true);}}
    }
}
