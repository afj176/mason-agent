using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;

namespace Mason;

public sealed partial class MainWindow
{
    Action<int>? selectResources;
    Action<int>? selectModelView;
    string currentRoute = "Home";
    Func<bool>? modelScanBusy;
    Action? modelScanCancel;
    static Border SurfaceCard(FrameworkElement child) => new() { Child = child, Style = Token("MasonCard") };
    static ScrollViewer ScrollPage(FrameworkElement child)=>new() { Content=child,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
    static FrameworkElement InspectorLayout(FrameworkElement main,FrameworkElement inspector,string title="Inspector",Action<bool>? focusChanged=null,Action<Action<string>>? titleReady=null)
    {
        var grid=new Grid {ColumnSpacing=12,RowSpacing=10};
        grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(400)});
        grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var caption=Text(title,20);titleReady?.Invoke(next=>caption.Text=next);
        var card=SurfaceCard(Vertical(caption,inspector));card.Padding=new Thickness(16,12,16,12);
        grid.Children.Add(main);grid.Children.Add(card);Grid.SetColumn(card,1);
        bool compact=false,focused=false;
        var toggle=Button("Show "+title,(_,_)=>{});Grid.SetRow(toggle,1);Grid.SetColumnSpan(toggle,2);grid.Children.Add(toggle);
        void Arrange(){compact=grid.ActualWidth<880;grid.ColumnDefinitions[1].Width=compact?new GridLength(0):new GridLength(Math.Clamp(grid.ActualWidth*.30,340,410));Grid.SetColumn(card,compact?0:1);card.Visibility=!compact||focused?Visibility.Visible:Visibility.Collapsed;main.Visibility=compact&&focused?Visibility.Collapsed:Visibility.Visible;toggle.Visibility=compact?Visibility.Visible:Visibility.Collapsed;toggle.Content=focused?"Back to work":"Show "+title;focusChanged?.Invoke(compact&&focused);}
        toggle.Click+=(_,_)=>{focused=!focused;Arrange();};grid.SizeChanged+=(_,_)=>Arrange();
        return grid;
    }
    static Grid FormGrid(params FrameworkElement[] elements)
        => FormGrid(680,elements);
    static Grid FormGrid(double narrowWidth,params FrameworkElement[] elements)
    {
        var grid=new Grid{ColumnSpacing=16,RowSpacing=16};grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        foreach(var element in elements)grid.Children.Add(element);
        void Arrange(){bool narrow=grid.ActualWidth<narrowWidth;grid.RowDefinitions.Clear();for(int i=0;i<(narrow?elements.Length:(elements.Length+1)/2);i++)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});for(int i=0;i<elements.Length;i++){Grid.SetRow(elements[i],narrow?i:i/2);Grid.SetColumn(elements[i],narrow?0:i%2);Grid.SetColumnSpan(elements[i],narrow?2:1);}}
        grid.SizeChanged+=(_,_)=>Arrange();Arrange();return grid;
    }
    static ViewSwitcher ModeSwitcher(params (string Label,FrameworkElement Page)[] items)
    {
        var views=new ViewSwitcher {UseSegments=true};foreach(var item in items)views.Items.Add(new(){Header=item.Label,Content=item.Page});return views;
    }
    static (FrameworkElement View,Action<int> Set,Action<int,string> Label) StageRail(params string[] labels)
    {
        var rail=new Grid{ColumnSpacing=10};var entries=new List<(Border Dot,TextBlock Number,TextBlock Label)>();var links=new List<Border>();
        foreach(var (label,index) in labels.Select((label,index)=>(label,index))){
            if(index>0){rail.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});var line=new Border{Height=1,MinWidth=18,Background=new SolidColorBrush(Windows.UI.Color.FromArgb(80,119,125,142)),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(line,rail.ColumnDefinitions.Count-1);rail.Children.Add(line);links.Add(line);}
            rail.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var number=Text((index+1).ToString(),12);number.HorizontalAlignment=HorizontalAlignment.Center;number.VerticalAlignment=VerticalAlignment.Center;var dot=SurfaceCard(number);dot.Padding=new Thickness(0);dot.Width=dot.Height=28;dot.CornerRadius=new CornerRadius(14);var text=Text(label,12);text.VerticalAlignment=VerticalAlignment.Center;entries.Add((dot,number,text));var item=Row(dot,text);Grid.SetColumn(item,rail.ColumnDefinitions.Count-1);rail.Children.Add(item);
        }
        void Set(int current)
        {
            // In high contrast the tinted fills and fixed ink would ignore the user's palette; use system colors.
            bool high=new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;var system=new Windows.UI.ViewManagement.UISettings();
            SolidColorBrush Muted(byte alpha)=>new(high?system.UIElementColor(Windows.UI.ViewManagement.UIElementType.GrayText):Windows.UI.Color.FromArgb(alpha,119,125,142));
            foreach(var (entry,index) in entries.Select((entry,index)=>(entry,index))){entry.Number.Text=index<current?"✓":(index+1).ToString();entry.Label.FontWeight=index==current?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal;entry.Dot.Opacity=index>current&&!high?.55:1;entry.Dot.BorderThickness=new Thickness(index==current?2:1);entry.Dot.Background=index<=current?AccentBrush():high?new SolidColorBrush(system.UIElementColor(Windows.UI.ViewManagement.UIElementType.Window)):Muted(30);entry.Number.Foreground=new SolidColorBrush(index<=current?(high?system.UIElementColor(Windows.UI.ViewManagement.UIElementType.HighlightText):Microsoft.UI.Colors.White):high?system.UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText):entry.Dot.ActualTheme==ElementTheme.Dark?Microsoft.UI.Colors.White:Windows.UI.Color.FromArgb(255,24,34,54));AutomationProperties.SetName(entry.Dot,labels[index]+(index<current?" · completed stage":index==current?" · current stage":" · upcoming"));}
            foreach(var (line,i) in links.Select((line,i)=>(line,i)))line.Background=i<current?AccentBrush():Muted(80);
        }
        rail.SizeChanged+=(_,_)=>{foreach(var entry in entries)entry.Label.Visibility=rail.ActualWidth<labels.Length*105?Visibility.Collapsed:Visibility.Visible;};
        var frame=SurfaceCard(rail);frame.Padding=new Thickness(12,10,12,10);Set(0);return(frame,Set,(index,label)=>entries[index].Label.Text=label);
    }
}
