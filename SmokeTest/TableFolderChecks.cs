using System.IO.Compression;
using System.Text.Json;
using VrTableManager.Services;

/// <summary>
/// More than one table folder (AppSettings.OtherTablesDirs): tables are listed from all of them, loose .vpx files in
/// any of them count as sharing a folder, a folder that isn't there is passed over, and a backup's table files
/// restore into whichever folder holds that table now.
/// </summary>
public static class TableFolderChecks
{
    public static IEnumerable<(bool ok, string name)> Run(string root)
    {
        var first = Path.Combine(root, "folders-c", "Tables");
        var second = Path.Combine(root, "folders-d", "Tables");
        var gone = Path.Combine(root, "folders-unplugged", "Tables");
        var inFirst = Path.Combine(first, "Alpha (Bally 1980)");
        var inSecond = Path.Combine(second, "Beta (Williams 1992)");
        Directory.CreateDirectory(inFirst);
        Directory.CreateDirectory(inSecond);
        File.WriteAllText(Path.Combine(inFirst, "Alpha.vpx"), "");
        File.WriteAllText(Path.Combine(inSecond, "Beta.vpx"), "");
        File.WriteAllText(Path.Combine(second, "Loose.vpx"), "");

        var s = new AppSettings
        {
            TablesDir = first,
            OtherTablesDirs = [second, gone, second + "\\", first],
        };

        yield return (
            s.AllTablesDirs.Count == 3 && s.AllTablesDirs[0] == first,
            "Table folders: install one first, each folder once"
        );

        var scanned = TableInstaller.Scan(s).Where(t => t.IsVpx).ToList();
        yield return (
            scanned.Any(t => t.Folder == inFirst)
                && scanned.Any(t => t.Folder == inSecond)
                && scanned.Any(t => t.Manifest.GameName == "Loose"),
            "Table folders: tables listed from every folder, an unplugged one passed over"
        );

        var loose = scanned.Single(t => t.Manifest.GameName == "Loose");
        var beta = scanned.Single(t => t.Folder == inSecond);
        yield return (
            loose.SharedFolder
                && loose.Folder == second
                && !Addons.CanHold(loose, s)
                && Addons.CanHold(beta, s),
            "Table folders: a loose .vpx in another folder shares it; a table's own folder there takes add-ons"
        );

        yield return (
            DiskUsage.SizeOf(loose, s) == 0 && s.IsTablesRoot(second) && !s.IsTablesRoot(inSecond),
            "Table folders: a loose table's size is its file, not the whole other folder"
        );

        var json = JsonSerializer.Serialize(s);
        yield return (
            json.Contains("OtherTablesDirs") && !json.Contains("AllTablesDirs"),
            "Table folders: the extra folders are saved, the combined list isn't"
        );

        // A backup knows tables by folder name, so Beta's NVRAM goes back into the second folder.
        var zipPath = Path.Combine(root, "folders-backup.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("tables/Beta (Williams 1992)/pinmame/nvram/beta.nv");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("scores");
            }
            zip.CreateEntry("tables/Gamma (Stern 2005)/Gamma.ini").Open().Dispose();
        }
        var r = Backup.Restore(zipPath, s);
        yield return (
            File.Exists(Path.Combine(inSecond, "pinmame", "nvram", "beta.nv"))
                && !Directory.Exists(Path.Combine(first, "Beta (Williams 1992)"))
                && r.Skipped.Contains("Gamma (Stern 2005)"),
            "Table folders: a backup restores a table's files into the folder it's in now"
        );
    }
}
