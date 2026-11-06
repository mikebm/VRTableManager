using System.IO.Compression;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// PuP packs (PupPacks): reading which pack a script opens, picking links from the catalog, installing
/// synthetic packs laid out the ways real ones are, version tracking and removal. Downloading is the browser's job.
/// </summary>
public static class PupPackChecks
{
    public static IEnumerable<(bool ok, string name)> Run(string root)
    {
        // Script: a literal, a variable set elsewhere, cGameName, and commented-out lines.
        yield return (RomDetector.PupNameFromScript("Set PuPlayer = CreateObject(\"PinUpPlayer.PinDisplay\")\nPuPlayer.Init pBackglass, \"jaws50\"\n") == "jaws50",
            "PuP name from PuPlayer.Init with a literal");
        yield return (RomDetector.PupNameFromScript("Const cPuPPack = \"ME_PuP\"\n' PuPlayer.Init pBackglass, \"old\"\nPuPlayer.Init pBackglass, cPuPPack\n") == "ME_PuP",
            "PuP name from a variable (commented-out Init ignored)");
        yield return (RomDetector.PupNameFromScript("Const cGameName = \"avr_200\"\nPuPlayer.Init pBackglass, cGameName\n") == "avr_200",
            "PuP name from cGameName (the ROM)");
        // JP's template (Jaws 50th): B2SInit with an empty first argument, the pack in a variable, PuP off by default.
        const string jp = "usePUP   = False               ' enable Pinup Player functions\r\ncPuPPack = \"JAWS_50Th_Anniversary\"    ' name of the PuP-Pack\r\n"
            + "Sub PuPStart(cPuPPack)\r\n    If usePUP=true then\r\n        Set PuPlayer = CreateObject(\"PinUpPlayer.PinDisplay\")\r\n        If PuPlayer is Nothing Then\r\n            usePUP=false\r\n"
            + "        Else\r\n            PuPlayer.B2SInit \"\",cPuPPack 'start the Pup-Pack\r\n";
        yield return (RomDetector.PupNameFromScript(jp) == "JAWS_50Th_Anniversary" && RomDetector.PupSwitch(jp) == false
                && RomDetector.PupSwitch("usePUP = True\r\nusePUP=false\r\n") == true && RomDetector.PupSwitch("Const cGameName = \"x\"") == null,
            "JP template: PuPlayer.B2SInit with the pack in a variable; usePUP = False is spotted (later fallbacks ignored)");
        yield return (RomDetector.UsesPup("CreateObject(\"PinUpPlayer.PinDisplay\")") && !RomDetector.UsesPup("Const cGameName = \"mm_109c\""),
            "PinUP Player use is spotted in the script");

        // Catalog: front-page links, Mega and broken links can't be fetched; newest first.
        var game = new VpsGame
        {
            Id = "g", Name = "Jaws",
            PupPackFiles =
            [
                new VpsFile { Id = "old", Version = "1.0a", UpdatedAt = 1, Urls = [new VpsUrl { Url = "https://vpuniverse.com/" }] },
                new VpsFile { Id = "mini", Version = "1.0.0", UpdatedAt = 2, Comment = "Mini PUP-pack", Authors = ["Nicolainuke"], Urls = [new VpsUrl { Url = "https://vpuniverse.com/files/file/25983-jaws-mini/" }] },
                new VpsFile { Id = "full", Version = "3.0.1", UpdatedAt = 3, Authors = ["A", "B", "C", "D", "E"], Urls = [new VpsUrl { Url = "https://vpuniverse.com/files/file/1-dead/", Broken = true }, new VpsUrl { Url = "https://vpuniverse.com/files/file/26016-jaws-50th/" }] },
                new VpsFile { Id = "mega", Version = "1", UpdatedAt = 4, Urls = [new VpsUrl { Url = "https://mega.nz/file/abc" }] },
            ],
        };
        var sources = PupPacks.SourcesFor(game);
        yield return (sources.Select(s => s.Id).SequenceEqual(["full", "mini"]) && Addons.UsableUrl(sources[0]) == "https://vpuniverse.com/files/file/26016-jaws-50th/",
            "catalog packs: newest first; site front pages, Mega and broken links left out");
        yield return (PupPacks.Describe(sources[1]) == "v1.0.0 by Nicolainuke — Mini PUP-pack" && PupPacks.Describe(sources[0]) == "v3.0.1 by A, B, C +2",
            "menu labels show version, authors and the catalog's note");

        // "incl. PuP" (BTILC 2026): the pack is a second file on the table's own page, with no PuP entry in the catalog.
        var btilc = new VpsGame
        {
            Id = "b", Name = "BTILC",
            TableFiles = [new VpsTableFile { Id = "t", Version = "1.0.1", Features = ["VR", "incl. PuP"], Urls = [new VpsUrl { Url = "https://vpuniverse.com/files/file/30000-btilc/" }] }],
        };
        var btilcTable = new InstalledTable(new TableManifest { GameId = "b", TableFileId = "t" }, "b.vpx", root);
        var bundled = PupPacks.SourcesFor(btilc, btilcTable);
        yield return (bundled.Count == 1 && bundled[0].Id == "t" && PupPacks.Describe(bundled[0]) == "From the table's page (v1.0.1)"
                && PupPacks.SourcesFor(btilc).Count == 0 && Addons.FilesOf(btilc, AddonKind.Pup).Any(f => f.Id == "t"),
            "an \"incl. PuP\" table offers its own page for the pack");

        // Install: the usual PinUPSystem\PUPVideos\<name>\ layout, into the table's pupvideos\.
        var folder = Path.Combine(root, "pup-tables", "Jaws (Original 2025)");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Jaws.vpx"), "Set PuPlayer = CreateObject(\"PinUpPlayer.PinDisplay\")\r\nPuPlayer.Init pBackglass, \"jaws50\"\r\n");
        var settings = new AppSettings { TablesDir = Path.Combine(root, "pup-tables") };
        var table = new InstalledTable(new TableManifest { GameId = "g", GameName = "Jaws", VpxFile = "Jaws.vpx" }, Path.Combine(folder, "Jaws.vpx"), folder);
        var loose = new InstalledTable(new TableManifest { GameName = "Loose", VpxFile = "x.vpx" }, Path.Combine(settings.TablesDir, "x.vpx"), settings.TablesDir);
        yield return (Addons.CanHold(table, settings) && !Addons.CanHold(loose, settings), "only tables in their own folder get a PuP pack");

        var names = PupPacks.ExpectedNames(table);
        yield return (names.Script == "jaws50", "expected pack name read from an older install's script");

        var zip1 = Zip(root, "pup1.zip",
            ("PinUPSystem/PUPVideos/Jaws50/screens.pup", "s"), ("PinUPSystem/PUPVideos/Jaws50/playlists.pup", "p"),
            ("PinUPSystem/PUPVideos/Jaws50/Backglass/intro.mp4", "v1"), ("PinUPSystem/PUPVideos/Jaws50/Backup/screens.pup", "old"),
            ("Option 1 - Full DMD.bat", "copy x y"));
        var rec = PupPacks.Install(zip1, table, sources[1], names).Record;
        var dest = Path.Combine(folder, "pupvideos", "Jaws50");
        yield return (rec.Folder == "Jaws50" && File.ReadAllText(Path.Combine(dest, "Backglass", "intro.mp4")) == "v1" && File.Exists(Path.Combine(dest, "Backup", "screens.pup")),
            "pack lands in pupvideos\\<folder> (same name as the script's, any case), with its sub-folders");
        yield return (!File.Exists(Path.Combine(folder, "Option 1 - Full DMD.bat")) && rec is { FileId: "mini", Version: "1.0.0" },
            "only the pack folder is installed; the version is recorded");

        table.Manifest.PupPack = rec;
        var state = PupPacks.StateOf(table);
        yield return (state.Tracked?.FileId == "mini" && state.Folders.SequenceEqual(["Jaws50"]), "installed pack is tracked");

        // A new catalog version of the same pack is an update; another pack is not.
        game.PupPackFiles[1].Version = "1.1.0";
        var updates = Addons.FindUpdates([table], [game]);
        yield return (updates.Count == 1 && updates[0].Label == "v1.0.0 → v1.1.0", "a new version of the installed pack shows as an update");

        // Update with a pack at the top of the archive (no folder of its own): named from the script, the old folder replaced.
        var zip2 = Zip(root, "pup2.zip", ("screens.pup", "s2"), ("playlists.pup", "p2"), ("Backglass/intro.mp4", "v2"));
        var rec2 = PupPacks.Install(zip2, table, game.PupPackFiles[1], names).Record;
        yield return (rec2.Folder == "jaws50" && File.ReadAllText(Path.Combine(folder, "pupvideos", "jaws50", "Backglass", "intro.mp4")) == "v2"
                && Directory.EnumerateDirectories(Path.Combine(folder, "pupvideos")).Count() == 1 && !File.Exists(Path.Combine(folder, "pupvideos", "jaws50", "Backup", "screens.pup")),
            "updating replaces the old pack; an unnamed pack takes the script's name");
        table.Manifest.PupPack = rec2;
        yield return (Addons.FindUpdates([table], [game]).Count == 0, "no update once the new version is in");

        // ROM table without its own PuP code: the pack's folder name is kept.
        var romTable = new InstalledTable(new TableManifest { GameName = "MB", RomNames = ["mb_106b"] }, Path.Combine(root, "pup-tables", "MB", "mb.vpx"), Path.Combine(root, "pup-tables", "MB"));
        var roots = new[] { Path.Combine(root, "x", "mb_106") };
        yield return (PupPacks.Choose(roots, Path.Combine(root, "x"), new PupNames(null, romTable.Manifest.RomNames)).name == "mb_106",
            "without a name in the script, the pack keeps its own folder name");
        bool threw;
        try { PupPacks.Install(Zip(root, "addon.zip", ("Bonus/clip.mp4", "v")), table, game.PupPackFiles[1], names); threw = false; }
        catch (InvalidDataException) { threw = true; }
        yield return (threw && File.Exists(Path.Combine(folder, "pupvideos", "jaws50", "screens.pup")), "an archive with no pack in it is refused and leaves the installed pack alone");

        PupPacks.Remove(table);
        yield return (!Directory.Exists(Path.Combine(folder, "pupvideos")) && File.Exists(table.TablePath), "removing deletes the pack (and the empty pupvideos), not the table");

        // Layouts, as Total Nuclear Annihilation ships them: option .bat files that copy a PuP-Pack_Options folder's
        // .pup files over the pack's own. The one named VR is used (minus its desktop helper .exe/.bat).
        const string opts = "tna/PuP-Pack_Options/";
        var tna = PupPacks.Install(Zip(root, "tna.zip",
            ("tna/screens.pup", "default"), ("tna/triggers.pup", "t-default"), ("tna/playlists.pup", "p"), ("tna/ReadMe.txt", "notes"),
            ("tna/EditThisPuPPack.bat", "Start PinUPPackEditor.exe %packname%"),
            ("tna/Option 1 - PuP_on_Backglass.bat", "xcopy /y \"PuP-Pack_Options\\Option 1 - PuP_on_Backglass\\*.pup\""),
            ("tna/Option 4 - VR_or_Desktop.bat", "xcopy /y \"PuP-Pack_Options\\Option 4 - VR_or_Desktop\\*.pup\"\r\nxcopy /y \"PuP-Pack_Options\\Option 4 - VR_or_Desktop\\*.exe\""),
            (opts + "Option 1 - PuP_on_Backglass/screens.pup", "bg"),
            (opts + "Option 4 - VR_or_Desktop/screens.pup", "vr"), (opts + "Option 4 - VR_or_Desktop/triggers.pup", "t-vr"),
            (opts + "Option 4 - VR_or_Desktop/PuPDesktopPunch.exe", "exe"), (opts + "Option 4 - VR_or_Desktop/pupinit.bat", "bat")),
            table, game.PupPackFiles[1], new PupNames("tna", []));
        var tnaDir = Path.Combine(folder, "pupvideos", "tna");
        yield return (tna.Layout.Applied == "Option 4 - VR_or_Desktop" && tna.Layout.Options.Count == 2
                && File.ReadAllText(Path.Combine(tnaDir, "screens.pup")) == "vr" && File.ReadAllText(Path.Combine(tnaDir, "triggers.pup")) == "t-vr"
                && !File.Exists(Path.Combine(tnaDir, "PuPDesktopPunch.exe")) && !File.Exists(Path.Combine(tnaDir, "pupinit.bat"))
                && tna.Layout.Note("pupvideos\\tna").Contains("VR layout (\"Option 4 - VR_or_Desktop\")") && tna.Layout.ReadMes.SequenceEqual(["ReadMe.txt"]),
            "a pack's VR layout is copied over its default (not its programs); the .bat that only opens the editor isn't a layout");

        // No VR layout (the .bat names a folder that differs from its own name): the default stays and the user is told how to switch.
        var noVr = PupPackOptions.SetUpForVr(Dir(root, "novr",
            ("screens.pup", "default"), ("FullDMD.bat", "copy /y \"Options\\Full DMD\\screens.pup\" ."), ("Options/Full DMD/screens.pup", "full")));
        var noVrNote = noVr.Note("pupvideos\\x");
        yield return (noVr.Applied == null && noVr.Options.SequenceEqual(["FullDMD"]) && File.ReadAllText(Path.Combine(root, "novr", "screens.pup")) == "default"
                && noVrNote.Contains("\"FullDMD\"") && noVrNote.Contains("Notepad"),
            "a pack with layouts but none for VR keeps its default, and the note says which layouts there are and how to switch");
        var twoVr = PupPackOptions.SetUpForVr(Dir(root, "twovr", ("screens.pup", "default"),
            ("PuP-Pack_Options/VR Cab/screens.pup", "a"), ("PuP-Pack_Options/VR_Desktop/screens.pup", "b"), ("PuP-Pack_Options/Non-VR/screens.pup", "c")));
        yield return (twoVr.Applied == null && twoVr.VrOptions.Count == 2 && File.ReadAllText(Path.Combine(root, "twovr", "screens.pup")) == "default"
                && twoVr.Note("x").Contains("several VR layouts"),
            "two VR layouts: neither is guessed at (and \"Non-VR\" isn't one)");
        yield return (PupPackOptions.SetUpForVr(Dir(root, "plain", ("screens.pup", "s"))).Note("x") == "", "a plain pack gets no extra note");

        // A pack installed before VR layouts were chosen (no marker) is switched once, before a VR launch.
        var oldPup = Path.Combine(root, "old-table", "pupvideos");
        Dir(oldPup, "tna", ("screens.pup", "default"), ("PuP-Pack_Options/Option 4 - VR/screens.pup", "vr"));
        Dir(oldPup, "other", ("screens.pup", "plain"));
        var oldTna = Path.Combine(oldPup, "tna");
        var switched = PupPackOptions.SetUpOnce(oldPup);
        yield return (switched.SequenceEqual(["tna: Option 4 - VR"]) && File.ReadAllText(Path.Combine(oldTna, "screens.pup")) == "vr"
                && File.ReadAllText(Path.Combine(oldTna, PupPackOptions.DefaultBackup, "screens.pup")) == "default"
                && File.Exists(Path.Combine(oldPup, "other", PupPackOptions.Marker)),
            "an already-installed pack gets its VR layout once, with its own files kept in a backup");
        File.WriteAllText(Path.Combine(oldTna, "screens.pup"), "picked by hand");
        yield return (PupPackOptions.SetUpOnce(oldPup).Count == 0 && File.ReadAllText(Path.Combine(oldTna, "screens.pup")) == "picked by hand",
            "once set up, a layout picked by hand later is left alone");

        // VR ini turns the PinUP Player plugin on (or off).
        var vs = new AppSettings { VpxDir = Path.Combine(root, "pup-vpx") };
        VpxSetup.EnsureVrIni(vs);
        var on = IniFile.Read(VpxSetup.VrIniPath(vs)).GetValueOrDefault(("Plugin.PUP", "Enable"));
        vs.UsePupPlugin = false;
        VpxSetup.EnsureVrIni(vs);
        var off = IniFile.Read(VpxSetup.VrIniPath(vs)).GetValueOrDefault(("Plugin.PUP", "Enable"));
        yield return (on == "1" && off == "0", "VR ini enables VPX's PUP plugin, and the setting turns it off");

        // Without a backglass display in the VR scene, a desktop table's PuP pack plays unseen.
        var bgOn = IniFile.Read(VpxSetup.VrIniPath(vs)).GetValueOrDefault(("PlayerVR", "AddBackglass"));
        vs.VrAddBackglass = false;
        VpxSetup.EnsureVrIni(vs);
        var bgOff = IniFile.Read(VpxSetup.VrIniPath(vs)).GetValueOrDefault(("PlayerVR", "AddBackglass"));
        yield return (bgOn == "1" && bgOff == "0", "VR ini adds VPX's default backglass for PuP, and the setting turns it off");
    }

    static string Dir(string root, string name, params (string path, string text)[] entries)
    {
        var dir = Path.Combine(root, name);
        foreach (var (p, t) in entries)
        {
            var f = Path.Combine(dir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, t);
        }
        return dir;
    }

    static string Zip(string root, string name, params (string path, string text)[] entries)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(root);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, t) in entries)
            using (var w = new StreamWriter(z.CreateEntry(p).Open())) w.Write(t);
        return path;
    }
}
