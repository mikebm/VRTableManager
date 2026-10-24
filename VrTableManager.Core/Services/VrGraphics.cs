using System.Diagnostics;
using System.Globalization;

namespace VrTableManager.Services;

/// <summary>
/// The VPX graphics settings that matter most for frame rate in VR, as stored in the VR ini.
/// Keys and value meanings are from VPX 10.8.1's Settings_properties.inl.
/// </summary>
public sealed record VpxGraphics
{
    /// <summary>[Player] AAFactor: supersampling, 1 = headset resolution, 0.5–2.</summary>
    public double RenderScale { get; init; } = 1.0;
    /// <summary>[Player] MSAASamples: 0 off, 1 = 4×, 2 = 6×, 3 = 8×.</summary>
    public int Msaa { get; init; }
    /// <summary>[Player] FXAA: 0 off, 1 Fast FXAA, 2 Standard FXAA, 3 Quality FXAA, 4 Fast NFAA, 5 Standard DLAA, 6 Quality SMAA, 7 Quality FAAA.</summary>
    public int PostAa { get; init; }
    /// <summary>[Player] PFReflection: 0 none, 1 balls, 2 static, 3 static + balls, 4 static + unsynced dynamic, 5 dynamic.</summary>
    public int Reflections { get; init; } = 5;
    /// <summary>[Player] DisableAO / DynamicAO: 0 off, 1 static, 2 dynamic.</summary>
    public int AmbientOcclusion { get; init; } = 2;
    /// <summary>[Player] ForceBloomOff, inverted.</summary>
    public bool Bloom { get; init; } = true;
    /// <summary>[Player] Sharpen: 0 off, 1 CAS, 2 bilateral CAS.</summary>
    public int Sharpen { get; init; }
    /// <summary>[Player] ShowFPS: 0 off, 1 FPS, 2 full overlay.</summary>
    public int FpsOverlay { get; init; }

    public static readonly string[] MsaaNames = ["Off", "4×", "6×", "8×"];
    public static readonly string[] PostAaNames = ["Off", "Fast FXAA", "Standard FXAA", "Quality FXAA", "Fast NFAA", "Standard DLAA", "Quality SMAA", "Quality FAAA"];
    public static readonly string[] ReflectionNames = ["Off", "Balls only", "Static only", "Static + balls", "Static + unsynced dynamic", "Dynamic (best)"];
    public static readonly string[] AoNames = ["Off", "Static", "Dynamic"];
    public static readonly string[] SharpenNames = ["Off", "CAS", "Bilateral CAS"];
    public static readonly string[] FpsNames = ["Off", "FPS counter", "Full overlay"];

    /// <summary>Steady frame rate first: a dropped frame shows as the ball skipping.</summary>
    public static readonly VpxGraphics Performance = new()
    {
        RenderScale = 1.0, Msaa = 0, PostAa = 1, Reflections = 3, AmbientOcclusion = 1, Bloom = false, Sharpen = 0,
    };

    public static readonly VpxGraphics Balanced = new()
    {
        RenderScale = 1.0, Msaa = 1, PostAa = 0, Reflections = 4, AmbientOcclusion = 2, Bloom = true, Sharpen = 0,
    };

    /// <summary>For fast GPUs: supersampled and every effect on.</summary>
    public static readonly VpxGraphics Quality = new()
    {
        RenderScale = 1.3, Msaa = 1, PostAa = 0, Reflections = 5, AmbientOcclusion = 2, Bloom = true, Sharpen = 1,
    };

    public static readonly (string Name, VpxGraphics Settings)[] Presets =
        [("Performance", Performance), ("Balanced", Balanced), ("Quality", Quality)];

    /// <summary>Which preset these settings match (ignoring the FPS overlay), or null for custom.</summary>
    public string? PresetName => Presets.FirstOrDefault(p => p.Settings with { FpsOverlay = FpsOverlay } == this).Name;

    /// <summary>Reads the settings, using VPX's defaults for keys that aren't set.</summary>
    public static VpxGraphics Read(string iniPath)
    {
        var ini = IniFile.Read(iniPath);
        string? Get(string key) => ini.GetValueOrDefault(("Player", key)) is { Length: > 0 } v ? v : null;
        int Int(string key, int def, int max) => int.TryParse(Get(key), out var v) ? Math.Clamp(v, 0, max) : def;
        bool Bool(string key, bool def) => Get(key) is { } v ? v.Trim() is "1" or "true" or "True" : def;
        var aoOff = Bool("DisableAO", false);
        return new VpxGraphics
        {
            RenderScale = double.TryParse(Get("AAFactor"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0.5, 2) : 1.0,
            Msaa = Int("MSAASamples", 0, 3),
            PostAa = Int("FXAA", 0, 7),
            Reflections = Int("PFReflection", 5, 5),
            AmbientOcclusion = aoOff ? 0 : Bool("DynamicAO", true) ? 2 : 1,
            Bloom = !Bool("ForceBloomOff", false),
            Sharpen = Int("Sharpen", 0, 2),
            FpsOverlay = Int("ShowFPS", 0, 2),
        };
    }

    public void Write(string iniPath)
    {
        var values = new (string Key, string Value)[]
        {
            ("AAFactor", RenderScale.ToString("0.0#", CultureInfo.InvariantCulture)),
            ("MSAASamples", Msaa.ToString()),
            ("FXAA", PostAa.ToString()),
            ("PFReflection", Reflections.ToString()),
            ("DisableAO", AmbientOcclusion == 0 ? "1" : "0"),
            ("DynamicAO", AmbientOcclusion == 2 ? "1" : "0"),
            ("ForceBloomOff", Bloom ? "0" : "1"),
            ("Sharpen", Sharpen.ToString()),
            ("ShowFPS", FpsOverlay.ToString()),
        };
        foreach (var (k, v) in values) IniFile.Set(iniPath, "Player", k, v);
    }

    // ---- Per-table render scale -----------------------------------------------------
    // VPX reads "<table>.ini" next to the table as overrides on top of the app settings, so a
    // heavy table can render at a lower scale without changing the rest.

    public static string TableIniPath(string tablePath) => Path.ChangeExtension(tablePath, ".ini");

    /// <summary>The table's own render scale, or null when it uses the global one.</summary>
    public static double? ReadTableScale(string tablePath)
    {
        var v = IniFile.Read(TableIniPath(tablePath)).GetValueOrDefault(("Player", "AAFactor"));
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    /// <summary>Sets (or with null, clears) the table's own render scale.</summary>
    public static void WriteTableScale(string tablePath, double? scale)
    {
        var ini = TableIniPath(tablePath);
        if (scale == null && !File.Exists(ini)) return;
        IniFile.Set(ini, "Player", "AAFactor", scale?.ToString("0.0#", CultureInfo.InvariantCulture) ?? "");
    }
}

/// <summary>An OpenXR runtime registered on this PC.</summary>
public sealed record OpenXrRuntime(string Name, string ManifestPath, bool IsActive)
{
    public bool IsSteamVr => Name == "SteamVR";
    public bool IsMeta => Name == "Meta Quest Link";
}

/// <summary>
/// Which OpenXR runtime VPX (and UEVR in OpenXR mode) will use, and switching it. Windows keeps the
/// choice in HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime; changing it needs administrator rights.
/// </summary>
public static class OpenXrRuntimes
{
    const string Key = @"SOFTWARE\Khronos\OpenXR\1";

    public static string NameFor(string manifestPath)
    {
        var p = manifestPath.ToLowerInvariant();
        return p.Contains("oculus") || p.Contains("meta") ? "Meta Quest Link"
            : p.Contains("steamvr") ? "SteamVR"
            : p.Contains("virtualdesktop") || p.Contains("vdxr") ? "Virtual Desktop (VDXR)"
            : p.Contains("pimax") ? "Pimax"
            : p.Contains("wmr") || p.Contains("mixedreality") ? "Windows Mixed Reality"
            : Path.GetFileNameWithoutExtension(manifestPath);
    }

    public static List<OpenXrRuntime> List()
    {
        var result = new List<OpenXrRuntime>();
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key);
            var active = key?.GetValue("ActiveRuntime") as string ?? "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var avail = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key + @"\AvailableRuntimes");
            foreach (var path in (avail?.GetValueNames() ?? []).Prepend(active))
            {
                if (string.IsNullOrEmpty(path) || !seen.Add(path) || !File.Exists(path)) continue;
                result.Add(new OpenXrRuntime(NameFor(path), path, string.Equals(path, active, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { /* no access: show nothing */ }
        return result;
    }

    public static OpenXrRuntime? Active() => List().FirstOrDefault(r => r.IsActive);

    /// <summary>Makes a runtime the active one. Shows Windows' administrator prompt; returns false if declined or it failed.</summary>
    public static async Task<bool> SetActiveAsync(OpenXrRuntime runtime)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var psi = new ProcessStartInfo("reg.exe")
        {
            Arguments = $"add \"HKLM\\{Key}\" /v ActiveRuntime /t REG_SZ /d \"{runtime.ManifestPath}\" /f",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // prompt declined
    }
}

/// <summary>
/// Meta Quest Link's Asynchronous Spacewarp (ASW). When a frame is late it shows a guessed frame
/// instead, which smears a fast-moving ball. Set through the Oculus Debug Tool's command line; the
/// setting lasts until the Oculus service restarts, so it's applied before each VR launch.
/// </summary>
public static class MetaAsw
{
    public static readonly string[] Modes = ["Leave as is", "Off", "Auto"];

    public static string ToolPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Oculus", "Support", "oculus-diagnostics", "OculusDebugToolCLI.exe");

    public static string DebugToolPath => Path.Combine(Path.GetDirectoryName(ToolPath)!, "OculusDebugTool.exe");

    public static bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(ToolPath);

    /// <summary>Applies "Off" or "Auto"; does nothing for "Leave as is". Never throws.</summary>
    public static async Task<bool> ApplyAsync(string mode)
    {
        var cmd = mode switch { "Off" => "asw.Off", "Auto" => "asw.Auto", _ => null };
        if (cmd == null || !IsAvailable) return false;
        var script = Path.Combine(Path.GetTempPath(), "vrtm-asw.txt");
        try
        {
            await File.WriteAllTextAsync(script, $"server: {cmd}\nexit\n");
            var psi = new ProcessStartInfo(ToolPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi)!;
            var exited = p.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(10))) != exited) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
        finally { try { File.Delete(script); } catch { } }
    }
}
