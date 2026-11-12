using System.Diagnostics;
using System.Text;

namespace VrTableManager.Services;

/// <summary>
/// One-click start for the VR lobby (VrLobby, a Godot 4.7 .NET project in this repo).
/// First time: downloads the official Godot .NET runtime from GitHub into the app's data folder.
/// Every time: rebuilds the lobby's C# if its code changed, makes sure Godot has imported the
/// project's assets, then runs it. The lobby launches tables back through this app (--play).
/// </summary>
public static class LobbyRunner
{
    public const string GodotVersion = "4.7.2-stable";
    const string GodotZip = $"Godot_v{GodotVersion}_mono_win64.zip";
    const string GodotUrl =
        $"https://github.com/godotengine/godot-builds/releases/download/{GodotVersion}/{GodotZip}";

    // Local, not Roaming: a 250 MB program shouldn't travel with a roaming Windows profile.
    static string GodotDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrTableManager",
            "godot"
        );

    /// <summary>The VrLobby project folder: next to this app, or in a parent folder (the repo layout).</summary>
    public static string? FindLobbyDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            foreach (var candidate in new[] { Path.Combine(dir, "VrLobby"), dir })
            {
                if (
                    File.Exists(Path.Combine(candidate, "project.godot"))
                    && File.Exists(Path.Combine(candidate, "VrLobby.csproj"))
                )
                {
                    return candidate;
                }
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>The Godot .NET editor exe the app downloaded (not the _console one), if present.</summary>
    public static string? FindGodot()
    {
        if (!Directory.Exists(GodotDir))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(GodotDir, "Godot_v*_mono_win64.exe", SearchOption.AllDirectories)
            .FirstOrDefault(f =>
                !f.EndsWith("_console.exe", StringComparison.OrdinalIgnoreCase)
                // An unpack cut short before this change could leave the exe without its .NET files.
                && Directory.Exists(Path.Combine(Path.GetDirectoryName(f)!, "GodotSharp"))
            );
    }

    public static async Task<string> EnsureGodotAsync(
        IProgress<double> progress,
        Action<string> status,
        CancellationToken ct
    )
    {
        if (FindGodot() is { } have)
        {
            return have;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The one-click lobby start is Windows-only for now."
            );
        }

        var tmp = Path.Combine(Path.GetTempPath(), GodotZip);
        status($"Downloading Godot {GodotVersion} (.NET), 117 MB, one time…");
        await Http.DownloadFileAsync(GodotUrl, tmp, progress, ct);
        status("Unpacking Godot…");
        // Unpacked beside it and moved in whole: an unpack cut off halfway would otherwise leave the exe without its
        // .NET files, and FindGodot would take that for an install from then on.
        var staging = GodotDir + ".part";
        await Task.Run(
            () =>
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }

                TableInstaller.Extract(tmp, staging);
                if (Directory.Exists(GodotDir))
                {
                    Directory.Delete(GodotDir, recursive: true); // a broken one from before
                }

                // A virus scanner often has the fresh exe open for a moment, which blocks renaming the folder.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        Directory.Move(staging, GodotDir);
                        break;
                    }
                    catch (Exception e)
                        when (attempt < 10 && e is IOException or UnauthorizedAccessException)
                    {
                        Thread.Sleep(500);
                    }
                }
            },
            ct
        );
        try
        {
            File.Delete(tmp);
        }
        catch { }
        return FindGodot()
            ?? throw new FileNotFoundException("Godot's exe wasn't in the download.");
    }

    static string LobbyDll(string lobbyDir) =>
        Path.Combine(lobbyDir, ".godot", "mono", "temp", "bin", "Debug", "VrLobby.dll");

    /// <summary>The shared library's source, present in a checkout of the repo but not in a release.</summary>
    static string CoreDir(string lobbyDir) =>
        Path.Combine(Path.GetDirectoryName(lobbyDir)!, "VrTableManager.Core");

    /// <summary>
    /// True when the lobby's compiled code is missing or older than any of its (or the shared library's) C# files.
    /// A release ships the lobby prebuilt without the shared library's source, so it can't be rebuilt there:
    /// its code is used as is. (Godot's first-run import writes new .cs.uid files, which would otherwise look
    /// like changed code.)
    /// </summary>
    public static bool NeedsBuild(string lobbyDir)
    {
        var dll = LobbyDll(lobbyDir);
        if (!File.Exists(dll))
        {
            return true;
        }

        var core = CoreDir(lobbyDir);
        if (!Directory.Exists(core))
        {
            return false;
        }

        var built = File.GetLastWriteTimeUtc(dll);
        return new[] { lobbyDir, core }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs*", SearchOption.AllDirectories))
            .Where(f =>
                f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            )
            .Where(f =>
                !f.Contains(Path.DirectorySeparatorChar + ".godot" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
            )
            .Any(f => File.GetLastWriteTimeUtc(f) > built);
    }

    public static async Task BuildAsync(
        string lobbyDir,
        Action<string> status,
        CancellationToken ct
    )
    {
        if (!Directory.Exists(CoreDir(lobbyDir)))
        {
            throw new InvalidOperationException(
                "The lobby's compiled code is missing from the VrLobby folder. Unzip the release again, keeping VrLobby next to VrTableManager.exe."
            );
        }

        status("Building the lobby…");
        var (code, output) = await RunAsync(
            DotnetExe(),
            ["build", Path.Combine(lobbyDir, "VrLobby.csproj"), "-nologo", "-v", "q"],
            lobbyDir,
            ct
        );
        if (code != 0)
        {
            var errors = output
                .Split('\n')
                .Where(l => l.Contains(" error "))
                .Take(3)
                .Select(l => l.Trim());
            throw new InvalidOperationException(
                "The lobby didn't build: "
                    + string.Join(" | ", errors.DefaultIfEmpty("dotnet build failed"))
            );
        }
    }

    /// <summary>Godot only reads imported assets when running a project; a fresh checkout has none (.godot isn't in git).</summary>
    public static async Task ImportIfNeededAsync(
        string godot,
        string lobbyDir,
        string? dotnetRoot,
        Action<string> status,
        CancellationToken ct
    )
    {
        if (Directory.Exists(Path.Combine(lobbyDir, ".godot", "imported")))
        {
            return;
        }

        status("Preparing the lobby's assets (first run)…");
        await RunAsync(
            ConsoleExe(godot),
            ["--headless", "--path", lobbyDir, "--import"],
            lobbyDir,
            ct,
            dotnetRoot
        );
    }

    /// <summary>Starts the lobby, on the app's bundled .NET when <paramref name="dotnetRoot"/> is set (<see cref="LobbyDotnet"/>).</summary>
    public static Process Start(string godot, string lobbyDir, string? dotnetRoot)
    {
        var psi = new ProcessStartInfo(godot)
        {
            WorkingDirectory = lobbyDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(lobbyDir);
        if (dotnetRoot != null)
        {
            LobbyDotnet.Apply(psi, dotnetRoot);
        }
        return Process.Start(psi) ?? throw new InvalidOperationException("Godot didn't start.");
    }

    /// <summary>
    /// Runs the lobby without a window to read every table ahead of time (VrLobby's PrepareTables), handing each
    /// line it prints to <paramref name="line"/> (off the UI thread). Below normal priority, so the PC stays usable
    /// meanwhile. Cancelling stops it; what it finished is kept. Returns its exit code.
    /// </summary>
    public static async Task<int> PrepareTablesAsync(
        string godot,
        string lobbyDir,
        string? dotnetRoot,
        Action<string> line,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo(ConsoleExe(godot))
        {
            WorkingDirectory = lobbyDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, // how PrepareTables writes its lines
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "--headless", "--path", lobbyDir, "--", "--prepare-tables" })
        {
            psi.ArgumentList.Add(a);
        }
        if (dotnetRoot != null)
        {
            LobbyDotnet.Apply(psi, dotnetRoot);
        }

        using var p =
            Process.Start(psi) ?? throw new InvalidOperationException("Godot didn't start.");
        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        { /* already gone, or not allowed: it runs at normal priority */
        }
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                line(e.Data);
            }
        };
        p.ErrorDataReceived += (_, _) => { }; // read, so a full error pipe can't stall it
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true); // the console exe runs Godot itself as its child
            }
            catch
            { /* already gone */
            }
            throw;
        }
        p.WaitForExit(); // the last lines' events
        return p.ExitCode;
    }

    static string ConsoleExe(string godot)
    {
        var console = Path.Combine(
            Path.GetDirectoryName(godot)!,
            Path.GetFileNameWithoutExtension(godot) + "_console.exe"
        );
        return File.Exists(console) ? console : godot;
    }

    static string DotnetExe()
    {
        var pf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe"
        );
        return File.Exists(pf) ? pf : "dotnet";
    }

    static async Task<(int code, string output)> RunAsync(
        string exe,
        string[] args,
        string cwd,
        CancellationToken ct,
        string? dotnetRoot = null
    )
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        if (dotnetRoot != null)
        {
            LobbyDotnet.Apply(psi, dotnetRoot);
        }

        using var p =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"Couldn't start {Path.GetFileName(exe)}.");
        var sb = new StringBuilder();
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled: stop the build or import too, rather than leave it running headless.
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            { /* already gone */
            }
            throw;
        }
        sb.Append(await o).Append(await e);
        return (p.ExitCode, sb.ToString());
    }
}
