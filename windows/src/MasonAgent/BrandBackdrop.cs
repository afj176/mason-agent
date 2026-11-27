using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mason;

/// <summary>
/// Mica tinted with the theme family's window background so the title bar and sidebar match the
/// pages instead of taking their colour from the wallpaper. Falls back to the same solid colour when Mica is off.
/// </summary>
sealed class BrandBackdrop : SystemBackdrop
{
    MicaController? controller;
    bool dark;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (!MicaController.IsSupported()) return;
        controller = new MicaController { Kind = MicaKind.Base };
        Tint();
        controller.AddSystemBackdropTarget(target);
        controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        controller?.RemoveSystemBackdropTarget(target);
        controller?.Dispose();
        controller = null;
    }

    // This backdrop owns its MicaController configuration and tint (SetTheme), so the base handling is not used;
    // WinUI's base implementation throws "The parameter is incorrect" for a custom controller like this one.
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) { }

    /// <summary>Retints for the window's current theme.</summary>
    public void SetTheme(bool isDark) { dark = isDark; Tint(); }

    void Tint()
    {
        if (controller == null) return;
        var color = ThemePalette.Background(dark);
        controller.TintColor = color;
        controller.FallbackColor = color;
        controller.TintOpacity = dark ? .85f : .8f;
        controller.LuminosityOpacity = 1f;
    }
}
