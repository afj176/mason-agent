using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Mason.Code;

static class CodingRuntimeChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        if(!OperatingSystem.IsWindows())return;
        var area=Path.Combine(root,"runtime-isolation");var workspace=Path.Combine(area,"workspace");var outside=Path.Combine(area,"outside");Directory.CreateDirectory(workspace);Directory.CreateDirectory(outside);
        check(CodeSandbox.RuntimeWorkspaceIdentity(workspace)!=CodeSandbox.RuntimeWorkspaceIdentity(outside),"different physical workspaces cannot share the MSYS snapshot cache identity");
        check(CodeSandbox.RuntimeWorkspaceIdentity(workspace)==CodeSandbox.RuntimeWorkspaceIdentity(Path.Combine(workspace,".")),"equivalent directory spelling retains the same stable runtime workspace identity");
        var ambient=Path.Combine(area,"ambient");Directory.CreateDirectory(ambient);var acl=new DirectoryInfo(ambient).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-2"),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));new DirectoryInfo(ambient).SetAccessControl(acl);
        string sentinel=Path.Combine(outside,"sentinel.txt"),broad=Path.Combine(ambient,"sentinel.txt");File.WriteAllText(sentinel,"original");File.WriteAllText(broad,"original");
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var hostPipeName="msys-mason-host-"+Guid.NewGuid().ToString("N");
        using var hostPipe=new System.IO.Pipes.NamedPipeServerStream(hostPipeName,System.IO.Pipes.PipeDirection.InOut,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous);
        string? secret=Environment.GetEnvironmentVariable("MASON_TEST_SECRET");Environment.SetEnvironmentVariable("MASON_TEST_SECRET","not-a-real-secret");
        try
        {
            foreach(var (name,script,flag) in new[]{("python.exe",Python,"-c"),("node.exe",Node,"-e")})
            {
                var executable=CodeCommand.ResolveExecutable(name,workspace);
                var result=await CodeCommand.Run(executable,[flag,script,sentinel,broad,port.ToString(),hostPipeName],workspace,default,workspace);
                check(result.ExitCode==0,"actual isolated "+name+" initializes: "+result.Output);
                using var json=JsonDocument.Parse(result.Output);
                foreach(var property in json.RootElement.EnumerateObject())check(property.Value.GetBoolean(),name+" sandbox: "+property.Name);
            }
            var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new InvalidOperationException("Install Git Bash before running its compatibility acceptance checks.");
            if(CodeSandboxTools.GitBashAutoAvailable)await BashAcceptance(bash,workspace,broad,check);
            else
            {
            bool bashBlocked=false;
            try{await CodeCommand.Run(bash,["--noprofile","--norc","-c","printf forbidden > unvalidated-bash.txt"],workspace,default,workspace);}catch(UnauthorizedAccessException error)when(error.Message.Contains("No unrestricted fallback was run",StringComparison.Ordinal)){bashBlocked=true;}
            check(bashBlocked&&!File.Exists(Path.Combine(workspace,"unvalidated-bash.txt")),"unavailable Git Bash Auto rejects launch without an unrestricted fallback");
            }
            check(File.ReadAllText(sentinel)=="original"&&File.ReadAllText(broad)=="original"&&!File.Exists(sentinel+".moved"),"Python/Node preserve normal and permissive outside canaries");
            check(!hostPipe.IsConnected,"nominating an existing host pipe does not connect the sandbox or delegate a host handle");
            using var cancelled=new CancellationTokenSource(TimeSpan.FromSeconds(60));bool stopped=false,childGone=false;
            var running=CodeCommand.Run(CodeCommand.ResolveExecutable("node.exe",workspace),["-e","require('node:child_process').spawn(process.execPath,['-e',\"require('node:fs').writeFileSync('cancel-child.pid',String(process.pid));setTimeout(()=>{},60000)\"],{stdio:'inherit'});setTimeout(()=>{},60000)"],workspace,cancelled.Token,workspace);
            try
            {
                var pidFile=Path.Combine(workspace,"cancel-child.pid");var ready=System.Diagnostics.Stopwatch.StartNew();
                while(!File.Exists(pidFile)&&!running.IsCompleted&&ready.Elapsed<TimeSpan.FromSeconds(45))await Task.Delay(25);
                if(!File.Exists(pidFile))throw new Exception("Node cancellation fixture did not reach its live-child readiness witness.");
                using var child=System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
                if(child.HasExited)throw new Exception("Node child exited before cancellation was requested.");
                cancelled.Cancel();try{await running;}catch(OperationCanceledException){stopped=true;}
                childGone=child.WaitForExit(3000);
            }
            finally{cancelled.Cancel();try{await running;}catch(OperationCanceledException){} }
            check(stopped&&childGone,"cancellation drains Node child exit events and closes inherited pipe writers before returning");
            var wow=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"SysWOW64","cmd.exe");
            if(File.Exists(wow))
            {
                var invalid=Path.Combine(workspace,"unsupported-x86.exe");File.Copy(wow,invalid);bool rejected=false;
                try{await CodeCommand.Run(invalid,["/d","/c","echo bad>must-not-run-x86.txt"],workspace,default,workspace);}catch(Exception error)when(error is InvalidDataException or PlatformNotSupportedException){rejected=true;}
                bool unlocked=false;
                for(int attempt=0;attempt<40;attempt++)
                {
                    try{using var file=new FileStream(invalid,FileMode.Open,FileAccess.ReadWrite,FileShare.None);unlocked=true;break;}
                    catch(IOException)when(attempt<39){await Task.Delay(50);}
                }
                check(rejected&&!File.Exists(Path.Combine(workspace,"must-not-run-x86.txt"))&&unlocked,"unsupported native image stops before payload execution and releases its debugger image-file handle");
            }

        }
        finally{Environment.SetEnvironmentVariable("MASON_TEST_SECRET",secret);}
    }
    // Required acceptance gate before enabling Git Bash Auto; not part of a Python/Node success claim.
    public static async Task BashAcceptance(string bash,string workspace,string broad,Action<bool,string> check)
    {
            var mixed=await CodeCommand.Run(bash,["--noprofile","--norc","-c","set -e; printf 'pipeline-ok\\n' | cat; python -c \"print('python-child-ok')\"; node -e \"console.log('node-child-ok')\"; if { printf bad > \"$1\"; } 2> outside-write.err; then exit 9; fi; printf 'outside-denied\\n'","qa",broad],workspace,default,workspace);
            check(mixed.ExitCode==0&&mixed.Output.Contains("pipeline-ok")&&mixed.Output.Contains("python-child-ok")&&mixed.Output.Contains("node-child-ok")&&mixed.Output.Contains("outside-denied"),"Git Bash pipelines, EOF, Python/Node children and denied outside redirection: "+mixed.Output);

            var streams=await CodeCommand.Run(bash,["--noprofile","--norc","-c","set -euo pipefail; value=$(printf 'subshell-ok'); [[ $value == subshell-ok ]]; mkdir -p 'folder with spaces'; printf 'quoted-file-ok' > 'folder with spaces/file.txt'; [[ $(cat 'folder with spaces/file.txt') == quoted-file-ok ]]; printf discarded >/dev/null; [[ -z $(cat /dev/null) ]]; python -c \"import subprocess,sys; assert subprocess.run([sys.executable,'-c','pass'],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0; print('python-devnull-ok')\"; node -e \"const c=require('node:child_process').spawnSync(process.execPath,['-e',''],{stdio:'ignore'}); if(c.status!==0)process.exit(8); console.log('node-ignore-ok')\"; set +o pipefail; yes x | head -n 1 > broken-pipe.txt; [[ $(cat broken-pipe.txt) == x ]]; printf 'subshell-quoted-null-broken-pipe-ok\\n'"],workspace,default,workspace);
            check(streams.ExitCode==0&&streams.Output.Contains("python-devnull-ok")&&streams.Output.Contains("node-ignore-ok")&&streams.Output.Contains("subshell-quoted-null-broken-pipe-ok"),"Bash subshells, quoted paths, Null EOF/write, Python/Node discarded streams and broken-pipe lifetime: "+streams.Output);
            var denied=await CodeCommand.Run(bash,["--noprofile","--norc","-c","set -e; if { printf forbidden >> \"$1\"; } 2> append-denied.err; then exit 81; fi; if { printf forbidden > \"$1.created\"; } 2> create-denied.err; then exit 82; fi; if rm -f \"$1\" 2> delete-denied.err; then exit 83; fi; if mv \"$1\" \"$1.moved\" 2> rename-denied.err; then exit 84; fi; printf 'outside-mutations-denied\\n'","qa",broad],workspace,default,workspace);
            check(denied.ExitCode==0&&denied.Output.Contains("outside-mutations-denied")&&File.ReadAllText(broad)=="original"&&!File.Exists(broad+".created")&&!File.Exists(broad+".moved"),"Bash cannot append, create, delete or rename even a permissive outside canary: "+denied.Output);
            var git=await CodeCommand.Run(bash,["--noprofile","--norc","-c","set -euo pipefail; mkdir -p git-runtime-check .qa-empty-hooks; git -c init.templateDir= -c core.hooksPath=\"$PWD/.qa-empty-hooks\" init -q git-runtime-check; cd git-runtime-check; printf 'sandboxed git\\n' > example.txt; git add example.txt; git -c core.hooksPath=\"$PWD/../.qa-empty-hooks\" -c user.name='Mason QA' -c user.email=qa@localhost commit -qm 'Sandboxed runtime acceptance'; git log -1 --format=%s"],workspace,default,workspace);
            check(git.ExitCode==0&&git.Output.Trim()=="Sandboxed runtime acceptance","native Git survives startup context restoration and completes init/add/commit inside the workspace: "+git.Output);
            for(int attempt=0;attempt<20;attempt++)
            {
                var repeated=await CodeCommand.Run(bash,["--noprofile","--norc","-c","set -euo pipefail; value=$(printf 'repeat-ok\\n' | cat); [[ $value == repeat-ok ]]; printf 'repeat-ok\\n'"],workspace,default,workspace);
                check(repeated.ExitCode==0&&repeated.Output.Trim()=="repeat-ok","fresh-identity Bash fork/pipeline repeat "+(attempt+1)+": "+repeated.Output);
            }

            using var cancelled=new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));bool stopped=false;
            try{await CodeCommand.Run(bash,["--noprofile","--norc","-c","sleep 60 | cat"],workspace,cancelled.Token,workspace);}catch(OperationCanceledException){stopped=true;}
            check(stopped,"cancelling a Bash pipeline stops its owned process tree and IPC lifetime");
    }
    const string Python="""
        import json, os, pathlib, socket, subprocess, sys, hashlib, ctypes
        outside, ambient, port, host_pipe = sys.argv[1:]
        def denied(action):
            try: action(); return False
            except (OSError, PermissionError): return True
        inside=pathlib.Path('python-inside.txt'); inside.write_text('inside')
        child="import pathlib,sys; p=pathlib.Path(sys.argv[1]);\ntry: p.write_text('bad')\nexcept PermissionError: sys.exit(0)\nsys.exit(4)"
        network=False
        try:
            with socket.socket() as connection:
                connection.settimeout(1)
                connection.connect(('127.0.0.1',int(port)))
        except PermissionError:
            network=True
        kernel=ctypes.WinDLL('kernel32',use_last_error=True); security=ctypes.WinDLL('advapi32',use_last_error=True)
        native=ctypes.WinDLL('ntdll')
        class Unicode(ctypes.Structure): _fields_=[('length',ctypes.c_ushort),('maximum',ctypes.c_ushort),('buffer',ctypes.c_void_p)]
        class Attributes(ctypes.Structure): _fields_=[('length',ctypes.c_uint),('root',ctypes.c_void_p),('name',ctypes.POINTER(Unicode)),('flags',ctypes.c_uint),('descriptor',ctypes.c_void_p),('sqos',ctypes.c_void_p)]
        device=ctypes.create_unicode_buffer('\\Device\\KsecDD'); name=Unicode(len(device.value)*2,ctypes.sizeof(device),ctypes.cast(device,ctypes.c_void_p))
        attributes=Attributes(ctypes.sizeof(Attributes),None,ctypes.pointer(name),64,None,None); crypto_handle=ctypes.c_void_p(); io=ctypes.create_string_buffer(16)
        native.NtOpenFile.argtypes=[ctypes.POINTER(ctypes.c_void_p),ctypes.c_uint,ctypes.POINTER(Attributes),ctypes.c_void_p,ctypes.c_uint,ctypes.c_uint]
        kernel.CloseHandle.argtypes=[ctypes.c_void_p]
        assert native.NtOpenFile(ctypes.byref(crypto_handle),0x100001,ctypes.byref(attributes),io,7,32)>=0
        kernel.CloseHandle(crypto_handle)
        kernel.CreatePipe.argtypes=[ctypes.POINTER(ctypes.c_void_p),ctypes.POINTER(ctypes.c_void_p),ctypes.c_void_p,ctypes.c_uint]
        kernel.WriteFile.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_uint,ctypes.c_void_p,ctypes.c_void_p]
        kernel.ReadFile.argtypes=kernel.WriteFile.argtypes
        kernel.CloseHandle.argtypes=[ctypes.c_void_p]
        security.ImpersonateNamedPipeClient.argtypes=[ctypes.c_void_p]
        security.OpenThreadToken.argtypes=[ctypes.c_void_p,ctypes.c_uint,ctypes.c_int,ctypes.POINTER(ctypes.c_void_p)]
        security.GetTokenInformation.argtypes=[ctypes.c_void_p,ctypes.c_uint,ctypes.c_void_p,ctypes.c_uint,ctypes.POINTER(ctypes.c_uint)]
        security.ConvertSidToStringSidW.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_void_p)]
        kernel.LocalFree.argtypes=[ctypes.c_void_p]
        kernel.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint,ctypes.c_uint,ctypes.c_void_p,ctypes.c_uint,ctypes.c_uint,ctypes.c_void_p];kernel.CreateFileW.restype=ctypes.c_void_p
        nominated=kernel.CreateFileW('\\\\.\\pipe\\'+host_pipe,0xc0000000,0,None,3,0,None)
        host_denied=nominated==ctypes.c_void_p(-1).value
        if not host_denied: kernel.CloseHandle(nominated)
        kernel.GetCurrentThread.restype=ctypes.c_void_p
        read=ctypes.c_void_p(); write=ctypes.c_void_p(); count=ctypes.c_uint(); byte=ctypes.create_string_buffer(1)
        assert kernel.CreatePipe(ctypes.byref(read),ctypes.byref(write),None,4096)
        try:
            assert kernel.WriteFile(write,b'x',1,ctypes.byref(count),None)
            assert kernel.ReadFile(read,byte,1,ctypes.byref(count),None)
            impersonated=security.ImpersonateNamedPipeClient(read)
            try:
                token=ctypes.c_void_p()
                queried=security.OpenThreadToken(kernel.GetCurrentThread(),8,True,ctypes.byref(token)) if impersonated else False
                anonymous=not impersonated or (not queried and ctypes.get_last_error() in (1008,1347))
                if queried:
                    try:
                        info=ctypes.create_string_buffer(1024); needed=ctypes.c_uint(); sid_text=ctypes.c_void_p()
                        assert security.GetTokenInformation(token,1,info,len(info),ctypes.byref(needed))
                        assert security.ConvertSidToStringSidW(ctypes.c_void_p.from_buffer(info),ctypes.byref(sid_text))
                        try:
                            client_sid=ctypes.wstring_at(sid_text)
                            anonymous=client_sid=='S-1-5-7'
                        finally: kernel.LocalFree(sid_text)
                    finally: kernel.CloseHandle(token)
            finally:
                if impersonated: assert security.RevertToSelf()
        finally:
            kernel.CloseHandle(read); kernel.CloseHandle(write)
        assert anonymous, f'Pipe client SID={locals().get("client_sid")}, impersonated={impersonated}, token_query={queried}, CreatePipe={ctypes.cast(kernel.CreatePipe,ctypes.c_void_p).value:x}'
        report=dict(ancestor_metadata=pathlib.Path.cwd().parent.stat().st_mode!=0,parent_listing_denied=denied(lambda:list(pathlib.Path.cwd().parent.iterdir())),sibling_read_denied=denied(lambda:pathlib.Path(outside).read_text()),
            crypto=hashlib.sha256(b'hello').hexdigest().startswith('2cf24dba'),inside_write=inside.read_text()=='inside',
            outside_overwrite=denied(lambda:pathlib.Path(outside).write_text('bad')),
            ambient_append=denied(lambda:open(ambient,'a').write('bad')),
            outside_delete=denied(lambda:pathlib.Path(outside).unlink()),
            outside_rename=denied(lambda:inside.rename(outside+'.moved')),
            runtime_read_only=denied(lambda:pathlib.Path(sys.executable).with_name('agent-created.tmp').write_text('bad')),
            child_isolated=subprocess.run([sys.executable,'-c',child,ambient],capture_output=True).returncode==0,
            network_blocked=network,host_pipe_denied=host_denied,pipe_client_cannot_impersonate_host=anonymous,secret_env_removed='MASON_TEST_SECRET' not in os.environ,
            workspace_home=pathlib.Path(os.environ['HOME']).is_relative_to(pathlib.Path.cwd()),
            local_appdata_in_private_home=pathlib.Path(os.environ['LOCALAPPDATA']).resolve().is_relative_to(pathlib.Path(os.environ['HOME']).resolve()))
        print(json.dumps(report))
        """;
    const string Node="""
        const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),cp=require('node:child_process'),net=require('node:net');
        const [outside,ambient,port,hostPipe]=process.argv.slice(1);
        const denied=fn=>{try{fn();return false}catch(e){return ['EACCES','EPERM'].includes(e.code)}};
        fs.writeFileSync('node-inside.txt','inside');
        const child=cp.spawnSync(process.execPath,['-e',"try{require('node:fs').writeFileSync(process.argv[1],'bad');process.exit(4)}catch(e){process.exit(['EACCES','EPERM'].includes(e.code)?0:5)}",ambient],{encoding:'utf8'});
        const report={ancestor_metadata:fs.statSync(path.dirname(process.cwd())).isDirectory(),parent_listing_denied:denied(()=>fs.readdirSync(path.dirname(process.cwd()))),sibling_read_denied:denied(()=>fs.readFileSync(outside)),
        crypto:crypto.createHash('sha256').update('hello').digest('hex').startsWith('2cf24dba'),inside_write:fs.readFileSync('node-inside.txt','utf8')==='inside',
        outside_overwrite:denied(()=>fs.writeFileSync(outside,'bad')),ambient_append:denied(()=>fs.appendFileSync(ambient,'bad')),outside_delete:denied(()=>fs.unlinkSync(outside)),
        outside_rename:denied(()=>fs.renameSync('node-inside.txt',outside+'.moved')),runtime_read_only:denied(()=>fs.writeFileSync(path.join(path.dirname(process.execPath),'agent-created.tmp'),'bad')),
        child_isolated:child.status===0,secret_env_removed:!('MASON_TEST_SECRET' in process.env),workspace_home:process.env.HOME.toLowerCase().startsWith(process.cwd().toLowerCase()+path.sep)};
        const socket=net.connect({host:'127.0.0.1',port:Number(port)});socket.setTimeout(1000);let done=false;
        const finish=blocked=>{if(done)return;done=true;socket.destroy();report.network_blocked=blocked;console.log(JSON.stringify(report))};
        socket.on('connect',()=>finish(false));socket.on('error',error=>finish(['EACCES','EPERM'].includes(error.code)));socket.on('timeout',()=>finish(false));
        """;
}
