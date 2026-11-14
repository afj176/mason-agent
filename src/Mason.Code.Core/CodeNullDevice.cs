namespace Mason.Code;

/// <summary>Two exact spellings select one prevalidated host-owned Null device; no path is opened on demand.</summary>
public static class CodeNullDevice
{
    public static uint? Access(string name,uint access)
    {
        if(!name.Equals(@"\Device\Null",StringComparison.OrdinalIgnoreCase)&&!name.Equals(@"\??\NUL",StringComparison.OrdinalIgnoreCase))return null;
        uint mapped=access&0x3fffffff;
        if((access&0x80000000)!=0)mapped|=0x120089; // FILE_GENERIC_READ
        if((access&0x40000000)!=0)mapped|=0x120116; // FILE_GENERIC_WRITE
        return (mapped&~0x12019fu)==0?mapped:null;
    }
}
