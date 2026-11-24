using Microsoft.Playwright;
using System.Text.Json;

namespace Mason.Code;

public sealed record ComputerAction(string Op, int Control, string Value);
public sealed record ComputerObservation(string Text,string Accessibility,string[] Selectors)
{
    public string[] ProductSections {get;init;}=[];
    public string[] ElementIds {get;init;}=[];
}
public interface ICodeBrowserOperator
{
    Task<ComputerAction> ChooseAction(string task,ComputerObservation observation,IReadOnlyList<string> history,CancellationToken ct);
    Task<BrowserScenario[]> DesignReview(string request,string path,ComputerObservation observation,CancellationToken ct);
}

public static class CodeComputerUse
{
    // The DOM is observed inside the same disposable Edge context used for acceptance.
    // Element handles bind actions to the observed node, even when page scripts reorder controls.
    const string Controls="button:visible, a[href]:visible, input:not([type=password]):not([type=hidden]):not([type=file]):visible, select:visible, textarea:visible, [role=button]:visible";
    static async Task<(ComputerObservation Observation,IElementHandle[] Handles)> Observe(IPage page)
    {
        var handles=(await page.Locator(Controls).ElementHandlesAsync()).Take(64).ToArray();
        var selectors=new List<string>();
        var sections=new List<string>();
        foreach(var element in handles){
            sections.Add(await element.EvaluateAsync<string>("""
                el => {for(let n=el.parentElement;n && n!==document.body;n=n.parentElement){
                    const title=n.querySelector('h2,h3,[role=heading]');
                    if(title && n.querySelectorAll('button,[role=button]').length===1){
                        const container=n.id?'#'+CSS.escape(n.id):':is(section,article,div):has(> :text-is('+JSON.stringify(title.innerText.trim())+'))';
                        return title.innerText.trim()+' | '+container;
                    }
                }return '';}
                """));
            selectors.Add(await element.EvaluateAsync<string>("""
                el => {
                  let path = [], node = el;
                  while (node && node !== document.documentElement) {
                    if (node.id && document.querySelectorAll('#'+CSS.escape(node.id)).length === 1) {
                      path.unshift('#'+CSS.escape(node.id)); break;
                    }
                    let index = 1;
                    for(let prev=node.previousElementSibling;prev;prev=prev.previousElementSibling) if(prev.tagName===node.tagName) index++;
                    path.unshift(node.localName + ':nth-of-type('+index+')'); node=node.parentElement;
                  }
                  return path.join(' > ') + ' | ' + (el.getAttribute('aria-label') || el.innerText || el.name || el.type || '').slice(0,120);
                }
                """));
        }
        var body=page.Locator("body");
        static string Clip(string s,int n)=>s.Length<=n?s:s[..n];
        // Include rendered IDs for noninteractive outputs; a reviewer needs a stable assertion target.
        var outputs=await page.Locator("[id]:visible").EvaluateAllAsync<string>("els=>els.slice(0,100).map(e=>'#'+CSS.escape(e.id)+' | '+e.tagName.toLowerCase()).join('\\n')");
        var ids=await page.Locator("[id]").EvaluateAllAsync<string[]>("els=>els.slice(0,150).map(e=>'#'+CSS.escape(e.id))");
        return(new ComputerObservation(Clip(await body.InnerTextAsync(),4500),Clip(await body.AriaSnapshotAsync(),5000)+"\nVisible elements\n"+Clip(outputs,2500),[..selectors]){ProductSections=[..sections],ElementIds=ids},handles);
    }

    public static Task<string> Run(CodeWorkspace workspace,string path,string task,ICodeBrowserOperator model,string artifacts,CancellationToken ct,CodePreviewServer? live=null)
    {
        if(string.IsNullOrWhiteSpace(task)||task.Length>2400)throw new ArgumentException("Provide a bounded browser task.");
        var trace=new List<string>();
        return CodeBrowserVerification.Interact(workspace,path,async(page,token)=>{
            Directory.CreateDirectory(artifacts);
            var explicitOutcome=System.Text.RegularExpressions.Regex.Match(task,@"(?i)\b(?:after observing|until you see|after you see)\s+(.+?)[.!]?\s*$");
            string? previousState=null;ComputerAction? previousAction=null;
            for(int i=0;i<16;i++){
                token.ThrowIfCancellationRequested();
                var (observation,handles)=await Observe(page);
                try{
                    string currentState=JsonSerializer.Serialize(observation);
                    string target=explicitOutcome.Success?explicitOutcome.Groups[1].Value.Trim().Trim('"','\''):"";
                    bool reached=i>0 && target.Length>0 && await page.GetByText(target,new(){Exact=true}).Filter(new(){Visible=true}).CountAsync()>0;
                    var next=reached?new ComputerAction("finish",0,"Observed "+target):await model.ChooseAction(task,observation,trace,token);
                    await File.AppendAllTextAsync(Path.Combine(artifacts,"actions.jsonl"),JsonSerializer.Serialize(new{step=i+1,observation,action=next})+"\n",token);
                    if(next==previousAction && previousState==currentState)throw new InvalidDataException("Repeated browser action produced no visible change. See the action trace.");
                    previousState=currentState;previousAction=next;
                    if(next.Op=="finish"){
                        if(target.Length>0&&!reached)throw new InvalidDataException("Requested browser outcome was not observed: "+target);
                        var screenshot=Path.Combine(artifacts,"final.png");
                        await page.ScreenshotAsync(new(){Path=screenshot,Timeout=5000});
                        return JsonSerializer.Serialize(new{trace,observed=observation,summary=next.Value,screenshot});
                    }
                    if(next.Op=="reload")await page.ReloadAsync();
                    else{
                        if(next.Control<1||next.Control>handles.Length)throw new InvalidDataException("Select a control from the latest observation.");
                        var element=handles[next.Control-1];
                        if(next.Op=="click")await element.ClickAsync();
                        else if(next.Op=="fill" && next.Value.Length<=500)await element.FillAsync(next.Value);
                        else throw new InvalidDataException("Unknown computer action.");
                    }
                    trace.Add($"{next.Op} {next.Control} {next.Value}");
                }finally{foreach(var handle in handles)await handle.DisposeAsync();}
            }
            throw new InvalidDataException("Computer-use action budget reached before completion.");
        },ct,live);
    }

    public static async Task<BrowserScenario[]> Review(CodeWorkspace scope,string path,string request,ICodeBrowserOperator model,CancellationToken ct,string? application=null)
    {
        BrowserScenario[]? scenarios=null;
        await CodeBrowserVerification.Interact(scope,path,async(page,token)=>{
            var (observation,handles)=await Observe(page);
            try{scenarios=await model.DesignReview(request,path,observation,token);}
            finally{foreach(var h in handles)await h.DisposeAsync();}
            return "Observed";
        },ct,application:application);
        if(scenarios is not {Length:>0 and <=4} || scenarios.Any(s=>s.Page!=path))throw new InvalidDataException("Independent reviewer did not supply an executable check for the observed page.");
        return BrowserContract.Parse(scope,JsonSerializer.Serialize(new BrowserContract(1,application,scenarios))).Scenarios;
    }
}
