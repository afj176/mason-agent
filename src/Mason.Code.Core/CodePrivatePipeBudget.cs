namespace Mason.Code;

internal sealed class CodePrivatePipeBudget
{
    int requests;long bytes;
    internal void Admit(uint incoming,uint outgoing)
    {
        long allocation=(long)Math.Max(incoming,4096u)+Math.Max(outgoing,4096u);
        if(incoming>1048576||outgoing>1048576||requests>=1024||allocation>16777216-bytes)
            throw new InvalidDataException("Private IPC budget exceeded (1,024 lifetime requests, 16 MiB total, 1 MiB per endpoint).");
        requests++;bytes+=allocation;
    }
}
