using System.Net.Sockets;
using System.Text.Json;
using Mason;

static class SandboxedFlintEvaluation
{
    public static async Task Run(string runtime,string weights,string destination,bool resume=false,bool batch=false,bool native=false,bool gitBash=false)
    {
        var root=Path.GetFullPath(destination);if(!resume&&Directory.Exists(root))throw new IOException("Use a fresh evaluation folder.");Directory.CreateDirectory(root);
        const int port=18138;string alias=native?"mason-flint-native-rc8":"mason-flint-sandbox-rc7";
        using(var portProbe=new TcpListener(System.Net.IPAddress.Loopback,port)){portProbe.Start();portProbe.Stop();}
        string endpoint=$"http://127.0.0.1:{port}";
        string stage=resume?"continuation-"+DateTimeOffset.UtcNow.ToString("HHmmss"):"initial";
        var arguments=new[]{"-m",Path.GetFullPath(weights),"--host","127.0.0.1","--port",port.ToString(),"--alias",alias,"-c","32768","--parallel","1","-b","256","-ub","128","--reasoning","auto","--spec-type","none","--device","CUDA0","--split-mode","none","-ngl","99"};
        // Job ownership guarantees GPU cleanup even if this evaluation process is interrupted.
        using(var owner=OwnedModelProcess.Start(runtime,arguments,Path.Combine(root,"server-"+stage+".log")))
        {
            File.WriteAllText(Path.Combine(root,"server-launch-"+stage+".json"),JsonSerializer.Serialize(new{runtime,weights,arguments,pid=owner.Process.Id,ownership="kill-on-close Windows job"},new JsonSerializerOptions{WriteIndented=true}));
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
            while(true)
            {
                deadline.Token.ThrowIfCancellationRequested();if(!owner.IsRunning)throw new Exception("Model server exited during startup.");
                try{using var response=await http.GetAsync(endpoint+"/health",deadline.Token);if(response.IsSuccessStatusCode)break;}catch(HttpRequestException){}catch(TaskCanceledException)when(!deadline.IsCancellationRequested){}
                await Task.Delay(400,deadline.Token);
            }
            if(batch)await SandboxedBatchEvaluation.Run(endpoint,alias,Path.Combine(root,"task"),resume,native?"default":null);
            else if(resume)await SandboxedFlintContinuation.Run(endpoint,alias,Path.Combine(root,"task"),stage);
            else await RealModelEvaluation.Run(endpoint,alias,Path.Combine(root,"task"),native?"default":"off",autoSandbox:true,gitBash:gitBash);
        }
        File.WriteAllText(Path.Combine(root,"server-stopped-"+stage+".json"),JsonSerializer.Serialize(new{ownedServerDisposed=true,at=DateTimeOffset.UtcNow}));
    }
}
