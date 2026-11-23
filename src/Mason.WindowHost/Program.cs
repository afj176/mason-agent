using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace Mason.WindowHost;

public sealed class Target { public int ProcessId {get;set;} public long Handle {get;set;} public long Started {get;set;} }
public sealed class Control {
    public string Id {get;set;}=""; public string Name {get;set;}=""; public string Type {get;set;}="";
    public bool Invoke {get;set;} public bool SetValue {get;set;}
}
public sealed class Request { public Target target {get;set;}=new();public Control? control {get;set;}public string operation {get;set;}="";public string value {get;set;}="";}
internal static class Program
{
    static IEnumerable<KeyValuePair<Control,AutomationElement>> Observe(AutomationElement root,int pid)
    {
        var queue=new Queue<AutomationElement>();queue.Enqueue(root);
        var walk=TreeWalker.ControlViewWalker;
        for(int seen=0;queue.Count>0&&seen<220;seen++){
            var el=queue.Dequeue();Control? node=null;
            try{
                var info=el.Current;if(info.ProcessId!=pid||info.IsPassword)continue;
                if(!info.IsOffscreen){
                    string name=info.Name??"";
                    node=new Control{Id=string.Join("-",el.GetRuntimeId()),Name=name.Substring(0,Math.Min(180,name.Length)),Type=info.ControlType.ProgrammaticName,
                        Invoke=info.IsEnabled && el.TryGetCurrentPattern(InvokePattern.Pattern,out _),
                        SetValue=info.IsEnabled && el.TryGetCurrentPattern(ValuePattern.Pattern,out var value)&&!((ValuePattern)value).Current.IsReadOnly};
                }
                for(var child=walk.GetFirstChild(el);child!=null&&queue.Count+seen<220;child=walk.GetNextSibling(child))queue.Enqueue(child);
            }catch(ElementNotAvailableException){continue;}
            if(node!=null)yield return new KeyValuePair<Control,AutomationElement>(node,el);
        }
    }
    [MTAThread]static int Main()
    {
        try{
            Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
            var json=new JavaScriptSerializer{MaxJsonLength=200000};
            var req=json.Deserialize<Request>(Console.In.ReadToEnd());
            using var process=Process.GetProcessById(req.target.ProcessId);
            if(process.StartTime.ToUniversalTime().Ticks!=req.target.Started||process.MainWindowHandle.ToInt64()!=req.target.Handle)throw new InvalidOperationException("Window identity changed.");
            var root=AutomationElement.FromHandle(process.MainWindowHandle);
            var entries=Observe(root,process.Id).ToArray();
            if(req.operation!="observe"){
                var prior=req.control??throw new InvalidOperationException("No observed control.");
                var entry=entries.Single(e=>e.Key.Id==prior.Id && e.Key.Name==prior.Name && e.Key.Type==prior.Type);
                if(req.operation=="invoke"&&entry.Key.Invoke)((InvokePattern)entry.Value.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                else if(req.operation=="set"&&entry.Key.SetValue)((ValuePattern)entry.Value.GetCurrentPattern(ValuePattern.Pattern)).SetValue(req.value);
                else throw new InvalidOperationException("Unsupported operation for the observed control.");
                entries=Observe(root,process.Id).ToArray();
            }
            Console.WriteLine(json.Serialize(entries.Select(x=>x.Key).ToArray()));return 0;
        }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
    }
}
