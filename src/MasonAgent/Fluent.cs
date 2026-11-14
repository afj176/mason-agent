using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.AnimatedVisuals;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Mason;

/// <summary>
/// Windows 11 motion, depth and materials shared by every page. Each effect checks <see cref="MotionOn"/> first, so
/// the Windows "Animation effects" setting and the app's own reduced-motion preference turn all of it off together.
/// Shared verbatim (apart from the namespace) by DB Cowboy, AI Gym and Mason Agent.
/// </summary>
static class Fluent
{
    static readonly UISettings settings = new();
    static readonly AccessibilitySettings accessibility = new();

    /// <summary>The app's own reduced-motion preference; set once by the window.</summary>
    public static Func<bool> ReducedMotion = () => false;

    public static bool MotionOn
    {
        get { try { return settings.AnimationsEnabled && !ReducedMotion(); } catch { return false; } }
    }

    public static bool HighContrast
    {
        get { try { return accessibility.HighContrast; } catch { return false; } }
    }

    // Windows 11 motion curves and durations (Fluent "fast" 167 ms, "normal" 250 ms, page entrance 300 ms).
    static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(167), Normal = TimeSpan.FromMilliseconds(250), Page = TimeSpan.FromMilliseconds(300);
    static CubicBezierEasingFunction Decelerate(Compositor c) => c.CreateCubicBezierEasingFunction(new(0f, 0f), new(0f, 1f));

    // ── Depth ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One shadow for the window's cards. A ThemeShadow only falls on its receivers, and a receiver may not contain
    /// the element that casts it, so each window puts one empty layer behind its pages and registers it here.
    /// </summary>
    public static readonly ThemeShadow CardShadow = new();

    public static void AddShadowReceiver(UIElement receiver) => CardShadow.Receivers.Add(receiver);

    /// <summary>
    /// Makes an interactive card rise toward the pointer: it lifts 2 px and gains a soft shadow on hover and keyboard
    /// focus, and settles slightly when pressed. Uses the GPU-backed Translation and Scale properties with implicit
    /// transitions, so the compositor animates every change without a storyboard. Cards at rest stay flat, as Fluent asks.
    /// </summary>
    public static T Lift<T>(T card, float rise = 2, float elevation = 24) where T : FrameworkElement
    {
        bool hover = false, focus = false, pressed = false;
        void Apply()
        {
            bool raised = (hover || focus) && !HighContrast;
            // ThemeShadow depth follows Translation.Z; the shadow is only attached while raised so resting cards stay crisp.
            card.Shadow = raised ? CardShadow : null;
            card.Translation = raised ? new Vector3(0, pressed ? 0 : -rise, elevation) : Vector3.Zero;
            card.Scale = pressed ? new Vector3(.985f, .985f, 1) : Vector3.One;
        }
        void Transitions()
        {
            bool on = MotionOn;
            card.TranslationTransition = on ? new Vector3Transition { Duration = Fast } : null;
            card.ScaleTransition = on ? new Vector3Transition { Duration = TimeSpan.FromMilliseconds(100) } : null;
        }
        card.SizeChanged += (_, e) => card.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        card.Loaded += (_, _) => Transitions();
        card.PointerEntered += (_, _) => { hover = true; Apply(); };
        card.PointerExited += (_, _) => { hover = false; pressed = false; Apply(); };
        card.PointerCanceled += (_, _) => { hover = false; pressed = false; Apply(); };
        // Buttons mark pointer presses handled, so listen to handled events too.
        card.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { pressed = true; Apply(); }), true);
        card.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { pressed = false; Apply(); }), true);
        card.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => { pressed = false; Apply(); }), true);
        card.GotFocus += (_, _) => { focus = card.FocusState == FocusState.Keyboard; Apply(); };
        card.LostFocus += (_, _) => { focus = false; Apply(); };
        Transitions();
        return card;
    }

    /// <summary>A floating surface (a docked composer, a toast-like panel) that always sits above the page.</summary>
    public static T Float<T>(T surface, float elevation = 32) where T : UIElement
    {
        if (HighContrast) return surface;
        surface.Shadow = CardShadow;
        surface.Translation = new Vector3(0, 0, elevation);
        return surface;
    }

    // ── Motion ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Children fade and rise in when the panel first shows them, and glide to new places when the panel reflows
    /// (a card grid going from three columns to two). Off when motion is off.
    /// </summary>
    public static T Stagger<T>(T panel, bool addDelete = false) where T : Panel
    {
        if (!MotionOn) return panel;
        var transitions = new TransitionCollection { new EntranceThemeTransition { IsStaggeringEnabled = true, FromVerticalOffset = 24 }, new RepositionThemeTransition { IsStaggeringEnabled = false } };
        if (addDelete) transitions.Add(new AddDeleteThemeTransition());
        panel.ChildrenTransitions = transitions;
        return panel;
    }

    /// <summary>Items added to or removed from a live list (messages, results) animate in and out.</summary>
    public static T Live<T>(T panel) where T : Panel
    {
        if (!MotionOn) return panel;
        panel.ChildrenTransitions = new TransitionCollection { new AddDeleteThemeTransition(), new RepositionThemeTransition() };
        return panel;
    }

    /// <summary>Content swapped inside a ContentControl cross-fades in place instead of popping.</summary>
    public static T Swap<T>(T host) where T : ContentControl
    {
        if (MotionOn) host.ContentTransitions = new TransitionCollection { new ContentThemeTransition { VerticalOffset = 12 } };
        return host;
    }

    public enum PageMotion { Entrance, EntranceFromAbove, DrillIn, SlideFromRight, SlideFromLeft }

    /// <summary>
    /// The page transitions a Frame would play (NavigationTransitionInfo: Entrance, DrillIn, Slide), done on the
    /// composition layer because these windows keep their pages alive and switch them instead of re-creating them.
    /// </summary>
    public static void PageIn(UIElement page, PageMotion motion)
    {
        if (!MotionOn) return;
        var visual = ElementCompositionPreview.GetElementVisual(page);
        var c = visual.Compositor;
        var ease = Decelerate(c);
        ElementCompositionPreview.SetIsTranslationEnabled(page, true);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = motion == PageMotion.DrillIn ? Normal : Page;
        visual.StartAnimation("Opacity", fade);
        if (motion == PageMotion.DrillIn)
        {
            var size = page.ActualSize;
            visual.CenterPoint = new Vector3(size.X / 2, Math.Min(size.Y / 2, 240), 0);
            var zoom = c.CreateVector3KeyFrameAnimation();
            zoom.InsertKeyFrame(0, new(.94f, .94f, 1)); zoom.InsertKeyFrame(1, Vector3.One, ease);
            zoom.Duration = Normal;
            visual.StartAnimation("Scale", zoom);
            return;
        }
        Vector3 from = motion switch
        {
            PageMotion.EntranceFromAbove => new(0, -28, 0),
            PageMotion.SlideFromRight => new(56, 0, 0),
            PageMotion.SlideFromLeft => new(-56, 0, 0),
            _ => new(0, 28, 0)
        };
        var move = c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, from); move.InsertKeyFrame(1, Vector3.Zero, ease);
        move.Duration = Page;
        visual.StartAnimation("Translation", move);
    }

    /// <summary>A short fade and rise for a panel that appears inside a page.</summary>
    public static void Appear(UIElement element, float rise = 8)
    {
        if (!MotionOn) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor; var ease = Decelerate(c);
        var fade = c.CreateScalarKeyFrameAnimation(); fade.Duration = Fast; fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, ease);
        var lift = c.CreateVector3KeyFrameAnimation(); lift.Duration = Fast; lift.InsertKeyFrame(0, new(0, rise, 0)); lift.InsertKeyFrame(1, Vector3.Zero, ease);
        // Translation is separate from the layout offset, so the element lands exactly where layout put it.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.StartAnimation("Opacity", fade); visual.StartAnimation("Translation", lift);
    }

    /// <summary>
    /// Shared-element hand-off: the card the user chose grows into the page it opens (ConnectedAnimationService).
    /// Returns true when the animation started, so the caller skips its own page transition.
    /// </summary>
    public static void Prepare(string key, UIElement source)
    {
        if (!MotionOn) return;
        try { ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(key, source); } catch { }
    }

    public static bool Connect(string key, UIElement destination)
    {
        try
        {
            var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation(key);
            if (animation == null) return false;
            if (!MotionOn) { animation.Cancel(); return false; }
            animation.Configuration = new BasicConnectedAnimationConfiguration();
            return animation.TryStart(destination);
        }
        catch { return false; }
    }

    // ── Loading ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A skeleton in the shape of what is loading: rounded bars that pulse gently while data is on its way. Screen
    /// readers hear "Loading" once instead of empty shapes; the pulse stops when motion is off.
    /// </summary>
    public static FrameworkElement Skeleton(params double[] lineWidths)
    {
        var panel = new StackPanel { Spacing = 10 };
        if (lineWidths.Length == 0) lineWidths = [.42, .9, .76];
        foreach (var share in lineWidths)
        {
            // A {ThemeResource} fill follows the element's own theme (the window may differ from the app theme), and
            // ControlAltFillColorQuarternary is a neutral tint that shows on white cards as well as dark surfaces.
            var bar = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource ControlAltFillColorQuarternaryBrush}'/>");
            bar.Height = share == lineWidths[0] ? 18 : 12; bar.CornerRadius = new CornerRadius(6); bar.HorizontalAlignment = HorizontalAlignment.Left;
            if (HighContrast) bar.Background = (Brush)Application.Current.Resources["SystemControlForegroundBaseLowBrush"];
            void Width() { if (panel.ActualWidth > 0) bar.Width = Math.Max(24, panel.ActualWidth * share); }
            panel.SizeChanged += (_, _) => Width();
            panel.Children.Add(bar);
        }
        AutomationProperties.SetName(panel, "Loading");
        AutomationProperties.SetLiveSetting(panel, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Loaded += (_, _) => Pulse(panel);
        return panel;
    }

    /// <summary>A skeleton for one headline number: a short label bar over a wider value bar.</summary>
    public static FrameworkElement SkeletonValue() => Skeleton(.5, .8);

    static void Pulse(UIElement element)
    {
        if (!MotionOn) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0, 1); pulse.InsertKeyFrame(.5f, .45f); pulse.InsertKeyFrame(1, 1);
        pulse.Duration = TimeSpan.FromMilliseconds(1400); pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", pulse);
    }

    // ── Icons ───────────────────────────────────────────────────────────────────────────────────────

    public enum Motif { Find, Settings, Accept, Back, ChevronDown, ChevronUpDown, Menu }

    /// <summary>
    /// A Lottie-based AnimatedIcon that plays its own pointer-over and pressed motion inside buttons and navigation
    /// items, with a static glyph as fallback (and as the only icon when motion is off or in high contrast).
    /// </summary>
    public static IconElement Icon(Motif motif)
    {
        string glyph = motif switch
        {
            Motif.Find => "",
            Motif.Settings => "",
            Motif.Accept => "",
            Motif.Back => "",
            Motif.ChevronDown => "",
            Motif.ChevronUpDown => "",
            _ => ""
        };
        var fallback = new FontIconSource { Glyph = glyph };
        if (!MotionOn || HighContrast) return new FontIcon { Glyph = glyph };
        IAnimatedVisualSource2 source = motif switch
        {
            Motif.Find => new AnimatedFindVisualSource(),
            Motif.Settings => new AnimatedSettingsVisualSource(),
            Motif.Accept => new AnimatedAcceptVisualSource(),
            Motif.Back => new AnimatedBackVisualSource(),
            Motif.ChevronDown => new AnimatedChevronDownSmallVisualSource(),
            Motif.ChevronUpDown => new AnimatedChevronUpDownSmallVisualSource(),
            _ => new AnimatedGlobalNavigationButtonVisualSource()
        };
        return new AnimatedIcon { Source = source, FallbackIconSource = fallback };
    }

    // ── Materials ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transient surfaces (menus, flyouts) take the desktop acrylic material, so they read as floating glass over
    /// whatever is behind the window. High contrast keeps the solid system surface.
    /// </summary>
    public static T Acrylic<T>(T flyout) where T : FlyoutBase
    {
        if (HighContrast) return flyout;
        flyout.SystemBackdrop = new DesktopAcrylicBackdrop();
        // The presenter paints its own fill over the backdrop; clear it so the material shows through.
        static Style Clear(Type presenter, Style? existing)
        {
            var style = new Style(presenter) { BasedOn = existing };
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
            return style;
        }
        if (flyout is MenuFlyout menu) menu.MenuFlyoutPresenterStyle = Clear(typeof(MenuFlyoutPresenter), menu.MenuFlyoutPresenterStyle);
        else if (flyout is Flyout plain) plain.FlyoutPresenterStyle = Clear(typeof(FlyoutPresenter), plain.FlyoutPresenterStyle);
        return flyout;
    }

    // ── Scroll depth ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A backdrop that drifts more slowly than the page as it scrolls (ParallaxView), for subtle depth in a hero header.
    /// The backdrop is made taller than its frame by the shift so no edge ever shows.
    /// </summary>
    public static FrameworkElement Parallax(ScrollViewer source, FrameworkElement backdrop, double shift = 72)
    {
        var view = new ParallaxView { Source = source, VerticalShift = MotionOn ? shift : 0, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Child = backdrop };
        // ParallaxView arranges its child at the child's desired width, so a backdrop that asks for no width (a Canvas,
        // or a right-aligned decoration) would get none; give it the view's full width instead.
        view.SizeChanged += (_, e) => backdrop.Width = e.NewSize.Width;
        AutomationProperties.SetAccessibilityView(view, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return view;
    }

    // ── Keyboard ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Arrow keys move focus between the cards of a grid, as they do in Windows Settings.</summary>
    public static T ArrowKeys<T>(T panel) where T : UIElement
    {
        panel.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        return panel;
    }

    // ── High contrast ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-runs every theme callback when Windows high contrast turns on or off. ActualThemeChanged alone does not fire
    /// for that switch, so the root is nudged through the other theme and back on the next frame.
    /// </summary>
    public static void FollowContrast(FrameworkElement root, Action? alsoRun = null)
    {
        // AccessibilitySettings.HighContrastChanged cannot be subscribed in an unpackaged app; the system colour
        // change that accompanies a contrast switch can, so watch that and act only when the contrast state flips.
        bool last = HighContrast;
        try
        {
            settings.ColorValuesChanged += (_, _) => root.DispatcherQueue.TryEnqueue(() =>
            {
                bool now = HighContrast;
                if (now == last) return;
                last = now;
                var chosen = root.RequestedTheme;
                root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                root.RequestedTheme = chosen;
                alsoRun?.Invoke();
            });
        }
        catch (System.Runtime.InteropServices.COMException) { /* contrast changes then apply on the next theme change */ }
    }
}
