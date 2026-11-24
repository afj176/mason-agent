using Mason.Code;

internal static class MilestoneChecks
{
    public static async Task Run(string area)
    {
        var project = Directory.CreateDirectory(Path.Combine(area, "workspace")).FullName;
        var scope = new CodeWorkspace(project);
        scope.CreateDirectory("billing"); scope.CreateDirectory("shipping"); scope.CreateDirectory("tests");
        var file = scope.Create("billing/Service.cs", "namespace Billing; public class Service { public int Total() => 1; }");
        scope.Create("shipping/Service.cs", "namespace Shipping; public class Service { public int Total() => 2; }");
        scope.Create("tests/Api.cs", "namespace Tests; public class Api { public int Run() => new Billing.Service().Total(); }");
        var map = new CodeRepositoryMap(scope);
        var result = map.Search("Billing.Service.Total");
        if (!result.Matches.Any(x => x.Path == "tests/Api.cs" && x.Kind == "test reference" && x.Name == "Total" && x.Detail.Contains("Billing.Service.Total")) ||
            result.Matches[0].Detail.Contains("Shipping.Service.Total") || map.Search("Total").Reindexed != 0)
            throw new Exception("Repository map lost a bound caller or reparsed unchanged files.");
        var profile = Path.Combine(area, "profile");
        using (var store = new CodeStore(profile))
        {
            var session = store.New(project);
            CodeMilestone Create(string title, string path, params Guid[] deps) => new(Guid.NewGuid(), title, "Keep the existing contract.", [path], deps, "");
            var billing = Create("Bill", file.Path);
            var shipping = Create("Ship", "shipping/Service.cs");
            var integration = Create("Integrate", "tests/Api.cs", billing.Id, shipping.Id);
            foreach (var item in new[] { billing, shipping, integration })
            {
                session.Milestones = CodeMilestones.Put(scope, session.Milestones, item);
                var receipt = await CodeMilestones.Check(scope, item, session.Milestones, false, "Checked against the source contract.", default);
                session.Milestones = session.Milestones.Select(x => x.Id == item.Id ? x with { Receipt = receipt } : x).ToArray();
            }
            try { CodeMilestones.Put(scope, session.Milestones, billing with { DependsOn = [integration.Id] }); throw new Exception("Cycle accepted."); }
            catch (ArgumentException) { }
            store.Save(session);
        }
        scope.Edit(file.Path, file.Sha256, "Total()", "Gross()", (_,_)=>{});
        using var restored = new CodeStore(profile);
        var statuses = CodeMilestones.Inspect(scope, restored.Sessions.Single().Milestones);
        if (!statuses.Select(x => x.State).SequenceEqual(["Needs recheck", "Reviewed", "Blocked"]) ||
            !CodeMilestones.Context(scope, restored.Sessions.Single().Milestones).Contains("Next: Bill"))
            throw new Exception("Reopening after an external edit did not isolate affected milestones.");
        var renamed = map.Search("Billing.Service.Gross");
        if (renamed.Reindexed != 1 || !renamed.Matches.Any(x => x.Kind == "definition" && x.Name == "Gross") ||
            map.Search("Billing.Service.Total").Matches.Any(x => x.Detail.Contains("Billing.Service.Total")))
            throw new Exception("Repository map retained an obsolete symbol after rename.");
        scope.Create("currency.ts","export function subtotal(value: number) { return value; }");
        scope.Create("store.ts","import { subtotal } from './currency';\nexport const receipt = subtotal(12);");
        var bound=await map.Query("subtotal");
        if(!bound.Matches.Any(h=>h.Kind=="bound reference" && h.Path=="store.ts" && h.Line==2))
            throw new Exception("TypeScript service did not bind the imported function to the caller.");
        Console.WriteLine("PASS cross-file TypeScript reference through the incremental map");
        Console.WriteLine("PASS qualified symbols, incremental rename, persisted decisions, dependency invalidation and next milestone");
    }
}
