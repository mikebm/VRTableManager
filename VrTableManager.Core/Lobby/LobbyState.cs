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

    [JsonIgnore] public string Path { get; private set; } = DefaultPath;
    [JsonIgnore] public string StatsPath => PlayStats.PathBeside(Path);
    /// <summary>Latest shared stats (refreshed on load and after every change here).</summary>
    [JsonIgnore] public PlayStats Stats { get; private set; } = new();

    /// <summary>Favourite table ids.</summary>
    [JsonIgnore] public List<string> Favourites => Stats.Favourites();
    /// <summary>Table ids, most recent first.</summary>
    [JsonIgnore] public List<string> Recent => Stats.Recent(MaxRecent);
    /// <summary>Table ids, most plays first.</summary>
    [JsonIgnore] public List<string> MostPlayed => Stats.MostPlayed(MaxRecent);

    public static string DefaultPath => System.IO.Path.Combine(AppSettings.DataDir, "lobby-state.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LobbyState Load(string? path = null)
    {
        path = string.IsNullOrEmpty(path) ? DefaultPath : path;
        var s = new LobbyState();
        try
        {
            if (File.Exists(path)) s = JsonSerializer.Deserialize<LobbyState>(File.ReadAllText(path)) ?? s;
        }
        catch { /* corrupt: start fresh */ }
        if (!LobbyNav.Filters.Contains(s.Filter)) s.Filter = "All";
        if (s.View is not ("ring" or "wall")) s.View = "ring";
        s.HeightOffset = LobbyHeight.Clamp(s.HeightOffset);
        s.Path = path;

        if (s.LegacyFavourites is { Count: > 0 } || s.LegacyRecent is { Count: > 0 })
        {
            var favs = s.LegacyFavourites ?? [];
            var recent = s.LegacyRecent ?? [];
            s.Stats = PlayStats.Mutate(st =>
            {
                foreach (var f in favs) st.SetFavourite(f, true);
                // Keep the old order: newest first, a minute apart, for tables not seen yet.
                for (var i = 0; i < recent.Count; i++)
                    if (st.LastPlayedOf(recent[i]) == null) st.Touch(recent[i], DateTime.Now.AddMinutes(-i - 1));
            }, s.StatsPath);
            s.LegacyFavourites = null;
            s.LegacyRecent = null;
            s.Save();
        }
        else
        {
            s.RefreshStats();
        }
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Json)); }
        catch { /* not worth interrupting VR over */ }
    }

    DateTime _statsStamp;

    /// <summary>
    /// Re-reads the shared stats if another process (the manager, the --play launcher) changed the file.
    /// Cheap enough to call every few seconds: it only checks the file time unless it moved.
    /// </summary>
    public bool RefreshStats()
    {
        DateTime stamp;
        try { stamp = File.Exists(StatsPath) ? File.GetLastWriteTimeUtc(StatsPath) : default; }
        catch { return false; }
        if (stamp == _statsStamp) return false;
        _statsStamp = stamp;
        Stats = PlayStats.Load(StatsPath);
        return true;
    }

    public bool IsFavourite(string id) => Stats.IsFavourite(id);

    /// <summary>Returns the new favourite state.</summary>
    public bool ToggleFavourite(string id)
    {
        var on = !Stats.IsFavourite(id);
        try { Stats = PlayStats.Mutate(st => st.SetFavourite(id, on), StatsPath); }
        catch { Stats.SetFavourite(id, on); /* keep it for this session at least */ }
        return IsFavourite(id);
    }

    /// <summary>
    /// The lobby is about to hand this table to the launcher. Moves it to the top of "Recently played";
    /// the launcher (VrTableManager --play) counts the play itself once the table has started.
    /// </summary>
    public void MarkPlayed(string id)
    {
        var now = DateTime.Now;
        try { Stats = PlayStats.Mutate(st => st.Touch(id, now), StatsPath); }
        catch { Stats.Touch(id, now); }
    }
}
