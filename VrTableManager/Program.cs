using Avalonia;
using VrTableManager.Services;

namespace VrTableManager;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Launched by the VR lobby: play one table headless, then start the lobby again.
        if (PlaySession.IsPlayCommand(args))
        {
            return PlaySession.RunAsync(args).GetAwaiter().GetResult();
        }

        // One manager window at a time: each clears the downloads folder as it starts (left over from the last
        // close), which would take a running one's downloads. (--play above runs beside it as before.)
        Mutex? single;
        bool first;
        try
        {
            single = new Mutex(true, @"Local\VrTableManager.Manager", out first);
        }
        catch (Exception e)
            when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Held by a manager run as administrator: that one is open.
            single = null;
            first = false;
        }
        using var held = single;
        if (!first)
        {
            ShowRunningManager();
            return 0;
        }

        RecordExePath();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Brings the manager that's already open to the front, instead of opening a second one.</summary>
    static void ShowRunningManager()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var self = Environment.ProcessId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("VrTableManager"))
            {
                using (p)
                {
                    // --play processes have no window.
                    if (p.Id != self && p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(p.MainWindowHandle);
                        return;
                    }
                }
            }
            // No window to show: it's hidden while a table or the VR lobby runs (or it's another user's).
            MessageBoxW(
                IntPtr.Zero,
                "VR Table Manager is already open. It comes back when the table or the VR lobby closes.",
                "VR Table Manager",
                0x40 // MB_ICONINFORMATION
            );
        }
        catch
        { /* it's open somewhere; nothing more to do */
        }
    }

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode
    )]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Lets the VR lobby find this exe (it launches tables through it).</summary>
    static void RecordExePath()
    {
        try
        {
            var s = AppSettings.Load();
            if (Environment.ProcessPath is { } exe && s.ManagerExe != exe)
            {
                s.ManagerExe = exe;
                s.Save();
            }
        }
        catch
        { /* not fatal: the lobby also looks next to itself */
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
