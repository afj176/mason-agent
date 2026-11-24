using System.IO;
using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace Mason;

public sealed partial class MainWindow
{
    void BuildCodeBrowser(CodeViewerEntry entry,ContentControl host,CodeTab tab,CodeWorkspace workspace,Action save,Action<string,string> open)
    {
        var site=new CodeWebsite(workspace);
        bool project=tab.Kind=="site";
        var initial=project?site.Page(tab.Target):CodeProtocol.PreviewUri(tab.Target);
        var origin=project?site.Origin:initial;
        var current=Uri.TryCreate(tab.Location,UriKind.Absolute,out var saved) && CodeWebsite.SameOrigin(origin,saved)?saved:initial;
        string Display(Uri uri)=>project?Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))+uri.Query+uri.Fragment:uri.AbsoluteUri;
        var address=new TextBox{Text=Display(current),PlaceholderText="HTML file or local server URL",MinWidth=90};
        AutomationProperties.SetName(address,"Website address");
        var status=CodeNote("Opening website…");
        var view=new WebView2{HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
        AutomationProperties.SetName(view,"Project website");
        var token=entry.Cancellation.Token;
        bool started=false;
        var back=ChatIcon("Back", "\uE72B",(_,_)=>{if(view.CoreWebView2?.CanGoBack==true)view.GoBack();});
        var forward=ChatIcon("Forward", "\uE72A",(_,_)=>{if(view.CoreWebView2?.CanGoForward==true)view.GoForward();});
        var reload=ChatIcon("Reload website", "\uE72C",(_,_)=>{if(view.CoreWebView2!=null)view.Reload();else _=Initialize();});
        back.IsEnabled=forward.IsEnabled=false;
        void Status(string value){status.Text=value;status.Visibility=value.Length==0?Visibility.Collapsed:Visibility.Visible;}
        void Navigate()
        {
            try{
                string input=address.Text.Trim();
                if(input==Display(current)){view.CoreWebView2?.Navigate(current.AbsoluteUri);return;}
                if(Uri.TryCreate(input,UriKind.Absolute,out var absolute)){
                    var url=CodeProtocol.PreviewUri(absolute.AbsoluteUri);
                    if(!project && CodeWebsite.SameOrigin(origin,url))view.CoreWebView2?.Navigate(url.AbsoluteUri);
                    else {address.Text=Display(current);open("web",url.AbsoluteUri);}
                }else{
                    var page=site.Page(input);
                    if(project)view.CoreWebView2?.Navigate(page.AbsoluteUri);
                    else {address.Text=Display(current);open("site",workspace.Relative(workspace.Resolve(input)));}
                }
            }catch(Exception error){Status(error.Message);}
        }
        var go=ChatIcon("Go to website address","\uE8AD",(_,_)=>Navigate());
        address.KeyDown+=(_,e)=>{if(e.Key==VirtualKey.Enter){e.Handled=true;Navigate();}};
        var bar=new Grid{ColumnSpacing=4};
        bar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        bar.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        bar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        bar.Children.Add(Row(back,forward,reload));Grid.SetColumn(address,1);bar.Children.Add(address);
        Grid.SetColumn(go,2);bar.Children.Add(go);
        var controls=Stack(bar,status);
        if(!project){
            var external=CodeLink("Open in browser",async (_,_)=>{try{await Launcher.LaunchUriAsync(current);}catch(Exception e){Status(e.Message);}});
            var links=Row(external);
            if(site.StartPage() is {} file)links.Children.Add(CodeLink("Open project page",(_,_)=>open("site",file)));
            controls.Children.Add(links);
        }
        host.Content=Vertical(controls,view);
        entry.ReleaseBrowser=()=>view.Close();
        view.Loaded+=async (_,_)=>await Initialize();

        async Task Initialize()
        {
            if(started || token.IsCancellationRequested)return;
            started=true;
            try{
                string folder=Path.Combine(Store.Root,"code-webview",site.Origin.Host);
                var env=await CoreWebView2Environment.CreateWithOptionsAsync(null,folder,null);
                if(token.IsCancellationRequested)return;
                await view.EnsureCoreWebView2Async(env);
                if(token.IsCancellationRequested)return;
                var core=view.CoreWebView2??throw new InvalidOperationException("The WebView2 runtime did not initialize.");
                core.Settings.IsWebMessageEnabled=false;
                core.Settings.AreHostObjectsAllowed=false;
                core.Settings.IsPasswordAutosaveEnabled=false;
                core.Settings.IsGeneralAutofillEnabled=false;
                core.NewWindowRequested+=(_,args)=>args.Handled=true;
                core.PermissionRequested+=(_,args)=>args.State=CoreWebView2PermissionState.Deny;
                core.DownloadStarting+=(_,args)=>args.Cancel=true;
                core.LaunchingExternalUriScheme+=(_,args)=>args.Cancel=true;
                void Restrict(CoreWebView2NavigationStartingEventArgs args){
                    if(!Uri.TryCreate(args.Uri,UriKind.Absolute,out var target) || !CodeWebsite.SameOrigin(origin,target)){
                        args.Cancel=true;Status("This link leaves the selected website.");
                    }
                }
                core.NavigationStarting+=(_,args)=>Restrict(args);
                core.FrameNavigationStarting+=(_,args)=>Restrict(args);
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested+=async (_,args)=>{
                    var req=args.Request;
                    if(!Uri.TryCreate(req.Uri,UriKind.Absolute,out var uri) || !CodeWebsite.SameOrigin(origin,uri)){
                        args.Response=env.CreateWebResourceResponse(null,403,"Forbidden","");return;
                    }
                    if(!project)return;
                    var pending=args.GetDeferral();
                    try{
                        if(req.Method is not ("GET" or "HEAD")){args.Response=env.CreateWebResourceResponse(null,405,"Method Not Allowed","Allow: GET, HEAD");return;}
                        var part=await Task.Run(()=>site.Read(uri),token);
                        if(token.IsCancellationRequested)return;
                        const string policy="default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; frame-src 'none'; worker-src 'none'; form-action 'none'; base-uri 'self'";
                        var stream=new MemoryStream(req.Method=="HEAD"?Array.Empty<byte>():part.Bytes,false).AsRandomAccessStream();
                        args.Response=env.CreateWebResourceResponse(stream,200,"OK","Content-Type: "+part.Type+"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: "+policy);
                    }catch(Exception) when(token.IsCancellationRequested){}
                    catch(Exception) when(!token.IsCancellationRequested){args.Response=env.CreateWebResourceResponse(null,404,"Not Found","Cache-Control: no-store");}
                    finally{
                        try{pending.Complete();}
                        catch(Exception) when(token.IsCancellationRequested){}
                    }
                };
                core.HistoryChanged+=(_,_)=>{back.IsEnabled=core.CanGoBack;forward.IsEnabled=core.CanGoForward;};
                core.NavigationCompleted+=(_,args)=>{
                    if(token.IsCancellationRequested)return;
                    if(args.WebErrorStatus==CoreWebView2WebErrorStatus.OperationCanceled)return;
                    if(!args.IsSuccess || args.HttpStatusCode>=400){
                        Status(project?"Could not load this project page. Check the file and reload.":$"Could not load {origin.Authority}. Check the server address, then reload.");return;
                    }
                    if(Uri.TryCreate(core.Source,UriKind.Absolute,out var location) && CodeWebsite.SameOrigin(origin,location)){
                        current=location;address.Text=Display(location);tab.Location=location.AbsoluteUri;Status("");save();
                    }
                };
                core.Navigate(current.AbsoluteUri);
            }catch(Exception error) when(!token.IsCancellationRequested){
                started=false;
                Status(error.HResult==unchecked((int)0x80070002)
                    ?"Install or repair the Microsoft Edge WebView2 Runtime, then reload this tab."
                    :"Website preview could not start. "+error.Message);
            }
            catch(Exception) when(token.IsCancellationRequested){}
        }
    }
}
