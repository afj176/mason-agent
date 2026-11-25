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
    public string State=>File==null?(Connection!.Checks.LastOrDefault() is {} check?"Observed "+check.At.LocalDateTime.ToString("g"):"Not checked"):File.Verdict.Length>0?File.State+" · "+File.Verdict:File.State;
    public string Kind=>File==null?"Saved server connection":"Local model · "+File.Model.Format;
    public Microsoft.UI.Xaml.Controls.Symbol Icon=>File?.Icon??Microsoft.UI.Xaml.Controls.Symbol.Link;
    /// <summary>Theme the pills are drawn for; the library re-renders when the theme changes.</summary>
    public static bool Dark {get;set;}=true;
    AiCapability? Passed=>Connection?.Checks.Where(c=>c.State=="Passed"&&c.Capability!=AiCapability.Models).OrderByDescending(c=>c.At).Select(c=>(AiCapability?)c.Capability).FirstOrDefault()??Connection?.ManagedCapability;
    AgentRole Role=>File?.Model.Role??Passed switch{AiCapability.Embeddings=>AgentRole.CodeSearch,AiCapability.Reranking=>AgentRole.Reranker,_=>AgentRole.Implementer};
    public string RoleLabel=>File!=null?ModelVerdict.Label(Role):Passed switch{AiCapability.Embeddings=>"Code search · embeddings",AiCapability.Reranking=>"Code search · reranker",AiCapability.Chat=>"Coding agent",_=>"Connection"};
    public Microsoft.UI.Xaml.Media.SolidColorBrush RoleInk=>ModelTones.InkBrush(ModelTones.For(Role),Dark);
    public Microsoft.UI.Xaml.Media.SolidColorBrush RoleFill=>ModelTones.FillBrush(ModelTones.For(Role),Dark);
    Tone StatusTone=>File?.StatusTone??(Connection!.Checks.Where(c=>c.Capability!=AiCapability.Models).OrderByDescending(c=>c.At).FirstOrDefault()?.State=="Passed"?Tone.Good:Tone.Neutral);
    public string StatusLabel=>File?.StatusLabel??(StatusTone==Tone.Good?"Checked":"Not checked");
    public string StatusDetail=>File?.StatusDetail??Connection!.Health.TrimStart('●','○',' ')+" · "+Connection.Endpoint;
    public Microsoft.UI.Xaml.Media.SolidColorBrush StatusInk=>ModelTones.InkBrush(StatusTone,Dark);
    public Microsoft.UI.Xaml.Media.SolidColorBrush StatusFill=>ModelTones.FillBrush(StatusTone,Dark);
    public string Subtitle=>File!=null?File.Facts:"Saved connection · "+Connection!.Model;
}
