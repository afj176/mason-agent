using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record CodeCommandFailure(string Category,string Summary,string Guidance,bool Retryable=false)
{
    // Recognize this specific loader failure only. An ordinary EACCES/EPERM or SyntaxError is not a runtime diagnosis.
    internal static CodeCommandFailure? Classify(int exit,string output,bool sandboxed)
    {
        if(!sandboxed||exit==0||!output.Contains("Error: EPERM: operation not permitted, lstat '",StringComparison.Ordinal)||
            !output.Contains("at Object.realpathSync (node:fs:",StringComparison.Ordinal)||
            !output.Contains("at toRealPath (node:internal/modules/helpers:",StringComparison.Ordinal)||
            !Regex.IsMatch(output,@"lstat '[A-Za-z]:\\'",RegexOptions.CultureInvariant))return null;
        return new("sandbox_node_path_resolution",
            "Node could not load the JavaScript file because its loader tried to inspect the drive root. No JavaScript syntax result is available.",
            "Validation is blocked by runtime path resolution. Preserve completed files and report this blocker. Do not diagnose TypeScript, rewrite source, move a checker to /tmp, repeat the command, or relax sandbox permissions to work around it.");
    }
}
