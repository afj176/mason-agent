using System.Runtime.Versioning;
using System.Text.Json;
using Mason.Code;

[SupportedOSPlatform("windows")]
static class CompatibilityChecks
{
    public static async Task Run(string destination)
    {
        if(!CodeSandboxTools.GitBashAutoAvailable)throw new InvalidOperationException("Use the isolated QA build for compatibility acceptance.");
        string area=Path.GetFullPath(destination);Directory.CreateDirectory(area);
        var python=CodeCommand.ResolveExecutable("python.exe",area);
        var runtime=CodeSandboxTools.Prepare(python,area);
        string helper=Path.Combine(runtime.Roots.Single(r=>Path.GetFileName(r).StartsWith("git-",StringComparison.Ordinal)),"usr","bin","dbcw.dll");
        var result=await CodeCommand.Run(python,["-c",Python,helper],area,default,area);
        File.WriteAllText(Path.Combine(area,"report.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        if(result.ExitCode!=0)throw new Exception(result.Output);
        using var report=JsonDocument.Parse(result.Output);
        foreach(var item in report.RootElement.EnumerateObject())
        {
            if(item.Value.ValueKind!=JsonValueKind.True)throw new Exception("FAIL "+item.Name+": "+result.Output);
            Console.WriteLine("PASS "+item.Name);
        }
    }
    const string Python="""
        import ctypes as c, ctypes.wintypes as w, json, os, subprocess, sys
        k=c.WinDLL('kernel32',use_last_error=True); n=c.WinDLL('ntdll'); h=c.WinDLL(sys.argv[1]); result={}
        P=c.c_void_p; U=c.c_ulong; S=c.c_long
        class UNICODE(c.Structure): _fields_=[('Length',c.c_ushort),('MaximumLength',c.c_ushort),('Buffer',P)]
        class OA(c.Structure): _fields_=[('Length',U),('RootDirectory',P),('ObjectName',c.POINTER(UNICODE)),('Attributes',U),('SecurityDescriptor',P),('SecurityQualityOfService',P)]
        def attrs(name,root=None,flags=0x40):
            text=c.create_unicode_buffer(name); u=UNICODE(len(name)*2,(len(name)+1)*2,c.cast(text,P)); a=OA(c.sizeof(OA),root,c.pointer(u),flags,None,None); a.keep=(text,u); return a
        k.GetAppContainerNamedObjectPath.argtypes=[P,P,U,w.LPWSTR,c.POINTER(U)]; k.GetAppContainerNamedObjectPath.restype=w.BOOL
        path=c.create_unicode_buffer(2048); needed=U(); assert k.GetAppContainerNamedObjectPath(None,None,2048,path,c.byref(needed))
        private=path.value
        if not private.startswith('\\'):
            session=U(); k.ProcessIdToSessionId(os.getpid(),c.byref(session)); private='\\Sessions\\'+str(session.value)+'\\'+private
        n.NtOpenDirectoryObject.argtypes=[c.POINTER(P),U,c.POINTER(OA)]; n.NtOpenDirectoryObject.restype=S
        directory=P(); assert n.NtOpenDirectoryObject(c.byref(directory),0xf,c.byref(attrs(private)))>=0
        k.CloseHandle.argtypes=[P]; k.CloseHandle.restype=w.BOOL
        adv=c.WinDLL('advapi32',use_last_error=True); adv.OpenProcessToken.argtypes=[P,U,c.POINTER(P)]; adv.GetTokenInformation.argtypes=[P,U,P,U,c.POINTER(U)]
        k.GetCurrentProcess.restype=P; token=P(); assert adv.OpenProcessToken(k.GetCurrentProcess(),8,c.byref(token))
        def token_state():
            state=[]
            for information in (3,11,25,29,30,31):
                b=c.create_string_buffer(4096); length=U(); assert adv.GetTokenInformation(token,information,b,len(b),c.byref(length))
                # SID buffers contain pointers; compare their referenced SID bytes for stable identity.
                if information in (11,30):
                    count=U.from_buffer(b).value; assert count==1
                    for i in range(count):
                        p=P.from_buffer(b,8+i*16).value; sid=c.string_at(p,8); state.append(sid+c.string_at(p+8,sid[1]*4)+b.raw[16+i*16:20+i*16])
                elif information in (25,31):
                    p=P.from_buffer(b).value; sid=c.string_at(p,8); state.append(sid+c.string_at(p+8,sid[1]*4))
                else:state.append(b.raw[:length.value])
            return state
        before_token=token_state()
        result['unchanged export forwarding']=c.cast(h.NtClose,P).value==c.cast(n.NtClose,P).value
        functions=[('NtCreateMutant',[c.c_ubyte],[False],0x1f0001),('NtCreateEvent',[U,c.c_ubyte],[0,False],0x1f0003),('NtCreateSemaphore',[S,S],[0,2],0x1f0003),('NtCreateTimer',[U],[0],0x1f0003),('NtCreateJobObject',[],[],0x1f003f)]
        for i,(name,types,extra,access) in enumerate(functions):
            fn=getattr(h,name); fn.argtypes=[c.POINTER(P),U,c.POINTER(OA)]+types; fn.restype=S
            out=P(); status=fn(c.byref(out),access,c.byref(attrs('dbc-test-'+str(i),directory)),*extra)
            assert status>=0 and out.value,(name,hex(status&0xffffffff)); k.CloseHandle(out)
            result[name+' private creation']=True
        fn=h.NtCreateMutant
        n.NtQuerySecurityObject.argtypes=[P,U,P,U,c.POINTER(U)]; n.NtQuerySecurityObject.restype=S
        def security(handle):
            b=c.create_string_buffer(4096); needed=U(); assert n.NtQuerySecurityObject(handle,4,b,len(b),c.byref(needed))>=0; return b.raw[:needed.value]
        held=P(); assert fn(c.byref(held),0x1f0001,c.byref(attrs('existing-object',directory)),False)>=0
        existing_acl=security(held); reopened=P(); existing_status=fn(c.byref(reopened),0x1f0001,c.byref(attrs('existing-object',directory,0xc0)),False)
        result['existing private object ACL and open semantics preserved']=existing_status==0x40000000 and bool(reopened.value) and security(held)==existing_acl
        if reopened.value:k.CloseHandle(reopened)
        k.CloseHandle(held)
        n.NtCreateMutant.argtypes=fn.argtypes; n.NtCreateMutant.restype=S
        original=P(); adapted=P(); a=n.NtCreateMutant(c.byref(original),0x1f0001,None,False); b=fn(c.byref(adapted),0x1f0001,None,False)
        result['anonymous requests retain original kernel behavior']=a==b and bool(original.value)==bool(adapted.value)
        if original.value:k.CloseHandle(original)
        if adapted.value:k.CloseHandle(adapted)
        def create(name,root=directory,access=0x1f0001,flags=0x40):
            out=P(); status=fn(c.byref(out),access,c.byref(attrs(name,root,flags)),False)
            if out.value:k.CloseHandle(out)
            return status
        result['host namespace is not adapted']=create('\\BaseNamedObjects\\dbc-forbidden',None)<0
        result['nested names receive no compatibility']=create('missing\\nested')<0
        with open('root-is-file.txt','w') as f:
            import msvcrt
            result['file handles cannot nominate object directories']=create('dbc-wrong-type',P(msvcrt.get_osfhandle(f.fileno())))<0
        event=h.NtCreateEvent; out=P(); status=event(c.byref(out),0x1f0003,c.byref(attrs('invalid-event',directory)),9,False)
        result['invalid event type retains kernel rejection']=status<0 and not out.value
        semaphore=h.NtCreateSemaphore; out=P(); status=semaphore(c.byref(out),0x1f0003,c.byref(attrs('invalid-semaphore',directory)),3,2)
        result['invalid semaphore bounds retain kernel rejection']=status<0 and not out.value
        link_types=[c.POINTER(P),U,c.POINTER(OA),c.POINTER(UNICODE)]
        h.NtCreateSymbolicLinkObject.argtypes=link_types;h.NtCreateSymbolicLinkObject.restype=S
        n.NtCreateSymbolicLinkObject.argtypes=link_types;n.NtCreateSymbolicLinkObject.restype=S
        n.NtOpenSymbolicLinkObject.argtypes=[c.POINTER(P),U,c.POINTER(OA)];n.NtOpenSymbolicLinkObject.restype=S
        n.NtQuerySymbolicLinkObject.argtypes=[P,c.POINTER(UNICODE),c.POINTER(U)];n.NtQuerySymbolicLinkObject.restype=S
        def link(name,target,real=False,root=directory,flags=0x40):
            text=c.create_unicode_buffer(target);u=UNICODE(len(target)*2,(len(target)+1)*2,c.cast(text,P));out=P()
            status=(n if real else h).NtCreateSymbolicLinkObject(c.byref(out),0xf0001,c.byref(attrs(name,root,flags)),c.byref(u));return status,out
        link_name='winpid.'+str(os.getpid());status,mapping=link(link_name,'77');assert status>=0 and mapping.value
        opened=P();status=n.NtOpenSymbolicLinkObject(c.byref(opened),1,c.byref(attrs(link_name,directory)))
        result['numeric PID mapping retains original-token query access']=status>=0 and bool(opened.value)
        text=c.create_unicode_buffer(32);u=UNICODE(0,64,c.cast(text,P));assert n.NtQuerySymbolicLinkObject(opened,c.byref(u),None)>=0
        result['PID mapping target remains numeric metadata']=text.value=='77'
        before_link_acl=security(mapping);status,duplicate=link(link_name,'88',flags=0xc0)
        text=c.create_unicode_buffer(32);u=UNICODE(0,64,c.cast(text,P));assert n.NtQuerySymbolicLinkObject(opened,c.byref(u),None)>=0
        result['existing PID mapping target and ACL preserved']=security(mapping)==before_link_acl and text.value=='77'
        if duplicate.value:k.CloseHandle(duplicate)
        k.CloseHandle(opened);k.CloseHandle(mapping)
        def same_link_policy(name,target,reference,root=directory):
            a,ha=link(name,target,root=root);b,hb=link(reference,target,real=True,root=root)
            same=a==b and bool(ha.value)==bool(hb.value)
            if ha.value and hb.value:same=same and security(ha)==security(hb)
            if ha.value:k.CloseHandle(ha)
            if hb.value:k.CloseHandle(hb)
            return same
        result['non-numeric link names retain original kernel policy']=same_link_policy('unrelated-pid','123','unrelated-pid-reference')
        result['invalid PID mapping targets receive no adaptation']=all(same_link_policy('winpid.'+str(100000+i),target,'reference-'+str(i)) for i,target in enumerate(['0','+1',' 1','1x','4294967296','123/456','123\\456','\\Device\\Null']))
        result['invalid PID mapping names receive no adaptation']=all(same_link_policy(name,'77','bad-name-reference-'+str(i)) for i,name in enumerate(['winpid.0','winpid.+1','winpid.4294967296','winpid.123x']))
        n.NtOpenFile.argtypes=[c.POINTER(P),U,c.POINTER(OA),P,U,U];n.NtOpenFile.restype=S
        n.NtCreateNamedPipeFile.argtypes=[c.POINTER(P),U,c.POINTER(OA),P,U,U,U,U,U,U,U,U,U,c.POINTER(c.c_longlong)];n.NtCreateNamedPipeFile.restype=S
        pipe_directory=P();io=c.create_string_buffer(16);assert n.NtOpenFile(c.byref(pipe_directory),0x100080,c.byref(attrs('\\Device\\NamedPipe\\')),io,3,0)>=0
        pipe_name='0123456789abcdef-'+str(os.getpid())+'-pipe-nt-0x1';server=P();timeout=c.c_longlong(-500000)
        status=n.NtCreateNamedPipeFile(c.byref(server),0x80100100,c.byref(attrs(pipe_name,pipe_directory)),io,3,2,0x20,0,0,0,1,4096,4096,c.byref(timeout));assert status>=0 and server.value,hex(status&0xffffffff)
        client=P();assert n.NtOpenFile(c.byref(client),0x40100080,c.byref(attrs(pipe_name,pipe_directory)),io,0,0x20)>=0
        k.WriteFile.argtypes=[P,P,U,c.POINTER(U),P];k.ReadFile.argtypes=k.WriteFile.argtypes
        data=c.create_string_buffer(b'private-pipe-ok');written=U();assert k.WriteFile(client,data,15,c.byref(written),None)
        received=c.create_string_buffer(32);read=U();assert k.ReadFile(server,received,32,c.byref(read),None)
        result['relative native pipe server/client exchange uses only a new private buffer']=received.raw[:read.value]==b'private-pipe-ok' and written.value==15
        result['read pipe duplicate has no data-write authority']=not bool(k.WriteFile(server,data,1,c.byref(written),None)) and c.get_last_error()==5
        k.CloseHandle(client);k.CloseHandle(server)
        with open('invalid-pipe-root.txt','w') as f:
            bad=P();status=n.NtCreateNamedPipeFile(c.byref(bad),0x80100100,c.byref(attrs(pipe_name.replace('0x1','0x2'),P(msvcrt.get_osfhandle(f.fileno())))),io,3,2,0x20,0,0,0,1,4096,4096,c.byref(timeout))
            result['disk handles receive no relative pipe compatibility']=status<0 and not bad.value
        unknown=P();status=n.NtOpenFile(c.byref(unknown),0x40100080,c.byref(attrs(pipe_name.replace('0x1','0x3'),pipe_directory)),io,0,0x20)
        result['unknown relative pipe clients cannot nominate a host buffer']=status<0 and not unknown.value
        k.CloseHandle(pipe_directory)
        # The helper must stop adapting after its fixed per-process quota.
        statuses=[create('quota-'+str(i)) for i in range(70)]
        result['bounded synchronization adaptation']=statuses[-1]==c.c_long(0xc0000044).value and 0xc0000044 in [s&0xffffffff for s in statuses]
        with open(os.devnull,'wb') as f:f.write(b'null-write')
        with open(os.devnull,'rb') as f:result['fixed Null read EOF and write']=f.read(1)==b''
        k.GetHandleInformation.argtypes=[P,c.POINTER(U)]
        for inherit in (False,True):
            null=P();status=n.NtOpenFile(c.byref(null),0x120089,c.byref(attrs('\\Device\\Null',flags=0x42 if inherit else 0x40)),io,3,0x20);assert status>=0
            flags=U();assert k.GetHandleInformation(null,c.byref(flags));result['fixed Null preserves '+('requested inheritance' if inherit else 'noninheritance')]=bool(flags.value&1)==inherit;k.CloseHandle(null)
        child=subprocess.run([sys.executable,'-c',"print('discarded')"],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=15)
        result['Python DEVNULL child']=child.returncode==0
        # Descriptor preservation is scoped to the actual current primary token
        # and current process; the permanent security identity is not replaced.
        h.NtSetInformationToken.argtypes=[P,U,P,U];h.NtSetInformationToken.restype=S
        n.NtSetInformationToken.argtypes=h.NtSetInformationToken.argtypes;n.NtSetInformationToken.restype=S
        h.NtSetSecurityObject.argtypes=[P,U,P];h.NtSetSecurityObject.restype=S
        n.NtSetSecurityObject.argtypes=h.NtSetSecurityObject.argtypes;n.NtSetSecurityObject.restype=S
        adv.ConvertSidToStringSidW.argtypes=[P,c.POINTER(P)];adv.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes=[w.LPCWSTR,U,c.POINTER(P),c.POINTER(U)]
        adv.GetSecurityDescriptorDacl.argtypes=[P,c.POINTER(w.BOOL),c.POINTER(P),c.POINTER(w.BOOL)]
        k.LocalFree.argtypes=[P];k.LocalFree.restype=P
        user=c.create_string_buffer(4096);length=U();assert adv.GetTokenInformation(token,1,user,len(user),c.byref(length))
        sid_text=P();assert adv.ConvertSidToStringSidW(P.from_buffer(user),c.byref(sid_text));user_sid=c.wstring_at(sid_text);k.LocalFree(sid_text)
        descriptor=P();assert adv.ConvertStringSecurityDescriptorToSecurityDescriptorW('D:(D;;0x00000020;;;WD)(A;;GA;;;'+user_sid+')',1,c.byref(descriptor),None)
        present=w.BOOL();defaulted=w.BOOL();stripped=P();assert adv.GetSecurityDescriptorDacl(descriptor,c.byref(present),c.byref(stripped),c.byref(defaulted))
        own_token=P();assert adv.OpenProcessToken(k.GetCurrentProcess(),0x8a,c.byref(own_token))
        def default_acl(handle):
            b=c.create_string_buffer(16384);length=U();assert adv.GetTokenInformation(handle,6,b,len(b),c.byref(length));a=P.from_buffer(b).value;return c.string_at(a,c.c_ushort.from_address(a+2).value)
        def identities():
            found=[]
            for info in (31,11):
                b=c.create_string_buffer(4096);length=U();assert adv.GetTokenInformation(token,info,b,len(b),c.byref(length));p=P.from_buffer(b,0 if info==31 else 8).value;s=c.string_at(p,8);found.append(s+c.string_at(p+8,s[1]*4))
            return found
        def ace_entries(raw):
            offset=8;entries=[]
            for i in range(c.c_ushort.from_buffer_copy(raw,4).value):
                size=c.c_ushort.from_buffer_copy(raw,offset+2).value;assert size>=4 and offset+size<=len(raw);entries.append(raw[offset:offset+size]);offset+=size
            return entries
        def preserved(raw,mask=0x10000000):
            entries=ace_entries(raw)
            return all(any(e[0]==0 and e[1]==0 and int.from_bytes(e[4:8],'little')&mask==mask and e[8:]==sid for e in entries) for sid in identities())
        original_default=default_acl(own_token);original_process=security(k.GetCurrentProcess());stripped_aces=ace_entries(c.string_at(stripped,c.c_ushort.from_address(stripped.value+2).value))
        try:
            status=h.NtSetInformationToken(own_token,6,c.byref(stripped),c.sizeof(P));after_default=default_acl(own_token)
            result['self default DACL retains existing command grants and all supplied ACEs']=status>=0 and preserved(after_default) and ace_entries(after_default)[:len(stripped_aces)]==stripped_aces
            before_invalid=default_acl(own_token);bad=P()
            status=h.NtSetInformationToken(own_token,6,c.byref(bad),c.sizeof(P))
            result['null default DACL rejected without mutation']=status<0 and default_acl(own_token)==before_invalid
            result['invalid default DACL size rejected without mutation']=h.NtSetInformationToken(own_token,6,c.byref(stripped),1)<0 and default_acl(own_token)==before_invalid
            status=h.NtSetSecurityObject(k.GetCurrentProcess(),4,descriptor)
            process_sd=c.create_string_buffer(security(k.GetCurrentProcess()));process_acl=P();assert adv.GetSecurityDescriptorDacl(process_sd,c.byref(present),c.byref(process_acl),c.byref(defaulted))
            raw=c.string_at(process_acl,c.c_ushort.from_address(process_acl.value+2).value)
            # The kernel maps GENERIC_ALL to PROCESS_ALL_ACCESS for process ACEs.
            mapped_aces=[e[:4]+(0x1fffff).to_bytes(4,'little')+e[8:] if e[0]==0 and int.from_bytes(e[4:8],'little')==0x10000000 else e for e in stripped_aces]
            result['self process DACL retains existing grants and all supplied ACEs']=status>=0 and preserved(raw,0x1fffff) and ace_entries(raw)[:len(mapped_aces)]==mapped_aces
            before_invalid=security(k.GetCurrentProcess())
            result['invalid self process descriptor rejected without mutation']=h.NtSetSecurityObject(k.GetCurrentProcess(),4,None)<0 and security(k.GetCurrentProcess())==before_invalid
            adv.DuplicateTokenEx.argtypes=[P,U,P,U,U,c.POINTER(P)];duplicate=P();assert adv.DuplicateTokenEx(own_token,0x88,None,2,1,c.byref(duplicate))
            try:
                status=h.NtSetInformationToken(duplicate,6,c.byref(stripped),c.sizeof(P))
                result['other primary token receives original behavior without adaptation']=status>=0 and not preserved(default_acl(duplicate)) and preserved(default_acl(own_token))
            finally:k.CloseHandle(duplicate)
            result['unrelated token information retains kernel rejection']=h.NtSetInformationToken(own_token,0xffff,None,0)==n.NtSetInformationToken(own_token,0xffff,None,0)<0
            with open('descriptor-wrong-type.txt','w') as f:
                file_handle=P(msvcrt.get_osfhandle(f.fileno()))
                result['wrong object type cannot receive token descriptor adaptation']=h.NtSetInformationToken(file_handle,6,c.byref(stripped),c.sizeof(P))==n.NtSetInformationToken(file_handle,6,c.byref(stripped),c.sizeof(P))<0
            statuses=[h.NtSetInformationToken(own_token,6,c.byref(stripped),c.sizeof(P)) for i in range(18)]
            result['self descriptor update quota is bounded']=statuses[-1]==c.c_long(0xc0000044).value
        finally:
            original_buffer=c.create_string_buffer(original_default);original_pointer=P(c.addressof(original_buffer));assert n.NtSetInformationToken(own_token,6,c.byref(original_pointer),c.sizeof(P))>=0
            original_sd=c.create_string_buffer(original_process);assert n.NtSetSecurityObject(k.GetCurrentProcess(),4,original_sd)>=0
            k.CloseHandle(own_token);k.LocalFree(descriptor)
        result['permanent token identities preserved']=token_state()==before_token
        k.CloseHandle(token)
        k.CloseHandle(directory)
        print(json.dumps(result))
        """;
}
