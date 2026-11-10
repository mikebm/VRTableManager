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
        VrFit.Write(ini, new VrFit.Choice(91, 15, VrFit.SizeReal, 57.1), resetPosition: false, VrFit.SizeReal);
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
        VrFit.Write(ini, new VrFit.Choice(91, 15, VrFit.SizeSame, 64), resetPosition: true, VrFit.SizeReal);
        v = IniFile.Read(ini);
        Check(
            v[("Player", "LockbarWidth")] == "64"
                && v[("PlayerVR", "OverrideLockbarWidth")] == "1"
                && v[("PlayerVR", "TableY")] == "15"
                && v[("PlayerVR", "TableX")] == ""
                && v[("PlayerVR", "Orientation")] == "",
            "fit: one width for every table, and the table put straight ahead at the standing distance"
        );
        VrFit.Write(ini, new VrFit.Choice(91, 15, VrFit.SizeVpx, 64), resetPosition: false, VrFit.SizeSame);
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
                && notKept.SequenceEqual([("Player", "LockbarWidth")]),
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
        (extra, _) = VrFit.LaunchSettings(new AppSettings(), mine, own);
        Check(Get(extra, "LockbarWidth") == null, "fit: a table's own .ini width is left to VPX");
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
        VrFit.Write(ini, new VrFit.Choice(91, 60, VrFit.SizeReal, 57.1), resetPosition: true, VrFit.SizeReal);
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
