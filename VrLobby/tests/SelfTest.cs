using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// In-engine checks of the lobby scene (cabinet ring, rail, art, filters, height), run with:
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

    static async Task Frames(Main lobby, int n)
    {
        for (var i = 0; i < n; i++) await lobby.ToSignal(lobby.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Real time, not frames: headless runs frames far faster than 90 per second.</summary>
    static async Task Seconds(Main lobby, double s) =>
        await lobby.ToSignal(lobby.GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    static float FrontDistance(Cabinet c) => new Vector2(c.Position.X, c.Position.Z).Length() - CabinetKit.Length / 2;

    public static async Task RunAsync(Main lobby)
    {
        var tree = lobby.GetTree();
        await Frames(lobby, 3);

        // A tiny attract sound next to Medieval Madness, written before anything can select that table
        // (the lobby remembers per table whether it has a sound).
        var mmIndex = lobby.Shown.FindIndex(t => t.Title == "Medieval Madness");
        var mmFolder = lobby.Shown[mmIndex].Source.Folder;
        Directory.CreateDirectory(Path.Combine(mmFolder, "music"));
        File.WriteAllBytes(Path.Combine(mmFolder, "music", "attract.wav"), TinyWav());
        // And a PinMAME .nv for its ROM with a Grand Champion in it (needs the NVRAM maps already cached).
        var nvWritten = WriteHighScoreNv(mmFolder, "mm_109c");

        // Pinball FX / Pinball M appear only if they're installed on this PC.
        var files = lobby.Library.Count(t => t.Engine is not ("PFX" or "PM"));
        Check(files == 103, $"library: 100 VPX + hand-installed + 2 FP tables, empty folder skipped ({files})");

        // ---- Cabinet ring ------------------------------------------------------------
        Check(lobby.Cabinets.Count == Main.PoolSize && lobby.Cabinets.Count == 11, "11 pooled cabinets, however big the library");
        Check(lobby.Cabinets.Count(c => c.Visible) == 6, "at the first table: it plus 5 to its right (nothing to the left)");
        var sel = lobby.SelectedCabinet;
        Check(sel != null && Mathf.IsZeroApprox(sel.Position.X) && Mathf.IsEqualApprox(FrontDistance(sel), Main.SelectedFront),
            $"selected cabinet stands straight ahead, {Main.SelectedFront} m from you");
        Check(lobby.RailItemCount == 27, "rail shows # and A–Z");

        lobby.JumpToKey('M');
        await Seconds(lobby, 1.5);
        var m = lobby.Shown[lobby.Selected];
        Check(m.Title == "Mars Attacks", "typing M jumps to the first M table: " + m.Title);
        var shownIdx = lobby.Cabinets.Where(c => c.Visible).Select(c => c.Index).OrderBy(i => i).ToList();
        Check(shownIdx.Count == 11 && shownIdx.SequenceEqual(Enumerable.Range(lobby.Selected - 5, 11)),
            "full ring: the selected table and 5 either side, each once");
        sel = lobby.SelectedCabinet!;
        var others = lobby.Cabinets.Where(c => c.Visible && c != sel).ToList();
        Check(sel.Table == m && Mathf.IsZeroApprox(sel.Position.X) && others.All(c => FrontDistance(c) > FrontDistance(sel) + 1f),
            "selected cabinet is in front; the others wait on the ring behind it");
        Check(others.All(c => c.Position.Y == 0f) && sel.Position.Y == 0f, "cabinets stand on the floor");

        var before = lobby.Cabinets.ToDictionary(c => c.Slot, c => c.Index);
        lobby.Select(lobby.Selected + 1);
        await Seconds(lobby, 1.5);
        var reassigned = lobby.Cabinets.Count(c => before[c.Slot] != c.Index);
        Check(reassigned == 1, $"one step re-uses the pool: only one cabinet changed table ({reassigned})");

        // ---- Art -----------------------------------------------------------------------
        for (var i = 0; i < 600 && !lobby.BackglassIndexReady; i++) await Frames(lobby, 1);
        Check(lobby.BackglassIndexReady, "backglass index loaded from the cached VPS catalog");
        sel = lobby.SelectedCabinet!;
        Check(sel.BackglassUrl == null && sel.ShowsTitlePlate, "no backglass on VPS: a lit title plate with the table's name instead");
        Check(sel.LampColour == Palette.Lamp && lobby.Cabinets.Where(c => c.Visible && c != sel).All(c => c.LampColour == Palette.Insert(c.Table!.Badge)),
            "selected cabinet glows lamp amber, the others their insert colour (also after the backglass index arrives)");
        var red = Image.CreateEmpty(8, 8, false, Image.Format.Rgb8);
        red.Fill(new Color(0.9f, 0.1f, 0.1f));
        sel.SetBackglass(ImageTexture.CreateFromImage(red), ArtLoader.Average(red));
        sel.BodyColour.ToHsv(out var hue, out var sat, out _);
        Check(sel.HasBackglassArt && !sel.ShowsTitlePlate && (hue < 0.05f || hue > 0.95f) && sat > 0.2f,
            "backglass art replaces the plate and tints the cabinet sides to match (red art → red sides)");
        var tall = Image.CreateEmpty(1200, 2400, false, Image.Format.Rgb8);
        ArtLoader.Shrink(tall, ArtLoader.MaxSide);
        Check(tall.GetWidth() == 512 && tall.GetHeight() == 1024, "art is capped at 1024 px on the longest side");

        // ---- The tables' own VR cabinets -----------------------------------------------
        // The bundled default cabinet (Medieval Madness's) is there from the start, table installed or not.
        var standard = lobby.TemplateCabinet;
        GD.Print($"      default cabinet: {standard?.Surfaces} surfaces, {standard?.Textures} textures, size {standard?.Bounds.Size}");
        Check(standard != null && standard.Surfaces > 3 && standard.Textures > 0 && standard.Bounds.Size.Y is > 1.7f and < 2.2f
              && standard.PlayfieldSize.X is > 0.45f and < 0.6f,
            "the bundled default cabinet loads: real-size, textured, with its playfield located");
        // The fixture's .vpx files are empty, so their cabinets can't be read: they wear the default one.
        await Seconds(lobby, 1.0);
        Check(lobby.Cabinets.Where(c => c.Visible).All(c => c.ShowsRealCabinet && c.Borrowed && c.Real == standard),
            "tables without a VR cabinet in their .vpx wear the default cabinet, never the drawn one");
        var realVpx = Directory.Exists(@"C:\Users\mikem\VPinball\Tables")
            ? Directory.GetFiles(@"C:\Users\mikem\VPinball\Tables", "*.vpx", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("Attack from Mars"))
            : null;
        if (realVpx != null)
        {
            // A cache folder of its own, empty, so the first load reads the table and the second the cache.
            var cacheDir = Path.Combine(OS.GetUserDataDir(), "selftest-fixture", "cabinet-cache-afm");
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
            async Task<(RealCabinet? Cab, long Ms, int Hits)> LoadReal()
            {
                var loader = new RealCabinetLoader { CacheDir = cacheDir };
                lobby.AddChild(loader);
                RealCabinet? cab = null;
                var finished = false;
                loader.CabinetReady += (_, c) => { cab = c; finished = true; };
                loader.Request(realVpx);
                for (var i = 0; i < 600 && !finished; i++) await Seconds(lobby, 0.05);
                var result = (cab, loader.LastLoadMs, loader.CacheHits);
                loader.QueueFree();
                return result;
            }
            var (got, coldMs, _) = await LoadReal();
            GD.Print($"      Attack from Mars cabinet: {got?.Surfaces} surfaces, {got?.Textures} textures ({got?.CompressedTextures} DXT-compressed), " +
                     $"size {got?.Bounds.Size}, playfield {got?.PlayfieldSize} at {got?.PlayfieldCentre}, {coldMs} ms");
            Check(got != null && got.Surfaces > 3 && got.Bounds.Size.Y is > 1.7f and < 2.2f && got.PlayfieldSize.X is > 0.45f and < 0.6f,
                "a real table's VR cabinet builds in Godot: real-size, with its playfield located");
            Check(got != null && got.CompressedTextures == got.Textures, "cabinet textures are GPU-compressed");

            // The next lobby run (a new loader) gets the same cabinet from the disk cache.
            var (again, warmMs, hits) = await LoadReal();
            GD.Print($"      from the cabinet cache: {warmMs} ms (first read {coldMs} ms), {new FileInfo(CabinetCache.FileFor(realVpx, cacheDir)).Length / 1024} KB");
            Check(hits == 1 && again != null && got != null && again.Surfaces == got.Surfaces && again.Textures == got.Textures
                  && again.CompressedTextures == got.CompressedTextures && again.Kinds.SequenceEqual(got.Kinds)
                  && again.BackglassSurface == got.BackglassSurface && again.PlayfieldCentre == got.PlayfieldCentre
                  && again.Bounds == got.Bounds && again.Backbox == got.Backbox
                  && again.Mesh.SurfaceGetArrayLen(0) == got.Mesh.SurfaceGetArrayLen(0),
                "a second load comes from the cabinet cache, identical");
            Check(warmMs < coldMs, $"and is faster ({warmMs} ms vs {coldMs} ms)");

            // Any cabinet can be the one tables without their own borrow, with their own art.
            lobby.SetTemplate(got);
            var shown = lobby.Cabinets.Where(c => c.Visible).ToList();
            Check(shown.All(c => c.ShowsRealCabinet && c.Borrowed && c.Real == got),
                "tables without a VR cabinet borrow the template cabinet");
        }
        lobby.SetTemplate(null);
        Check(lobby.Cabinets.Where(c => c.Visible).All(c => !c.ShowsRealCabinet && !c.Borrowed),
            "default cabinet file missing: the drawn cabinet, as a last resort");
        lobby.SetTemplate(standard);

        // ---- Random --------------------------------------------------------------------
        var was = lobby.Selected;
        var differs = true;
        for (var i = 0; i < 20; i++)
        {
            var prev = lobby.Selected;
            lobby.SelectRandom();
            differs &= lobby.Selected != prev && lobby.Selected >= 0 && lobby.Selected < lobby.Shown.Count;
        }
        Check(differs && lobby.Selected != was, "Random always picks a different table from the list");

        // ---- Attract sound (the tiny WAV written at the start) ----------------------------
        lobby.Select(mmIndex);
        await Seconds(lobby, 2.5); // ring settles (~0.8 s), then the sound waits 0.8 s
        Check(lobby.AttractPlaying?.EndsWith("attract.wav") == true,
            $"selected table's own attract sound plays after a moment (found: {LobbyArt.AttractSound(lobby.Shown[mmIndex]) ?? "none"}, playing: {lobby.AttractPlaying ?? "nothing"})");
        if (nvWritten)
            Check(lobby.ScoresText.StartsWith("GRAND CHAMPION  VRT  1,234,567,890"),
                $"high scores from the table's pinmame/nvram .nv, as DMD lines ({lobby.ScoresText.Split('\n')[0]})");
        else
            GD.Print("SKIP  high scores (no cached NVRAM map for mm_109c)");
        lobby.Select(mmIndex + 1);
        Check(lobby.AttractPlaying == null, "and stops when you move on");
        await Seconds(lobby, 0.5);
        Check(lobby.ScoresText == "", "no high scores shown for a table without an .nv file");

        // ---- Filters -------------------------------------------------------------------
        lobby.CycleFilter();
        Check(lobby.State.Filter == "VPX" && lobby.Shown.All(t => t.Engine == "VPX"), "B / Tab cycles to the VPX filter");
        SetFilter(lobby, "Manufacturer");
        Check(lobby.RailItemCount is > 0 and <= 9 && lobby.Groups[0].Label == "Bally", "manufacturer rail shows maker groups");
        SetFilter(lobby, "All");

        // ---- Height --------------------------------------------------------------------
        Check(Mathf.IsZeroApprox(lobby.RigHeight), "rig starts at the saved height (default 0)");
        lobby.SetHeight(0.12);
        lobby.SaveHeight();
        Check(Mathf.IsEqualApprox(lobby.RigHeight, 0.12f), $"raising 12 cm moves the rig ({lobby.RigHeight:F2} m)");
        Check(LobbyState.Load(lobby.State.Path).HeightOffset == 0.12, "height saved to the lobby state file");
        lobby.SetHeight(5);
        Check(Mathf.IsEqualApprox(lobby.RigHeight, (float)LobbyHeight.Max), "height clamped to +60 cm");
        lobby.ResetHeight();
        Check(Mathf.IsZeroApprox(lobby.RigHeight) && LobbyState.Load(lobby.State.Path).HeightOffset == 0, "reset puts it back and saves");

        // ---- Shared favourites / plays ---------------------------------------------------
        Check(LobbyNav.Filters.Contains("Pinball M") && LobbyNav.Filters.Contains("Most played"), "filters include Pinball M and Most played");
        Check(lobby.StatusText.Contains("Not played yet"), "unplayed table says so");
        SetFilter(lobby, "Most played");
        Check(lobby.Shown.Count == 0 && lobby.HeaderText == "No tables here" && lobby.StatusText.Contains("No plays counted yet")
              && lobby.Cabinets.All(c => !c.Visible), "empty Most played explains itself and shows no cabinets");
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
        Check(lobby.Shown.Count == 1 && lobby.Shown[0] == afm && lobby.SelectedCabinet?.ShowsFavourite == true && lobby.HeaderText.StartsWith("★"),
            "favourite set in the manager shows in the lobby (★ over the cabinet)");
        lobby.ToggleFavourite();
        Check(lobby.Shown.Count == 0 && !PlayStats.Load(lobby.State.StatsPath).IsFavourite(afm.Id), "unfavouriting in the lobby writes the shared file");

        SetFilter(lobby, "Pinball M");
        Check(lobby.Shown.All(t => t.Engine == "PM" && t.Badge == "M")
              && (lobby.Shown.Count > 0 || lobby.StatusText.Contains("Pinball M isn't installed")),
            lobby.Shown.Count > 0 ? "Pinball M filter shows the installed game, badged M" : "Pinball M filter explains it isn't installed");
        if (lobby.Shown.Count > 0)
            Check(lobby.SelectedCabinet?.PlayfieldUrl == null, "store games put their landscape header on the backglass, not the playfield");
        SetFilter(lobby, "All");

        GD.Print(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        tree.Quit(_failures);
    }

    /// <summary>
    /// Writes &lt;folder&gt;/pinmame/nvram/&lt;rom&gt;.nv with "VRT 1,234,567,890" as Grand Champion, at the
    /// addresses the cached NVRAM map gives (WPC: NVRAM at address 0). False when the map isn't cached.
    /// </summary>
    static bool WriteHighScoreNv(string folder, string rom)
    {
        try
        {
            var index = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(HighScores.MapsDir, "index.json")));
            var map = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(HighScores.MapsDir, (string)index![rom]!)));
            var gc = map!["high_scores"]![0]!;
            int Addr(string key) => gc[key]!["start"]!.ToString() is var s && s.StartsWith("0x") ? Convert.ToInt32(s, 16) : int.Parse(s);
            var nv = new byte[0x2000 + 6];
            "VRT"u8.CopyTo(nv.AsSpan(Addr("initials")));
            var score = new byte[] { 0x00, 0x12, 0x34, 0x56, 0x78, 0x90 };
            var len = (int)gc["score"]!["length"]!;
            score.AsSpan(score.Length - len).CopyTo(nv.AsSpan(Addr("score")));
            Directory.CreateDirectory(Path.Combine(folder, "pinmame", "nvram"));
            File.WriteAllBytes(Path.Combine(folder, "pinmame", "nvram", rom + ".nv"), nv);
            return true;
        }
        catch { return false; }
    }

    /// <summary>0.2 s of 8-bit mono silence as a valid WAV file.</summary>
    static byte[] TinyWav()
    {
        const int rate = 8000, samples = 1600;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate); w.Write((short)1); w.Write((short)8);
        w.Write("data"u8); w.Write(samples);
        for (var i = 0; i < samples; i++) w.Write((byte)128);
        w.Flush();
        return ms.ToArray();
    }
}
