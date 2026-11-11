using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using VrTableManager.Models;

namespace VrTableManager.Services;

public sealed record InstalledTable(TableManifest Manifest, string TablePath, string Folder)
{
    public bool IsVpx => Manifest.Engine == TableManifest.Vpx;

    /// <summary>
    /// The table shares its folder with other tables (a loose .vpx straight in the tables folder, a Future Pinball
    /// table in FP's Tables folder), so nothing kept per folder (lobby video, add-ons) can be this table's alone.
    /// </summary>
    public bool SharedFolder { get; init; }

    /// <summary>
    /// A VPX table in a folder of its own: media kept per table folder (lobby video, loading video, wheel art) can be
    /// its own.
    /// </summary>
    public bool HasOwnFolder => IsVpx && !SharedFolder;

    /// <summary>
    /// The ROM(s) to add when none is in pinmame/roms yet. A table loads exactly one ROM: when the
    /// script's cGameName was found this is that one name; otherwise it's VPS's candidates, any of
    /// which will do.
    /// </summary>
    public IReadOnlyList<string> MissingRoms
    {
        get
        {
            if (!IsVpx || Manifest.RomNames.Count == 0)
            {
                return [];
            }

            bool Has(string r) => File.Exists(Path.Combine(Folder, "pinmame", "roms", r + ".zip"));
            if (!Manifest.RomNames.Any(Has))
            {
                return Manifest.RomNames;
            }
            // The ROM is there; a clone may still need its parent set.
            return Manifest.RomParents.Where(p => !Has(p)).ToList();
        }
    }

    /// <summary>
    /// An install into this folder started but never finished (app closed or crashed mid-copy),
    /// so files may be missing. Reinstalling fixes it.
    /// </summary>
    public bool IsIncomplete =>
        IsVpx && File.Exists(Path.Combine(Folder, TableInstaller.InstallingMarker));

    /// <summary>What the interrupted install was installing (to reinstall it), if known.</summary>
    public InstallMarker? IncompleteInstall =>
        IsIncomplete ? TableInstaller.ReadMarker(Folder) : null;
}

/// <summary>Written into a VPX table folder while an install copies files; removed when it completes.</summary>
public sealed record InstallMarker(
    string GameId,
    string TableFileId,
    string GameName,
    DateTime StartedAt
);

/// <summary>
/// Turns a downloaded archive into a ready-to-play table:
/// VPX tables get their own folder (the layout VPX 10.8.1 expects, with
/// pinmame/roms, music, altsound and serum inside it); Future Pinball tables go to
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
            return File.Exists(p)
                ? JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(p))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<InstalledTable> InstallAsync(
        string downloaded,
        VpsGame game,
        VpsTableFile tf,
        AppSettings settings,
        Action<string> status,
        CancellationToken ct,
        InstalledTable? replacing = null
    )
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
                foreach (
                    var nested in Directory
                        .EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                        .Where(IsArchive)
                        .ToList()
                )
                {
                    if (LooksLikeRom(nested, game))
                    {
                        continue;
                    }

                    await Task.Run(
                        () =>
                            Extract(
                                nested,
                                Path.Combine(
                                    staging,
                                    "_nested",
                                    Path.GetFileNameWithoutExtension(nested)
                                )
                            ),
                        ct
                    );
                }
                tables = Find(staging, "*" + tableExt);
            }
            if (tables.Count == 0)
            {
                throw new InvalidDataException(
                    $"No {tableExt} file in the download. It may be a patch or a media pack, not a table."
                );
            }

            var main = PickMainTable(tables, tf);
            // (*.BIN entries are Stern firmware files, not PinMAME ROM names.)
            var vpsRoms = (game.RomFiles ?? [])
                .Select(r => r.Version)
                .OfType<string>()
                .Where(v =>
                    !string.IsNullOrWhiteSpace(v)
                    && !v.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                )
                .Distinct()
                .ToList();
            // The script names the one ROM this table loads; fall back to VPS's list if it can't be read.
            // Originals that don't use PinMAME need no ROM even if they set cGameName.
            // VPS's "no ROM" tag on the table file settles it either way.
            var rom = isFp ? new VpxRomInfo(null, false) : RomDetector.Inspect(main);
            var noRom = rom.NoRomNeeded || VpsCatalog.HasFeature(tf, RomRequirements.NoRomFeature);
            List<string> romNames =
                noRom ? []
                : rom.RomName != null ? [rom.RomName]
                : vpsRoms;

            status("Installing files…");
            var installed = await Task.Run(
                () =>
                    isFp
                        ? InstallFp(staging, main, settings)
                        : InstallVpx(
                            staging,
                            main,
                            game,
                            vpsRoms.Union(romNames, StringComparer.OrdinalIgnoreCase).ToList(),
                            settings,
                            tf.Id,
                            replacing
                        ),
                ct
            ); // copying can take a while; keep it off the UI thread

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
                ImgUrl =
                    !string.IsNullOrWhiteSpace(tf.ImgUrl) ? tf.ImgUrl
                    : !string.IsNullOrWhiteSpace(game.ImgUrl) ? game.ImgUrl
                    : null,
                VpxFile = Path.GetFileName(installed.tablePath),
                RomNames = isFp ? [] : romNames,
                InstalledAt = DateTime.Now,
                SourceUpdatedAt = tf.UpdatedAt,
                PupName = rom.UsesPup ? rom.PupName ?? rom.GameName : null,
                // An update keeps the folder, so add-ons installed earlier stay tracked.
                PupPack = replacing?.Manifest.PupPack,
                AltColor = replacing?.Manifest.AltColor,
                AltSound = replacing?.Manifest.AltSound,
            };

            var table = new InstalledTable(manifest, installed.tablePath, installed.folder);
            // Ask PinMAME what the ROM really is (aliases, parent sets) before the manifest is saved.
            if (!isFp && !noRom)
            {
                await Task.Run(() => RomRequirements.Update(table, settings), ct);
            }

            await File.WriteAllTextAsync(
                ManifestPathFor(installed.tablePath, isFp),
                JsonSerializer.Serialize(manifest, Json),
                ct
            );
            // Everything's in place: the folder is no longer half-installed.
            if (!isFp)
            {
                File.Delete(Path.Combine(installed.folder, InstallingMarker));
            }

            // Updating: the new version usually has a new file name ("… v1.2.vpx"). Remove the old table file so
            // only one version shows up; ROMs, NVRAM and settings in the folder stay. Only now, once the new
            // version is complete: a step above failing must not leave neither version.
            if (
                replacing != null
                && !SamePath(replacing.TablePath, installed.tablePath)
                && Path.GetExtension(replacing.TablePath)
                    .Equals(tableExt, StringComparison.OrdinalIgnoreCase)
                && File.Exists(replacing.TablePath)
            )
            {
                File.Delete(replacing.TablePath);
                File.Delete(VrDmdFix.BackupPath(replacing.TablePath));
                if (isFp && File.Exists(ManifestPathFor(replacing.TablePath, true)))
                {
                    File.Delete(ManifestPathFor(replacing.TablePath, true));
                }
            }
            if (replacing != null && !SamePath(replacing.TablePath, installed.tablePath))
            {
                KeepStatsAndCollections(
                    replacing.TablePath,
                    installed.tablePath,
                    settings.SourcePath
                );
                if (!isFp)
                {
                    KeepTableSettings(replacing.TablePath, installed.tablePath, settings);
                }
            }
            return table;
        }
        finally
        {
            try
            {
                Directory.Delete(staging, true);
            }
            catch { }
        }
    }

    /// <summary>
    /// Favourites, plays and collections are kept by table path, and an update usually brings a new file name
    /// ("… v1.2.vpx"): move them to the new path so the update doesn't lose them. Files are only written when the
    /// old path is in them. Never fails the install.
    /// </summary>
    public static void KeepStatsAndCollections(string oldPath, string newPath, string? settingsPath)
    {
        try
        {
            var stats = PlayStats.PathBeside(settingsPath);
            if (PlayStats.Load(stats).Tables.ContainsKey(PlayStats.Key(oldPath)))
            {
                PlayStats.Mutate(st => st.MoveTable(oldPath, newPath), stats);
            }

            var collections = TableCollections.PathBeside(settingsPath);
            if (TableCollections.Load(collections).Collections.Any(c => c.Contains(oldPath)))
            {
                TableCollections.Mutate(c => c.MoveTable(oldPath, newPath), collections);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The table is updated either way; at worst its star and plays start again.
        }
    }

    /// <summary>
    /// What else is tied to a VPX table's file name, kept when an update brings a new one:
    /// <list type="bullet">
    /// <item>VPX's own settings for the table (<c>&lt;table&gt;.ini</c>: its VR height and render scale, and anything
    /// else set for it). Renamed to go with the new file; when the download brought an .ini of its own, that one
    /// stays and only gets the settings it doesn't have.</item>
    /// <item>"Don't ask again" for the PuP DMD fix (<see cref="AppSettings.DmdFixDeclined"/>).</item>
    /// </list>
    /// Never fails the install.
    /// </summary>
    /// <summary>
    /// A table's own settings that are yours, not the author's: its render scale (VR Table Manager's per-table scale)
    /// and where you placed it in VR (VR Table Manager's per-table height, and VPX's own saved table position).
    /// </summary>
    static readonly HashSet<(string Section, string Key)> YourTableSettings = new(
        [
            ("Player", "AAFactor"),
            ("PlayerVR", "TableX"),
            ("PlayerVR", "TableY"),
            ("PlayerVR", "TableZ"),
            ("PlayerVR", "Orientation"),
        ],
        IniFile.SectionKeyComparer.Instance
    );

    public static void KeepTableSettings(string oldPath, string newPath, AppSettings settings)
    {
        try
        {
            var oldIni = VpxGraphics.TableIniPath(oldPath);
            var newIni = VpxGraphics.TableIniPath(newPath);
            if (File.Exists(oldIni) && !SamePath(oldIni, newIni))
            {
                if (!File.Exists(newIni))
                {
                    File.Move(oldIni, newIni);
                }
                else
                {
                    // Theirs stays, except your render scale and where you put the table in VR, which you set
                    // for this PC; settings before any [section] heading aren't VPX's and are left out.
                    var theirs = IniFile.Read(newIni);
                    foreach (
                        var section in IniFile
                            .Read(oldIni)
                            .Where(kv =>
                                kv.Key.Section != ""
                                && kv.Value != ""
                                && (
                                    YourTableSettings.Contains(kv.Key)
                                    || string.IsNullOrEmpty(theirs.GetValueOrDefault(kv.Key))
                                )
                            )
                            .GroupBy(kv => kv.Key.Section, StringComparer.OrdinalIgnoreCase)
                    )
                    {
                        IniFile.SetMany(
                            newIni,
                            section.Key,
                            section.Select(kv => (kv.Key.Key, kv.Value))
                        );
                    }
                    File.Delete(oldIni);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The table is updated either way; at worst its VR height and render scale need setting again.
        }

        var declined = settings.DmdFixDeclined.FindIndex(p => SamePath(p, oldPath));
        if (declined >= 0)
        {
            settings.DmdFixDeclined[declined] = newPath;
            try
            {
                settings.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Kept for this session; the question may come back after a restart.
            }
        }
    }

    // ---- VPX ---------------------------------------------------------------

    static (string tablePath, string folder) InstallVpx(
        string staging,
        string main,
        VpsGame game,
        List<string> romNames,
        AppSettings settings,
        string tableFileId,
        InstalledTable? replacing
    )
    {
        var folder = TableFolderFor(game, tableFileId, settings, replacing);
        Directory.CreateDirectory(folder);
        // Flag the folder until the install completes (see InstalledTable.IsIncomplete).
        File.WriteAllText(
            Path.Combine(folder, InstallingMarker),
            JsonSerializer.Serialize(
                new InstallMarker(game.Id, tableFileId, game.Name, DateTime.Now),
                Json
            )
        );

        // Everything next to the .vpx (b2s, ini, vbs, music/, pinmame/ …) comes along as-is.
        var contentRoot = Path.GetDirectoryName(main)!;
        CopyTree(contentRoot, folder);

        // Cabinet-style packs spread files around (Tables/, VPinMAME/roms/, Music/); pull those in too.
        foreach (
            var dir in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
        )
        {
            if (
                dir.StartsWith(
                    contentRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                RelocateRomFolder(dir, contentRoot, folder);
                continue;
            }
            var name = Path.GetFileName(dir).ToLowerInvariant();
            var target = name switch
            {
                "music" => Path.Combine(folder, "music"),
                // Where VPX 10.8.1's plugins look; colorizations are sorted into serum\ / vni\ below.
                "altsound" => Path.Combine(folder, AltPacks.SoundFolder),
                "altcolor" => Path.Combine(folder, "altcolor"),
                "serum" => Path.Combine(folder, AltPacks.SerumFolder),
                "vni" => Path.Combine(folder, AltPacks.VniFolder),
                "roms" => Path.Combine(folder, "pinmame", "roms"),
                "nvram" => Path.Combine(folder, "pinmame", "nvram"),
                "pupvideos" => Path.Combine(folder, PupPacks.FolderName),
                _ => null,
            };
            if (target != null)
            {
                CopyTree(dir, target);
            }
        }

        // Loose ROM zips and backglasses anywhere in the pack.
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            if (
                file.StartsWith(
                    contentRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var fn = Path.GetFileNameWithoutExtension(file);
            if (
                file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && romNames.Contains(fn, StringComparer.OrdinalIgnoreCase)
            )
            {
                CopyFile(file, Path.Combine(folder, "pinmame", "roms", Path.GetFileName(file)));
            }
            else if (file.EndsWith(".directb2s", StringComparison.OrdinalIgnoreCase))
            {
                CopyFile(file, Path.Combine(folder, Path.GetFileName(file)));
            }
        }

        // ROM zips that sat right next to the .vpx belong in pinmame/roms too.
        foreach (var zip in Directory.EnumerateFiles(folder, "*.zip"))
        {
            if (
                romNames.Contains(
                    Path.GetFileNameWithoutExtension(zip),
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                MoveFile(zip, Path.Combine(folder, "pinmame", "roms", Path.GetFileName(zip)));
            }
        }

        AltPacks.FixLayout(folder);
        // A PuP pack that came in the table's archive gets its VR layout too, when it has one.
        var pupDir = Path.Combine(folder, PupPacks.FolderName);
        if (Directory.Exists(pupDir))
        {
            foreach (var pack in PupPacks.PackRoots(pupDir))
            {
                PupPackOptions.SetUpForVr(pack);
            }
        }

        return (Path.Combine(folder, Path.GetFileName(main)), folder);
    }

    /// <summary>
    /// Where a VPX table installs: an update stays in the folder it's in (the catalog may have renamed the game
    /// since); the same table file again (a reinstall, or finishing a cut-off install) goes back into its folder;
    /// another table file of an installed game (its VR build beside the desktop one) gets a folder of its own,
    /// "Name (2)", so neither table's files or vrtm.json are overwritten.
    /// </summary>
    static string TableFolderFor(
        VpsGame game,
        string tableFileId,
        AppSettings settings,
        InstalledTable? replacing
    )
    {
        if (
            replacing is { IsVpx: true, SharedFolder: false }
            && Directory.Exists(replacing.Folder)
            && !SamePath(replacing.Folder, settings.TablesDir)
        )
        {
            return replacing.Folder;
        }

        var baseName = SafeName(game.DisplayTitle);
        for (var n = 1; ; n++)
        {
            var folder = Path.Combine(settings.TablesDir, n == 1 ? baseName : $"{baseName} ({n})");
            if (!Directory.Exists(folder))
            {
                return folder;
            }

            var owner =
                ReadManifest(Path.Combine(folder, ManifestName))?.TableFileId
                ?? ReadMarker(folder)?.TableFileId;
            if (
                owner is { Length: > 0 }
                    ? owner == tableFileId
                    : !Directory.EnumerateFiles(folder, "*.vpx").Any()
            )
            {
                return folder;
            }
        }
    }

    /// <summary>
    /// A roms\ or nvram\ folder that came beside the .vpx (or in VPinMAME\ beside it) was copied with the table under
    /// the same path; PinMAME only looks in pinmame\roms and pinmame\nvram, so it's moved there.
    /// </summary>
    static void RelocateRomFolder(string dir, string contentRoot, string folder)
    {
        var rel = Path.GetRelativePath(contentRoot, dir);
        var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[^1].ToLowerInvariant();
        if (name is not ("roms" or "nvram"))
        {
            return;
        }

        if (
            parts.Length > 2
            || (parts.Length == 2 && parts[0].ToLowerInvariant() is not ("vpinmame" or "pinmame"))
        )
        {
            return;
        }

        var copied = Path.Combine(folder, rel);
        var target = Path.Combine(folder, "pinmame", name);
        if (SamePath(copied, target) || !Directory.Exists(copied))
        {
            return;
        }

        CopyTree(dir, target);
        try
        {
            Directory.Delete(copied, recursive: true);
            if (parts.Length == 2)
            {
                Addons.DeleteIfEmpty(Path.GetDirectoryName(copied)!);
            }
        }
        catch (IOException)
        { /* the copy in pinmame\ is what counts */
        }
    }

    public static string AddRom(InstalledTable table, string romZip)
    {
        var dest = Path.Combine(table.Folder, "pinmame", "roms", Path.GetFileName(romZip));
        CopyFile(romZip, dest);
        return dest;
    }

    // ---- Future Pinball ----------------------------------------------------

    static (string tablePath, string folder) InstallFp(
        string staging,
        string main,
        AppSettings settings
    )
    {
        if (string.IsNullOrWhiteSpace(settings.FpDir) || !Directory.Exists(settings.FpDir))
        {
            throw new InvalidOperationException(
                "Set your Future Pinball folder on the Setup tab first."
            );
        }

        var tablesDir = Path.Combine(settings.FpDir, "Tables");
        var libsDir = Path.Combine(settings.FpDir, "Libraries");
        var contentRoot = Path.GetDirectoryName(main)!;

        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".fpl")
            {
                CopyFile(file, Path.Combine(libsDir, Path.GetFileName(file)));
            }
            else if (
                Path.GetDirectoryName(file)!.Equals(contentRoot, StringComparison.OrdinalIgnoreCase)
            )
            {
                CopyFile(file, Path.Combine(tablesDir, Path.GetFileName(file))); // .fpt plus its .vbs/.ini/.directb2s
            }
        }

        foreach (
            var dir in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
        )
        {
            var name = Path.GetFileName(dir).ToLowerInvariant();
            if (name == "music")
            {
                CopyTree(dir, Path.Combine(settings.FpDir, "Music"));
            }
            else if (
                name == "cfg"
                && Path.GetFileName(Path.GetDirectoryName(dir))!
                    .Equals("BAM", StringComparison.OrdinalIgnoreCase)
            )
            {
                CopyTree(dir, Path.Combine(settings.FpDir, "BAM", "cfg"));
            }
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
                var vpx =
                    m != null && File.Exists(Path.Combine(folder, m.VpxFile))
                        ? Path.Combine(folder, m.VpxFile)
                        : Directory.EnumerateFiles(folder, "*.vpx").FirstOrDefault();
                if (vpx == null && ReadMarker(folder) is { } cut)
                {
                    // Install cut off before the table file was copied: still list it, so it can be reinstalled.
                    result.Add(
                        new InstalledTable(
                            new TableManifest
                            {
                                GameId = cut.GameId,
                                TableFileId = cut.TableFileId,
                                GameName = cut.GameName,
                            },
                            folder,
                            folder
                        )
                    );
                    continue;
                }
                if (vpx == null)
                {
                    continue;
                }

                m ??= new TableManifest
                {
                    GameName = Path.GetFileName(folder),
                    VpxFile = Path.GetFileName(vpx),
                };
                result.Add(new InstalledTable(m, vpx, folder));
            }
            // Loose .vpx files from a manual install (not in their own folder).
            foreach (var vpx in Directory.EnumerateFiles(settings.TablesDir, "*.vpx"))
            {
                result.Add(
                    new InstalledTable(
                        new TableManifest
                        {
                            GameName = Path.GetFileNameWithoutExtension(vpx),
                            VpxFile = Path.GetFileName(vpx),
                        },
                        vpx,
                        settings.TablesDir
                    )
                    {
                        SharedFolder = true,
                    }
                );
            }
        }

        var fpTables = Path.Combine(settings.FpDir, "Tables");
        if (!string.IsNullOrWhiteSpace(settings.FpDir) && Directory.Exists(fpTables))
        {
            foreach (var fpt in Directory.EnumerateFiles(fpTables, "*.fpt"))
            {
                var m =
                    ReadManifest(ManifestPathFor(fpt, true))
                    ?? new TableManifest
                    {
                        Engine = TableManifest.FuturePinball,
                        GameName = Path.GetFileNameWithoutExtension(fpt),
                        VpxFile = Path.GetFileName(fpt),
                    };
                result.Add(new InstalledTable(m, fpt, fpTables) { SharedFolder = true });
            }
        }

        foreach (var engine in Engines.All)
        {
            try
            {
                result.AddRange(engine.ListExtraTables(settings));
            }
            catch
            { /* one engine failing to list shouldn't hide the rest */
            }
        }

        return result.OrderBy(t => t.Manifest.GameName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Remove(InstalledTable table)
    {
        if (!Engines.For(table.Manifest.Engine).ManagesFiles)
        {
            throw new InvalidOperationException(
                "This game belongs to a store; uninstall it there."
            );
        }

        if (table.IsIncomplete && SamePath(table.TablePath, table.Folder))
        {
            // Cut off before the table file arrived; the marker shows this app created the folder.
            Directory.Delete(table.Folder, recursive: true);
            return;
        }
        if (
            table.IsVpx
            && Path.GetFullPath(table.Folder)
                != Path.GetFullPath(Path.GetDirectoryName(table.TablePath)!)
        )
        {
            throw new InvalidOperationException("Unexpected table layout; not removing.");
        }

        if (table.IsVpx && File.Exists(Path.Combine(table.Folder, ManifestName)))
        {
            Directory.Delete(table.Folder, recursive: true); // folder was created by this app
        }
        else
        {
            File.Delete(table.TablePath);
            File.Delete(VrDmdFix.BackupPath(table.TablePath)); // no-op when there's none
            var manifest = ManifestPathFor(table.TablePath, !table.IsVpx);
            if (File.Exists(manifest))
            {
                File.Delete(manifest);
            }
        }
    }

    // ---- Helpers -----------------------------------------------------------

    static string ManifestPathFor(string tablePath, bool isFp) =>
        isFp
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
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<TableManifest>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    static string PickMainTable(List<string> tables, VpsTableFile tf)
    {
        if (tables.Count == 1)
        {
            return tables[0];
        }

        var wantVr = VpsCatalog.HasFeature(tf, "VR");
        bool IsVrName(string p) =>
            Path.GetFileNameWithoutExtension(p).Contains("VR", StringComparison.Ordinal);
        return tables.FirstOrDefault(p => IsVrName(p) == wantVr) ?? tables[0];
    }

    static bool IsArchive(string path) =>
        ArchiveExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    static bool LooksLikeRom(string path, VpsGame game) =>
        game.RomFiles?.Any(r =>
            string.Equals(
                r.Version,
                Path.GetFileNameWithoutExtension(path),
                StringComparison.OrdinalIgnoreCase
            )
        ) == true;

    static List<string> Find(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList();

    static void Unpack(string file, string staging)
    {
        if (IsArchive(file))
        {
            Extract(file, staging);
        }
        else
        {
            File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
        }
    }

    public static void Extract(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        // 7-Zip unpacks a .tar.gz only to the inner .tar, so gzip stays with SharpCompress, which reads through both.
        if (
            !Path.GetExtension(archivePath).Equals(".gz", StringComparison.OrdinalIgnoreCase)
            && TryNative7Zip(archivePath, destDir, root)
        )
        {
            return;
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        // 7z (and solid RAR) packs many files into one compressed stream. Opening entries one by one
        // makes each restart that stream from the top, so a big table pack takes hours; read it once, in order.
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (Target(reader.Entry, destDir, root) is not { } dest)
                {
                    continue;
                }

                using var src = reader.OpenEntryStream();
                using var dst = File.Create(dest);
                src.CopyTo(dst);
            }
            return;
        }
        foreach (var entry in archive.Entries)
        {
            if (Target(entry, destDir, root) is not { } dest)
            {
                continue;
            }

            using var src = entry.OpenEntryStream();
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    /// <summary>
    /// Unpacks with the bundled 7-Zip library (SevenZipExtractor's 7z.dll), several times faster than
    /// SharpCompress's managed LZMA decoder (a 330 MB table pack: ~1 s against ~10 s) and one pass over
    /// solid archives. False if it can't open or finish the archive, so the caller falls back.
    /// </summary>
    static bool TryNative7Zip(string archivePath, string destDir, string root)
    {
        try
        {
            using var archive = new SevenZipExtractor.ArchiveFile(archivePath);
            archive.Extract(e => Target(e.IsFolder, e.FileName, destDir, root));
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string? Target(IEntry entry, string destDir, string root) =>
        Target(entry.IsDirectory, entry.Key, destDir, root);

    /// <summary>Where an entry lands under <paramref name="destDir"/>, its folder created; null for folders and path-traversal entries.</summary>
    static string? Target(bool isFolder, string? key, string destDir, string root)
    {
        if (isFolder || string.IsNullOrEmpty(key))
        {
            return null;
        }

        var dest = Path.GetFullPath(Path.Combine(destDir, key));
        if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        return dest;
    }

    static void CopyTree(string src, string dest)
    {
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, Path.Combine(dest, Path.GetRelativePath(src, file)));
        }
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
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim().TrimEnd('.');
    }
}
