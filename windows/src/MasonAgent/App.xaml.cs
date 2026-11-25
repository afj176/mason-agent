using Microsoft.UI.Xaml;
using System.IO;
namespace Mason;

public partial class App : Application
{
    // Kept alive for the full process lifetime; Setup checks this before replacing files.
    readonly System.Threading.Mutex installerMutex=new(false,@"Local\MasonAgent.Desktop.App");
    Window? window;
    public App() { ShellIdentity.Initialize(); if (Environment.GetCommandLineArgs().Contains("--integration-ui") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MASON_DATA_HOME"))) Environment.SetEnvironmentVariable("MASON_DATA_HOME", Path.Combine(AppContext.BaseDirectory, "ui-qa-data")); InitializeComponent(); UnhandledException += (_, e) => { Directory.CreateDirectory(Store.Root); File.WriteAllText(Path.Combine(Store.Root, "crash.txt"), e.Exception.ToString()); }; }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { var main = new MainWindow(); window = main; main.Activate(); if (Environment.GetEnvironmentVariable("MASON_UI_CAPTURE") is { Length: > 0 } capture) main.DispatcherQueue.TryEnqueue(async () => await main.CaptureForReview(capture)); }
        catch (Exception error) { Directory.CreateDirectory(Store.Root); File.WriteAllText(Path.Combine(Store.Root, "crash.txt"), error.ToString()); throw; }
    }
}

