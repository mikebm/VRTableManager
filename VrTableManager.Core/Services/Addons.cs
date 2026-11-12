using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>Catalog extras a VPX table can have beside it, each in its own folder of the table folder.</summary>
public enum AddonKind
{
    Pup,
    AltColor,
    AltSound,
}

/// <summary>A catalog add-on that's newer than the one installed for a table.</summary>
public sealed record AddonUpdate(InstalledTable Table, VpsGame Game, VpsFile File, AddonKind Kind)
{
    public string Label =>
        $"v{Addons.Record(Table, Kind)?.Version ?? "?"} → v{File.Version ?? "?"}";
}

/// <summary>What's installed of one kind of add-on.</summary>
/// <param name="Tracked">The one this app installed (catalog id and version), if it's still there.</param>
/// <param name="Folders">Every installed folder of that kind, relative to the table folder's add-on folder
/// (a pack name for PuP, "serum\afm_113b" for colour, the ROM name for AltSound), including ones put there by hand.</param>
public sealed record AddonState(InstalledAddon? Tracked, IReadOnlyList<string> Folders)
{
    public static readonly AddonState None = new(null, []);
    public bool IsInstalled => Folders.Count > 0;
}

/// <summary>
/// What PuP packs, colorizations and AltSound share: catalog links that can be fetched, the record in vrtm.json,
/// update checks, and unpacking big archives into the table folder. VPX 10.8.1 looks for all of them in the
/// table's own folder (docs\FileLayout.md): pupvideos\, serum\, vni\ and altsound\.
/// </summary>
public static class Addons
{
    static readonly string[] ArchiveExts = [".zip", ".rar", ".7z"];

    /// <summary>File hosts the sign-in browser can't download from (their own download apps, links that expire, bare folder listings).</summary>
    static readonly string[] UnsupportedHosts =
    [
        "mega.nz",
        "mega.io",
        "we.tl",
        "wetransfer.com",
        "montetoncab.fr",
    ];

    /// <summary>A table VPX can give add-ons: a VPX table in its own folder (loose .vpx files share one).</summary>
    public static bool CanHold(InstalledTable t, AppSettings s) =>
        t.IsVpx
        && !t.IsIncomplete
        && !string.Equals(
            Path.GetFullPath(t.Folder).TrimEnd('\\', '/'),
            Path.GetFullPath(s.TablesDir).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>
    /// The file's first link that goes to a download page: not a site's front page or a category listing
    /// (pinsound-community "files/category/…"), not Mega/WeTransfer.
    /// </summary>
    public static string? UsableUrl(VpsFile f) =>
        f
            .Urls?.FirstOrDefault(u =>
                !u.Broken
                && Uri.TryCreate(u.Url, UriKind.Absolute, out var uri)
                && uri.AbsolutePath.Trim('/') != ""
                && !uri.PathAndQuery.Contains(
                    "/files/category/",
                    StringComparison.OrdinalIgnoreCase
                )
                && !UnsupportedHosts.Any(h =>
                    uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)
                )
            )
            ?.Url;

    /// <summary>"v1.1 by Hawkeyez88, Xkane47 — Mini PUP-pack"</summary>
    public static string Describe(VpsFile f, string fallback)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Version))
        {
            parts.Add("v" + f.Version.Trim().TrimStart('v', 'V'));
        }

        if (f.Authors is { Count: > 0 } a)
        {
            parts.Add(
                "by " + string.Join(", ", a.Take(3)) + (a.Count > 3 ? " +" + (a.Count - 3) : "")
            );
        }

        var text = parts.Count > 0 ? string.Join(" ", parts) : fallback;
        return string.IsNullOrWhiteSpace(f.Comment) ? text : $"{text} — {f.Comment.Trim()}";
    }

    public static InstalledAddon? Record(InstalledTable t, AddonKind kind) =>
        kind switch
        {
            AddonKind.Pup => t.Manifest.PupPack,
            AddonKind.AltColor => t.Manifest.AltColor,
            _ => t.Manifest.AltSound,
        };

    public static void SetRecord(InstalledTable t, AddonKind kind, InstalledAddon? value)
    {
        switch (kind)
        {
            case AddonKind.Pup:
                t.Manifest.PupPack = value;
                break;
            case AddonKind.AltColor:
                t.Manifest.AltColor = value;
                break;
            default:
                t.Manifest.AltSound = value;
                break;
        }
    }

    /// <summary>All of the game's catalog files of this kind (usable or not); for PuP, also table files whose page has the pack.</summary>
    public static IEnumerable<VpsFile> FilesOf(VpsGame game, AddonKind kind) =>
        kind switch
        {
            AddonKind.Pup => (game.PupPackFiles ?? []).Concat(
                (game.TableFiles ?? []).Where(f =>
                    VpsCatalog.HasFeature(f, PupPacks.BundledFeature)
                )
            ),
            AddonKind.AltColor => game.AltColorFiles ?? [],
            _ => game.AltSoundFiles ?? [],
        };

    public static AddonState StateOf(InstalledTable t, AddonKind kind) =>
        kind == AddonKind.Pup ? PupPacks.StateOf(t) : AltPacks.StateOf(t, kind);

    /// <summary>
    /// Installed add-ons whose catalog entry now has another version (same id). VPS edits an entry in place for a
    /// new release, so a different version string is an update; UpdatedAt only counts when neither side has one.
    /// </summary>
    public static List<AddonUpdate> FindUpdates(
        IEnumerable<InstalledTable> installed,
        IReadOnlyList<VpsGame> games
    )
    {
        var byId = games.ToDictionary(g => g.Id);
        var result = new List<AddonUpdate>();
        foreach (var t in installed)
        {
            if (t.Manifest.GameId == "" || !byId.TryGetValue(t.Manifest.GameId, out var game))
            {
                continue;
            }

            foreach (var kind in Enum.GetValues<AddonKind>())
            {
                if (Record(t, kind) is not { } rec)
                {
                    continue;
                }

                var f = FilesOf(game, kind).FirstOrDefault(x => x.Id == rec.FileId);
                // A pack from the table's own page has the table's version, not its own: the table's update covers it.
                if (f == null || f is VpsTableFile || UsableUrl(f) == null)
                {
                    continue;
                }

                var newer = !string.IsNullOrWhiteSpace(f.Version)
                    ? !TableUpdates.SameVersion(f.Version, rec.Version)
                    : string.IsNullOrWhiteSpace(rec.Version)
                        && f.UpdatedAt > (rec.SourceUpdatedAt ?? long.MaxValue);
                if (newer && StateOf(t, kind).Tracked != null)
                {
                    result.Add(new AddonUpdate(t, game, f, kind));
                }
            }
        }
        return result;
    }

    /// <summary>What to record in vrtm.json for a file just installed to &lt;table&gt;\&lt;location&gt;\&lt;folder&gt;.</summary>
    public static InstalledAddon NewRecord(VpsFile file, string location, string folder) =>
        new()
        {
            FileId = file.Id,
            Version = file.Version,
            Authors = file.Authors ?? [],
            Location = location,
            Folder = folder,
            SourceUrl = UsableUrl(file),
            InstalledAt = DateTime.Now,
            SourceUpdatedAt = file.UpdatedAt,
        };

    /// <summary>
    /// Unpacks a download into <paramref name="staging"/>, and the archives inside it too when <paramref name="found"/>
    /// says the first pass has nothing usable (some packs wrap the real archive, a .rar inside the .zip).
    /// A download with one of <paramref name="looseExts"/> is the file itself (a bare .cRZ) and is copied in as it is.
    /// </summary>
    public static void Unpack(
        string downloaded,
        string staging,
        Func<bool> found,
        string what,
        string[]? looseExts = null
    )
    {
        if (Directory.Exists(downloaded))
        {
            // A pack you already have unpacked (an altsound or pupvideos folder from another setup): copied, the
            // original left where it is.
            CopyTree(
                downloaded,
                Path.Combine(staging, Path.GetFileName(downloaded.TrimEnd('\\', '/')))
            );
            if (found())
            {
                return;
            }

            throw new InvalidDataException(
                $"No {what} in the folder {Path.GetFileName(downloaded.TrimEnd('\\', '/'))}."
            );
        }

        if (looseExts?.Contains(Path.GetExtension(downloaded).ToLowerInvariant()) == true)
        {
            Directory.CreateDirectory(staging);
            File.Copy(downloaded, Path.Combine(staging, Path.GetFileName(downloaded)));
            return;
        }
        if (!IsArchive(downloaded))
        {
            throw new InvalidDataException(
                $"Expected a {what} archive, got {Path.GetFileName(downloaded)}."
            );
        }

        TableInstaller.Extract(downloaded, staging);
        if (found())
        {
            return;
        }

        foreach (
            var nested in Directory
                .EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Where(IsArchive)
                .ToList()
        )
        {
            TableInstaller.Extract(
                nested,
                Path.Combine(staging, "_nested", Path.GetFileNameWithoutExtension(nested))
            );
        }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    /// <summary>Topmost folders under <paramref name="dir"/> (itself included) that pass <paramref name="isRoot"/>.</summary>
    public static List<string> Roots(string dir, Func<string, bool> isRoot)
    {
        var all = new[] { dir }
            .Concat(Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
            .Where(isRoot)
            .ToList();
        return all.Where(r =>
                !all.Any(o =>
                    o != r
                    && r.StartsWith(
                        o.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
            .ToList();
    }

    public static string StagingDir(string tag) =>
        Path.Combine(Path.GetTempPath(), $"vrtm-{tag}-" + Guid.NewGuid().ToString("N")[..8]);

    public static bool IsArchive(string path) =>
        ArchiveExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static void DeleteFolder(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Added to an add-on's folder name while the new one is copied in beside the one installed.</summary>
    public const string NewSuffix = ".vrtm-new";

    /// <summary>Added to the installed add-on's folder name while the new one takes its place.</summary>
    public const string OldSuffix = ".vrtm-old";

    /// <summary>A folder <see cref="SwapIn"/> uses on the way (not an installed add-on).</summary>
    public static bool IsSwapFolder(string dir) =>
        dir.TrimEnd('\\', '/').EndsWith(NewSuffix, StringComparison.OrdinalIgnoreCase)
        || dir.TrimEnd('\\', '/').EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What an install cut off during <see cref="SwapIn"/> left beside <paramref name="dest"/>: an old add-on that
    /// never got its name back gets it; the rest is deleted.
    /// </summary>
    public static void CleanUpSwap(string dest)
    {
        var old = dest + OldSuffix;
        if (Directory.Exists(old) && !Directory.Exists(dest))
        {
            Directory.Move(old, dest);
        }

        DeleteFolder(old);
        DeleteFolder(dest + NewSuffix);
    }

    /// <summary>
    /// Puts <paramref name="fresh"/> in <paramref name="dest"/>'s place: the add-on there is renamed out of the way,
    /// the new one renamed in (tried again for a moment while something has a file open), and only then is the old
    /// one deleted. When the new one can't go in, the old one gets its name back and this throws.
    /// </summary>
    public static void SwapIn(string fresh, string dest)
    {
        var old = dest + OldSuffix;
        DeleteFolder(old);
        var hadOld = Directory.Exists(dest);
        if (hadOld)
        {
            Directory.Move(dest, old);
        }

        try
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    Directory.Move(fresh, dest);
                    break;
                }
                catch (Exception e)
                    when ((e is IOException or UnauthorizedAccessException) && i < 5)
                {
                    Thread.Sleep(200);
                }
            }
        }
        catch
        {
            if (hadOld)
            {
                try
                {
                    Directory.Move(old, dest);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { /* CleanUpSwap gives it its name back on the next install */
                }
            }
            throw;
        }

        TryDeleteFolder(old);
    }

    /// <summary><see cref="DeleteFolder"/>, for cleaning up after a failure: never throws.</summary>
    public static void TryDeleteFolder(string dir)
    {
        try
        {
            DeleteFolder(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes <paramref name="dir"/> when nothing is left in it.</summary>
    public static void DeleteIfEmpty(string dir)
    {
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }
    }

    /// <summary>Packs run to gigabytes: a move when staging is on the same drive, a copy otherwise.</summary>
    public static void MoveTree(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (
            string.Equals(
                Path.GetPathRoot(Path.GetFullPath(src)),
                Path.GetPathRoot(Path.GetFullPath(dest)),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            try
            {
                Directory.Move(src, dest);
                return;
            }
            catch (IOException)
            { /* staging root itself, or locked: copy instead */
            }
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (
                file.StartsWith(
                    Path.Combine(src, "_nested") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var target = Path.Combine(dest, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
