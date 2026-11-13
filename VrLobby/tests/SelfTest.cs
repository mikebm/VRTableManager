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
        if (!ok)
        {
            _failures++;
        }
    }

    /// <summary>Cycles B / Tab until the filter is reached (also proves it's in the cycle).</summary>
    static void SetFilter(Main lobby, string filter)
    {
        for (
            var i = 0;
            i < LobbyNav.FiltersFor(lobby.State).Length && lobby.State.Filter != filter;
            i++
        )
        {
            lobby.CycleFilter();
        }
    }

    /// <summary>
    /// A collection made while the lobby is open, as the manager would: picked up, cycled to, and gone again when
    /// deleted. Only ever adds and removes its own collection, and only removes the file if it made it.
    /// </summary>
    static void CollectionsCheck(Main lobby, string collections)
    {
        const string name = "Self-test picks";
        var existed = File.Exists(collections);
        var picks = lobby.Shown.Take(2).Select(t => t.Id).ToList();
        TableCollections.Mutate(
            c =>
            {
                c.Delete(name); // left over from a run that stopped half-way
                c.Create(name);
                foreach (var id in picks)
                {
                    c.Set(name, id, true);
                }
            },
            collections
        );
        lobby.RefreshFromStats();
        SetFilter(lobby, name);
        Check(
            lobby.State.Filter == name
                && lobby
                    .Shown.Select(t => t.Id)
                    .OrderBy(x => x)
                    .SequenceEqual(picks.OrderBy(x => x))
                && lobby.StatusText.Contains(name),
            $"a collection made in the manager is a filter after the built-in ones ({lobby.Shown.Count} shown)"
        );
        TableCollections.Mutate(c => c.Delete(name), collections);
        lobby.RefreshFromStats();
        Check(
            lobby.State.Filter == "All" && lobby.Shown.Count > picks.Count,
            "deleting the shown collection in the manager goes back to All"
        );
        if (!existed && TableCollections.Load(collections).Collections.Count == 0)
        {
            File.Delete(collections);
        }
    }

    static async Task Frames(Main lobby, int n)
    {
        for (var i = 0; i < n; i++)
        {
            await lobby.ToSignal(lobby.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>Real time, not frames: headless runs frames far faster than 90 per second.</summary>
    static async Task Seconds(Main lobby, double s) =>
        await lobby.ToSignal(lobby.GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    /// <summary>
    /// The cabinet and picture caches hold every cabinet of the biggest ring. With room for only 14 cabinets, a ring
    /// of 15 or more read one table's cabinet, forgot another still on show, which hid and was read again, and so
    /// on: cabinets flickered or vanished and every read hitched the frame.
    /// </summary>
    static async Task BiggestRingKeepsEveryCabinet(Main lobby)
    {
        var count = LobbyLayout.MaxCount;
        var loader = new RealCabinetLoader
        {
            CacheDir = null,
            Keep = RealCabinetLoader.KeepFor(count),
        };
        lobby.AddChild(loader);
        // Tables that don't exist: each is answered at once as "no VR cabinet", and remembered like any other.
        var tables = Enumerable
            .Range(0, count)
            .Select(i => Path.Combine(OS.GetUserDataDir(), $"no-such-table-{i}.vpx"))
            .ToList();
        var answered = 0;
        loader.CabinetReady += (_, _) => answered++;
        foreach (var t in tables)
        {
            loader.Request(t);
        }
        for (var i = 0; i < 200 && answered < count; i++)
        {
            await Seconds(lobby, 0.02);
        }
        Check(
            answered == count
                && tables.All(loader.IsKnown)
                && Enumerable.All(LobbyLayout.Counts, n => ArtLoader.KeepFor(n) >= n * 2 + 1),
            $"a ring of {count} keeps every cabinet's table and pictures (no cabinet forgotten while on show)"
        );
        loader.QueueFree();
    }

    static float FrontDistance(Cabinet c) =>
        new Vector2(c.Position.X, c.Position.Z).Length() - CabinetKit.Length * c.Scale.Z / 2;

    public static async Task RunAsync(Main lobby)
    {
        var tree = lobby.GetTree();
        await Frames(lobby, 3);
        // A loading video for every table may be set on this PC (it lives in the real data folder): the loading
        // screen checks below expect the plain one, until the loading video checks turn it on.
        lobby.Settings.LobbyLoadingVideos = false;

        // A tiny attract sound next to Medieval Madness, written before anything can select that table
        // (the lobby remembers per table whether it has a sound).
        var mmIndex = lobby.Shown.FindIndex(t => t.Title == "Medieval Madness");
        var mmFolder = lobby.Shown[mmIndex].Source.Folder;
        Directory.CreateDirectory(Path.Combine(mmFolder, "music"));
        File.WriteAllBytes(Path.Combine(mmFolder, "music", "attract.wav"), TinyWav());
        // And a PinMAME .nv for its ROM with a Grand Champion in it (needs the NVRAM maps already cached).
        var nvWritten = WriteHighScoreNv(mmFolder, "mm_109c");
        // And a three-frame lobby video (red, green, blue).
        WriteLobbyVideo(mmFolder);
        // And a backglass picture of your own (solid blue), which wins over the catalog's.
        var myBackglass = Image.CreateEmpty(64, 48, false, Image.Format.Rgb8);
        myBackglass.Fill(new Color(0.1f, 0.2f, 0.9f));
        var artDir = Directory
            .CreateDirectory(UserArtwork.FolderFor(lobby.Shown[mmIndex].Source))
            .FullName;
        myBackglass.SavePng(Path.Combine(artDir, "backglass.png"));

        // Pinball FX / Pinball M appear only if they're installed on this PC.
        var files = lobby.Library.Count(t => t.Engine is not ("PFX" or "PM"));
        Check(
            files == 103,
            $"library: 100 VPX + hand-installed + 2 FP tables, empty folder skipped ({files})"
        );

        // ---- Cabinet ring ------------------------------------------------------------
        Check(
            lobby.Cabinets.Count == lobby.PoolSize && lobby.Cabinets.Count == 11,
            "11 pooled cabinets, however big the library"
        );
        Check(
            Mathf.IsEqualApprox(lobby.CabinetScale, 1f)
                && Mathf.IsEqualApprox(lobby.RingFront, 2.1f)
                && lobby.Cabinets.All(c => c.Scale.IsEqualApprox(Vector3.One)),
            "default cabinet size and count: the ring is as it always was (real size, 2.1 m away)"
        );
        await BiggestRingKeepsEveryCabinet(lobby);
        {
            var n = lobby.Shown.Count;
            var atStart = lobby
                .Cabinets.Where(c => c.Visible)
                .Select(c => c.Index)
                .OrderBy(i => i)
                .ToList();
            Check(
                atStart.SequenceEqual(Enumerable.Range(0, 6).Concat(Enumerable.Range(n - 5, 5))),
                "at the first table: 5 to its right, and the list's last 5 to its left (the ring goes round)"
            );
        }
        var sel = lobby.SelectedCabinet;
        Check(
            sel != null
                && Mathf.IsZeroApprox(sel.Position.X)
                && Mathf.IsEqualApprox(FrontDistance(sel), Main.SelectedFront),
            $"selected cabinet stands straight ahead, {Main.SelectedFront} m from you"
        );
        Check(lobby.RailItemCount == 27, "rail shows # and A–Z");

        lobby.JumpToKey('M');
        await Seconds(lobby, 1.5);
        var m = lobby.Shown[lobby.Selected];
        Check(m.Title == "Mars Attacks", "typing M jumps to the first M table: " + m.Title);
        var shownIdx = lobby
            .Cabinets.Where(c => c.Visible)
            .Select(c => c.Index)
            .OrderBy(i => i)
            .ToList();
        Check(
            shownIdx.Count == 11
                && shownIdx.SequenceEqual(Enumerable.Range(lobby.Selected - 5, 11)),
            "full ring: the selected table and 5 either side, each once"
        );
        sel = lobby.SelectedCabinet!;
        var others = lobby.Cabinets.Where(c => c.Visible && c != sel).ToList();
        Check(
            sel.Table == m
                && Mathf.IsZeroApprox(sel.Position.X)
                && others.All(c => FrontDistance(c) > FrontDistance(sel) + 1f),
            "selected cabinet is in front; the others wait on the ring behind it"
        );
        Check(
            others.All(c => c.Position.Y == 0f) && sel.Position.Y == 0f,
            "cabinets stand on the floor"
        );

        var before = lobby.Cabinets.ToDictionary(c => c.Slot, c => c.Index);
        lobby.Select(lobby.Selected + 1);
        await Seconds(lobby, 1.5);
        var reassigned = lobby.Cabinets.Count(c => before[c.Slot] != c.Index);
        Check(
            reassigned == 1,
            $"one step re-uses the pool: only one cabinet changed table ({reassigned})"
        );

        // ---- Round the end of the list -------------------------------------------------
        {
            var n = lobby.Shown.Count;
            var back = lobby.Selected;
            lobby.Select(n - 1);
            await Seconds(lobby, 1.5);
            var atEnd = lobby.Cabinets.ToDictionary(c => c.Slot, c => c.Index);
            lobby.Step(1);
            await Seconds(lobby, 1.5);
            var moved = lobby.Cabinets.Count(c => atEnd[c.Slot] != c.Index);
            var sel0 = lobby.SelectedCabinet;
            Check(
                lobby.Selected == 0
                    && moved == 1
                    && sel0 != null
                    && Mathf.IsZeroApprox(sel0.Position.X),
                $"→ on the last table goes round to the first, one step like any other ({moved} re-assigned)"
            );
            lobby.Step(-1);
            await Seconds(lobby, 1.5);
            Check(lobby.Selected == n - 1, "← on the first table goes back round to the last");
            lobby.Select(back);
            await Seconds(lobby, 1.5);
        }

        // ---- Art -----------------------------------------------------------------------
        for (var i = 0; i < 600 && !lobby.BackglassIndexReady; i++)
        {
            await Frames(lobby, 1);
        }

        Check(lobby.BackglassIndexReady, "backglass index loaded from the cached VPS catalog");
        sel = lobby.SelectedCabinet!;
        Check(
            sel.BackglassUrl == null && sel.ShowsTitlePlate,
            "no backglass on VPS: a lit title plate with the table's name instead"
        );
        Check(
            sel.LampColour == Palette.Lamp
                && lobby
                    .Cabinets.Where(c => c.Visible && c != sel)
                    .All(c => c.LampColour == Palette.Insert(c.Table!.Badge)),
            "selected cabinet glows lamp amber, the others their insert colour (also after the backglass index arrives)"
        );
        var red = Image.CreateEmpty(8, 8, false, Image.Format.Rgb8);
        red.Fill(new Color(0.9f, 0.1f, 0.1f));
        sel.SetBackglass(ImageTexture.CreateFromImage(red), ArtLoader.Average(red));
        sel.BodyColour.ToHsv(out var hue, out var sat, out _);
        Check(
            sel.HasBackglassArt
                && !sel.ShowsTitlePlate
                && (hue < 0.05f || hue > 0.95f)
                && sat > 0.2f,
            "backglass art replaces the plate and tints the cabinet sides to match (red art → red sides)"
        );
        var tall = Image.CreateEmpty(1200, 2400, false, Image.Format.Rgb8);
        ArtLoader.Shrink(tall, ArtLoader.MaxSide);
        Check(
            tall.GetWidth() == 512 && tall.GetHeight() == 1024,
            "art is capped at 1024 px on the longest side"
        );
        DxtEncoderChecks();

        // ---- The tables' own VR cabinets -----------------------------------------------
        // The bundled default cabinet (Medieval Madness's) is there from the start, table installed or not.
        var standard = lobby.TemplateCabinet;
        GD.Print(
            $"      default cabinet: {standard?.Surfaces} surfaces, {standard?.Textures} textures, size {standard?.Bounds.Size}"
        );
        Check(
            standard != null
                && standard.Surfaces > 3
                && standard.Textures > 0
                && standard.Bounds.Size.Y is > 1.7f and < 2.2f
                && standard.PlayfieldSize.X is > 0.45f and < 0.6f,
            "the bundled default cabinet loads: real-size, textured, with its playfield located"
        );
        // The fixture's .vpx files are empty, so their cabinets can't be read: they wear the default one.
        await Seconds(lobby, 1.0);
        Check(
            lobby
                .Cabinets.Where(c => c.Visible)
                .All(c => c.ShowsRealCabinet && c.Borrowed && c.Real == standard),
            "tables without a VR cabinet in their .vpx wear the default cabinet, never the drawn one"
        );
        var realVpx = Directory.Exists(@"C:\Users\mikem\VPinball\Tables")
            ? Directory
                .GetFiles(@"C:\Users\mikem\VPinball\Tables", "*.vpx", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.Contains("Attack from Mars"))
            : null;
        if (realVpx != null)
        {
            // A cache folder of its own, empty, so the first load reads the table and the second the cache.
            var cacheDir = Path.Combine(
                OS.GetUserDataDir(),
                "selftest-fixture",
                "cabinet-cache-afm"
            );
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }

            async Task<(RealCabinet? Cab, long Ms, int Hits)> LoadReal()
            {
                var loader = new RealCabinetLoader { CacheDir = cacheDir };
                lobby.AddChild(loader);
                RealCabinet? cab = null;
                var finished = false;
                loader.CabinetReady += (_, c) =>
                {
                    cab = c;
                    finished = true;
                };
                loader.Request(realVpx);
                for (var i = 0; i < 600 && !finished; i++)
                {
                    await Seconds(lobby, 0.05);
                }

                var result = (cab, loader.LastLoadMs, loader.CacheHits);
                loader.QueueFree();
                return result;
            }
            var (got, coldMs, _) = await LoadReal();
            GD.Print(
                $"      Attack from Mars cabinet: {got?.Surfaces} surfaces, {got?.Textures} textures ({got?.CompressedTextures} DXT-compressed), "
                    + $"size {got?.Bounds.Size}, playfield {got?.PlayfieldSize} at {got?.PlayfieldCentre}, {coldMs} ms"
            );
            Check(
                got != null
                    && got.Surfaces > 3
                    && got.Bounds.Size.Y is > 1.7f and < 2.2f
                    && got.PlayfieldSize.X is > 0.45f and < 0.6f,
                "a real table's VR cabinet builds in Godot: real-size, with its playfield located"
            );
            Check(
                got != null && got.CompressedTextures == got.Textures,
                "cabinet textures are GPU-compressed"
            );

            // The next lobby run (a new loader) gets the same cabinet from the disk cache.
            var (again, warmMs, hits) = await LoadReal();
            GD.Print(
                $"      from the cabinet cache: {warmMs} ms (first read {coldMs} ms), {new FileInfo(CabinetCache.FileFor(realVpx, cacheDir)).Length / 1024} KB"
            );
            Check(
                hits == 1
                    && again != null
                    && got != null
                    && again.Surfaces == got.Surfaces
                    && again.Textures == got.Textures
                    && again.CompressedTextures == got.CompressedTextures
                    && again.Kinds.SequenceEqual(got.Kinds)
                    && again.BackglassSurface == got.BackglassSurface
                    && again.PlayfieldCentre == got.PlayfieldCentre
                    && again.Bounds == got.Bounds
                    && again.Backbox == got.Backbox
                    && again.Mesh.SurfaceGetArrayLen(0) == got.Mesh.SurfaceGetArrayLen(0),
                "a second load comes from the cabinet cache, identical"
            );
            Check(warmMs < coldMs, $"and is faster ({warmMs} ms vs {coldMs} ms)");

            // Any cabinet can be the one tables without their own borrow, with their own art.
            lobby.SetTemplate(got);
            var shown = lobby.Cabinets.Where(c => c.Visible).ToList();
            Check(
                shown.All(c => c.ShowsRealCabinet && c.Borrowed && c.Real == got),
                "tables without a VR cabinet borrow the template cabinet"
            );
        }
        lobby.SetTemplate(null);
        Check(
            lobby.Cabinets.Where(c => c.Visible).All(c => !c.ShowsRealCabinet && !c.Borrowed),
            "default cabinet file missing: the drawn cabinet, as a last resort"
        );
        lobby.SetTemplate(standard);

        // ---- Random --------------------------------------------------------------------
        var was = lobby.Selected;
        var differs = true;
        for (var i = 0; i < 20; i++)
        {
            var prev = lobby.Selected;
            lobby.SelectRandom();
            differs &=
                lobby.Selected != prev && lobby.Selected >= 0 && lobby.Selected < lobby.Shown.Count;
        }
        Check(
            differs && lobby.Selected != was,
            "Random always picks a different table from the list"
        );

        // ---- Attract sound (the tiny WAV written at the start) ----------------------------
        lobby.Select(mmIndex);
        await Seconds(lobby, 2.5); // ring settles (~0.8 s), then the sound waits 0.8 s
        Check(
            lobby.AttractPlaying?.EndsWith("attract.wav") == true,
            $"selected table's own attract sound plays after a moment (found: {LobbyArt.AttractSound(lobby.Shown[mmIndex]) ?? "none"}, playing: {lobby.AttractPlaying ?? "nothing"})"
        );
        if (nvWritten)
        {
            Check(
                lobby.ScoresText.StartsWith("GRAND CHAMPION  VRT  1,234,567,890"),
                $"high scores from the table's pinmame/nvram .nv, as DMD lines ({lobby.ScoresText.Split('\n')[0]})"
            );
        }
        else
        {
            GD.Print("SKIP  high scores (no cached NVRAM map for mm_109c)");
        }

        var mmCab = lobby.SelectedCabinet!;
        mmCab.BodyColour.ToHsv(out var myHue, out var mySat, out _);
        Check(
            mmCab.BackglassUrl?.StartsWith(UserArtwork.LocalScheme) == true
                && mmCab.HasBackglassArt
                && !mmCab.ShowsTitlePlate
                && myHue is > 0.55f and < 0.75f
                && mySat > 0.2f,
            $"a backglass picture of your own (the table's artwork folder) is shown instead of the catalog's (blue art → blue sides; {mmCab.BackglassUrl})"
        );
        Check(
            lobby.VideoPlaying?.EndsWith("lobby-video") == true && mmCab.ShowsPlayfieldVideo,
            $"selected table's lobby video plays on its playfield (playing: {lobby.VideoPlaying ?? "nothing"})"
        );
        var seen = new HashSet<string>();
        for (var i = 0; i < 12; i++)
        {
            if (mmCab.PlayfieldTextureColour() is { } c)
            {
                seen.Add(
                    c.R > 0.5f ? "r"
                    : c.G > 0.5f ? "g"
                    : c.B > 0.5f ? "b"
                    : "?"
                );
            }

            await Seconds(lobby, 0.05);
        }
        // The headless (dummy) renderer keeps a texture's first picture, so colours are only checked with a real one.
        var headless = DisplayServer.GetName() == "headless";
        Check(
            lobby.VideoFramesShown > 6 && (headless || seen.IsSupersetOf(["r", "g", "b"])),
            $"its frames advance and loop ({lobby.VideoFramesShown} shown{(headless ? "" : ", colours " + string.Join("", seen.Order()))})"
        );
        // Launching freezes the video on its picture (keeping any playfield depth) instead of going flat.
        lobby.FreezeVideoForTest();
        var frozenAt = lobby.VideoFramesShown;
        await Seconds(lobby, 0.3);
        Check(
            lobby.VideoPlaying == null
                && mmCab.ShowsPlayfieldVideo
                && lobby.VideoFramesShown == frozenAt,
            "launching freezes the video on its last picture, not the flat still playfield"
        );
        lobby.Select(mmIndex + 1);
        Check(lobby.AttractPlaying == null, "and stops when you move on");
        Check(
            lobby.VideoPlaying == null && !mmCab.ShowsPlayfieldVideo,
            "the video stops too, and the cabinet shows its still playfield again"
        );
        // Back on the table (as after a table closes: the ring comes back through the same selection update).
        lobby.Select(mmIndex);
        await Seconds(lobby, 1.5);
        Check(
            lobby.VideoPlaying?.EndsWith("lobby-video") == true
                && lobby.SelectedCabinet?.ShowsPlayfieldVideo == true,
            $"coming back to a frozen table plays its video again (playing: {lobby.VideoPlaying ?? "nothing"})"
        );
        lobby.Select(mmIndex + 1);
        await Seconds(lobby, 0.5);
        Check(lobby.ScoresText == "", "no high scores shown for a table without an .nv file");

        // ---- Filters -------------------------------------------------------------------
        lobby.CycleFilter();
        Check(
            lobby.State.Filter == "VPX" && lobby.Shown.All(t => t.Engine == "VPX"),
            "B / Tab cycles to the VPX filter"
        );
        Check(
            lobby.FlashTimeLeft > 2.0 && lobby.FlashTimeLeft <= Main.NameFlashSeconds,
            $"the filter's name stays up long enough to read ({lobby.FlashTimeLeft:F1} s)"
        );
        SetFilter(lobby, "Manufacturer");
        Check(
            lobby.RailItemCount is > 0 and <= 9 && lobby.Groups[0].Label == "Bally",
            "manufacturer rail shows maker groups"
        );
        lobby.JumpToGroup(1);
        Check(
            lobby.FlashTimeLeft <= Main.LetterFlashSeconds,
            $"a rail group's name (a maker) flashes briefly, like a letter ({lobby.FlashTimeLeft:F1} s)"
        );
        SetFilter(lobby, "All");

        // ---- Collections from the manager ----------------------------------------------
        // Made while the lobby is open, as the manager would: picked up, cycled to, and gone again when deleted.
        // Only in the self-test's own fixture: run against your settings (--settings without --state), the
        // collections file beside the state is your real one, and this mustn't touch it.
        var collections = lobby.State.CollectionsPath;
        var fixture =
            Path.GetFullPath(Path.Combine(OS.GetUserDataDir(), "selftest-fixture"))
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(collections).StartsWith(fixture, StringComparison.OrdinalIgnoreCase))
        {
            GD.Print(
                $"SKIP  collections (the lobby state isn't the self-test's own: {collections})"
            );
        }
        else
        {
            CollectionsCheck(lobby, collections);
        }

        // ---- Height --------------------------------------------------------------------
        Check(Mathf.IsZeroApprox(lobby.RigHeight), "rig starts at the saved height (default 0)");
        lobby.SetHeight(0.12);
        lobby.SaveHeight();
        Check(
            Mathf.IsEqualApprox(lobby.RigHeight, 0.12f),
            $"raising 12 cm moves the rig ({lobby.RigHeight:F2} m)"
        );
        Check(
            LobbyState.Load(lobby.State.Path).HeightOffset == 0.12,
            "height saved to the lobby state file"
        );
        lobby.SetHeight(5);
        Check(
            Mathf.IsEqualApprox(lobby.RigHeight, (float)LobbyHeight.Max),
            "height clamped to +60 cm"
        );
        lobby.ResetHeight();
        Check(
            Mathf.IsZeroApprox(lobby.RigHeight)
                && LobbyState.Load(lobby.State.Path).HeightOffset == 0,
            "reset puts it back and saves"
        );

        // ---- Shared favourites / plays ---------------------------------------------------
        Check(
            LobbyNav.Filters.Contains("Pinball M") && LobbyNav.Filters.Contains("Most played"),
            "filters include Pinball M and Most played"
        );
        Check(lobby.StatusText.Contains("Not played yet"), "unplayed table says so");
        SetFilter(lobby, "Most played");
        Check(
            lobby.Shown.Count == 0
                && lobby.HeaderText == "No tables here"
                && lobby.StatusText.Contains("No plays counted yet")
                && lobby.Cabinets.All(c => !c.Visible),
            "empty Most played explains itself and shows no cabinets"
        );
        Check(!lobby.RefreshFromStats(), "no re-read while the stats file is unchanged");

        var mm = lobby.Library.First(t => t.Title == "Medieval Madness");
        var afm = lobby.Library.First(t => t.Title == "Attack from Mars");
        var now = DateTime.Now;
        PlayStats.Mutate(
            st => // what the manager and two --play sessions would write
            {
                st.RecordPlay(mm.Id, now.AddHours(-3), TimeSpan.FromMinutes(20));
                st.RecordPlay(mm.Id, now.AddMinutes(-30), TimeSpan.FromMinutes(25));
                st.RecordPlay(afm.Id, now.AddDays(-1), TimeSpan.FromMinutes(5));
                st.SetFavourite(afm.Id, true);
            },
            lobby.State.StatsPath
        );
        Check(lobby.RefreshFromStats(), "picks up stats written by another process while open");
        Check(
            lobby
                .Shown.Select(t => t.Title)
                .SequenceEqual(["Medieval Madness", "Attack from Mars"]),
            "Most played: most plays first"
        );
        Check(
            lobby.StatusText.Contains("Played 2×")
                && lobby.StatusText.Contains("45 min")
                && lobby.StatusText.Contains("last played 30 min ago"),
            "status shows plays, time and last played: " + lobby.StatusText.Split('\n')[1]
        );

        SetFilter(lobby, "Favourites");
        Check(
            lobby.Shown.Count == 1
                && lobby.Shown[0] == afm
                && lobby.SelectedCabinet?.ShowsFavourite == true
                && lobby.HeaderText.StartsWith("★"),
            "favourite set in the manager shows in the lobby (★ over the cabinet)"
        );
        lobby.ToggleFavourite();
        Check(
            lobby.Shown.Count == 0 && !PlayStats.Load(lobby.State.StatsPath).IsFavourite(afm.Id),
            "unfavouriting in the lobby writes the shared file"
        );

        SetFilter(lobby, "Pinball M");
        Check(
            lobby.Shown.All(t => t.Engine == "PM" && t.Badge == "M")
                && (
                    lobby.Shown.Count > 0 || lobby.StatusText.Contains("Pinball M isn't installed")
                ),
            lobby.Shown.Count > 0
                ? "Pinball M filter shows the installed game, badged M"
                : "Pinball M filter explains it isn't installed"
        );
        if (lobby.Shown.Count > 0)
        {
            Check(
                lobby.SelectedCabinet?.PlayfieldUrl == null,
                "store games put their landscape header on the backglass, not the playfield"
            );
        }

        SetFilter(lobby, "All");

        // Loading screen: only the picked table stays, with its title and a running count; a failed launch
        // brings the ring back with the reason, instead of the lobby closing into an empty headset.
        {
            var statusFile = Path.Combine(OS.GetUserDataDir(), "selftest-play-status.json");
            PlayStatus.Clear(statusFile);
            var picked = lobby.Shown[lobby.Selected];
            var cabinetsBefore = lobby.VisibleCabinets;
            lobby.StartLoadingForTest(statusFile);
            await Seconds(lobby, 1.2);
            Check(
                lobby.IsLoading
                    && lobby.HeaderText == picked.Title
                    && lobby.StatusText.StartsWith("Loading")
                    && lobby.StatusText.Contains("1 s"),
                "loading screen shows the table and how long it's been"
            );
            Check(
                !lobby.RailVisible && lobby.VisibleCabinets == 1,
                "only the picked cabinet stays while it loads"
            );
            PlayStatus.Write(statusFile, PlayStatus.Failed, "VPX wasn't found");
            await Seconds(lobby, 0.6);
            Check(
                !lobby.IsLoading && lobby.StatusText.Contains("didn't start: VPX wasn't found"),
                "a failed launch says why, back in the lobby"
            );
            PlayStats.Mutate(st => st.RecordPlay(picked.Id, DateTime.Now), lobby.State.StatsPath);
            lobby.RefreshFromStats();
            Check(
                lobby.StatusText.Contains("didn't start"),
                "the error stays through a stats refresh"
            );
            Check(
                lobby.RailVisible && lobby.VisibleCabinets == cabinetsBefore,
                "the ring and rail come back"
            );
            lobby.StartLoadingForTest(statusFile);
            PlayStatus.Write(statusFile, PlayStatus.Exited);
            await Seconds(lobby, 0.6);
            Check(
                !lobby.IsLoading && lobby.StatusText.Contains("closed while it was loading"),
                "a table that closes while loading is reported"
            );

            // Staying open behind the table: everything hidden while it plays, then back on the same table,
            // without the old error, once it closes.
            var played = lobby.Shown[lobby.Selected];
            lobby.StartLoadingForTest(statusFile);
            PlayStatus.Write(statusFile, PlayStatus.Running);
            lobby.StartPlayingForTest();
            await Seconds(lobby, 0.8);
            Check(
                lobby.IsPlaying && lobby.IsLoading && !lobby.RoomVisible && !lobby.RailVisible,
                "the room is hidden while the table plays"
            );
            PlayStatus.Write(statusFile, PlayStatus.Exited);
            await Seconds(lobby, 0.8);
            Check(
                !lobby.IsPlaying
                    && !lobby.IsLoading
                    && lobby.RoomVisible
                    && lobby.RailVisible
                    && lobby.VisibleCabinets == cabinetsBefore,
                "the room comes back when the table closes"
            );
            Check(
                lobby.Shown[lobby.Selected] == played
                    && lobby.HeaderText.EndsWith(played.Title)
                    && !lobby.StatusText.Contains("closed while"),
                "back on the table you played, with no old error"
            );
            PlayStatus.Clear(statusFile);
        }

        // Wheel art and the loading video, on Medieval Madness.
        {
            var mmAt = lobby.Shown.FindIndex(t => t.Title == "Medieval Madness");
            var mmDir = lobby.Shown[mmAt].Source.Folder;
            // A wheel saved from a media pack: an orange bar on a transparent background.
            var wheel = Image.CreateEmpty(200, 80, false, Image.Format.Rgba8);
            wheel.Fill(new Color(1, 1, 1, 0));
            wheel.FillRect(new Rect2I(20, 20, 160, 40), new Color(1, 0.5f, 0));
            var wheelFile = Path.Combine(
                Directory.CreateDirectory(Path.Combine(mmDir, "medias")).FullName,
                WheelArt.Prefix + "Medieval Madness.png"
            );
            wheel.SavePng(wheelFile);
            lobby.ForgetWheelLookups(); // it was looked up (none) when the tests above passed it
            lobby.Select(mmAt == 0 ? 1 : 0);
            lobby.Select(mmAt);
            await Seconds(lobby, 1.0);
            Check(
                lobby.WheelKey?.Contains(WheelArt.Prefix) == true && lobby.WheelShowing,
                "a saved wheel shows over the selected table's title"
            );
            Check(
                lobby.WheelTransparent && lobby.WheelAboveTitle,
                "the wheel keeps its transparency and sits above the title"
            );
            lobby.Select(mmAt + 1);
            await Seconds(lobby, 0.3);
            Check(!lobby.WheelShowing, "a table without wheel art shows only its title");
            lobby.Select(mmAt);
            await Seconds(lobby, 0.5);

            // Its own loading video (three frames and a sound) plays on the loading screen's big screen.
            var lv = LoadingVideo.TableFolder(mmDir);
            Directory.CreateDirectory(lv);
            Color[] colours = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
            for (var i = 0; i < colours.Length; i++)
            {
                var img = Image.CreateEmpty(64, 36, false, Image.Format.Rgb8);
                img.Fill(colours[i]);
                File.WriteAllBytes(LobbyVideo.FramePath(lv, i), img.SaveJpgToBuffer(0.9f));
            }
            File.WriteAllText(
                Path.Combine(lv, LobbyVideo.InfoFile),
                System.Text.Json.JsonSerializer.Serialize(
                    new LobbyVideo.Info(LoadingVideo.Fps, colours.Length, 64, 36, "test")
                )
            );
            File.WriteAllBytes(Path.Combine(lv, LoadingVideo.AudioName + ".wav"), TinyWav());

            var statusFile = Path.Combine(OS.GetUserDataDir(), "selftest-play-status.json");
            PlayStatus.Clear(statusFile);
            var cabinetsBefore = lobby.VisibleCabinets;
            lobby.Settings.LobbyLoadingVideos = true;
            lobby.StartLoadingForTest(statusFile);
            await Seconds(lobby, 0.8);
            Check(
                lobby.LoadingVideoPlaying == lv
                    && lobby.LoadingVideoFrames > colours.Length
                    && lobby.VisibleCabinets == 0
                    && lobby.HeaderText == lobby.Shown[mmAt].Title,
                $"the loading video plays, looping, on a screen where the cabinet stood ({lobby.LoadingVideoFrames} frames)"
            );
            Check(lobby.LoadingVideoSound, "with its sound");
            Check(lobby.WheelShowing, "the wheel stays over the title while it loads");
            PlayStatus.Write(statusFile, PlayStatus.Failed, "VPX wasn't found");
            await Seconds(lobby, 0.6);
            Check(
                lobby.LoadingVideoPlaying == null
                    && !lobby.LoadingVideoSound
                    && lobby.VisibleCabinets == cabinetsBefore,
                "the video and its sound stop when the loading screen ends"
            );

            PlayStatus.Clear(statusFile); // the failure above is still in it
            lobby.Settings.LobbyLoadingVideos = false;
            lobby.StartLoadingForTest(statusFile);
            await Seconds(lobby, 0.4);
            Check(
                lobby.LoadingVideoPlaying == null && lobby.VisibleCabinets == 1,
                "turned off: only the table and its lamps"
            );
            PlayStatus.Write(statusFile, PlayStatus.Failed, "VPX wasn't found");
            await Seconds(lobby, 0.6);
            PlayStatus.Clear(statusFile);

            // A broken loading video (no frame decodes) gives up and puts the table back, instead of a black screen.
            for (var i = 0; i < colours.Length; i++)
            {
                File.WriteAllText(LobbyVideo.FramePath(lv, i), "not a jpeg");
            }
            lobby.Settings.LobbyLoadingVideos = true;
            lobby.StartLoadingForTest(statusFile);
            await Seconds(lobby, 0.8);
            Check(
                lobby.LoadingVideoPlaying == null && lobby.VisibleCabinets == 1 && lobby.IsLoading,
                "a loading video that won't decode gives way to the plain loading screen"
            );
            PlayStatus.Write(statusFile, PlayStatus.Failed, "VPX wasn't found");
            await Seconds(lobby, 0.6);
            PlayStatus.Clear(statusFile);
            lobby.Settings.LobbyLoadingVideos = false;
            LoadingVideo.Remove(lv);
            File.Delete(wheelFile);
        }

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
            var index = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(HighScores.MapsDir, "index.json"))
            );
            var map = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(HighScores.MapsDir, (string)index![rom]!))
            );
            var gc = map!["high_scores"]![0]!;
            int Addr(string key) =>
                gc[key]!["start"]!.ToString() is var s && s.StartsWith("0x")
                    ? Convert.ToInt32(s, 16)
                    : int.Parse(s);
            var nv = new byte[0x2000 + 6];
            "VRT"u8.CopyTo(nv.AsSpan(Addr("initials")));
            var score = new byte[] { 0x00, 0x12, 0x34, 0x56, 0x78, 0x90 };
            var len = (int)gc["score"]!["length"]!;
            score.AsSpan(score.Length - len).CopyTo(nv.AsSpan(Addr("score")));
            Directory.CreateDirectory(Path.Combine(folder, "pinmame", "nvram"));
            File.WriteAllBytes(Path.Combine(folder, "pinmame", "nvram", rom + ".nv"), nv);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The lobby's own DXT encoder (what an exported lobby compresses cabinet art with): the right format, every
    /// mipmap level, and a picture that decodes back close to the original, colour and alpha.
    /// </summary>
    static void DxtEncoderChecks()
    {
        // 70×45: not a multiple of 4, so the edge blocks are padded. Smooth gradients, a hard edge, soft alpha.
        const int w = 70,
            h = 45;
        var src = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                src.SetPixel(
                    x,
                    y,
                    new Color(
                        x / (float)w,
                        y / (float)h,
                        x < w / 2 ? 0.1f : 0.9f,
                        0.25f + 0.75f * x / w
                    )
                );
            }
        }
        src.GenerateMipmaps();

        static (double colour, double alpha) MeanError(Image a, Image b)
        {
            double c = 0,
                al = 0;
            for (var y = 0; y < a.GetHeight(); y++)
            {
                for (var x = 0; x < a.GetWidth(); x++)
                {
                    Color p = a.GetPixel(x, y),
                        q = b.GetPixel(x, y);
                    c +=
                        (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B)) / 3 * 255;
                    al += Math.Abs(p.A - q.A) * 255;
                }
            }
            var n = a.GetWidth() * a.GetHeight();
            return (c / n, al / n);
        }

        var dxt5 = DxtEncoder.Compress(src, alpha: true);
        var back5 = (Image)dxt5.Duplicate();
        back5.Decompress();
        back5.Convert(Image.Format.Rgba8);
        var (c5, a5) = MeanError(src, back5);
        Check(
            dxt5.GetFormat() == Image.Format.Dxt5 && dxt5.HasMipmaps() && back5.GetWidth() == w,
            "own DXT encoder: a picture with alpha becomes DXT5, mipmaps kept"
        );
        Check(
            c5 < 4 && a5 < 2,
            $"own DXT encoder: DXT5 decodes close to the original (colour {c5:0.0}, alpha {a5:0.0} of 255)"
        );

        var opaque = (Image)src.Duplicate();
        opaque.Convert(Image.Format.Rgb8);
        var dxt1 = DxtEncoder.Compress(opaque, alpha: false);
        var back1 = (Image)dxt1.Duplicate();
        back1.Decompress();
        back1.Convert(Image.Format.Rgba8);
        var (c1, _) = MeanError(src, back1);
        Check(
            dxt1.GetFormat() == Image.Format.Dxt1 && dxt1.HasMipmaps() && c1 < 4,
            $"own DXT encoder: an opaque picture becomes DXT1, close to the original (colour {c1:0.0} of 255)"
        );
    }

    /// <summary>medias/lobby-video with three solid-colour frames at 15 fps.</summary>
    static void WriteLobbyVideo(string folder)
    {
        var dir = LobbyVideo.Folder(folder);
        Directory.CreateDirectory(dir);
        Color[] colours = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        for (var i = 0; i < colours.Length; i++)
        {
            var img = Image.CreateEmpty(36, 64, false, Image.Format.Rgb8);
            img.Fill(colours[i]);
            File.WriteAllBytes(LobbyVideo.FramePath(dir, i), img.SaveJpgToBuffer(0.9f));
        }
        File.WriteAllText(
            Path.Combine(dir, LobbyVideo.InfoFile),
            System.Text.Json.JsonSerializer.Serialize(
                new LobbyVideo.Info(LobbyVideo.Fps, colours.Length, 36, 64, "test")
            )
        );
    }

    /// <summary>0.2 s of 8-bit mono silence as a valid WAV file.</summary>
    static byte[] TinyWav()
    {
        const int rate = 8000,
            samples = 1600;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + samples);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate);
        w.Write((short)1);
        w.Write((short)8);
        w.Write("data"u8);
        w.Write(samples);
        for (var i = 0; i < samples; i++)
        {
            w.Write((byte)128);
        }

        w.Flush();
        return ms.ToArray();
    }
}
