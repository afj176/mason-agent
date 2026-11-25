using System.Text.Json;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    FrameworkElement DeliveryView(CodeSession session,CodeWorkspace scope,CodeViewerEntry entry,Action<CodeSession,DeveloperEvidence>? attach)
    {
        var branch=new TextBox{Header="Target branch",Text=session.Delivery?.Base??"main"};
        var title=new TextBox{Header="Pull request title",Text=session.Delivery?.Title??session.Title,MaxLength=120};
        var list=new ListView{Header="Reviewed hunks",SelectionMode=ListViewSelectionMode.Multiple,MaxHeight=190};
        var patch=new TextBox{Header="Patch",IsReadOnly=true,AcceptsReturn=true,MaxHeight=220,TextWrapping=TextWrapping.NoWrap};
        var summary=new TextBox{Header="Draft",IsReadOnly=true,AcceptsReturn=true,MaxHeight=230,TextWrapping=TextWrapping.Wrap,Text=session.Delivery?.Body??""};
        var status=CodeNote(session.Delivery?.Url??"");
        ReviewedPatch[] patches=[];string revision="";bool busy=false;
        async Task Guard(Func<Task> f){if(busy)return;busy=true;try{await f();}catch(Exception e){status.Text=e.Message;}finally{busy=false;}}
        var refresh=Button("Load patch",async (_,_)=>await Guard(async ()=>{
            var result=await CodeDelivery.Review(scope,branch.Text.Trim(),entry.Cancellation.Token);(revision,patches)=result;
            list.ItemsSource=patches.Select(p=>p.Path+" · "+p.Hunk.Title).ToArray();patch.Text="";status.Text=patches.Length+" hunks available";
        }));
        list.SelectionChanged+=(_,_)=>patch.Text=list.SelectedIndex>=0 && list.SelectedIndex<patches.Length?patches[list.SelectedIndex].Hunk.Patch:"";
        var draft=Button("Save local draft",async (_,_)=>await Guard(async ()=>{
            var ids=list.SelectedItems.Cast<string>().Select(label=>patches.Single(p=>p.Path+" · "+p.Hunk.Title==label).Hunk.Id).ToArray();
            var d=await CodeDelivery.Draft(scope,session,branch.Text.Trim(),revision,title.Text,ids,entry.Cancellation.Token);
            var old=session.Delivery;session.Delivery=d;
            try{codeStore!.Save(session);}catch{session.Delivery=old;throw;}
            summary.Text=d.Body;status.Text="Saved "+d.Commit[..12]+" locally.";
        }));
        var publish=Button("Publish draft PR…",async (_,_)=>await Guard(async ()=>{
            var d=session.Delivery??throw new InvalidOperationException("Save a local draft first.");
            if(d.Repository.Length==0)throw new InvalidOperationException("This draft has no GitHub origin.");
            var dlg=new ContentDialog{Title="Push and publish draft PR?",XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,
                Content=CodeScroll(CodeNote(d.Repository+" · "+d.Branch+" → "+d.Base+"\n"+d.Commit+"\n\n"+d.Title+"\n\n"+d.Body)),
                PrimaryButtonText="Push and publish",CloseButtonText="Cancel"};
            if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
            var url=await CodeDelivery.Publish(scope,d,entry.Cancellation.Token);session.Delivery=d with{Url=url};codeStore!.Save(session);status.Text=url;
        }));
        var feedback=Button("Attach CI and review feedback",async (_,_)=>await Guard(async ()=>{
            var d=session.Delivery??throw new InvalidOperationException("Select a published draft first.");
            var review=await CodeDelivery.Feedback(scope,d,entry.Cancellation.Token);
            if(review.Observations.Length==0){status.Text="No failing check or review feedback returned.";return;}
            var text=JsonSerializer.Serialize(new{review.Url,review.Head, matchesDraft=review.Head==d.Commit,observations=review.Observations.Take(12).Select(s=>s[..Math.Min(s.Length,440)])});
            if(attach==null)throw new InvalidOperationException("Open the delivery tab from a session.");
            attach(session,new(Guid.NewGuid(),"PR feedback",d.Repository+" · review",text,"",null));status.Text="Attached feedback to this session.";
        }));
        var body=Stack(Heading("Git delivery"),branch,refresh,list,patch,title,draft,summary,publish,feedback,status);
        body.Padding=new Thickness(18);return CodeScroll(body);
    }
}
