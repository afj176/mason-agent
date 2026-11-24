using System.IO;
using System.Text.Json;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Mason;

public sealed partial class MainWindow
{
    FrameworkElement HarnessSettings(CodeSession session,CodeViewerEntry entry)
    {
        var scope=new CodeWorkspace(session.Workspace);
        var current=CodeProjectPolicy.Load(Store.Root,scope.Root,true);
        var name=new TextBox{Header="Project policy",Text=current?.Name??"",MaxLength=80,PlaceholderText="Policy name"};
        var disabled=new TextBox{Header="Disabled model tools",Text=string.Join(", ",current?.DisabledTools??[]),PlaceholderText="Comma-separated tool names"};
        var workflows=new TextBox{Header="Allowed model workflows",Text=current?.Workflows==null?"*":string.Join(", ",current.Workflows),PlaceholderText="* for all, empty for none"};
        var expiry=new TextBox{Header="Expiry (optional, UTC)",Text=current?.Expires?.UtcDateTime.ToString("O")??""};
        var hooks=new CheckBox{Content="Run project workflow events in Auto",IsChecked=current?.EnableHooks==true};
        var status=CodeNote("");
        string[] List(string text)=>text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToArray();
        var apply=Button("Apply policy",(_,_)=>{
            try {
                DateTimeOffset? until=null;if(expiry.Text.Trim().Length>0)until=DateTimeOffset.Parse(expiry.Text,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal);
                var p=new CodeProjectPolicy(name.Text.Trim(),scope.Root,List(disabled.Text),workflows.Text.Trim()=="*"?null:List(workflows.Text),until,hooks.IsChecked==true);
                CodeProjectPolicy.Set(Store.Root,scope.Root,p);status.Text="Policy saved for "+scope.Root;
            }catch(Exception e){status.Text=e.Message;}
        });
        var revoke=CodeLink("Revoke policy",(_,_)=>{try{CodeProjectPolicy.Set(Store.Root,scope.Root,null);name.Text=disabled.Text=expiry.Text="";workflows.Text="*";hooks.IsChecked=false;status.Text="Policy revoked.";}catch(Exception e){status.Text=e.Message;}});
        var bundles=new ListView{MaxHeight=200,SelectionMode=ListViewSelectionMode.Single};
        var info=new TextBox{IsReadOnly=true,MaxHeight=220,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap};
        InstalledBundle[] packages=[];
        void Refresh(){packages=CodeBundles.List(Store.Root);bundles.ItemsSource=packages.Select(b=>b.Package.Name+" "+b.Package.Version+" · "+(b.Enabled?"Enabled":"Disabled")).ToArray();info.Text="";}
        bundles.SelectionChanged+=(_,_)=>{if(bundles.SelectedIndex>=0 && bundles.SelectedIndex<packages.Length)info.Text=JsonSerializer.Serialize(packages[bundles.SelectedIndex].Package,new JsonSerializerOptions{WriteIndented=true});};
        var install=Button("Inspect package…",async (_,_)=>{
            try {
                var picker=new FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".json");var f=await picker.PickSingleFileAsync();if(f==null)return;
                if(new FileInfo(f.Path).Length>512000)throw new InvalidDataException("Extension package is too large.");
                string contents=await File.ReadAllTextAsync(f.Path,entry.Cancellation.Token);
                var p=CodeBundles.Inspect(contents);
                var dlg=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title=p.Name+" "+p.Version,
                    Content=CodeScroll(CodeNote(JsonSerializer.Serialize(p,new JsonSerializerOptions{WriteIndented=true}))),PrimaryButtonText="Install disabled",CloseButtonText="Cancel"};
                if(await dlg.ShowAsync()==ContentDialogResult.Primary){CodeBundles.Save(Store.Root,p,false);Refresh();status.Text="Installed "+p.Name+" "+p.Version;}
            }catch(Exception e){status.Text=e.Message;}
        });
        var toggle=Button("Enable / disable",(_,_)=>{try{if(bundles.SelectedIndex<0)return;var b=packages[bundles.SelectedIndex];CodeBundles.Save(Store.Root,b.Package,!b.Enabled);Refresh();}catch(Exception e){status.Text=e.Message;}});
        var remove=CodeLink("Uninstall",async (_,_)=>{
            try{
                if(bundles.SelectedIndex<0)return;var b=packages[bundles.SelectedIndex];
                var dlg=new ContentDialog{Title="Uninstall "+b.Package.Name+"?",XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,PrimaryButtonText="Uninstall",CloseButtonText="Cancel"};
                if(await dlg.ShowAsync()==ContentDialogResult.Primary){CodeBundles.Remove(Store.Root,b.Package.Name);Refresh();}
            }catch(Exception e){status.Text=e.Message;}
        });
        var editor=CodeLink("Open bundled VS Code extension",async (_,_)=>{
            try{
                var p=Path.Combine(AppContext.BaseDirectory,"integrations","mason-workspace.vsix");
                if(!File.Exists(p))throw new FileNotFoundException("The installer includes the editor extension under integrations.");
                var folder=await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(p)!);
                var options=new Windows.System.FolderLauncherOptions();options.ItemsToSelect.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(p));
                await Windows.System.Launcher.LaunchFolderAsync(folder,options);status.Text="In VS Code, choose Extensions: Install from VSIX and select mason-workspace.vsix.";
            }catch(Exception e){status.Text=e.Message;}
        });
        Refresh();var body=Stack(Heading("Project policy"),name,disabled,workflows,expiry,hooks,Row(apply,revoke),status,
            Heading("Harness extensions",19),bundles,info,Row(install,toggle,remove),editor);
        body.Padding=new Thickness(18);return CodeScroll(body);
    }
}
