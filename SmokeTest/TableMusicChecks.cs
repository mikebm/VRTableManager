using System.Text;
using VrTableManager.Services;

/// <summary>A table's music folder left beside the .vpx is moved into music\, where VPX plays it from.</summary>
static class TableMusicChecks
{
    static byte[] Rec(string tag, byte[] data) =>
        [.. BitConverter.GetBytes(data.Length + 4), .. Encoding.ASCII.GetBytes(tag), .. data];

    static string Table(string dir, string script)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "table.vpx");
        var code = Encoding.Latin1.GetBytes(script);
        using var cfb = OpenMcdf.RootStorage.Create(path);
        using var s = cfb.CreateStorage("GameStg").CreateStream("GameData");
        byte[] bytes =
        [
            .. BitConverter.GetBytes(4),
            .. "CODE"u8,
            .. BitConverter.GetBytes(code.Length),
            .. code,
            .. Rec("ENDB", []),
        ];
        s.Write(bytes, 0, bytes.Length);
        return path;
    }

    static void Put(string file, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();

        // Empire Strikes Back: PlayMusic"StarWarsESB\0ESB01.ogg" with StarWarsESB\ beside the table.
        var esb = Path.Combine(root, "music-esb");
        var vpx = Table(
            esb,
            "PinMAMETimer.Enabled=1:PlayMusic\"StarWarsESB\\0ESB01.ogg\"\r\n"
                + "    Case 0:PlayMusic\"StarWarsESB\\0ESB10.ogg\"\r\n"
                + "LoadVPM \"01120100\",\"Hankin.vbs\",3.61\r\nPlayMusic \"pinmame\\x\"\r\n"
                // A media player's folder, and one only a commented-out line plays: not VPX's music.
                + "Jukebox.URL = TablePath & \"Tunes\\\" & n & \".mp3\"\r\n"
                + "' PlayMusic \"Old\\x.mp3\"\r\n"
                + "x = 1 ' PlayMusic \"Older\\x.mp3\"\r\n"
        );
        Put(Path.Combine(esb, "StarWarsESB", "0ESB01.ogg"));
        Put(Path.Combine(esb, "StarWarsESB", "0ESB02.ogg"));
        Put(Path.Combine(esb, "Other", "a.ogg")); // audio, but the script never names it
        Put(Path.Combine(esb, "Tunes", "1.mp3"));
        Put(Path.Combine(esb, "Old", "x.mp3"));
        Put(Path.Combine(esb, "Older", "x.mp3"));
        Put(Path.Combine(esb, "pinmame", "roms", "empsback.zip")); // named, but not audio
        TableMusic.FixLayout(esb, vpx);
        results.Add(
            (
                File.Exists(Path.Combine(esb, "music", "StarWarsESB", "0ESB01.ogg"))
                    && File.Exists(Path.Combine(esb, "music", "StarWarsESB", "0ESB02.ogg"))
                    && !Directory.Exists(Path.Combine(esb, "StarWarsESB"))
                    && Directory.Exists(Path.Combine(esb, "Other"))
                    && File.Exists(Path.Combine(esb, "pinmame", "roms", "empsback.zip")),
                "table music: a folder the script plays from beside the table goes into music\\; other folders stay"
            )
        );
        results.Add(
            (
                Directory.Exists(Path.Combine(esb, "Tunes"))
                    && Directory.Exists(Path.Combine(esb, "Old"))
                    && Directory.Exists(Path.Combine(esb, "Older"))
                    && !Directory.Exists(Path.Combine(esb, "music", "Tunes")),
                "table music: a folder the script plays some other way, or only on a commented-out line, stays"
            )
        );

        // Already partly in music\: merged, the table's file replacing the one there.
        var merge = Path.Combine(root, "music-merge");
        var mvpx = Table(merge, "PlayMusic \"Tunes/theme.mp3\"\r\n");
        Put(Path.Combine(merge, "Tunes", "theme.mp3"), "new");
        Put(Path.Combine(merge, "music", "Tunes", "old.mp3"), "old");
        Put(Path.Combine(merge, "music", "Tunes", "theme.mp3"), "stale");
        TableMusic.FixLayout(merge, mvpx);
        results.Add(
            (
                File.ReadAllText(Path.Combine(merge, "music", "Tunes", "theme.mp3")) == "new"
                    && File.Exists(Path.Combine(merge, "music", "Tunes", "old.mp3"))
                    && !Directory.Exists(Path.Combine(merge, "Tunes")),
                "table music: merged into a music\\ folder already there (forward slash paths too)"
            )
        );

        // A folder with anything besides audio, or a table file that can't be read, is left alone.
        var mixed = Path.Combine(root, "music-mixed");
        var xvpx = Table(mixed, "PlayMusic \"Pack\\a.ogg\"\r\n");
        Put(Path.Combine(mixed, "Pack", "a.ogg"));
        Put(Path.Combine(mixed, "Pack", "readme.txt"));
        TableMusic.FixLayout(mixed, xvpx);
        var broken = Path.Combine(root, "music-broken");
        Put(Path.Combine(broken, "table.vpx"), "not a table");
        Put(Path.Combine(broken, "Songs", "a.ogg"));
        TableMusic.FixLayout(broken, Path.Combine(broken, "table.vpx"));
        results.Add(
            (
                Directory.Exists(Path.Combine(mixed, "Pack"))
                    && Directory.Exists(Path.Combine(broken, "Songs")),
                "table music: a folder with other files in it, or an unreadable table, is left as it is (never throws)"
            )
        );
        return results;
    }
}
