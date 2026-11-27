using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Mason;

/// <summary>
/// Windows notifications for a finished run or a waiting approval, shown only while Mason is in the background.
/// Registration needs the app identity Mason sets up for its shortcut; without it the notification is skipped and the
/// taskbar flash still tells the user.
/// </summary>
public static class Notifications
{
    static bool registered, unavailable;

    /// <param name="pose">A file in Assets/Poses (without extension) shown as the notification's picture, or null for none.</param>
    public static void Show(string title, string body, IntPtr window, string? pose = null)
    {
        if (unavailable || GetForegroundWindow() == window) return;
        try
        {
            if (!registered) { AppNotificationManager.Default.Register(); registered = true; }
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body.Length > 180 ? body[..180] + "…" : body);
            // Unpackaged apps cannot use ms-appx URIs here, so the pose is referenced by its installed file.
            var picture = pose == null ? null : Path.Combine(AppContext.BaseDirectory, "Assets", "Poses", pose + ".png");
            if (picture != null && File.Exists(picture)) builder.SetAppLogoOverride(new Uri(picture), AppNotificationImageCrop.Default);
            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            unavailable = true;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
