using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Mason;

/// <summary>
/// The welcome backdrop: a few courses of masonry in running bond, drawn as faint brand-violet outlines with the
/// odd brick filled in, fading out toward the edges and away from the welcome column. It drifts more slowly than
/// the welcome when a short window makes it scroll (ParallaxView), and is hidden in high contrast.
/// </summary>
public sealed partial class MainWindow
{
    const double BrickWidth = 112, BrickHeight = 34, Joint = 8, WallWidth = 1600;
    const int Courses = 8;

    /// <summary>A full-width layer for behind a welcome scroller. The caller shows it only while a welcome is on screen.</summary>
    static FrameworkElement MasonryBackdrop(ScrollViewer source)
    {
        // The wall is drawn at a design size and scaled to the width with a render transform, which never feeds back into layout.
        var scale = new ScaleTransform();
        var wall = new Canvas { RenderTransform = scale, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var bricks = new List<(Rectangle Brick, double Strength, bool Filled)>();
        for (int course = 0; course < Courses; course++)
        {
            double top = course * (BrickHeight + Joint);
            double offset = course % 2 == 0 ? 0 : -(BrickWidth + Joint) / 2;   // running bond
            for (double left = offset; left < WallWidth; left += BrickWidth + Joint)
            {
                double centre = (left + BrickWidth / 2) / WallWidth;
                // Soft at both ends and quieter behind the welcome column, fading course by course down the wall.
                double edges = Smooth(0, .3, centre) * Smooth(1, .7, centre);
                double column = 1 - .7 * Math.Exp(-Math.Pow((centre - .5) / .2, 2));
                double depth = Math.Pow(1 - course / (double)Courses, 1.6);
                double strength = edges * column * depth;
                if (strength < .03) continue;
                var brick = new Rectangle { Width = BrickWidth, Height = BrickHeight, RadiusX = 4, RadiusY = 4, StrokeThickness = 1.2 };
                Canvas.SetLeft(brick, left); Canvas.SetTop(brick, top);
                wall.Children.Add(brick);
                // A fixed scatter of filled bricks, so the wall reads as built rather than ruled.
                bool filled = (course * 7 + (int)((left - offset) / (BrickWidth + Joint)) * 3) % 11 == 0;
                bricks.Add((brick, strength, filled));
            }
        }
        var layer = new Grid { IsHitTestVisible = false };
        layer.Children.Add(wall);
        var parallax = Fluent.Parallax(source, layer, 56);
        // Fitted to the width the page gives the backdrop, so the softened ends of the wall are always in view.
        // ParallaxView lays its child out at the child's own size: Fluent.Parallax gives it the frame's width, and the
        // layer takes the frame's height plus the shift.
        parallax.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            layer.Height = e.NewSize.Height + 56;
            scale.ScaleX = scale.ScaleY = Math.Clamp(e.NewSize.Width / WallWidth, .3, 1.25);
        };
        parallax.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(parallax, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        void Theme()
        {
            wall.Visibility = BrandHighContrast ? Visibility.Collapsed : Visibility.Visible;
            if (BrandHighContrast) return;
            bool dark = parallax.ActualTheme == ElementTheme.Dark;
            var ink = ThemePalette.Accent(dark);
            double peak = dark ? 40 : 44;
            foreach (var (brick, strength, filled) in bricks)
            {
                brick.Stroke = new SolidColorBrush(Color.FromArgb((byte)(peak * strength), ink.R, ink.G, ink.B));
                brick.Fill = filled ? new SolidColorBrush(Color.FromArgb((byte)(peak * .45 * strength), ink.R, ink.G, ink.B)) : null;
            }
        }
        parallax.Loaded += (_, _) => Theme();
        parallax.ActualThemeChanged += (_, _) => Theme();
        Theme();
        return parallax;
    }

    static double Smooth(double from, double to, double value)
    {
        double t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
