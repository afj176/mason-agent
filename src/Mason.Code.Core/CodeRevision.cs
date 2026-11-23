using System.Security.Cryptography;
using System.Text;

namespace Mason.Code;

internal static class CodeRevision
{
    public static string Measure(CodeWorkspace scope,CodeAcceptancePlan plan,CancellationToken ct,bool pythonSourcesOnly=false)
    {
        var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var c in plan.Checks.Where(c=>c.Kind!="unverified" && (!pythonSourcesOnly || c.Kind=="python_test")))
            if(File.Exists(scope.Resolve(c.Path)))files.Add(scope.Relative(scope.Resolve(c.Path)));
        if(plan.ContractHash!=null) files.Add(BrowserContract.Filename);
        if(plan.Checks.Any(c=>c.Kind.StartsWith("browser_") || c.Kind=="python_test")){
            var inventory=scope.VerificationFiles();
            // Only revisions of completely inventoried dependency sets are reusable as evidence.
            if(inventory.Truncated)throw new InvalidDataException("Verification dependency inventory is incomplete. Select the application subproject.");
            files.UnionWith(inventory.Files.Where(p=>!pythonSourcesOnly || Path.GetExtension(p).Equals(".py",StringComparison.OrdinalIgnoreCase)));
        }
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total=0;
        foreach(var path in files.Order(StringComparer.Ordinal)){
            ct.ThrowIfCancellationRequested();
            using var input=File.OpenRead(scope.Resolve(path));
            if((total+=input.Length)>64L*1024*1024)throw new InvalidDataException("Verification inputs exceed the 64 MiB content-hash budget.");
            hash.AppendData(Encoding.UTF8.GetBytes(path+"\0"));hash.AppendData(SHA256.HashData(input));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
