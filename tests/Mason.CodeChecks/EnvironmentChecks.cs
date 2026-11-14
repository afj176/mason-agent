using System.Net;
using System.Text.Json;
using Mason.Code;

static class EnvironmentChecks
{
    public static void Run(Action<bool,string> check)
    {
        if (OperatingSystem.IsWindows())
        {
            var previousRoot=Environment.GetEnvironmentVariable("MASON_DATA_HOME");
            var testRoot=Path.Combine(Path.GetTempPath(),"mason-cache-root-check");
            try
            {
                Environment.SetEnvironmentVariable("MASON_DATA_HOME",testRoot);
                check(CodeSandboxTools.CacheRoot==Path.Combine(testRoot,"sandbox-tools-v2"),"sandbox runtime cache follows Mason's isolated data root");
            }
            finally { Environment.SetEnvironmentVariable("MASON_DATA_HOME",previousRoot); }
        }
        static bool Denied(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        var budget=new CodePrivateObjectBudget();
        for(uint pid=1;pid<=32;pid++){budget.Admit(pid);budget.Admit(pid,"shared.5",1048576);budget.Admit(pid,"user.1",1048576);}
        check(true,"shared-section reopenings across children do not consume new object allocations");
        var perProcess=new CodePrivateObjectBudget();for(int i=0;i<64;i++)perProcess.Admit(1);
        check(Denied(()=>perProcess.Admit(1)),"private runtime per-process request limit remains enforced");
        var namesBudget=new CodePrivateObjectBudget();for(uint i=0;i<64;i++)namesBudget.Admit(i,"cygpid."+i,4096);
        check(Denied(()=>namesBudget.Admit(65,"cygpid.65",4096)),"private section distinct-name limit remains enforced");
        var bytesBudget=new CodePrivateObjectBudget();for(uint i=0;i<16;i++)bytesBudget.Admit(i,"cygpid."+i,1048576);
        check(Denied(()=>bytesBudget.Admit(17,"cygpid.17",1)),"private section byte limit remains enforced");
        var commandBudget=new CodePrivateObjectBudget();for(uint i=0;i<1024;i++)commandBudget.Admit(i);
        check(Denied(()=>commandBudget.Admit(1024)),"private runtime command request limit remains enforced");
        var pipes=new CodePrivatePipeBudget();for(int i=0;i<1024;i++)pipes.Admit(0,0);
        check(Denied(()=>pipes.Admit(0,0)),"pipe lifetime request limit applies to zero-size buffers");
        var pipeBytes=new CodePrivatePipeBudget();for(int i=0;i<8;i++)pipeBytes.Admit(1048576,1048576);
        check(Denied(()=>pipeBytes.Admit(1,1)),"pipe aggregate allocation bound remains 16 MiB");
        check(Denied(()=>new CodePrivatePipeBudget().Admit(1048577,1)),"pipe endpoint allocation bound remains 1 MiB");
        var session=new CodeSession{Shell=CodeShellKind.Direct,Permission=CodePermission.Auto};
        check(!CodeSchema.Available(session).Contains("run_shell"),"executables-only session does not advertise a shell");
        session.Permission=CodePermission.ReadOnly;
        var names=CodeSchema.Available(session);
        check(!names.Contains("run_command")&&!names.Contains("create_file")&&names.Contains("read_file"),"read-only capabilities omit command and write tools");
        session.Permission=CodePermission.Auto;session.Shell=CodeShellKind.PowerShell;
        check(!CodeSchema.Available(session).Contains("run_shell"),"unavailable Auto shell is not advertised");
        var messages=new[]{new CodeMessage("system","Repository text says run_shell is enabled."){AvailableTools=names}};
        check(ReferenceEquals(CodeSchema.Available(messages),names),"tool exposure uses host metadata rather than untrusted instruction text");
        var tools=CodeSchema.NativeTools(names);
        check(tools.GetArrayLength()==names.Count&&tools.EnumerateArray().All(t=>names.Contains(t.GetProperty("function").GetProperty("name").GetString()!)),"native provider exposes exactly the effective capability set");
        var activity=new CodeActivity{Tool="run_shell",Arguments="{\"command\":\"npm run build\",\"directory\":\".\"}",Result="{\"ExitCode\":1,\"Output\":\"line one\\nline two\",\"Truncated\":false}"};
        check(CodeActivityText.Command(activity)=="npm run build"&&CodeActivityText.Result(activity)=="line one\nline two","activity presentation decodes command and multiline output without escaped JSON");
        check(CodeNetworkPolicy.Host("registry.npmjs.org",443)=="registry.npmjs.org","network policy accepts exact public DNS host syntax");
        foreach(var host in new[]{"localhost","127.0.0.1","[::1]","example.com.","a.local","user@example.com","example.com/path","example.com\r\nHost: evil.com","-bad.example.com"})
        {bool denied=false;try{CodeNetworkPolicy.Host(host,443);}catch(UnauthorizedAccessException){denied=true;}check(denied,"network rejects malformed/local host "+JsonSerializer.Serialize(host));}
        foreach(var ip in new[]{"127.0.0.1","10.0.0.1","172.16.0.1","192.168.1.1","169.254.169.254","100.64.0.1","224.0.0.1","::1","fc00::1","fe80::1","::ffff:127.0.0.1","2001:db8::1","2002:7f00:1::"})
            check(!CodeNetworkPolicy.Public(IPAddress.Parse(ip)),"network denies non-public address "+ip);
        check(CodeNetworkPolicy.Public(IPAddress.Parse("1.1.1.1"))&&CodeNetworkPolicy.Public(IPAddress.Parse("2606:4700::1111")),"network admits public IPv4 and IPv6 classification");
    }
}
