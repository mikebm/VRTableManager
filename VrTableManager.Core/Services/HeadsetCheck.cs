using System.Runtime.InteropServices;

namespace VrTableManager.Services;

public enum HeadsetState
{
    /// <summary>The runtime has a headset ready (e.g. the Quest is in an active Link session).</summary>
    Ready,
    /// <summary>The runtime is installed but no headset is connected right now (XR_ERROR_FORM_FACTOR_UNAVAILABLE).</summary>
    NotConnected,
    /// <summary>No OpenXR runtime is set up on this PC.</summary>
    NoRuntime,
    /// <summary>Couldn't tell (no OpenXR loader found, the runtime timed out, or another error). Don't block the launch.</summary>
    Unknown,
}

public sealed record HeadsetStatus(HeadsetState State, string Detail);

/// <summary>
/// Asks the active OpenXR runtime whether a headset is available, the same question VPX asks when it
/// starts (xrCreateInstance + xrGetSystem for a head-mounted display). If the Quest isn't in a Link
/// session, the runtime answers XR_ERROR_FORM_FACTOR_UNAVAILABLE and VPX silently opens on the
/// monitor instead, so the app checks first and asks you to start Link.
/// Uses the OpenXR loader that ships with VPX (openxr_loader64.dll) or UEVR; no session is created.
/// </summary>
public static class HeadsetCheck
{
    public static async Task<HeadsetStatus> CheckAsync(AppSettings s, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows()) return new(HeadsetState.Unknown, "Headset check is Windows-only.");
        var loader = FindLoader(s);
        if (loader == null) return new(HeadsetState.Unknown, "No OpenXR loader found (install VPX first).");

        // Some runtimes (SteamVR) start up on first contact, which can take a while; don't hang on it.
        var work = Task.Run(() => Query(loader));
        var done = await Task.WhenAny(work, Task.Delay(timeout));
        return done == work ? work.Result : new(HeadsetState.Unknown, "The VR runtime didn't answer in time.");
    }

    public static string? FindLoader(AppSettings s)
    {
        string[] names = ["openxr_loader64.dll", "openxr_loader.dll"];
        foreach (var dir in new[] { s.VpxDir, s.UevrDir })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (var n in names)
                if (Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).FirstOrDefault() is { } hit) return hit;
        }
        return null;
    }

    // ---- OpenXR, just enough for xrCreateInstance / xrGetSystem / xrDestroyInstance ----

    const int XR_SUCCESS = 0;
    const int XR_ERROR_RUNTIME_UNAVAILABLE = -51;
    const int XR_ERROR_RUNTIME_FAILURE = -2;
    const int XR_ERROR_FORM_FACTOR_UNAVAILABLE = -35;
    const int XR_TYPE_INSTANCE_CREATE_INFO = 3;
    const int XR_TYPE_SYSTEM_GET_INFO = 4;
    const int XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY = 1;
    const ulong XR_API_VERSION_1_0 = 1UL << 48;

    [StructLayout(LayoutKind.Sequential)]
    struct XrApplicationInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] ApplicationName;
        public uint ApplicationVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] EngineName;
        public uint EngineVersion;
        public ulong ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XrInstanceCreateInfo
    {
        public int Type;
        public IntPtr Next;
        public ulong CreateFlags;
        public XrApplicationInfo ApplicationInfo;
        public uint EnabledApiLayerCount;
        public IntPtr EnabledApiLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XrSystemGetInfo
    {
        public int Type;
        public IntPtr Next;
        public int FormFactor;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int XrCreateInstance(ref XrInstanceCreateInfo info, out IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int XrGetSystem(IntPtr instance, ref XrSystemGetInfo info, out ulong systemId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int XrDestroyInstance(IntPtr instance);

    static HeadsetStatus Query(string loaderPath)
    {
        try
        {
            var lib = NativeLibrary.Load(loaderPath);
            var create = Marshal.GetDelegateForFunctionPointer<XrCreateInstance>(NativeLibrary.GetExport(lib, "xrCreateInstance"));
            var getSystem = Marshal.GetDelegateForFunctionPointer<XrGetSystem>(NativeLibrary.GetExport(lib, "xrGetSystem"));
            var destroy = Marshal.GetDelegateForFunctionPointer<XrDestroyInstance>(NativeLibrary.GetExport(lib, "xrDestroyInstance"));

            var info = new XrInstanceCreateInfo
            {
                Type = XR_TYPE_INSTANCE_CREATE_INFO,
                ApplicationInfo = new XrApplicationInfo
                {
                    ApplicationName = Name("VR Table Manager headset check"),
                    ApplicationVersion = 1,
                    EngineName = Name("none"),
                    ApiVersion = XR_API_VERSION_1_0,
                },
            };
            var r = create(ref info, out var instance);
            if (r == XR_ERROR_RUNTIME_UNAVAILABLE || r == XR_ERROR_RUNTIME_FAILURE)
                return new(HeadsetState.NoRuntime, $"No working OpenXR runtime (error {r}).");
            if (r != XR_SUCCESS) return new(HeadsetState.Unknown, $"OpenXR couldn't start (error {r}).");
            try
            {
                var sys = new XrSystemGetInfo { Type = XR_TYPE_SYSTEM_GET_INFO, FormFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY };
                r = getSystem(instance, ref sys, out _);
                return r switch
                {
                    XR_SUCCESS => new(HeadsetState.Ready, "Headset connected."),
                    XR_ERROR_FORM_FACTOR_UNAVAILABLE => new(HeadsetState.NotConnected, "The VR runtime is running but no headset is connected."),
                    _ => new(HeadsetState.Unknown, $"OpenXR couldn't look up the headset (error {r})."),
                };
            }
            finally
            {
                destroy(instance);
            }
        }
        catch (Exception e)
        {
            return new(HeadsetState.Unknown, "Headset check failed: " + e.Message);
        }
    }

    static byte[] Name(string s)
    {
        var b = new byte[128];
        System.Text.Encoding.ASCII.GetBytes(s, 0, Math.Min(s.Length, 127), b, 0);
        return b;
    }
}
