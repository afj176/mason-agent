using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Mason.Code;

public sealed partial class CodeSandbox
{
    static void VerifySidSet(IntPtr data,IntPtr required,IntPtr optional,string error)
    {
        int count=Marshal.ReadInt32(data),expected=optional==IntPtr.Zero?1:2;
        if(count!=expected)throw new UnauthorizedAccessException(error);
        bool first=false,second=optional==IntPtr.Zero;
        for(int i=0;i<count;i++)
        {
            var sid=Marshal.ReadIntPtr(data,8+i*Marshal.SizeOf<SidAttributes>());
            if(EqualSid(sid,required)&&!first)first=true;
            else if(optional!=IntPtr.Zero&&EqualSid(sid,optional)&&!second)second=true;
            else throw new UnauthorizedAccessException(error);
        }
        if(!first||!second)throw new UnauthorizedAccessException(error);
    }
    void VerifyPermanentPolicy(IntPtr token)
    {
        void Query(int kind,Action<IntPtr,int> inspect)
        {
            GetTokenInformationBuffer(token,kind,IntPtr.Zero,0,out int bytes);
            if(bytes is <8 or >16384)throw new UnauthorizedAccessException("Unexpected sandbox token policy size.");
            var memory=Marshal.AllocHGlobal(bytes);
            try{if(!GetTokenInformationBuffer(token,kind,memory,bytes,out _))throw new Win32Exception();inspect(memory,bytes);}
            finally{Marshal.FreeHGlobal(memory);}
        }
        bool Range(IntPtr memory,int bytes,IntPtr pointer,long length)=>length>=0&&(long)pointer>=(long)memory&&(long)pointer<=(long)memory+bytes&&length<=(long)memory+bytes-(long)pointer;
        Query(30,(memory,bytes)=>
        {
            VerifySidSet(memory,registryReadSid,loggingSid,"Sandbox capabilities changed from the exact approved capability set.");
        });
        Query(25,(memory,bytes)=>
        {
            if(new SecurityIdentifier(Marshal.ReadIntPtr(memory)).Value!="S-1-16-4096")throw new UnauthorizedAccessException("Sandbox integrity policy is not low integrity.");
        });
        Query(39,(memory,bytes)=>
        {
            if(Marshal.ReadInt16(memory)!=1)throw new UnauthorizedAccessException("Unsupported sandbox security-attribute format.");
            uint count=unchecked((uint)Marshal.ReadInt32(memory,4));IntPtr table=Marshal.ReadIntPtr(memory,8);
            if(count>32||!Range(memory,bytes,table,count*40))throw new UnauthorizedAccessException("Invalid sandbox security-attribute bounds.");
            bool lpac=false;
            for(int i=0;i<count;i++)
            {
                IntPtr entry=table+i*40;int length=unchecked((ushort)Marshal.ReadInt16(entry));var text=Marshal.ReadIntPtr(entry,8);
                if((length&1)!=0||!Range(memory,bytes,text,length))throw new UnauthorizedAccessException("Invalid sandbox security-attribute name.");
                if(Marshal.PtrToStringUni(text,length/2)!="WIN://NOALLAPPPKG")continue;
                var values=Marshal.ReadIntPtr(entry,32);
                if(lpac||Marshal.ReadInt16(entry,16)!=2||Marshal.ReadInt32(entry,24)!=1||!Range(memory,bytes,values,8)||Marshal.ReadInt64(values)==0||(Marshal.ReadInt32(entry,20)&0x10)!=0)throw new UnauthorizedAccessException("Sandbox LPAC policy is missing, disabled or malformed.");
                lpac=true;
            }
            if(!lpac)throw new UnauthorizedAccessException("The command did not preserve permanent LPAC policy.");
        });
    }
}
