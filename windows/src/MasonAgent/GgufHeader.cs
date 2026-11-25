using System.Text;

namespace Mason;

/// <summary>Bounded GGUF metadata read: architecture, file type and role hints. Never reads tensor data.</summary>
public sealed record GgufHeader(string Architecture, string Type, string Name, int? Pooling, long? ContextLength, string AdapterType)
{
    const long HeaderLimit = 16 * 1024 * 1024;
    public static GgufHeader? TryRead(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(input);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or ArgumentException or OverflowException) { return null; }
    }
    public static GgufHeader Read(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46554747 || reader.ReadUInt32() is not (2 or 3)) throw new InvalidDataException("Not a GGUF v2/v3 file.");
        reader.ReadUInt64(); var count = reader.ReadUInt64();
        if (count > 100_000) throw new InvalidDataException("Too many GGUF metadata entries.");
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        void Bound() { if (input.Position > HeaderLimit || input.Position > input.Length) throw new InvalidDataException("GGUF metadata exceeds the supported size."); }
        string Text(bool keep)
        {
            Bound(); var size = reader.ReadUInt64();
            if (size > (ulong)Math.Min(HeaderLimit, input.Length - input.Position)) throw new InvalidDataException("Invalid GGUF string.");
            if (keep && size <= 1024) return Encoding.UTF8.GetString(reader.ReadBytes((int)size));
            input.Seek((long)size, SeekOrigin.Current); Bound(); return "";
        }
        object? Value(uint type, bool keep)
        {
            Bound(); if (type == 8) return Text(keep);
            if (type == 9) { var element = reader.ReadUInt32(); var length = reader.ReadUInt64(); if (element == 9 || length > 1_000_000) throw new InvalidDataException("Invalid GGUF array."); if (element == 8) { for (ulong j = 0; j < length; j++) Text(false); return null; } var width = element switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => throw new InvalidDataException("Invalid GGUF array type.") }; input.Seek((long)length * width, SeekOrigin.Current); Bound(); return null; }
            object v = type switch { 0 => reader.ReadByte(), 1 => reader.ReadSByte(), 2 => reader.ReadUInt16(), 3 => reader.ReadInt16(), 4 => reader.ReadUInt32(), 5 => reader.ReadInt32(), 6 => reader.ReadSingle(), 7 => reader.ReadBoolean(), 10 => reader.ReadUInt64(), 11 => reader.ReadInt64(), 12 => reader.ReadDouble(), _ => throw new InvalidDataException("Invalid GGUF value type.") };
            Bound(); return v;
        }
        for (ulong i = 0; i < count; i++)
        {
            var key = Text(true); var type = reader.ReadUInt32();
            bool keep = key is "general.architecture" or "general.type" or "general.name" or "adapter.type" || key.EndsWith(".pooling_type", StringComparison.Ordinal) || key.EndsWith(".context_length", StringComparison.Ordinal);
            var value = Value(type, keep); if (keep && value != null) values[key] = value;
            // Everything the classifier needs precedes the tokenizer arrays in practice; stop once it is known.
            if (values.ContainsKey("general.architecture") && key.StartsWith("tokenizer.", StringComparison.Ordinal)) break;
        }
        var architecture = values.GetValueOrDefault("general.architecture") as string ?? "";
        long? Number(string key) => values.GetValueOrDefault(key) is { } v and not string and not bool ? Convert.ToInt64(v) : null;
        var pooling = Number(architecture + ".pooling_type");
        return new(architecture, values.GetValueOrDefault("general.type") as string ?? "", values.GetValueOrDefault("general.name") as string ?? "",
            pooling is { } p ? (int)p : null, Number(architecture + ".context_length"), values.GetValueOrDefault("adapter.type") as string ?? "");
    }
}
