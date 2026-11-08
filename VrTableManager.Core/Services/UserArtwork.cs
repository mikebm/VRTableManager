using System.Security.Cryptography;
using System.Text;

namespace VrTableManager.Services;

/// <summary>
/// Pictures you choose yourself for a table, which win over the catalog's and the table file's:
/// <list type="bullet">
/// <item><b>card</b>: the table's tile in My Tables;</item>
/// <item><b>playfield</b>: the VR lobby cabinet's playfield (a still; a lobby video still plays over it);</item>
/// <item><b>backglass</b>: the VR lobby cabinet's backglass, even when the table's own VR cabinet has one.</item>
/// </list>
/// They live in <c>&lt;table folder&gt;\artwork\</c> as card.png, playfield.jpg, … (any of png/jpg/jpeg/webp/bmp),
/// so you can also drop files there by hand. Tables without a folder of their own (Future Pinball and store
/// games, or a .vpx loose in the tables folder) keep theirs under the app's data folder instead.
/// </summary>
public static class UserArtwork
{
    public enum Kind
    {
        Card,
        Playfield,
        Backglass,
    }

    public const string FolderName = "artwork";
    public static readonly string[] PictureExts = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <summary>Art keys for a local picture: "local:&lt;path&gt;|&lt;last write ticks&gt;", so a replaced file is loaded again.</summary>
    public const string LocalScheme = "local:";

    static string FileStem(Kind k) => k.ToString().ToLowerInvariant();

    public static string FolderFor(InstalledTable t)
    {
        if (t.IsVpx && File.Exists(Path.Combine(t.Folder, "vrtm.json")))
        {
            return Path.Combine(t.Folder, FolderName);
        }

        var hash = Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes(t.TablePath.ToLowerInvariant()))
        )[..10];
        var name = new string(
            Path.GetFileNameWithoutExtension(t.TablePath)
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                .ToArray()
        );
        return Path.Combine(AppSettings.DataDir, FolderName, $"{name}-{hash}");
    }

    /// <summary>The picture you chose for this, or null.</summary>
    public static string? Find(InstalledTable t, Kind kind)
    {
        var dir = FolderFor(t);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(dir, FileStem(kind) + ".*")
                .Where(f =>
                    PictureExts.Contains(Path.GetExtension(f).ToLowerInvariant())
                    && new FileInfo(f).Length > 0
                )
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The art key for <see cref="Find"/>'s picture, for the app's and the lobby's image loaders; null when there's none.</summary>
    public static string? Key(InstalledTable t, Kind kind) =>
        Find(t, kind) is { } f ? $"{LocalScheme}{f}|{File.GetLastWriteTimeUtc(f).Ticks}" : null;

    /// <summary>The file behind a <see cref="Key"/>, or null when <paramref name="key"/> isn't a local one.</summary>
    public static string? PathOf(string key)
    {
        if (!key.StartsWith(LocalScheme, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = key[LocalScheme.Length..];
        var bar = rest.LastIndexOf('|');
        return bar > 0 ? rest[..bar] : rest;
    }

    /// <summary>Copies <paramref name="picture"/> in as this kind's picture, replacing the one there was.</summary>
    public static string Set(InstalledTable t, Kind kind, string picture)
    {
        var ext = Path.GetExtension(picture).ToLowerInvariant();
        if (!PictureExts.Contains(ext))
        {
            throw new InvalidOperationException(
                $"Use a {string.Join(", ", PictureExts.Select(e => e.TrimStart('.')))} picture."
            );
        }

        var dir = Directory.CreateDirectory(FolderFor(t)).FullName;
        var target = Path.Combine(dir, FileStem(kind) + ext);
        var tmp = target + ".tmp";
        File.Copy(picture, tmp, overwrite: true);
        Remove(t, kind);
        File.Move(tmp, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow); // a new key, so loaders don't show the old picture
        return target;
    }

    /// <summary>Deletes this kind's picture(s).</summary>
    public static void Remove(InstalledTable t, Kind kind)
    {
        var dir = FolderFor(t);
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (
            var f in Directory
                .EnumerateFiles(dir, FileStem(kind) + ".*")
                .Where(f => PictureExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
        )
        {
            File.Delete(f);
        }
    }
}
