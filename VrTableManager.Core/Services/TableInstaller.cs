using System.Text.Json;
using SharpCompress.Archives;
using VrTableManager.Models;

namespace VrTableManager.Services;

public sealed record InstalledTable(TableManifest Manifest, string TablePath, string Folder)
{
    public bool IsVpx => Manifest.Engine == TableManifest.Vpx;

    /// <summary>
    /// The ROM(s) to add when none is in pinmame/roms yet. A table loads exactly one ROM: when the
    /// script's cGameName was found this is that one name; otherwise it's VPS's candidates, any of
    /// which will do.
    /// </summary>
    public IReadOnlyList<string> MissingRoms =>
        IsVpx && Manifest.RomNames.Count > 0
              && !Manifest.RomNames.Any(r => File.Exists(Path.Combine(Folder, "pinmame", "roms", r + ".zip")))
            ? Manifest.RomNames
            : [];

    /// <summary>
    /// An install into this folder started but never finished (app closed or crashed mid-copy),
    /// so files may be missing. Reinstalling fixes it.
    /// </summary>
    public bool IsIncomplete => IsVpx && File.Exists(Path.Combine(Folder, TableInstaller.InstallingMarker));

    /// <summary>What the interrupted install was installing (to reinstall it), if known.</summary>
    public InstallMarker? IncompleteInstall => IsIncomplete ? TableInstaller.ReadMarker(Folder) : null;
}

/// <summary>Written into a VPX table folder while an install copies files; removed when it completes.</summary>
public sealed record InstallMarker(string GameId, string TableFileId, string GameName, DateTime StartedAt);

/// <summary>
/// Turns a downloaded archive into a ready-to-play table:
/// VPX tables get their own folder (the layout VPX 10.8.1 expects, with
/// pinmame/roms, music and altsound inside it); Future Pinball tables go to
/// the FP Tables folder with libraries in Libraries.
/// </summary>
public static class TableInstaller
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly string[] ArchiveExts = [".zip", ".rar", ".7z", ".tar", ".gz"];
    const string ManifestName = "vrtm.json";
    /// <summary>Present in a VPX table folder only while an install is copying into it.</summary>
    public const string InstallingMarker = "vrtm-installing.json";

    public static InstallMarker? ReadMarker(string folder)
    {
        try
        {
            var p = Path.Combine(folder, InstallingMarker);
            return File.Exists(p) ? JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public static async Task<InstalledTable> InstallAsync(string downloaded, VpsGame game, VpsTableFile tf,
        AppSettings settings, Action<string> status, CancellationToken ct, InstalledTable? replacing = null)
    {
        var isFp = !VpsCatalog.IsVpx(tf);
        var tableExt = isFp ? ".fpt" : ".vpx";
        var staging = Path.Combine(Path.GetTempPath(), "vrtm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            status("Unpacking…");
            await Task.Run(() => Unpack(downloaded, staging), ct);

            var tables = Find(staging, "*" + tableExt);
            if (tables.Count == 0)
            {
                // Some packs nest the real archive inside another one.
                foreach (var nested in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Where(IsArchive).ToList())
                {
                    if (LooksLikeRom(nested, game)) continue;
                    await Task.Run(() => Extract(nested, Path.Combine(staging, "_nested", Path.GetFileNameWithoutExtension(nested))), ct);
                }
                tables = Find(staging, "*" + tableExt);
            }
            if (tables.Count == 0)
                throw new InvalidDataException($"No {tableExt} file in the download. It may be a patch or a media pack, not a table.");

            var main = PickMainTable(tables, tf);
            // (*.BIN entries are Stern firmware files, not PinMAME ROM names.)
            var vpsRoms = (game.RomFiles ?? []).Select(r => r.Version).OfType<string>()
                .Where(v => !string.IsNullOrWhiteSpace(v) && !v.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            // The script names the one ROM this table loads; fall back to VPS's list if it can't be read.
            // Originals that don't use PinMAME need no ROM even if they set cGameName.
            var rom = isFp ? new VpxRomInfo(null, false) : RomDetector.Inspect(main);
            List<string> romNames = rom.NoRomNeeded ? [] : rom.GameName != null ? [rom.GameName] : vpsRoms;

            status("Installing files…");
            var installed = await Task.Run(() => isFp
                ? InstallFp(staging, main, settings)
                : InstallVpx(staging, main, game, vpsRoms.Union(romNames, StringComparer.OrdinalIgnoreCase).ToList(), settings, tf.Id), ct); // copying can take a while; keep it off the UI thread

            var manifest = new TableManifest
            {
                Engine = isFp ? TableManifest.FuturePinball : TableManifest.Vpx,
                GameId = game.Id,
                GameName = game.Name,
                Manufacturer = game.Manufacturer,
                Year = game.Year,
                TableFileId = tf.Id,
                Version = tf.Version,
                Authors = tf.Authors ?? [],
                Features = tf.Features ?? [],
                SourceUrl = tf.Urls?.FirstOrDefault()?.Url,
                ImgUrl = tf.ImgUrl ?? game.ImgUrl,
                VpxFile = Path.GetFileName(installed.tablePath),
                RomNames = isFp ? [] : romNames,
                InstalledAt = DateTime.Now,
                SourceUpdatedAt = tf.UpdatedAt,
            };

            // Updating: the new version usually has a new file name ("… v1.2.vpx"). Remove the old
            // table file so only one version shows up; ROMs, NVRAM and settings in the folder stay.
            if (replacing != null && !SamePath(replacing.TablePath, installed.tablePath)
                && Path.GetExtension(replacing.TablePath).Equals(tableExt, StringComparison.OrdinalIgnoreCase)
                && File.Exists(replacing.TablePath))
            {
                File.Delete(replacing.TablePath);
                if (isFp && File.Exists(ManifestPathFor(replacing.TablePath, true))) File.Delete(ManifestPathFor(replacing.TablePath, true));
            }

            await File.WriteAllTextAsync(ManifestPathFor(installed.tablePath, isFp), JsonSerializer.Serialize(manifest, Json), ct);
            // Everything's in place: the folder is no longer half-installed.
            if (!isFp) File.Delete(Path.Combine(installed.folder, InstallingMarker));
            return new InstalledTable(manifest, installed.tablePath, installed.folder);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }
    }

    // ---- VPX ---------------------------------------------------------------

    static (string tablePath, string folder) InstallVpx(string staging, string main, VpsGame game,
        List<string> romNames, AppSettings settings, string tableFileId)
    {
        var folder = Path.Combine(settings.TablesDir, SafeName(game.DisplayTitle));
        Directory.CreateDirectory(folder);
        // Flag the folder until the install completes (see InstalledTable.IsIncomplete).
        File.WriteAllText(Path.Combine(folder, InstallingMarker),
            JsonSerializer.Serialize(new InstallMarker(game.Id, tableFileId, game.Name, DateTime.Now), Json));

        // Everything next to the .vpx (b2s, ini, vbs, music/, pinmame/ …) comes along as-is.
        var contentRoot = Path.GetDirectoryName(main)!;
        CopyTree(contentRoot, folder);

        // Cabinet-style packs spread files around (Tables/, VPinMAME/roms/, Music/); pull those in too.
        foreach (var dir in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories))
        {
            if (dir.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(dir).ToLowerInvariant();
            var target = name switch
            {
                "music" => Path.Combine(folder, "music"),
                "altsound" => Path.Combine(folder, "pinmame", "altsound"),
                "altcolor" => Path.Combine(folder, "pinmame", "altcolor"),
                "roms" => Path.Combine(folder, "pinmame", "roms"),
                "nvram" => Path.Combine(folder, "pinmame", "nvram"),
                _ => null,
            };
            if (target != null) CopyTree(dir, target);
        }

        // Loose ROM zips and backglasses anywhere in the pack.
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var fn = Path.GetFileNameWithoutExtension(file);
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && romNames.Contains(fn, StringComparer.OrdinalIgnoreCase))
                CopyFile(file, Path.Combine(folder, "pinmame", "roms", Path.GetFileName(file)));
            else if (file.EndsWith(".directb2s", StringComparison.OrdinalIgnoreCase))
                CopyFile(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        // ROM zips that sat right next to the .vpx belong in pinmame/roms too.
        foreach (var zip in Directory.EnumerateFiles(folder, "*.zip"))
        {
            if (romNames.Contains(Path.GetFileNameWithoutExtension(zip), StringComparer.OrdinalIgnoreCase))
                MoveFile(zip, Path.Combine(folder, "pinmame", "roms", Path.GetFileName(zip)));
        }

        return (Path.Combine(folder, Path.GetFileName(main)), folder);
    }

    public static string AddRom(InstalledTable table, string romZip)
    {
        var dest = Path.Combine(table.Folder, "pinmame", "roms", Path.GetFileName(romZip));
        CopyFile(romZip, dest);
        return dest;
    }

    // ---- Future Pinball ----------------------------------------------------

    static (string tablePath, string folder) InstallFp(string staging, string main, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FpDir) || !Directory.Exists(settings.FpDir))
            throw new InvalidOperationException("Set your Future Pinball folder on the Setup tab first.");

        var tablesDir = Path.Combine(settings.FpDir, "Tables");
        var libsDir = Path.Combine(settings.FpDir, "Libraries");
        var contentRoot = Path.GetDirectoryName(main)!;

        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".fpl") CopyFile(file, Path.Combine(libsDir, Path.GetFileName(file)));
            else if (Path.GetDirectoryName(file)!.Equals(contentRoot, StringComparison.OrdinalIgnoreCase))
                CopyFile(file, Path.Combine(tablesDir, Path.GetFileName(file))); // .fpt plus its .vbs/.ini/.directb2s
        }

        foreach (var dir in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir).ToLowerInvariant();
            if (name == "music") CopyTree(dir, Path.Combine(settings.FpDir, "Music"));
            else if (name == "cfg" && Path.GetFileName(Path.GetDirectoryName(dir))!.Equals("BAM", StringComparison.OrdinalIgnoreCase))
                CopyTree(dir, Path.Combine(settings.FpDir, "BAM", "cfg"));
        }

        return (Path.Combine(tablesDir, Path.GetFileName(main)), tablesDir);
    }

    // ---- Listing / removal -------------------------------------------------

    public static List<InstalledTable> Scan(AppSettings settings)
    {
        var result = new List<InstalledTable>();

        if (Directory.Exists(settings.TablesDir))
        {
            foreach (var folder in Directory.EnumerateDirectories(settings.TablesDir))
            {
                var m = ReadManifest(Path.Combine(folder, ManifestName));
                var vpx = m != null && File.Exists(Path.Combine(folder, m.VpxFile))
                    ? Path.Combine(folder, m.VpxFile)
                    : Directory.EnumerateFiles(folder, "*.vpx").FirstOrDefault();
                if (vpx == null && ReadMarker(folder) is { } cut)
                {
                    // Install cut off before the table file was copied: still list it, so it can be reinstalled.
                    result.Add(new InstalledTable(new TableManifest { GameId = cut.GameId, TableFileId = cut.TableFileId, GameName = cut.GameName },
                        folder, folder));
                    continue;
                }
                if (vpx == null) continue;
                m ??= new TableManifest { GameName = Path.GetFileName(folder), VpxFile = Path.GetFileName(vpx) };
                result.Add(new InstalledTable(m, vpx, folder));
            }
            // Loose .vpx files from a manual install (not in their own folder).
            foreach (var vpx in Directory.EnumerateFiles(settings.TablesDir, "*.vpx"))
                result.Add(new InstalledTable(new TableManifest { GameName = Path.GetFileNameWithoutExtension(vpx), VpxFile = Path.GetFileName(vpx) },
                    vpx, settings.TablesDir));
        }

        var fpTables = Path.Combine(settings.FpDir, "Tables");
        if (!string.IsNullOrWhiteSpace(settings.FpDir) && Directory.Exists(fpTables))
        {
            foreach (var fpt in Directory.EnumerateFiles(fpTables, "*.fpt"))
            {
                var m = ReadManifest(ManifestPathFor(fpt, true)) ?? new TableManifest
                {
                    Engine = TableManifest.FuturePinball,
                    GameName = Path.GetFileNameWithoutExtension(fpt),
                    VpxFile = Path.GetFileName(fpt),
                };
                result.Add(new InstalledTable(m, fpt, fpTables));
            }
        }

        foreach (var engine in Engines.All)
        {
            try { result.AddRange(engine.ListExtraTables(settings)); }
            catch { /* one engine failing to list shouldn't hide the rest */ }
        }

        return result.OrderBy(t => t.Manifest.GameName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Remove(InstalledTable table)
    {
        if (!Engines.For(table.Manifest.Engine).ManagesFiles)
            throw new InvalidOperationException("This game belongs to a store; uninstall it there.");
        if (table.IsIncomplete && SamePath(table.TablePath, table.Folder))
        {
            // Cut off before the table file arrived; the marker shows this app created the folder.
            Directory.Delete(table.Folder, recursive: true);
            return;
        }
        if (table.IsVpx && Path.GetFullPath(table.Folder) != Path.GetFullPath(Path.GetDirectoryName(table.TablePath)!))
            throw new InvalidOperationException("Unexpected table layout; not removing.");

        if (table.IsVpx && File.Exists(Path.Combine(table.Folder, ManifestName)))
            Directory.Delete(table.Folder, recursive: true); // folder was created by this app
        else
        {
            File.Delete(table.TablePath);
            var manifest = ManifestPathFor(table.TablePath, !table.IsVpx);
            if (File.Exists(manifest)) File.Delete(manifest);
        }
    }

    // ---- Helpers -----------------------------------------------------------

    static string ManifestPathFor(string tablePath, bool isFp) => isFp
        ? Path.ChangeExtension(tablePath, ".vrtm.json")
        : Path.Combine(Path.GetDirectoryName(tablePath)!, ManifestName);

    /// <summary>Rewrites a table's vrtm.json (e.g. after narrowing its ROM name).</summary>
    public static void SaveManifest(InstalledTable table)
    {
        var path = ManifestPathFor(table.TablePath, !table.IsVpx);
        File.WriteAllText(path, JsonSerializer.Serialize(table.Manifest, Json));
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static TableManifest? ReadManifest(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<TableManifest>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    static string PickMainTable(List<string> tables, VpsTableFile tf)
    {
        if (tables.Count == 1) return tables[0];
        var wantVr = VpsCatalog.HasFeature(tf, "VR");
        bool IsVrName(string p) => Path.GetFileNameWithoutExtension(p).Contains("VR", StringComparison.Ordinal);
        return tables.FirstOrDefault(p => IsVrName(p) == wantVr) ?? tables[0];
    }

    static bool IsArchive(string path) => ArchiveExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    static bool LooksLikeRom(string path, VpsGame game) =>
        game.RomFiles?.Any(r => string.Equals(r.Version, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)) == true;

    static List<string> Find(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList();

    static void Unpack(string file, string staging)
    {
        if (IsArchive(file)) Extract(file, staging);
        else File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
    }

    public static void Extract(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
            var dest = Path.GetFullPath(Path.Combine(destDir, entry.Key));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // skip path-traversal entries
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = entry.OpenEntryStream();
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    static void CopyTree(string src, string dest)
    {
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(dest, Path.GetRelativePath(src, file)));
    }

    static void CopyFile(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: true);
    }

    static void MoveFile(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(src, dest, overwrite: true);
    }

    public static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }
}
