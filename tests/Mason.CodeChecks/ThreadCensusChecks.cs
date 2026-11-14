using System.Buffers.Binary;
using Mason.Code;

static class ThreadCensusChecks
{
    public static void Run()
    {
        int count=0;
        void Check(bool condition,string name){if(!condition)throw new Exception("FAIL "+name);count++;Console.WriteLine("PASS "+name);}
        byte[] Record(uint pid,params uint[] threads)
        {
            var data=new byte[256+80*threads.Length];BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4),(uint)threads.Length);BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(80),pid);
            for(int i=0;i<threads.Length;i++){BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(256+i*80+40),pid);BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(256+i*80+48),threads[i]);}
            return data;
        }
        byte[] Join(params byte[][] records)
        {
            foreach(var record in records.SkipLast(1))BinaryPrimitives.WriteUInt32LittleEndian(record,(uint)record.Length);
            return records.SelectMany(r=>r).ToArray();
        }
        var members=new HashSet<uint>{42,43};
        void Reject(byte[] data,string name){bool denied=false;try{CodeThreadCensus.Decode(data,members);}catch(InvalidDataException){denied=true;}Check(denied,name);}
        var actual=CodeThreadCensus.Decode(Join(Record(12,100),Record(42,200,201),Record(43,300)),members);
        Check(actual.SequenceEqual(new[]{(42u,200u),(42u,201u),(43u,300u)}),"owned threads are complete and unrelated processes are excluded");
        foreach(int size in new[]{0,4,80,255})Reject(new byte[size],"truncated process header "+size+" rejected");
        foreach(uint next in new uint[]{8,257,uint.MaxValue}){var data=Record(42,200);BinaryPrimitives.WriteUInt32LittleEndian(data,next);Reject(data,"invalid next-record offset "+next+" rejected");}
        var oversized=Record(42,200);BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(4),uint.MaxValue);Reject(oversized,"thread count cannot overflow record bounds");
        Reject(Record(42,200)[..^1],"truncated final thread record rejected");
        var wrongOwner=Record(42,200);BinaryPrimitives.WriteUInt64LittleEndian(wrongOwner.AsSpan(296),43);Reject(wrongOwner,"thread ownership mismatch rejected");
        foreach(ulong id in new[]{0UL,1UL<<32}){var data=Record(42,200);BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(304),id);Reject(data,"invalid thread identity "+id+" rejected");}
        Reject(Record(42,200,200),"duplicate thread within a process rejected");
        Reject(Join(Record(42,200),Record(43,200)),"duplicate thread across processes rejected");
        Reject(Join(Record(42,200),Record(42,201)),"duplicate owned process record rejected");
        Reject(Record(42,Enumerable.Range(1,1025).Select(i=>(uint)i).ToArray()),"owned process thread bound preserved");
        Console.WriteLine(count+" native census decoder checks passed.");
    }
}
