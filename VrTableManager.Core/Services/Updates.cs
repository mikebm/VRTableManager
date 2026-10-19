using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>An installed table whose VPS entry now lists a newer version.</summary>
public sealed record TableUpdate(InstalledTable Table, VpsGame Game, VpsTableFile File)
{
    public string Label => $"v{Table.Manifest.Version ?? "?"} → v{File.Version ?? "?"}";
}

/// <summary>A downloadable release of a tool (UEVR etc.) on GitHub.</summary>
public sealed record ToolRelease(string Tag, string Url, long Size);

public static class TableUpdates
{
    /// <summary>
    /// Compares each installed table with the VPS entry it was installed from (same table file id).
    /// VPS edits an entry in place when an author releases a new version, so a different version
    /// string means there's an update. Metadata-only edits (new artwork, links) change UpdatedAt
    /// but not the version, so UpdatedAt is only used when neither side has a version.
    /// </summary>
    public static List<TableUpdate> Find(IEnumerable<InstalledTable> installed, IReadOnlyList<VpsGame> games)
    {
        var byId = games.ToDictionary(g => g.Id);
        var result = new List<TableUpdate>();
        foreach (var t in installed)
        {
            var m = t.Manifest;
            if (m.GameId == "" || m.TableFileId == "" || !byId.TryGetValue(m.GameId, out var game)) continue;
            var tf = game.TableFiles?.FirstOrDefault(f => f.Id == m.TableFileId);
            if (tf == null) continue;

            var newer = !string.IsNullOrWhiteSpace(tf.Version)
                ? !SameVersion(tf.Version, m.Version)
                : string.IsNullOrWhiteSpace(m.Version) && tf.UpdatedAt > (m.SourceUpdatedAt ?? long.MaxValue);
            if (newer && tf.Urls?.Any(u => !u.Broken) == true) result.Add(new TableUpdate(t, game, tf));
        }
        return result;
    }

    public static bool SameVersion(string? a, string? b) => Normalize(a) == Normalize(b);

    static string Normalize(string? v) => (v ?? "").Trim().TrimStart('v', 'V').Trim().ToLowerInvariant();
}

/// <summary>Installs and updates UEVR from github.com/praydog/UEVR (stable releases).</summary>
public static class UevrSetup
{
    const string ReleasesApi = "https://api.github.com/repos/praydog/UEVR/releases?per_page=10";
    const string VersionFile = "vrtm-version.txt";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VPinball", "UEVR");

    public static async Task<ToolRelease?> FindLatestAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(ReleasesApi, ct));
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.GetProperty("draft").GetBoolean() || rel.GetProperty("prerelease").GetBoolean()) continue;
            foreach (var asset in rel.GetProperty("assets").EnumerateArray())
                if (asset.GetProperty("name").GetString() == "UEVR.zip")
                    return new ToolRelease(rel.GetProperty("tag_name").GetString()!,
                        asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64());
        }
        return null;
    }

    /// <summary>Version this app installed; "unknown" for a copy you installed yourself; null if not installed.</summary>
    public static string? InstalledVersion(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.UevrDir) || !File.Exists(PfxSetup.InjectorPath(s))) return null;
        var f = Path.Combine(s.UevrDir, VersionFile);
        return File.Exists(f) ? File.ReadAllText(f).Trim() : "unknown";
    }

    /// <summary>
    /// Unpacks UEVR.zip over the UEVR folder. Game profiles live in %APPDATA%\UnrealVRMod,
    /// not here, so updating never touches the Pinball FX profile.
    /// </summary>
    public static async Task InstallAsync(AppSettings s, ToolRelease rel, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.UevrDir)) s.UevrDir = DefaultDir;
        if (Process.GetProcessesByName("UEVRInjector").Length > 0)
            throw new InvalidOperationException("Close UEVRInjector first, then update.");
        var tmp = Path.Combine(Path.GetTempPath(), $"UEVR-{rel.Tag}.zip");
        await Http.DownloadFileAsync(rel.Url, tmp, progress, ct);
        await Task.Run(() => TableInstaller.Extract(tmp, s.UevrDir), ct);
        await File.WriteAllTextAsync(Path.Combine(s.UevrDir, VersionFile), rel.Tag, ct);
        File.Delete(tmp);
    }
}

/// <summary>
/// BAM is only published on ravarcade.pl (no stable download API), so this only reports
/// the latest version shown there; installing stays manual.
/// </summary>
public static partial class BamCheck
{
    public const string DownloadPage = "https://www.ravarcade.pl/";

    public static async Task<string?> LatestAsync(CancellationToken ct)
    {
        var html = await Http.Client.GetStringAsync(DownloadPage, ct);
        return ParseLatest(html);
    }

    /// <summary>Reads "BAM version: v.1.5-412" (the site's format) into "1.5-412".</summary>
    public static string? ParseLatest(string html) =>
        VersionRegex().Match(html) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Version stamped on the installed FPLoader.exe, if it carries one.</summary>
    public static string? InstalledVersion(AppSettings s)
    {
        var loader = FpSetup.LoaderPath(s);
        if (string.IsNullOrWhiteSpace(s.FpDir) || !File.Exists(loader)) return null;
        var v = FileVersionInfo.GetVersionInfo(loader);
        return string.IsNullOrWhiteSpace(v.ProductVersion) ? "installed" : v.ProductVersion.Trim();
    }

    [GeneratedRegex(@"BAM\s+version:\s*v\.?\s*([0-9][0-9.]*(?:-[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
