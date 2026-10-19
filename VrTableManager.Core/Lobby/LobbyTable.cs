using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.Lobby;

/// <summary>One installed table as the VR lobby shows it.</summary>
public sealed record LobbyTable(
    string Id,
    string Engine,
    string Title,
    string Subtitle,
    string Manufacturer,
    int Year,
    string ImgUrl,
    IReadOnlyList<string> MissingRoms,
    InstalledTable Source)
{
    /// <summary>Short badge text for the card.</summary>
    public string Badge => Engine switch
    {
        TableManifest.Vpx => "VPX",
        TableManifest.FuturePinball => "FP",
        TableManifest.PinballFx => "FX",
        TableManifest.PinballM => "M",
        _ => Engine,
    };
}

public static class LobbyLibrary
{
    /// <summary>Everything the manager knows about: its table folders plus engine-owned tables (Pinball FX).</summary>
    public static List<LobbyTable> Load(AppSettings s) =>
        TableInstaller.Scan(s).Select(From).OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Table ids are full paths with the platform's separators, so ids saved in favourites/recent and
    /// passed back on --select still match when a path arrives with "/" (Godot) instead of "\".
    /// </summary>
    public static string NormalizeId(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    public static LobbyTable From(InstalledTable t)
    {
        var m = t.Manifest;
        var maker = string.Join(" ", new[] { m.Manufacturer, m.Year is > 0 ? m.Year.ToString() : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var parts = new List<string> { Engines.For(m.Engine).DisplayName };
        if (maker != "") parts.Add(maker);
        if (!string.IsNullOrWhiteSpace(m.Version)) parts.Add("v" + m.Version);
        return new LobbyTable(
            Id: NormalizeId(t.TablePath),
            Engine: m.Engine,
            Title: m.GameName,
            Subtitle: string.Join("  ·  ", parts),
            Manufacturer: m.Manufacturer ?? "",
            Year: m.Year ?? 0,
            ImgUrl: m.ImgUrl ?? "",
            MissingRoms: t.MissingRoms,
            Source: t);
    }
}
