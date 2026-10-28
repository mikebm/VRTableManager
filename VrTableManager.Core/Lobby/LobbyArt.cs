using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.Lobby;

/// <summary>
/// Which pictures and sounds the VR lobby's cabinets use. Pure lookups (no Godot), so they're
/// tested with plain dotnet.
/// </summary>
public static class LobbyArt
{
    /// <summary>The game's backglass: the first backglass file with a picture, else the game's own image (also a backglass on VPS).</summary>
    public static string? BackglassUrl(VpsGame g) =>
        g.B2sFiles?.Select(f => f.ImgUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
        ?? (string.IsNullOrWhiteSpace(g.ImgUrl) ? null : g.ImgUrl);

    /// <summary>VPS game id → backglass picture url, for every game that has one.</summary>
    public static Dictionary<string, string> BackglassIndex(IEnumerable<VpsGame> games)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in games)
            if (!string.IsNullOrEmpty(g.Id) && BackglassUrl(g) is { } url) index.TryAdd(g.Id, url);
        return index;
    }

    static bool IsStoreGame(LobbyTable t) => t.Engine is TableManifest.PinballFx or TableManifest.PinballM;

    /// <summary>
    /// Picture for the cabinet's playfield: the table's own image (VPS table shots are portrait).
    /// Store games (Pinball FX / M) only have a landscape store header, which goes on the backglass instead.
    /// </summary>
    public static string? PlayfieldUrl(LobbyTable t) => IsStoreGame(t) || string.IsNullOrWhiteSpace(t.ImgUrl) ? null : t.ImgUrl;

    /// <summary>Art key for a playfield read straight out of the table file (see <see cref="Services.VpxImages"/>).</summary>
    public const string VpxScheme = "vpx:";

    /// <summary>
    /// Where the cabinet's playfield comes from: the table's own .vpx (full resolution, real top-down art)
    /// when there is one, as "vpx:&lt;path&gt;"; otherwise the VPS picture. The VPS picture stays the
    /// fallback (<see cref="PlayfieldUrl"/>) for tables whose file has no usable playfield image.
    /// </summary>
    public static string? PlayfieldSource(LobbyTable t) =>
        t.Engine == TableManifest.Vpx && t.Source.TablePath.EndsWith(".vpx", StringComparison.OrdinalIgnoreCase) && File.Exists(t.Source.TablePath)
            ? VpxScheme + t.Source.TablePath
            : PlayfieldUrl(t);

    /// <summary>Picture for the backbox, or null to show a lit title plate.</summary>
    public static string? BackglassFor(LobbyTable t, IReadOnlyDictionary<string, string>? index)
    {
        var gameId = t.Source.Manifest.GameId;
        if (index != null && !string.IsNullOrEmpty(gameId) && index.TryGetValue(gameId, out var url)) return url;
        return IsStoreGame(t) && !string.IsNullOrWhiteSpace(t.ImgUrl) ? t.ImgUrl : null;
    }

    static readonly string[] AudioExts = [".ogg", ".mp3", ".wav"];
    static readonly string[] Preferred = ["attract", "intro", "theme", "title", "main"];
    const long MaxAudioBytes = 20 * 1024 * 1024;

    /// <summary>
    /// A short sound to play when the cabinet is selected: an .ogg/.mp3/.wav in the VPX table's own
    /// folder or its music/ subfolder (names with "attract", "intro", "theme"… first). Never for
    /// Future Pinball (its Tables folder is shared) or store games. Without such a file, the sound
    /// recorded from the table's ROM (<see cref="RomAudio"/>), if it has been. Null when there's nothing suitable.
    /// </summary>
    public static string? AttractSound(LobbyTable t) =>
        LooseSound(t) ?? (t.Engine == TableManifest.Vpx ? RomAudio.Cached(t.Source) : null);

    static string? LooseSound(LobbyTable t)
    {
        if (t.Engine != TableManifest.Vpx) return null;
        var folder = t.Source.Folder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        // Loose .vpx files in the tables root share the folder with other tables: skip those too.
        if (!File.Exists(Path.Combine(folder, "vrtm.json")) && Directory.EnumerateFiles(folder, "*.vpx").Skip(1).Any()) return null;
        try
        {
            var candidates = new[] { folder, Path.Combine(folder, "music") }
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Take(200))
                .Where(f => AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => new FileInfo(f).Length is > 0 and < MaxAudioBytes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return candidates
                .OrderBy(f => PreferenceRank(Path.GetFileNameWithoutExtension(f)))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    static int PreferenceRank(string name)
    {
        for (var i = 0; i < Preferred.Length; i++)
            if (name.Contains(Preferred[i], StringComparison.OrdinalIgnoreCase)) return i;
        return Preferred.Length;
    }
}
