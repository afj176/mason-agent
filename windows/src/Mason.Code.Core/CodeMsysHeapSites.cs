using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace Mason.Code;

/// <summary>Inspected call-site contract for the hash-pinned MSYS candidate.</summary>
public static class CodeMsysHeapSites
{
    const long PreferredBase=0x210040000;
    public const int RootReserve=(int)(0x21018d64d-PreferredBase),RootCommit=(int)(0x21018d674-PreferredBase);
    public const int ForkReserve=(int)(0x21018d718-PreferredBase),ForkCommit=(int)(0x21018d73d-PreferredBase);
    public const int SmallForkReserve=(int)(0x21018d85d-PreferredBase);
    public const int RootUserReserve=(int)(0x21018e4b7-PreferredBase),ForkUserReserve=(int)(0x21018e376-PreferredBase);
    public const int Thunk=(int)(0x2102349e0-PreferredBase),Import=(int)(0x21039d374-PreferredBase);
    public static void Validate(byte[] image)
    {
        using var stream=new MemoryStream(image,false);using var pe=new PEReader(stream);
        if(pe.PEHeaders.PEHeader?.ImageBase!=(ulong)PreferredBase)throw new InvalidDataException("MSYS preferred-base contract mismatch.");
        foreach(int site in new[]{RootReserve,RootCommit,ForkReserve,SmallForkReserve,ForkCommit,RootUserReserve,ForkUserReserve})
        {
            var bytes=pe.GetSectionData(site-5).GetContent(0,5).AsSpan();
            if(bytes[0]!=0xe8||checked(site+BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]))!=Thunk)throw new InvalidDataException("MSYS heap CALL contract mismatch.");
        }
        var jump=pe.GetSectionData(Thunk).GetContent(0,6).AsSpan();
        if(jump[0]!=0xff||jump[1]!=0x25||checked(Thunk+6+BinaryPrimitives.ReadInt32LittleEndian(jump[2..]))!=Import)throw new InvalidDataException("MSYS heap import JMP contract mismatch.");
    }
}
