using System.Runtime.InteropServices;

namespace Mason;

internal static class ShellIdentity
{
    // Match the installer shortcut. Set before creating any windows so launches
    // from another desktop app cannot inherit that app's taskbar grouping/icon.
    internal const string AppId = "MasonAgent.Desktop";

    internal static void Initialize() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
