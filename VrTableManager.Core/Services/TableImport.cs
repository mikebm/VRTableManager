using System.Text.Json;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <param name="Copied">Tables copied into this app's tables folder.</param>
/// <param name="Skipped">Tables left out because a folder of that name is already there.</param>
/// <param name="Failed">Tables that couldn't be copied, with why.</param>
public sealed record ImportResult(List<string> Copied, List<string> Skipped, List<string> Failed);

/// <summary>
/// Copies the tables of another VPX install (a cabinet setup, an older copy of VPX, another PC's
/// folder) into this app's tables folder, in the per-table layout VPX 10.8.1 expects. The source
/// is only read, never changed.
/// <list type="bullet">
/// <item>Table folders (already one folder per table) are copied whole.</item>
/// <item>Loose .vpx files get their own folder, with the files named like them (.directb2s, .ini,
/// .vbs …) and, from the install's shared folders, their ROM, NVRAM, AltSound, AltColor and PuP pack.</item>
/// </list>
/// </summary>
public static class TableImport
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// The folder holding the tables: the picked folder's Tables subfolder (the picked folder is the VPX
    /// install itself), else the picked folder when it has .vpx files (directly or one folder down).
    /// </summary>
    public static string? FindTablesDir(string picked)
    {
        if (!Directory.Exists(picked))
        {
            return null;
        }

        var tables = Path.Combine(picked, "Tables");
        if (Directory.Exists(tables) && HasTables(tables))
        {
            return tables;
        }

        return HasTables(picked) ? picked : null;

        static bool HasTables(string dir) =>
            Directory.EnumerateFiles(dir, "*.vpx").Any()
            || Directory
                .EnumerateDirectories(dir)
                .Any(d => Directory.EnumerateFiles(d, "*.vpx").Any());
    }

    public static ImportResult Import(
        string picked,
        AppSettings settings,
        Action<string> status,
        CancellationToken ct
    )
    {
        var tablesDir =
            FindTablesDir(picked)
            ?? throw new InvalidOperationException(
                "No .vpx tables in that folder or its Tables folder."
            );
        if (SamePath(tablesDir, settings.TablesDir))
        {
            throw new InvalidOperationException("That's this app's own tables folder.");
        }

        Directory.CreateDirectory(settings.TablesDir);

        // Where a classic install keeps what its tables share: VPinMAME\roms etc. beside Tables\.
        var root =
            Path.GetDirectoryName(Path.GetFullPath(tablesDir).TrimEnd(Path.DirectorySeparatorChar))
            ?? tablesDir;
        var pinmameDirs = new[]
        {
            Path.Combine(root, "VPinMAME"),
            Path.Combine(root, "PinMAME"),
            Path.Combine(tablesDir, "VPinMAME"),
        }
            .Where(Directory.Exists)
            .ToList();
        var pupDirs = new[]
        {
            Path.Combine(root, "PinUPSystem", "PUPVideos"),
            Path.Combine(Path.GetDirectoryName(root) ?? root, "PinUPSystem", "PUPVideos"),
            Path.Combine(tablesDir, PupPacks.FolderName),
        }
            .Where(Directory.Exists)
            .ToList();

        var result = new ImportResult([], [], []);
        var folders = Directory
            .EnumerateDirectories(tablesDir)
            .Where(d => Directory.EnumerateFiles(d, "*.vpx").Any())
            .ToList();
        var loose = Directory.EnumerateFiles(tablesDir, "*.vpx").ToList();
        var total = folders.Count + loose.Count;
        var done = 0;

        foreach (var src in folders)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(src);
            status($"Copying {name} ({++done} of {total})…");
            var dest = Path.Combine(settings.TablesDir, name);
            if (Directory.Exists(dest))
            {
                result.Skipped.Add(name);
                continue;
            }
            try
            {
                CopyTree(src, dest);
                var vpx = Directory.EnumerateFiles(dest, "*.vpx").First();
                FinishTable(dest, vpx, name, pinmameDirs, pupDirs, settings);
                result.Copied.Add(name);
            }
            catch (Exception e)
                when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result.Failed.Add($"{name}: {e.Message}");
            }
        }

        foreach (var vpxSrc in loose)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(vpxSrc);
            status($"Copying {name} ({++done} of {total})…");
            var dest = Path.Combine(settings.TablesDir, TableInstaller.SafeName(name));
            if (Directory.Exists(dest))
            {
                result.Skipped.Add(name);
                continue;
            }
            try
            {
                Directory.CreateDirectory(dest);
                // The table and the files named like it: backglass, settings, sidecar script, POV … Only that exact
                // name: "Name.*" also finds another table's "Name.VR.vpx" and its files.
                foreach (
                    var f in Directory
                        .EnumerateFiles(tablesDir, name + ".*")
                        .Where(f =>
                            Path.GetFileNameWithoutExtension(f)
                                .Equals(name, StringComparison.OrdinalIgnoreCase)
                        )
                )
                {
                    CopyFile(f, Path.Combine(dest, Path.GetFileName(f)));
                }

                FinishTable(
                    dest,
                    Path.Combine(dest, Path.GetFileName(vpxSrc)),
                    name,
                    pinmameDirs,
                    pupDirs,
                    settings
                );
                result.Copied.Add(name);
            }
            catch (Exception e)
                when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result.Failed.Add($"{name}: {e.Message}");
            }
        }
        return result;
    }

    /// <summary>Brings in what the table needs from the install's shared folders and writes its vrtm.json.</summary>
    static void FinishTable(
        string folder,
        string vpx,
        string name,
        List<string> pinmameDirs,
        List<string> pupDirs,
        AppSettings settings
    )
    {
        var manifestPath = Path.Combine(folder, "vrtm.json");
        // Copied from another VR Table Manager: its manifest already says what the table is.
        if (File.Exists(manifestPath))
        {
            return;
        }

        var rom = RomDetector.Inspect(vpx);
        var romName = rom.NoRomNeeded ? null : rom.RomName;
        if (romName != null)
        {
            foreach (var pm in pinmameDirs)
            {
                CopyIfMissing(
                    Path.Combine(pm, "roms", romName + ".zip"),
                    Path.Combine(folder, "pinmame", "roms", romName + ".zip")
                );
                CopyIfMissing(
                    Path.Combine(pm, "nvram", romName + ".nv"),
                    Path.Combine(folder, "pinmame", "nvram", romName + ".nv")
                );
                CopyTreeIfMissing(
                    Path.Combine(pm, "altsound", romName),
                    Path.Combine(folder, AltPacks.SoundFolder, romName)
                );
                // altcolor\<rom> is sorted into serum\ or vni\ by FixLayout below.
                CopyTreeIfMissing(
                    Path.Combine(pm, "altcolor", romName),
                    Path.Combine(folder, "altcolor", romName)
                );
            }
        }
        var pupName = rom.UsesPup ? rom.PupName ?? rom.GameName : null;
        if (pupName != null)
        {
            foreach (var pup in pupDirs)
            {
                CopyTreeIfMissing(
                    Path.Combine(pup, pupName),
                    Path.Combine(folder, PupPacks.FolderName, pupName)
                );
            }
        }

        AltPacks.FixLayout(folder);

        var manifest = new TableManifest
        {
            GameName = name,
            VpxFile = Path.GetFileName(vpx),
            RomNames = romName != null ? [romName] : [],
            PupName = pupName,
            InstalledAt = DateTime.Now,
        };
        // Aliases and parent sets, as for a downloaded table; then the parents from the shared roms folder.
        try
        {
            RomRequirements.Update(new InstalledTable(manifest, vpx, folder), settings);
        }
        catch
        { /* PinMAME missing or unhappy: the ROM check runs again later */
        }
        foreach (var parent in manifest.RomParents)
        {
            foreach (var pm in pinmameDirs)
            {
                CopyIfMissing(
                    Path.Combine(pm, "roms", parent + ".zip"),
                    Path.Combine(folder, "pinmame", "roms", parent + ".zip")
                );
            }
        }

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Json));
    }

    static void CopyIfMissing(string src, string dest)
    {
        if (File.Exists(src) && !File.Exists(dest))
        {
            CopyFile(src, dest);
        }
    }

    static void CopyTreeIfMissing(string src, string dest)
    {
        if (Directory.Exists(src) && !Directory.Exists(dest))
        {
            CopyTree(src, dest);
        }
    }

    static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, Path.Combine(dest, Path.GetRelativePath(src, file)));
        }
    }

    static void CopyFile(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: false);
    }

    static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase
        );
}
