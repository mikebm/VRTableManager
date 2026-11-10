using System.Runtime.InteropServices;
using Godot;

namespace VrLobby;

/// <summary>
/// Puts the lobby's desktop window in front, so keyboard-type cabinet boards and other non-VR input reach it.
/// Godot's WindowMoveToForeground is one plain SetForegroundWindow, which Windows quietly turns down (it only
/// flashes the taskbar) for a window in the background whose process didn't get the last key press, and that's
/// the lobby after a table: the player's keys went to VPX. Joining the input of the window that has the
/// keyboard first makes Windows treat the request as coming from that window, which it allows.
/// </summary>
public static class WindowFocus
{
    static IntPtr Handle() =>
        (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);

    public static bool IsInFront()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true; // nothing to do elsewhere: the retry stops straight away
        }

        var hwnd = Handle();
        return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
    }

    /// <summary>One attempt; true when the lobby's window is in front afterwards.</summary>
    public static bool BringToFront()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var hwnd = Handle();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (GetForegroundWindow() == hwnd)
        {
            return true;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        // Godot's window belongs to this (the main) thread, the one _Process runs on.
        var us = GetCurrentThreadId();
        var front = GetForegroundWindow();
        var theirs = front != IntPtr.Zero ? GetWindowThreadProcessId(front, out _) : 0;
        var joined = theirs != 0 && theirs != us && AttachThreadInput(us, theirs, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (joined)
            {
                AttachThreadInput(us, theirs, false);
            }
        }

        if (GetForegroundWindow() == hwnd)
        {
            return true;
        }

        // Last resort, the call Alt+Tab uses: Windows lets it through more often than SetForegroundWindow.
        SwitchToThisWindow(hwnd, true);
        return GetForegroundWindow() == hwnd;
    }

    const int SwRestore = 9;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    static extern void SwitchToThisWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attach
    );

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
}
