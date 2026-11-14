using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// The lobby's fallback for attract music (Setup → VR lobby → Music, <see cref="AppSettings.LobbyRecordsRomAudio"/>):
/// the manager records it ahead of time (<see cref="RomAudioBatch"/>), and this records it for a ROM table you've
/// stopped on that still has none, through the same routine (<c>VrTableManager.exe --record-rom-audio</c>, below normal
/// priority). One at a time, and only the table you're on: moving on replaces what waits instead of piling up a queue,
/// and a recording that has started is left to finish (it's kept for next time). A recording killed when a table
/// starts leaves no "gives nothing" verdict, so it's simply tried again later.
/// </summary>
public partial class RomRecorder : Node
{
    /// <summary>A table's recording finished (the file may still be missing: the ROM gave nothing).</summary>
    public event Action<string>? Recorded;

    /// <summary>The manager, which records (null: no fallback).</summary>
    public string? ManagerExe { get; set; }

    public string PinmameDll { get; set; } = "";

    public int Seconds { get; set; } = RomAudio.DefaultSeconds;

    public bool Busy => _running != null;

    /// <summary>The table waiting to be recorded next: the one you're on, or none.</summary>
    LobbyTable? _waiting;
    readonly HashSet<string> _tried = [];
    CancellationTokenSource? _running;

    /// <summary>Set when the lobby closes: a recording that ends after that must not call back into a freed node.</summary>
    volatile bool _closing;

    /// <summary>Records this table next, in place of any waiting, if its ROM has no music yet.</summary>
    public void Request(LobbyTable t)
    {
        if (
            ManagerExe == null
            || _tried.Contains(t.Id)
            || RomAudio.RomOf(t.Source) is not { } rom
            || !RomAudio.NeedsRecording(t.Source, Seconds)
        )
        {
            return;
        }

        _waiting = t;
        Next();
    }

    void Next()
    {
        if (_running != null || _waiting is not { } t)
        {
            return;
        }

        _waiting = null;
        _tried.Add(t.Id);
        var cts = new CancellationTokenSource();
        _running = cts;
        var item = new RomAudioBatch.Item(RomAudio.RomOf(t.Source)!, t.Source);
        _ = RomAudioBatch
            .RecordInProcessAsync(ManagerExe!, item, Seconds, PinmameDll, cts.Token)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushWarning(
                        $"Couldn't record {t.Title}: {task.Exception?.InnerException?.Message}"
                    );
                }
                if (!_closing)
                {
                    Callable.From(() => Done(cts, t.Id)).CallDeferred();
                }
            });
    }

    void Done(CancellationTokenSource cts, string id)
    {
        if (_running != cts)
        {
            return;
        }

        _running = null;
        cts.Dispose();
        Recorded?.Invoke(id);
        Next();
    }

    /// <summary>Stops everything, e.g. when a table is about to start (it needs the CPU, and PinMAME).</summary>
    public void StopAll()
    {
        _waiting = null;
        if (_running == null)
        {
            return;
        }

        try
        {
            _running.Cancel(); // kills the recording process; nothing is remembered about the ROM
        }
        catch { }
        _running = null;
    }

    public override void _ExitTree()
    {
        _closing = true;
        StopAll();
    }
}
