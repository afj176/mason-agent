using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Mason;

// Mason Agent identity: type, pose art and the shared themed surfaces. Colours come from the active theme
// family (ThemePalette.cs). Windows high contrast always wins over the theme.
public sealed partial class MainWindow
{
    static readonly UISettings brandSystemColors = new();
    // When files were last put back (rewind, undo, redo, restore original); the session shows the "restored" pose for a short while.
    DateTimeOffset restoredAt = DateTimeOffset.MinValue;
    Action? refreshStatusPose;

    const double RadiusSmall = 8, RadiusLarge = 20;

    // Manrope is the display face; body text and controls keep the Windows system font.
    static readonly FontFamily DisplayFont = new("ms-appx:///Assets/Fonts/Manrope.ttf#Manrope");

    static bool BrandHighContrast => new AccessibilitySettings().HighContrast;
    static Color BrandAccentColor(bool dark) => ThemePalette.Accent(dark);
    static Color BrandLine(bool dark) => ThemePalette.Line(dark);

    /// <summary>A page or panel title on the type ramp. Title sizes and above use Manrope, the brand display face.</summary>
    static TextBlock Heading(string text, double size = 22)
    {
        var label = Text(text, size);
        if (size >= 25) label.FontFamily = DisplayFont;
        return label;
    }

    /// <summary>Secondary text: Caption at 12 and below, Body above, in the theme's secondary text colour.</summary>
    static TextBlock Muted(string text, double size = 13) => new() { Text = text, Style = Token(size < 12.5 ? "MasonCaptionSecondary" : "MasonBodySecondary") };

    /// <summary>A card on the content layer: translucent card fill, 1 px stroke, 8 px corners (Fluent layering).</summary>
    static Border BrandSurface(UIElement? content, Thickness? padding = null, double radius = RadiusSmall)
    {
        var surface = new Border { Child = content, Style = Token("MasonCard") };
        if (padding is { } p) surface.Padding = p;
        if (radius < 8) surface.CornerRadius = new CornerRadius(radius);
        return surface;
    }

    /// <summary>Puts the brand surface behind an existing panel grid without re-parenting its children.</summary>
    static void SurfaceBehind(Grid panel, Thickness padding)
    {
        panel.Padding = padding;
        var back = BrandSurface(null, new Thickness(0));
        back.Margin = new Thickness(-padding.Left, -padding.Top, -padding.Right, -padding.Bottom);
        Grid.SetRowSpan(back, Math.Max(1, panel.RowDefinitions.Count));
        Grid.SetColumnSpan(back, Math.Max(1, panel.ColumnDefinitions.Count));
        panel.Children.Insert(0, back);
    }

    /// <summary>A message box that sits flush inside a composer surface: the surface is the field.</summary>
    static TextBox ComposerBox(TextBox box)
    {
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            box.Resources[key] = clear;
        box.Background = clear;
        box.BorderThickness = new Thickness(0);
        return box;
    }

    // Pose art (Assets/Poses): the bear and the level-bot, one still per state. The stills are transparent
    // cut-outs that read on every theme, so they are drawn as they are. Each pose stands for one true state.
    const string PoseOpenProject = "open-project", PoseListening = "listening", PoseReading = "reading-files", PosePlanning = "planning";
    const string PoseEditing = "editing", PoseChecks = "running-checks", PoseReview = "ready-to-review", PoseChooseModel = "choose-model";
    const string PoseDownloading = "downloading", PoseLoadingModel = "loading-model", PoseConnected = "connected", PoseApproval = "awaiting-approval";
    const string PoseFailed = "check-failed", PoseRestored = "restored", PoseAttention = "needs-attention", PoseFirstLaunch = "first-launch";

    /// <summary>The pose for what a running session is doing, from the tool it is using (read, edit, check) or planning between calls.</summary>
    internal static string PoseForTool(string? tool)
    {
        if (string.IsNullOrEmpty(tool)) return PosePlanning;
        bool Has(params string[] words) => words.Any(w => tool.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Has("edit", "replace", "create", "write", "delete", "rename", "move", "patch")) return PoseEditing;
        if (Has("run_", "shell", "check", "review", "test", "browser", "build", "verify", "workflow", "preview")) return PoseChecks;
        if (Has("read", "open", "list", "search", "find", "grep", "symbol", "definition", "reference", "documentation", "skill")) return PoseReading;
        return PosePlanning;
    }

    // Poses are wider than tall, so a pose is sized by its height and keeps its own width.
    static BitmapImage PoseSource(string pose, double height) => new(new Uri($"ms-appx:///Assets/Poses/{pose}.png")) { DecodePixelHeight = (int)Math.Min(720, height * 2) };

    /// <summary>One pose still, <paramref name="size"/> pixels tall. <paramref name="meaning"/> is what assistive technology reads.</summary>
    static Image MascotImage(string pose, double size, string meaning)
    {
        var image = new Image { Source = PoseSource(pose, size), Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(image, "Mason Agent: " + meaning);
        return image;
    }

    /// <summary>The app icon (the bear with the level-bot): the title bar logo and the agent's speaker mark.</summary>
    static Image AppIconImage(double size)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Brand/icon.png")) { DecodePixelWidth = (int)(size * 2) },
            Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(image, "Mason Agent");
        return image;
    }

    /// <summary>Something that tried and broke: the "check failed" pose beside the plain explanation.</summary>
    static FrameworkElement TroubleNotice(string title, params string[] details) => ProblemNotice(PoseFailed, title, details);

    /// <summary>A problem notice with a pose: <see cref="PoseAttention"/> when the environment needs the user to act, <see cref="PoseFailed"/> when something broke.</summary>
    static FrameworkElement ProblemNotice(string pose, string title, params string[] details)
    {
        var text = Stack(Text(title, 20));
        foreach (var detail in details) text.Children.Add(Muted(detail, 14));
        text.VerticalAlignment = VerticalAlignment.Center;
        var notice = new Grid { ColumnSpacing = 20, Padding = new Thickness(24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        notice.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        notice.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        bool attention = pose == PoseAttention;
        notice.Children.Add(MascotImage(pose, attention ? 80 : 66, attention ? "needs your attention" : "something went wrong"));
        Grid.SetColumn(text, 1); notice.Children.Add(text);
        return notice;
    }

    /// <summary>Links take the app accent from App.xaml; nothing to override per control.</summary>
    static HyperlinkButton BrandLink(HyperlinkButton link) => link;

    /// <summary>A pill button used for starter prompts. Native hover, pressed and focus states apply.</summary>
    static Button PromptChip(string label, string glyph, Action action)
    {
        var caption = new TextBlock { Text = label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        var chip = new Button
        {
            Content = Row(new FontIcon { Glyph = glyph, FontSize = 13 }, caption),
            Padding = new Thickness(14, 8, 16, 8),
            CornerRadius = new CornerRadius(16)
        };
        chip.Click += (_, _) => action();
        AutomationProperties.SetName(chip, "Use prompt: " + label);
        // Rises toward the pointer and keyboard focus like a Windows 11 card.
        return Fluent.Lift(chip, 2, 16);
    }

    /// <summary>
    /// Short entrance for content that just became visible. Skipped when Windows animations or the app's
    /// reduced-motion preference turn motion off (Fluent.MotionOn).
    /// </summary>
    static void FadeIn(UIElement element) => Fluent.Appear(element);

    /// <summary>
    /// A Windows animated icon (settings cog, magnifier, back arrow, chevron) at a given size. It plays its own
    /// pointer-over and pressed motion inside a button, and is a still glyph when motion is off or in high contrast.
    /// </summary>
    static IconElement MotifIcon(Fluent.Motif motif, double size)
    {
        var icon = Fluent.Icon(motif);
        icon.Width = icon.Height = size;
        if (icon is FontIcon glyph) glyph.FontSize = size;
        return icon;
    }

    /// <summary>The accent ramp lives in App.xaml, so accent buttons need no per-control recolouring.</summary>
    static void BrandAccent(Button button) { }

    /// <summary>Kept for call sites: controls take the accent ramp from App.xaml.</summary>
    static T BrandControl<T>(T control) where T : FrameworkElement => control;

    /// <summary>Title bar lockup, sized for the 48 px caption: the app icon and the name in the display face and the theme's text colour.</summary>
    FrameworkElement BrandHeader()
    {
        var name = new TextBlock { Text = "Mason Agent", Style = Token("MasonBody"), VerticalAlignment = VerticalAlignment.Center };
        name.FontFamily = DisplayFont; name.FontSize = 15; name.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        var lockup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        lockup.Children.Add(AppIconImage(24));
        lockup.Children.Add(name);
        AutomationProperties.SetName(lockup, "Mason Agent");
        ToolTipService.SetToolTip(lockup, "Mason Agent · Make the next move clear.");
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "app.ico")); }
        catch { /* The app still starts if the shell icon cannot be assigned. */ }
        return lockup;
    }

    // In high contrast the app theme may not follow the system palette, so read the real background.
    bool BrandIsDark()
    {
        if (!BrandHighContrast) return root.ActualTheme == ElementTheme.Dark;
        var background = brandSystemColors.GetColorValue(UIColorType.Background);
        return background.R + background.G + background.B < 384;
    }

    /// <summary>Applies the token background, text and accent colours to the window, shell chrome and title bar.</summary>
    void ApplyWindowTheme()
    {
        string[] accents = ["AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "NavigationViewSelectionIndicatorForeground", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed"];
        const string inkKey = "TextFillColorPrimaryBrush";
        if (BrandHighContrast)
        {
            foreach (var key in accents.Append(inkKey)) root.Resources.Remove(key);
            ApplyWindowMaterial(BrandIsDark());
            return;
        }
        bool dark = root.ActualTheme == ElementTheme.Dark;
        // The window is clear so Mica shows in the title bar and sidebar; pages sit on the brand paper (MainWindow.Chrome.cs).
        ApplyWindowMaterial(dark);

        void Set(string key, Color color)
        {
            // Mounted templates keep brush references, so recolour in place when possible.
            if (root.Resources.TryGetValue(key, out var prior) && prior is SolidColorBrush brush) brush.Color = color;
            else root.Resources[key] = new SolidColorBrush(color);
        }
        foreach (var key in accents) Set(key, BrandAccentColor(dark));
        // Text keeps the Fluent text ramp; an older build set the brand ink here.
        root.Resources.Remove(inkKey);
    }
}
