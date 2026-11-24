using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    FrameworkElement ExperimentReview(CodeSession session,CodeWorkspace scope,CodeViewerEntry entry)
    {
        var origin=session.Experiment??throw new InvalidOperationException("Open an experiment session.");
        var parent=codeStore!.Sessions.Single(s=>s.Id==origin.Session && s.Workspace.Equals(origin.Parent,StringComparison.OrdinalIgnoreCase));
        var files=new ComboBox{Header="Changed file",HorizontalAlignment=HorizontalAlignment.Stretch};
        var list=new ComboBox{Header="Hunk",HorizontalAlignment=HorizontalAlignment.Stretch};
        var preview=new TextBox{IsReadOnly=true,AcceptsReturn=true,MaxHeight=360,TextWrapping=TextWrapping.NoWrap};
        var status=CodeNote("");
        var checks=CodeNote("");
        var siblings=codeStore.Sessions.Where(s=>s.Experiment?.Session==parent.Id && s.Experiment.Turn==origin.Turn).ToArray();
        var forkChoice=new ComboBox{Header="Experiment",ItemsSource=siblings.Select(s=>s.Title).ToArray(),SelectedIndex=Array.FindIndex(siblings,s=>s.Id==session.Id)};
        ExperimentHunk[] hunks=[];int generation=0;
        CodeSession Chosen()=>siblings[forkChoice.SelectedIndex];
        async Task LoadFiles()
        {
            int now=++generation;
            var branch=Chosen();
            var changed=await CodeExperiment.Changed(new CodeWorkspace(branch.Workspace),branch.Experiment!,entry.Cancellation.Token);
            if(now!=generation)return;
            list.ItemsSource=null;preview.Text="";hunks=[];files.ItemsSource=changed;files.SelectedIndex=changed.Length>0?0:-1;
            checks.Text=branch.Turns.LastOrDefault(t=>t.Acceptance!=null)?.Acceptance is {} plan ? CodeAcceptance.Receipt(plan):"No acceptance receipts in this experiment session.";
            status.Text=changed.Length+" changed files";
        }
        async Task Guard(Func<Task> action){try{await action();}catch(Exception e){status.Text=e.Message;}}
        list.SelectionChanged+=(_,_)=>preview.Text=list.SelectedIndex>=0 && list.SelectedIndex<hunks.Length?hunks[list.SelectedIndex].Patch:"";
        files.SelectionChanged+=async (_,_)=>await Guard(async ()=>{
            int now=++generation; var branch=Chosen();hunks=[];list.ItemsSource=null;preview.Text="";
            if(files.SelectedItem is not string path)return;
            var patches=await CodeExperiment.Hunks(new CodeWorkspace(branch.Workspace),branch.Experiment!,path,entry.Cancellation.Token);
            if(now!=generation)return;
            hunks=patches;list.ItemsSource=hunks.Select(h=>h.Title).ToArray();list.SelectedIndex=hunks.Length>0?0:-1;
        });
        forkChoice.SelectionChanged+=async (_,_)=>await Guard(LoadFiles);
        var apply=Button("Apply selected hunk to parent",async (_,_)=>await Guard(async ()=>{
            var branch=Chosen();var index=list.SelectedIndex;
            if(files.SelectedItem is not string path||index<0 ||index>=hunks.Length)return;
            var hunk=hunks[index];
            var dlg=new ContentDialog{Title="Apply reviewed change?",XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,
                Content=CodeNote(path+"\n"+hunk.Title+"\nDestination: "+parent.Workspace),PrimaryButtonText="Apply hunk",CloseButtonText="Cancel"};
            if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
            await CodeExperiment.Apply(new CodeWorkspace(branch.Workspace),branch.Experiment!,path,hunk.Id,entry.Cancellation.Token);
            status.Text="Applied "+path+" to "+parent.Workspace;
        }));
        var body=Stack(Heading("Review experiment"),forkChoice,CodeNote("Parent · "+parent.Workspace),files,list,preview,checks,
            Button("Refresh patch",async (_,_)=>await Guard(LoadFiles)),apply,status);
        body.Padding=new Thickness(18);body.Loaded+=async (_,_)=>await Guard(LoadFiles);
        return CodeScroll(body);
    }
}
