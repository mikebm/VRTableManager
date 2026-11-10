using VrTableManager.Services;

/// <summary>The .NET 10 Runtime check before the VR lobby starts (LobbyRunner.HasLobbyRuntime).</summary>
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

    public static IEnumerable<(bool, string)> Run(string root)
    {
        var none = Path.Combine(root, "dotnet-none");
        Directory.CreateDirectory(none);
        var old = Runtime(Path.Combine(root, "dotnet-6"), "6.0.36");
        var next = Runtime(Path.Combine(root, "dotnet-11"), "11.0.1");
        var ten = Runtime(Path.Combine(root, "dotnet-10"), "10.0.13");
        var empty10 = Path.Combine(root, "dotnet-10-empty");
        Directory.CreateDirectory(
            Path.Combine(empty10, "shared", "Microsoft.NETCore.App", "10.0.2")
        );
        Directory.CreateDirectory(Path.Combine(empty10, "host", "fxr"));

        yield return (
            !LobbyRunner.HasLobbyRuntime([none, Path.Combine(root, "missing")]),
            "lobby runtime: no .NET installed is caught"
        );
        yield return (
            !LobbyRunner.HasLobbyRuntime([old, next]),
            "lobby runtime: .NET 6 and 11 don't count (the lobby rolls forward within 10 only)"
        );
        yield return (
            !LobbyRunner.HasLobbyRuntime([empty10]),
            "lobby runtime: a leftover empty 10.x folder doesn't count"
        );
        yield return (
            LobbyRunner.HasLobbyRuntime([none, old, ten]),
            "lobby runtime: .NET 10 in any of the roots is found"
        );
    }
}
