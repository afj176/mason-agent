using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

public sealed record CodeCommandResult(int ExitCode, string Output, bool Truncated)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CodeCommandFailure? Failure {get;init;}
}

/// <summary>Noninteractive Windows commands, assigned to a kill-on-close job before the first instruction runs.</summary>
public static class CodeCommand
{
    public static string ResolveExecutable(string value, string workspace)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1000 || value.Contains('\0')) throw new ArgumentException("Choose an executable.");
        string? executable = null;
        if (Path.IsPathFullyQualified(value)) executable = Path.GetFullPath(value);
        else if (value.IndexOfAny(['/', '\\', ':']) < 0)
        {
            var name = value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (!Path.IsPathFullyQualified(directory)) continue;
                var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), name));
                if (candidate.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(candidate)) { executable = candidate; break; }
            }
        }
        if (executable == null || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an existing .exe. Shell scripts require an explicit interpreter and reviewed arguments.");
        return executable;
    }
    public static Task<CodeCommandResult> Run(string executable, IReadOnlyList<string> arguments, string directory, CancellationToken cancellation, string? sandboxWorkspace = null, CodeNetworkAccess? network=null,
        TimeSpan? timeout=null,Action<Func<string>>? observeLog=null,Action<Func<int,bool>>? observeOwner=null,CodeTerminalInput? terminal=null)
    {
        if(sandboxWorkspace==null)return RunCore(executable,arguments,directory,cancellation,null,null,timeout,observeLog,observeOwner,terminal);
        // Windows debug events belong to the OS thread that created the process, not an async continuation.
        var completion=new TaskCompletionSource<CodeCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(()=>
        {
            try { completion.TrySetResult(RunCore(executable,arguments,directory,cancellation,sandboxWorkspace,network,timeout,observeLog,observeOwner,terminal).GetAwaiter().GetResult()); }
            catch(OperationCanceledException){completion.TrySetCanceled(cancellation);}
            catch(Exception error){completion.TrySetException(error);}
        }) { IsBackground=true,Name="Mason Agent Auto compatibility broker" }.Start();
        return completion.Task;
    }
    static async Task<CodeCommandResult> RunCore(string executable,IReadOnlyList<string> arguments,string directory,CancellationToken cancellation,string? sandboxWorkspace,CodeNetworkAccess? network,TimeSpan? timeout,Action<Func<string>>? observeLog,Action<Func<int,bool>>? observeOwner,CodeTerminalInput? terminal)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This command host currently supports Windows.");
        cancellation.ThrowIfCancellationRequested();
        using var sandbox = sandboxWorkspace == null ? null : new CodeSandbox(sandboxWorkspace,executable,cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (sandbox != null) executable = sandbox.Executable;
        if(sandbox?.Handoff is {} launch&&executable.Equals(launch.LogicalShell,StringComparison.OrdinalIgnoreCase))
        {
            arguments=[launch.LogicalShell,CommandLine(executable,arguments)];executable=launch.Bootstrap;
        }
        var log = Path.Combine(sandbox?.Scratch ?? Path.GetTempPath(), "mason-command-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var job = CreateJobObject(IntPtr.Zero, null); if (job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new Win32Exception();
            using var relay=sandbox!=null&&network!=null?new CodeNetworkRelay(sandbox,job.DangerousGetHandle(),network,cancellation):null;
            if(relay!=null)sandbox!.ConfigureNetwork(relay.Path);
            var security = new Security { Length = Marshal.SizeOf<Security>(), Inherit = true };
            var nativeLog=log.StartsWith(@"\\",StringComparison.Ordinal) ? @"\\?\UNC\"+log[2..] : @"\\?\"+log;
            using var output = CreateFile(nativeLog, 0x40000000, 3, ref security, 2, 0x80, IntPtr.Zero);
            if (output.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot create the owned command output log.");
            using var live=observeLog==null?null:File.OpenHandle(log,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            if(live!=null)observeLog!(()=>{
                if(live.IsClosed)return "";
                try{
                    long size=RandomAccess.GetLength(live);
                    var bytes=new byte[(int)Math.Min(size,8000)];
                    return Encoding.UTF8.GetString(bytes,0,RandomAccess.Read(live,bytes,size-bytes.Length));
                }catch(ObjectDisposedException){return "";}
            });
            using var input = CreateFile(sandbox==null?"NUL":@"\\?\GLOBALROOT\Device\Null", sandbox==null?0x80000000:0xc0000000, 3, ref security, 3, 0x80, IntPtr.Zero);
            IntPtr stdin=terminal?.Child.DangerousGetHandle()??input.DangerousGetHandle();
            if (input.IsInvalid) throw new Win32Exception();
            using var outputPipe=sandbox==null?null:new CodeOutputPipe();
            var standardOutput=outputPipe?.Writer??output;
            int attributeCount = sandbox == null ? 1 : 3;
            IntPtr size = IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
            var attributes = Marshal.AllocHGlobal(size); var handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Info info = default; bool initialized = false;
            var capabilities = IntPtr.Zero; var policy = IntPtr.Zero; var environment = IntPtr.Zero;
            try
            {
                if (!InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size)) throw new Win32Exception();
                initialized = true;
                Marshal.WriteIntPtr(handles, 0, stdin); Marshal.WriteIntPtr(handles, IntPtr.Size, standardOutput.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (IntPtr)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                if (sandbox != null)
                {
                    capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<Capabilities>());
                    Marshal.StructureToPtr(new Capabilities { Sid = sandbox.Sid, CapabilitySids = sandbox.Capabilities, Count = sandbox.CapabilityCount }, capabilities, false);
                    policy = Marshal.AllocHGlobal(4); Marshal.WriteInt32(policy, 1);
                    if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, capabilities, (IntPtr)Marshal.SizeOf<Capabilities>(), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                    if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x2000f, policy, (IntPtr)4, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                    environment = sandbox.EnvironmentBlock();
                }
                var startup = new StartupEx { Startup = new Startup { Size = Marshal.SizeOf<StartupEx>(), Desktop = sandbox?.DesktopName, Flags = 0x100, Input = stdin, Output = standardOutput.DangerousGetHandle(), Error = standardOutput.DangerousGetHandle() }, Attributes = attributes };
                var command = new StringBuilder(CommandLine(executable, arguments));
                bool created = sandbox == null
                    ? CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080004u, environment, directory, ref startup, out info)
                    : CreateProcessAsUser(sandbox.RestrictedToken, executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080405u, environment, directory, ref startup, out info);
                if (!created) throw new Win32Exception();
            }
            finally { if (initialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(handles); Marshal.FreeHGlobal(capabilities); Marshal.FreeHGlobal(policy); Marshal.FreeHGlobal(environment); }
            terminal?.Started();
            using var nativeProcess = new SafeFileHandle(info.Process, true);
            var drain=outputPipe?.Drain(output)??Task.CompletedTask;
            try
            {
                if (!AssignProcessToJobObject(job, info.Process)) throw new Win32Exception();
                observeOwner?.Invoke(pid=>{
                    try{using var child=System.Diagnostics.Process.GetProcessById(pid);return IsProcessInJob(child.SafeHandle,job,out var inside)&&inside;}
                    catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}catch(Win32Exception){return false;}
                });
                sandbox?.VerifyToken(info.Process);
                if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
            }
            catch { TerminateProcess(info.Process, 1); throw; }
            finally { CloseHandle(info.Thread); }
            var duration=timeout??TimeSpan.FromSeconds(120);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(duration);
            bool truncated = false; uint nativeExit = 0;string nativeDiagnostics="";
            try
            {
                if(sandbox!=null)
                {
                    using var broker=new CodeRuntimeBroker(sandbox,job.DangerousGetHandle(),input);
                    (nativeExit,truncated)=broker.Pump(info.Id,log,deadline.Token);
                    if(Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS") is "1" or "2")nativeDiagnostics=broker.Diagnostics;
                }
                else
                while (true)
                {
                    var wait = WaitForSingleObject(nativeProcess, 0);
                    if (wait == uint.MaxValue) throw new Win32Exception();
                    if (wait == 0) { if (!GetExitCodeProcess(nativeProcess, out nativeExit)) throw new Win32Exception(); break; }
                    if (new FileInfo(log).Length > 256 * 1024) { truncated = true; job.Dispose(); break; }
                    await Task.Delay(100, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException($"Command exceeded {duration.TotalSeconds:0} seconds and its owned process tree was stopped."); }
            // Close the job before reading so still-running descendants cannot continue writing to the log.
            var exit = truncated ? -1 : unchecked((int)nativeExit);relay?.Dispose();job.Dispose();
            await drain;
            using var read = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[(int)Math.Min(read.Length, 64 * 1024)]; int at = 0, count;
            while (at < bytes.Length && (count = await read.ReadAsync(bytes.AsMemory(at), cancellation)) > 0) at += count;
            var utf16 = at >= 2 && (bytes[0] == 255 && bytes[1] == 254 || at >= 8 && Enumerable.Range(0, Math.Min(at / 2, 30)).Count(i => bytes[i * 2 + 1] == 0) * 2 > Math.Min(at / 2, 30));
            var text = utf16 ? Encoding.Unicode.GetString(bytes, bytes[0] == 255 && bytes[1] == 254 ? 2 : 0, at - (bytes[0] == 255 && bytes[1] == 254 ? 2 : 0)) : Encoding.UTF8.GetString(bytes, 0, at);
            if(nativeDiagnostics.Length>0)text+="\n[Owned runtime diagnostics]\n"+nativeDiagnostics;
            if (sandbox != null && exit is unchecked((int)0xC0000022) or unchecked((int)0xC0000142)) text += "\nThe selected runtime could not initialize under Auto isolation. No unrestricted fallback was run.";
            return new(exit, text + (read.Length > bytes.Length || truncated ? "\n[Output clipped; command output limit reached.]" : ""), read.Length > bytes.Length || truncated)
                {Failure=CodeCommandFailure.Classify(exit,text,sandbox!=null)};
        }
        finally { try { File.Delete(log); } catch (IOException) { } }
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool IsProcessInJob(SafeProcessHandle process,SafeFileHandle job,out bool inside);
    /// <summary>CMD consumes one literal script after /c; it does not decode CRT backslash escapes.</summary>
    public static string CommandLine(string executable,IReadOnlyList<string> arguments)
    {
        if(arguments.Any(a=>a.Contains('\0')))throw new ArgumentException("Command arguments cannot contain NUL characters.");
        var systemCmd=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
        if(OperatingSystem.IsWindows()&&Path.GetFullPath(executable).Equals(systemCmd,StringComparison.OrdinalIgnoreCase))
        {
            if(arguments.Count==1&&arguments[0]=="/?")return Quote(executable)+" /?";
            int command=-1;
            for(int i=0;i<arguments.Count;i++)if(arguments[i].Equals("/c",StringComparison.OrdinalIgnoreCase)){command=i;break;}
            if(command<0||arguments.Count!=command+2)throw new ArgumentException("Noninteractive cmd.exe requires /c followed by one complete command string. Use run_shell for the selected shell.");
            var switches=arguments.Take(command).ToList();
            if(switches.Any(a=>!a.StartsWith('/')||a.Any(char.IsWhiteSpace)||a.Contains('"')||a.Equals("/k",StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Use individual CMD switches before /c and one literal script after it.");
            if(!switches.Contains("/d",StringComparer.OrdinalIgnoreCase))switches.Add("/d");
            if(!switches.Contains("/s",StringComparer.OrdinalIgnoreCase))switches.Add("/s");
            return Quote(executable)+" "+string.Join(" ",switches)+" /c \""+arguments[command+1]+"\"";
        }
        return string.Join(" ",new[]{executable}.Concat(arguments).Select(Quote));
    }
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] struct Capabilities { public IntPtr Sid, CapabilitySids; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit { public long PerProcess, PerJob; public uint LimitFlags; public UIntPtr MinWorking, MaxWorking; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit { public BasicLimit Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [StructLayout(LayoutKind.Sequential)] struct Security { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Startup { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags; public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] struct StartupEx { public Startup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct Info { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref Security security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupEx startup, out Info info);
    [DllImport("advapi32.dll",EntryPoint="CreateProcessAsUserW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessAsUser(IntPtr token,string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out Info info);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint exit);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
}


