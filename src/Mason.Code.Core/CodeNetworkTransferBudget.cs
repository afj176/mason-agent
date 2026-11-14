namespace Mason.Code;

internal sealed class CodeNetworkTransferBudget
{
    internal const long DirectionLimit=32L*1024*1024,CommandLimit=128L*1024*1024;
    long transferred;
    internal void Consume(ref long direction,int count)
    {
        if(count<0||count>32768)throw new IOException("Invalid network transfer accounting.");
        direction=checked(direction+count);
        if(direction>DirectionLimit||Interlocked.Add(ref transferred,count)>CommandLimit)throw new IOException("Network transfer quota exceeded.");
    }
}
