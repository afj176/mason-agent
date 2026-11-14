using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mason;

/// <summary>Starts suspended, assigns a kill-on-close job, then resumes; children cannot escape ownership during startup.</summary>
public sealed class OwnedModelProcess : IDisposable
{
    readonly SafeFileHandle job;
    readonly SafeFileHandle nativeProcess;
    public uint ExitCode { get { if(!GetExitCodeProcess(nativeProcess,out var code))throw new Win32Exception();return code; } }
    public Process Process { get; }
    bool disposed;
    public bool IsRunning { get { if (disposed) return false; try { return !Process.HasExited; } catch (InvalidOperationException) { return false; } } }
    public (ulong PeakProcessCommittedBytes,ulong PeakJobCommittedBytes) AllocationPeaks()
    {
        if(!QueryInformationJobObject(job,9,out var value,(uint)Marshal.SizeOf<ExtendedLimit>(),IntPtr.Zero))throw new Win32Exception();
        return(value.PeakProcess.ToUInt64(),value.PeakJob.ToUInt64());
    }
    OwnedModelProcess(SafeFileHandle job, Process process, SafeFileHandle nativeProcess) { this.job = job; Process = process; this.nativeProcess=nativeProcess; }
    public static OwnedModelProcess Start(string executable, IReadOnlyList<string> arguments, string logPath, string? extraDllPath=null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var fullPath = Path.GetFullPath(executable);
        if (!File.Exists(fullPath) || !fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select an existing local executable.");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())) { job.Dispose(); throw new Win32Exception(); }
        var security = new Security { Length = Marshal.SizeOf<Security>(), Inherit = true };
        using var output = CreateFile(logPath, 0x40000000, 3, ref security, 2, 0x80, IntPtr.Zero);
        using var input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0x80, IntPtr.Zero);
        if (output.IsInvalid || input.IsInvalid) { job.Dispose(); throw new Win32Exception(); }
        var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Flags = 0x100, Input = input.DangerousGetHandle(), Output = output.DangerousGetHandle(), Error = output.DangerousGetHandle() };
        var command = new System.Text.StringBuilder(string.Join(" ", new[] { fullPath }.Concat(arguments).Select(Quote)));
        IntPtr environment=IntPtr.Zero;Info info;
        try
        {
            if(extraDllPath!=null)
            {
                var variables=Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(x=>(string)x.Key,x=>(string?)x.Value??"",StringComparer.OrdinalIgnoreCase);
                variables["PATH"]=extraDllPath+Path.PathSeparator+variables.GetValueOrDefault("PATH","");
                environment=Marshal.StringToHGlobalUni(string.Join("\0",variables.OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).Select(x=>x.Key+"="+x.Value))+"\0\0");
            }
            if(!CreateProcess(fullPath,command,IntPtr.Zero,IntPtr.Zero,true,0x08000004u+(environment!=IntPtr.Zero?0x400u:0u),environment,Path.GetDirectoryName(fullPath),ref startup,out info))
            {job.Dispose();throw new Win32Exception();}
        }
        finally {if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);}
        try
        {
            if (!AssignProcessToJobObject(job, info.Process)) throw new Win32Exception();
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
            return new(job, Process.GetProcessById((int)info.Id),new SafeFileHandle(info.Process,true));
        }
        catch { TerminateProcess(info.Process, 1); CloseHandle(info.Process); job.Dispose(); throw; }
        finally { CloseHandle(info.Thread); }
    }
    public void Dispose() { if (disposed) return; disposed = true; job.Dispose(); Process.Dispose(); nativeProcess.Dispose(); }
    public static string Quote(string argument)
    {
        // Windows CommandLineToArgvW convention; no shell interprets this string.
        var result = new System.Text.StringBuilder("\""); int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit { public long PerProcess, PerJob; public uint LimitFlags; public UIntPtr MinWorking, MaxWorking; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit { public BasicLimit Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [StructLayout(LayoutKind.Sequential)] struct Security { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Startup { public int Size; public string? Reserved, Desktop, Title; public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags; public ushort Show,ReservedSize; public IntPtr ReservedBytes, Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] struct Info { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryInformationJobObject(SafeFileHandle job,int kind,out ExtendedLimit info,uint length,IntPtr returned);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref Security security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint="CreateProcessW", SetLastError=true, CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcess(string application, System.Text.StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref Startup startup, out Info info);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetExitCodeProcess(SafeFileHandle process,out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint exit);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
