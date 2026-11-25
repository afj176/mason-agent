using System.Buffers.Binary;
using System.Text;

namespace Mason.Code;

/// <summary>Untrusted helper input only. Decoding never authorizes an operation.</summary>
public sealed record CodeHandoffRequest(uint Operation,ulong[] Arguments,ulong Caller,string Name)
{
    public const int FrameBytes=512;
    public const uint AbiVersion=3;
    public static CodeHandoffRequest Decode(uint operation,ReadOnlySpan<byte> frame)
    {
        void Invalid(){throw new InvalidDataException("Malformed native handoff request; the command was stopped.");}
        if(frame.Length!=FrameBytes)Invalid();
        uint version=BinaryPrimitives.ReadUInt32LittleEndian(frame),size=BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]);
        uint embedded=BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]),bytes=BinaryPrimitives.ReadUInt32LittleEndian(frame[12..]);
        if(version!=AbiVersion||size!=FrameBytes||operation is <1 or >7||embedded!=operation||bytes>440||(bytes&1)!=0)Invalid();
        var arguments=new ulong[5];for(int i=0;i<5;i++)arguments[i]=BinaryPrimitives.ReadUInt64LittleEndian(frame[(16+i*8)..]);
        ulong caller=BinaryPrimitives.ReadUInt64LittleEndian(frame[56..]);
        for(int i=64;i<64+bytes;i+=2)
            if(BinaryPrimitives.ReadUInt16LittleEndian(frame[i..]) is <0x20 or >0x7e)Invalid();
        if(frame[(64+(int)bytes)..].IndexOfAnyExcept((byte)0)>=0)Invalid();
        bool named=operation is 1 or 2;
        if(named!=(bytes>0)||operation==1&&arguments.Skip(2).Any(v=>v!=0)||operation==2&&arguments[4]!=0||operation is 3 or 6&&arguments[4]!=0||operation==5&&(arguments.Any(v=>v!=0)||caller!=0))Invalid();
        if(operation==7&&(arguments[0]==0||arguments[1]==0||caller==0||arguments.Skip(2).Any(v=>v!=0)))Invalid();
        return new(operation,arguments,caller,Encoding.Unicode.GetString(frame.Slice(64,(int)bytes)));
    }
}
