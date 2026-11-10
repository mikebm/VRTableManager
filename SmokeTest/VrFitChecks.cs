using System.Text;
using VrTableManager.Services;

/// <summary>Fitting every VR table to the player (VrFit): lengths, the VR ini values, real-size launches.</summary>
static class VrFitChecks
{
    static byte[] Rec(string tag, byte[] data) =>
        [.. BitConverter.GetBytes(data.Length + 4), .. Encoding.ASCII.GetBytes(tag), .. data];

    static byte[] F32(float v) => BitConverter.GetBytes(v);

    /// <summary>A .vpx whose GameData has only the playfield's edges, in VPX units.</summary>
    static string Table(string dir, string name, float left, float right)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".vpx");
        using var cfb = OpenMcdf.RootStorage.Create(path);
        var gs = cfb.CreateStorage("GameStg");
        using var s = gs.CreateStream("GameData");
        byte[] data =
        [
            .. Rec("LEFT", F32(left)),
            .. Rec("TOPX", F32(0)),
            .. Rec("RGHT", F32(right)),
            .. Rec("BOTM", F32(2108)),
            .. Rec("ENDB", []),
        ];
        s.Write(data, 0, data.Length);
        return path;
    }

    static bool Near(double? a, double b, double tolerance = 0.05) =>
        a is { } x && Math.Abs(x - b) <= tolerance;

    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        void Check(bool ok, string name) => results.Add((ok, name));

        // Lengths as people type them.
        Check(Near(VrFit.ParseLength("178", false), 178), "fit: a bare number is centimetres");
        Check(Near(VrFit.ParseLength("1.78 m", false), 178), "fit: metres");
        Check(Near(VrFit.ParseLength("5'10\"", false), 177.8), "fit: feet and inches (5'10\")");
        Check(Near(VrFit.ParseLength("5 ft 10", true), 177.8), "fit: feet and inches (5 ft 10)");
        Check(
            Near(VrFit.ParseLength("70", true), 177.8),
            "fit: a bare number is inches in feet-and-inches mode"
        );
        Check(
            Near(VrFit.ParseLength("36 in", false), 91.44),
            "fit: inches with a unit, in cm mode"
        );
        Check(VrFit.ParseLength("tall", false) == null, "fit: nonsense isn't a length");
        Check(
            VrFit.Format(177.8, true) == "5'10\""
                && VrFit.Format(91.44, true) == "36\""
                && VrFit.Format(91.4, false) == "91 cm",
            "fit: lengths shown back in either unit"
        );
        Check(
            VrFit.LockbarHeightFor(175) == 91 && VrFit.LockbarHeightFor(300) == 110,
            "fit: lockbar height from your height (175 cm → 91 cm), kept in range"
        );

        // Writing the choice into the VR ini.
        var ini = Path.Combine(root, "vr fit", "vr.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(
            ini,
            "[Player]\nAAFactor = 1.2\nLockbarWidth = 80\n\n[PlayerVR]\nAskToTurnOn = 0\nOrientation = 21.3\nTableX = -0.04\nTableY = 19.8\nTableZ = 6\n"
        );
        VrFit.Write(
            ini,
            new VrFit.Choice(91, 15, VrFit.SizeReal, 57.1),
            resetPosition: false,
            VrFit.SizeReal
        );
        var v = IniFile.Read(ini);
        Check(
            v[("Player", "LockbarHeight")] == "91"
                && v[("Player", "ScreenPlayerY")] == "15"
                && v[("Player", "LockbarWidth")] == "80"
                && v[("Player", "AAFactor")] == "1.2",
            "fit: lockbar height and eyes 15 cm behind it (positive: VPX moves you back); a width set in VPX and other settings kept"
        );
        Check(
            v[("PlayerVR", "TableZ")] == ""
                && v[("PlayerVR", "TableY")] == "19.8"
                && v[("PlayerVR", "OverrideLockbarHeight")] == "1"
                && v[("PlayerVR", "AskToTurnOn")] == "0",
            "fit: the height offset is cleared, where you stand is kept unless asked"
        );
        VrFit.Write(
            ini,
            new VrFit.Choice(91, 15, VrFit.SizeSame, 64),
            resetPosition: true,
            VrFit.SizeReal
        );
        v = IniFile.Read(ini);
        Check(
            v[("Player", "LockbarWidth")] == "64"
                && v[("PlayerVR", "OverrideLockbarWidth")] == "1"
                && v[("PlayerVR", "TableY")] == "15"
                && v[("PlayerVR", "TableX")] == ""
                && v[("PlayerVR", "Orientation")] == "",
            "fit: one width for every table, and the table put straight ahead at the standing distance"
        );
        VrFit.Write(
            ini,
            new VrFit.Choice(91, 15, VrFit.SizeVpx, 64),
            resetPosition: false,
            VrFit.SizeSame
        );
        v = IniFile.Read(ini);
        Check(
            v[("Player", "LockbarWidth")] == "" && v[("PlayerVR", "OverrideLockbarWidth")] == "",
            "fit: leaving same-width mode clears the width it wrote"
        );

        // Real size: each table's own lockbar width, for its launch only.
        var tables = Path.Combine(root, "vr fit", "tables");
        var wpc = Table(tables, "wpc", 0, 952); // 20.25" playfield
        Check(
            Near(VpxCabinet.PlayfieldWidthCm(wpc), 51.4, 0.1),
            "fit: a table's playfield width read from its file (952 VPX units = 51.4 cm)"
        );
        Check(
            Near(VrFit.RealLockbarWidth(wpc), 57.1, 0.1),
            "fit: its real lockbar width is the playfield plus 2¼\""
        );
        // At launch: real-cabinet values until the player sets their own, and real size per table.
        Dictionary<(string, string), string> none = new();
        var (extra, notKept) = VrFit.LaunchSettings(new AppSettings(), none, wpc);
        string? Get(List<(string Section, string Key, string Value)> l, string key) =>
            l.Where(e => e.Key == key).Select(e => e.Value).FirstOrDefault();
        Check(
            Get(extra, "LockbarHeight") == "91"
                && Get(extra, "ScreenPlayerY") == "10"
                && Get(extra, "TableY") == "10"
                && Get(extra, "LockbarWidth") == "57.1"
                && notKept.SequenceEqual([("Player", "LockbarWidth"), ("PlayerVR", "TableZ")]),
            "fit: without settings of your own, a launch gets a real cabinet's lockbar height, eyes and size"
        );
        Dictionary<(string, string), string> mine = new()
        {
            [("Player", "LockbarHeight")] = "100",
            [("Player", "ScreenPlayerY")] = "-5",
        };
        (extra, _) = VrFit.LaunchSettings(new AppSettings(), mine, wpc);
        Check(
            Get(extra, "LockbarHeight") == null && Get(extra, "ScreenPlayerY") == null,
            "fit: your own lockbar height and eye position are left alone"
        );
        var own = Table(tables, "own", 0, 1100);
        File.WriteAllText(Path.ChangeExtension(own, ".ini"), "[Player]\nLockbarWidth = 70\n");
        (extra, notKept) = VrFit.LaunchSettings(new AppSettings(), mine, own);
        Check(
            Get(extra, "LockbarWidth") == null && notKept.SequenceEqual([("PlayerVR", "TableZ")]),
            "fit: a table's own .ini width is left to VPX"
        );

        // Every table's height is 0 at launch (an old shared height is dropped), and never kept.
        (extra, notKept) = VrFit.LaunchSettings(new AppSettings(), mine, wpc);
        Check(
            extra.Contains(("PlayerVR", "TableZ", ""))
                && notKept.Contains(("PlayerVR", "TableZ"))
                && !notKept.Contains(("PlayerVR", "TableY"))
                && !notKept.Contains(("PlayerVR", "TableX")),
            "fit: where you stand is saved for every table; height is 0 for every table and never kept"
        );

        // A position a table keeps for itself (VPX's "Save as Table Override"), read when it closes.
        var jaws = Table(tables, "jaws", 0, 952);
        Check(
            !VrFit.OwnPositionKeys(jaws).Any(),
            "fit: a table without its own position keeps nothing to itself"
        );

        // Saved during this play: the .ini only has it once the table closes.
        File.WriteAllText(
            Path.ChangeExtension(jaws, ".ini"),
            "[PlayerVR]\nTableZ = 6.0\nTableY = 12\nTableX = \n"
        );
        var ownKeys = VrFit.OwnPositionKeys(jaws).ToList();
        Check(
            ownKeys.Contains(("PlayerVR", "TableZ"))
                && ownKeys.Contains(("PlayerVR", "TableY"))
                && !ownKeys.Contains(("PlayerVR", "TableX"))
                && !ownKeys.Contains(("PlayerVR", "Orientation")),
            "fit: a position saved for a table during play isn't kept for every table (only the keys it sets)"
        );

        // Height moved in the headset: kept for that table only. Every table's height is "" (0), as at launch.
        var afm = Table(tables, "afm", 0, 1025);
        Dictionary<(string, string), string> P(string z, string y = "0.000000") =>
            new()
            {
                [("PlayerVR", "TableZ")] = z,
                [("PlayerVR", "TableX")] = "0.000000",
                [("PlayerVR", "TableY")] = y,
                [("PlayerVR", "Orientation")] = "0.000000",
            };
        var before = P("", "10");
        var log = VrFit.KeepTableHeight(afm, before, P("6.000000", "10.000000"));
        Check(
            VrFit.ReadTableHeight(afm) == 6 && log != null,
            "fit: raising a table in the headset saves that height for it alone"
        );
        Check(
            VrFit.KeepTableHeight(afm, before, P("6.0", "10")) == null
                && VrFit.ReadTableHeight(afm) == 6,
            "fit: playing it again without moving keeps its height"
        );
        VrFit.KeepTableHeight(afm, before, P("0.000000", "23.4"));
        Check(
            VrFit.ReadTableHeight(afm) == 6,
            "fit: recentering (height to 0, and where you stand moves) keeps a table's height"
        );
        VrFit.KeepTableHeight(afm, before, P("2.5", "10"));
        Check(VrFit.ReadTableHeight(afm) == 2.5, "fit: moving it again saves the new height");
        VrFit.KeepTableHeight(afm, before, P("0.000000", "10.000000"));
        Check(
            VrFit.ReadTableHeight(afm) == null,
            "fit: lowering it back to 0 in the headset (nothing else moved) clears its own height"
        );
        VrFit.WriteTableHeight(afm, 4);
        VrFit.WriteTableHeight(afm, null);
        Check(VrFit.ReadTableHeight(afm) == null, "fit: a table's VR height can be cleared");
        Check(
            VrFit.KeepTableHeight(wpc, before, P("", "10")) == null
                && VrFit.ReadTableHeight(wpc) == null,
            "fit: a table nobody moved gets no height of its own"
        );

        // Saving back: a failed table write doesn't lose the rest, and a second caller waits for the first.
        var layersRoot = Path.Combine(root, "vr fit", "layers");
        Directory.CreateDirectory(layersRoot);
        var s2 = new AppSettings
        {
            VpxDir = Path.Combine(layersRoot, "vpx"),
            TablesDir = Path.Combine(layersRoot, "tables"),
        };
        Directory.CreateDirectory(s2.VpxDir);
        // Not your own VPinballX.ini: an empty preferences folder.
        var oldPrefs = Environment.GetEnvironmentVariable("VRTM_VPX_PREFS");
        Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", Path.Combine(layersRoot, "prefs"));
        VpxSetup.EnsureVrIni(s2);
        var play = VpxIniLayers.Build(
            s2,
            vr: true,
            path: Path.Combine(layersRoot, "play.ini"),
            notKept: [("PlayerVR", "TableZ")],
            afterPlay: new VpxIniLayers.AfterPlay(
                NotKept: () => [("PlayerVR", "Orientation")],
                After: (_, _) => throw new IOException("locked")
            )
        );
        IniFile.SetMany(
            play.Path,
            "PlayerVR",
            [("TableY", "14.5"), ("TableZ", "6"), ("Orientation", "12")]
        );
        var kept = play.SaveBack();
        var vrIni = IniFile.Read(VpxSetup.VrIniPath(s2));
        Check(
            kept == 1
                && vrIni.GetValueOrDefault(("PlayerVR", "TableY")) == "14.5"
                && vrIni.GetValueOrDefault(("PlayerVR", "TableZ"), "") == ""
                && vrIni.GetValueOrDefault(("PlayerVR", "Orientation"), "") == ""
                && play.TakeNotes().Any(n => n.Contains("locked")),
            "fit: a table's own settings failing to save doesn't lose where you stand, and is logged"
        );
        Check(
            play.SaveBack() == 0 && play.TakeNotes().Count == 0,
            "fit: saving back runs once, and its notes are logged once"
        );
        Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", oldPrefs);
        (extra, _) = VrFit.LaunchSettings(
            new AppSettings { VrTableSizing = VrFit.SizeSame },
            mine,
            wpc
        );
        Check(
            Get(extra, "LockbarWidth") == null,
            "fit: no per-table width when every table gets the same one"
        );
        Check(
            VrFit.Describe(mine, new AppSettings())
                == "Lockbar 100 cm high, your eyes 5 cm over the glass, every table at its real size.",
            "fit: the summary says over the glass for a negative eye position"
        );
        VrFit.Write(
            ini,
            new VrFit.Choice(91, 60, VrFit.SizeReal, 57.1),
            resetPosition: true,
            VrFit.SizeReal
        );
        v = IniFile.Read(ini);
        Check(
            v[("Player", "ScreenPlayerY")] == "60" && v[("PlayerVR", "TableY")] == "60",
            "fit: eyes can be further back than VPX's slider allows (60 cm)"
        );

        // − / + on the eye distance: 5 cm steps, or whole inches.
        Check(
            VrFit.StepEyes(10, +1, false) == 11
                && VrFit.StepEyes(10.4, -1, false) == 9
                && VrFit.StepEyes(100, +1, false) == 100,
            "fit: − / + move the eyes 1 cm, on whole cm, kept in range"
        );
        Check(
            VrFit.FormatEyes(35, true) == "14\""
                && VrFit.FormatEyes(VrFit.StepEyes(35, +1, true), true) == "15\""
                && VrFit.FormatEyes(35, false) == "35 cm",
            "fit: in feet and inches the eyes step 1\" on whole inches (35 cm shows 14\", not 13.8\")"
        );

        Check(
            VrFit.StepLockbarHeight(91, +1, false) == 92
                && VrFit.Format(VrFit.StepLockbarHeight(91, -1, true), true) == "35\""
                && VrFit.StepLockbarHeight(130, +1, false) == 130,
            "fit: − / + move the lockbar 1 cm or 1\", kept in range"
        );
        Check(
            VrFit.Format(VrFit.StepPlayerHeight(177.8, +1, true), true) == "5'11\""
                && VrFit.StepPlayerHeight(VrFit.TypicalPlayerHeight, -1, false) == 174,
            "fit: − / + move your height 1\" (5'10\" to 5'11\") or 1 cm"
        );

        // After a saved fit, an empty value is VPX's own default (VPX blanks one equal to it), not the manager's.
        var saved = new AppSettings { VrFitSaved = true };
        (extra, _) = VrFit.LaunchSettings(saved, none, wpc);
        Check(
            Get(extra, "LockbarHeight") == null
                && Get(extra, "ScreenPlayerY") == null
                && Get(extra, "TableY") == null
                && Get(extra, "LockbarWidth") == "57.1"
                && VrFit.LockbarHeight(none, saved) == 85
                && VrFit.EyesBehind(none, saved) == -10,
            "fit: after you save a fit, a value VPX blanked at its default stays VPX's, not the manager's"
        );

        // Reset to defaults: the VR file's own values go, so launches get the real-cabinet ones again.
        VrFit.Reset(ini);
        v = IniFile.Read(ini);
        (extra, _) = VrFit.LaunchSettings(new AppSettings(), v, wpc);
        Check(
            v[("Player", "LockbarHeight")] == ""
                && v[("Player", "ScreenPlayerY")] == ""
                && v[("PlayerVR", "TableY")] == "10"
                && v[("Player", "AAFactor")] == "1.2"
                && Get(extra, "LockbarHeight") == "91"
                && Get(extra, "ScreenPlayerY") == "10",
            "fit: reset to defaults clears your values (other settings kept), so tables get a real cabinet's again"
        );
        return results;
    }
}
