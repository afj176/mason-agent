using System.Text.RegularExpressions;

namespace Mason;

/// <summary>Display-only model facts. Discovery and verified capability remain separate.</summary>
public sealed record ModelLibraryCard(LocalModel Model, string State) : System.ComponentModel.INotifyPropertyChanged
{
    string fit="",verdict="";
    public string StatusLabel {get;set;}="";
    public string StatusDetail {get;set;}="";
    public Tone StatusTone {get;set;}
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Fit {get=>fit;set{if(fit==value)return;fit=value;PropertyChanged?.Invoke(this,new(nameof(Fit)));}}
    /// <summary>Compatibility badge and reason for the agent, from <see cref="ModelCompatibility"/>.</summary>
    public string Verdict {get=>verdict;set{if(verdict==value)return;verdict=value;PropertyChanged?.Invoke(this,new(nameof(Verdict)));}}
    public string Path => Model.Path;
    public string Title => FriendlyName(Model.Name);
    public string Task => ModelVerdict.Label(Model.Role);
    public Microsoft.UI.Xaml.Controls.Symbol Icon => Model.Role switch { AgentRole.Implementer=>Microsoft.UI.Xaml.Controls.Symbol.Message,AgentRole.Vision=>Microsoft.UI.Xaml.Controls.Symbol.Camera,AgentRole.CodeSearch or AgentRole.Reranker=>Microsoft.UI.Xaml.Controls.Symbol.Find,AgentRole.Draft=>Microsoft.UI.Xaml.Controls.Symbol.Forward,AgentRole.Adapter=>Microsoft.UI.Xaml.Controls.Symbol.Repair,AgentRole.Projector=>Microsoft.UI.Xaml.Controls.Symbol.Pictures,_=>Microsoft.UI.Xaml.Controls.Symbol.Document };
    public string Facts => $"{Model.Size} · {Model.Format}"+(Quantization(Model.Name) is {Length:>0} quant?$" · {quant}":"")+(Model.Architecture.Length>0?" · "+Model.Architecture:"");
    public string Source => Model.Source;
    public string Purpose => ModelVerdict.Jobs(Model.Role);
    public static string FriendlyName(string name)
    {
        var shortName = name[(name.LastIndexOf('/') + 1)..];
        var quant=Regex.Match(shortName,@"^(.*?)-(?:UD-)?(?:Q\d[A-Za-z0-9_]*|IQ\d[A-Za-z0-9_]*)$");
        if(quant.Success)shortName=quant.Groups[1].Value;
        shortName=shortName.Replace("-Instruct", " Instruct");
        return Regex.Replace(shortName,@"-(\d+(?:\.\d+)?[BM])(?=-|$)"," · $1");
    }
    static string Quantization(string name) => Regex.Match(name,@"(?:^|-)(?:UD-)?((?:Q|IQ)\d[A-Za-z0-9_]*)$").Groups[1].Value;
    public override string ToString() => $"{Title}. {Task}. {Facts}. {Purpose} {State}";
}
