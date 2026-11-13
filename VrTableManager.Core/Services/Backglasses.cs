using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// A table's .directb2s backglass, from the catalog (b2sFiles) or a file you already have. VPX's B2S plugin looks for
/// it beside the table, named like it: <c>&lt;table folder&gt;\&lt;table name&gt;.directb2s</c>. So it's always
/// installed under the table's own name, whatever the download calls it.
/// </summary>
public static class Backglasses
{
    public const string Ext = ".directb2s";

    /// <summary>Where the table's backglass goes: its .vpx with the .directb2s extension.</summary>
    public static string PathFor(InstalledTable t) => Path.ChangeExtension(t.TablePath, Ext);

    /// <summary>The catalog's backglasses that can be downloaded, newest first.</summary>
    public static List<VpsFile> SourcesFor(VpsGame game) =>
        (game.B2sFiles ?? [])
            .Where(f => Addons.UsableUrl(f) != null)
            .OrderByDescending(f => f.UpdatedAt ?? 0)
            .ToList();

    /// <summary>"v2.0 by Wildman — Full DMD"</summary>
    public static string Describe(VpsFile f) => Addons.Describe(f, "Backglass");

    /// <summary>Installed when the table's own-named .directb2s is there.</summary>
    public static AddonState StateOf(InstalledTable t)
    {
        var path = PathFor(t);
        if (!File.Exists(path))
        {
            return AddonState.None;
        }

        var name = Path.GetFileName(path);
        var rec = t.Manifest.Backglass;
        return new(
            rec != null && string.Equals(rec.Folder, name, StringComparison.OrdinalIgnoreCase)
                ? rec
                : null,
            [name]
        );
    }

    /// <summary>
    /// Installs a downloaded .directb2s, or the one in an archive or folder, as the table's backglass (replacing the
    /// one there). Returns what to record in vrtm.json, the file it took and how many the download had.
    /// </summary>
    public static (InstalledAddon Record, string From, int Count) Install(
        string downloaded,
        InstalledTable table,
        VpsFile file,
        Action<string>? status = null
    )
    {
        var staging = Addons.StagingDir("b2s");
        try
        {
            status?.Invoke("Unpacking the backglass…");
            Addons.Unpack(
                downloaded,
                staging,
                () => Candidates(staging).Count > 0,
                "backglass (.directb2s)",
                [Ext]
            );
            var all = Candidates(staging);
            var source =
                Pick(all, table)
                ?? throw new InvalidDataException(
                    $"No .directb2s backglass in {Path.GetFileName(downloaded.TrimEnd('\\', '/'))}."
                );

            // Copied in beside it first (staging may be on another drive), then moved over it in one step, so a
            // failure leaves the old backglass whole. A copy left by an install that was cut off goes too.
            var dest = PathFor(table);
            var fresh = dest + Addons.NewSuffix;
            File.Copy(source, fresh, overwrite: true);
            try
            {
                Addons.MoveFileOver(fresh, dest);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Addons.TryDeleteFile(fresh);
                throw new IOException(
                    $"Couldn't replace {Path.GetFileName(dest)}: it may be open in VPX. Close the table and try again.",
                    e
                );
            }
            return (
                Addons.NewRecord(file, "", Path.GetFileName(dest)),
                Path.GetFileName(source),
                all.Count
            );
        }
        finally
        {
            Addons.TryDeleteFolder(staging);
        }
    }

    public static void Remove(InstalledTable t)
    {
        var path = PathFor(t);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    static List<string> Candidates(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*" + Ext, SearchOption.AllDirectories).ToList()
            : [];

    /// <summary>
    /// The one named like the table if there is one, else the biggest (a pack with several, such as a full-DMD and a
    /// plain version, says which it took).
    /// </summary>
    internal static string? Pick(List<string> files, InstalledTable t)
    {
        var name = Path.GetFileNameWithoutExtension(t.TablePath);
        return files.FirstOrDefault(f =>
                string.Equals(
                    Path.GetFileNameWithoutExtension(f),
                    name,
                    StringComparison.OrdinalIgnoreCase
                )
            ) ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }
}
