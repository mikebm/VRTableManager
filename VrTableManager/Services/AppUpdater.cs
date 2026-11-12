using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VrTableManager.Services;

/// <summary>A published release of this app on GitHub.</summary>
public sealed record AppRelease(
    string Tag,
    string Version,
    string Url,
    long Size,
    string? Sha256Url,
    string PageUrl,
    string Notes
);

/// <summary>
/// Keeps VR Table Manager itself up to date from its GitHub releases (the zip scripts\release.ps1 publishes).
/// Updating downloads the zip, checks its SHA-256, unpacks it, and hands over to a small script that waits
/// for this app to close, copies the new files over the install folder and starts the app again.
/// Settings, favourites and stats live in %APPDATA%, so they're untouched.
/// </summary>
public static class AppUpdater
{
    public const string Repo = "mikebm/VRTableManager";
    const string LatestApi = $"https://api.github.com/repos/{Repo}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";

    /// <summary>This build's version ("1.2.0", "1.3.0-beta.1"), without the commit hash the SDK appends.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v =
                Assembly
                    .GetEntryAssembly()
                    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                ?? "0.0.0";
            return v.Split('+')[0];
        }
    }

    static string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Running from a build of the repo (bin\…) rather than an unzipped release: updating would overwrite a build folder.</summary>
    public static bool IsDevBuild
    {
        get
        {
            for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            {
                if (File.Exists(Path.Combine(dir, "VR Table Manager.slnx")))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The newest non-prerelease release with a Windows zip, or null.</summary>
    public static async Task<AppRelease?> FindLatestAsync(CancellationToken ct)
    {
        try
        {
            return ParseLatest(await Http.Client.GetStringAsync(LatestApi, ct));
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        } // no releases yet
    }

    public static AppRelease? ParseLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rel = doc.RootElement;
        if (rel.GetProperty("draft").GetBoolean() || rel.GetProperty("prerelease").GetBoolean())
        {
            return null;
        }

        var tag = rel.GetProperty("tag_name").GetString() ?? "";
        string? zip = null,
            sha = null;
        long size = 0;
        foreach (var asset in rel.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name.StartsWith("VrTableManager-") && name.EndsWith("-win-x64.zip"))
            {
                zip = url;
                size = asset.GetProperty("size").GetInt64();
            }
            else if (name.StartsWith("VrTableManager-") && name.EndsWith("-win-x64.zip.sha256"))
            {
                sha = url;
            }
        }
        if (zip == null)
        {
            return null;
        }

        return new AppRelease(
            tag,
            tag.TrimStart('v', 'V'),
            zip,
            size,
            sha,
            rel.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? ReleasesPage
                : ReleasesPage,
            rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : ""
        );
    }

    /// <summary>True when <paramref name="a"/> is a later version than <paramref name="b"/> ("1.2.0" &gt; "1.2.0-beta.1" &gt; "1.1.9").</summary>
    public static bool IsNewer(string a, string b)
    {
        static (Version v, string pre) Parse(string s)
        {
            var parts = s.Trim().TrimStart('v', 'V').Split('-', 2);
            return (
                Version.TryParse(parts[0], out var v) ? v : new Version(0, 0),
                parts.Length > 1 ? parts[1] : ""
            );
        }
        var (va, pa) = Parse(a);
        var (vb, pb) = Parse(b);
        if (va != vb)
        {
            return va > vb;
        }

        if (pa == pb)
        {
            return false;
        }

        if (pa == "")
        {
            return true; // a release is newer than its prereleases
        }

        if (pb == "")
        {
            return false;
        }

        return string.CompareOrdinal(pa, pb) > 0;
    }

    /// <summary>Whether the install folder can be written (not under Program Files without admin rights).</summary>
    public static bool CanWriteInstallDir()
    {
        try
        {
            var probe = Path.Combine(InstallDir, ".vrtm-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads and unpacks the release, then starts the script that swaps it in once this process exits.
    /// The caller closes the app straight after.
    /// </summary>
    public static async Task PrepareAsync(
        AppRelease rel,
        IProgress<double> progress,
        Action<string> status,
        CancellationToken ct
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Self-update is Windows-only.");
        }

        var work = Path.Combine(Path.GetTempPath(), "vrtm-update-" + rel.Version);
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, "update.zip");

        status($"Downloading VR Table Manager {rel.Version}…");
        await Http.DownloadFileAsync(rel.Url, zip, progress, ct);

        if (rel.Sha256Url != null)
        {
            status("Checking the download…");
            var expected = (await Http.Client.GetStringAsync(rel.Sha256Url, ct))
                .Split(' ', '\t', '\n')[0]
                .Trim()
                .ToLowerInvariant();
            await using var f = File.OpenRead(zip);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
            if (actual != expected)
            {
                throw new InvalidDataException(
                    "The download doesn't match its published SHA-256. Try again later."
                );
            }
        }

        status("Unpacking…");
        var staging = Path.Combine(work, "files");
        await Task.Run(() => TableInstaller.Extract(zip, staging), ct);
        // The zip holds one VrTableManager\ folder.
        var newFiles = Directory.Exists(Path.Combine(staging, "VrTableManager"))
            ? Path.Combine(staging, "VrTableManager")
            : staging;
        if (!File.Exists(Path.Combine(newFiles, "VrTableManager.exe")))
        {
            throw new InvalidDataException("The release zip doesn't contain VrTableManager.exe.");
        }

        var exe = Environment.ProcessPath ?? Path.Combine(InstallDir, "VrTableManager.exe");
        var script = Path.Combine(work, "apply-update.ps1");
        await File.WriteAllTextAsync(
            script,
            $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {{Environment.ProcessId}} -Timeout 120
            # Still running: copying now would replace some files and not the ones in use, a mix of two versions.
            if (Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue) {
                (New-Object -ComObject WScript.Shell).Popup("VR Table Manager didn't close, so the update to {{rel.Version}} wasn't installed. Nothing was changed. Close the app and update again from Setup > Updates.", 0, "VR Table Manager", 48) | Out-Null
                Remove-Item -LiteralPath '{{Esc(work)}}' -Recurse -Force
                exit
            }
            Start-Sleep -Milliseconds 500
            # /E: everything, keeping files the new version doesn't have (the lobby's imported assets etc.).
            robocopy '{{Esc(newFiles)}}' '{{Esc(
                InstallDir
            )}}' /E /R:30 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            $ok = $LASTEXITCODE -lt 8
            if ($ok) { Start-Process -FilePath '{{Esc(exe)}}' -ArgumentList '--updated' }
            else { Start-Process -FilePath '{{Esc(exe)}}' -ArgumentList '--update-failed' }
            Start-Sleep -Seconds 2
            if ($ok) { Remove-Item -LiteralPath '{{Esc(work)}}' -Recurse -Force }
            """,
            ct
        );

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            var a in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-WindowStyle",
                "Hidden",
                "-File",
                script,
            }
        )
        {
            psi.ArgumentList.Add(a);
        }

        Process.Start(psi);
        status("Restarting to finish the update…");
    }

    static string Esc(string path) => path.Replace("'", "''");
}
