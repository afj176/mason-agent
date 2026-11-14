using System.Text.RegularExpressions;

namespace Mason;

/// <summary>Display-only model facts. Discovery and verified capability remain separate.</summary>
public sealed record ModelLibraryCard(LocalModel Model, string State) : System.ComponentModel.INotifyPropertyChanged
{
    string fit="";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Fit {get=>fit;set{if(fit==value)return;fit=value;PropertyChanged?.Invoke(this,new(nameof(Fit)));}}
    public string Path => Model.Path;
    public string Title => FriendlyName(Model.Name);
    public string Task => Model.Task == "Inventory only" ? "Other files" : Model.Task;
    public Microsoft.UI.Xaml.Controls.Symbol Icon => Model.Task switch { "Chat"=>Microsoft.UI.Xaml.Controls.Symbol.Message,_=>Microsoft.UI.Xaml.Controls.Symbol.Document };
    public string Facts => $"{Model.Size} · {Model.Format}"+(Quantization(Model.Name) is {Length:>0} quant?$" · {quant}":"");
    public string Source => Model.Source;
    public string Purpose => Model.Task switch
    {
        "Chat" => "Write, explain and work with text.",
        _ => Model.Support.StartsWith("Needs local files:") ? "Complete this model's local files before starting." : "Saved locally · see compatibility details."
    };
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
