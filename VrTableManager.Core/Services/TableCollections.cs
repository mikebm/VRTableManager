using System.Text.Json;

namespace VrTableManager.Services;

/// <summary>One of your own groups of tables ("Party night", "90s Williams"). A table can be in any number of them.</summary>
public sealed class TableCollection
{
    public string Name { get; set; } = "";

    /// <summary>Full table paths, in the order they were added.</summary>
    public List<string> Tables { get; set; } = [];

    public bool Contains(string tablePath) =>
        Tables.Contains(PlayStats.Key(tablePath), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Your collections of tables, shared by VR Table Manager (My Tables → Collection, and each card's ⋯ menu) and the
/// VR lobby (each collection is a filter after the built-in ones). Kept in collections.json in the app's data
/// folder, keyed by full table path like <see cref="PlayStats"/>, and written the same way: re-read under a lock
/// and replaced atomically, since the manager and the lobby can both have it open.
/// </summary>
public sealed class TableCollections
{
    public const int MaxNameLength = 40;

    public List<TableCollection> Collections { get; set; } = [];

    public static string DefaultPath => Path.Combine(AppSettings.DataDir, "collections.json");

    /// <summary>The collections file next to another settings file (keeps tests and custom setups self-contained).</summary>
    public static string PathBeside(string? otherFile) =>
        string.IsNullOrEmpty(otherFile)
            ? DefaultPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(otherFile))!, "collections.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static TableCollections Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (
                File.Exists(path)
                && JsonSerializer.Deserialize<TableCollections>(File.ReadAllText(path)) is { } c
            )
            {
                c.Tidy();
                return c;
            }
        }
        catch
        { /* corrupt or mid-write: treat as empty */
        }
        return new TableCollections();
    }

    /// <summary>Read-modify-write under a cross-process lock. Returns the saved collections.</summary>
    public static TableCollections Mutate(Action<TableCollections> change, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var _ = PlayStats.AcquireLock(path + ".lock");
        var c = Load(path);
        change(c);
        c.Tidy();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, Json));
        File.Move(tmp, path, overwrite: true);
        return c;
    }

    /// <summary>
    /// Drops nameless collections and repeated tables (a hand-edited or merged file). Collections that end up with
    /// the same name (differing only in capitals, or in letters past the 40th) become one, keeping all their tables.
    /// </summary>
    void Tidy()
    {
        Collections = Collections
            .Where(c => c != null && CleanName(c.Name) != null)
            .GroupBy(c => CleanName(c.Name)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TableCollection
            {
                Name = CleanName(g.First().Name)!,
                Tables = g.SelectMany(c => c.Tables ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(PlayStats.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>A name as it's stored: trimmed, inner runs of spaces made one, at most 40 letters. Null when blank.</summary>
    public static string? CleanName(string? name)
    {
        var n = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (n.Length > MaxNameLength)
        {
            n = n[..MaxNameLength].TrimEnd();
        }
        return n == "" ? null : n;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> Names => Collections.Select(c => c.Name);

    public TableCollection? Find(string? name) =>
        Collections.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>The collections a table is in, in the order of the list.</summary>
    public List<string> NamesOf(string tablePath) =>
        Collections.Where(c => c.Contains(tablePath)).Select(c => c.Name).ToList();

    public bool Contains(string name, string tablePath) => Find(name)?.Contains(tablePath) == true;

    /// <summary>Adds an empty collection. Returns its stored name, or null if the name is blank or taken.</summary>
    public string? Create(string? name, IEnumerable<string>? reserved = null)
    {
        var n = CleanName(name);
        if (n == null || Find(n) != null || IsReserved(n, reserved))
        {
            return null;
        }

        Collections.Add(new TableCollection { Name = n });
        return n;
    }

    /// <summary>Renames a collection. False if it doesn't exist or the new name is blank or taken by another one.</summary>
    public bool Rename(string name, string? newName, IEnumerable<string>? reserved = null)
    {
        var c = Find(name);
        var n = CleanName(newName);
        if (c == null || n == null || IsReserved(n, reserved))
        {
            return false;
        }

        if (Find(n) is { } other && other != c)
        {
            return false;
        }

        c.Name = n;
        return true;
    }

    public bool Delete(string name) => Find(name) is { } c && Collections.Remove(c);

    /// <summary>Puts a table in a collection or takes it out. False if there's no such collection.</summary>
    public bool Set(string name, string tablePath, bool on)
    {
        if (Find(name) is not { } c)
        {
            return false;
        }

        var key = PlayStats.Key(tablePath);
        c.Tables.RemoveAll(t => string.Equals(t, key, StringComparison.OrdinalIgnoreCase));
        if (on)
        {
            c.Tables.Add(key);
        }

        return true;
    }

    /// <summary>A table's file moved (an update with a new file name): it stays in its collections.</summary>
    public bool MoveTable(string oldPath, string newPath)
    {
        var from = PlayStats.Key(oldPath);
        var to = PlayStats.Key(newPath);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return false; // the same table (paths only differ in capitals): nothing to move, nothing to drop
        }

        var moved = false;
        foreach (var c in Collections)
        {
            var i = c.Tables.FindIndex(t =>
                string.Equals(t, from, StringComparison.OrdinalIgnoreCase)
            );
            if (i < 0)
            {
                continue;
            }

            moved = true;
            if (c.Tables.Any(t => string.Equals(t, to, StringComparison.OrdinalIgnoreCase)))
            {
                c.Tables.RemoveAt(i);
            }
            else
            {
                c.Tables[i] = to;
            }
        }
        return moved;
    }

    /// <summary>Names the lobby already uses for its own filters ("Favourites", "Decade" …) can't be collection names.</summary>
    static bool IsReserved(string name, IEnumerable<string>? reserved) =>
        reserved?.Contains(name, StringComparer.OrdinalIgnoreCase) == true;
}
