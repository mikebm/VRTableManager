using VrTableManager.Models;

namespace VrTableManager.Lobby;

/// <summary>A jump-rail entry: a letter, manufacturer or decade, and the first table in it (-1 = empty, greyed out).</summary>
public sealed record LobbyGroup(string Label, int Index);

public sealed record LobbyView(List<LobbyTable> List, List<LobbyGroup> Groups);

/// <summary>Filtering, sorting, grouping for the jump rail, and stick acceleration. No Godot types, so it's unit-tested with plain dotnet.</summary>
public static class LobbyNav
{
    public static readonly string[] Filters = ["All", "VPX", "Future Pinball", "Pinball FX", "Pinball M", "Recently played", "Most played", "Favourites", "Manufacturer", "Decade"];
    public const string Letters = "#ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static LobbyView Apply(IReadOnlyList<LobbyTable> tables, string filter, LobbyState state)
    {
        IEnumerable<LobbyTable> q = filter switch
        {
            "VPX" => tables.Where(t => t.Engine == TableManifest.Vpx),
            "Future Pinball" => tables.Where(t => t.Engine == TableManifest.FuturePinball),
            "Pinball FX" => tables.Where(t => t.Engine == TableManifest.PinballFx),
            "Pinball M" => tables.Where(t => t.Engine == TableManifest.PinballM),
            "Favourites" => tables.Where(t => state.IsFavourite(t.Id)),
            _ => tables,
        };

        if (filter is "Recently played" or "Most played")
        {
            // Newest first / most plays first; tables that were removed since are skipped.
            var byId = tables.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var ids = filter == "Recently played" ? state.Recent : state.MostPlayed;
            return new(ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), []);
        }
        if (filter == "Manufacturer")
        {
            var list = q.OrderBy(ManufacturerOf, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
            return new(list, GroupsBy(list, ManufacturerOf));
        }
        if (filter == "Decade")
        {
            var list = q.OrderBy(t => t.Year > 0 ? t.Year : 9999).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
            return new(list, GroupsBy(list, DecadeOf));
        }

        var sorted = q.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var first = new int[Letters.Length];
        Array.Fill(first, -1);
        for (var i = 0; i < sorted.Count; i++)
        {
            var g = Letters.IndexOf(LetterOf(sorted[i].Title));
            if (first[g] == -1) first[g] = i;
        }
        return new(sorted, Letters.Select((c, g) => new LobbyGroup(c.ToString(), first[g])).ToList());
    }

    static List<LobbyGroup> GroupsBy(List<LobbyTable> list, Func<LobbyTable, string> key)
    {
        var groups = new List<LobbyGroup>();
        for (var i = 0; i < list.Count; i++)
        {
            var k = key(list[i]);
            if (groups.Count == 0 || groups[^1].Label != k) groups.Add(new(k, i));
        }
        return groups;
    }

    /// <summary>"#" for titles starting with a digit or symbol. A leading "The " is skipped, so "The Addams Family" files under A.</summary>
    public static string LetterOf(string title)
    {
        var t = title.Trim();
        if (t.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && t.Length > 4) t = t[4..];
        var c = t.Length > 0 ? char.ToUpperInvariant(t[0]) : '#';
        return c is >= 'A' and <= 'Z' ? c.ToString() : "#";
    }

    public static string ManufacturerOf(LobbyTable t) => string.IsNullOrWhiteSpace(t.Manufacturer) ? "Unknown" : t.Manufacturer.Trim();

    public static string DecadeOf(LobbyTable t) => t.Year > 0 ? $"{t.Year / 10 * 10}s" : "Unknown";

    /// <summary>Group containing table index i (position in the groups list), or -1.</summary>
    public static int GroupAt(IReadOnlyList<LobbyGroup> groups, int i)
    {
        var found = -1;
        for (var g = 0; g < groups.Count; g++)
            if (groups[g].Index != -1 && groups[g].Index <= i) found = g;
        return found;
    }

    /// <summary>
    /// Table index to jump to: the next group's first table (dir = 1), or the start of this group /
    /// the previous group (dir = -1), like a phone's contact list. Empty groups are skipped.
    /// </summary>
    public static int Jump(IReadOnlyList<LobbyGroup> groups, int i, int dir)
    {
        var g = GroupAt(groups, i);
        if (dir < 0 && g != -1 && groups[g].Index < i) return groups[g].Index;
        for (var k = g + dir; k >= 0 && k < groups.Count; k += dir)
            if (groups[k].Index != -1) return groups[k].Index;
        return i;
    }

    /// <summary>Index to select for rail group g; an empty letter goes to the next letter that has tables.</summary>
    public static int IndexForGroup(IReadOnlyList<LobbyGroup> groups, int g)
    {
        for (var k = g; k < groups.Count; k++)
            if (groups[k].Index != -1) return groups[k].Index;
        for (var k = g - 1; k >= 0; k--)
            if (groups[k].Index != -1) return groups[k].Index;
        return 0;
    }

    /// <summary>Rail group for a typed key: a letter, a digit (→ "#"), or the first manufacturer/decade starting with it.</summary>
    public static int GroupForKey(IReadOnlyList<LobbyGroup> groups, char key)
    {
        var k = char.ToUpperInvariant(key);
        for (var g = 0; g < groups.Count; g++)
        {
            var label = groups[g].Label;
            if (label == k.ToString() || (label.Length > 1 && char.ToUpperInvariant(label[0]) == k) || (label == "#" && char.IsDigit(k)))
                return g;
        }
        return -1;
    }

    /// <summary>Seconds between repeats while the stick is held: 0.3 s at first, down to 0.05 s after ~2 s.</summary>
    public static double RepeatInterval(double held) => Math.Max(0.05, 0.3 - held * 0.125);
}
