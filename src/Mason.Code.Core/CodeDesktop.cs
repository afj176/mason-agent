using System.Diagnostics;
using System.Text.Json;

namespace Mason.Code;

public sealed record DesktopTarget(int ProcessId,long Handle,long Started,string Title)
{
    public override string ToString()=>Title+" · PID "+ProcessId;
}
public sealed record DesktopControl(string Id,string Name,string Type,bool Invoke,bool SetValue);
public sealed class CodeDesktop
{
    public static IReadOnlyList<DesktopTarget> Windows()
    {
        var result=new List<DesktopTarget>();
        using var current=Process.GetCurrentProcess();
        int session=current.SessionId;
        foreach(var p in Process.GetProcesses()){
            using(p)try{
                if(p.Id!=Environment.ProcessId && p.SessionId==session && p.MainWindowHandle!=0 && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    result.Add(new(p.Id,p.MainWindowHandle.ToInt64(),p.StartTime.ToUniversalTime().Ticks,p.MainWindowTitle));
            }catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){}
        }
        return result.OrderBy(p=>p.Title).ToArray();
    }
    readonly Dictionary<string,DesktopControl> controls=[];
    public DesktopTarget Target {get;}
    public CodeDesktop(DesktopTarget target){Target=target;}
    async Task<DesktopControl[]> Call(string operation,DesktopControl? control,string? value,CancellationToken ct)
    {
        using var process=Process.GetProcessById(Target.ProcessId);
        if(process.HasExited||process.StartTime.ToUniversalTime().Ticks!=Target.Started||process.MainWindowHandle.ToInt64()!=Target.Handle)
            throw new InvalidOperationException("The attached window changed. Attach it again.");
        var exe=Path.Combine(AppContext.BaseDirectory,"computer-use","Mason.WindowHost.exe");
        var input=JsonSerializer.Serialize(new{target=Target,operation,control,value});
        var result=await CodeOwnedHelper.Run(exe,[],input,AppContext.BaseDirectory,ct,12);
        return JsonSerializer.Deserialize<DesktopControl[]>(result)??throw new InvalidDataException("Invalid window observation.");
    }
    public DesktopControl Resolve(string id,string operation)
    {
        if(!controls.TryGetValue(id,out var found)||(operation=="invoke"?!found.Invoke:!found.SetValue))
            throw new InvalidOperationException("Observe the window and select an available control.");
        return found;
    }
    public async Task<string> Act(string operation,string id,string? value,CancellationToken ct)
    {
        DesktopControl? target=operation=="observe"?null:Resolve(id,operation);
        if(operation is not ("observe" or "invoke" or "set")||value?.Length>1200)throw new ArgumentException("Unsupported window action.");
        controls.Clear();var fresh=await Call(operation,target,value,ct);
        foreach(var c in fresh)controls[c.Id]=c;
        return JsonSerializer.Serialize(new{window=Target.Title,controls=fresh});
    }
}
