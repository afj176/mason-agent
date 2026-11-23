using Mason.Code;
using System.Text.Json;

internal static class CheckoutEvaluation
{
    public const string Request="Verify the features in mason.acceptance.json";
    public static void Seed(string workspace,string variant)
    {
        Directory.CreateDirectory(workspace);
        foreach(var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"fixtures","checkout"))) File.Copy(path,Path.Combine(workspace,Path.GetFileName(path)));
        var app=Path.Combine(workspace,"store.py");
        string source=File.ReadAllText(app);
        source=variant switch { "working"=>source,"products"=>source.Replace("\"Salmon\"","\"Honey\""),
            "discount"=>source.Replace("THRESHOLD = 3","THRESHOLD = 4"),"order"=>source.Replace("SAVE_ORDER = True","SAVE_ORDER = False"),
            _=>throw new ArgumentException("Unknown checkout variant.") };
        File.WriteAllText(app,source);
    }
    public static async Task Run(string root,ICodeModel model)
    {
        Directory.CreateDirectory(root);
        foreach(var variant in new[]{"working","products","discount","order"})
        {
            string dir=Path.Combine(root,variant);
            Seed(dir,variant);
            var scope=new CodeWorkspace(dir);
            var plan=await CodeAcceptance.Prepare(Request,model,scope,scope.Files(),default);
            await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,default);
            Directory.CreateDirectory(Path.Combine(root,"evidence"));
            File.WriteAllText(Path.Combine(root,"evidence",variant+".json"),JsonSerializer.Serialize(plan,new JsonSerializerOptions{WriteIndented=true}));
            var failed=plan.Results.Where(r=>!r.Passed).Select(r=>r.Check.Target).ToArray();
            Console.WriteLine(variant+" "+CodeAcceptance.Receipt(plan));
            if(plan.Results.Count!=3 || !(variant=="working"?failed.Length==0:failed.SequenceEqual([variant])) || File.Exists(Path.Combine(dir,"orders.sqlite")))
                throw new InvalidDataException("Unexpected checkout outcome for "+variant);
        }
    }
}
