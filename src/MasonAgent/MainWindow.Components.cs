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
    static FrameworkElement ListTable(ListView rows, params (string Label, string Path, double Weight)[] columns)
    {
        double rowViewport=rows.Height;
        if(!double.IsNaN(rowViewport)){
            rows.VerticalAlignment=VerticalAlignment.Top;
            void FitRows(){if(rows.Items.Count>0&&rows.GroupStyle.Count==0)rows.Height=Math.Min(rowViewport,Math.Max(44,rows.Items.Count*44));}
            rows.Loaded+=(_,_)=>FitRows();
            rows.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty,(_,_)=>rows.DispatcherQueue.TryEnqueue(FitRows));
        }
        string Definitions() => string.Join("", columns.Select(c => $"<ColumnDefinition Width='{c.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture)}*'/>"));
        string cells = string.Join("", columns.Select((c, i) => $"<TextBlock Grid.Column='{i}' Text='{{Binding {SecurityElement.Escape(c.Path)}}}' FontSize='13' Padding='12,6' TextTrimming='CharacterEllipsis' TextWrapping='Wrap' MaxLines='2' VerticalAlignment='Center'><ToolTipService.ToolTip><ToolTip><TextBlock Text='{{Binding {SecurityElement.Escape(c.Path)}}}' MaxWidth='400' MaxHeight='120' TextWrapping='Wrap' TextTrimming='CharacterEllipsis' MaxLines='6'/></ToolTip></ToolTipService.ToolTip></TextBlock>"));
        rows.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid MinHeight='44'><Grid.ColumnDefinitions>" + Definitions() + "</Grid.ColumnDefinitions>" + cells + "</Grid></DataTemplate>");
        rows.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0)),
            new Setter(Control.MinHeightProperty, 44d),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
        }};
        void SelectionColors(){
            string[] keys=["ListViewItemBackgroundSelected","ListViewItemBackgroundSelectedPointerOver","ListViewItemBackgroundSelectedPressed"];
            if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast){foreach(var key in keys.Concat(new[]{"ListViewItemCheckBoxSelectedBrush","ListViewItemCheckBoxSelectedPointerOverBrush","ListViewItemCheckBoxSelectedPressedBrush","ListViewItemCheckBrush","ListViewItemCheckPressedBrush"}))rows.Resources.Remove(key);return;}
            foreach(var key in keys)rows.Resources[key]=new SolidColorBrush(rows.ActualTheme==ElementTheme.Dark?Windows.UI.Color.FromArgb(46,195,194,255):Windows.UI.Color.FromArgb(30,72,73,168));
            if(rows.SelectionMode==ListViewSelectionMode.Multiple){
                foreach(var key in new[]{"ListViewItemCheckBoxSelectedBrush","ListViewItemCheckBoxSelectedPointerOverBrush","ListViewItemCheckBoxSelectedPressedBrush"})rows.Resources[key]=new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                foreach(var key in new[]{"ListViewItemCheckBrush","ListViewItemCheckPressedBrush"})rows.Resources[key]=new SolidColorBrush(rows.ActualTheme==ElementTheme.Dark?Microsoft.UI.Colors.White:Windows.UI.Color.FromArgb(255,24,34,54));
            }
        }
        rows.Loaded+=(_,_)=>SelectionColors();rows.ActualThemeChanged+=(_,_)=>SelectionColors();
        var headings = new Grid { MinHeight = 34 };
        // The faint heading tint is brand chrome; high contrast keeps the system window color.
        void HeadingFill() => headings.Background = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? null : new SolidColorBrush(Windows.UI.Color.FromArgb(22, 119, 125, 142));
        HeadingFill(); headings.Loaded += (_, _) => HeadingFill(); headings.ActualThemeChanged += (_, _) => HeadingFill();
        foreach (var (column, index) in columns.Select((c, i) => (c, i)))
        {
            headings.ColumnDefinitions.Add(new() { Width = new GridLength(column.Weight, GridUnitType.Star) });
            var label = Text(column.Label, 11); label.Margin = new Thickness(12, 8, 12, 8);
            label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; label.TextWrapping=TextWrapping.NoWrap; label.TextTrimming=TextTrimming.CharacterEllipsis; ToolTipService.SetToolTip(label,column.Label);
            Grid.SetColumn(label, index); headings.Children.Add(label);
        }
        var table = SurfaceCard(Vertical(headings, rows)); table.Padding = new Thickness(0);
        return table;
    }

    static Border Badge(string label)
    {
        var badge = SurfaceCard(Text(label, 11)); badge.Padding = new Thickness(8, 3, 8, 3);
        badge.CornerRadius = new CornerRadius(10);badge.VerticalAlignment=VerticalAlignment.Center; return badge;
    }

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
        var toggle = new DropDownButton { Content = title, Padding = new Thickness(12, 6, 12, 6) };
        var body = new Grid { Width = 480, MaxHeight = 650 };
        body.Children.Add(ScrollPage(content));
        var flyout = Fluent.Acrylic(new Flyout { Content = body, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle=new Style(typeof(FlyoutPresenter)){Setters={new Setter(FrameworkElement.MaxWidthProperty,560d)}} });
        flyout.Opening+=(_,_)=>body.Width=Math.Min(480,Math.Max(280,(toggle.XamlRoot?.Size.Width??544)-64));toggle.Flyout=flyout;
        AutomationProperties.SetName(toggle, title); return toggle;
    }
}
