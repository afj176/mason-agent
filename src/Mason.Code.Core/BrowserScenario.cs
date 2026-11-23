using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Mason.Code;

public sealed record BrowserStep(string Op, string Target = "", string Value = "");
public sealed record BrowserScenario(string Id, string Requirement, string Page, BrowserStep[] Steps);
public sealed record BrowserContract(int Version, string? Application, BrowserScenario[] Scenarios)
{
    public const string Filename = "mason.acceptance.json";
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    public static BrowserContract Read(CodeWorkspace scope)
    {
        var contract = JsonSerializer.Deserialize<BrowserContract>(scope.Read(Filename).Content,Json);
        if (contract is not { Version: 1, Scenarios.Length: >0 and <=32 } ||
            contract.Scenarios.Any(s=>s is null) || contract.Scenarios.Select(s=>s.Id).Distinct().Count()!=contract.Scenarios.Length)
            throw new InvalidDataException("Use version 1 with 1–32 uniquely named acceptance scenarios.");
        if (contract.Application != null && !Regex.IsMatch(contract.Application,@"\A[a-zA-Z_]\w*(?:\.[a-zA-Z_]\w*)*:[a-zA-Z_]\w*\z"))
            throw new InvalidDataException("Application must identify a local Python WSGI module:callable.");
        foreach (var scenario in contract.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Id) || scenario.Id.Length>100 || string.IsNullOrWhiteSpace(scenario.Requirement) || scenario.Requirement.Length>500)
                throw new InvalidDataException("Each scenario requires a bounded ID and a requirement.");
            scope.Resolve(scenario.Page);
            Validate(scenario.Steps);
        }
        return contract;
    }
    public static void Validate(BrowserStep[]? steps)
    {
        if (steps is not { Length: >0 and <=24 }) throw new InvalidDataException("Provide 1–24 observable browser steps.");
        bool captured=false;
        foreach (var s in steps)
        {
            if (s is null || s.Target==null || s.Value==null || s.Target.Length>180 || s.Value.Length>300 ||
                s.Op is not ("count_distinct" or "fill" or "click" or "money" or "text" or "empty" or "capture" or "reload" or "same"))
                throw new InvalidDataException("Unknown or oversized browser step.");
            if(s.Op!="reload" && string.IsNullOrWhiteSpace(s.Target)) throw new InvalidDataException("A browser step needs a target.");
            if(s.Op=="count_distinct" && !(int.TryParse(s.Value,out var n) && n is >0 and <=1000)) throw new InvalidDataException("Specify a positive distinct item count.");
            if(s.Op=="money" && !decimal.TryParse(s.Value,NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out _)) throw new InvalidDataException("Specify the expected decimal amount.");
            if(s.Op=="text" && string.IsNullOrWhiteSpace(s.Value)) throw new InvalidDataException("Specify exact visible text.");
            if(s.Op=="same" && !captured) throw new InvalidDataException("Capture a value before comparing it.");
            if(s.Op=="capture") captured=true;
        }
        if(steps[^1].Op is not ("count_distinct" or "money" or "text" or "same" or "empty")) throw new InvalidDataException("End each scenario with an assertion.");
    }
    internal static bool CoversBehavior(string text,BrowserStep[] steps)
    {
        bool Mentions(string pattern)=>Regex.IsMatch(text,pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        bool After(string first,string last){
            int start=Array.FindIndex(steps,s=>s.Op==first);
            return start>=0 && steps.Skip(start+1).Any(s=>s.Op==last);
        }
        if(Mentions(@"\b(?:discount(?:ed)?|calculat\w*|subtotal)\b") && !steps.Any(s=>s.Op=="money"))return false;
        if(Mentions(@"\b(?:quantity|units)\b") && Mentions(@"\b(?:discount(?:ed)?|total|subtotal)\b") && !After("fill","money"))return false;
        if(Mentions(@"\bdistinct\s+products?\b") && !steps.Any(s=>s.Op=="count_distinct"))return false;
        if(Mentions(@"\b(order|confirmation)\b") && Mentions(@"\b(?:persist\w*|reload\w*|refresh\w*|survive\w*)\b")){
            int capture=Array.FindIndex(steps,s=>s.Op=="capture");
            int reload=capture<0?-1:Array.FindIndex(steps,capture+1,s=>s.Op=="reload");
            if(reload<0 || !steps.Skip(reload+1).Any(s=>s.Op=="same"))return false;
        }
        return true;
    }
    internal static async Task<string> Observe(IPage page, BrowserStep[] steps, CancellationToken ct)
    {
        Validate(steps);
        string? captured=null;
        var observations=new List<string>();
        foreach(var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if(step.Op=="reload") { await page.ReloadAsync(new(){WaitUntil=WaitUntilState.Load}); continue; }
            var node=page.Locator("css="+step.Target).Filter(new(){Visible=true});
            if(step.Op=="click") { await node.ClickAsync(); continue; }
            if(step.Op=="fill") { await node.FillAsync(step.Value); await node.PressAsync("Tab"); continue; }
            var until=System.Diagnostics.Stopwatch.StartNew();
            string actual="";
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                var values=(await node.AllInnerTextsAsync()).Select(s=>s.Trim()).ToArray();
                actual=values.Length==1 ? values[0] : string.Join(" | ",values.Take(8));
                bool pass=step.Op switch
                {
                    "count_distinct"=>values.Length==int.Parse(step.Value,CultureInfo.InvariantCulture) && values.All(v=>v.Length>0) && values.Distinct(StringComparer.OrdinalIgnoreCase).Count()==values.Length,
                    "empty"=>values.Length==0 || values.Length==1 && actual.Length==0,
                    "money"=>values.Length==1 && Amount(actual,out var amount) && amount==decimal.Parse(step.Value,CultureInfo.InvariantCulture),
                    "text"=>values.Length==1 && actual==step.Value,
                    "capture"=>values.Length==1 && actual.Length is >0 and <=200 && actual.StartsWith(step.Value,StringComparison.Ordinal) && actual.Length>step.Value.Length,
                    "same"=>values.Length==1 && actual==captured,
                    _=>false
                };
                if(pass) break;
                if(until.ElapsedMilliseconds>=2200) throw new InvalidDataException($"{step.Op} {step.Target}: expected '{(step.Op=="same"?captured:step.Value)}', observed '{Clip(actual)}' ({values.Length} visible).");
                await Task.Delay(75,ct);
            }
            if(step.Op=="capture") captured=actual;
            observations.Add($"{step.Op} {step.Target} = {Clip(actual)}");
        }
        return string.Join("; ",observations);
    }
    static string Clip(string s)=>s.Length>160?s[..160]+"…":s;
    static bool Amount(string value,out decimal result) =>
        decimal.TryParse(value,NumberStyles.Currency,CultureInfo.GetCultureInfo("en-US"),out result);
}
