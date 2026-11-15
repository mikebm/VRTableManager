using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VrTableManager.Services;

/// <summary>
/// GET for GitHub's API (VPX, UEVR and app releases, VPX nightlies). GitHub allows 60 unauthenticated calls an hour
/// per address, and a check, an install and a start each needed several, so answers are kept and reused:
/// <list type="bullet">
/// <item>for <see cref="FreshFor"/>, in memory and on disk (a restart reuses them too);</item>
/// <item>the same URL asked for twice at once (the startup checks) is fetched once;</item>
/// <item>when GitHub says the hour's calls are used up (or can't be reached, or fails), the last answer is used,
/// however old; with none, a used-up limit's error says when it resets.</item>
/// </list>
/// </summary>
public static class GitHubApi
{
    /// <summary>How long an answer is reused without asking GitHub again. Releases don't change by the minute.</summary>
    public static TimeSpan FreshFor { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Where answers are kept (tests point it elsewhere).</summary>
    public static string CacheDir { get; set; } = Path.Combine(AppSettings.DataDir, "github-cache");

    static readonly ConcurrentDictionary<string, Lazy<Task<string>>> InFlight = new();

    public static Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        if (Read(url) is { } kept && DateTime.UtcNow - kept.At < FreshFor)
        {
            return Task.FromResult(kept.Body);
        }

        // One fetch per URL at a time: a second caller waits for the first's answer. (Its token isn't the
        // first's: a cancelled caller stops waiting, the fetch itself carries on for the others.)
        var lazy = InFlight.GetOrAdd(url, u => new Lazy<Task<string>>(() => FetchAsync(u)));
        return lazy.Value.WaitAsync(ct);
    }

    static async Task<string> FetchAsync(string url)
    {
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await Http.Client.GetAsync(url);
            }
            catch (HttpRequestException) when (Read(url) is { } offline)
            {
                return offline.Body; // no connection: the last answer beats none
            }
            using var resp = response;
            if (IsRateLimited(resp) || (int)resp.StatusCode >= 500)
            {
                if (Read(url) is { } stale)
                {
                    return stale.Body;
                }
                if (IsRateLimited(resp))
                {
                    throw new HttpRequestException(LimitMessage(resp), null, resp.StatusCode);
                }
            }
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            Write(url, body);
            return body;
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }

    static bool IsRateLimited(HttpResponseMessage resp) =>
        resp.StatusCode == HttpStatusCode.TooManyRequests
        || (
            resp.StatusCode == HttpStatusCode.Forbidden
            && resp.Headers.TryGetValues("X-RateLimit-Remaining", out var left)
            && left.FirstOrDefault() == "0"
        );

    /// <summary>"GitHub's limit of 60 lookups an hour is used up; it resets at 21:40."</summary>
    static string LimitMessage(HttpResponseMessage resp)
    {
        var when =
            resp.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var secs)
                ? $"; it resets at {DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime():t}"
                : "; try again in an hour";
        return $"GitHub's limit of 60 lookups an hour from this connection is used up{when}.";
    }

    static string PathFor(string url) =>
        Path.Combine(
            CacheDir,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".json"
        );

    /// <summary>Answers this session, so an unwritable cache folder doesn't mean asking GitHub every time.</summary>
    static readonly ConcurrentDictionary<string, (string Body, DateTime At)> Memory = new();

    static (string Body, DateTime At)? Read(string url)
    {
        if (Memory.TryGetValue(url, out var mem))
        {
            return mem;
        }
        try
        {
            var path = PathFor(url);
            return File.Exists(path)
                ? (File.ReadAllText(path), File.GetLastWriteTimeUtc(path))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void Write(string url, string body)
    {
        Memory[url] = (body, DateTime.UtcNow);
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = PathFor(url);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, body);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not kept: the next check asks GitHub again.
        }
    }

    /// <summary>Forgets every kept answer (tests).</summary>
    public static void Clear()
    {
        Memory.Clear();
        try
        {
            if (Directory.Exists(CacheDir))
            {
                Directory.Delete(CacheDir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
