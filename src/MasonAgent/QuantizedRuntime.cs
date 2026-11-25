using System.Text.Json;

namespace Mason;

public static class QuantizedRuntime
{
    public static string ManagedPython => Path.Combine(Store.Root,"runtimes","quantized-text","Scripts","python.exe");
    // Runtimes prepared by earlier builds carry a "dbcowboy-" marker; it still counts. Keep in step with scripts/Setup-QuantizedRuntime.py.
    static string Marker{get{var folder=Path.Combine(Store.Root,"runtimes","quantized-text");var current=Path.Combine(folder,"mason-quantized-runtime.json");return File.Exists(current)?current:Path.Combine(folder,"dbcowboy-quantized-runtime.json");}}
    public static bool IsPrepared
    {
        get {try {using var doc=JsonDocument.Parse(File.ReadAllText(Marker));return File.Exists(ManagedPython)&&doc.RootElement.TryGetProperty("ready",out var ready)&&ready.ValueKind==JsonValueKind.True;}catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){return false;}}
    }

    public static async Task<string> Prepare(string sourcePython, string scripts, CancellationToken cancellation, Action<string> progress)
    {
        var folder=Path.Combine(Store.Root,"runtimes","quantized-text");
        var manifest=Path.Combine(Store.Root,"runtime-setup","quantized-status.json");
        var log=Path.Combine(Store.Root,"runtime-setup","quantized-setup.log");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        if(File.Exists(manifest))File.WriteAllText(manifest,"{}");
        using var process=OwnedModelProcess.Start(sourcePython,new[] { Path.Combine(scripts,"Setup-QuantizedRuntime.py"),"--target",folder,"--manifest",manifest },log);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromMinutes(15));
        string message="Preparing quantized runtime…";bool ready=false;
        try
        {
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
                    if(doc.RootElement.TryGetProperty("status",out var status))message=status.GetString()??message;
                    ready=doc.RootElement.TryGetProperty("ready",out var value)&&value.ValueKind==JsonValueKind.True;
                    progress(message);
                }catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException) { }
                if(process.Process.HasExited)break;
                await Task.Delay(500,timeout.Token);
            }
            if(process.ExitCode!=0 || !ready || !File.Exists(ManagedPython))throw new InvalidOperationException(message+" Setup log: "+log);
            return ManagedPython;
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){throw new TimeoutException("Quantized runtime setup exceeded 15 minutes. Setup log: "+log);}
    }
}
