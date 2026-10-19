using System.Diagnostics;

namespace VrTableManager.Services;

/// <summary>
/// Headless "play a table, then go back" mode, used by the VR lobby:
/// <c>VrTableManager.exe --play &lt;table&gt; [--vr] [--settings &lt;json&gt;] [--return &lt;exe&gt; [--return-arg &lt;a&gt;]...]</c>.
/// The lobby quits (releasing the headset), this launches the table through the same engine the
/// manager's Play button uses, waits for it to exit, then starts the lobby again.
/// </summary>
public static class PlaySession
{
    public const string PlayFlag = "--play";

    public static bool IsPlayCommand(string[] args) => args.Length > 1 && args[0] == PlayFlag;

    public static List<string> BuildArgs(string tablePath, bool vr, string? settingsPath, string? returnExe, IEnumerable<string> returnArgs)
    {
        var a = new List<string> { PlayFlag, tablePath };
        if (vr) a.Add("--vr");
        if (!string.IsNullOrEmpty(settingsPath)) a.AddRange(["--settings", settingsPath]);
        if (!string.IsNullOrEmpty(returnExe))
        {
            a.AddRange(["--return", returnExe]);
            foreach (var r in returnArgs) a.AddRange(["--return-arg", r]);
        }
        return a;
    }

    public sealed record Request(string TablePath, bool Vr, string? SettingsPath, string? ReturnExe, List<string> ReturnArgs);

    public static Request Parse(string[] args)
    {
        string? settings = null, ret = null;
        var vr = false;
        var retArgs = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vr": vr = true; break;
                case "--settings" when i + 1 < args.Length: settings = args[++i]; break;
                case "--return" when i + 1 < args.Length: ret = args[++i]; break;
                case "--return-arg" when i + 1 < args.Length: retArgs.Add(args[++i]); break;
            }
        }
        return new Request(args[1], vr, settings, ret, retArgs);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var req = Parse(args);
        var code = 0;
        try
        {
            var s = req.SettingsPath != null ? AppSettings.LoadFrom(req.SettingsPath) : AppSettings.Load();
            var table = TableInstaller.Scan(s).FirstOrDefault(t => SamePath(t.TablePath, req.TablePath))
                ?? throw new FileNotFoundException("Table not found in the manager's folders: " + req.TablePath);
            var engine = Engines.For(table.Manifest.Engine);
            Log($"start {engine.DisplayName}: {req.TablePath} (vr={req.Vr})");
            using var proc = await engine.LaunchAsync(s, table, req.Vr, CancellationToken.None);
            var stats = PlayStats.PathBeside(req.SettingsPath);
            var started = DateTime.Now;
            TryRecord(() => PlayStats.Mutate(st => st.RecordPlay(table.TablePath, started), stats));
            await proc.WaitForExitAsync();
            TryRecord(() => PlayStats.Mutate(st => st.AddTime(table.TablePath, DateTime.Now - started), stats));
            Log("table exited");
        }
        catch (Exception e)
        {
            Log("failed: " + e.Message);
            code = 1;
        }
        finally
        {
            if (!string.IsNullOrEmpty(req.ReturnExe))
            {
                var psi = new ProcessStartInfo(req.ReturnExe) { UseShellExecute = false };
                foreach (var a in req.ReturnArgs) psi.ArgumentList.Add(a);
                try { Process.Start(psi); Log("returned to " + req.ReturnExe); }
                catch (Exception e) { Log("couldn't return: " + e.Message); }
            }
        }
        return code;
    }

    /// <summary>Play counting must never stop a table from launching or the lobby from coming back.</summary>
    static void TryRecord(Action a)
    {
        try { a(); } catch (Exception e) { Log("couldn't save play stats: " + e.Message); }
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string LogPath => Path.Combine(AppSettings.DataDir, "play.log");

    static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:s} {line}{Environment.NewLine}"); }
        catch { /* logging must never stop a launch */ }
    }
}
