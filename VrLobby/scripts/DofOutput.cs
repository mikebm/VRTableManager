using System.Diagnostics;
using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// The lobby's DirectOutput (DOF): the DofBridge helper, a .NET Framework program that runs the player's own DOF (a
/// .NET Framework library this .NET 10 lobby can't load), fed the lobby's menu events (<see cref="LobbyDof"/>) as
/// lines on its stdin. DOF holds the cabinet's output boards while it runs, so it's stopped before a table starts
/// (the table's own DOF needs them) and started again when the lobby has the headset back.
/// </summary>
public sealed class DofOutput
{
    /// <summary>
    /// How long a stop may take before the helper is killed. DOF reads "quit" only once it has finished starting, and
    /// finishing turns every output off, so this allows for a slow start as well as the finish.
    /// </summary>
    const int StopWaitMs = 8000;

    Process? _bridge;
    LobbyDof.Install? _install;
    bool _looked;
    Task? _stopping; // a stop in progress: DOF hasn't let go of the boards yet
    AppSettings? _startAfterStop; // a start asked for meanwhile, done once it has
    volatile bool _quiet; // shutting down: the helper's last lines aren't logged

    /// <summary>False in the self-test, which may run on the player's real settings: nothing ever starts.</summary>
    public bool Enabled { get; set; } = true;

    public bool Running => _bridge is { HasExited: false };

    /// <summary>
    /// Starts DOF, when Setup turns it on and DOF is installed. Does nothing if it's already running; while a stop
    /// is still letting go of the boards, it starts once that's done, so two DOFs never hold them at once.
    /// </summary>
    public void Start(AppSettings settings)
    {
        if (!Enabled || !settings.LobbyDof || Running)
        {
            return;
        }

        if (_stopping is { IsCompleted: false })
        {
            _startAfterStop = settings;
            return;
        }

        if (!_looked)
        {
            _looked = true;
            _install = LobbyDof.Find();
            GD.Print(
                _install != null
                    ? $"DOF: {_install.Folder} ({(_install.Is64 ? "64" : "32")}-bit)"
                    : "DOF: not installed (no DirectOutput.dll registered or in C:\\DirectOutput)"
            );
        }

        if (_install == null)
        {
            return;
        }

        var exe = BridgeExe(_install.Is64);
        if (exe == null)
        {
            GD.PushWarning("DOF: the DofBridge helper isn't next to the lobby; DOF stays off");
            return;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            WorkingDirectory = _install.Folder,
        };
        psi.ArgumentList.Add("--dof");
        psi.ArgumentList.Add(_install.Folder);
        psi.ArgumentList.Add("--rom");
        psi.ArgumentList.Add(LobbyDof.Rom);
        try
        {
            var p = Process.Start(psi)!;
            p.StandardInput.AutoFlush = true;
            p.OutputDataReceived += (_, e) =>
            {
                if (!_quiet && !string.IsNullOrEmpty(e.Data))
                {
                    GD.Print("DOF: " + e.Data);
                }
            };
            p.BeginOutputReadLine();
            _bridge = p;
        }
        catch (Exception e)
        {
            GD.PushWarning("DOF: couldn't start DofBridge: " + e.Message);
        }
    }

    /// <summary>Pulses a menu event (E800 …) on the cabinet. Nothing when DOF isn't running.</summary>
    public void Pulse(int element)
    {
        if (!Running)
        {
            return;
        }

        GD.Print($"DOF: E{element}"); // like the button presses: what was sent, for when nothing fires
        try
        {
            _bridge!.StandardInput.WriteLine($"E {element}");
        }
        catch (Exception e)
        {
            GD.PushWarning($"DOF: lost the helper ({e.Message})");
            Forget();
        }
    }

    /// <summary>
    /// Stops DOF and waits until it has let go of the output boards, killing the helper if it hangs: a table starting
    /// while the lobby still holds them would find its own DOF can't open them.
    /// </summary>
    public Task StopAsync()
    {
        _startAfterStop = null; // whoever stops wants it off
        var p = _bridge;
        if (p == null)
        {
            return _stopping ?? Task.CompletedTask;
        }

        _bridge = null;
        var stop = StopAndWait(p);
        _stopping = stop;
        return Continue(stop);
    }

    async Task Continue(Task stop)
    {
        await stop;
        if (_startAfterStop is { } s && !Running)
        {
            _startAfterStop = null;
            Start(s);
        }
    }

    static async Task StopAndWait(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.StandardInput.WriteLine("quit");
                using var cts = new CancellationTokenSource(StopWaitMs);
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    GD.PushWarning($"DOF: helper still running after {StopWaitMs} ms; stopping it");
                    p.Kill();
                }
            }
        }
        catch (Exception e)
        {
            GD.PushWarning("DOF: stopping the helper: " + e.Message);
        }
        finally
        {
            p.Dispose();
        }
    }

    /// <summary>On the way out: closing its stdin makes the helper finish DOF and exit by itself.</summary>
    public void Stop()
    {
        _quiet = true; // the engine is shutting down: no more logging from the reader thread
        _startAfterStop = null;
        var p = _bridge;
        _bridge = null;
        try
        {
            p?.CancelOutputRead();
            p?.StandardInput.Close();
        }
        catch
        {
            // already gone
        }

        p?.Dispose();
    }

    void Forget()
    {
        try
        {
            _bridge?.Dispose();
        }
        catch
        {
            // already gone
        }

        _bridge = null;
    }

    /// <summary>
    /// DofBridge for this DOF's bitness: VrLobby\dof\x64 or \x86 in a release, or its build output beside the lobby's
    /// project when running from source.
    /// </summary>
    static string? BridgeExe(bool is64)
    {
        var arch = is64 ? "x64" : "x86";
        var exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        var candidates = new List<string> { Path.Combine(exeDir, "dof", arch, "DofBridge.exe") };
        if (!OS.HasFeature("template"))
        {
            var repo = Path.GetFullPath(
                Path.Combine(ProjectSettings.GlobalizePath("res://"), "..")
            );
            foreach (var config in new[] { "Release", "Debug" })
            {
                candidates.Add(
                    is64
                        ? Path.Combine(repo, "DofBridge", "bin", config, "net48", "DofBridge.exe")
                        : Path.Combine(
                            repo,
                            "DofBridge",
                            "bin",
                            "x86",
                            config,
                            "net48",
                            "DofBridge.exe"
                        )
                );
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
