using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mason;

public sealed record GpuReading(string Id, string Name, double? Load, double? TotalMiB, double? UsedMiB, double? FreeMiB);
public sealed record GpuComputeProcess(string GpuId, int Pid, string Name, double? MemoryMiB);
public sealed record GpuSample(DateTimeOffset At, GpuReading[] Adapters, GpuComputeProcess[] Processes, string? Error, string? ProcessError);

/// <summary>Read-only NVIDIA telemetry. Never loads weights or changes driver configuration.</summary>
public static class GpuTelemetry
{
    public static string? FindTool() => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
    }.FirstOrDefault(File.Exists);

    public static async Task<GpuSample> Read(CancellationToken ct)
    {
        var tool = FindTool();
        if (tool == null) return new(DateTimeOffset.Now, [], [], "NVIDIA telemetry unavailable. Install or update your NVIDIA driver to provide nvidia-smi.", null);
        try
        {
            var adapters = ParseAdapters(await Query(tool, "index,uuid,name,utilization.gpu,memory.total,memory.used,memory.free", false, ct));
            if (adapters.Length == 0) return new(DateTimeOffset.Now, [], [], "No NVIDIA GPUs reported by the driver.", null);
            try { return new(DateTimeOffset.Now, adapters, ParseProcesses(await Query(tool, "gpu_uuid,pid,process_name,used_gpu_memory", true, ct)), null, null); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return new(DateTimeOffset.Now, adapters, [], null, "Compute process details unavailable from the driver."); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(DateTimeOffset.Now, [], [], "GPU reading unavailable. The driver query failed or timed out; retrying automatically.", null); }
    }

    static async Task<string> Query(string tool, string fields, bool processes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new(tool) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true } };
        process.StartInfo.ArgumentList.Add($"--query-{(processes ? "compute-apps" : "gpu")}={fields}");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        if (!process.Start()) throw new IOException("GPU query could not start.");
        try
        {
            var output = ReadBounded(process.StandardOutput, timeout.Token);
            var error = ReadBounded(process.StandardError, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new IOException("GPU driver query failed.");
            return await output;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree:true); }
    }

    static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (text.Length + count > 131072) throw new InvalidDataException("GPU query output exceeded its limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    public static double? Number(string value) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : null;

    public static GpuReading[] ParseAdapters(string csv) => Rows(csv).Select(row => {
        if (row.Length != 7 || string.IsNullOrWhiteSpace(row[1])) throw new InvalidDataException("Malformed GPU telemetry.");
        var load = Number(row[3]);
        return new GpuReading(row[1], row[2], load <= 100 ? load : null, Number(row[4]), Number(row[5]), Number(row[6]));
    }).ToArray();

    public static GpuComputeProcess[] ParseProcesses(string csv) => Rows(csv).Select(row => {
        if (row.Length != 4 || !int.TryParse(row[1], out var pid) || pid <= 0) throw new InvalidDataException("Malformed GPU process telemetry.");
        return new GpuComputeProcess(row[0], pid, Path.GetFileName(row[2]), Number(row[3]));
    }).ToArray();

    static IEnumerable<string[]> Rows(string csv)
    {
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false;
            for (int i=0;i<line.Length;i++)
            {
                char ch=line[i];
                if(ch=='"') { if(quoted && i+1<line.Length && line[i+1]=='"') {field.Append('"');i++;} else quoted=!quoted; }
                else if(ch==',' && !quoted) {fields.Add(field.ToString().Trim());field.Clear();}
                else field.Append(ch);
            }
            if(quoted) throw new InvalidDataException("Malformed GPU CSV quoting.");
            fields.Add(field.ToString().Trim());yield return fields.ToArray();
        }
    }
}
