using System.Globalization;

namespace Mason.Code;

/// <summary>Logical IDs for new MSYS pipe buffers. These strings are never opened as host paths.</summary>
public static class CodePrivatePipes
{
    public static string? RelativeName(string name,uint process,bool creating)
    {
        var fields=name.Split('-');
        if(fields.Length!=5||fields[0].Length!=16||!fields[0].All(char.IsAsciiHexDigit)||fields[1].Length is <1 or >10||!fields[1].All(char.IsAsciiDigit)||fields[2]!="pipe"||fields[3]!="nt")return null;
        if(!uint.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out var owner)||owner==0||creating&&owner!=process)return null;
        if(fields[4].Length is <3 or >10||!fields[4].StartsWith("0x",StringComparison.Ordinal)||!fields[4][2..].All(char.IsAsciiHexDigit)||!uint.TryParse(fields[4].AsSpan(2),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var sequence)||sequence==0)return null;
        return "msys-relative:"+name;
    }
}
