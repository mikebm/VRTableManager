using VrTableManager.Services;

/// <summary>
/// The .NET the VR lobby runs on (LobbyDotnet): the app's own runtime shared through a junction, and the check
/// for an installed one when that can't be used.
/// </summary>
static class LobbyRuntimeChecks
{
    static string Runtime(string root, string version)
    {
        var dir = Path.Combine(root, "shared", "Microsoft.NETCore.App", version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "System.Private.CoreLib.dll"), "");
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", version));
        return root;
    }

    /// <summary>A flat self-contained app folder, with or without the framework deps file the release adds.</summary>
    static string App(string dir, bool deps = true)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll" })
        {
            File.WriteAllText(Path.Combine(dir, f), f);
        }
        if (deps)
        {
            File.WriteAllText(Path.Combine(dir, LobbyDotnet.DepsFile), "{}");
        }
        return dir;
    }

    static string? Target(string link) =>
        new DirectoryInfo(link).LinkTarget is { } t
            ? Path.TrimEndingDirectorySeparator(t.StartsWith(@"\??\") ? t[4..] : t)
            : null;

    public static IEnumerable<(bool, string)> Run(string root)
    {
        // ---- An installed .NET ----
        var none = Path.Combine(root, "dotnet-none");
        Directory.CreateDirectory(none);
        var old = Runtime(Path.Combine(root, "dotnet-9"), "9.0.6");
        var next = Runtime(Path.Combine(root, "dotnet-11"), "11.0.1");
        var ten = Runtime(Path.Combine(root, "dotnet-10"), "10.0.13");
        var empty10 = Path.Combine(root, "dotnet-10-empty");
        Directory.CreateDirectory(
            Path.Combine(empty10, "shared", "Microsoft.NETCore.App", "10.0.2")
        );
        Directory.CreateDirectory(Path.Combine(empty10, "host", "fxr"));

        yield return (
            !LobbyDotnet.HasLobbyRuntime([none, Path.Combine(root, "missing")]),
            "lobby runtime: no .NET installed is caught"
        );
        yield return (
            !LobbyDotnet.HasLobbyRuntime([old]),
            "lobby runtime: .NET 9 is too old for the lobby's code"
        );
        yield return (
            LobbyDotnet.HasLobbyRuntime([next]),
            "lobby runtime: .NET 11 runs it (Godot rolls forward to the newest major)"
        );
        yield return (
            !LobbyDotnet.HasLobbyRuntime([empty10]),
            "lobby runtime: a leftover empty 10.x folder doesn't count"
        );
        yield return (
            LobbyDotnet.HasLobbyRuntime([none, old, ten]),
            "lobby runtime: .NET 10 in any of the roots is found"
        );

        // ---- The app's own .NET ----
        var dotnet = Path.Combine(root, "lobby-dotnet");
        var noDeps = App(Path.Combine(root, "app no deps"), deps: false);
        yield return (
            LobbyDotnet.Prepare(noDeps, dotnet, "10.0.13", out var why) == null
                && why.Contains(LobbyDotnet.DepsFile),
            "bundled .NET: not used without the framework deps file (running from source)"
        );

        var app1 = App(Path.Combine(root, "VR Table Manager 1"));
        var got = LobbyDotnet.Prepare(app1, dotnet, "10.0.13", out why);
        var link = Path.Combine(dotnet, "shared", "Microsoft.NETCore.App", "10.0.13");
        var fxr = Path.Combine(dotnet, "host", "fxr", "10.0.13", "hostfxr.dll");
        yield return (
            got == dotnet
                && File.Exists(fxr)
                && string.Equals(Target(link), app1, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(link, LobbyDotnet.DepsFile))
                && LobbyDotnet.HasLobbyRuntime([dotnet]),
            $"bundled .NET: hostfxr copied, framework is a junction to the app folder (a path with spaces) {why}"
        );
        yield return (
            !File.Exists(Path.Combine(dotnet, "host", "fxr", "10.0.13", "hostpolicy.dll")),
            "bundled .NET: hostfxr alone, without the app's hostpolicy next to it"
        );
        yield return (
            LobbyDotnet.Prepare(app1, dotnet, "10.0.13", out _) == dotnet
                && string.Equals(Target(link), app1, StringComparison.OrdinalIgnoreCase),
            "bundled .NET: a second start reuses the folder"
        );

        // The app updated and moved: new version, new folder. The old link goes, the old app folder stays.
        var app2 = App(Path.Combine(root, "VR Table Manager 2"));
        got = LobbyDotnet.Prepare(app2, dotnet, "10.0.14", out why);
        var shared = Directory.GetDirectories(
            Path.Combine(dotnet, "shared", "Microsoft.NETCore.App")
        );
        var fxrs = Directory.GetDirectories(Path.Combine(dotnet, "host", "fxr"));
        yield return (
            got == dotnet
                && shared.Length == 1
                && Path.GetFileName(shared[0]) == "10.0.14"
                && fxrs.Length == 1
                && Path.GetFileName(fxrs[0]) == "10.0.14"
                && File.Exists(Path.Combine(app1, "hostpolicy.dll"))
                && File.Exists(Path.Combine(app1, LobbyDotnet.DepsFile)),
            $"bundled .NET: a newer version replaces the old links, and the old app folder is untouched {why}"
        );

        // Same version, app moved to another folder: the link is pointed at the new one.
        var app3 = App(Path.Combine(root, "moved"));
        got = LobbyDotnet.Prepare(app3, dotnet, "10.0.14", out _);
        yield return (
            got == dotnet
                && string.Equals(
                    Target(Path.Combine(dotnet, "shared", "Microsoft.NETCore.App", "10.0.14")),
                    app3,
                    StringComparison.OrdinalIgnoreCase
                )
                && File.Exists(Path.Combine(app2, "hostpolicy.dll")),
            "bundled .NET: an app moved to another folder gets its link re-pointed"
        );

        // Forward slashes: mklink would read "/Users" as a switch.
        var slashed = Path.Combine(root, "slashed-dotnet").Replace('\\', '/');
        yield return (
            LobbyDotnet.Prepare(app3.Replace('\\', '/'), slashed, "10.0.14", out why) != null,
            $"bundled .NET: paths with forward slashes work {why}"
        );

        // DOTNET_ROOT handed on by a lobby is cleared for the table; one the player set is kept.
        Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnet);
        Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", @"C:\Their\dotnet");
        LobbyDotnet.ClearInherited(dotnet);
        var cleared =
            Environment.GetEnvironmentVariable("DOTNET_ROOT") == null
            && Environment.GetEnvironmentVariable("DOTNET_ROOT_X64") == @"C:\Their\dotnet";
        Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", null);
        yield return (
            cleared,
            "bundled .NET: play mode clears it for the table, but keeps the player's own DOTNET_ROOT"
        );

        var psi = new System.Diagnostics.ProcessStartInfo("x");
        LobbyDotnet.Apply(psi, dotnet);
        yield return (
            psi.Environment["DOTNET_ROOT"] == dotnet
                && psi.Environment["DOTNET_ROOT_X64"] == dotnet,
            "bundled .NET: Godot gets DOTNET_ROOT and DOTNET_ROOT_X64"
        );
    }
}
