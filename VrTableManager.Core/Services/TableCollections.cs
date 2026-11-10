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

    /// <summary>Smart collections you saved (<see cref="SmartRule"/>); the ready-made ones aren't stored.</summary>
    public List<SmartRule> Smart { get; set; } = [];

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

        // Saved smart collections: one per name, and never the name of a collection or a ready-made one. A clash
        // (a hand edit, or a ready-made one added later with a name you'd used) gets " (2)", " (3)" … rather than
        // losing the rule.
        var taken = new HashSet<string>(
            Collections.Select(c => c.Name).Concat(SmartCollections.BuiltInNames),
            StringComparer.OrdinalIgnoreCase
        );
        Smart = (Smart ?? []).Where(r => r != null && CleanName(r.Name) != null).ToList();
        foreach (var r in Smart)
        {
            var cleaned = CleanName(r.Name)!;
            var name = cleaned;
            for (var i = 2; !taken.Add(name); i++)
            {
                var suffix = $" ({i})";
                name =
                    cleaned[..Math.Min(cleaned.Length, MaxNameLength - suffix.Length)].TrimEnd()
                    + suffix;
            }
            r.Name = name;
            r.Search ??= "";
            r.Engine ??= "";
            r.Maker ??= "";
            r.Era ??= "";
            r.Type ??= "";
            r.Theme ??= "";
            r.Collection ??= "";
        }
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

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> SmartNames => Smart.Select(r => r.Name);

    public SmartRule? FindSmart(string? name) =>
        Smart.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a name is in use by a collection, a saved smart collection or a ready-made one (other than
    /// <paramref name="except"/>, the one being renamed), or is one of the <paramref name="reserved"/> names.
    /// </summary>
    public bool IsTaken(string name, IEnumerable<string>? reserved = null, object? except = null) =>
        IsReserved(name, reserved)
        || SmartCollections.FindBuiltIn(name) != null
        || (Find(name) is { } c && c != except)
        || (FindSmart(name) is { } r && r != except);

    /// <summary>Adds an empty collection. Returns its stored name, or null if the name is blank or taken.</summary>
    public string? Create(string? name, IEnumerable<string>? reserved = null)
    {
        var n = CleanName(name);
        if (n == null || IsTaken(n, reserved))
        {
            return null;
        }

        Collections.Add(new TableCollection { Name = n });
        return n;
    }

    /// <summary>Saves a smart collection under its name. Returns the stored name, or null if blank or taken.</summary>
    public string? SaveSmart(SmartRule rule, IEnumerable<string>? reserved = null)
    {
        var n = CleanName(rule.Name);
        if (n == null || IsTaken(n, reserved))
        {
            return null;
        }

        rule.Name = n;
        Smart.Add(rule);
        return n;
    }

    /// <summary>
    /// Renames a collection or a saved smart collection (smart ones that use a renamed collection keep up). False if
    /// there's no such one, or the new name is blank or taken by another.
    /// </summary>
    public bool Rename(string name, string? newName, IEnumerable<string>? reserved = null)
    {
        object? which = (object?)Find(name) ?? FindSmart(name);
        var n = CleanName(newName);
        // Only the capitals changing is always fine, even for an older collection that has a name now reserved.
        if (
            which == null
            || n == null
            || (
                !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                && IsTaken(n, reserved, which)
            )
        )
        {
            return false;
        }

        if (which is TableCollection c)
        {
            foreach (
                var r in Smart.Where(r =>
                    string.Equals(r.Collection, c.Name, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                r.Collection = n;
            }
            c.Name = n;
        }
        else
        {
            ((SmartRule)which).Name = n;
        }

        return true;
    }

    /// <summary>Deletes a collection or a saved smart collection.</summary>
    public bool Delete(string name) =>
        Find(name) is { } c ? Collections.Remove(c) : FindSmart(name) is { } r && Smart.Remove(r);

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
