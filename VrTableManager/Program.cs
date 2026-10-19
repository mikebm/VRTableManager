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
            return PlaySession.RunAsync(args).GetAwaiter().GetResult();

        RecordExePath();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
        catch { /* not fatal: the lobby also looks next to itself */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
