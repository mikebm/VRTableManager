using System.Diagnostics;

namespace VrTableManager.Services;

/// <summary>
/// The .NET the VR lobby runs on when it runs as a project in the Godot editor. A release's exported lobby brings
/// its own .NET, and releases no longer ship <see cref="DepsFile"/>, so <see cref="Prepare()"/> returns null there.
/// The lobby is a Godot .NET project, and Godot starts .NET from a .NET install
/// folder (DOTNET_ROOT, or the one Windows has). The manager is self-contained, so a player who never installed
/// .NET has one only inside the manager's own folder, which is flat, not laid out like an install.
/// <para>
/// <see cref="Prepare()"/> makes a small install-shaped folder that reuses the manager's files instead of a second
/// copy: <c>host\fxr\&lt;ver&gt;\hostfxr.dll</c> (a copy: next to the app's hostpolicy.dll, hostfxr would take the
/// folder for a self-contained app and not look for a framework) and <c>shared\Microsoft.NETCore.App\&lt;ver&gt;</c>,
/// a directory junction to the manager's folder. The release ships the framework's
/// <see cref="DepsFile"/> there, which a self-contained publish leaves out and .NET needs to accept the folder.
/// </para>
/// <para>
/// Godot gets the folder through <see cref="Apply"/>, and only Godot: <c>--play</c> mode takes it back out of its
/// own environment before launching a table (<see cref="ClearInherited"/>), so a table's own .NET helpers see the
/// player's .NET as usual.
/// </para>
/// </summary>
public static class LobbyDotnet
{
    public const string DepsFile = "Microsoft.NETCore.App.deps.json";
    public const string RuntimeUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

    /// <summary>The lobby's code targets .NET 10; Godot's editor binary rolls forward to the newest major installed.</summary>
    public const int MinMajor = 10;

    static readonly string[] RootVars = ["DOTNET_ROOT", "DOTNET_ROOT_X64"];

    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrTableManager",
            "dotnet"
        );

    /// <summary>The bundled-runtime folder for this app, or null when it can't be made (see the other overload).</summary>
    public static string? Prepare() =>
        Prepare(AppContext.BaseDirectory, DefaultRoot, Environment.Version.ToString(), out _);

    /// <summary>
    /// Makes or refreshes <paramref name="root"/> for the self-contained runtime in <paramref name="appDir"/>
    /// (version <paramref name="version"/>) and returns it, or null with <paramref name="why"/> when the app has no
    /// bundled runtime to share (running from source, or a release without the deps file) or the junction can't
    /// be made (a drive without junctions). Folders left by other versions or app locations are removed; only
    /// links are ever deleted under <c>shared</c>, never what they point to.
    /// <para>
    /// Never throws: any failure (cmd.exe not starting, a bad path) means the installed .NET is used instead, and
    /// play mode still brings the lobby back after a table. It starts a process and copies a file, so call it off
    /// the UI thread.
    /// </para>
    /// </summary>
    public static string? Prepare(string appDir, string root, string version, out string why)
    {
        try
        {
            return PrepareCore(appDir, root, version, out why);
        }
        catch (Exception e)
        {
            why = e.Message;
            return null;
        }
    }

    static string? PrepareCore(string appDir, string root, string version, out string why)
    {
        // Full paths with backslashes: mklink reads a forward slash as a switch.
        appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (
            var f in new[]
            {
                "hostfxr.dll",
                "hostpolicy.dll",
                "System.Private.CoreLib.dll",
                DepsFile,
            }
        )
        {
            if (!File.Exists(Path.Combine(appDir, f)))
            {
                why = $"no {f} in {appDir}";
                return null;
            }
        }

        try
        {
            var fxrRoot = Path.Combine(root, "host", "fxr");
            var fxr = Path.Combine(fxrRoot, version);
            Directory.CreateDirectory(fxr);
            var hostfxr = Path.Combine(fxr, "hostfxr.dll");
            var src = new FileInfo(Path.Combine(appDir, "hostfxr.dll"));
            var have = new FileInfo(hostfxr);
            if (
                !have.Exists
                || have.Length != src.Length
                || have.LastWriteTimeUtc != src.LastWriteTimeUtc
            )
            {
                src.CopyTo(hostfxr, overwrite: true);
            }

            foreach (var other in Directory.EnumerateDirectories(fxrRoot))
            {
                if (!SameName(other, version))
                {
                    TryDeleteFxr(other);
                }
            }

            var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            Directory.CreateDirectory(shared);
            foreach (var other in Directory.EnumerateDirectories(shared))
            {
                if (!SameName(other, version) && IsLink(other))
                {
                    Directory.Delete(other); // the link only
                }
            }

            var link = Path.Combine(shared, version);
            if (Directory.Exists(link) && !SamePath(LinkTarget(link), appDir))
            {
                if (!IsLink(link))
                {
                    why = $"{link} is a real folder, not a link";
                    return null;
                }
                Directory.Delete(link); // the link only: it points to an older copy of the app
            }
            if (!Directory.Exists(link) && !MakeJunction(link, appDir, out why))
            {
                return null;
            }

            if (!HasLobbyRuntime([root]))
            {
                why = $"{root} isn't usable after setting it up";
                return null;
            }
            why = "";
            return root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = e.Message;
            return null;
        }
    }

    /// <summary>
    /// True for an exported lobby (its .pck beside it, as Godot exports it), which brings its own .NET and needs no
    /// root. An install updated from an older release still has <see cref="DepsFile"/>, so this is checked first.
    /// </summary>
    public static bool IsExported(string exe) => File.Exists(Path.ChangeExtension(exe, ".pck"));

    /// <summary>Points a process's .NET at <paramref name="root"/> (Godot reads DOTNET_ROOT_X64 first, then DOTNET_ROOT).</summary>
    public static void Apply(ProcessStartInfo psi, string root)
    {
        foreach (var v in RootVars)
        {
            psi.Environment[v] = root;
        }
    }

    /// <summary>
    /// Removes the bundled folder from this process's environment, where a lobby passed it on, so nothing this
    /// process starts inherits it. A DOTNET_ROOT the player set themselves is kept.
    /// </summary>
    public static void ClearInherited(string? root = null)
    {
        root ??= DefaultRoot;
        foreach (var v in RootVars)
        {
            if (SamePath(Environment.GetEnvironmentVariable(v), root))
            {
                Environment.SetEnvironmentVariable(v, null);
            }
        }
    }

    /// <summary>The places Godot looks for an installed .NET (DOTNET_ROOT, dotnet.exe on PATH, the default install folder).</summary>
    public static IEnumerable<string> DotnetRoots()
    {
        foreach (var v in RootVars)
        {
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } env)
            {
                yield return env;
            }
        }

        foreach (
            var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (
                File.Exists(
                    Path.Combine(dir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
                )
            )
            {
                yield return dir;
            }
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet"
        );
    }

    /// <summary>True when one of <paramref name="roots"/> has a .NET <see cref="MinMajor"/> or newer runtime and its host.</summary>
    public static bool HasLobbyRuntime(IEnumerable<string> roots) =>
        roots.Any(root =>
        {
            var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            return Directory.Exists(Path.Combine(root, "host", "fxr"))
                && Directory.Exists(shared)
                && Directory
                    .EnumerateDirectories(shared)
                    .Any(d =>
                        Version.TryParse(Path.GetFileName(d).Split('-')[0], out var ver)
                        && ver.Major >= MinMajor
                        && File.Exists(Path.Combine(d, "System.Private.CoreLib.dll"))
                    );
        });

    static bool SameName(string dir, string name) =>
        string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase);

    static bool SamePath(string? a, string? b) =>
        a != null
        && b != null
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase
        );

    static bool IsLink(string dir) =>
        new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);

    static string? LinkTarget(string dir)
    {
        var target = new DirectoryInfo(dir).LinkTarget;
        if (target == null)
        {
            return null;
        }
        // Junctions can report their target with the \??\ prefix.
        if (target.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            target = target[4..];
        }
        return Path.GetFullPath(target, Path.GetDirectoryName(dir)!);
    }

    /// <summary>An old version's hostfxr copy: removed file by file, never recursively.</summary>
    static void TryDeleteFxr(string dir)
    {
        try
        {
            if (IsLink(dir))
            {
                Directory.Delete(dir);
                return;
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                File.Delete(f);
            }
            Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by a lobby still running on it: the next start cleans it up.
        }
    }

    /// <summary>A directory junction (no admin rights or Developer Mode needed, unlike a symbolic link).</summary>
    static bool MakeJunction(string link, string target, out string why)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            why = "";
            return true;
        }

        // Both paths always in quotes, written out here: ArgumentList quotes only paths with a space, and cmd reads an
        // unquoted "&" or "^" (allowed in user and folder names, as in C:\Users\Tom&Jerry) as its own. Neither path
        // can hold a quote, and cmd keeps quotes after /c when the line doesn't start with one.
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(psi);
        if (p == null)
        {
            why = "cmd.exe didn't start";
            return false;
        }
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        why = p.ExitCode == 0 && Directory.Exists(link) ? "" : $"mklink /J failed: {err.Trim()}";
        return why == "";
    }
}
