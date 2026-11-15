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

        // One ROM's attract music, recorded for GENERATE ATTRACT MUSIC (several of these run at once) or the VR lobby.
        if (RomAudioBatch.IsCommand(args))
        {
            RomAudio.ToMp3 = CompressedAudio.WavToMp3;
            return RomAudioBatch.RunCommand(args);
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
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            // Before the window is up the app's own error reporting isn't hooked in yet, and the app would just not
            // open: say why, and keep it for a bug report.
            StartupFailed(e);
            return 1;
        }
    }

    /// <summary>The app couldn't start: the error goes to errors.log and a message box.</summary>
    static void StartupFailed(Exception e)
    {
        var log = "errors.log";
        try
        {
            log = Path.Combine(AppSettings.DataDir, "errors.log");
            File.AppendAllText(
                log,
                $"{DateTime.Now:s} VR Table Manager couldn't start: {e}{Environment.NewLine}{Environment.NewLine}"
            );
        }
        catch
        { /* the message box still says what happened */
        }

        if (OperatingSystem.IsWindows())
        {
            var inner = e is AggregateException { InnerExceptions.Count: 1 } agg
                ? agg.InnerExceptions[0]
                : e;
            MessageBoxW(
                IntPtr.Zero,
                $"VR Table Manager couldn't start:\n\n{inner.GetType().Name}: {inner.Message}\n\n"
                    + "If you've just unzipped an update or patch over it, close VR Table Manager and the VR lobby "
                    + "(check Task Manager) and unzip it again: a file that was in use may not have been replaced.\n\n"
                    + $"The details are saved in {log}.",
                "VR Table Manager",
                0x10 // MB_ICONERROR
            );
        }
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
        AppBuilder
            .Configure<App>()
            // What UsePlatformDetect picks on Windows; the other platforms' backends aren't referenced (see the csproj).
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace();
}
