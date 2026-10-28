using System.Diagnostics;
using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// Records tables' ROM sound (<see cref="RomAudio"/>) in the background, one at a time, each in a
/// headless copy of the lobby (--record-audio): PinMAME runs in real time for about 50 s, and a ROM
/// that crashes the emulator then takes down only that copy. Each table is tried once per lobby run;
/// a result (or a ROM that gave nothing) is kept on disk, so it's never recorded again.
/// </summary>
public partial class RomRecorder : Node
{
    /// <summary>A table's recording finished (the file may still be missing: the ROM gave nothing).</summary>
    public event Action<string>? Recorded;

    public string? SettingsPath { get; set; }
    public bool Busy => _running != null;

    readonly LinkedList<LobbyTable> _queue = [];
    readonly HashSet<string> _tried = [];
    Process? _running;

    /// <summary>Records this table next (ahead of any waiting) if it has a ROM that hasn't been recorded.</summary>
    public void Request(LobbyTable t)
    {
        if (!_tried.Add(t.Id) || !RomAudio.NeedsRecording(t.Source)) return;
        _queue.AddFirst(t);
        Next();
    }

    void Next()
    {
        if (_running != null || _queue.Count == 0) return;
        var t = _queue.First!.Value;
        _queue.RemoveFirst();
        var psi = new ProcessStartInfo(OS.GetExecutablePath()) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--headless");
        if (!OS.HasFeature("template")) { psi.ArgumentList.Add("--path"); psi.ArgumentList.Add(ProjectSettings.GlobalizePath("res://")); }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--record-audio=" + t.Id);
        if (!string.IsNullOrEmpty(SettingsPath)) psi.ArgumentList.Add("--settings=" + SettingsPath);
        try
        {
            var p = Process.Start(psi);
            if (p == null) return;
            _running = p;
            _ = p.WaitForExitAsync().ContinueWith(_ => Callable.From(() => Done(p, t.Id)).CallDeferred());
        }
        catch (Exception e)
        {
            GD.PushWarning($"Couldn't record {t.Title}: {e.Message}");
        }
    }

    void Done(Process p, string id)
    {
        if (_running != p) return;
        _running = null;
        p.Dispose();
        Recorded?.Invoke(id);
        Next();
    }

    /// <summary>Stops everything, e.g. when a table is about to start (it needs the CPU, and PinMAME).</summary>
    public void StopAll()
    {
        _queue.Clear();
        if (_running == null) return;
        try { _running.Kill(entireProcessTree: true); } catch { }
        _running = null;
    }

    public override void _ExitTree() => StopAll();
}
