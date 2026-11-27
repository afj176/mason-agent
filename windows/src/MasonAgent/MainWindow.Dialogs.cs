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
                Content = WithPose(PoseAttention, new ScrollViewer
                {
                    MaxHeight = 340,
                    Content = new TextBlock
                    {
                        Text = "Try this model on CPU? CPU inference uses system RAM and may be slower.\n\n" + detail,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    }
                }),
                PrimaryButtonText = "Start on CPU",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { dialogOpen = false; }
    }

    // Notices about the machine or its setup ask the user to act ("needs attention"); the rest report something that broke.
    static readonly string[] AttentionNotices = ["Runtime", "Runtime setup", "llama.cpp", "Quantized runtime", "Hugging Face access", "Model folder", "Model locations", "Download storage", "Quick setup", "AI request active"];

    /// <summary>A dialog body with a 64 px pose at its left.</summary>
    static FrameworkElement WithPose(string pose, FrameworkElement body, string? meaning = null)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var art = MascotImage(pose, 64, meaning ?? (pose == PoseAttention ? "needs your attention" : "something went wrong"));
        art.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(art);
        Grid.SetColumn(body, 1); grid.Children.Add(body);
        return grid;
    }

    async Task Notice(string title, string message)
    {
        if (dialogOpen) { status.Text = message; return; } dialogOpen = true;
        try { await new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = WithPose(AttentionNotices.Contains(title) ? PoseAttention : PoseFailed, new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 500 }), CloseButtonText = "Close", RequestedTheme = root.RequestedTheme }.ShowAsync(); } finally { dialogOpen = false; }
    }
}







