using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Mason.Code;
using Microsoft.Win32.SafeHandles;

[SupportedOSPlatform("windows")]
static class NetworkIsolationChecks
{
    public static async Task Run(string workspace)
    {
        Directory.CreateDirectory(workspace);
        using var sandbox=new CodeSandbox(workspace,CodeCommand.ResolveExecutable("cmd.exe",workspace),default);
        using var job=CreateJobObject(IntPtr.Zero,null);
        if(job.IsInvalid)throw new Exception("QA job creation failed");
        int approvals=0;
        var access=new CodeNetworkAccess((_,_)=>{approvals++;return Task.FromResult(true);});
        using(var relay=new CodeNetworkRelay(sandbox,job.DangerousGetHandle(),access,default))
        {
            for(int i=0;i<64;i++)
            {
                using var client=new NamedPipeClientStream(".",relay.Name,PipeDirection.InOut,PipeOptions.Asynchronous);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(deadline.Token);
                using var reader=new StreamReader(client);
                string? response=await reader.ReadLineAsync(deadline.Token);
                if(response==null||!response.Contains("not a member"))throw new Exception("Foreign process was not denied: "+response);
            }
            using var extra=new NamedPipeClientStream(".",relay.Name,PipeDirection.InOut,PipeOptions.Asynchronous);
            using var bound=new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try{await extra.ConnectAsync(bound.Token);throw new Exception("Connection limit was not enforced");}catch(OperationCanceledException){}
        }
        if(approvals!=0)throw new Exception("An unauthenticated client reached destination approval");
        Console.WriteLine("PASS foreign process denied before approval; 64-connection bound; relay shutdown");
        using var cancel=new CancellationTokenSource();
        using(var relay=new CodeNetworkRelay(sandbox,job.DangerousGetHandle(),access,cancel.Token)){cancel.Cancel();}
        Console.WriteLine("PASS pending listener cancellation");
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
}
