using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Mason;

/// <summary>
/// Design review captures. With MASON_UI_CAPTURE set to a folder, Mason renders each page and Models section in
/// light and dark to PNG files there and then exits. Use with MASON_DATA_HOME pointing at a throwaway profile.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Further screens (settings pages, dialogs) register themselves here for review captures.</summary>
    readonly List<(string Name, Action Open)> extraCaptures = [];
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);

    internal async Task CaptureForReview(string folder)
    {
        Directory.CreateDirectory(folder);
        // RenderTargetBitmap cannot draw the Mica backdrop; stand in the theme's solid base colour, which Mica approximates.
        var backdrop = (Microsoft.UI.Xaml.Controls.Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource SolidBackgroundFillColorBaseBrush}'/>");
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(backdrop, Math.Max(1, root.RowDefinitions.Count)); root.Children.Insert(0, backdrop);
        // Lay out at 1600×1000 effective pixels whatever the display scaling, so captures match the published screenshots.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)Math.Round(1600 * scale), (int)Math.Round(1000 * scale)));
        await Task.Delay(1500);
        var shots = new List<(string Name, Action Go)>
        {
            ("code", () => Navigate("Code")),
            ("chat", () => Navigate("Chat")),
        };
        foreach (var (name, page) in new[] { ("models-library", 0), ("models-discover", 3), ("models-downloads", 4), ("models-runtimes", 2), ("models-connections", 5) })
            shots.Add((name, () => { Navigate("Models and connections"); selectModelView?.Invoke(page); }));
        foreach (var (name, open) in extraCaptures) shots.Add((name, open));
        // MASON_UI_CAPTURE_FAMILIES ("all" or a comma list such as "kiln,retro-neon") also captures each theme family;
        // without it only the current family is captured, under the original file names.
        var familyList = Environment.GetEnvironmentVariable("MASON_UI_CAPTURE_FAMILIES");
        var families = string.IsNullOrWhiteSpace(familyList) ? [ThemePalette.Current]
            : familyList.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? Mason.Code.ThemeFamilies.All.ToList()
            : familyList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Mason.Code.ThemeFamilies.Find).Distinct().ToList();
        bool named = !string.IsNullOrWhiteSpace(familyList);
        foreach (var family in families)
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            if (family != ThemePalette.Current) ApplyThemeFamily(family);
            root.RequestedTheme = theme;
            foreach (var (name, go) in shots)
            {
                try { go(); } catch (Exception error) { File.WriteAllText(Path.Combine(folder, name + "-error.txt"), error.ToString()); continue; }
                await Task.Delay(900);
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(root);
                var pixels = await bitmap.GetPixelsAsync();
                var path = Path.Combine(folder, named ? $"{name}-{family.Slug}-{theme.ToString().ToLowerInvariant()}.png" : $"{name}-{theme.ToString().ToLowerInvariant()}.png");
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
                await encoder.FlushAsync();
                stream.Seek(0);
                using var file = File.Create(path);
                await stream.AsStreamForRead().CopyToAsync(file);
            }
        }
        Application.Current.Exit();
    }
}
