using VrTableManager.Services;

namespace VrTableManager.Lobby;

/// <summary>Lobby wording that depends on data: the play-stats line and empty-filter messages. Pure, so it's tested without Godot.</summary>
public static class LobbyText
{
    /// <summary>"Played 12× · 3 h 20 min · last played 2 days ago", or "Not played yet".</summary>
    public static string Stats(PlayStats stats, string id, DateTime now)
    {
        var plays = stats.PlaysOf(id);
        var last = stats.LastPlayedOf(id);
        if (plays == 0 && last == null) return "Not played yet";
        var parts = new List<string>();
        if (plays > 0) parts.Add($"Played {plays}×");
        if (Duration(stats.MinutesOf(id)) is { } time) parts.Add(time);
        if (last is { } when) parts.Add("last played " + Ago(when, now));
        return string.Join("  ·  ", parts);
    }

    /// <summary>"45 min", "3 h 20 min", "12 h"; null under a minute.</summary>
    public static string? Duration(double minutes)
    {
        var m = (int)Math.Round(minutes);
        if (m < 1) return null;
        if (m < 60) return $"{m} min";
        return m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";
    }

    public static string Ago(DateTime when, DateTime now)
    {
        var d = now - when;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (when.Date == now.Date) return "today";
        if (when.Date == now.Date.AddDays(-1)) return "yesterday";
        if (d < TimeSpan.FromDays(14)) return $"{(int)Math.Ceiling(d.TotalDays)} days ago";
        return when.ToString("d MMM yyyy");
    }

    /// <summary>What to say when a filter has nothing in it.</summary>
    public static string Empty(string filter, int libraryCount) => libraryCount == 0
        ? "Install tables with VR Table Manager, then open the lobby again."
        : filter switch
        {
            "Favourites" => "No favourites yet. Right grip (F on the keyboard) on a table, or ★ in VR Table Manager.",
            "Recently played" => "Nothing played yet. Tables you launch show up here.",
            "Most played" => "No plays counted yet. A play counts once the table actually starts.",
            "Pinball FX" => "Pinball FX isn't installed (Steam or Epic).",
            "Pinball M" => "Pinball M isn't installed (Steam).",
            "Future Pinball" => "No Future Pinball tables. Set the Future Pinball folder in VR Table Manager.",
            _ => $"Nothing in “{filter}”.",
        } + "  Press B / Tab to change the filter.";
}
