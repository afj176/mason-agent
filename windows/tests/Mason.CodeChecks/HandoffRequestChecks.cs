using System.Buffers.Binary;
using Mason.Code;

static class HandoffRequestChecks
{
    public static void Run()
    {
        int count=0;
        void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);count++;}
        byte[] Frame(uint operation)
        {
            var frame=new byte[512];BinaryPrimitives.WriteUInt32LittleEndian(frame,CodeHandoffRequest.AbiVersion);BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4),512);BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8),operation);return frame;
        }
        void Reject(uint operation,byte[] frame,string label){bool failed=false;try{CodeHandoffRequest.Decode(operation,frame);}catch(InvalidDataException){failed=true;}Check(failed,label);}
        Check(CodeHandoffRequest.Decode(5,Frame(5)) is {Operation:5,Name.Length:0,Caller:0},"closed Ready request decodes without granting authority");
        foreach(int length in new[]{0,63,511,513,16384})Reject(5,new byte[length],"frame length "+length+" is rejected before field access");
        foreach(int field in new[]{0,4,8}){var input=Frame(5);input[field]^=1;Reject(5,input,"version/size/operation mismatch "+field+" rejected");}
        foreach(uint op in new uint[]{0,8,0xffffffff})Reject(op,Frame(op),"unknown operation "+op+" rejected");
        foreach(byte version in new byte[]{1,2}){var legacy=Frame(5);legacy[0]=version;Reject(5,legacy,"legacy ABI "+version+" cannot silently use the context-preserving host");}
        var restore=Frame(7);BinaryPrimitives.WriteUInt64LittleEndian(restore.AsSpan(16),0x10000);BinaryPrimitives.WriteUInt64LittleEndian(restore.AsSpan(24),0x20000);BinaryPrimitives.WriteUInt64LittleEndian(restore.AsSpan(56),0x30000);
        Check(CodeHandoffRequest.Decode(7,restore).Operation==7,"closed context request decodes without authorizing a memory write");
        foreach(int offset in new[]{16,24,56}){var input=restore.ToArray();input.AsSpan(offset,8).Clear();Reject(7,input,"context request requires its context, facade and caller at "+offset);}
        foreach(int offset in new[]{32,40,48}){var input=restore.ToArray();input[offset]=1;Reject(7,input,"context request rejects additional scalar authority at "+offset);}
        var userHeap=Frame(6);userHeap[48]=1;Reject(6,userHeap,"user-heap adoption cannot carry a fifth scalar or pointer");
        foreach(uint length in new uint[]{1,439,441,442,uint.MaxValue}){var input=Frame(1);BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12),length);Reject(1,input,"invalid inline name byte length "+length+" rejected");}
        var nonAscii=Frame(1);BinaryPrimitives.WriteUInt32LittleEndian(nonAscii.AsSpan(12),2);nonAscii[64]=0xff;Reject(1,nonAscii,"non-ASCII inline name rejected");
        var embeddedNull=Frame(1);BinaryPrimitives.WriteUInt32LittleEndian(embeddedNull.AsSpan(12),2);Reject(1,embeddedNull,"embedded NUL in inline name rejected");
        var padding=Frame(5);padding[511]=1;Reject(5,padding,"nonzero trailing frame data rejected");
        foreach(int field in new[]{16,24,32,40,48,56}){var input=Frame(5);input[field]=1;Reject(5,input,"Ready cannot carry pointer or scalar authority at "+field);}
        Reject(1,Frame(1),"directory operation requires inline name");
        Reject(2,Frame(2),"section operation requires inline name");
        Console.WriteLine(count+" handoff protocol checks passed.");
    }
}
