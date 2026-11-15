using Godot;

namespace VrLobby;

// The selected table's own 3D toys on its cabinet (Setup: AppSettings.LobbyTableToys, off by default).
public partial class Main
{
    readonly TableToysLoader _toyLoader = new();
    bool _toysOn,
        _toyStats;
    Cabinet? _toyCabinet;
    double _toyStatsAt = 2;

    /// <summary>Toys shown on the selected cabinet (tests, diagnostics).</summary>
    public TableToys? ShownToys => _toyCabinet?.Toys;

    /// <summary>
    /// --toys turns them on whatever Setup says; --toy-budget=&lt;vertices&gt; changes the budget; --toy-stats prints the
    /// frame's draw calls, triangles and video memory every 2 s (to compare with and without).
    /// </summary>
    void SetupToys(Dictionary<string, string> args)
    {
        _toysOn = Settings.LobbyTableToys || args.ContainsKey("toys");
        _toyStats = args.ContainsKey("toy-stats");
        _toyLoader.CacheLimitBytes = VrTableManager.Lobby.ToyFiles.Bytes(Settings.LobbyToyCacheGb);
        if (int.TryParse(args.GetValueOrDefault("toy-budget"), out var budget) && budget > 0)
        {
            _toyLoader.Budget = budget;
        }

        if (!_toysOn)
        {
            return;
        }

        // Setup offers toys or parallax, not both: with toys, no playfield gets a height map (a table without toys, or
        // over the budget, plays its video flat), and none are read or built.
        _noDepth = true;
        AddChild(_toyLoader);
        _toyLoader.ToysReady += (vpx, _) => Trace("toys " + Path.GetFileNameWithoutExtension(vpx));
    }

    /// <summary>
    /// Every frame, after the ring is laid out: the selected cabinet (once the ring has nearly stopped on it) shows its
    /// table's toys, read the first time; any other cabinet that had them lets them go.
    /// </summary>
    void UpdateToys(double delta)
    {
        if (_toyStats)
        {
            PrintToyStats(delta);
        }

        if (!_toysOn)
        {
            return;
        }

        TableToys? want = null;
        var cab = SelectedCabinet;
        if (
            cab is { Table: { } t }
            && !_launching
            && Mathf.Abs(_scroll - Target) < 0.5f
            && VpxFile(t) is { } vpx
        )
        {
            if (_toyLoader.IsKnown(vpx))
            {
                _toyLoader.Touch(vpx); // on show: reading others must never forget it
                want = _toyLoader.Get(vpx);
            }
            else
            {
                _toyLoader.Request(vpx);
            }
        }

        if (_toyCabinet != null && (_toyCabinet != cab || want == null))
        {
            _toyCabinet.ShowToys(null);
            _toyCabinet = null;
        }

        if (want != null && cab != null)
        {
            if (cab.Toys != want)
            {
                cab.ShowToys(want);
            }
            else
            {
                cab.PlaceToys();
            }

            _toyCabinet = cab;
        }
    }

    void PrintToyStats(double delta)
    {
        _toyStatsAt -= delta;
        if (_toyStatsAt > 0)
        {
            return;
        }

        _toyStatsAt = 2;
        var vp = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vp, true);
        GD.Print(
            $"STATS toys {(ShownToys is { } s ? $"{s.Vertices:N0} vertices" : "none")}: "
                + $"{Performance.GetMonitor(Performance.Monitor.TimeFps):0} fps, "
                + $"render cpu {RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp):0.0} ms, "
                + $"gpu {RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp):0.0} ms, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draws, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1e6:0.00} M triangles, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:0} MB video memory"
        );
    }
}
