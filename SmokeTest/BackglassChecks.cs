using System.IO.Compression;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// The .directb2s backglass add-on (Backglasses): installed beside the table under the table's own name, from a bare
/// file or an archive, tracked in vrtm.json, and removed again.
/// </summary>
public static class BackglassChecks
{
    public static IEnumerable<(bool ok, string name)> Run(string root)
    {
        var folder = Path.Combine(root, "b2s-tables", "Twilight Zone (Bally 1993)");
        Directory.CreateDirectory(folder);
        var table = new InstalledTable(
            new TableManifest
            {
                GameId = "tz",
                GameName = "Twilight Zone",
                VpxFile = "TZ v1.2.vpx",
            },
            Path.Combine(folder, "TZ v1.2.vpx"),
            folder
        );
        var game = new VpsGame
        {
            Id = "tz",
            Name = "Twilight Zone",
            B2sFiles =
            [
                new VpsFile
                {
                    Id = "old",
                    Version = "1.0",
                    UpdatedAt = 1,
                    Urls = [new() { Url = "https://vpuniverse.com/files/file/1-tz-b2s/" }],
                },
                new VpsFile
                {
                    Id = "new",
                    Version = "2.0",
                    UpdatedAt = 9,
                    Urls = [new() { Url = "https://vpuniverse.com/files/file/2-tz-b2s/" }],
                },
                new VpsFile
                {
                    Id = "nolink",
                    Version = "3.0",
                    UpdatedAt = 10,
                },
            ],
        };

        var sources = Backglasses.SourcesFor(game);
        yield return (
            sources.Select(f => f.Id).SequenceEqual(["new", "old"]),
            "backglass: catalog files with a link are offered, newest first"
        );
        yield return (
            !Backglasses.StateOf(table).IsInstalled,
            "backglass: none installed at first"
        );

        // A bare .directb2s with another name is installed under the table's name.
        var bare = Path.Combine(root, "b2s-src", "Twilight Zone (Bally 1993).directb2s");
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        File.WriteAllText(bare, "bare");
        var dest = Path.Combine(folder, "TZ v1.2.directb2s");
        var r = Backglasses.Install(bare, table, game.B2sFiles[0]);
        table.Manifest.Backglass = r.Record;
        yield return (
            File.ReadAllText(dest) == "bare"
                && File.Exists(bare)
                && r.Record is { FileId: "old", Folder: "TZ v1.2.directb2s" },
            "backglass: a bare file is copied in, named like the table, and the original kept"
        );
        yield return (
            Backglasses.StateOf(table) is { Tracked.FileId: "old", IsInstalled: true },
            "backglass: the installed one is tracked"
        );

        // An archive with two: the one named like the table wins; else the biggest.
        var zip = Path.Combine(root, "b2s-src", "tz-b2s.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Entry(z, "pack/Twilight Zone FullDMD.directb2s", new string('x', 100));
            Entry(z, "pack/TZ v1.2.directb2s", "named");
        }
        r = Backglasses.Install(zip, table, game.B2sFiles[1]);
        yield return (
            File.ReadAllText(dest) == "named" && r.Count == 2 && r.From == "TZ v1.2.directb2s",
            "backglass: from an archive, the one named like the table replaces the old one"
        );

        var zip2 = Path.Combine(root, "b2s-src", "tz-b2s-2.zip");
        using (var z = ZipFile.Open(zip2, ZipArchiveMode.Create))
        {
            Entry(z, "a.directb2s", "small");
            Entry(z, "b.directb2s", new string('x', 100));
        }
        r = Backglasses.Install(zip2, table, game.B2sFiles[1]);
        yield return (
            r.From == "b.directb2s" && File.ReadAllText(dest).Length == 100,
            "backglass: with no name match, the biggest is taken and named"
        );
        yield return (
            !Directory.EnumerateFiles(folder, "*" + Addons.NewSuffix).Any(),
            "backglass: nothing left over from the swap"
        );

        var empty = Path.Combine(root, "b2s-src", "empty.zip");
        using (var z = ZipFile.Open(empty, ZipArchiveMode.Create))
        {
            Entry(z, "readme.txt", "x");
        }
        var threw = false;
        try
        {
            Backglasses.Install(empty, table, game.B2sFiles[1]);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        yield return (
            threw && File.Exists(dest),
            "backglass: an archive without one fails and keeps the installed backglass"
        );

        Backglasses.Remove(table);
        yield return (
            !File.Exists(dest),
            "backglass: removing deletes the file"
        );
    }

    static void Entry(ZipArchive z, string name, string text)
    {
        using var w = new StreamWriter(z.CreateEntry(name).Open());
        w.Write(text);
    }
}
