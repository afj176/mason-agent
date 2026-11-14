using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Mason;

public sealed partial class MainWindow
{
    async Task Notice(string title, string message)
    {
        if (dialogOpen) { status.Text = message; return; } dialogOpen = true;
        try { await new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 500 }, CloseButtonText = "Close", RequestedTheme = root.RequestedTheme }.ShowAsync(); } finally { dialogOpen = false; }
    }
}







