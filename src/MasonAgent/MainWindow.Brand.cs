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

// Mason Agent identity: palette, type, mascot assets and the shared themed surfaces.
// Values mirror brand/tokens.json. Windows high contrast always wins over the brand.
public sealed partial class MainWindow
{
    static readonly UISettings brandSystemColors = new();

    // brand/tokens.json palette
    static readonly Color BrandPaper = Rgb(0xF8F6F2), BrandWhite = Rgb(0xFFFFFF), BrandInk = Rgb(0x20263C), BrandMuted = Rgb(0x535B72);
    static readonly Color BrandViolet = Rgb(0x4849A8), BrandNight = Rgb(0x171A2B), BrandRaised = Rgb(0x24283E), BrandSilver = Rgb(0xC3C9E0), BrandBright = Rgb(0xC3C2FF);
    // Hairline borders over each theme's surface ("hairline" and "seam" in brand/tokens.json).
    static readonly Color BrandLineLight = Rgb(0xD9DBE1), BrandLineDark = Rgb(0x3B4059);
    const double RadiusSmall = 8, RadiusLarge = 20;

    // Manrope is the display face; body text and controls keep the Windows system font.
    static readonly FontFamily DisplayFont = new("ms-appx:///Assets/Fonts/Manrope.ttf#Manrope");

    static Color Rgb(int value) => Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    static bool BrandHighContrast => new AccessibilitySettings().HighContrast;
    static Color BrandAccentColor(bool dark) => dark ? BrandBright : BrandViolet;
    static Color BrandLine(bool dark) => dark ? BrandLineDark : BrandLineLight;

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

    // Mortar's mascot art (Assets/Brand/mascot). One artwork set serves both themes: the
    // stills are transparent cut-outs, so they are drawn as they are, with no badge or clip behind them.
    const string MoodWelcome = "08-you-made-my-day", MoodReady = "01-that-checks-out", MoodChecking = "02-let-me-check";
    const string MoodUnsure = "03-not-convinced", MoodPleased = "04-well-played", MoodTrouble = "05-oof";
    const string MoodDone = "06-were-good", MoodWorking = "07-im-on-it";

    static BitmapImage MascotSource(string mood, double size)
        => new(new Uri(mood is "on-the-mend" or "mark-this-line"
            ? $"ms-appx:///Assets/Brand/poses/{mood}.png" : $"ms-appx:///Assets/Brand/mascot/{mood}-light.png")) { DecodePixelWidth = (int)Math.Min(768, size * 2) };

    /// <summary>One Mortar mood still. <paramref name="meaning"/> is what assistive technology reads.</summary>
    static Image MascotImage(string mood, double size, string meaning)
    {
        var image = new Image { Source = MascotSource(mood, size), Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(image, "Mortar: " + meaning);
        return image;
    }

    /// <summary>Mortar as the logo and as the agent's speaker mark (Assets/Brand/mascot.png).</summary>
    static Image MortarImage(double size)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Brand/mascot.png")) { DecodePixelWidth = (int)(size * 2) },
            Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(image, "Mortar, the Mason Agent mascot");
        return image;
    }

    /// <summary>An error or unavailable state: Mortar's "oof" beside the plain explanation.</summary>
    static FrameworkElement TroubleNotice(string title, params string[] details)
    {
        var text = Stack(Text(title, 20));
        foreach (var detail in details) text.Children.Add(Muted(detail, 14));
        text.VerticalAlignment = VerticalAlignment.Center;
        var notice = new Grid { ColumnSpacing = 20, Padding = new Thickness(24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        notice.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        notice.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var mortar = MascotImage(MoodTrouble, 72, "something went wrong");
        notice.Children.Add(mortar);
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

    /// <summary>
    /// Title bar lockup, sized for the 48 px caption: Mortar is the logo, followed by the approved outlined wordmark.
    /// The wordmark is an image on purpose; the brand guide forbids resetting it in live text.
    /// </summary>
    FrameworkElement BrandHeader()
    {
        var mortar = MortarImage(32);
        var wordmark = new Image { Height = 16, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(wordmark, "Mason Agent");
        var lockup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(2, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        lockup.Children.Add(mortar);
        lockup.Children.Add(wordmark);
        ToolTipService.SetToolTip(lockup, "Mason Agent · Make the next move clear.");

        void Update()
        {
            bool dark = BrandIsDark();
            wordmark.Source = new BitmapImage(new Uri("ms-appx:///Assets/Brand/" + (dark ? "wordmark-dark.png" : "wordmark-light.png")));
        }
        root.ActualThemeChanged += (_, _) => Update();
        brandSystemColors.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(Update);
        Update();
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
        var ink = dark ? BrandPaper : BrandInk;
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
