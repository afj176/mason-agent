using System.Net;
using System.Text;
using System.Text.Json;
using Mason;
using Mason.Code;

static class CodeNativeChecks
{
    public static async Task Replay(string original,string destination)
    {
        var old=Path.GetFullPath(original);var area=Path.GetFullPath(destination);if(Directory.Exists(area))throw new IOException("Use a fresh replay area.");
        var source=JsonSerializer.Deserialize<CodeSession>(File.ReadAllText(Directory.GetFiles(Path.Combine(old,"task/profile/code-sessions"),"*.json").Single()))!;
        var workspace=Path.Combine(area,"workspace");Directory.CreateDirectory(workspace);
        foreach(var name in new[]{"AGENTS.md","README.md","version.cmd","unrelated.txt"})File.Copy(Path.Combine(source.Workspace,name),Path.Combine(workspace,name));
        var actions=source.Messages.Where(m=>m.Role=="assistant").Select(m=>CodeProtocol.Parse(m.Content) with { ReasoningContent=m.ReasoningContent }).Concat(new[]{new CodeAction("Replay complete.",null,default)}).ToArray();
        using var store=new CodeStore(Path.Combine(area,"profile"));var session=store.New(workspace);session.Permission=CodePermission.Auto;session.Shell=CodeShellKind.CommandPrompt;session.ShellExecutable=CodeShells.Find(session.Shell);
        await new CodeEngine(store,new ScriptedModel(actions),(_,_)=>throw new Exception("Unexpected review"),()=>{},error=>File.WriteAllText(Path.Combine(area,"error.txt"),error.ToString())).Run(session,"Replay the recorded file and command operations for diagnosis.",default);
        Console.WriteLine(session.State+": "+session.Detail);if(File.Exists(Path.Combine(area,"error.txt")))Console.WriteLine(File.ReadAllText(Path.Combine(area,"error.txt")));if(session.State!="Completed")Environment.ExitCode=1;
    }
    public static async Task Run(string root,Action<bool,string> check)
    {
        var definitions=CodeSchema.NativeTools();check(definitions.GetArrayLength()==13&&definitions.EnumerateArray().All(t=>!string.IsNullOrWhiteSpace(t.GetProperty("function").GetProperty("description").GetString())),"every registered native tool has a complete description and executable argument schema");
        using(var unwrapped=JsonDocument.Parse(CodeNativeProtocol.Receipt(JsonSerializer.Serialize(new{tool_result="read_file",state="Completed",result=JsonSerializer.Serialize(new{Content="\"quoted\"\nλ",Sha256="abc"})}))))
            check(unwrapped.RootElement.GetProperty("result").GetProperty("Content").GetString()=="\"quoted\"\nλ"&&unwrapped.RootElement.GetProperty("state").GetString()=="Completed","native receipts remove double-encoded JSON while preserving exact source text and outcome");
        var profile=new AiEndpointProfile { Model="native-test",Endpoint="http://127.0.0.1:18139" };
        var source="const quote = \"λ\\path\";\nconsole.log(quote);\n";
        var arguments=JsonSerializer.Serialize(new {path="example.js",content=source});
        string Event(object delta,string? finish=null)=>"data: "+JsonSerializer.Serialize(new { model=profile.Model,choices=new[]{new {index=0,delta,finish_reason=finish} } })+"\n\n";
        string Call(string id,string name,string args)=>Event(new { tool_calls=new[]{new {index=0,id,type="function",function=new{name,arguments=args}}} });
        string End(string finish="tool_calls")=>Event(new { },finish)+"data: [DONE]\n\n";
        var chunks=Event(new {reasoning_content="Inspecting the real receipt."})+
            Call("call_a","create_file",arguments[..19])+
            Event(new {tool_calls=new[]{new{index=0,function=new{arguments=arguments[19..]}}}})+End();
        var handler=new Handler(chunks);
        var progress=new List<string>();var adapter=new CodeLocalModel(profile,"","default",_=>{},()=>new LocalAiClient(handler),progress.Add);
        var messages=new CodeMessage[]{new("system",CodeProtocol.Instructions+"\nAgent mode: build"),new("user","Build the requested change.")};
        check(CodeLocalModel.RequiresCompletionRecoveryAction(messages.Concat([new CodeMessage("user","Build completion rejected: implement the requested change.")]).ToArray()) && !CodeLocalModel.RequiresCompletionRecoveryAction(messages), "completion recovery requires a real tool or question only immediately after a rejected completion");
        var action=await adapter.Next(messages,_=>{},default);
        check(action.Tool=="create_file"&&action.Arguments.GetProperty("content").GetString()==source,"native streamed arguments preserve quotes, backslashes, Unicode and source newlines exactly");
        check(action.ReasoningContent=="Inspecting the real receipt.","Flint reasoning is retained separately from public text");
        check(handler.MeasuredWithTools&&handler.GenerationWithTools,"native token measurement and generation share tools, messages and publisher sampling settings without the JSON action grammar");
        var argumentProgress=new List<int>();using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(chunks)))await LocalAiClient.ReadChatStream(stream,_=>{},default,allowToolCalls:true,onToolArguments:argumentProgress.Add);
        check(argumentProgress.Count==2&&argumentProgress[^1]==arguments.Length,"native argument streaming reports received characters while the uncommitted action remains unexecutable");
        var intent=new CodeMessage("assistant",JsonSerializer.Serialize(new {version=1,message="Creating the file.",tool=new{name="create_file",arguments=new{path="example.js",content=source}}})) {ReasoningContent=action.ReasoningContent};
        var receipt=new CodeMessage("user","{\"tool_result\":\"create_file\",\"state\":\"Completed\",\"result\":\"written\"}");
        var projected=CodeNativeProtocol.Project(messages.Concat(new[]{intent,receipt}).ToArray());
        bool textHistoryRejected=false;try{new ChatGenerationOptions().Body(profile,projected,100,true);}catch(ArgumentException){textHistoryRejected=true;}
        check(textHistoryRejected,"ordinary Chat cannot enable tool calling by supplying native tool history");
        check(projected[^2].ToolCalls?.Single().Id==projected[^1].ToolCallId&&projected[^1].Role=="tool"&&projected[^2].ReasoningContent==action.ReasoningContent,"canonical journal projects to paired native tool calls, exact execution receipts and preserved reasoning");
        check(!projected[0].Content.Contains("Return one JSON object")&&!projected[0].Content.Contains("Use null tool only")&&projected[0].Content.Contains("only the workspace is writable"),"native instructions replace the JSON envelope while retaining the security policy");
        var orphan=CodeNativeProtocol.Project(messages.Concat(new[]{intent}).ToArray());
        check(orphan[^1].ToolCalls==null,"unconfirmed intent does not acquire an invented native execution receipt");
        check(JsonSerializer.Serialize(new CodeMessage("user","legacy"))=="{\"Role\":\"user\",\"Content\":\"legacy\"}","legacy journal serialization and checkpoint hashes are unchanged when no reasoning exists");
        using(var store=new CodeStore(Path.Combine(root,"native-history")))
        {
            var session=store.New(Path.Combine(root,"workspace"));session.Messages.Add(intent);session.Messages.Add(receipt);store.Save(session);
        }
        using(var restored=new CodeStore(Path.Combine(root,"native-history")))check(restored.Sessions.Single().Messages[0].ReasoningContent==action.ReasoningContent,"provider reasoning survives save and restart without changing speakers or receipt order");
        using(var store=new CodeStore(Path.Combine(root,"bounded-native-run")))
        {
            var workspace=Path.Combine(root,"workspace");var session=store.New(workspace);session.ActionLimit=2;session.Permission=CodePermission.WorkspaceEdits;
            var read=new CodeAction("", "list_files",JsonSerializer.SerializeToElement(new{path=".",limit=100}));
            await new CodeEngine(store,new ScriptedModel(read,read,new CodeAction("", "create_file",JsonSerializer.SerializeToElement(new{path="past-budget.txt",content="bad"}))),(_,_)=>throw new Exception("Unexpected review"),()=>{}).Run(session,"Use the bounded run.",default);
            check(session.State=="Failed"&&session.Activities.Count==2&&!File.Exists(Path.Combine(workspace,"past-budget.txt")),"configurable action budget stops before a later mutation and retains receipts");
        }
        using(var store=new CodeStore(Path.Combine(root,"reader-race")))
        {
            var session=store.New(Path.Combine(root,"workspace"));var path=Path.Combine(store.Root,"code-sessions",session.Id.ToString("N")+".json");
            var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            var release=Task.Run(()=>{Thread.Sleep(55);held.Dispose();});
            session.Title="Saved after a transient Windows reader";store.Save(session);await release;
            check(JsonSerializer.Deserialize<CodeSession>(File.ReadAllText(path))!.Title==session.Title,"atomic journal save tolerates a transient Windows reader without truncating the previous snapshot");
            var saved=File.ReadAllBytes(path);using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                session.Title="Not committed while locked";bool refused=false;try{store.Save(session);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){refused=true;}
                check(refused&&File.ReadAllBytes(path).SequenceEqual(saved),"a persistent Windows reader lock fails boundedly and preserves the last complete journal");
            }
            store.Save(session);
        }
        async Task Reject(string sse,bool allow,string name)
        {
            try{using var stream=new MemoryStream(Encoding.UTF8.GetBytes(sse));await LocalAiClient.ReadChatStream(stream,_=>{},default,allowToolCalls:allow);}
            catch(Exception error)when(error is InvalidDataException or JsonException or IOException){check(true,name);return;}
            throw new Exception("FAIL native stream accepted "+name);
        }
        await Reject(chunks,false,"ordinary Chat still rejects model tool calls");
        await Reject(Call("call_a","create_file",arguments),true,"disconnect before completion cannot produce an executable action");
        await Reject(Call("call_a","create_file",arguments)+Event(new{tool_calls=new[]{new{index=1,function=new{name="run_command",arguments="{}"}}}})+End(),true,"parallel native actions are rejected before execution");
        await Reject(Call("call_a","create_file",arguments)+Call("different","create_file","{}")+End(),true,"changing native call identity is rejected");
        await Reject(Call("call_a","create_file",arguments)+End()+"data: ",false,"native calls never enable themselves in an ordinary text stream");
        var incomplete=new CodeLocalModel(profile,"","default",_=>{},()=>new LocalAiClient(new Handler(Call("call_a","create_file",arguments)+End("length"))));
        bool rejected=false;try{await incomplete.Next(messages,_=>{},default);}catch(CodeResponseException){rejected=true;}
        check(rejected,"output-limit tool response remains unexecutable even with syntactically complete arguments");
        var malformed=new CodeLocalModel(profile,"","default",_=>{},()=>new LocalAiClient(new Handler(Call("call_a","create_file","{broken")+End())));
        rejected=false;try{await malformed.Next(messages,_=>{},default);}catch(CodeResponseException){rejected=true;}
        check(rejected,"malformed native arguments are rejected without heuristic code repair");
    }
    sealed class Handler(string response):HttpMessageHandler
    {
        public bool MeasuredWithTools,GenerationWithTools;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string path=request.RequestUri!.AbsolutePath,body="";string media="application/json";
            if(request.Content!=null)
            {
                using var json=JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));var value=json.RootElement;
                if(path is "/apply-template" or "/v1/chat/completions")
                {
                    bool valid=value.GetProperty("tools").GetArrayLength()==13&&!value.TryGetProperty("response_format",out _)&&value.GetProperty("temperature").GetDouble()==1&&value.GetProperty("top_p").GetDouble()==.95&&value.GetProperty("top_k").GetInt32()==20&&!value.GetProperty("parallel_tool_calls").GetBoolean();
                    if(path=="/apply-template")MeasuredWithTools=valid;else GenerationWithTools=valid;
                }
            }
            switch(path)
            {
                case "/v1/models":body="{\"data\":[{\"id\":\"native-test\"}]}";break;
                case "/props":body=JsonSerializer.Serialize(new{build_info="test",chat_template="macro flint_text <arg_key> tool_calls <think></think>",default_generation_settings=new{n_ctx=32768}});break;
                case "/apply-template":body="{\"prompt\":\"Measured native history\"}";break;
                case "/tokenize":body="{\"tokens\":[1,2,3,4]}";break;
                case "/v1/chat/completions":body=response;media="text/event-stream";break;
                default:throw new Exception(path);
            }
            return new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,media)};
        }
    }
}
