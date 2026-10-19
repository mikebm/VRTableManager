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
    public Dictionary<string, TableStats> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(AppSettings.DataDir, "play-stats.json");

    /// <summary>Stats file next to another settings file (keeps tests and custom setups self-contained).</summary>
    public static string PathBeside(string? otherFile) =>
        string.IsNullOrEmpty(otherFile) ? DefaultPath : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(otherFile))!, "play-stats.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Key(string tablePath)
    {
        if (string.IsNullOrEmpty(tablePath)) return tablePath;
        try { return Path.GetFullPath(tablePath); } catch { return tablePath; }
    }

    public static PlayStats Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<PlayStats>(File.ReadAllText(path)) is { } s)
            {
                // Re-key case-insensitively (the deserializer builds an ordinal dictionary).
                s.Tables = new Dictionary<string, TableStats>(s.Tables, StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch { /* corrupt or mid-write: treat as empty */ }
        return new PlayStats();
    }

    /// <summary>Read-modify-write under a cross-process lock. Returns the saved stats.</summary>
    public static PlayStats Mutate(Action<PlayStats> change, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var _ = AcquireLock(path + ".lock");
        var stats = Load(path);
        change(stats);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(stats, Json));
        File.Move(tmp, path, overwrite: true);
        return stats;
    }

    static FileStream? AcquireLock(string lockPath)
    {
        for (var i = 0; i < 50; i++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { Thread.Sleep(40); }
        }
        return null; // couldn't lock in 2 s: write anyway rather than lose the change
    }

    TableStats? Get(string tablePath) => Tables.GetValueOrDefault(Key(tablePath));

    TableStats GetOrAdd(string tablePath)
    {
        var k = Key(tablePath);
        if (!Tables.TryGetValue(k, out var t)) Tables[k] = t = new TableStats();
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
        if (duration is { } d && d > TimeSpan.Zero) t.MinutesPlayed += d.TotalMinutes;
    }

    public void AddTime(string tablePath, TimeSpan played)
    {
        if (played > TimeSpan.Zero) GetOrAdd(tablePath).MinutesPlayed += played.TotalMinutes;
    }

    /// <summary>Marks a table as just picked without counting a play (the lobby, before handing over).</summary>
    public void Touch(string tablePath, DateTime when) => GetOrAdd(tablePath).LastPlayed = when;

    public List<string> Favourites() => Tables.Where(kv => kv.Value.Favourite).Select(kv => kv.Key).ToList();

    /// <summary>Most recent first.</summary>
    public List<string> Recent(int max = 30) => Tables.Where(kv => kv.Value.LastPlayed != null)
        .OrderByDescending(kv => kv.Value.LastPlayed).Take(max).Select(kv => kv.Key).ToList();

    /// <summary>Most plays first; ties go to the more recently played.</summary>
    public List<string> MostPlayed(int max = 30) => Tables.Where(kv => kv.Value.Plays > 0)
        .OrderByDescending(kv => kv.Value.Plays).ThenByDescending(kv => kv.Value.LastPlayed)
        .Take(max).Select(kv => kv.Key).ToList();
}
