using VrTableManager.Models;

namespace VrTableManager.Services;

/// <param name="Script">The folder the table script opens, when it uses PinUP Player and names it.</param>
/// <param name="Roms">The table's ROM names: packs that follow the DMD are named after the ROM.</param>
public sealed record PupNames(string? Script, IReadOnlyList<string> Roms)
{
    /// <summary>The script ships with <c>usePUP = False</c>: the pack only shows once that's set to True.</summary>
    public bool ScriptTurnsPupOff { get; init; }

    public IReadOnlyList<string> All =>
        (Script is { } s ? Roms.Prepend(s) : Roms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <param name="Record">What to record in the table's manifest.</param>
/// <param name="Layout">The pack's layouts, and the VR one used if any.</param>
public sealed record PupInstall(InstalledAddon Record, PupLayout Layout);

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

    /// <summary>Folder names in packs that hold packs rather than being one.</summary>
    static readonly string[] Containers =
    [
        "pupvideos",
        "pinupsystem",
        "pinup",
        "pup",
        "puppack",
        "pup pack",
        "pup-pack",
    ];

    /// <summary>The catalog's tag for a table whose own page also offers its pack, as a file beside the .vpx.</summary>
    public const string BundledFeature = "incl. PuP";

    /// <summary>
    /// Packs for the table that can be downloaded: its own page first when the installed table file is tagged
    /// <see cref="BundledFeature"/> (the catalog then often has no PuP entry for it), then the game's packs, newest first.
    /// </summary>
    public static List<VpsFile> SourcesFor(VpsGame game, InstalledTable? t = null)
    {
        var packs = (game.PupPackFiles ?? [])
            .Where(f => Addons.UsableUrl(f) != null)
            .OrderByDescending(f => f.UpdatedAt ?? 0)
            .ToList();
        if (
            t != null
            && game.TableFiles?.FirstOrDefault(f => f.Id == t.Manifest.TableFileId) is { } tf
            && VpsCatalog.HasFeature(tf, BundledFeature)
            && Addons.UsableUrl(tf) != null
        )
            packs.Insert(0, tf);
        return packs;
    }

    /// <summary>"v1.1 by Hawkeyez88, Xkane47 — Mini PUP-pack", or for a table's own page "From the table's page (v1.0.1)".</summary>
    public static string Describe(VpsFile f) =>
        f is VpsTableFile
            ? "From the table's page"
                + (
                    string.IsNullOrWhiteSpace(f.Version)
                        ? ""
                        : $" (v{f.Version.Trim().TrimStart('v', 'V')})"
                )
            : Addons.Describe(f, "PuP pack");

    /// <summary>The pack folders in a table's pupvideos\, and which one this app installed.</summary>
    public static AddonState StateOf(InstalledTable t)
    {
        var root = Path.Combine(t.Folder, FolderName);
        if (!Directory.Exists(root))
            return AddonState.None;
        var folders = Directory
            .EnumerateDirectories(root)
            .Where(IsPackFolder)
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tracked =
            t.Manifest.PupPack is { } p
            && folders.Contains(p.Folder, StringComparer.OrdinalIgnoreCase)
                ? p
                : null;
        return new(tracked, folders);
    }

    /// <summary>
    /// The names a table may open its pack under: the script's own (<see cref="TableManifest.PupName"/>, or read
    /// now for tables installed before it was recorded) and its ROM names (packs that follow the DMD).
    /// </summary>
    public static PupNames ExpectedNames(InstalledTable t)
    {
        // Read the script each time: the user may have edited it (usePUP) since it was installed.
        var info = File.Exists(t.TablePath) ? RomDetector.Inspect(t.TablePath) : null;
        var pupName = info is { UsesPup: true }
            ? info.PupName ?? info.GameName
            : t.Manifest.PupName;
        return new(string.IsNullOrWhiteSpace(pupName) ? null : pupName, t.Manifest.RomNames)
        {
            ScriptTurnsPupOff = info?.PupOff == true,
        };
    }

    /// <summary>
    /// Puts a downloaded pack into <c>&lt;table&gt;\pupvideos\&lt;name&gt;</c>, replacing the pack this app
    /// installed before (an update) and any folder already under that name, and switches it to its VR layout
    /// when it has one (<see cref="PupPackOptions"/>). Returns what to record in the table's manifest (saving it
    /// is up to the caller) and what was found about the pack's layouts.
    /// </summary>
    public static PupInstall Install(
        string downloaded,
        InstalledTable table,
        VpsFile file,
        PupNames expectedNames,
        Action<string>? status = null
    )
    {
        var staging = Addons.StagingDir("pup");
        try
        {
            status?.Invoke("Unpacking the PuP pack…");
            Addons.Unpack(downloaded, staging, () => PackRoots(staging).Count > 0, "PuP pack");
            var roots = PackRoots(staging);
            if (roots.Count == 0)
                throw new InvalidDataException(
                    "No PuP pack in the download (no screens.pup or playlists.pup). It may be an add-on for another pack."
                );

            var (root, name) = Choose(roots, staging, expectedNames);
            var pupDir = Path.Combine(table.Folder, FolderName);
            var dest = Path.Combine(pupDir, TableInstaller.SafeName(name));
            status?.Invoke($"Installing to {FolderName}\\{Path.GetFileName(dest)}…");

            // An update may come under another folder name; the old one would only take up space.
            if (
                table.Manifest.PupPack is { Folder: { Length: > 0 } old }
                && !old.Equals(Path.GetFileName(dest), StringComparison.OrdinalIgnoreCase)
            )
                Addons.DeleteFolder(Path.Combine(pupDir, old));
            Addons.DeleteFolder(dest);
            Addons.MoveTree(root, dest);
            status?.Invoke("Checking the pack's layouts…");
            var layout = PupPackOptions.SetUpForVr(dest);
            return new(Addons.NewRecord(file, FolderName, Path.GetFileName(dest)), layout);
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

    /// <summary>Deletes the pack this app installed (or, given a name, that pack folder) from the table's pupvideos\.</summary>
    public static void Remove(InstalledTable table, string? folder = null)
    {
        folder ??= table.Manifest.PupPack?.Folder;
        if (string.IsNullOrEmpty(folder))
            return;
        var pupDir = Path.Combine(table.Folder, FolderName);
        Addons.DeleteFolder(Path.Combine(pupDir, folder));
        Addons.DeleteIfEmpty(pupDir);
    }

    /// <summary>
    /// Which pack folder to install and the name to give it. The script's name wins when it's known
    /// (it's what PinUP opens); otherwise the pack's own folder name, which authors set to the ROM or
    /// table name. A pack at the top of the archive with no folder of its own needs one of the expected names.
    /// </summary>
    public static (string root, string name) Choose(
        IReadOnlyList<string> roots,
        string staging,
        PupNames expected
    )
    {
        string NameOf(string r) => Path.GetFileName(r.TrimEnd('\\', '/'));
        bool Unnamed(string r) =>
            string.Equals(
                Path.GetFullPath(r).TrimEnd('\\', '/'),
                Path.GetFullPath(staging).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase
            ) || Containers.Contains(NameOf(r).ToLowerInvariant());
        var all = expected.All;

        var root =
            roots.FirstOrDefault(r => all.Contains(NameOf(r), StringComparer.OrdinalIgnoreCase))
            ?? (
                roots.Count == 1
                    ? roots[0]
                    : roots
                        .OrderByDescending(r =>
                            Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories).Count()
                        )
                        .First()
            );
        if (Unnamed(root))
            return (
                root,
                all.FirstOrDefault()
                    ?? throw new InvalidDataException(
                        "The PuP pack has no folder name and the table doesn't say which one it opens."
                    )
            );
        var own = NameOf(root);
        return (
            root,
            expected.Script is { } s && !s.Equals(own, StringComparison.OrdinalIgnoreCase) ? s : own
        );
    }

    /// <summary>Topmost folders under <paramref name="dir"/> that are PuP packs (backup copies inside a pack don't count).</summary>
    public static List<string> PackRoots(string dir) => Addons.Roots(dir, IsPackFolder);

    static bool IsPackFolder(string dir) =>
        Directory
            .EnumerateFiles(dir)
            .Any(f => PackFiles.Contains(Path.GetFileName(f).ToLowerInvariant()));
}
