using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mason;

/// <summary>
/// Maps the active theme family (Mason.Code.ThemeFamilies) onto the Fluent 2 theme resources the app overrides.
/// The dictionary is merged after XamlControlsResources so its keys win; switching family replaces it and
/// re-applies the window theme so every {ThemeResource} and code-side colour picks up the new values.
/// </summary>
static class ThemePalette
{
    static ResourceDictionary? installed;

    /// <summary>The family whose colours are on screen.</summary>
    public static ThemeFamily Current { get; private set; } = ThemeFamilies.Default;

    public static Color C(uint rgb, byte alpha = 255) => Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    static uint Mix(uint a, uint b, double amount) => ThemeFamilies.Mix(a, b, amount);

    public static ThemeTokens Tokens(bool dark) => Current.For(dark);
    public static Color Background(bool dark) => C(Tokens(dark).Background);
    public static Color Pane(bool dark) => C(Tokens(dark).Pane);
    public static Color Card(bool dark) => C(Tokens(dark).Card);
    public static Color Line(bool dark) => C(Tokens(dark).Line);
    public static Color Ink(bool dark) => C(Tokens(dark).Text);
    public static Color Accent(bool dark) => C(Tokens(dark).Accent);
    public static Color OnAccent(bool dark) => C(Tokens(dark).OnAccent);
    public static Color Muted(bool dark) => C(SecondaryText(Tokens(dark), dark));
    public static Color AccentAlpha(bool dark, byte alpha) => C(Tokens(dark).Accent, alpha);

    static uint SecondaryText(ThemeTokens t, bool dark) => Mix(t.Text, t.Background, dark ? .22 : .28);

    // Status colours are shared by every family so a failure reads the same in each theme.
    static readonly (uint Success, uint Caution, uint Critical, uint Search) LightStatus = (0x14734A, 0x8A5A00, 0xAF2323, 0x0F6E7A);
    static readonly (uint Success, uint Caution, uint Critical, uint Search) DarkStatus = (0x7EE0A6, 0xF2C66D, 0xFF9D94, 0x8FD9E3);
    public static Color Success(bool dark) => C(dark ? DarkStatus.Success : LightStatus.Success);
    public static Color Caution(bool dark) => C(dark ? DarkStatus.Caution : LightStatus.Caution);
    public static Color Critical(bool dark) => C(dark ? DarkStatus.Critical : LightStatus.Critical);
    public static Color Search(bool dark) => C(dark ? DarkStatus.Search : LightStatus.Search);

    /// <summary>Puts <paramref name="family"/>'s palette into the application resources, replacing the previous one.</summary>
    public static void Install(ThemeFamily family)
    {
        Current = family;
        var merged = Application.Current.Resources.MergedDictionaries;
        var palette = Build(family);
        int at = installed is null ? -1 : merged.IndexOf(installed);
        if (at < 0) at = Math.Min(1, merged.Count); // after XamlControlsResources, before Mason.xaml
        else merged.RemoveAt(at);
        merged.Insert(at, palette);
        installed = palette;
    }

    public static ResourceDictionary Build(ThemeFamily family)
    {
        var palette = new ResourceDictionary();
        palette.ThemeDictionaries["Light"] = Mode(family, dark: false);
        palette.ThemeDictionaries["Dark"] = Mode(family, dark: true);
        palette.ThemeDictionaries["HighContrast"] = new ResourceDictionary();
        return palette;
    }

    static ResourceDictionary Mode(ThemeFamily family, bool dark)
    {
        var t = family.For(dark);
        var d = new ResourceDictionary();
        void Brush(string key, uint rgb, byte alpha = 255) => d[key] = new SolidColorBrush(C(rgb, alpha));
        void Pair(string key, uint rgb, byte alpha = 255) { d[key] = C(rgb, alpha); d[key + "Brush"] = new SolidColorBrush(C(rgb, alpha)); }

        uint bg = t.Background, pane = t.Pane, card = t.Card, line = t.Line, ink = t.Text, accent = t.Accent, on = t.OnAccent;
        uint white = 0xFFFFFF, black = 0x000000;
        uint layer = dark ? bg : Mix(bg, white, .5);
        var status = dark ? DarkStatus : LightStatus;
        uint hover = dark ? Mix(accent, bg, .10) : Mix(accent, black, .10);
        uint pressed = dark ? Mix(accent, bg, .20) : Mix(accent, black, .20);
        uint linkHover = dark ? Mix(accent, white, .30) : Mix(accent, black, .20);

        // Accent: the family's light-mode accent is the darker end of the ramp, its dark-mode accent the lighter end.
        uint light = family.Light.Accent, bright = family.Dark.Accent;
        d["SystemAccentColor"] = C(Mix(light, bright, .5));
        d["SystemAccentColorLight1"] = C(Mix(light, bright, .7));
        d["SystemAccentColorLight2"] = C(bright);
        d["SystemAccentColorLight3"] = C(Mix(bright, white, .4));
        d["SystemAccentColorDark1"] = C(light);
        d["SystemAccentColorDark2"] = C(Mix(light, black, .12));
        d["SystemAccentColorDark3"] = C(Mix(light, black, .24));

        Brush("NavigationViewContentBackground", bg);
        Pair("SolidBackgroundFillColorBase", bg);
        Pair("SolidBackgroundFillColorSecondary", dark ? Mix(bg, black, .2) : pane);
        Pair("SolidBackgroundFillColorTertiary", layer);
        Pair("SolidBackgroundFillColorQuarternary", dark ? card : white);
        Pair("LayerFillColorDefault", layer);
        Pair("LayerFillColorAlt", dark ? pane : white);
        Pair("LayerOnMicaBaseAltFillColorDefault", layer);
        Pair("CardBackgroundFillColorDefault", card);
        Pair("CardBackgroundFillColorSecondary", pane);
        Pair("CardStrokeColorDefault", line);
        Pair("DividerStrokeColorDefault", line);
        Pair("SurfaceStrokeColorDefault", line);
        if (dark)
        {
            Pair("ControlStrokeColorDefault", ink, 0x12); Pair("ControlStrokeColorSecondary", ink, 0x18);
            Pair("ControlFillColorDefault", ink, 0x0F); Pair("ControlFillColorSecondary", ink, 0x15); Pair("ControlFillColorTertiary", ink, 0x08);
            Pair("ControlFillColorInputActive", pane);
            Pair("ControlAltFillColorSecondary", black, 0x19);
            Pair("SubtleFillColorSecondary", ink, 0x0F); Pair("SubtleFillColorTertiary", ink, 0x0A);
        }
        else
        {
            Pair("ControlStrokeColorDefault", ink, 0x0F); Pair("ControlStrokeColorSecondary", ink, 0x29);
            Pair("ControlFillColorDefault", white, 0xB3); Pair("ControlFillColorSecondary", white, 0x80); Pair("ControlFillColorTertiary", white, 0x4D);
            Pair("ControlFillColorInputActive", white);
            Pair("ControlAltFillColorSecondary", ink, 0x06);
            Pair("SubtleFillColorSecondary", ink, 0x09); Pair("SubtleFillColorTertiary", ink, 0x06);
        }
        Pair("TextFillColorPrimary", ink);
        Pair("TextFillColorSecondary", SecondaryText(t, dark));
        Pair("TextFillColorTertiary", Mix(ink, bg, dark ? .48 : .50));
        Pair("TextFillColorDisabled", Mix(ink, bg, .62));

        double tint = dark ? .80 : .90;
        Pair("SystemFillColorSuccess", status.Success); Pair("SystemFillColorSuccessBackground", Mix(status.Success, bg, tint));
        Pair("SystemFillColorCaution", status.Caution); Pair("SystemFillColorCautionBackground", Mix(status.Caution, bg, tint));
        Pair("SystemFillColorCritical", status.Critical); Pair("SystemFillColorCriticalBackground", Mix(status.Critical, bg, tint));
        Pair("SystemFillColorAttention", accent); Pair("SystemFillColorAttentionBackground", Mix(accent, bg, dark ? .75 : .88));
        Pair("SystemFillColorNeutral", SecondaryText(t, dark)); Pair("SystemFillColorNeutralBackground", pane);
        Pair("MasonSearchColor", status.Search);
        Pair("MasonDiffAddedColor", status.Success, dark ? (byte)0x2E : (byte)0x22);
        Pair("MasonDiffRemovedColor", status.Critical, dark ? (byte)0x2E : (byte)0x22);
        Pair("MasonChartCanvasColor", dark ? card : white);

        Brush("ListViewItemBackgroundSelected", accent, dark ? (byte)0x2E : (byte)0x1E);
        Brush("ListViewItemBackgroundSelectedPointerOver", accent, dark ? (byte)0x38 : (byte)0x28);
        Brush("ListViewItemBackgroundSelectedPressed", accent, dark ? (byte)0x24 : (byte)0x16);
        d["AcrylicBackgroundFillColorDefaultBrush"] = new AcrylicBrush
        {
            TintColor = C(dark ? card : white), TintOpacity = dark ? .15 : 0, TintLuminosityOpacity = dark ? .96 : .85, FallbackColor = C(dark ? card : layer)
        };

        foreach (var key in new[] { "AccentButtonBackground", "NavigationViewSelectionIndicatorForeground", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked",
                     "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundStrokeIndeterminate", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver",
                     "ToggleButtonBackgroundCheckedPressed", "ProgressBarForeground", "TextControlBorderBrushFocused", "ListViewItemSelectionIndicatorBrush",
                     "AccentFillColorDefaultBrush", "AccentTextFillColorPrimaryBrush", "HyperlinkButtonForeground", "HyperlinkForeground" })
            Brush(key, accent);
        Brush("AccentButtonBackgroundPointerOver", hover); Brush("AccentFillColorSecondaryBrush", hover);
        Brush("AccentButtonBackgroundPressed", pressed); Brush("AccentFillColorTertiaryBrush", pressed);
        foreach (var key in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "CheckBoxCheckGlyphForegroundChecked",
                     "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed", "TextOnAccentFillColorPrimaryBrush" })
            Brush(key, on);
        Brush("HyperlinkButtonForegroundPointerOver", linkHover); Brush("HyperlinkButtonForegroundPressed", linkHover);
        return d;
    }
}
