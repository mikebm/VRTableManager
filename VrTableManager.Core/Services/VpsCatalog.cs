using System.Text.Json;
using System.Text.Json.Serialization;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>Downloads and caches the Virtual Pinball Spreadsheet database.</summary>
public static class VpsCatalog
{
    const string DbUrl = "https://virtualpinballspreadsheet.github.io/vps-db/db/vpsdb.json";
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    static string CachePath => Path.Combine(AppSettings.DataDir, "vpsdb.json");

    /// <summary>
    /// Every game in the already-downloaded catalog, with no network access and no filtering
    /// (the VR lobby uses it for backglass art). Null when there's no cache yet or it can't be read.
    /// </summary>
    public static List<VpsGame>? LoadCached(string? path = null)
    {
        path ??= CachePath;
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<VpsGame>>(stream, Json);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<List<VpsGame>> LoadAsync(bool forceRefresh, CancellationToken ct = default)
    {
        var cacheFresh = File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < MaxAge;
        if (forceRefresh || !cacheFresh)
        {
            try
            {
                var bytes = await Http.Client.GetByteArrayAsync(DbUrl, ct);
                var tmp = CachePath + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, CachePath, overwrite: true);
            }
            catch when (File.Exists(CachePath))
            {
                // Offline or GitHub hiccup: use the stale cache rather than failing.
            }
        }

        await using var stream = File.OpenRead(CachePath);
        var games = await JsonSerializer.DeserializeAsync<List<VpsGame>>(stream, Json, ct) ?? [];
        return games
            .Where(g => g.TableFiles?.Any(IsVpx) == true)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsVpx(VpsTableFile t) => string.Equals(t.TableFormat, "VPX", StringComparison.OrdinalIgnoreCase);

    public static bool HasFeature(VpsFile f, string feature) =>
        f.Features?.Any(x => string.Equals(x, feature, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>VR-ready tables: tagged VR, or Hybrid (desktop/cab/VR in one file).</summary>
    public static bool IsVrReady(VpsTableFile t) => HasFeature(t, "VR") || HasFeature(t, "Hybrid");

    public static bool IsAdult(VpsTableFile t) => HasFeature(t, "Adult");
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VrTableManager/0.1 (+https://github.com)");
        return c;
    }

    public static async Task DownloadFileAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buffer = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total.Value);
        }
    }
}
