using System.Text;
using VrTableManager.Services;

/// <summary>The PuP DMD fix for VPX 10.8.1: which flashers it touches, the patch itself, backup and undo.</summary>
static class VrDmdFixChecks
{
    static byte[] Rec(string tag, byte[] data) => [.. BitConverter.GetBytes(data.Length + 4), .. Encoding.ASCII.GetBytes(tag), .. data];
    static byte[] I32(int v) => BitConverter.GetBytes(v);
    static byte[] WStr(string s) => [.. BitConverter.GetBytes(s.Length), .. Encoding.Unicode.GetBytes(s)];

    // A flasher like BTILC's: settings, then a drag point with its own nested records and ENDB, then the item's ENDB.
    static byte[] Flasher(string name, params byte[][] settings) =>
        [.. I32(20), .. Rec("FHEI", I32(0)), .. Rec("NAME", WStr(name)), .. settings.SelectMany(b => b),
         .. Rec("FLDB", I32(0)), .. Rec("DPNT", []), .. Rec("VCEN", new byte[8]), .. Rec("IDMD", I32(1)), .. Rec("ENDB", []),
         .. Rec("ENDB", [])];

    static Dictionary<string, int> TopInts(byte[] d)
    {
        var tags = new Dictionary<string, int>();
        for (int p = 4; p + 8 <= d.Length;)
        {
            int len = BitConverter.ToInt32(d, p);
            var tag = Encoding.ASCII.GetString(d, p + 4, 4);
            if (tag is "DPNT" or "ENDB") break;
            if (len == 8) tags[tag] = BitConverter.ToInt32(d, p + 8);
            p += 4 + len;
        }
        return tags;
    }

    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        var vpx = Path.Combine(root, "dmd fix", "table.vpx");
        Directory.CreateDirectory(Path.GetDirectoryName(vpx)!);
        var pinmameDmd = Flasher("VR_DMD", Rec("IDMD", I32(1)));
        var pupDmd = Flasher("VR_PUPDMD", Rec("DSPT", I32(0)), Rec("IDMD", I32(1)));
        var modern = Flasher("PUPDMD_10_8_1", Rec("RDMD", I32(1)));
        using (var cfb = OpenMcdf.RootStorage.Create(vpx))
        {
            var gs = cfb.CreateStorage("GameStg");
            void Put(string name, byte[] bytes) { using var s = gs.CreateStream(name); s.Write(bytes, 0, bytes.Length); }
            Put("GameData", Rec("ENDB", []));
            Put("GameItem0", pinmameDmd);
            Put("GameItem1", pupDmd);
            Put("GameItem2", modern);
        }

        var found = VrDmdFix.Find(vpx);
        results.Add((found.Count == 1 && found[0] is { Item: 1, Name: "VR_PUPDMD" },
            "PuP DMD fix: only a PuP-named flasher with 10.8.0's DMD flag needs it (not a PinMAME DMD, not one already set up for 10.8.1)"));

        VrDmdFix.Apply(vpx);
        byte[] Item(string path, int i)
        {
            using var r = OpenMcdf.RootStorage.OpenRead(path);
            using var s = r.OpenStorage("GameStg").OpenStream("GameItem" + i);
            var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }
        var patched = Item(vpx, 1);
        var t = TopInts(patched);
        var tail = pupDmd.AsSpan(pupDmd.Length - 60).ToArray(); // the drag point and ENDBs, untouched
        results.Add((t.GetValueOrDefault("RDMD") == 4 && t.GetValueOrDefault("RSTL") == 2 && !t.ContainsKey("IDMD") && t.ContainsKey("DSPT")
                     && patched.Length == pupDmd.Length + 12 && patched.AsSpan(patched.Length - 60).SequenceEqual(tail),
            "PuP DMD fix: the flasher becomes External Renderer → Score View; its other settings and drag points are kept"));
        results.Add((Item(vpx, 0).SequenceEqual(pinmameDmd) && Item(vpx, 2).SequenceEqual(modern) && VrDmdFix.Find(vpx).Count == 0,
            "PuP DMD fix: other items are untouched, and a fixed table isn't offered the fix again"));
        results.Add((VrDmdFix.HasBackup(vpx) && Item(VrDmdFix.BackupPath(vpx), 1).SequenceEqual(pupDmd)
                     && !File.Exists(vpx + ".dmdfix.tmp") && !Directory.EnumerateFiles(Path.GetDirectoryName(vpx)!, "*.vpx").Skip(1).Any(),
            "PuP DMD fix: the original is kept as .vpx.dmdfix.bak (not a second .vpx table) and no temp file is left"));
        VrDmdFix.Undo(vpx);
        results.Add((Item(vpx, 1).SequenceEqual(pupDmd) && !VrDmdFix.HasBackup(vpx), "PuP DMD fix: undo puts the original back"));

        // Total Nuclear Annihilation: the PuP DMD flasher is called VR_DMD. With no ROM and FlexDMD/UltraDMD switched
        // off, PuP is all that could draw on it; with FlexDMD on (or PinMAME), it's the table's own DMD.
        const string pupOnly = "Const UseUltraDMD = 0\r\nSet UltraDMD = CreateObject(\"UltraDMD.DMDObject\")\r\nSet PuPlayer = CreateObject(\"PinUpPlayer.PinDisplay\")\r\n";
        string ScriptTable(string name, string script)
        {
            var path = Path.Combine(root, "dmd fix", name + ".vpx");
            using var cfb = OpenMcdf.RootStorage.Create(path);
            var gs = cfb.CreateStorage("GameStg");
            void Put(string n, byte[] bytes) { using var s = gs.CreateStream(n); s.Write(bytes, 0, bytes.Length); }
            Put("GameData", Rec("CODE", Encoding.Latin1.GetBytes(script)));
            Put("GameItem0", pinmameDmd);
            return path;
        }
        var tna = ScriptTable("tna", pupOnly);
        var flex = ScriptTable("flex", pupOnly.Replace("UseUltraDMD = 0", "UseUltraDMD = 1"));
        var rom = ScriptTable("rom", pupOnly + "LoadVPM \"01560000\", \"S11.VBS\", 3.26\r\n");
        results.Add((VrDmdFix.Find(tna) is [{ Item: 0, Name: "VR_DMD" }] && VrDmdFix.Find(flex).Count == 0 && VrDmdFix.Find(rom).Count == 0,
            "PuP DMD fix: an old DMD flasher with another name (VR_DMD) is fixed when PuP is the table's only DMD, not when FlexDMD/UltraDMD is on or it uses PinMAME"));
        VrDmdFix.Apply(tna);
        results.Add((TopInts(Item(tna, 0)).GetValueOrDefault("RDMD") == 4 && VrDmdFix.Find(tna).Count == 0, "PuP DMD fix: VR_DMD in a PuP-only table is patched"));

        results.Add((VrDmdFix.EngineSupportsFix("v10.8.1-5436-af26b2d93") && VrDmdFix.EngineSupportsFix("v10.9.0-1")
                     && !VrDmdFix.EngineSupportsFix("v10.8.0-2051-28dd6c3") && !VrDmdFix.EngineSupportsFix(null),
            "PuP DMD fix: only offered with VPX 10.8.1 or later (10.8.0 still shows the DMD the old way)"));
        return results;
    }
}
