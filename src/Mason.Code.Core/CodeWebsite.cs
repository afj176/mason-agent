using System.Text;

namespace Mason.Code;

/// <summary>Read-only project resources for the desktop Website tab.</summary>
public sealed class CodeWebsite(CodeWorkspace workspace)
{
    public Uri Origin { get; }=new("https://p-"+CodeProtocol.Hash(Encoding.UTF8.GetBytes(workspace.Root.ToUpperInvariant()))[..32]+".mason.invalid/");
    static readonly Dictionary<string,string> Types=new(StringComparer.OrdinalIgnoreCase){
        [".html"]="text/html",[".htm"]="text/html",[".css"]="text/css",[".js"]="text/javascript",[".mjs"]="text/javascript",
        [".json"]="application/json",[".webmanifest"]="application/manifest+json",[".wasm"]="application/wasm",
        [".png"]="image/png",[".jpg"]="image/jpeg",[".jpeg"]="image/jpeg",[".webp"]="image/webp",[".gif"]="image/gif",
        [".svg"]="image/svg+xml",[".ico"]="image/x-icon",[".avif"]="image/avif",[".woff"]="font/woff",[".woff2"]="font/woff2",
        [".ttf"]="font/ttf",[".otf"]="font/otf"
    };
    public static bool IsPage(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";
    public string? StartPage(string? selected=null)
    {
        foreach(var name in new[]{selected,"index.html","index.htm"}){
            if(name!=null && IsPage(name) && File.Exists(workspace.Resolve(name)))return name;
        }
        return null;
    }
    public Uri Page(string relative)
    {
        string path=workspace.Relative(workspace.Resolve(relative));
        if(!IsPage(path))throw new ArgumentException("Choose an HTML page in this project.");
        if(!File.Exists(workspace.Resolve(path)))throw new FileNotFoundException("Page missing from this project.",path);
        return new Uri(Origin,string.Join("/",path.Split('/').Select(Uri.EscapeDataString)));
    }
    public static bool SameOrigin(Uri a,Uri b)=>a.IsAbsoluteUri && b.IsAbsoluteUri && a.Scheme==b.Scheme &&
        a.IdnHost.Equals(b.IdnHost,StringComparison.OrdinalIgnoreCase) && a.Port==b.Port && b.UserInfo.Length==0;
    public (byte[] Bytes,string Type) Read(Uri uri)
    {
        if(!SameOrigin(Origin,uri))throw new UnauthorizedAccessException("Resource outside the selected website.");
        string path=Uri.UnescapeDataString(uri.AbsolutePath[1..]);
        if(path.Length==0 || path.EndsWith('/'))path+="index.html";
        var full=workspace.Resolve(path);
        if(!Types.TryGetValue(Path.GetExtension(full),out var type))throw new UnauthorizedAccessException("Unsupported website resource.");
        using var file=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length>16*1024*1024)throw new InvalidDataException("Website resource exceeds 16 MiB.");
        var bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);
        return(bytes,type);
    }
}
