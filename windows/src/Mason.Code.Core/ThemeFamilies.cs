using System.Text.Json;

namespace Mason.Code;

/// <summary>One mode of a theme family: the seven approved tokens. Everything else is derived from them.</summary>
public sealed record ThemeTokens(uint Background, uint Pane, uint Card, uint Line, uint Text, uint Accent, uint OnAccent);

/// <summary>An approved theme family with its light and dark modes.</summary>
public sealed record ThemeFamily(string Name, ThemeTokens Light, ThemeTokens Dark)
{
    public ThemeTokens For(bool dark) => dark ? Dark : Light;
    /// <summary>The lower-case, hyphenated name used by the CLI and in files ("retro-neon").</summary>
    public string Slug => Name.ToLowerInvariant().Replace(' ', '-');
}

/// <summary>
/// The five approved theme families. Values were sampled from the approved light and dark reference images.
/// The desktop app maps them onto Fluent resources; the CLI uses each family's accent.
/// </summary>
public static class ThemeFamilies
{
    public static readonly ThemeFamily Blueprint = new("Blueprint",
        new(0xF3F5FC, 0xE6EAF7, 0xE3E8F6, 0xD5DCF0, 0x172240, 0x374FAB, 0xFFFFFF),
        new(0x151B30, 0x212A44, 0x232C46, 0x2F3852, 0xF4F6FF, 0xB4B8FF, 0x151B30));
    public static readonly ThemeFamily Evergreen = new("Evergreen",
        new(0xF3F7F0, 0xE5EEE2, 0xE2ECE0, 0xD4E4D6, 0x18362A, 0x22603F, 0xFFFFFF),
        new(0x111E19, 0x1C2D24, 0x1E3027, 0x2A3F33, 0xF0FBF2, 0x95DDAF, 0x111E19));
    public static readonly ThemeFamily Graphite = new("Graphite",
        new(0xF6F7F9, 0xE9EBEF, 0xE6E9ED, 0xD9DDE4, 0x161A22, 0x124FBB, 0xFFFFFF),
        new(0x141619, 0x202328, 0x23262B, 0x2F333A, 0xF6F7FA, 0x87B0FF, 0x141619));
    public static readonly ThemeFamily Kiln = new("Kiln",
        new(0xFBF5EE, 0xF1E6DA, 0xEFE3D7, 0xE7D7C8, 0x35231E, 0x9F4125, 0xFFFFFF),
        new(0x211915, 0x2F241E, 0x322620, 0x413129, 0xFFF1E6, 0xF2AB7E, 0x211915));
    public static readonly ThemeFamily RetroNeon = new("Retro Neon",
        new(0xF7F0FE, 0xEEE2FA, 0xEDE0F9, 0xE1D3F2, 0x281132, 0x8F267B, 0xFFFFFF),
        new(0x191126, 0x271835, 0x2A1937, 0x352146, 0xFFF4FF, 0xDD8BC8, 0x191126));

    public static readonly IReadOnlyList<ThemeFamily> All = [Blueprint, Evergreen, Graphite, Kiln, RetroNeon];
    public static ThemeFamily Default => Blueprint;

    /// <summary>Finds a family by display name or slug, ignoring case; unknown names give the default.</summary>
    public static ThemeFamily Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Default;
        var key = name.Trim().Replace('-', ' ');
        return All.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase)) ?? Default;
    }

    public static bool TryFind(string? name, out ThemeFamily family)
    {
        family = Default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var key = name.Trim().Replace('-', ' ');
        var found = All.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
        if (found is null) return false;
        family = found; return true;
    }

    /// <summary>Mixes two 0xRRGGBB colours: amount 0 gives <paramref name="a"/>, 1 gives <paramref name="b"/>.</summary>
    public static uint Mix(uint a, uint b, double amount)
    {
        static uint Channel(uint c, int shift) => (c >> shift) & 0xFF;
        uint Blend(int shift) => (uint)Math.Round(Channel(a, shift) + (Channel(b, shift) - (double)Channel(a, shift)) * amount);
        return (Blend(16) << 16) | (Blend(8) << 8) | Blend(0);
    }

    /// <summary>The theme family saved in appearance.json ("Family"), shared by the desktop app and the CLI.</summary>
    public static ThemeFamily Saved(string dataHome)
    {
        try
        {
            var path = Path.Combine(dataHome, "appearance.json");
            if (!File.Exists(path)) return Default;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Family", out var family) && family.ValueKind == JsonValueKind.String ? Find(family.GetString()) : Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return Default; }
    }

    /// <summary>Saves the family into appearance.json, keeping every other appearance setting the app wrote.</summary>
    public static void Save(string dataHome, ThemeFamily family)
    {
        Directory.CreateDirectory(dataHome);
        var path = Path.Combine(dataHome, "appearance.json");
        var values = new Dictionary<string, JsonElement>();
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        }
        values["Family"] = JsonSerializer.SerializeToElement(family.Name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
