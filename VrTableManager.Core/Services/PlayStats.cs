using System.Text.Json;

namespace VrTableManager.Services;

public sealed class TableStats
{
    public bool Favourite { get; set; }
    public int Plays { get; set; }
    public DateTime? LastPlayed { get; set; }
    public double MinutesPlayed { get; set; }
}

/// <summary>
/// Favourites and play history, shared by VR Table Manager and the VR lobby (play-stats.json in
/// the app's data folder). Keys are full table paths. Several processes write it (the manager, the
/// lobby, and the "--play" launcher that counts plays), so every change re-reads the file under a
/// lock and writes it back atomically.
/// </summary>
public sealed class PlayStats
{
    public Dictionary<string, TableStats> Tables { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(AppSettings.DataDir, "play-stats.json");

    /// <summary>Stats file next to another settings file (keeps tests and custom setups self-contained).</summary>
    public static string PathBeside(string? otherFile) =>
        string.IsNullOrEmpty(otherFile)
            ? DefaultPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(otherFile))!, "play-stats.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Key(string tablePath)
    {
        if (string.IsNullOrEmpty(tablePath))
        {
            return tablePath;
        }

        try
        {
            return Path.GetFullPath(tablePath);
        }
        catch
        {
            return tablePath;
        }
    }

    public static PlayStats Load(string? path = null) =>
        TryLoad(path ?? DefaultPath, out var s, out _) ? s : new PlayStats(); // unreadable: shown as empty

    /// <summary>
    /// The stats in the file (empty when there's none). False when the file is there but can't be read or parsed;
    /// <paramref name="corrupt"/> says it was read and isn't stats (bad JSON, or zeros left by a power cut).
    /// </summary>
    static bool TryLoad(string path, out PlayStats stats, out bool corrupt)
    {
        stats = new PlayStats();
        corrupt = false;
        string? text;
        try
        {
            text = ReadShared(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return true; // none, or empty: nothing to lose
        }

        try
        {
            if (JsonSerializer.Deserialize<PlayStats>(text) is { } s)
            {
                // Re-key case-insensitively (the deserializer builds an ordinal dictionary).
                s.Tables = new Dictionary<string, TableStats>(
                    s.Tables ?? new Dictionary<string, TableStats>(),
                    StringComparer.OrdinalIgnoreCase
                );
                stats = s;
            }
            return true;
        }
        catch (JsonException)
        {
            corrupt = true;
            return false;
        }
    }

    /// <summary>
    /// Read-modify-write under a cross-process lock. Returns the saved stats. Throws <see cref="IOException"/>, and
    /// writes nothing, when the lock can't be had or the file is there but can't be opened (in use, no access):
    /// saving then would replace every table's stats with this one change. A file that was read but isn't stats is
    /// kept as .bad and the stats start again, rather than no change ever being saved.
    /// </summary>
    public static PlayStats Mutate(Action<PlayStats> change, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var _ =
            AcquireLock(path + ".lock")
            ?? throw new IOException("The play stats are busy in another program; try again.");
        if (!TryLoad(path, out var stats, out var corrupt))
        {
            if (!corrupt)
            {
                throw new IOException(
                    $"{Path.GetFileName(path)} can't be read, so it's left as it is."
                );
            }

            SetAsideBad(path);
        }

        change(stats);
        WriteAtomic(path, JsonSerializer.Serialize(stats, Json));
        return stats;
    }

    /// <summary>
    /// A file the manager and the VR lobby share, or null when there's none. Opened so the other program can still
    /// swap in a new copy (File.Move over it), and tried again for a moment while it's doing so.
    /// </summary>
    internal static string? ReadShared(string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            // Access denied too: opening it just as another process swaps it in can say so for a moment.
            catch (Exception e) when (i < 10 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(30);
            }
        }
    }

    /// <summary>A shared file that was read but isn't what it should be: kept as &lt;file&gt;.bad (throws when it can't be).</summary>
    internal static void SetAsideBad(string path) =>
        File.Copy(path, path + ".bad", overwrite: true);

    /// <summary>
    /// Writes a copy beside the file, on disk before it's swapped in (a power cut can't leave an empty or zeroed
    /// file), then swaps it in.
    /// </summary>
    internal static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        MoveReplacing(tmp, path);
    }

    /// <summary>
    /// Moves <paramref name="tmp"/> over <paramref name="path"/>, tried again for a moment while a reader has it
    /// open; deletes <paramref name="tmp"/> when it still can't.
    /// </summary>
    internal static void MoveReplacing(string tmp, string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < 10)
            {
                Thread.Sleep(30);
            }
            catch
            {
                try
                {
                    File.Delete(tmp);
                }
                catch { }
                throw;
            }
        }
    }

    /// <summary>The cross-process lock for a shared file; null when it can't be had within 2 s.</summary>
    internal static FileStream? AcquireLock(string lockPath)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose
                );
            }
            catch (IOException)
            {
                Thread.Sleep(40);
            }
        }
        return null; // couldn't lock in 2 s: the caller gives up rather than race the other writer
    }

    TableStats? Get(string tablePath) => Tables.GetValueOrDefault(Key(tablePath));

    TableStats GetOrAdd(string tablePath)
    {
        var k = Key(tablePath);
        if (!Tables.TryGetValue(k, out var t))
        {
            Tables[k] = t = new TableStats();
        }

        return t;
    }

    public bool IsFavourite(string tablePath) => Get(tablePath)?.Favourite == true;

    public int PlaysOf(string tablePath) => Get(tablePath)?.Plays ?? 0;

    public DateTime? LastPlayedOf(string tablePath) => Get(tablePath)?.LastPlayed;

    public double MinutesOf(string tablePath) => Get(tablePath)?.MinutesPlayed ?? 0;

    public void SetFavourite(string tablePath, bool on) => GetOrAdd(tablePath).Favourite = on;

    /// <summary>Counts a play. Call when the table actually launched; <paramref name="duration"/> once it closed.</summary>
    public void RecordPlay(string tablePath, DateTime startedAt, TimeSpan? duration = null)
    {
        var t = GetOrAdd(tablePath);
        t.Plays++;
        t.LastPlayed = startedAt;
        if (duration is { } d && d > TimeSpan.Zero)
        {
            t.MinutesPlayed += d.TotalMinutes;
        }
    }

    public void AddTime(string tablePath, TimeSpan played)
    {
        if (played > TimeSpan.Zero)
        {
            GetOrAdd(tablePath).MinutesPlayed += played.TotalMinutes;
        }
    }

    /// <summary>Marks a table as just picked without counting a play (the lobby, before handing over).</summary>
    public void Touch(string tablePath, DateTime when) => GetOrAdd(tablePath).LastPlayed = when;

    /// <summary>
    /// A table's file moved (an update with a new file name): its favourite, plays and time go with it. If the new
    /// path already has stats, the two are added together. False when the old path had none.
    /// </summary>
    public bool MoveTable(string oldPath, string newPath)
    {
        var from = Key(oldPath);
        var to = Key(newPath);
        if (
            string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
            || !Tables.Remove(from, out var old)
        )
        {
            return false;
        }

        if (Tables.TryGetValue(to, out var cur))
        {
            cur.Favourite |= old.Favourite;
            cur.Plays += old.Plays;
            cur.MinutesPlayed += old.MinutesPlayed;
            if (old.LastPlayed > cur.LastPlayed || cur.LastPlayed == null)
            {
                cur.LastPlayed = old.LastPlayed;
            }
        }
        else
        {
            Tables[to] = old;
        }

        return true;
    }

    public List<string> Favourites() =>
        Tables.Where(kv => kv.Value.Favourite).Select(kv => kv.Key).ToList();

    /// <summary>Most recent first.</summary>
    public List<string> Recent(int max = 30) =>
        Tables
            .Where(kv => kv.Value.LastPlayed != null)
            .OrderByDescending(kv => kv.Value.LastPlayed)
            .Take(max)
            .Select(kv => kv.Key)
            .ToList();

    /// <summary>Most plays first; ties go to the more recently played.</summary>
    public List<string> MostPlayed(int max = 30) =>
        Tables
            .Where(kv => kv.Value.Plays > 0)
            .OrderByDescending(kv => kv.Value.Plays)
            .ThenByDescending(kv => kv.Value.LastPlayed)
            .Take(max)
            .Select(kv => kv.Key)
            .ToList();
}
