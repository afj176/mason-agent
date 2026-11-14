namespace Mason.Code;

/// <summary>Per-command limits. Reopening a shared section does not allocate its bytes again.</summary>
internal sealed class CodePrivateObjectBudget
{
    readonly Dictionary<string,long> sections=new(StringComparer.Ordinal);
    readonly Dictionary<uint,int> requests=[];
    int total;
    long bytes;
    internal void Admit(uint process,string? section=null,long size=0)
    {
        int perProcess=requests.GetValueOrDefault(process)+1;
        if(total>=1024||perProcess>64)throw new InvalidDataException("Private runtime request budget exceeded (1,024 per command, 64 per process).");
        long previous=section==null?0:sections.GetValueOrDefault(section);
        long additional=Math.Max(0,size-previous);
        if(section!=null&&(size is <=0 or >1048576||(!sections.ContainsKey(section)&&sections.Count>=64)||additional>16777216-bytes))
            throw new InvalidDataException("Private runtime section budget exceeded (64 distinct names, 16 MiB total, 1 MiB per section).");
        // Commit only after every bound passes. Names are already validated by the caller.
        total++;requests[process]=perProcess;
        if(section!=null){sections[section]=Math.Max(previous,size);bytes+=additional;}
    }
}
