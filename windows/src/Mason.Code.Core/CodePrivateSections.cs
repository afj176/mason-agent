namespace Mason.Code;

/// <summary>Admission policy only; accepted syscalls still execute with the original permanent token.</summary>
public static class CodePrivateSections
{
    public static bool Allows(string kind,string? directory,string ownedNamespace,string name,string user,long size,uint protection,uint allocation,long file)
    {
        if(string.IsNullOrEmpty(ownedNamespace)||directory==null||kind!="Directory"||size is <=0 or >1024*1024||protection!=4||allocation!=0x08000000||file!=0)return false;
        if(!KnownName(name,user))return false;
        if(directory.Equals(ownedNamespace,StringComparison.OrdinalIgnoreCase))return true;
        return directory.StartsWith(ownedNamespace+"\\msys-",StringComparison.OrdinalIgnoreCase)&&directory[(ownedNamespace.Length+1)..].All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.');
    }
    public static bool KnownName(string name,string user)=>name=="shared.5"||name==user+".1"||name.StartsWith("cygpid.",StringComparison.Ordinal)&&uint.TryParse(name.AsSpan(7),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out uint pid)&&pid>0;
}
