using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mason.Code;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Mason;

public sealed partial class MainWindow
{
    static async Task WirePreviewPicker(CoreWebView2 core, CodeSession owner, CodeWorkspace scope, Uri origin, bool project,
        Button pick, Button check, Action<string> status, Action<CodeSession,DeveloperEvidence> attach, CancellationToken token)
    {
        bool picking = false;
        int generation = 0;
        var errors = new Queue<string>();
        (string Page, string Name)? selected = null;

        async Task<JsonElement> Call(string method, object args)
        {
            token.ThrowIfCancellationRequested();
            var raw = await core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args));
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        static string Field(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        void Error(string text)
        {
            if (text.Length == 0) return;
            errors.Enqueue(text[..Math.Min(text.Length, 380)]);
            if (errors.Count > 4) errors.Dequeue();
        }
        string Location()
        {
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var here) || !CodeWebsite.SameOrigin(origin, here))
                throw new InvalidOperationException("Open a page in the selected project first.");
            return here.GetLeftPart(UriPartial.Path);
        }
        var inspect = core.GetDevToolsProtocolEventReceiver("Overlay.inspectNodeRequested");
        var exceptions = core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
        var console = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
        exceptions.DevToolsProtocolEventReceived += (_, args) => {
            if (token.IsCancellationRequested) return;
            using var doc = JsonDocument.Parse(args.ParameterObjectAsJson);
            var d = doc.RootElement.GetProperty("exceptionDetails");
            string reason = d.TryGetProperty("exception", out var ex) ? Field(ex, "description").Split('\n')[0] : Field(d, "text");
            Error(reason);
        };
        console.DevToolsProtocolEventReceived += (_, args) => {
            if (token.IsCancellationRequested) return;
            using var doc = JsonDocument.Parse(args.ParameterObjectAsJson);
            var log = doc.RootElement;
            if (Field(log, "type") == "error" && log.TryGetProperty("args", out var items))
                Error(string.Join(" ", items.EnumerateArray().Take(4).Select(v => Field(v, "value"))));
        };
        async Task Toggle(bool on)
        {
            await Call("Overlay.setInspectMode", new { mode = on ? "searchForNode" : "none",
                highlightConfig = new { showInfo = true, contentColor = new { r = 125, g = 110, b = 220, a = 0.35 } } });
            picking = on;
            pick.Content = on ? "Cancel selection" : "Pick element";
        }
        pick.Click += async (_, _) => {
            try { _ = Location(); await Toggle(!picking); if (picking) status("Choose an element to attach to the next message."); }
            catch (Exception e) when (!token.IsCancellationRequested) { status(e.Message); }
            catch (Exception) when (token.IsCancellationRequested) { }
        };
        core.NavigationStarting += (_, _) => {
            generation++; errors.Clear(); selected = null; check.IsEnabled = false;
            picking = false; pick.Content = "Pick element";
        };
        inspect.DevToolsProtocolEventReceived += async (_, args) => {
            if (!picking || token.IsCancellationRequested) return;
            picking = false;
            int documentVersion = generation;
            string? imagePath = null;
            try {
                string page = Location();
                await Toggle(false);
                using var doc = JsonDocument.Parse(args.ParameterObjectAsJson);
                var backendNodeId = doc.RootElement.GetProperty("backendNodeId").GetInt32();
                var ax = await Call("Accessibility.getPartialAXTree", new { backendNodeId, fetchRelatives = false });
                var axNode = ax.GetProperty("nodes").EnumerateArray().FirstOrDefault();
                string name = axNode.ValueKind == JsonValueKind.Object && axNode.TryGetProperty("name", out var n) ? Field(n, "value") : "";
                string role = axNode.ValueKind == JsonValueKind.Object && axNode.TryGetProperty("role", out var r) ? Field(r, "value") : "";
                var resolved = await Call("DOM.resolveNode", new { backendNodeId });
                string oid = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
                JsonElement info;
                try {
                    var reply = await Call("Runtime.callFunctionOn", new { objectId = oid, returnByValue = true, functionDeclaration =
                        "function(){const r=this.getBoundingClientRect();return {main:this.ownerDocument===top.document,tag:this.localName,id:this.id||'',testId:this.getAttribute('data-testid')||'',x:r.left+scrollX,y:r.top+scrollY,width:r.width,height:r.height,privateField:!!this.closest('input,textarea,[contenteditable]')||!!this.querySelector('input,textarea,[contenteditable]')}}"});
                    info = reply.GetProperty("result").GetProperty("value");
                } finally { await Call("Runtime.releaseObject", new { objectId = oid }); }
                if (!info.GetProperty("main").GetBoolean()) throw new InvalidOperationException("Select an element in the main document.");
                if (info.GetProperty("privateField").GetBoolean()) throw new InvalidOperationException("Select a label or control outside editable fields.");
                name = name[..Math.Min(name.Length, 240)];
                string id = Field(info, "id"); id = id[..Math.Min(id.Length, 200)];
                string testId = Field(info, "testId"); testId = testId[..Math.Min(testId.Length, 200)];
                string path = "", hash = ""; int line = 0;
                if (project) {
                    path = Uri.UnescapeDataString(new Uri(page).AbsolutePath.TrimStart('/'));
                    if (path.Length == 0 || path.EndsWith('/')) path += "index.html";
                    var file = scope.Read(path); hash = file.Sha256;
                    if (id.Length > 0) {
                        var candidates = Regex.Matches(file.Content, """\bid\s*=\s*(['"])""" + Regex.Escape(id) + """\1""", RegexOptions.IgnoreCase);
                        if (candidates.Count == 1) line = 1 + file.Content.AsSpan(0,candidates[0].Index).Count('\n');
                    }
                }
                var key = Guid.NewGuid();
                double x = Math.Max(0, info.GetProperty("x").GetDouble()), y = Math.Max(0, info.GetProperty("y").GetDouble());
                double width = Math.Clamp(info.GetProperty("width").GetDouble(), 0, 800), height = Math.Clamp(info.GetProperty("height").GetDouble(), 0, 450);
                if (width >= 1 && height >= 1) {
                    var shot = await Call("Page.captureScreenshot", new {format = "png", captureBeyondViewport = true, clip = new {x,y,width,height,scale = 1}});
                    var bytes = Convert.FromBase64String(shot.GetProperty("data").GetString()!);
                    if (bytes.Length <= 2 * 1024 * 1024) {
                        imagePath = DeveloperEvidence.ImageFile(Store.Root, owner.Id, key);
                        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
                        await File.WriteAllBytesAsync(imagePath, bytes, token);
                    }
                }
                if (generation != documentVersion || Location() != page) throw new InvalidOperationException("The preview navigated. Select the element again.");
                var details = JsonSerializer.Serialize(new {page,role,name,tag=Field(info,"tag"),id,testId,consoleErrors=errors.ToArray(),candidateSourceLine=line});
                attach(owner, new(key, "preview element", (role + " " + name).Trim(), details, path, hash.Length==0?null:hash, Line:line, HasScreenshot:imagePath!=null));
                imagePath = null;
                selected = project && (role is "button" or "link") && name.Length != 0 ? (path,name) : null;
                check.IsEnabled = selected != null;
                status("Attached " + role + " " + name + " to the next message.");
            } catch (Exception e) when (!token.IsCancellationRequested) { status("Could not attach element. " + e.Message); }
            catch (Exception) when (token.IsCancellationRequested) { }
            finally { if (imagePath != null) try { File.Delete(imagePath); } catch (IOException) { } }
        };
        check.Click += async (_, _) => {
            if (selected is not {} target) return;
            try {
                var expected = new TextBox { Header = "Exact text that should appear after the click", MaxLength = 240 };
                var dlg = new ContentDialog {XamlRoot=pick.XamlRoot,RequestedTheme=pick.ActualTheme,Title="Verify " + target.Name,
                    Content=expected,PrimaryButtonText="Run in disposable Edge",CloseButtonText="Cancel"};
                if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(expected.Text)) return;
                check.IsEnabled = false;
                var plan = new CodeAcceptancePlan {Request=target.Name,Units=[target.Name],Checks=[new(1,target.Name,"browser_click",target.Page,target.Name,expected.Text.Trim())]};
                await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,token);
                var result = plan.Results.Single();
                var proof = JsonSerializer.Serialize(new { result.Passed, result.Detail, target.Page, target.Name, expected=expected.Text.Trim(), plan.VerifiedRevision });
                attach(owner,new(Guid.NewGuid(),"browser check",(result.Passed?"Passed · ":"Failed · ")+target.Name,proof,target.Page,null));
                status(result.Detail);
            } catch (Exception e) when (!token.IsCancellationRequested) { status(e.Message); }
            catch (Exception) when (token.IsCancellationRequested) { }
            finally { if (!token.IsCancellationRequested) check.IsEnabled = selected != null; }
        };
        await Call("DOM.enable",new {}); await Call("Overlay.enable",new {}); await Call("Runtime.enable",new {});
        pick.IsEnabled = true;
    }
}
