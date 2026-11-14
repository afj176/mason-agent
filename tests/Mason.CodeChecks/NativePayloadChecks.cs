using System.Reflection;
using System.Text.Json.Nodes;
using Mason.Code;

static class NativePayloadChecks
{
    public static void Run(string destination)
    {
        string root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Choose a fresh payload fixture.");Directory.CreateDirectory(root);
        var method=typeof(CodeWorkspace).Assembly.GetType("Mason.Code.CodeHandoffAvailability",true)!.GetMethod("VerifyPayload",BindingFlags.NonPublic|BindingFlags.Static)!;
        string original=Path.Combine(AppContext.BaseDirectory,"native-handoff");int count=0;
        string Copy(string name)
        {
            string folder=Path.Combine(root,name);Directory.CreateDirectory(folder);
            foreach(string file in new[]{"dbcw.dll","dbck.dll","Mason.BashBootstrap.exe","handoff-manifest.json"})File.Copy(Path.Combine(original,file),Path.Combine(folder,file));
            return folder;
        }
        void Manifest(string folder,Action<JsonObject> edit)
        {
            string path=Path.Combine(folder,"handoff-manifest.json");var json=JsonNode.Parse(File.ReadAllText(path))!.AsObject();edit(json);File.WriteAllText(path,json.ToJsonString());
        }
        void Reject(string folder,string name)
        {
            bool rejected=false;try{method.Invoke(null,[folder]);}catch(TargetInvocationException error)when(error.InnerException is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException){rejected=true;}
            if(!rejected)throw new Exception("FAIL "+name);count++;Console.WriteLine("PASS "+name);
        }
        method.Invoke(null,[Copy("valid")]);count++;Console.WriteLine("PASS exact native ABI and all three payload hashes validate");
        var abi=Copy("abi");Manifest(abi,j=>j["Abi"]=CodeHandoffRequest.AbiVersion-1);Reject(abi,"old native ABI cannot be advertised as available");
        var missing=Copy("missing-field");Manifest(missing,j=>j.Remove("FrameBytes"));Reject(missing,"incomplete native manifest rejected");
        var countMismatch=Copy("missing-entry");Manifest(countMismatch,j=>j["Files"]!.AsArray().RemoveAt(0));Reject(countMismatch,"missing payload entry rejected");
        var duplicate=Copy("duplicate");Manifest(duplicate,j=>j["Files"]!.AsArray()[1]=j["Files"]!.AsArray()[0]!.DeepClone());Reject(duplicate,"duplicate payload identity rejected");
        var traversal=Copy("traversal");Manifest(traversal,j=>j["Files"]!.AsArray()[0]!["Name"]="../outside.dll");Reject(traversal,"manifest cannot nominate a path outside the fixed payload");
        var tampered=Copy("tampered");var path=Path.Combine(tampered,"dbck.dll");var bytes=File.ReadAllBytes(path);bytes[128]^=1;File.WriteAllBytes(path,bytes);Reject(tampered,"same-length native payload corruption rejected");
        var invalid=Copy("invalid-json");File.WriteAllText(Path.Combine(invalid,"handoff-manifest.json"),"{");Reject(invalid,"malformed manifest rejected");
        var oversized=Copy("oversized");File.WriteAllBytes(Path.Combine(oversized,"handoff-manifest.json"),new byte[256*1024+1]);Reject(oversized,"manifest size bounded before parsing");
        Console.WriteLine(count+" native payload checks passed.");
    }
}
