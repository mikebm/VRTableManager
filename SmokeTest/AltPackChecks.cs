using System.IO.Compression;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// DMD colorizations and AltSound (AltPacks): which catalog files are offered, installing synthetic packs laid
/// out like real ones into serum\, vni\ and altsound\, version tracking, and moving older layouts. Downloading is the browser's job.
/// </summary>
public static class AltPackChecks
{
    public static IEnumerable<(bool ok, string name)> Run(string root)
    {
        var folder = Path.Combine(root, "alt-tables", "Medieval Madness (Williams 1997)");
        Directory.CreateDirectory(folder);
        var table = new InstalledTable(
            new TableManifest
            {
                GameId = "mm",
                GameName = "Medieval Madness",
                VpxFile = "mm.vpx",
                RomNames = ["mm_109c"],
            },
            Path.Combine(folder, "mm.vpx"),
            folder
        );
        var original = new InstalledTable(
            new TableManifest
            {
                GameId = "o",
                GameName = "Original",
                VpxFile = "o.vpx",
            },
            Path.Combine(root, "o", "o.vpx"),
            Path.Combine(root, "o")
        );

        static VpsUrl U(string url) => new() { Url = url };
        var game = new VpsGame
        {
            Id = "mm",
            Name = "Medieval Madness",
            AltColorFiles =
            [
                new VpsAltColorFile
                {
                    Id = "p2d",
                    Type = "Pin2dmd",
                    Folder = "mm_109c",
                    Version = "2.0",
                    UpdatedAt = 5,
                    Urls = [U("https://vpuniverse.com/files/file/5667-medieval-madness/")],
                },
                new VpsAltColorFile
                {
                    Id = "serum",
                    Type = "Serum - 65k",
                    Folder = "mm_109c",
                    Version = "1.1.0",
                    UpdatedAt = 3,
                    Authors = ["Fred"],
                    Urls = [U("https://vpuniverse.com/files/file/24028-mm-serum/")],
                },
                new VpsAltColorFile
                {
                    Id = "other",
                    Type = "Serum",
                    Folder = "mm_109b",
                    Version = "1.0",
                    UpdatedAt = 9,
                    Urls = [U("https://vpuniverse.com/files/file/1-mm-b/")],
                },
                new VpsAltColorFile
                {
                    Id = "patch",
                    Type = "PB Patch",
                    Folder = "mm_109c",
                    Urls = [U("https://vpuniverse.com/files/file/2-patch/")],
                },
                new VpsAltColorFile
                {
                    Id = "pac",
                    Type = "Pin2DMD - PAC - 64 Colors",
                    Folder = "mm_109c",
                    Urls = [U("https://vpuniverse.com/files/file/3-pac/")],
                },
                new VpsAltColorFile
                {
                    Id = "monte",
                    Type = "Pin2DMD",
                    Folder = "mm_109c",
                    Urls = [U("https://montetoncab.fr/x/mm_109c/")],
                },
            ],
            AltSoundFiles =
            [
                new VpsFile
                {
                    Id = "fr",
                    Version = "1.3",
                    UpdatedAt = 2,
                    Comment = "French Altsound",
                    Urls = [U("https://vpuniverse.com/files/file/27043-altsound-fr/")],
                },
                new VpsFile
                {
                    Id = "cat",
                    Version = "1.0",
                    UpdatedAt = 4,
                    Urls =
                    [
                        U(
                            "https://pinsound-community.org/forum/index.php?/files/category/21-the-addams-family/"
                        ),
                    ],
                },
                new VpsFile
                {
                    Id = "orch",
                    Version = "2.0.0",
                    UpdatedAt = 1,
                    Urls = [U("https://vpuniverse.com/files/file/5937-altsound-orch/")],
                },
            ],
        };

        var colours = AltPacks.SourcesFor(game, table, AddonKind.AltColor);
        yield return (
            colours.Select(f => f.Id).SequenceEqual(["serum", "p2d", "other"]),
            "colours: this ROM's first, Serum before Pin2DMD; ROM patches, PAC-only and folder-listing links left out"
        );
        yield return (
            AltPacks.Describe(colours[0], table) == "Serum - 65k · v1.1.0 by Fred"
                && AltPacks.Describe(colours[2], table).EndsWith("(made for mm_109b)"),
            "colour labels show the type, and when a file was made for another ROM version"
        );
        yield return (
            AltPacks
                .SourcesFor(game, table, AddonKind.AltSound)
                .Select(f => f.Id)
                .SequenceEqual(["fr", "orch"]),
            "AltSound: newest first; pinsound category pages left out"
        );
        yield return (
            AltPacks.SourcesFor(game, original, AddonKind.AltColor).Count == 0,
            "tables without a ROM get no colours or AltSound"
        );

        // Serum, with a companion file, into serum\<rom>\<rom>.cRZ (renamed from the file's own ROM).
        var serumZip = Zip(
            root,
            "serum.zip",
            ("MM Serum/mm_109b.cRZ", "crz"),
            ("MM Serum/mm_109b.pup.csv", "csv"),
            ("MM Serum/readme.txt", "r")
        );
        var rec = AltPacks.Install(AddonKind.AltColor, serumZip, table, game.AltColorFiles[1]);
        yield return (
            rec is { Location: "serum", Folder: "mm_109c", FileId: "serum" }
                && File.ReadAllText(Path.Combine(folder, "serum", "mm_109c", "mm_109c.cRZ"))
                    == "crz"
                && File.Exists(Path.Combine(folder, "serum", "mm_109c", "mm_109c.pup.csv"))
                && !File.Exists(Path.Combine(folder, "serum", "mm_109c", "readme.txt")),
            "Serum lands in serum\\<rom>\\<rom>.cRZ, with its .pup.csv, renamed to the table's ROM"
        );
        table.Manifest.AltColor = rec;

        // Some are posted as the bare .cRZ, not in an archive.
        var bareCrz = Path.Combine(root, "bare", "MM_Serum.cRZ");
        Write(bareCrz);
        rec = AltPacks.Install(AddonKind.AltColor, bareCrz, table, game.AltColorFiles[1]);
        yield return (
            rec is { Location: "serum", Folder: "mm_109c" }
                && File.Exists(Path.Combine(folder, "serum", "mm_109c", "mm_109c.cRZ")),
            "a bare .cRZ download installs like an archived one"
        );
        table.Manifest.AltColor = rec;
        yield return (
            AltPacks.StateOf(table, AddonKind.AltColor) is { Tracked.FileId: "serum" } s
                && s.Folders.SequenceEqual(["serum\\mm_109c"]),
            "installed colours are tracked"
        );

        // A folder you already have: copied in, left where it is. A whole altcolor folder of several games
        // installs this ROM's pack, and is refused when none is this ROM's rather than guessing.
        var have = Path.Combine(root, "have", "altcolor");
        Write(Path.Combine(have, "afm_113b", "afm_113b.cRZ"));
        Write(Path.Combine(have, "mm_109c", "mm_109c.cRZ"));
        rec = AltPacks.Install(AddonKind.AltColor, have, table, game.AltColorFiles[1]);
        var others = Path.Combine(root, "have2", "altcolor");
        Write(Path.Combine(others, "afm_113b", "afm_113b.cRZ"));
        Write(Path.Combine(others, "tz_92", "tz_92.cRZ"));
        string? refused = null;
        try
        {
            AltPacks.Install(AddonKind.AltColor, others, table, game.AltColorFiles[1]);
        }
        catch (InvalidDataException e)
        {
            refused = e.Message;
        }
        yield return (
            rec is { Location: "serum", Folder: "mm_109c" }
                && File.Exists(Path.Combine(folder, "serum", "mm_109c", "mm_109c.cRZ"))
                && File.Exists(Path.Combine(have, "mm_109c", "mm_109c.cRZ"))
                && refused?.Contains("several games") == true,
            "a colour folder you have: this ROM's pack copied in; a folder of other games' packs refused"
        );
        table.Manifest.AltColor = rec;

        // A new catalog version shows as an update; switching to Pin2DMD replaces the Serum files.
        game.AltColorFiles[1].Version = "1.2.0";
        var updates = Addons.FindUpdates([table], [game]);
        yield return (
            updates.Count == 1
                && updates[0] is { Kind: AddonKind.AltColor, Label: "v1.1.0 → v1.2.0" },
            "a new colour version shows as an update"
        );
        var vniZip = Zip(
            root,
            "vni.zip",
            ("mm_109c/pin2dmd.pal", "pal"),
            ("mm_109c/MM.vni", "vni"),
            ("mm_109c/pin2dmd.pac", "pac")
        );
        rec = AltPacks.Install(AddonKind.AltColor, vniZip, table, game.AltColorFiles[0]);
        yield return (
            rec is { Location: "vni", Folder: "mm_109c" }
                && File.Exists(Path.Combine(folder, "vni", "mm_109c", "pin2dmd.pal"))
                && File.Exists(Path.Combine(folder, "vni", "mm_109c", "mm_109c.vni"))
                && !File.Exists(Path.Combine(folder, "vni", "mm_109c", "pin2dmd.pac"))
                && !Directory.Exists(Path.Combine(folder, "serum")),
            "Pin2DMD palettes go to vni\\<rom> (pin2dmd.pal kept, others renamed, .pac left out); the old Serum files go"
        );
        table.Manifest.AltColor = rec;

        bool Throws(Action a)
        {
            try
            {
                a();
                return false;
            }
            catch (InvalidDataException)
            {
                return true;
            }
        }
        yield return (
            Throws(() =>
                AltPacks.Install(
                    AddonKind.AltColor,
                    Zip(root, "pac.zip", ("x/pin2dmd.pac", "p")),
                    table,
                    game.AltColorFiles[0]
                )
            ) && File.Exists(Path.Combine(folder, "vni", "mm_109c", "pin2dmd.pal")),
            "a PAC-only colorization is refused (VPX can't read it) and the installed one stays"
        );
        var barePac = Path.Combine(root, "bare", "pin2dmd.pac");
        Write(barePac);
        string? pacError = null;
        try
        {
            AltPacks.Install(AddonKind.AltColor, barePac, table, game.AltColorFiles[0]);
        }
        catch (InvalidDataException e)
        {
            pacError = e.Message;
        }
        yield return (
            pacError?.Contains("encrypted") == true,
            "a bare .pac download says why it can't be used, not that it isn't an archive"
        );

        // A catalog "Pin2DMD" file found to be PAC-only is remembered: listed last, labelled, retried once the catalog has a newer one.
        PacOnlyFiles.StorePath = Path.Combine(root, "pac-only.json");
        var p2d = game.AltColorFiles[0];
        var before = AltPacks
            .SourcesFor(game, table, AddonKind.AltColor)
            .FindIndex(f => f.Id == p2d.Id);
        PacOnlyFiles.Mark(p2d);
        var after = AltPacks.SourcesFor(game, table, AddonKind.AltColor);
        yield return (
            before == 1
                && after[^1].Id == p2d.Id
                && AltPacks.Describe(p2d, table).Contains("PAC only"),
            "a colorization found to be PAC-only goes last and says so"
        );
        var newer = new VpsAltColorFile
        {
            Id = p2d.Id,
            Type = "Pin2dmd",
            UpdatedAt = (p2d.UpdatedAt ?? 0) + 1,
        };
        yield return (
            !PacOnlyFiles.IsKnown(newer),
            "a newer catalog version of it is offered normally again"
        );

        // AltSound: the pack folder with altsound.csv, wherever it sits in the archive, into altsound\<rom>.
        var soundZip = Zip(
            root,
            "sound.zip",
            ("AltSound/mm_109c/altsound.csv", "csv"),
            ("AltSound/mm_109c/music/theme.ogg", "ogg"),
            ("Install.txt", "t")
        );
        var srec = AltPacks.Install(AddonKind.AltSound, soundZip, table, game.AltSoundFiles[0]);
        yield return (
            srec is { Location: "altsound", Folder: "mm_109c" }
                && File.ReadAllText(
                    Path.Combine(folder, "altsound", "mm_109c", "music", "theme.ogg")
                ) == "ogg"
                && !File.Exists(Path.Combine(folder, "altsound", "mm_109c", "Install.txt")),
            "AltSound lands in altsound\\<rom> (the pack folder only)"
        );
        var legacyZip = Zip(
            root,
            "legacy.zip",
            ("Medieval/jingle/001.ogg", "j"),
            ("Medieval/sfx/002.ogg", "s")
        );
        var lrec = AltPacks.Install(AddonKind.AltSound, legacyZip, table, game.AltSoundFiles[1]);
        yield return (
            lrec.Folder == "mm_109c"
                && File.Exists(Path.Combine(folder, "altsound", "mm_109c", "jingle", "001.ogg"))
                && !File.Exists(Path.Combine(folder, "altsound", "mm_109c", "altsound.csv")),
            "a legacy PinSound layout is recognised and replaces the previous pack"
        );
        table.Manifest.AltSound = lrec;

        // Aliased ROMs (Mass Effect runs afm_113b_me): the plugins look under the alias.
        var aliased = new InstalledTable(
            new TableManifest { RomNames = ["afm_113b"], RomAlias = "afm_113b_me" },
            "x.vpx",
            folder
        );
        yield return (
            AltPacks.GameName(aliased) == "afm_113b_me"
                && AltPacks.GameName(table, game.AltColorFiles[2]) == "mm_109c",
            "folders are named after the ROM the script runs (its alias when it has one)"
        );

        AltPacks.Remove(table, AddonKind.AltSound, "mm_109c");
        AltPacks.Remove(table, AddonKind.AltColor, "vni\\mm_109c");
        yield return (
            !Directory.Exists(Path.Combine(folder, "altsound"))
                && !Directory.Exists(Path.Combine(folder, "vni")),
            "removing deletes the folders"
        );

        // Older layouts move to where the plugins look; Pin2DMD palettes don't end up in serum\.
        var old = Path.Combine(root, "alt-tables", "Old Layout");
        Write(Path.Combine(old, "pinmame", "altcolor", "taf_l7", "pin2dmd.pal"));
        Write(Path.Combine(old, "pinmame", "altcolor", "afm_113b", "afm_113b.cRZ"));
        Write(Path.Combine(old, "pinmame", "altsound", "taf_l7", "altsound.csv"));
        AltPacks.FixLayout(old);
        yield return (
            File.Exists(Path.Combine(old, "vni", "taf_l7", "pin2dmd.pal"))
                && File.Exists(Path.Combine(old, "serum", "afm_113b", "afm_113b.cRZ"))
                && File.Exists(Path.Combine(old, "altsound", "taf_l7", "altsound.csv"))
                && !Directory.Exists(Path.Combine(old, "pinmame", "altcolor"))
                && !Directory.Exists(Path.Combine(old, "pinmame", "altsound")),
            "old pinmame\\altcolor and pinmame\\altsound move to serum\\, vni\\ and altsound\\"
        );
    }

    static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    static string Zip(string root, string name, params (string path, string text)[] entries)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(root);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, t) in entries)
        {
            using (var w = new StreamWriter(z.CreateEntry(p).Open()))
            {
                w.Write(t);
            }
        }

        return path;
    }
}
