using System.Diagnostics;
using Godot;
using VrTableManager.Lobby;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// Hands a table to VR Table Manager's headless play mode (<c>VrTableManager.exe --play</c>), which
/// launches it with the same engine code as the manager's Play button, waits for it to exit and starts
/// this lobby again. The lobby then quits, ending its OpenXR session so the table gets the headset.
/// </summary>
public static class LobbyLauncher
{
    public static string? FindManagerExe(AppSettings s)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(s.ManagerExe)) candidates.Add(s.ManagerExe);
        // Exported lobby shipped next to the manager.
        candidates.Add(Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? "", "VrTableManager.exe"));
        // Running from source: the manager's build output next to this project.
        var repo = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        foreach (var config in new[] { "Release", "Debug" })
            candidates.Add(Path.Combine(repo, "VrTableManager", "bin", config, "net10.0", "VrTableManager.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Checks the engine is set up before the lobby quits, so problems show in VR instead of silently.</summary>
    public static string? CheckReady(LobbyTable t, AppSettings s) => t.Engine switch
    {
        TableManifest.Vpx when VpxSetup.FindExe(s) == null => "Visual Pinball X isn't installed. Install it from VR Table Manager's Setup tab.",
        TableManifest.FuturePinball when !File.Exists(FpSetup.LoaderPath(s)) => "BAM's FPLoader.exe wasn't found. Check the Future Pinball folder in VR Table Manager.",
        TableManifest.PinballFx or TableManifest.PinballM when ZenGames.For(t.Engine) is { } g && PfxSetup.Detect(g) == null
            => $"{g.Title} isn't installed (Steam or Epic).",
        TableManifest.PinballFx or TableManifest.PinballM when !File.Exists(PfxSetup.InjectorPath(s))
            => "UEVR wasn't found. Install it from VR Table Manager's Setup tab.",
        _ => null,
    };

    /// <summary>How to start this lobby again: an exported build, or the Godot editor binary with --path.</summary>
    public static (string Exe, List<string> Args) ReturnCommand(string selectId, string? settingsPath, string? statePath)
    {
        var args = new List<string>();
        if (!OS.HasFeature("template")) args.AddRange(["--path", ProjectSettings.GlobalizePath("res://")]);
        args.Add("--");
        args.Add("--select=" + selectId);
        if (!string.IsNullOrEmpty(settingsPath)) args.Add("--settings=" + settingsPath);
        if (!string.IsNullOrEmpty(statePath)) args.Add("--state=" + statePath);
        return (OS.GetExecutablePath(), args);
    }

    /// <summary>Returns null when the manager was started, otherwise an error to show.</summary>
    public static string? Launch(LobbyTable t, AppSettings s, string? settingsPath, string? statePath)
    {
        if (CheckReady(t, s) is { } problem) return problem;
        var manager = FindManagerExe(s);
        if (manager == null) return "VR Table Manager wasn't found. Open it once so the lobby can find it.";
        var (retExe, retArgs) = ReturnCommand(t.Id, settingsPath, statePath);
        var psi = new ProcessStartInfo(manager) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(manager)! };
        foreach (var a in PlaySession.BuildArgs(t.Id, vr: true, settingsPath, retExe, retArgs)) psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi);
            return null;
        }
        catch (Exception e)
        {
            return "Couldn't start VR Table Manager: " + e.Message;
        }
    }
}
