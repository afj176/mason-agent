using System.Runtime.InteropServices;

namespace Mason;

/// <summary>
/// Shows run progress on Mason's taskbar button and flashes it when something needs the user while the window is in
/// the background: an approval (paused, amber), a failure (error, red) or a finished run.
/// </summary>
public static class TaskbarStatus
{
    public enum Kind { None, Working, NeedsYou, Failed }
    static ITaskbarList3? taskbar;
    static Kind last = Kind.None;

    public static void Show(IntPtr window, Kind kind)
    {
        if (window == IntPtr.Zero || kind == last) return;
        var previous = last; last = kind;
        try
        {
            taskbar ??= Create();
            taskbar?.SetProgressState(window, kind switch { Kind.Working => 0x1, Kind.NeedsYou => 0x8, Kind.Failed => 0x4, _ => 0x0 });
            if (kind is Kind.NeedsYou or Kind.Failed) { taskbar?.SetProgressValue(window, 100, 100); Flash(window); }
            else if (kind == Kind.None && previous == Kind.Working) Flash(window);
        }
        catch (COMException) { taskbar = null; }
    }

    /// <summary>Flashes the taskbar button until Mason is brought forward; does nothing when it already is.</summary>
    public static void Flash(IntPtr window)
    {
        if (GetForegroundWindow() == window) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = 0x2 | 0xC, Count = 0, Timeout = 0 };
        FlashWindowEx(ref info);
    }

    static ITaskbarList3? Create()
    {
        var type = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
        if (type == null) return null;
        var list = (ITaskbarList3)Activator.CreateInstance(type)!;
        list.HrInit();
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
    }
}
