using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VrTableManager.Services;

/// <summary>
/// Headless "play a table, then go back" mode, used by the VR lobby:
/// <c>VrTableManager.exe --play &lt;table&gt; [--vr] [--settings &lt;json&gt;] [--status &lt;json&gt;] [--lobby-pid &lt;pid&gt;]
/// [--return &lt;exe&gt; [--return-arg &lt;a&gt;]...]</c>.
/// This launches the table through the same engine the manager's Play button uses, waits for it to exit,
/// then starts the lobby again. Meanwhile the lobby shows a loading screen in the headset and quits once the
/// table takes the headset over; <c>--status</c> tells it how the launch is going (<see cref="PlayStatus"/>).
/// </summary>
public static class PlaySession
{
    public const string PlayFlag = "--play";

    public static bool IsPlayCommand(string[] args) => args.Length > 1 && args[0] == PlayFlag;

    public static List<string> BuildArgs(
        string tablePath,
        bool vr,
        string? settingsPath,
        string? returnExe,
        IEnumerable<string> returnArgs,
        string? statusPath = null,
        int lobbyPid = 0
    )
    {
        var a = new List<string> { PlayFlag, tablePath };
        if (vr)
        {
            a.Add("--vr");
        }

        if (!string.IsNullOrEmpty(settingsPath))
        {
            a.AddRange(["--settings", settingsPath]);
        }

        if (!string.IsNullOrEmpty(statusPath))
        {
            a.AddRange(["--status", statusPath]);
        }

        if (lobbyPid > 0)
        {
            a.AddRange(["--lobby-pid", lobbyPid.ToString()]);
        }

        if (!string.IsNullOrEmpty(returnExe))
        {
            a.AddRange(["--return", returnExe]);
            foreach (var r in returnArgs)
            {
                a.AddRange(["--return-arg", r]);
            }
        }
        return a;
    }

    public sealed record Request(
        string TablePath,
        bool Vr,
        string? SettingsPath,
        string? ReturnExe,
        List<string> ReturnArgs,
        string? StatusPath = null,
        int LobbyPid = 0
    );

    public static Request Parse(string[] args)
    {
        string? settings = null,
            ret = null,
            status = null;
        var vr = false;
        var lobbyPid = 0;
        var retArgs = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vr":
                    vr = true;
                    break;
                case "--settings" when i + 1 < args.Length:
                    settings = args[++i];
                    break;
                case "--status" when i + 1 < args.Length:
                    status = args[++i];
                    break;
                case "--lobby-pid" when i + 1 < args.Length:
                    int.TryParse(args[++i], out lobbyPid);
                    break;
                case "--return" when i + 1 < args.Length:
                    ret = args[++i];
                    break;
                case "--return-arg" when i + 1 < args.Length:
                    retArgs.Add(args[++i]);
                    break;
            }
        }
        return new Request(args[1], vr, settings, ret, retArgs, status, lobbyPid);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var req = Parse(args);
        // Started by a lobby running on this app's own .NET: the table and its helpers shouldn't inherit that.
        LobbyDotnet.ClearInherited();
        var code = 0;
        void Status(string state, string message = "")
        {
            if (req.StatusPath != null)
            {
                PlayStatus.Write(req.StatusPath, state, message);
            }
        }
        Status(PlayStatus.Starting);
        try
        {
            var s =
                req.SettingsPath != null
                    ? AppSettings.LoadFrom(req.SettingsPath)
                    : AppSettings.Load();
            var table =
                TableInstaller.Scan(s).FirstOrDefault(t => SamePath(t.TablePath, req.TablePath))
                ?? throw new FileNotFoundException(
                    "Table not found in the manager's folders: " + req.TablePath
                );
            var engine = Engines.For(table.Manifest.Engine);
            Log($"start {engine.DisplayName}: {req.TablePath} (vr={req.Vr})");
            using var proc = await engine.LaunchAsync(s, table, req.Vr, CancellationToken.None);
            Status(PlayStatus.Running);
            var stats = PlayStats.PathBeside(req.SettingsPath);
            var started = DateTime.Now;
            TryRecord(() => PlayStats.Mutate(st => st.RecordPlay(table.TablePath, started), stats));
            await proc.WaitForExitAsync();
            VpxIniLayers.Finish(proc); // this process ends soon: don't leave it to the background wait
            TryRecord(() =>
                PlayStats.Mutate(st => st.AddTime(table.TablePath, DateTime.Now - started), stats)
            );
            Log("table exited");
            LetLobbyTakeKeyboard(req.LobbyPid);
            Status(PlayStatus.Exited);
        }
        catch (Exception e)
        {
            Log("failed: " + e.Message);
            LetLobbyTakeKeyboard(req.LobbyPid);
            Status(PlayStatus.Failed, e.Message);
            code = 1;
        }
        finally
        {
            // A lobby that's still open is still on its loading screen (the table failed, or closed before it
            // took over the headset). It reads the status and carries on, so starting another would make two.
            if (LobbyStillOpen(req.LobbyPid))
            {
                Log("lobby still open; not starting another");
            }
            else if (!string.IsNullOrEmpty(req.ReturnExe))
            {
                var psi = new ProcessStartInfo(req.ReturnExe) { UseShellExecute = false };
                foreach (var a in req.ReturnArgs)
                {
                    psi.ArgumentList.Add(a);
                }
                // The lobby runs on this app's own .NET when it can (cleared from this process for the table),
                // unless it's exported, which brings its own.
                if (
                    !LobbyDotnet.IsExported(req.ReturnExe)
                    && LobbyDotnet.Prepare() is { } dotnetRoot
                )
                {
                    LobbyDotnet.Apply(psi, dotnetRoot);
                }

                try
                {
                    Process.Start(psi);
                    Log("returned to " + req.ReturnExe);
                }
                catch (Exception e)
                {
                    Log("couldn't return: " + e.Message);
                }
            }
        }
        return code;
    }

    /// <summary>
    /// True when the lobby that started this session is still running. It gets a few seconds to finish
    /// closing first, in case it was on its way out just as the table exited.
    /// </summary>
    static bool LobbyStillOpen(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.WaitForExit(3000);
        }
        catch
        {
            return false;
        } // gone already (or not ours to see)
    }

    /// <summary>
    /// Windows only lets a window in the background take the keyboard when a process it trusts says so. This one
    /// was started by the lobby while the lobby was in front, so it may still be trusted; passing that on helps the
    /// lobby (waiting behind the table) get keyboard-type cabinet boards back. Harmless when it isn't allowed: the
    /// lobby keeps trying on its own.
    /// </summary>
    static void LetLobbyTakeKeyboard(int pid)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            AllowSetForegroundWindow(pid);
        }
        catch
        { /* only a help */
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Play counting must never stop a table from launching or the lobby from coming back.</summary>
    static void TryRecord(Action a)
    {
        try
        {
            a();
        }
        catch (Exception e)
        {
            Log("couldn't save play stats: " + e.Message);
        }
    }

    static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

    public static string LogPath => Path.Combine(AppSettings.DataDir, "play.log");

    internal static void Log(string line)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
        catch
        { /* logging must never stop a launch */
        }
    }
}
