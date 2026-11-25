namespace Mason.Code;

public enum CodeShellKind { Direct, GitBash, PowerShell, CommandPrompt, Recommended }
public sealed record CodeShellCommand(string Executable, string[] Arguments);

/// <summary>Explicit shell selection. This is command routing, not process isolation.</summary>
public static class CodeShells
{
    public static string Name(CodeShellKind kind) => kind switch
    {
        CodeShellKind.GitBash => "Git Bash", CodeShellKind.PowerShell => "PowerShell",
        CodeShellKind.CommandPrompt => "Command Prompt", CodeShellKind.Recommended => "Recommended", _ => "Executables only (advanced)"
    };
    public static string? Find(CodeShellKind kind)
    {
        if(kind==CodeShellKind.Recommended)return Find(Recommended());
        if (kind == CodeShellKind.Direct) return null;
        var candidates = new List<string>();
        if (kind == CodeShellKind.GitBash)
        {
            foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
                if (!string.IsNullOrWhiteSpace(folder)) candidates.Add(Path.Combine(folder, "Git", "bin", "bash.exe"));
            foreach (var folder in SearchPath())
                if (File.Exists(Path.Combine(folder, "git.exe"))) candidates.Add(Path.GetFullPath(Path.Combine(folder, "..", "bin", "bash.exe")));
        }
        else if (kind == CodeShellKind.PowerShell)
        {
            candidates.AddRange(SearchPath().Select(p => Path.Combine(p, "pwsh.exe")));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
        }
        else if (kind == CodeShellKind.CommandPrompt) candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
    static IEnumerable<string> SearchPath() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => p.Trim('"')).Where(Path.IsPathFullyQualified);
    public static CodeShellKind Recommended()=>OperatingSystem.IsWindows()&&CodeSandboxTools.GitBashAutoAvailable&&Find(CodeShellKind.GitBash)!=null?CodeShellKind.GitBash:CodeShellKind.CommandPrompt;
    public static CodeShellKind Effective(CodeSession session)=>session.Shell==CodeShellKind.Recommended?Recommended():session.Shell;
    public static string? Unavailable(CodeSession session)
    {
        var kind=Effective(session);
        if(kind==CodeShellKind.Direct)return null;
        if(session.Permission==CodePermission.Auto&&kind==CodeShellKind.PowerShell)
        {
            if(!OperatingSystem.IsWindows()||!CodeSandboxTools.PowerShellAutoAvailable)return "PowerShell Auto requires optional administrator logging setup in Environment.";
            if(!string.Equals(Path.GetFileName(session.ShellExecutable),"pwsh.exe",StringComparison.OrdinalIgnoreCase))return "Auto requires PowerShell 7 (pwsh.exe). Windows PowerShell 5 is not supported.";
        }
        if(session.Permission==CodePermission.Auto&&kind==CodeShellKind.GitBash&&(!OperatingSystem.IsWindows()||!CodeSandboxTools.GitBashAutoAvailable))return "Git Bash has not passed local Auto compatibility checks.";
        var path=session.Shell==CodeShellKind.Recommended?Find(kind):session.ShellExecutable;
        return path==null||!File.Exists(path)?Name(kind)+" is not installed or its saved executable is unavailable.":null;
    }
    public static CodeShellCommand Command(CodeSession session, string script)
    {
        if (string.IsNullOrWhiteSpace(script) || script.Length > 8000 || script.Contains('\0')) throw new ArgumentException("Use a shell command of 1–8,000 characters.");
        if (session.Shell == CodeShellKind.Direct) throw new InvalidOperationException("Shell commands are disabled in Executables only. Select Recommended in Environment, or use run_command.");
        if(Unavailable(session) is {} reason)throw new InvalidOperationException(reason);
        var kind=Effective(session);
        var executable = session.Shell==CodeShellKind.Recommended?Find(kind):session.ShellExecutable;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected shell is unavailable. Select an installed shell again; no fallback shell was substituted.");
        var arguments = kind switch
        {
            CodeShellKind.GitBash => new[] { "--noprofile", "--norc", "-c", script },
            CodeShellKind.PowerShell => new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
            CodeShellKind.CommandPrompt => new[] { "/d", "/s", "/c", script },
            _ => throw new ArgumentException("Unsupported shell selection.")
        };
        return new(Path.GetFullPath(executable), arguments);
    }
    public static string Describe(CodeSession session) => "Operating system: Windows. Selected shell: " + Name(Effective(session)) +
        (session.Shell == CodeShellKind.Direct ? ". No implicit shell; run_command requires an existing .exe. Use list_files for inventory." :
        ". Executable: " + (session.Shell==CodeShellKind.Recommended?Find(Effective(session)):session.ShellExecutable) + ". Use run_shell with " + (Effective(session) == CodeShellKind.GitBash ? "Bash" : Effective(session) == CodeShellKind.PowerShell ? "PowerShell" : "cmd.exe") + " syntax. Commands are noninteractive and start in the requested workspace directory. Shell startup profiles are disabled.")+
        (Effective(session)==CodeShellKind.GitBash?DescribeGitAuto():"");
    static string DescribeGitAuto()
    {
        if(!OperatingSystem.IsWindows())return " Git Bash Auto requires Windows.";
        return CodeSandboxTools.GitBashAutoAvailable?
            " Git Bash Auto is available for this validated local runtime. From Python/Node, resolve bash.exe from the sandbox PATH and pass its full path to subprocess APIs; a bare bash can select Windows' WSL launcher before PATH.":
            " Git Bash Auto is unavailable: "+CodeSandboxTools.GitBashAutoUnavailableReason+" Use reviewed mode or direct Python/Node/Command Prompt.";
    }
}
