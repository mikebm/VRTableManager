using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// DMD colorizations and AltSound packs from the catalog, for ROM tables. VPX 10.8.1's plugins read them
/// from the table folder (docs\FileLayout.md), in a folder named after the ROM the table runs:
/// <list type="bullet">
/// <item>Serum: <c>serum\&lt;rom&gt;\&lt;rom&gt;.cRZ</c> (or .cROM);</item>
/// <item>Pin2DMD palettes: <c>vni\&lt;rom&gt;\&lt;rom&gt;.pal / .vni</c> (the VNI plugin can't read encrypted .pac files);</item>
/// <item>AltSound: <c>altsound\&lt;rom&gt;\</c> holding altsound.csv, g-sound.csv or the legacy PinSound folders.</item>
/// </list>
/// Older layouts (altcolor\, pinmame\altcolor\, pinmame\altsound\) are moved there by <see cref="FixLayout"/>.
/// </summary>
public static class AltPacks
{
    public const string SerumFolder = "serum";
    public const string VniFolder = "vni";
    public const string SoundFolder = "altsound";
    static readonly string[] SerumExts = [".crz", ".crom", ".cromc"];
    static readonly string[] VniExts = [".vni", ".pal"];
    static readonly string[] SoundCsvs = ["altsound.csv", "g-sound.csv"];

    /// <summary>Folders of the legacy PinSound layout (sounds sorted by type, no CSV).</summary>
    static readonly string[] LegacySoundFolders = ["jingle", "music", "sfx", "single", "voice"];

    /// <summary>
    /// The catalog's colorizations (best first: for this table's ROM, Serum, newest) or AltSound packs (newest first)
    /// that can be downloaded and used: ROM patches and PAC-only colorizations are left out. Empty for tables without a ROM.
    /// </summary>
    public static List<VpsFile> SourcesFor(VpsGame game, InstalledTable t, AddonKind kind)
    {
        if (t.Manifest.RomNames.Count == 0)
        {
            return [];
        }

        if (kind == AddonKind.AltSound)
        {
            return (game.AltSoundFiles ?? [])
                .Where(f => Addons.UsableUrl(f) != null)
                .OrderByDescending(f => f.UpdatedAt ?? 0)
                .ToList<VpsFile>();
        }

        return (game.AltColorFiles ?? [])
            .Where(f =>
                Addons.UsableUrl(f) != null
                && !Mentions(f.Type, "patch")
                && !Mentions(f.Type, "pac")
            )
            .OrderBy(PacOnlyFiles.IsKnown)
            .ThenByDescending(f => ForTable(f, t))
            .ThenByDescending(f => Mentions(f.Type, "serum"))
            .ThenByDescending(f => f.UpdatedAt ?? 0)
            .ToList<VpsFile>();
    }

    /// <summary>"Serum 2.0 · v2.0 by Mustang1961 (for afm_113b)"</summary>
    public static string Describe(VpsFile f, InstalledTable t)
    {
        var text = Addons.Describe(f, f is VpsAltColorFile ? "Colorization" : "AltSound");
        if (f is not VpsAltColorFile c)
        {
            return text;
        }

        if (!string.IsNullOrWhiteSpace(c.Type))
        {
            text = $"{c.Type.Trim()} · {text}";
        }

        if (PacOnlyFiles.IsKnown(c))
        {
            text += " — PAC only, VPX can't use it";
        }

        return !string.IsNullOrWhiteSpace(c.Folder) && !ForTable(c, t)
            ? $"{text} (made for {c.Folder})"
            : text;
    }

    static bool ForTable(VpsFile f, InstalledTable t) =>
        f is VpsAltColorFile { Folder: { } rom }
        && (
            t.Manifest.RomNames.Contains(rom, StringComparer.OrdinalIgnoreCase)
            || string.Equals(t.Manifest.RomAlias, rom, StringComparison.OrdinalIgnoreCase)
        );

    static bool Mentions(string? text, string word) =>
        text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The ROM name the plugins look the files up by: the name the script runs (an alias such as "afm_113b_me" when
    /// it has one), else the catalog file's own ROM when the table could use it, else the table's first ROM.
    /// </summary>
    public static string? GameName(InstalledTable t, VpsFile? f = null) =>
        t.Manifest.RomAlias
        ?? (
            f is VpsAltColorFile { Folder: { } rom }
            && t.Manifest.RomNames.Contains(rom, StringComparer.OrdinalIgnoreCase)
                ? t.Manifest.RomNames.First(r => r.Equals(rom, StringComparison.OrdinalIgnoreCase))
                : null
        )
        ?? t.Manifest.RomNames.FirstOrDefault();

    /// <summary>Installed colorizations ("serum\afm_113b", "vni\taf_l7") or AltSound folders ("afm_113b").</summary>
    public static AddonState StateOf(InstalledTable t, AddonKind kind)
    {
        var folders = new List<string>();
        if (kind == AddonKind.AltColor)
        {
            foreach (var (loc, exts) in new[] { (SerumFolder, SerumExts), (VniFolder, VniExts) })
            {
                var root = Path.Combine(t.Folder, loc);
                if (Directory.Exists(root))
                {
                    folders.AddRange(
                        Directory
                            .EnumerateDirectories(root)
                            .Where(d => HasFile(d, exts))
                            .Select(d => loc + "\\" + Path.GetFileName(d))
                    );
                }
            }
        }
        else
        {
            var root = Path.Combine(t.Folder, SoundFolder);
            if (Directory.Exists(root))
            {
                folders.AddRange(
                    Directory
                        .EnumerateDirectories(root)
                        .Where(d =>
                            Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any()
                        )
                        .Select(d => Path.GetFileName(d)!)
                );
            }
        }
        folders.Sort(StringComparer.OrdinalIgnoreCase);
        var rec = Addons.Record(t, kind);
        var tracked =
            rec != null && folders.Contains(Display(rec, kind), StringComparer.OrdinalIgnoreCase)
                ? rec
                : null;
        return new(tracked, folders);
    }

    /// <summary>How a record shows in <see cref="AddonState.Folders"/>.</summary>
    static string Display(InstalledAddon rec, AddonKind kind) =>
        kind == AddonKind.AltColor ? $"{rec.Location}\\{rec.Folder}" : rec.Folder;

    /// <summary>
    /// Installs a downloaded colorization or AltSound pack under the table's ROM name, replacing the one this app
    /// installed before and anything already in that folder. Returns what to record in vrtm.json.
    /// </summary>
    public static InstalledAddon Install(
        AddonKind kind,
        string downloaded,
        InstalledTable table,
        VpsFile file,
        Action<string>? status = null
    )
    {
        var game =
            GameName(table, file)
            ?? throw new InvalidOperationException(
                "This table doesn't use a ROM, so it has no DMD colours or ROM sounds to replace."
            );
        var staging = Addons.StagingDir(kind == AddonKind.AltColor ? "color" : "altsound");
        try
        {
            status?.Invoke(
                kind == AddonKind.AltColor
                    ? "Unpacking the colorization…"
                    : "Unpacking the AltSound pack…"
            );
            return kind == AddonKind.AltColor
                ? InstallColor(downloaded, staging, table, file, game, status)
                : InstallSound(downloaded, staging, table, file, game, status);
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

    static InstalledAddon InstallColor(
        string downloaded,
        string staging,
        InstalledTable table,
        VpsFile file,
        string game,
        Action<string>? status
    )
    {
        List<string> Find(string[] exts) =>
            Directory
                .EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
        // Some colorizations are posted as the bare file rather than in an archive.
        Addons.Unpack(
            downloaded,
            staging,
            () => Find([.. SerumExts, .. VniExts]).Count > 0,
            "colorization",
            [.. SerumExts, .. VniExts, ".pac"]
        );
        var names = new[] { game, (file as VpsAltColorFile)?.Folder }.OfType<string>().ToList();
        bool Named(string f) =>
            names.Contains(Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);

        if (Find(SerumExts) is { Count: > 0 } serum)
        {
            // Packs ship .cRZ (or .cROM); a .cROMc is a cache Serum builds itself from them.
            var main = serum
                .OrderByDescending(Named)
                .ThenBy(f => Array.IndexOf(SerumExts, Path.GetExtension(f).ToLowerInvariant()))
                .First();
            var dest = Prepare(table, SerumFolder, game, status);
            var stem = Path.GetFileNameWithoutExtension(main);
            File.Copy(main, Path.Combine(dest, game + Path.GetExtension(main)));
            // Companions named after it (Serum 2's <rom>.pup.csv), renamed along with it.
            foreach (
                var extra in Directory
                    .EnumerateFiles(Path.GetDirectoryName(main)!, stem + ".*")
                    .Where(f =>
                        f != main && !SerumExts.Contains(Path.GetExtension(f).ToLowerInvariant())
                    )
            )
            {
                File.Copy(
                    extra,
                    Path.Combine(dest, game + Path.GetFileName(extra)[stem.Length..]),
                    overwrite: true
                );
            }

            return Addons.NewRecord(file, SerumFolder, game);
        }
        if (Find(VniExts) is { Count: > 0 } vni)
        {
            var dir = Path.GetDirectoryName(vni.OrderByDescending(Named).First())!;
            var dest = Prepare(table, VniFolder, game, status);
            foreach (
                var f in Directory
                    .EnumerateFiles(dir)
                    .Where(f => VniExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            )
            {
                // pin2dmd.pal/.vni is a name the plugin knows; anything else takes the ROM's name.
                var name = Path.GetFileNameWithoutExtension(f)
                    .Equals("pin2dmd", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(f)
                    : game + Path.GetExtension(f);
                File.Copy(f, Path.Combine(dest, name), overwrite: true);
            }
            return Addons.NewRecord(file, VniFolder, game);
        }
        if (Find([".pac"]).Count > 0)
        {
            throw PacOnlyFiles.Error();
        }

        throw new InvalidDataException(
            "No colorization in the download (no .cRZ, .cROM, .vni or .pal file)."
        );
    }

    static InstalledAddon InstallSound(
        string downloaded,
        string staging,
        InstalledTable table,
        VpsFile file,
        string game,
        Action<string>? status
    )
    {
        Addons.Unpack(
            downloaded,
            staging,
            () => Addons.Roots(staging, IsSoundRoot).Count > 0,
            "AltSound"
        );
        var roots = Addons.Roots(staging, IsSoundRoot);
        if (roots.Count == 0)
        {
            throw new InvalidDataException(
                "No AltSound pack in the download (no altsound.csv, g-sound.csv or PinSound folders)."
            );
        }

        var names = table.Manifest.RomNames.Append(game).ToList();
        var root =
            roots.FirstOrDefault(r =>
                names.Contains(
                    Path.GetFileName(r.TrimEnd('\\', '/')),
                    StringComparer.OrdinalIgnoreCase
                )
            )
            ?? roots
                .OrderByDescending(r =>
                    Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories).Count()
                )
                .First();
        var dest = Prepare(table, SoundFolder, game, status, create: false);
        Addons.MoveTree(root, dest);
        return Addons.NewRecord(file, SoundFolder, game);
    }

    /// <summary>Empties &lt;table&gt;\&lt;location&gt;\&lt;game&gt; and removes the one of this kind installed before (it may sit elsewhere).</summary>
    static string Prepare(
        InstalledTable table,
        string location,
        string game,
        Action<string>? status,
        bool create = true
    )
    {
        var kind = location == SoundFolder ? AddonKind.AltSound : AddonKind.AltColor;
        if (Addons.Record(table, kind) is { } old)
        {
            Remove(table, kind, Display(old, kind));
        }

        var dest = Path.Combine(table.Folder, location, game);
        status?.Invoke($"Installing to {location}\\{game}…");
        Addons.DeleteFolder(dest);
        if (create)
        {
            Directory.CreateDirectory(dest);
        }

        return dest;
    }

    static bool IsSoundRoot(string dir) =>
        Directory
            .EnumerateFiles(dir)
            .Any(f => SoundCsvs.Contains(Path.GetFileName(f).ToLowerInvariant()))
        || Directory
            .EnumerateDirectories(dir)
            .Any(d => LegacySoundFolders.Contains(Path.GetFileName(d).ToLowerInvariant()));

    static bool HasFile(string dir, string[] exts) =>
        Directory
            .EnumerateFiles(dir)
            .Any(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()));

    /// <summary>Deletes an installed folder as <see cref="AddonState.Folders"/> lists it ("serum\afm_113b", or "afm_113b" for AltSound).</summary>
    public static void Remove(InstalledTable table, AddonKind kind, string folder)
    {
        var rel = kind == AddonKind.AltSound ? Path.Combine(SoundFolder, folder) : folder;
        var dir = Path.Combine(table.Folder, rel);
        Addons.DeleteFolder(dir);
        Addons.DeleteIfEmpty(Path.GetDirectoryName(dir)!);
    }

    /// <summary>
    /// Moves colorizations and AltSound from where table downloads and older VPX setups put them (altcolor\,
    /// pinmame\altcolor\, pinmame\altsound\) to where VPX 10.8.1's plugins look: Serum files to serum\, Pin2DMD
    /// palettes to vni\, sounds to altsound\. A folder already at the destination is left as it is.
    /// </summary>
    public static void FixLayout(string tableFolder)
    {
        try
        {
            foreach (
                var src in new[]
                {
                    Path.Combine(tableFolder, "altcolor"),
                    Path.Combine(tableFolder, "pinmame", "altcolor"),
                }
            )
            {
                if (!Directory.Exists(src))
                {
                    continue;
                }

                foreach (var dir in Directory.EnumerateDirectories(src).ToList())
                {
                    var loc =
                        HasFile(dir, SerumExts) ? SerumFolder
                        : HasFile(dir, VniExts) ? VniFolder
                        : null;
                    if (loc != null)
                    {
                        MoveIfFree(dir, Path.Combine(tableFolder, loc, Path.GetFileName(dir)));
                    }
                }
                Addons.DeleteIfEmpty(src);
            }
            var oldSound = Path.Combine(tableFolder, "pinmame", "altsound");
            if (Directory.Exists(oldSound))
            {
                foreach (var dir in Directory.EnumerateDirectories(oldSound).ToList())
                {
                    MoveIfFree(dir, Path.Combine(tableFolder, SoundFolder, Path.GetFileName(dir)));
                }

                Addons.DeleteIfEmpty(oldSound);
            }
        }
        catch
        { /* a file in use: try again next time */
        }
    }

    static void MoveIfFree(string dir, string dest)
    {
        if (Directory.Exists(dest))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Directory.Move(dir, dest);
    }
}

/// <summary>
/// Catalog colorizations that turned out to be PAC-only when downloaded. The catalog lists them as plain
/// "Pin2DMD", so this is only known after one download; remembering it lets the list say so instead of
/// offering the same useless download again. A newer catalog version of the file is tried again.
/// </summary>
public static class PacOnlyFiles
{
    /// <summary>Where the list is kept (tests point it elsewhere).</summary>
    public static string StorePath { get; set; } =
        Path.Combine(AppSettings.DataDir, "pac-only-colorizations.json");

    const string Flag = "VrTableManager.PacOnly";

    /// <summary>The error a PAC-only download fails with (<see cref="IsError"/> recognises it).</summary>
    public static InvalidDataException Error()
    {
        var e = new InvalidDataException(
            "This colorization only has an encrypted Pin2DMD .pac file, which VPX's VNI plugin can't read."
        );
        e.Data[Flag] = true;
        return e;
    }

    public static bool IsError(Exception e) => e.Data.Contains(Flag);

    static Dictionary<string, long>? _cache;
    static string? _cachePath;

    static Dictionary<string, long> Load()
    {
        if (_cache != null && _cachePath == StorePath)
        {
            return _cache;
        }

        _cachePath = StorePath;
        return _cache = Read();
    }

    static Dictionary<string, long> Read()
    {
        try
        {
            return File.Exists(StorePath)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(
                    File.ReadAllText(StorePath)
                ) ?? []
                : [];
        }
        catch (Exception e)
            when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool IsKnown(VpsFile f) =>
        f.Id != "" && Load().TryGetValue(f.Id, out var at) && at >= (f.UpdatedAt ?? 0);

    public static void Mark(VpsFile f)
    {
        if (f.Id == "")
        {
            return;
        }

        var all = new Dictionary<string, long>(Load()) { [f.Id] = f.UpdatedAt ?? 0 };
        _cache = all;
        try
        {
            File.WriteAllText(StorePath, System.Text.Json.JsonSerializer.Serialize(all));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
