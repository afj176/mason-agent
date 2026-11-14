using Microsoft.UI.Xaml.Data;

namespace Mason;

// Inventory presentation joins distinct resources without fabricating model files or ownership.
[Bindable]
public sealed class LibraryEntry(ModelLibraryCard? file,AiEndpointProfile? connection)
{
    public ModelLibraryCard? File {get;}=file;
    public AiEndpointProfile? Connection {get;}=connection;
    public Guid? ChatProfileId { get; set; }
    public Microsoft.UI.Xaml.Visibility ChatVisibility => ChatProfileId.HasValue ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public override string ToString()=>Title+" · "+Kind+" · "+State;
    public string Key=>File==null?"connection:"+Connection!.Id:"file:"+File.Path;
    public string Title=>File?.Title??Connection!.Name;
    public string Task=>File?.Task??string.Join(", ",Connection!.Checks.Where(c=>c.State=="Passed"&&c.Capability!=AiCapability.Models).Select(c=>c.Capability));
    public string Facts=>File?.Facts??Connection!.Endpoint;
    public string State=>File?.State??(Connection!.Checks.LastOrDefault() is {} check?"Observed "+check.At.LocalDateTime.ToString("g"):"Not checked");
    public string Kind=>File==null?"Saved server connection":"Local model · "+File.Model.Format;
    public Microsoft.UI.Xaml.Controls.Symbol Icon=>File==null?Microsoft.UI.Xaml.Controls.Symbol.Link:Microsoft.UI.Xaml.Controls.Symbol.Library;
}
