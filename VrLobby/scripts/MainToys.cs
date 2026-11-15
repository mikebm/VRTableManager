using Godot;

namespace VrLobby;

// Every table in view shows its own 3D toys on its cabinet (Setup: AppSettings.LobbyTableToys, off by default).
public partial class Main
{
    readonly TableToysLoader _toyLoader = new();
    bool _toysOn,
        _toyStats;
    double _toyStatsAt = 2;

    /// <summary>Toys shown on the selected cabinet (tests, diagnostics).</summary>
    public TableToys? ShownToys => SelectedCabinet?.Toys;

    /// <summary>Cabinets showing toys (tests, diagnostics).</summary>
    public int CabinetsShowingToys => _pool.Count(c => c.ShowsToys);

    // Reused every frame: the cabinets in view (the selected one first) and their tables.
    readonly List<Cabinet> _toyCabs = [];
    readonly List<string> _toyTables = [];

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
    /// Every frame, after the ring is laid out: every cabinet in view shows its table's toys, the selected one first.
    /// "In view" is the side videos' (<see cref="VideoStartAngle"/>, kept until <see cref="VideoStopAngle"/>, so
    /// glancing about at the edge doesn't drop and show them again). Tables not read yet are read once the ring has
    /// nearly stopped, the selected one first; toys already read stay on show while it scrolls, for as long as their
    /// cabinet keeps its table and stays in view. A cabinet out of view, or handed another table, lets its toys go.
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

        _toyCabs.Clear();
        _toyTables.Clear();
        if (!_launching)
        {
            var selected = SelectedCabinet;
            if (
                selected is { Visible: true, Loading: false, Table: { } st }
                && VpxFile(st) is { } svpx
            )
            {
                _toyCabs.Add(selected);
                _toyTables.Add(svpx);
            }

            foreach (var cab in _pool)
            {
                if (cab == selected || !cab.Visible || cab.Loading || cab.Table is not { } t)
                {
                    continue;
                }

                var angle = AngleFromView(cab);
                if (
                    (angle < VideoStartAngle || (cab.ShowsToys && angle < VideoStopAngle))
                    && VpxFile(t) is { } vpx
                )
                {
                    _toyCabs.Add(cab);
                    _toyTables.Add(vpx);
                }
            }
        }

        _toyLoader.Want(_toyTables, read: Mathf.Abs(_scroll - Target) < 0.5f);

        foreach (var cab in _pool)
        {
            var i = _toyCabs.IndexOf(cab);
            var want = i >= 0 ? _toyLoader.Get(_toyTables[i]) : null;
            if (cab.Toys != want)
            {
                cab.ShowToys(want);
            }
            else if (want != null)
            {
                cab.PlaceToys();
            }
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
        var shown = _pool.Where(c => c.ShowsToys).Select(c => c.Toys!).ToList();
        GD.Print(
            $"STATS toys {(shown.Count > 0 ? $"{shown.Count} cabinet(s), {shown.Sum(t => t.Vertices):N0} vertices" : "none")}: "
                + $"{Performance.GetMonitor(Performance.Monitor.TimeFps):0} fps, "
                + $"render cpu {RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp):0.0} ms, "
                + $"gpu {RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp):0.0} ms, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draws, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1e6:0.00} M triangles, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:0} MB video memory"
        );
    }
}
