using System.Text.Json;

namespace Mason.Code;

public static class CodeSemantic
{
    public static async Task<string> Query(CodeWorkspace scope,string path,int line,int column,string mode,CancellationToken ct)
    {
        if(mode is not ("references" or "diagnostics" or "definitions")||line<=0||column<=0)throw new ArgumentException("Use definitions, references or diagnostics and a 1-based position.");
        var main=scope.Read(path);
        var ext=Path.GetExtension(main.Path).ToLowerInvariant();
        bool python=ext==".py";
        string[] extensions=python?[".py",".pyi"]:[".ts",".tsx",".js",".jsx",".mts",".cts"];
        if(!extensions.Contains(ext))throw new ArgumentException("Select a Python, JavaScript or TypeScript source file.");
        var inventory=scope.Files(".",1000);var files=new Dictionary<string,string>();int size=0;bool partial=inventory.Truncated;
        void Add(CodeFile f){files.TryAdd(f.Path,f.Content);size+=f.Content.Length;}
        Add(main);
        foreach(var p in inventory.Files.Where(p=>p!=main.Path&&extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))){
            ct.ThrowIfCancellationRequested();if(size>4000000||files.Count>=400){partial=true;break;}
            try{Add(scope.Read(p));}catch(IOException){partial=true;}catch(InvalidDataException){partial=true;}
        }
        var host=Path.Combine(AppContext.BaseDirectory,"language-tools");
        var input=JsonSerializer.Serialize(new{path=main.Path,line,column,mode,files,partial});
        if(python)return await CodeOwnedHelper.Run(CodeCommand.ResolveExecutable("python",scope.Root),["-I","-B",Path.Combine(host,"python-symbols.py")],input,host,ct);
        var node=Path.Combine(AppContext.BaseDirectory,".playwright","node","win32_x64","node.exe");
        return await CodeOwnedHelper.Run(node,[Path.Combine(host,"typescript-symbols.cjs")],input,host,ct);
    }
}
