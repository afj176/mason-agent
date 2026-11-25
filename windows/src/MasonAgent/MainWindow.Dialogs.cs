using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Mason;

public sealed partial class MainWindow
{
    async Task<bool> OfferModelCpuRetry(string detail)
    {
        if (dialogOpen) return false;
        dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                RequestedTheme = root.RequestedTheme,
                Title = "CUDA unavailable for this runtime",
                Content = new ScrollViewer
                {
                    MaxHeight = 340,
                    Content = new TextBlock
                    {
                        Text = "Try this model on CPU? CPU inference uses system RAM and may be slower.\n\n" + detail,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    }
                },
                PrimaryButtonText = "Start on CPU",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { dialogOpen = false; }
    }

    async Task Notice(string title, string message)
    {
        if (dialogOpen) { status.Text = message; return; } dialogOpen = true;
        try { await new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 500 }, CloseButtonText = "Close", RequestedTheme = root.RequestedTheme }.ShowAsync(); } finally { dialogOpen = false; }
    }
}







