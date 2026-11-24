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
        public string Name=>NewSession?"New session":Session?.Title??Project.Name;
        public Visibility QualifierVisibility => string.IsNullOrEmpty(Label) ? Visibility.Collapsed : Visibility.Visible;
        public string Glyph=>NewSession?"\uE710":Session==null?"\uE8B7":"\uE8BD";
        public double TextSize=>Session==null&&!NewSession?14:13;
        public Windows.UI.Text.FontWeight Weight=>Session==null&&!NewSession?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal;
        public string Description=>NewSession?"New session in "+Project.Name:Session==null?Project.Workspace:Session.Title+"\n"+Project.Workspace+"\n"+Session.State;
        public override string ToString()=>string.IsNullOrEmpty(Label) ? Name : Name+" · "+Label;
    }
    static string ProjectQualifier(CodeProject project, IEnumerable<CodeProject> projects)
    {
        var others = projects.Where(p => p.Id != project.Id && p.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase))
            .Select(p => System.IO.Path.GetDirectoryName(p.Workspace) ?? p.Workspace).ToArray();
        if (others.Length == 0) return "";
        var parent = System.IO.Path.GetDirectoryName(project.Workspace) ?? project.Workspace;
        var parts = parent.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int n = 1; n <= parts.Length; n++)
        {
            var suffix = string.Join("\\", parts.TakeLast(n));
            if (!others.Any(path => path.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("\\" + suffix, StringComparison.OrdinalIgnoreCase))) return suffix;
        }
        return parent;
    }

    static string ProjectLabel(CodeProject project, IEnumerable<CodeProject> projects)
    {
        var qualifier = ProjectQualifier(project, projects);
        return qualifier.Length == 0 ? project.Name : project.Name + " · " + qualifier;
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
                <StackPanel Grid.Column="1" VerticalAlignment="Center"><TextBlock Text="{Binding Content.Name}" FontSize="{Binding Content.TextSize}" FontWeight="{Binding Content.Weight}" TextTrimming="CharacterEllipsis" MaxLines="1" VerticalAlignment="Center"/>
                <TextBlock Text="{Binding Content.Label}" Visibility="{Binding Content.QualifierVisibility}" FontSize="12" TextWrapping="Wrap" Opacity=".8"/></StackPanel>
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
            var group=new TreeViewNode{Content=new CodeProjectItem(project,Label:ProjectQualifier(project,store.Projects)),IsExpanded=project.Expanded};
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
