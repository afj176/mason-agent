using System.Runtime.Versioning;

namespace Mason.Code;

/// <summary>Serializes trusted cache/ACL updates across app hosts without granting a command any additional access.</summary>
[SupportedOSPlatform("windows")]
internal static class CodeHostCoordination
{
    public static string Root
    {
        get
        {
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if(!Path.IsPathFullyQualified(local))throw new IOException("Cannot resolve the host coordination directory.");
            return Path.Combine(local,"Temp","Mason.Code.Host");
        }
    }
    public static IDisposable Acquire(CancellationToken cancellation=default)
    {
        // Workspace and window-station ACLs are shared even when hosts use different data profiles.
        string root=Root;Directory.CreateDirectory(root);
        if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("Host coordination directories cannot be links.");
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        string path=Path.Combine(root,identity.User!.Value+".lock");var elapsed=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("Host coordination files cannot be links.");
                return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            }
            catch(IOException error) when((error.HResult&0xffff) is 32 or 33)
            {
                if(elapsed.Elapsed>TimeSpan.FromSeconds(30))throw new IOException("Another coding host did not release its runtime/ACL update lock; no command was launched.",error);
                if(cancellation.WaitHandle.WaitOne(25))cancellation.ThrowIfCancellationRequested();
            }
        }
    }
}
