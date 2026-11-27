using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace Mason;

// Page modes are a native menu, not document tabs. All content stays mounted so
// changing modes preserves editors, scroll position and in-flight work.
public sealed record ViewOption { public string Header { get; init; } = "View"; public FrameworkElement? Content { get; init; } }

public sealed class ViewSwitcher : UserControl
{
    readonly DropDownButton selector = new() { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14,9,14,9) };
    readonly MenuFlyout menu = Fluent.Acrylic(new MenuFlyout { Placement=Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft });
    readonly Grid host = new();
    readonly StackPanel segments = new() { Orientation=Orientation.Horizontal, Spacing=2 };
    readonly ScrollViewer segmentScroll;
    bool useSegments;
    bool selectorVisible=true;
    public bool SelectorVisible {get=>selectorVisible;set{selectorVisible=value;Render();}}
    public bool UseSegments { get => useSegments; set { useSegments=value; Render(); } }
    int selected = -1;
    public ObservableCollection<ViewOption> Items { get; } = [];
    public event EventHandler? SelectionChanged;
    public int SelectedIndex
    {
        get => selected;
        set
        {
            int next = Items.Count == 0 ? -1 : Math.Clamp(value,0,Items.Count-1);
            if (next == selected) return;
            selected = next; Render(); SelectionChanged?.Invoke(this,EventArgs.Empty);
        }
    }
    public ViewSwitcher()
    {
        var layout=new Grid {RowSpacing=8}; Content=layout;
        layout.RowDefinitions.Add(new() { Height=GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height=new GridLength(1,GridUnitType.Star) });
        selector.Flyout=menu; layout.Children.Add(selector); Grid.SetRow(host,1); layout.Children.Add(host);
        segmentScroll=new ScrollViewer {Content=segments,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,Visibility=Visibility.Collapsed};
        layout.Children.Add(segmentScroll);
        ToolTipService.SetToolTip(selector,"Change view");
        Items.CollectionChanged+=(_,_)=>
        {
            host.Children.Clear();
            foreach(var item in Items)
                if(item.Content is FrameworkElement content)host.Children.Add(content);
            if(selected<0&&Items.Count>0)SelectedIndex=0;
            else { if(selected>=Items.Count)SelectedIndex=Items.Count-1; Render(); }
            host.Visibility=host.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
            layout.RowDefinitions[1].Height=host.Children.Count==0?GridLength.Auto:new GridLength(1,GridUnitType.Star);
        };
        foreach(bool reverse in new[]{false,true})
        {
            var shortcut=new KeyboardAccelerator {Key=VirtualKey.Tab,Modifiers=VirtualKeyModifiers.Control|(reverse?VirtualKeyModifiers.Shift:VirtualKeyModifiers.None)};
            shortcut.Invoked+=(_,e)=>{if(Items.Count>1){SelectedIndex=(selected+(reverse?Items.Count-1:1))%Items.Count;e.Handled=true;FocusSelector();}};
            KeyboardAccelerators.Add(shortcut);
        }
    }
    public void FocusSelector(){if(useSegments&&selected>=0&&selected<segments.Children.Count)((Control)segments.Children[selected]).Focus(FocusState.Keyboard);else if(selectorVisible)selector.Focus(FocusState.Keyboard);}
    void Render()
    {
        selector.Visibility=!selectorVisible||useSegments?Visibility.Collapsed:Visibility.Visible;
        segmentScroll.Visibility=selectorVisible&&useSegments?Visibility.Visible:Visibility.Collapsed;
        segments.Children.Clear();
        menu.Items.Clear();
        for(int i=0;i<Items.Count;i++)
        {
            var item=Items[i]; int index=i;
            string label=Convert.ToString(item.Header)??"View";
            var option=new RadioMenuFlyoutItem { Text=label,IsChecked=i==selected };
            option.Click+=(_,_)=>SelectedIndex=index;menu.Items.Add(option);
            var segment=new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton {Content=label,IsChecked=i==selected,Padding=new Thickness(12,5,12,5),CornerRadius=new CornerRadius(3),FontSize=12};
            void Colors(){
                string[] backgrounds=["ToggleButtonBackground","ToggleButtonBackgroundPointerOver","ToggleButtonBackgroundPressed","ToggleButtonBackgroundChecked","ToggleButtonBackgroundCheckedPointerOver","ToggleButtonBackgroundCheckedPressed"];
                if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast){foreach(var key in backgrounds)segment.Resources.Remove(key);segment.ClearValue(Control.BackgroundProperty);segment.ClearValue(Control.ForegroundProperty);segment.ClearValue(Control.BorderBrushProperty);return;}
                bool dark=segment.ActualTheme==ElementTheme.Dark;
                var accent=new Microsoft.UI.Xaml.Media.SolidColorBrush(ThemePalette.Accent(dark));
                var clear=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                foreach(var key in backgrounds)segment.Resources[key]=clear;
                segment.Background=clear;segment.BorderBrush=index==selected?accent:clear;segment.BorderThickness=new Thickness(0,0,0,2);segment.CornerRadius=new CornerRadius(0);
                segment.Foreground=index==selected?accent:new Microsoft.UI.Xaml.Media.SolidColorBrush(ThemePalette.Ink(dark));
                segment.Resources["ToggleButtonForegroundChecked"]=segment.Foreground;
                segment.Resources["ToggleButtonForegroundCheckedPointerOver"]=segment.Foreground;
            }
            segment.Loaded+=(_,_)=>Colors();segment.ActualThemeChanged+=(_,_)=>Colors();Colors();
            AutomationProperties.SetName(segment,label+" view");
            segment.Click+=(_,_)=>{SelectedIndex=index;FocusSelector();};segments.Children.Add(segment);
            if(item.Content is FrameworkElement content)content.Visibility=i==selected?Visibility.Visible:Visibility.Collapsed;
        }
        string title=selected>=0?Convert.ToString(Items[selected].Header)??"View":"Choose view";
        selector.Content=new TextBlock {Text=title,FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=300};
        AutomationProperties.SetName(selector,title+" · Change view");
        selector.IsEnabled=Items.Count>1;
    }
}
