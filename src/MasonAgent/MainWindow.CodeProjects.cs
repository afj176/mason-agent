using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mason;

public sealed partial class MainWindow
{
    sealed record CodeProjectItem(CodeProject Project,CodeSession? Session=null,bool NewSession=false,string? Label=null)
    {
        public string Name=>NewSession?"New session":Session?.Title??Label??Project.Name;
        public string Glyph=>NewSession?"\uE710":Session==null?"\uE8B7":"\uE8BD";
        public double TextSize=>Session==null&&!NewSession?14:13;
        public Windows.UI.Text.FontWeight Weight=>Session==null&&!NewSession?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal;
        public string Description=>NewSession?"New session in "+Project.Name:Session==null?Project.Workspace:Session.Title+"\n"+Project.Workspace+"\n"+Session.State;
        public override string ToString()=>Name;
    }
    static TreeView CodeProjectNavigation()
    {
        var tree=new TreeView{SelectionMode=TreeViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        AutomationProperties.SetName(tree,"Projects and coding sessions");
        tree.ItemTemplate=(DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid ColumnSpacing="8" Padding="0,3" ToolTipService.ToolTip="{Binding Content.Description}">
                <Grid.ColumnDefinitions><ColumnDefinition Width="22"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <FontIcon Glyph="{Binding Content.Glyph}" FontSize="14" Opacity=".8"/>
                <TextBlock Grid.Column="1" Text="{Binding Content.Name}" FontSize="{Binding Content.TextSize}" FontWeight="{Binding Content.Weight}" TextTrimming="CharacterEllipsis" MaxLines="1" VerticalAlignment="Center"/>
              </Grid>
            </DataTemplate>
            """);
        return tree;
    }
    static void PopulateCodeProjects(TreeView tree,CodeStore store,CodeSession? selected)
    {
        tree.SelectedNode=null;tree.RootNodes.Clear();TreeViewNode? selection=null;
        foreach(var project in store.Projects)
        {
            bool ambiguous=store.Projects.Count(p=>p.Name.Equals(project.Name,StringComparison.OrdinalIgnoreCase))>1;
            string label=ambiguous?project.Name+" · "+System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(project.Workspace)):project.Name;
            var group=new TreeViewNode{Content=new CodeProjectItem(project,Label:label),IsExpanded=project.Expanded};
            if(selected==null&&store.SelectedProject?.Id==project.Id)selection=group;
            foreach(var session in store.SessionsFor(project.Id))
            {
                var node=new TreeViewNode{Content=new CodeProjectItem(project,session)};
                group.Children.Add(node);if(session==selected)selection=node;
            }
            group.Children.Add(new(){Content=new CodeProjectItem(project,NewSession:true)});
            tree.RootNodes.Add(group);
        }
        tree.SelectedNode=selection;
        if(selection!=null)tree.DispatcherQueue.TryEnqueue(()=>
        {
            if(tree.SelectedNode!=selection)return;
            // Selection can be below several expanded projects. Bring the
            // actual native item into its own scroll viewport after layout.
            tree.UpdateLayout();
            ListView? Host(DependencyObject parent)
            {
                if(parent is ListView list)return list;
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)if(Host(VisualTreeHelper.GetChild(parent,i)) is {} found)return found;
                return null;
            }
            Host(tree)?.ScrollIntoView(selection);
        });
    }
}
