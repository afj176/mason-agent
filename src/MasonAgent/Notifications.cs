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

    public static void Show(string title, string body, IntPtr window)
    {
        if (unavailable || GetForegroundWindow() == window) return;
        try
        {
            if (!registered) { AppNotificationManager.Default.Register(); registered = true; }
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body.Length > 180 ? body[..180] + "…" : body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            unavailable = true;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
