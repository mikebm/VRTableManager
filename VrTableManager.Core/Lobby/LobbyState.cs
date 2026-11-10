using System.Text.Json;
using System.Text.Json.Serialization;
using VrTableManager.Services;

namespace VrTableManager.Lobby;

/// <summary>
/// What the VR lobby remembers between runs (it restarts after every game): the chosen filter,
/// the ring/wall view and your height adjustment. Favourites and play history live in the shared
/// <see cref="PlayStats"/> file (play-stats.json beside this one), so the manager shows the same ones.
/// </summary>
public sealed class LobbyState
{
    public const int MaxRecent = 30;

    public string Filter { get; set; } = "All";

    /// <summary>"ring" or "wall".</summary>
    public string View { get; set; } = "ring";

    /// <summary>Height adjustment in metres (see <see cref="LobbyHeight"/>); re-applied every time the lobby starts.</summary>
    public double HeightOffset { get; set; }

    // Older lobby-state files kept favourites and recents here; they're moved into PlayStats on load.
    [JsonPropertyName("Favourites"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyFavourites { get; set; }

    [JsonPropertyName("Recent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyRecent { get; set; }

    [JsonIgnore]
    public string Path { get; private set; } = DefaultPath;

    [JsonIgnore]
    public string StatsPath => PlayStats.PathBeside(Path);

    [JsonIgnore]
    public string CollectionsPath => TableCollections.PathBeside(Path);

    /// <summary>Your collections from the manager (collections.json beside this file), refreshed with the stats.</summary>
    [JsonIgnore]
    public TableCollections Collections { get; private set; } = new();

    /// <summary>A collection's filter. A built-in filter always wins, whatever the capitals (a hand-edited "decade" is ignored).</summary>
    public bool IsCollection(string filter) =>
        !LobbyNav.Filters.Contains(filter, StringComparer.OrdinalIgnoreCase)
        && Collections.Find(filter) != null;

    /// <summary>
    /// A smart collection's filter (ready-made or saved in the manager). Built-in filters and your own collections
    /// win over a smart one with the same name.
    /// </summary>
    public bool IsSmart(string filter) =>
        !LobbyNav.Filters.Contains(filter, StringComparer.OrdinalIgnoreCase)
        && Collections.Find(filter) == null
        && SmartCollections.Exists(filter, Collections);

    /// <summary>
    /// The manager's cached catalog by game id, for smart collections that look at a table's type or themes. Empty
    /// until the lobby has read it (in the background); those tables then only match when it's there.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, Models.VpsGame> Catalog
    {
        get => _catalog;
        set
        {
            _catalog = value;
            _smartSearch.Clear(); // themes and designers are searchable now
        }
    }
    IReadOnlyDictionary<string, Models.VpsGame> _catalog = new Dictionary<string, Models.VpsGame>();
    readonly Dictionary<string, SearchEntry> _smartSearch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a table is in the smart collection called <paramref name="name"/>.</summary>
    public bool InSmart(string name, LobbyTable t, DateTime now)
    {
        var game = _catalog.GetValueOrDefault(t.Source.Manifest.GameId);
        if (!_smartSearch.TryGetValue(t.Id, out var search))
        {
            _smartSearch[t.Id] = search = TableSearch.Index(t.Source.Manifest, game);
        }
        return SmartCollections.Matches(
            name,
            SmartTable.From(t.Source, game, Stats, search),
            Collections,
            now
        );
    }

    /// <summary>Latest shared stats (refreshed on load and after every change here).</summary>
    [JsonIgnore]
    public PlayStats Stats { get; private set; } = new();

    /// <summary>Favourite table ids.</summary>
    [JsonIgnore]
    public List<string> Favourites => Stats.Favourites();

    /// <summary>Table ids, most recent first.</summary>
    [JsonIgnore]
    public List<string> Recent => Stats.Recent(MaxRecent);

    /// <summary>Table ids, most plays first.</summary>
    [JsonIgnore]
    public List<string> MostPlayed => Stats.MostPlayed(MaxRecent);

    public static string DefaultPath =>
        System.IO.Path.Combine(AppSettings.DataDir, "lobby-state.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LobbyState Load(string? path = null)
    {
        path = string.IsNullOrEmpty(path) ? DefaultPath : path;
        var s = new LobbyState();
        try
        {
            if (File.Exists(path))
            {
                s = JsonSerializer.Deserialize<LobbyState>(File.ReadAllText(path)) ?? s;
            }
        }
        catch
        { /* corrupt: start fresh */
        }
        if (s.View is not ("ring" or "wall"))
        {
            s.View = "ring";
        }

        s.HeightOffset = LobbyHeight.Clamp(s.HeightOffset);
        s.Path = path;

        if (s.LegacyFavourites is { Count: > 0 } || s.LegacyRecent is { Count: > 0 })
        {
            var favs = s.LegacyFavourites ?? [];
            var recent = s.LegacyRecent ?? [];
            s.Stats = PlayStats.Mutate(
                st =>
                {
                    foreach (var f in favs)
                    {
                        st.SetFavourite(f, true);
                    }
                    // Keep the old order: newest first, a minute apart, for tables not seen yet.
                    for (var i = 0; i < recent.Count; i++)
                    {
                        if (st.LastPlayedOf(recent[i]) == null)
                        {
                            st.Touch(recent[i], DateTime.Now.AddMinutes(-i - 1));
                        }
                    }
                },
                s.StatsPath
            );
            s.LegacyFavourites = null;
            s.LegacyRecent = null;
            s.Save();
        }
        else
        {
            s.RefreshStats();
        }
        // A filter that's gone (a collection deleted or renamed in the manager) falls back to All.
        s.RefreshCollections();
        s.KeepFilterValid();
        return s;
    }

    /// <summary>Falls back to All when the chosen filter no longer exists. True when it changed.</summary>
    public bool KeepFilterValid()
    {
        // The filter's own spelling, built-in ones first: "favourites" is Favourites even if a collection is
        // called that, and a collection whose capitals changed keeps up with them.
        if (
            LobbyNav
                .FiltersFor(this)
                .FirstOrDefault(f =>
                    string.Equals(f, Filter, StringComparison.OrdinalIgnoreCase)
                ) is
            { } match
        )
        {
            Filter = match;
            return false;
        }

        Filter = "All";
        return true;
    }

    DateTime _collectionsStamp;

    /// <summary>Re-reads collections.json if the manager changed it; like <see cref="RefreshStats"/>, cheap to call often.</summary>
    public bool RefreshCollections()
    {
        DateTime stamp;
        try
        {
            stamp = File.Exists(CollectionsPath)
                ? File.GetLastWriteTimeUtc(CollectionsPath)
                : default;
        }
        catch
        {
            return false;
        }
        if (stamp == _collectionsStamp)
        {
            return false;
        }

        _collectionsStamp = stamp;
        Collections = TableCollections.Load(CollectionsPath);
        return true;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        }
        catch
        { /* not worth interrupting VR over */
        }
    }

    DateTime _statsStamp;

    /// <summary>
    /// Re-reads the shared stats if another process (the manager, the --play launcher) changed the file.
    /// Cheap enough to call every few seconds: it only checks the file time unless it moved.
    /// </summary>
    public bool RefreshStats()
    {
        DateTime stamp;
        try
        {
            stamp = File.Exists(StatsPath) ? File.GetLastWriteTimeUtc(StatsPath) : default;
        }
        catch
        {
            return false;
        }
        if (stamp == _statsStamp)
        {
            return false;
        }

        _statsStamp = stamp;
        Stats = PlayStats.Load(StatsPath);
        return true;
    }

    public bool IsFavourite(string id) => Stats.IsFavourite(id);

    /// <summary>Returns the new favourite state.</summary>
    public bool ToggleFavourite(string id)
    {
        var on = !Stats.IsFavourite(id);
        try
        {
            Stats = PlayStats.Mutate(st => st.SetFavourite(id, on), StatsPath);
        }
        catch
        {
            Stats.SetFavourite(id, on); /* keep it for this session at least */
        }
        return IsFavourite(id);
    }

    /// <summary>
    /// The lobby is about to hand this table to the launcher. Moves it to the top of "Recently played";
    /// the launcher (VrTableManager --play) counts the play itself once the table has started.
    /// </summary>
    public void MarkPlayed(string id)
    {
        var now = DateTime.Now;
        try
        {
            Stats = PlayStats.Mutate(st => st.Touch(id, now), StatsPath);
        }
        catch
        {
            Stats.Touch(id, now);
        }
    }
}
