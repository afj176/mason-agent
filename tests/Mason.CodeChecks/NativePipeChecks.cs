using System.Text.Json;
using Mason.Code;

static class NativePipeChecks
{
    public static async Task Run(string destination)
    {
#if MASON_NATIVE_PIPES_QA
        var root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Choose a fresh pipe-rights fixture.");Directory.CreateDirectory(root);
        var script="""
        import ctypes as c,json
        P=c.c_void_p;U=c.c_ulong;Z=c.c_size_t
        k=c.WinDLL('kernel32',use_last_error=True);n=c.WinDLL('ntdll')
        class US(c.Structure):_fields_=[('Length',c.c_ushort),('MaximumLength',c.c_ushort),('Buffer',P)]
        class OA(c.Structure):_fields_=[('Length',U),('Root',P),('Name',P),('Attributes',U),('Security',P),('Quality',P)]
        class SA(c.Structure):_fields_=[('Length',U),('Security',P),('Inherit',c.c_int)]
        keep=[]
        def attrs(name,root=None):
            b=c.create_unicode_buffer(name);s=US(len(name)*2,len(name)*2+2,c.cast(b,P));a=OA(c.sizeof(OA),root,c.cast(c.pointer(s),P),0x40,None,None);keep.extend([b,s]);return a
        n.NtOpenFile.argtypes=[c.POINTER(P),U,c.POINTER(OA),P,U,U];n.NtOpenFile.restype=c.c_long
        n.NtQueryObject.argtypes=[P,U,P,U,c.POINTER(U)];n.NtQueryObject.restype=c.c_long
        k.CloseHandle.argtypes=[P];k.GetHandleInformation.argtypes=[P,c.POINTER(U)]
        k.CreatePipe.argtypes=[c.POINTER(P),c.POINTER(P),P,U]
        k.WriteFile.argtypes=k.ReadFile.argtypes=[P,P,U,c.POINTER(U),P]
        def granted(h):
            b=c.create_string_buffer(56);needed=U();status=n.NtQueryObject(h,0,b,len(b),c.byref(needed));assert status>=0,(hex(status&0xffffffff),needed.value,h.value);return U.from_buffer(b,4).value
        io=c.create_string_buffer(16);root=P();a=attrs('\\Device\\NamedPipe\\')
        assert n.NtOpenFile(c.byref(root),0x80100000,c.byref(a),io,3,0x20)>=0
        try:assert granted(root)==0
        finally:k.CloseHandle(root)
        checks={'fixed_root_zero_access':True}
        for i in range(8):
            r=P();w=P();sa=SA(c.sizeof(SA),None,i%2);size=[0,16,4096,8192][i%4]
            assert k.CreatePipe(c.byref(r),c.byref(w),c.byref(sa),size),c.get_last_error()
            try:
                ra,wa=granted(r),granted(w);assert ra&1 and not ra&2 and wa&2 and not wa&1,(ra,wa)
                flags=U();assert k.GetHandleInformation(r,c.byref(flags)) and flags.value&1==i%2
                assert k.GetHandleInformation(w,c.byref(flags)) and flags.value&1==i%2
                count=U();b=c.create_string_buffer(3)
                assert not k.WriteFile(r,b'bad',3,c.byref(count),None) and c.get_last_error()==5
                assert not k.ReadFile(w,b,1,c.byref(count),None) and c.get_last_error()==5
                assert k.WriteFile(w,b'yes',3,c.byref(count),None) and count.value==3
                assert k.ReadFile(r,b,3,c.byref(count),None) and b.raw==b'yes'
                extra=P();empty=attrs('',r.value) # A consumed server cannot request another broker-owned client.
                assert n.NtOpenFile(c.byref(extra),0x40100080,c.byref(empty),io,3,0x60)<0 and not extra.value
                k.CloseHandle(w);w=P()
                ok=k.ReadFile(r,b,1,c.byref(count),None)
                assert (not ok and c.get_last_error()==109) or (ok and count.value==0)
            finally:
                if w.value:k.CloseHandle(w)
                k.CloseHandle(r)
        checks.update(read_endpoint_cannot_write=True,write_endpoint_cannot_read=True,inheritance_preserved=True,roundtrip=True,actual_eof=True,consumed_server_reopen_denied=True)
        print(json.dumps(checks))
        """;
        var python=CodeCommand.ResolveExecutable("python.exe",root);var result=await CodeCommand.Run(python,["-c",script],root,default,root);
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        if(result.ExitCode!=0||result.Truncated)throw new Exception(result.Output);
        using var json=JsonDocument.Parse(result.Output);foreach(var item in json.RootElement.EnumerateObject()){if(!item.Value.GetBoolean())throw new Exception(item.Name);Console.WriteLine("PASS "+item.Name);}
#else
        await Task.CompletedTask;throw new InvalidOperationException("Use the explicit native-pipe QA build.");
#endif
    }
}
