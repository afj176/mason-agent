using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Mason.Code;

/// <summary>ASLR opt-in for a newly staged, unsigned MSYS x64 image. Never changes installed files or code bytes.</summary>
public static class CodeRuntimeImage
{
    public sealed record Prepared(string SourceSha256,string SnapshotSha256,bool AslrOptIn,int[] ChangedOffsets,[property:JsonIgnore] byte[] Image);
    public static Prepared EnableMsysAslr(byte[] source)=>Prepare(source,false)!;
    public static Prepared? PrepareMsysDependency(byte[] source)=>Prepare(source,true);
    static Prepared? Prepare(byte[] source,bool dependent)
    {
        var image=source.ToArray();
        void Require(bool condition){if(!condition)throw new InvalidDataException("Unsupported MSYS image; no ASLR snapshot transformation was applied.");}
        ushort U16(int at)=>BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at,2));
        uint U32(int at)=>BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at,4));
        Require(image.Length is >=512 and <=64*1024*1024&&U16(0)==0x5a4d);
        uint peValue=U32(60);Require(peValue>=64&&peValue<=image.Length-264);int pe=(int)peValue,opt=pe+24;
        bool x64=U16(pe+4)==0x8664&&U16(opt)==0x20b;
        bool x86=dependent&&U16(pe+4)==0x14c&&U16(opt)==0x10b;
        Require(U32(pe)==0x4550&&(x64||x86)&&(U16(pe+22)&(dependent?2:0x2000))!=0&&U16(pe+20)>=(x64?240:224)&&U32(opt+(x64?108:92))>=6);
        int count=U16(pe+6),table=opt+U16(pe+20);Require(count is >=1 and <=96&&table<=image.Length-count*40);
        uint size=U32(opt+56);Require(size>=4096&&U32(opt+60)>=table+count*40&&U32(opt+60)<=image.Length);
        var sections=new List<(uint Virtual,uint Size,uint Raw,uint Bytes)>();
        bool shared=false;
        for(int i=0;i<count;i++)
        {
            int at=table+i*40;uint va=U32(at+12),length=U32(at+8),raw=U32(at+20),bytes=U32(at+16);
            Require((ulong)raw+bytes<=(ulong)image.Length&&(ulong)va+Math.Max(length,bytes)<=size);
            sections.Add((va,Math.Max(length,bytes),raw,bytes));
            if(Encoding.ASCII.GetString(image,at,8).StartsWith(".cygwin_",StringComparison.Ordinal)&&(U32(at+36)&0xd0000000u)==0xd0000000u)shared=true;
        }
        if(!dependent)Require(shared);
        int Raw(uint rva,uint length)
        {
            var matches=sections.Where(s=>rva>=s.Virtual&&(ulong)rva+length<=(ulong)s.Virtual+s.Bytes).ToArray();
            Require(matches.Length==1);return checked((int)(matches[0].Raw+rva-matches[0].Virtual));
        }
        if(!dependent)
        {
            uint exports=U32(opt+112);Require(exports!=0);int export=Raw(exports,40);var expectedName=Encoding.ASCII.GetBytes("msys-2.0.dll\0");int name=Raw(U32(export+12),(uint)expectedName.Length);
            Require(image.AsSpan(name,expectedName.Length).SequenceEqual(expectedName));
        }
        else
        {
            uint imports=U32(opt+(x64?120:104)),length=U32(opt+(x64?124:108));if(imports==0&&length==0)return null;
            Require(imports!=0&&length is >=40 and <=4*1024*1024);int start=Raw(imports,length),finish=checked(start+(int)length);bool found=false,terminated=false;
            for(int at=start;at<=finish-20&&at-start<4096*20;at+=20)
            {
                if((U32(at)|U32(at+4)|U32(at+8)|U32(at+12)|U32(at+16))==0){terminated=true;break;}
                uint name=U32(at+12);var text=new StringBuilder();bool ended=false;
                for(uint i=0;i<256;i++){byte c=image[Raw(checked(name+i),1)];if(c==0){ended=true;break;}Require(c is >=0x20 and <=0x7e);text.Append((char)c);}
                Require(ended);if(text.ToString().Equals("msys-2.0.dll",StringComparison.OrdinalIgnoreCase))found=true;
            }
            Require(terminated);if(!found)return null;
            Require(x64); // PE32 is inspected only to exclude unrelated managed/vendor dependencies, never transformed.
        }
        uint reloc=U32(opt+152),relocSize=U32(opt+156);Require(reloc!=0&&relocSize>=8&&relocSize<=4*1024*1024);
        int cursor=Raw(reloc,relocSize),end=checked(cursor+(int)relocSize);bool relocated=false;
        while(cursor<end)
        {
            Require(end-cursor>=8);uint page=U32(cursor),block=U32(cursor+4);
            Require((page&4095)==0&&block>=8&&(block&1)==0&&block<=end-cursor);
            for(int at=cursor+8;at<cursor+block;at+=2)
            {
                ushort entry=U16(at);int type=entry>>12;Require(type is 0 or 10);
                if(type==10){ulong address=(ulong)page+(uint)(entry&4095);Require(address+8<=size&&sections.Any(s=>address>=s.Virtual&&address+8<=(ulong)s.Virtual+s.Size));relocated=true;}
            }
            cursor+=checked((int)block);
        }
        Require(relocated);
        Require((U16(pe+22)&0x20)!=0); // Existing LARGE_ADDRESS_AWARE is required for 64-bit ASLR.
        ushort flags=U16(opt+70);bool change=(flags&0x60)!=0x60;
        if(change)
        {
            // Certificate tables use file offsets. Any signature metadata prohibits transformation.
            Require(U32(opt+144)==0&&U32(opt+148)==0);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt+70,2),(ushort)(flags|0x60));
        }
        return new(Convert.ToHexStringLower(SHA256.HashData(source)),Convert.ToHexStringLower(SHA256.HashData(image)),change,change?[opt+70]:[],image);
    }
}
