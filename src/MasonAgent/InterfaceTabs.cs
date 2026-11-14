using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Mason;

public enum TabPresentation { Page, Document, Result }

// Native TabView owns selection, accessibility, overflow, closing and drag/reorder.
// These passive surfaces change only how its header is drawn.
public static class InterfaceTabs
{
    public static TabView Apply(TabView tabs,TabPresentation presentation=TabPresentation.Page)
    { _=new Presentation(tabs,presentation);return tabs; }

    sealed class ItemSurface
    {
        public Border? Surface;
        public Grid? Root;
        public bool? Selected;
        public RoutedEventHandler? Loaded;
    }
    sealed class Presentation
    {
        readonly TabView tabs;
        readonly TabPresentation kind;
        readonly UISettings settings=new();
        readonly Dictionary<TabViewItem,ItemSurface> items=[];
        readonly SolidColorBrush railBrush=new(),selectedBrush=new();
        Grid? header;Border? rail;bool subscribed,queued;
        bool Contrast=>new AccessibilitySettings().HighContrast;
        static readonly string[] Tokens=["TabViewItemHeaderBackground","TabViewItemHeaderBackgroundSelected","TabViewItemHeaderBackgroundPointerOver","TabViewItemHeaderBackgroundPressed","TabViewSelectedItemBorderBrush","TabViewItemHeaderForegroundSelected","TabViewSelectedItemBorderThickness","TabViewSelectedItemHeaderMargin"];

        public Presentation(TabView tabs,TabPresentation kind)
        {
            this.tabs=tabs;this.kind=kind;
            if(kind!=TabPresentation.Document)tabs.TabWidthMode=TabViewWidthMode.SizeToContent;
            tabs.Loaded+=OnLoaded;tabs.Unloaded+=OnUnloaded;
            tabs.ActualThemeChanged+=(_,_)=>Refresh(false);
            tabs.TabItemsChanged+=(_,_)=>QueueRefresh();
            tabs.SelectionChanged+=(_,_)=>Refresh(true);
            tabs.LayoutUpdated+=(_,_)=>LayoutRail();
        }
        void OnLoaded(object sender,RoutedEventArgs e)
        {
            if(!subscribed){settings.ColorValuesChanged+=ColorsChanged;subscribed=true;}
            Refresh(false);QueueRefresh();
        }
        void OnUnloaded(object sender,RoutedEventArgs e)
        {if(subscribed){settings.ColorValuesChanged-=ColorsChanged;subscribed=false;}}
        void ColorsChanged(UISettings sender,object args)=>QueueRefresh();
        void QueueRefresh()
        {
            if(queued)return;queued=true;
            tabs.DispatcherQueue.TryEnqueue(()=>{queued=false;if(tabs.IsLoaded)Refresh(false);});
        }
        void Token(string name,Color color)
        {
            // Mounted visual states retain brush references across theme changes.
            if(tabs.Resources.TryGetValue(name,out var value)&&value is SolidColorBrush old)old.Color=color;
            else tabs.Resources[name]=new SolidColorBrush(color);
        }
        static Color C(byte r,byte g,byte b)=>Color.FromArgb(255,r,g,b);
        void Refresh(bool animate)
        {
            if(!tabs.IsLoaded)return;
            bool dark=tabs.ActualTheme==ElementTheme.Dark,contrast=Contrast;
            var transparent=Color.FromArgb(0,0,0,0);
            var accent=dark?C(195,194,255):C(72,73,168);
            railBrush.Color=dark?C(36,40,62):C(248,246,242);
            selectedBrush.Color=kind==TabPresentation.Result?accent:dark?C(36,40,62):C(255,255,255);
            if(contrast)
            {
                foreach(var token in Tokens)tabs.Resources.Remove(token);
                tabs.ClearValue(Control.BackgroundProperty);
            }
            else
            {
                tabs.Background=new SolidColorBrush(transparent);
                Token("TabViewItemHeaderBackground",transparent);
                Token("TabViewItemHeaderBackgroundSelected",kind==TabPresentation.Document?(dark?C(36,40,62):C(255,255,255)):transparent);
                Token("TabViewItemHeaderBackgroundPointerOver",dark?Color.FromArgb(50,195,194,255):Color.FromArgb(25,72,73,168));
                Token("TabViewItemHeaderBackgroundPressed",dark?C(36,40,62):C(248,246,242));
                Token("TabViewSelectedItemBorderBrush",kind==TabPresentation.Document?(dark?C(195,194,255):C(72,73,168)):transparent);
                Token("TabViewItemHeaderForegroundSelected",kind==TabPresentation.Result?accent:dark?C(248,246,242):C(32,38,60));
                tabs.Resources["TabViewSelectedItemBorderThickness"]=new Thickness(kind==TabPresentation.Document?1:0);
                tabs.Resources["TabViewSelectedItemHeaderMargin"]=new Thickness(0);
            }
            header??=Find<Grid>(tabs,"TabContainerGrid");
            if(header!=null&&kind==TabPresentation.Page&&rail==null)
            {
                rail=new Border{Background=railBrush,CornerRadius=new CornerRadius(12),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
                Grid.SetColumnSpan(rail,4);header.Children.Insert(0,rail);
            }
            if(rail!=null)rail.Visibility=contrast?Visibility.Collapsed:Visibility.Visible;
            if(header!=null)
                foreach(var name in new[]{"LeftBottomBorderLine","RightBottomBorderLine"})
                    if(Find<Border>(header,name) is {} line)line.Opacity=contrast||kind==TabPresentation.Document?1:0;
            var current=tabs.TabItems.OfType<TabViewItem>().ToHashSet();
            foreach(var removed in items.Keys.Where(x=>!current.Contains(x)).ToArray())
            {if(items[removed].Loaded is {} handler)removed.Loaded-=handler;items.Remove(removed);}
            foreach(var item in current)
            {
                if(!items.TryGetValue(item,out var state))
                {
                    state=new();items[item]=state;state.Loaded=(_,_)=>QueueRefresh();item.Loaded+=state.Loaded;
                }
                item.MinHeight=kind==TabPresentation.Document?42:44;
                item.Margin=kind switch{TabPresentation.Page=>new Thickness(3,1,3,1),TabPresentation.Document=>new Thickness(2,5,2,0),_=>new Thickness(0,1,8,1)};
                item.BorderThickness=new Thickness(0);
                if(!item.IsLoaded)continue;
                state.Root??=Find<Grid>(item,"LayoutRoot");
                if(kind!=TabPresentation.Document)
                {
                    foreach(var name in new[]{"SelectedBackgroundPath","LeftRadiusRenderArc","RightRadiusRenderArc","BottomBorderLine","TabSeparator"})
                        if(Find<UIElement>(item,name) is {} part)part.Opacity=contrast?1:0;
                    if(Find<Grid>(item,"TabContainer") is {} container)container.CornerRadius=new CornerRadius(8);
                    if(state.Root!=null&&state.Surface==null)
                    {
                        state.Surface=new Border{Background=selectedBrush,IsHitTestVisible=false,CornerRadius=new CornerRadius(kind==TabPresentation.Result?1:8),Margin=kind==TabPresentation.Result?new Thickness(12,0,12,0):new Thickness(1,4,1,4),VerticalAlignment=kind==TabPresentation.Result?VerticalAlignment.Bottom:VerticalAlignment.Stretch};
                        if(kind==TabPresentation.Result)state.Surface.Height=2;
                        Grid.SetColumnSpan(state.Surface,3);state.Root.Children.Insert(0,state.Surface);
                    }
                    if(state.Surface!=null)
                    {
                        state.Surface.Visibility=contrast?Visibility.Collapsed:Visibility.Visible;
                        bool selected=ReferenceEquals(tabs.SelectedItem,item);
                        if(state.Selected!=selected){Transition(state.Surface,selected,animate&&state.Selected.HasValue&&!contrast);state.Selected=selected;}
                    }
                }
            }
            LayoutRail();
        }
        void Transition(Border surface,bool selected,bool animate)
        {
            var visual=ElementCompositionPreview.GetElementVisual(surface);
            visual.StopAnimation("Opacity");visual.StopAnimation("Scale.X");
            float opacity=selected?1:0;
            if(!animate||!Fluent.MotionOn){visual.Opacity=opacity;visual.Scale=Vector3.One;return;}
            // Keep an explicit final base value and both keyframes, so revisiting a
            // tab animates consistently after a previous animation has completed.
            visual.Opacity=opacity;visual.Scale=Vector3.One;
            var compositor=visual.Compositor;
            var easing=compositor.CreateCubicBezierEasingFunction(new(.2f,0),new(0,1));
            var fade=compositor.CreateScalarKeyFrameAnimation();fade.Duration=TimeSpan.FromMilliseconds(160);fade.InsertKeyFrame(0,selected?0:1);fade.InsertKeyFrame(1,opacity,easing);visual.StartAnimation("Opacity",fade);
            if(selected&&kind==TabPresentation.Result)
            {
                visual.CenterPoint=new Vector3((float)surface.ActualWidth/2,0,0);
                var grow=compositor.CreateScalarKeyFrameAnimation();grow.Duration=TimeSpan.FromMilliseconds(180);grow.InsertKeyFrame(0,.72f);grow.InsertKeyFrame(1,1,easing);visual.StartAnimation("Scale.X",grow);
            }
        }
        void LayoutRail()
        {
            if(rail==null||header==null||!tabs.IsLoaded)return;
            double end=0,height=0;
            foreach(var item in items.Keys.Where(x=>x.IsLoaded&&x.ActualWidth>0))
            {
                var position=item.TransformToVisual(header).TransformPoint(new Windows.Foundation.Point());
                end=Math.Max(end,position.X+item.ActualWidth+3);height=Math.Max(height,item.ActualHeight+2);
            }
            double width=Math.Min(header.ActualWidth,Math.Max(0,end));
            if(Math.Abs(rail.Width-width)>.5||double.IsNaN(rail.Width))rail.Width=width;
            if(Math.Abs(rail.Height-height)>.5||double.IsNaN(rail.Height))rail.Height=height;
        }
    }
    static T? Find<T>(DependencyObject parent,string name) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T result&&child is FrameworkElement{ Name:var found }&&found==name)return result;
            if(Find<T>(child,name) is {} descendant)return descendant;
        }
        return null;
    }
}
