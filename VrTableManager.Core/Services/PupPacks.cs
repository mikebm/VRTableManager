using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>A catalog PuP pack that's newer than the one installed for a table.</summary>
public sealed record PupUpdate(InstalledTable Table, VpsGame Game, VpsFile File)
{
    public string Label => $"v{Table.Manifest.PupPack?.Version ?? "?"} → v{File.Version ?? "?"}";
}

/// <param name="Script">The folder the table script opens, when it uses PinUP Player and names it.</param>
/// <param name="Roms">The table's ROM names: packs that follow the DMD are named after the ROM.</param>
public sealed record PupNames(string? Script, IReadOnlyList<string> Roms)
{
    /// <summary>The script ships with <c>usePUP = False</c>: the pack only shows once that's set to True.</summary>
    public bool ScriptTurnsPupOff { get; init; }

    public IReadOnlyList<string> All =>(Script is { } s ? Roms.Prepend(s) : Roms).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>What's in a table's pupvideos\ folder.</summary>
/// <param name="Tracked">The pack this app installed (catalog id and version), if it's still there.</param>
/// <param name="Folders">Every pack folder under pupvideos\ (including ones put there by hand).</param>
public sealed record PupState(InstalledPupPack? Tracked, IReadOnlyList<string> Folders)
{
    public static readonly PupState None = new(null, []);
    public bool IsInstalled => Folders.Count > 0;
}

/// <summary>
/// PinUP Player packs: the videos a table plays on the backglass, DMD and topper. VPX 10.8.1's PUP plugin
/// reads them from the table's own folder, <c>&lt;table&gt;\pupvideos\&lt;pack&gt;\</c> (a pack folder holds
/// screens.pup, playlists.pup and triggers.pup beside its video folders), and in VR shows them on the
/// cabinet. The folder name is what the table script opens (<c>PuPlayer.Init pBackglass, "name"</c>); for
/// ROM tables whose pack follows the DMD it's the ROM name.
/// Packs are only ever copied: the option .bat files and installers some ship with are never run.
/// </summary>
public static class PupPacks
{
    public const string FolderName = "pupvideos";
    /// <summary>Files that mark a PuP pack folder.</summary>
    static readonly string[] PackFiles = ["screens.pup", "playlists.pup", "triggers.pup"];
    static readonly string[] ArchiveExts = [".zip", ".rar", ".7z"];
    /// <summary>Folder names in packs that hold packs rather than being one.</summary>
    static readonly string[] Containers = ["pupvideos", "pinupsystem", "pinup", "pup", "puppack", "pup pack", "pup-pack"];
    /// <summary>File hosts the sign-in browser can't download from (their own download apps, links that expire).</summary>
    static readonly string[] UnsupportedHosts = ["mega.nz", "mega.io", "we.tl", "wetransfer.com"];

    /// <summary>A table VPX can show a PuP pack for: a VPX table in its own folder (loose .vpx files share one).</summary>
    public static bool CanHold(InstalledTable t, AppSettings s) =>
        t.IsVpx && !t.IsIncomplete && !string.Equals(Path.GetFullPath(t.Folder).TrimEnd('\\', '/'),
            Path.GetFullPath(s.TablesDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalog's packs for a game that can be downloaded, newest first.</summary>
    public static List<VpsFile> SourcesFor(VpsGame game) =>
        (game.PupPackFiles ?? []).Where(f => UsableUrl(f) != null).OrderByDescending(f => f.UpdatedAt ?? 0).ToList();

    /// <summary>The pack's first link that goes to a download page (not a site's front page, not Mega/WeTransfer).</summary>
    public static string? UsableUrl(VpsFile f) =>
        f.Urls?.FirstOrDefault(u => !u.Broken && Uri.TryCreate(u.Url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.Trim('/') != ""
            && !UnsupportedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))?.Url;

    /// <summary>"v1.1 by Hawkeyez88, Xkane47 — Mini PUP-pack"</summary>
    public static string Describe(VpsFile f)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Version)) parts.Add("v" + f.Version.Trim().TrimStart('v', 'V'));
        if (f.Authors is { Count: > 0 } a) parts.Add("by " + string.Join(", ", a.Take(3)) + (a.Count > 3 ? " +" + (a.Count - 3) : ""));
        var text = parts.Count > 0 ? string.Join(" ", parts) : "PuP pack";
        return string.IsNullOrWhiteSpace(f.Comment) ? text : $"{text} — {f.Comment.Trim()}";
    }

    /// <summary>The pack folders in a table's pupvideos\, and which one this app installed.</summary>
    public static PupState StateOf(InstalledTable t)
    {
        var root = Path.Combine(t.Folder, FolderName);
        if (!Directory.Exists(root)) return PupState.None;
        var folders = Directory.EnumerateDirectories(root).Where(IsPackFolder).Select(d => Path.GetFileName(d)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var tracked = t.Manifest.PupPack is { } p && folders.Contains(p.Folder, StringComparer.OrdinalIgnoreCase) ? p : null;
        return new(tracked, folders);
    }

    /// <summary>Installed packs whose catalog entry now has another version (same pack id).</summary>
    public static List<PupUpdate> FindUpdates(IEnumerable<InstalledTable> installed, IReadOnlyList<VpsGame> games)
    {
        var byId = games.ToDictionary(g => g.Id);
        var result = new List<PupUpdate>();
        foreach (var t in installed)
        {
            var p = t.Manifest.PupPack;
            if (p == null || t.Manifest.GameId == "" || !byId.TryGetValue(t.Manifest.GameId, out var game)) continue;
            var f = game.PupPackFiles?.FirstOrDefault(x => x.Id == p.FileId);
            if (f == null || UsableUrl(f) == null) continue;
            var newer = !string.IsNullOrWhiteSpace(f.Version)
                ? !TableUpdates.SameVersion(f.Version, p.Version)
                : string.IsNullOrWhiteSpace(p.Version) && f.UpdatedAt > (p.SourceUpdatedAt ?? long.MaxValue);
            if (newer && StateOf(t).Tracked != null) result.Add(new PupUpdate(t, game, f));
        }
        return result;
    }

    /// <summary>
    /// The names a table may open its pack under: the script's own (<see cref="TableManifest.PupName"/>, or read
    /// now for tables installed before it was recorded) and its ROM names (packs that follow the DMD).
    /// </summary>
    public static PupNames ExpectedNames(InstalledTable t)
    {
        // Read the script each time: the user may have edited it (usePUP) since it was installed.
        var info = File.Exists(t.TablePath) ? RomDetector.Inspect(t.TablePath) : null;
        var pupName = info is { UsesPup: true } ? info.PupName ?? info.GameName : t.Manifest.PupName;
        return new(string.IsNullOrWhiteSpace(pupName) ? null : pupName, t.Manifest.RomNames) { ScriptTurnsPupOff = info?.PupOff == true };
    }

    /// <summary>
    /// Puts a downloaded pack into <c>&lt;table&gt;\pupvideos\&lt;name&gt;</c>, replacing the pack this app
    /// installed before (an update) and any folder already under that name. Returns what to record in the
    /// table's manifest; saving it is up to the caller.
    /// </summary>
    public static InstalledPupPack Install(string downloaded, InstalledTable table, VpsFile file, PupNames expectedNames, Action<string>? status = null)
    {
        var staging = Path.Combine(Path.GetTempPath(), "vrtm-pup-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            status?.Invoke("Unpacking the PuP pack…");
            if (!IsArchive(downloaded)) throw new InvalidDataException($"Expected a PuP pack archive, got {Path.GetFileName(downloaded)}.");
            TableInstaller.Extract(downloaded, staging);
            var roots = PackRoots(staging);
            if (roots.Count == 0)
            {
                // Some packs wrap the real archive (a .rar inside the .zip).
                foreach (var nested in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Where(IsArchive).ToList())
                    TableInstaller.Extract(nested, Path.Combine(staging, "_nested", Path.GetFileNameWithoutExtension(nested)));
                roots = PackRoots(staging);
            }
            if (roots.Count == 0)
                throw new InvalidDataException("No PuP pack in the download (no screens.pup or playlists.pup). It may be an add-on for another pack.");

            var (root, name) = Choose(roots, staging, expectedNames);
            var pupDir = Path.Combine(table.Folder, FolderName);
            var dest = Path.Combine(pupDir, TableInstaller.SafeName(name));
            status?.Invoke($"Installing to {FolderName}\\{Path.GetFileName(dest)}…");

            // An update may come under another folder name; the old one would only take up space.
            if (table.Manifest.PupPack is { Folder: { Length: > 0 } old } && !old.Equals(Path.GetFileName(dest), StringComparison.OrdinalIgnoreCase))
                DeleteFolder(Path.Combine(pupDir, old));
            DeleteFolder(dest);
            Directory.CreateDirectory(pupDir);
            MoveTree(root, dest);

            return new InstalledPupPack
            {
                FileId = file.Id,
                Version = file.Version,
                Authors = file.Authors ?? [],
                Folder = Path.GetFileName(dest),
                SourceUrl = UsableUrl(file),
                InstalledAt = DateTime.Now,
                SourceUpdatedAt = file.UpdatedAt,
            };
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }
    }

    /// <summary>Deletes the pack this app installed (or, given a name, that pack folder) from the table's pupvideos\.</summary>
    public static void Remove(InstalledTable table, string? folder = null)
    {
        folder ??= table.Manifest.PupPack?.Folder;
        if (string.IsNullOrEmpty(folder)) return;
        var pupDir = Path.Combine(table.Folder, FolderName);
        DeleteFolder(Path.Combine(pupDir, folder));
        if (Directory.Exists(pupDir) && !Directory.EnumerateFileSystemEntries(pupDir).Any()) Directory.Delete(pupDir);
    }

    /// <summary>
    /// Which pack folder to install and the name to give it. The script's name wins when it's known
    /// (it's what PinUP opens); otherwise the pack's own folder name, which authors set to the ROM or
    /// table name. A pack at the top of the archive with no folder of its own needs one of the expected names.
    /// </summary>
    public static (string root, string name) Choose(IReadOnlyList<string> roots, string staging, PupNames expected)
    {
        string NameOf(string r) => Path.GetFileName(r.TrimEnd('\\', '/'));
        bool Unnamed(string r) => string.Equals(Path.GetFullPath(r).TrimEnd('\\', '/'), Path.GetFullPath(staging).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || Containers.Contains(NameOf(r).ToLowerInvariant());
        var all = expected.All;

        var root = roots.FirstOrDefault(r => all.Contains(NameOf(r), StringComparer.OrdinalIgnoreCase))
            ?? (roots.Count == 1 ? roots[0] : roots.OrderByDescending(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories).Count()).First());
        if (Unnamed(root))
            return (root, all.FirstOrDefault()
                ?? throw new InvalidDataException("The PuP pack has no folder name and the table doesn't say which one it opens."));
        var own = NameOf(root);
        return (root, expected.Script is { } s && !s.Equals(own, StringComparison.OrdinalIgnoreCase) ? s : own);
    }

    /// <summary>Topmost folders under <paramref name="dir"/> that are PuP packs (backup copies inside a pack don't count).</summary>
    public static List<string> PackRoots(string dir)
    {
        var all = new[] { dir }.Concat(Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)).Where(IsPackFolder).ToList();
        return all.Where(r => !all.Any(o => o != r && r.StartsWith(o.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    static bool IsPackFolder(string dir) =>
        Directory.EnumerateFiles(dir).Any(f => PackFiles.Contains(Path.GetFileName(f).ToLowerInvariant()));

    static bool IsArchive(string path) => ArchiveExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    static void DeleteFolder(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Packs run to gigabytes: a move when staging is on the same drive, a copy otherwise.</summary>
    static void MoveTree(string src, string dest)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.Move(src, dest); return; }
            catch (IOException) { /* staging root itself, or locked: copy instead */ }
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(Path.Combine(src, "_nested") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(dest, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
