using SharpCompress.Archives;

namespace VrTableManager.Services;

/// <summary>
/// Takes one file out of a downloaded media pack (PinUP Popper / PinballX front-end media, or a wheel-art pack):
/// the archive entry a picker chooses, looking one level into archives inside it (some packs wrap each front
/// end's media in its own archive). A download that's already a single file of the right type is used as is.
/// </summary>
public static class MediaPackFiles
{
    public static readonly string[] ArchiveExts = [".zip", ".rar", ".7z"];

    /// <summary>How good an entry is (higher is better), or null when it isn't the kind of file wanted.</summary>
    public delegate int? Scorer(string entryPath, long size);

    /// <summary>Chooses one of an archive's files (path, size), or null for none.</summary>
    public delegate string? Picker(IReadOnlyList<(string Path, long Size)> entries);

    /// <summary>The picker that takes the best-scoring entry (<see cref="Pick"/>).</summary>
    public static Picker Best(Scorer score) => entries => Pick(entries, score);

    /// <summary>The best of (path, size) pairs by <paramref name="score"/>: highest score, then biggest, then by name.</summary>
    public static string? Pick(IEnumerable<(string path, long size)> entries, Scorer score) =>
        entries
            .Select(e => (e.path, e.size, score: score(e.path, e.size)))
            .Where(e => e.score != null)
            .OrderByDescending(e => e.score)
            .ThenByDescending(e => e.size)
            .ThenBy(e => e.path, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.path)
            .FirstOrDefault();

    /// <summary>
    /// Hands the chosen file's contents and its path in the pack (for its name and extension) to
    /// <paramref name="use"/>, and returns what that gives; null when the pack has nothing to choose. A download
    /// whose extension is in <paramref name="direct"/> is the file itself. Nothing is copied on the way.
    /// </summary>
    public static T? Open<T>(
        string downloaded,
        Picker pick,
        IReadOnlyCollection<string> direct,
        Func<Stream, string, T?> use,
        CancellationToken ct = default
    )
        where T : class
    {
        var ext = Path.GetExtension(downloaded).ToLowerInvariant();
        if (direct.Contains(ext))
        {
            using var src = File.OpenRead(downloaded);
            return use(src, downloaded);
        }

        return ArchiveExts.Contains(ext)
            ? FromArchive(downloaded, pick, use, nested: true, ct)
            : null;
    }

    static T? FromArchive<T>(
        string archivePath,
        Picker pick,
        Func<Stream, string, T?> use,
        bool nested,
        CancellationToken ct
    )
        where T : class
    {
        ct.ThrowIfCancellationRequested();
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var files = archive
            .Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key))
            .ToList();
        if (pick(files.Select(e => (e.Key!, e.Size)).ToList()) is { } chosen)
        {
            using var src = files.First(e => e.Key == chosen).OpenEntryStream();
            return use(src, chosen);
        }
        if (!nested)
        {
            return null;
        }

        foreach (
            var inner in files.Where(e =>
                ArchiveExts.Contains(Path.GetExtension(e.Key!).ToLowerInvariant())
            )
        )
        {
            var tmp = TempBeside(archivePath, Path.GetExtension(inner.Key!));
            try
            {
                using (var src = inner.OpenEntryStream())
                using (var dst = File.Create(tmp))
                {
                    Copy(src, dst, ct);
                }

                if (FromArchive(tmp, pick, use, nested: false, ct) is { } found)
                {
                    return found;
                }
            }
            finally
            {
                try
                {
                    File.Delete(tmp);
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>Copies a stream, stopping between blocks when <paramref name="ct"/> is cancelled.</summary>
    public static void Copy(Stream src, Stream dst, CancellationToken ct = default)
    {
        var buffer = new byte[1 << 20];
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buffer, 0, n);
        }
    }

    /// <summary>Writes <paramref name="src"/> to <paramref name="dest"/> by way of a .tmp file, so a failed copy never leaves half a file there.</summary>
    public static void WriteFile(Stream src, string dest, CancellationToken ct = default)
    {
        var tmp = dest + ".tmp";
        try
        {
            using (var dst = File.Create(tmp))
            {
                Copy(src, dst, ct);
            }

            File.Move(tmp, dest, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch { }
        }
    }

    /// <summary>A file taken out to a scratch copy (for a decoder that needs a path); Dispose deletes it.</summary>
    public sealed class Extracted(string path, string entry, bool scratch) : IDisposable
    {
        public string Path { get; } = path;

        /// <summary>The entry's path in the pack (its name and extension).</summary>
        public string Entry { get; } = entry;

        public string Ext => System.IO.Path.GetExtension(Entry).ToLowerInvariant();

        public void Dispose()
        {
            if (!scratch)
            {
                return;
            }

            try
            {
                File.Delete(Path);
            }
            catch { }
        }
    }

    /// <summary><see cref="Open"/>, copying the chosen file next to the download (the job's folder); null when there's none.</summary>
    public static Extracted? ExtractToFile(
        string downloaded,
        Picker pick,
        IReadOnlyCollection<string> direct,
        CancellationToken ct = default
    )
    {
        var ext = Path.GetExtension(downloaded).ToLowerInvariant();
        if (direct.Contains(ext))
        {
            return new Extracted(downloaded, downloaded, scratch: false);
        }

        return Open(
            downloaded,
            pick,
            [],
            (src, entry) =>
            {
                var tmp = TempBeside(downloaded, Path.GetExtension(entry));
                using (var dst = File.Create(tmp))
                {
                    Copy(src, dst, ct);
                }

                return new Extracted(tmp, entry, scratch: true);
            },
            ct
        );
    }

    /// <summary>A scratch file next to the download (the job's folder, deleted with it).</summary>
    static string TempBeside(string file, string ext) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(file))!,
            "inner-" + Guid.NewGuid().ToString("N")[..8] + ext
        );

    /// <summary>Lower-case letters and digits only, for comparing names.</summary>
    public static string Normalize(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The entry's folders and file name (without extension), each <see cref="Normalize"/>d.</summary>
    public static (string[] Folders, string File) Parts(string entryPath)
    {
        var parts = entryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (
            parts[..^1].Select(Normalize).ToArray(),
            Normalize(Path.GetFileNameWithoutExtension(parts[^1]))
        );
    }
}
