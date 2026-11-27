using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mason;

/// <summary>Saved in appearance.json. Theme is the mode (Default = Auto, Light, Dark); Family is the theme family (ThemeFamilies), also read by the CLI.</summary>
public sealed record AppPreferences(string Theme = "Default", double FontSize = 14, bool ReducedMotion = false, WindowPlacement? Window = null, string Family = "Blueprint");
/// <summary>Where the main window was last shown, in physical pixels; restored only onto a display that still exists.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);
public static class Store
{
    public static string Root => Environment.GetEnvironmentVariable("MASON_DATA_HOME") ?? (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")) ? Path.Combine(AppContext.BaseDirectory, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MasonAgent"));
    public static T? Read<T>(string file) { var path = Path.Combine(Root, file); return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
    public static void Write<T>(string file, T value) { Directory.CreateDirectory(Root); var path = Path.Combine(Root, file); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
    public static string Protect(string text) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string text) => string.IsNullOrEmpty(text) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser));
}
