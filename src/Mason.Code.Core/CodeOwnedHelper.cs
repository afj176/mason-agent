using System.Diagnostics;
using System.Text;

namespace Mason.Code;

internal static class CodeOwnedHelper
{
    internal static async Task<string> Run(string exe,IEnumerable<string> args,string input,string directory,CancellationToken ct,int seconds=25,int outputLimit=240000)
    {
        var settings=new ProcessStartInfo(exe){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in args)settings.ArgumentList.Add(arg);
        foreach(var key in new[]{"NODE_OPTIONS","NODE_PATH","PYTHONPATH","PYTHONHOME","GIT_DIR","GIT_WORK_TREE","GIT_INDEX_FILE","GIT_CONFIG_PARAMETERS","GIT_CONFIG_COUNT"})settings.Environment.Remove(key);
        using var process=Process.Start(settings)??throw new IOException("Could not start "+Path.GetFileName(exe));
        using var timer=CancellationTokenSource.CreateLinkedTokenSource(ct);timer.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var stop=timer.Token.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
        async Task<string> Read(StreamReader reader){
            var result=new StringBuilder();var buf=new char[2048];
            while(true){int count=await reader.ReadAsync(buf,timer.Token);if(count==0)return result.ToString();
                if(result.Length+count>outputLimit){timer.Cancel();throw new InvalidDataException("Helper output limit exceeded.");}
                result.Append(buf,0,count);}
        }
        var output=Read(process.StandardOutput);var error=Read(process.StandardError);
        try{
            await process.StandardInput.WriteAsync(input.AsMemory(),timer.Token);process.StandardInput.Close();
            await Task.WhenAll(output,error,process.WaitForExitAsync(timer.Token));
            if(process.ExitCode!=0)throw new InvalidDataException(Path.GetFileName(exe)+" failed: "+(await error).Trim());
            return (await output).Trim();
        }finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
    }
}
