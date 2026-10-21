using System.Text.Json;

namespace VrTableManager.Services;

/// <summary>A download the user asked for that hasn't finished yet.</summary>
public sealed class PendingDownload
{
    public const string TableKind = "Table";
    public const string RomKind = "Rom";

    /// <summary>Matches the download job's id while the app is running.</summary>
    public string Id { get; set; } = "";
    /// <summary>"Table" (install or update a table) or "Rom" (fetch a table's ROMs).</summary>
    public string Kind { get; set; } = TableKind;
    public string Title { get; set; } = "";
    /// <summary>Catalog ids of the table to install (Kind = Table).</summary>
    public string GameId { get; set; } = "";
    public string TableFileId { get; set; } = "";
    /// <summary>The installed table being updated (Kind = Table) or getting ROMs (Kind = Rom).</summary>
    public string? TablePath { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// The download queue, saved to download-queue.json as items are added and finished, so downloads
/// that were queued or running when the app closed can be offered again at the next launch.
/// (Downloads restart from the beginning: browser downloads can't resume halfway.)
/// </summary>
public sealed class PendingDownloads
{
    readonly string _path;
    readonly object _lock = new();
    List<PendingDownload> _items;

    public PendingDownloads(string? path = null)
    {
        _path = path ?? Path.Combine(AppSettings.DataDir, "download-queue.json");
        _items = Load(_path);
    }

    public IReadOnlyList<PendingDownload> Items { get { lock (_lock) return _items.ToList(); } }

    public void Add(PendingDownload d)
    {
        lock (_lock)
        {
            _items.RemoveAll(x => x.Id == d.Id);
            _items.Add(d);
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_items.RemoveAll(x => x.Id == id) > 0) Save();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            Save();
        }
    }

    static List<PendingDownload> Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<List<PendingDownload>>(File.ReadAllText(path)) ?? [];
        }
        catch { /* corrupt: start empty */ }
        return [];
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* losing the saved queue isn't worth interrupting a download over */ }
    }

    /// <summary>
    /// Deletes leftover temporary download folders (from downloads cut off when the app closed).
    /// Call at startup, before any download starts.
    /// </summary>
    public static int CleanDownloadsFolder(string? downloadsDir = null)
    {
        downloadsDir ??= Path.Combine(AppSettings.DataDir, "downloads");
        if (!Directory.Exists(downloadsDir)) return 0;
        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(downloadsDir))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
                removed++;
            }
            catch { /* in use: leave it for next time */ }
        }
        return removed;
    }
}
