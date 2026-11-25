using System.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mason;

public sealed partial class MainWindow
{
    static SolidColorBrush AccentBrush() => new(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
        ? new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight)
        : Windows.UI.Color.FromArgb(255, 72, 73, 168));
    // Shared presentation only. The caller retains selection, persistence and execution ownership.
    /// <summary>A Fluent table: Caption Strong headers over a divider, 40 px Body rows, system selection visuals.</summary>
    static FrameworkElement ListTable(ListView rows, params (string Label, string Path, double Weight)[] columns)
    {
        double rowViewport=rows.Height;
        if(!double.IsNaN(rowViewport)){
            rows.VerticalAlignment=VerticalAlignment.Top;
            void FitRows(){if(rows.Items.Count>0&&rows.GroupStyle.Count==0)rows.Height=Math.Min(rowViewport,Math.Max(40,rows.Items.Count*44+8));}
            rows.Loaded+=(_,_)=>FitRows();
            rows.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty,(_,_)=>rows.DispatcherQueue.TryEnqueue(FitRows));
        }
        string Definitions() => string.Join("", columns.Select(c => $"<ColumnDefinition Width='{c.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture)}*'/>"));
        string cells = string.Join("", columns.Select((c, i) => $"<TextBlock Grid.Column='{i}' Text='{{Binding {SecurityElement.Escape(c.Path)}}}' Style='{{StaticResource {(i==0?"MasonBody":"MasonBodySecondary")}}}' Padding='12,0' TextTrimming='CharacterEllipsis' TextWrapping='NoWrap' VerticalAlignment='Center'><ToolTipService.ToolTip><ToolTip><TextBlock Text='{{Binding {SecurityElement.Escape(c.Path)}}}' MaxWidth='400' MaxHeight='120' TextWrapping='Wrap' TextTrimming='CharacterEllipsis' MaxLines='6'/></ToolTip></ToolTipService.ToolTip></TextBlock>"));
        rows.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid MinHeight='40'><Grid.ColumnDefinitions>" + Definitions() + "</Grid.ColumnDefinitions>" + cells + "</Grid></DataTemplate>");
        rows.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0)),
            new Setter(Control.MinHeightProperty, 40d),
            new Setter(FrameworkElement.MarginProperty, new Thickness(4,1,4,1)),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
        }};
        var headings = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        foreach (var (column, index) in columns.Select((c, i) => (c, i)))
        {
            headings.ColumnDefinitions.Add(new() { Width = new GridLength(column.Weight, GridUnitType.Star) });
            var label = new TextBlock { Text = column.Label, Style = Token("MasonCaptionStrong"), Margin = new Thickness(12, 10, 12, 9), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(label,column.Label);
            Grid.SetColumn(label, index); headings.Children.Add(label);
        }
        var header = new Border { Child = headings, Style = Token("MasonTableHeader") };
        rows.Padding = new Thickness(0, 4, 0, 4);
        var body = Vertical(header, rows); body.RowSpacing = 0;
        var table = SurfaceCard(body); table.Padding = new Thickness(0);
        return table;
    }

    static Border Badge(string label) => Pill(label, Tone.Neutral);

    static FrameworkElement Toolbar(FrameworkElement leading, params UIElement[] actions)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        leading.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(leading);
        var commands = ScrollTools(Row(actions)); Grid.SetColumn(commands, 1); grid.Children.Add(commands);
        return grid;
    }

    static DropDownButton DrawerButton(string title, FrameworkElement content)
    {
        var toggle = new DropDownButton { Content = title };
        var body = new Grid { Width = 480, MaxHeight = 650 };
        body.Children.Add(ScrollPage(content));
        var flyout = Fluent.Acrylic(new Flyout { Content = body, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle=new Style(typeof(FlyoutPresenter)){Setters={new Setter(FrameworkElement.MaxWidthProperty,560d)}} });
        flyout.Opening+=(_,_)=>body.Width=Math.Min(480,Math.Max(280,(toggle.XamlRoot?.Size.Width??544)-64));toggle.Flyout=flyout;
        AutomationProperties.SetName(toggle, title); return toggle;
    }
}
