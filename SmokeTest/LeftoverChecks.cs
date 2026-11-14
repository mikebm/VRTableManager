using VrTableManager.Services;

/// <summary>Copies an install leaves behind: stale work folders in Temp, and an nvram left beside the table.</summary>
static class LeftoverChecks
{
    static string Put(string file, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    static void Age(string path, DateTime when)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTime(f, when);
            }
            foreach (
                var d in Directory
                    .EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                    .Prepend(path)
            )
            {
                Directory.SetLastWriteTime(d, when);
            }
        }
        else
        {
            File.SetLastWriteTime(path, when);
        }
    }

    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        var temp = Path.Combine(root, "temp");
        var old = DateTime.Now.AddDays(-3);

        var install = Path.GetDirectoryName(Put(Path.Combine(temp, "vrtm-1a2b3c4d", "t.vpx")))!;
        var rec = Path.Combine(temp, "vrtm-romaudio-0010fbed");
        Put(Path.Combine(rec, "roms", "afm.zip"));
        var update = Path.GetDirectoryName(Put(Path.Combine(temp, "vrtm-update-1.4.2", "a")))!;
        var video = Put(Path.Combine(temp, "vrtm-0a1b2c3d.mp4"));
        var ini = Put(Path.Combine(temp, "vrtm-ini-0a1b2c3d.ini")); // your .ini: never
        var other = Path.GetDirectoryName(Put(Path.Combine(temp, "someone-else", "a")))!;
        var named = Path.GetDirectoryName(Put(Path.Combine(temp, "vrtm-staged", "a")))!; // not one of ours
        foreach (var p in new[] { install, rec, update, video, ini, other, named })
        {
            Age(p, old);
        }
        // Old folder, but a file in it was written just now: still in use.
        var busy = Path.Combine(temp, "vrtm-romaudio-ffffffff");
        Put(Path.Combine(busy, "roms", "old.zip"));
        Age(busy, old);
        Put(Path.Combine(busy, "roms", "new.zip"));
        var fresh = Path.GetDirectoryName(Put(Path.Combine(temp, "vrtm-9a8b7c6d", "t.vpx")))!;

        var freed = TempLeftovers.Sweep(temp, TimeSpan.FromDays(1));
        results.Add(
            (
                !Directory.Exists(install)
                    && !Directory.Exists(rec)
                    && !Directory.Exists(update)
                    && !File.Exists(video)
                    && freed > 0,
                "temp leftovers: a day-old install, ROM recording, update folder and video copy are deleted"
            )
        );
        results.Add(
            (
                File.Exists(ini)
                    && Directory.Exists(other)
                    && Directory.Exists(named)
                    && Directory.Exists(busy)
                    && Directory.Exists(fresh),
                "temp leftovers: your kept .ini, others' folders, recent ones and one still being written stay"
            )
        );
        results.Add(
            (
                TempLeftovers.Sweep(Path.Combine(root, "no such temp"), TimeSpan.FromDays(1)) == 0,
                "temp leftovers: a missing Temp folder is nothing to do (never throws)"
            )
        );

        // empsback.nv beside the table goes into pinmame\nvram; a machine's own nvram there is kept.
        var table = Path.Combine(root, "nv-table");
        Put(Path.Combine(table, "empsback.nv"), "download");
        Put(Path.Combine(table, "taf_l7.nv"), "download");
        Put(Path.Combine(table, "pinmame", "nvram", "taf_l7.nv"), "yours");
        RomRequirements.MoveLooseNvram(table);
        results.Add(
            (
                File.ReadAllText(Path.Combine(table, "pinmame", "nvram", "empsback.nv"))
                    == "download"
                    && !File.Exists(Path.Combine(table, "empsback.nv"))
                    && File.ReadAllText(Path.Combine(table, "pinmame", "nvram", "taf_l7.nv"))
                        == "yours",
                "loose nvram: moved from beside the table into pinmame\\nvram; one already there is kept"
            )
        );
        return results;
    }
}
