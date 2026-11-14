using System.Diagnostics;
using System.Text.Json;
using Mason.Code;

static class NetworkLifecycleChecks
{
    public static async Task Run(string workspace)
    {
        Directory.CreateDirectory(workspace);
        var budget=new CodeNetworkTransferBudget();
        for(int stream=0;stream<4;stream++){long bytes=0;for(int chunk=0;chunk<1024;chunk++)budget.Consume(ref bytes,32768);}
        long extra=0;try{budget.Consume(ref extra,1);throw new Exception("Aggregate transfer quota allowed overflow");}catch(IOException){}
        var one=new CodeNetworkTransferBudget();long direction=0;for(int chunk=0;chunk<1024;chunk++)one.Consume(ref direction,32768);
        try{one.Consume(ref direction,1);throw new Exception("Directional transfer quota allowed overflow");}catch(IOException){}
        Console.WriteLine("PASS production transfer budget: exact direction and aggregate bounds");
        string script=Path.Combine(workspace,"active-relay.cjs");
        File.WriteAllText(script,"const fs=require('node:fs'),cp=require('node:child_process'),net=require('node:net');const child=cp.spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'});fs.writeFileSync('child-pid.txt',String(child.pid));const socket=net.connect(process.env.MASON_NETWORK_PIPE,()=>socket.write(JSON.stringify({host:'pypi.org',port:443})+'\\n'));socket.on('data',()=>{});setInterval(()=>{},1000);");
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var access=new CodeNetworkAccess((host,_)=>Task.FromResult(host=="pypi.org"));
        var operation=CodeCommand.Run(CodeCommand.ResolveExecutable("node.exe",workspace),[script],workspace,cancellation.Token,workspace,access);
        var wait=Stopwatch.StartNew();
        while(!access.Receipts.Any(x=>x=="Connected pypi.org:443")&&!operation.IsCompleted&&wait.Elapsed<TimeSpan.FromSeconds(30))await Task.Delay(100);
        if(!access.Receipts.Any(x=>x=="Connected pypi.org:443")){cancellation.Cancel();try{await operation;}catch(OperationCanceledException){}throw new Exception("No active authenticated tunnel was established: "+string.Join("; ",access.Receipts));}
        await Task.Delay(150);var elapsed=Stopwatch.StartNew();cancellation.Cancel();
        try{await operation;}catch(OperationCanceledException){}
        if(elapsed.Elapsed>TimeSpan.FromSeconds(10))throw new Exception("Active tunnel cancellation exceeded bound");
        int pid=int.Parse(File.ReadAllText(Path.Combine(workspace,"child-pid.txt")));
        try{using var child=Process.GetProcessById(pid);if(!child.HasExited)throw new Exception("Owned descendant survived cancellation");}catch(ArgumentException){}
        Console.WriteLine("PASS active authenticated tunnel cancellation and descendant teardown: "+elapsed.ElapsedMilliseconds+" ms");
        File.WriteAllText(Path.Combine(workspace,"receipts.json"),JsonSerializer.Serialize(access.Receipts));
    }
}
