using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// In-engine checks of the lobby scene (layout, rail, views), run with:
/// godot --headless --path VrLobby -- --selftest
/// It builds its own 100-table fixture unless VRLOBBY_SETTINGS points at one.
/// The navigation logic itself is tested with plain dotnet in SmokeTest.
/// </summary>
public static class SelfTest
{
    static int _failures;

    static void Check(bool ok, string what)
    {
        GD.Print((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) _failures++;
    }

    /// <summary>Cycles B / Tab until the filter is reached (also proves it's in the cycle).</summary>
    static void SetFilter(Main lobby, string filter)
    {
        for (var i = 0; i < LobbyNav.Filters.Length && lobby.State.Filter != filter; i++) lobby.CycleFilter();
    }

    public static async Task RunAsync(Main lobby)
    {
        var tree = lobby.GetTree();
        for (var i = 0; i < 3; i++) await lobby.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        // Pinball FX / Pinball M appear only if they're installed on this PC.
        var files = lobby.Library.Count(t => t.Engine is not ("PFX" or "PM"));
        Check(files == 103, $"library: 100 VPX + hand-installed + 2 FP tables, empty folder skipped ({files})");
        Check(lobby.Cards.Count == lobby.Shown.Count && lobby.Shown.Count == lobby.Library.Count, "one card per table");
        Check(lobby.Cards.Count(c => c.Visible) == 6, "ring shows the selection plus 5 ahead");
        Check(lobby.RailItemCount == 27, "rail shows # and A–Z");

        lobby.JumpToKey('M');
        var m = lobby.Shown[lobby.Selected];
        Check(m.Title == "Mars Attacks", "typing M jumps to the first M table: " + m.Title);

        lobby.ToggleView();
        for (var i = 0; i < 60; i++) await lobby.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var visible = lobby.Cards.Count(c => c.Visible);
        Check(lobby.Shown[lobby.Selected] == m && visible is >= 24 and <= 27, $"wall keeps the selection and shows a 3×9 grid ({visible} visible)");
        lobby.ToggleView();

        lobby.CycleFilter();
        Check(lobby.State.Filter == "VPX" && lobby.Shown.All(t => t.Engine == "VPX"), "B / Tab cycles to the VPX filter");
        while (lobby.State.Filter != "Manufacturer") lobby.CycleFilter();
        Check(lobby.RailItemCount is > 0 and <= 9 && lobby.Groups[0].Label == "Bally", "manufacturer rail shows maker groups");
        while (lobby.State.Filter != "All") lobby.CycleFilter();

        // Height: the rig moves, the value is clamped, saved to the state file, and reset clears it.
        Check(Mathf.IsZeroApprox(lobby.RigHeight), "rig starts at the saved height (default 0)");
        lobby.SetHeight(0.12);
        lobby.SaveHeight();
        Check(Mathf.IsEqualApprox(lobby.RigHeight, 0.12f), $"raising 12 cm moves the rig ({lobby.RigHeight:F2} m)");
        Check(LobbyState.Load(lobby.State.Path).HeightOffset == 0.12, "height saved to the lobby state file");
        lobby.SetHeight(5);
        Check(Mathf.IsEqualApprox(lobby.RigHeight, (float)LobbyHeight.Max), "height clamped to +60 cm");
        lobby.ResetHeight();
        Check(Mathf.IsZeroApprox(lobby.RigHeight) && LobbyState.Load(lobby.State.Path).HeightOffset == 0, "reset puts it back and saves");

        // Shared favourites / plays (play-stats.json, also written by the manager and VrTableManager --play).
        Check(LobbyNav.Filters.Contains("Pinball M") && LobbyNav.Filters.Contains("Most played"), "filters include Pinball M and Most played");
        Check(lobby.StatusText.Contains("Not played yet"), "unplayed table says so");
        SetFilter(lobby, "Most played");
        Check(lobby.Shown.Count == 0 && lobby.HeaderText == "No tables here" && lobby.StatusText.Contains("No plays counted yet"),
            "empty Most played explains itself");
        Check(!lobby.RefreshFromStats(), "no re-read while the stats file is unchanged");

        var mm = lobby.Library.First(t => t.Title == "Medieval Madness");
        var afm = lobby.Library.First(t => t.Title == "Attack from Mars");
        var now = DateTime.Now;
        PlayStats.Mutate(st =>   // what the manager and two --play sessions would write
        {
            st.RecordPlay(mm.Id, now.AddHours(-3), TimeSpan.FromMinutes(20));
            st.RecordPlay(mm.Id, now.AddMinutes(-30), TimeSpan.FromMinutes(25));
            st.RecordPlay(afm.Id, now.AddDays(-1), TimeSpan.FromMinutes(5));
            st.SetFavourite(afm.Id, true);
        }, lobby.State.StatsPath);
        Check(lobby.RefreshFromStats(), "picks up stats written by another process while open");
        Check(lobby.Shown.Select(t => t.Title).SequenceEqual(["Medieval Madness", "Attack from Mars"]), "Most played: most plays first");
        Check(lobby.StatusText.Contains("Played 2×") && lobby.StatusText.Contains("45 min") && lobby.StatusText.Contains("last played 30 min ago"),
            "status shows plays, time and last played: " + lobby.StatusText.Split('\n')[1]);

        SetFilter(lobby, "Favourites");
        Check(lobby.Shown.Count == 1 && lobby.Shown[0] == afm && lobby.Cards[0].ShowsFavourite && lobby.HeaderText.StartsWith("★"),
            "favourite set in the manager shows in the lobby");
        lobby.ToggleFavourite();
        Check(lobby.Shown.Count == 0 && !PlayStats.Load(lobby.State.StatsPath).IsFavourite(afm.Id), "unfavouriting in the lobby writes the shared file");

        SetFilter(lobby, "Pinball M");
        Check(lobby.Shown.All(t => t.Engine == "PM" && t.Badge == "M")
              && (lobby.Shown.Count > 0 || lobby.StatusText.Contains("Pinball M isn't installed")),
            lobby.Shown.Count > 0 ? "Pinball M filter shows the installed game, badged M" : "Pinball M filter explains it isn't installed");
        SetFilter(lobby, "All");

        GD.Print(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        tree.Quit(_failures);
    }
}
