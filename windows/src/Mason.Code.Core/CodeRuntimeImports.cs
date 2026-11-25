using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Mason.Code;

/// <summary>Validates the first-party native contract and edits only a staged PE import module name.</summary>
public static class CodeRuntimeImports
{
    static readonly string[] Wrapped=["NtCreateMutant","NtCreateEvent","NtCreateSemaphore","NtCreateTimer","NtCreateJobObject","NtCreateSymbolicLinkObject","NtSetInformationToken","NtSetSecurityObject"];
    public sealed record Prepared(string SourceSha256,string SnapshotSha256,string CompatibilitySha256,int[] ChangedOffsets,string[] Imports,[property:JsonIgnore] byte[] Image);
    static readonly string[] HandoffWrapped=[..Wrapped,"NtCreateDirectoryObject","NtCreateSection"];
    public static Prepared Redirect(byte[] source,byte[] compatibility)
    {
        try{return RedirectCore(source,compatibility,Wrapped);}
        catch(OverflowException error){throw new InvalidDataException("Native image metadata exceeded the bounded address range.",error);}
    }
    /// <summary>Separate, explicit contract for the handoff candidate; never selected by environment variables.</summary>
    public static Prepared RedirectHandoff(byte[] source,byte[] compatibility,byte[] facade)
    {
        try
        {
            var native=RedirectCore(source,compatibility,HandoffWrapped);
            var image=new Pe(native.Image);var helper=new Pe(facade);var exports=helper.Exports();
            helper.Require(exports.Name=="dbck.dll"&&(helper.U16(helper.Optional+70)&0x160)==0x160);
            var contract=new Pe(compatibility).Exports();
            image.Require(contract.Symbols.TryGetValue("DbcBrokerCall",out var thunk)&&thunk==null);
            foreach(var (symbol,forward) in contract.Symbols)
                image.Require(HandoffWrapped.Contains(symbol)||symbol=="DbcBrokerCall"?forward==null:forward=="ntdll."+symbol);
            foreach(var (symbol,forward) in exports.Symbols)
                helper.Require(symbol is "VirtualAlloc" or "RtlRestoreContext"?forward==null:forward=="KERNEL32."+symbol);
            var imports=image.Imports("kernel32.dll");
            image.Require(imports.Symbols.Count>=50&&imports.Symbols.Contains("VirtualAlloc")&&exports.Symbols.Keys.Order().SequenceEqual(imports.Symbols.Order()));
            var result=native.Image.ToArray();Encoding.ASCII.GetBytes("dbck.dll\0\0\0\0\0").CopyTo(result,imports.Module);
            var changes=Enumerable.Range(0,result.Length).Where(i=>result[i]!=source[i]).ToArray();
            image.Require(changes.All(i=>native.ChangedOffsets.Contains(i)||i>=imports.Module&&i<imports.Module+13));
            return new(Hash(source),Hash(result),Hash(compatibility),changes,[..native.Imports,..imports.Symbols],result);
        }
        catch(OverflowException error){throw new InvalidDataException("Native handoff metadata exceeded the bounded address range.",error);}
    }
    static Prepared RedirectCore(byte[] source,byte[] compatibility,string[] wrapped)
    {
        var image=new Pe(source);var helper=new Pe(compatibility);
        image.Require(image.U32(image.Optional+144)==0&&image.U32(image.Optional+148)==0);
        // Bound/delayed imports require a separate compatibility decision.
        image.Require(image.U32(image.Optional+200)==0&&image.U32(image.Optional+204)==0&&image.U32(image.Optional+216)==0&&image.U32(image.Optional+220)==0);
        var exports=helper.Exports();helper.Require(exports.Name=="dbcw.dll"&&(helper.U16(helper.Optional+70)&0x140)==0x140);
        foreach(var name in wrapped)helper.Require(exports.Symbols.TryGetValue(name,out var forward)&&forward==null);
        uint directory=image.U32(image.Optional+120),size=image.U32(image.Optional+124);
        image.Require(directory!=0&&size is >=40 and <=65536);int begin=image.Raw(directory,size),end=begin+(int)size,module=-1;bool terminated=false;
        var imports=new List<string>();
        for(int at=begin;at<=end-20;at+=20)
        {
            uint original=image.U32(at),stamp=image.U32(at+4),chain=image.U32(at+8),name=image.U32(at+12),iat=image.U32(at+16);
            if((original|stamp|chain|name|iat)==0){terminated=true;break;}
            var dll=image.Text(name);
            if(!dll.Equals("ntdll.dll",StringComparison.OrdinalIgnoreCase))continue;
            image.Require(module<0&&stamp==0&&chain==0&&original!=0&&iat!=0);module=image.Raw(name,10);
            for(uint index=0;index<4096;index++)
            {
                ulong thunk=image.U64(image.Raw(checked(original+index*8),8));
                if(thunk==0){image.Require(index>0);break;}
                image.Require(index<4095&&(thunk&0x8000000000000000)==0&&thunk<=uint.MaxValue-2);
                string symbol=image.Text((uint)thunk+2);image.Require(exports.Symbols.TryGetValue(symbol,out var forward));
                helper.Require(wrapped.Contains(symbol)?forward==null:forward=="ntdll."+symbol);
                imports.Add(symbol);
            }
        }
        image.Require(terminated&&module>=0&&wrapped.All(imports.Contains));
        var result=source.ToArray();Encoding.ASCII.GetBytes("dbcw.dll\0\0").CopyTo(result,module);
        int[] changes=Enumerable.Range(module,10).Where(i=>result[i]!=source[i]).ToArray();
        image.Require(changes.Length>0);
        return new(Hash(source),Hash(result),Hash(compatibility),changes,imports.ToArray(),result);
    }
    static string Hash(byte[] data)=>Convert.ToHexStringLower(SHA256.HashData(data));
    sealed class Pe
    {
        readonly byte[] bytes;readonly List<(uint Virtual,uint Raw,uint Bytes,uint Flags)> sections=[];
        public int Optional {get;}
        public void Require(bool value){if(!value)throw new InvalidDataException("Unsupported native compatibility image or import contract; no runtime transformation was applied.");}
        public ushort U16(int at){Require(at>=0&&at<=bytes.Length-2);return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at,2));}
        public uint U32(int at){Require(at>=0&&at<=bytes.Length-4);return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));}
        public ulong U64(int at){Require(at>=0&&at<=bytes.Length-8);return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at,8));}
        public Pe(byte[] data)
        {
            bytes=data;Require(bytes.Length is >=512 and <=64*1024*1024&&U16(0)==0x5a4d);uint header=U32(60);Require(header>=64&&header<=bytes.Length-264);int pe=(int)header;Optional=pe+24;
            Require(U32(pe)==0x4550&&U16(pe+4)==0x8664&&(U16(pe+22)&0x2000)!=0&&U16(pe+20)>=240&&U16(Optional)==0x20b&&U32(Optional+108)>=14);
            int count=U16(pe+6),table=Optional+U16(pe+20);Require(count is >=1 and <=96&&table<=bytes.Length-count*40);
            for(int i=0;i<count;i++){int at=table+i*40;uint raw=U32(at+20),length=U32(at+16),va=U32(at+12);Require((ulong)raw+length<=(ulong)bytes.Length);sections.Add((va,raw,length,U32(at+36)));}
        }
        public int Raw(uint rva,uint size)
        {
            var matches=sections.Where(s=>rva>=s.Virtual&&(ulong)rva+size<=(ulong)s.Virtual+s.Bytes).ToArray();Require(matches.Length==1);
            // Metadata edits must never land in an executable section.
            Require((matches[0].Flags&0x20000000)==0);return checked((int)(matches[0].Raw+rva-matches[0].Virtual));
        }
        public string Text(uint rva)
        {
            var result=new StringBuilder();for(uint i=0;i<256;i++){byte c=bytes[Raw(checked(rva+i),1)];if(c==0)return result.ToString();Require(c is >=0x20 and <=0x7e);result.Append((char)c);}throw new InvalidDataException("Native symbol exceeds the import-name limit.");
        }
        public (int Module,List<string> Symbols) Imports(string requested)
        {
            uint directory=U32(Optional+120),size=U32(Optional+124);
            Require(directory!=0&&size is >=40 and <=65536);int begin=Raw(directory,size),end=begin+(int)size,module=-1;bool terminated=false;
            var symbols=new List<string>();
            for(int at=begin;at<=end-20;at+=20)
            {
                uint original=U32(at),stamp=U32(at+4),chain=U32(at+8),name=U32(at+12),iat=U32(at+16);
                if((original|stamp|chain|name|iat)==0){terminated=true;break;}
                if(!Text(name).Equals(requested,StringComparison.OrdinalIgnoreCase))continue;
                Require(module<0&&stamp==0&&chain==0&&original!=0&&iat!=0);module=Raw(name,checked((uint)requested.Length+1));
                bool ended=false;
                for(uint index=0;index<4096;index++)
                {
                    ulong thunk=U64(Raw(checked(original+index*8),8));
                    if(thunk==0){Require(index>0);ended=true;break;}
                    Require((thunk&0x8000000000000000)==0&&thunk<=uint.MaxValue-2);
                    string symbol=Text((uint)thunk+2);Require(!symbols.Contains(symbol));symbols.Add(symbol);
                }
                Require(ended);
            }
            Require(terminated&&module>=0);return(module,symbols);
        }
        public (string Name,Dictionary<string,string?> Symbols) Exports()
        {
            uint rva=U32(Optional+112),length=U32(Optional+116);Require(rva!=0&&length>=40);int at=Raw(rva,length);
            uint count=U32(at+20),names=U32(at+24),functions=U32(at+28),nameTable=U32(at+32),ordinals=U32(at+36);Require(count is >=5 and <=4096&&names>=5&&names<=count);
            var symbols=new Dictionary<string,string?>(StringComparer.Ordinal);
            for(uint i=0;i<names;i++)
            {
                string name=Text(U32(Raw(checked(nameTable+i*4),4)));ushort ordinal=U16(Raw(checked(ordinals+i*2),2));Require(ordinal<count);
                uint function=U32(Raw(checked(functions+(uint)ordinal*4),4));Require(function!=0);
                string? forward=function>=rva&&(ulong)function<(ulong)rva+length?Text(function):null;
                if(forward==null)Require(sections.Count(s=>function>=s.Virtual&&(ulong)function<(ulong)s.Virtual+s.Bytes&&(s.Flags&0x20000000)!=0)==1);
                Require(symbols.TryAdd(name,forward));
            }
            return(Text(U32(at+12)),symbols);
        }
    }
}
