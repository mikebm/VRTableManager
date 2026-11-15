using VrTableManager.Services;

namespace VrTableManager.Lobby;

/// <summary>
/// The VR lobby's DirectOutput (DOF) events. The lobby plays the part of a front end, as PinUP Popper's menu does: DOF
/// runs with the player's own global config (GlobalConfig_B2SServer.xml, the one their tables use) for the ROM name
/// <see cref="Rom"/>, and the lobby sends Popper's menu events. So whatever the player set up for Popper's menu in the
/// DOF Config Tool (its "pinupmenu" table) fires in the lobby too, and new effects are set up there the same way.
/// </summary>
public static class LobbyDof
{
    /// <summary>PinUP Popper's ROM name in the DOF Config Tool.</summary>
    public const string Rom = "pinupmenu";

    /// <summary>Popper's menu events (E800–E822 on its "pinupmenu" table): the ones the lobby has a match for.</summary>
    public const int GamePrior = 800,
        GameNext = 801,
        ListNext = 802,
        PageNext = 804,
        PagePrior = 805,
        GameStart = 806,
        FavAdd = 816,
        FavDel = 817;

    /// <summary>DOF's COM object, which its installer registers with the path to the DLLs.</summary>
    const string ComClsid = "{a23bfdbc-9a8a-46c0-8672-60f23d54ffb6}";

    /// <summary>
    /// The DOF the lobby runs. Either the .NET DirectOutput: a folder holding DirectOutput.dll, and whether it's the
    /// 64-bit build. Or, with <see cref="ConfigBase"/> set, libdof (the native port VPX 10.8.1 and later ship as its
    /// DOF plugin): the plugin's folder holding dof64.dll, which reads its config from
    /// <c>ConfigBase\directoutputconfig</c>.
    /// </summary>
    public sealed record Install(string Folder, bool Is64, string? ConfigBase = null)
    {
        public bool IsLibDof => ConfigBase != null;

        /// <summary>The global config this DOF reads (libdof only: the .NET DOF finds its own).</summary>
        public string? LibDofGlobalConfig =>
            ConfigBase == null
                ? null
                : Path.Combine(ConfigBase, LibDofConfigFolder, GlobalConfigName);

        /// <summary>The folder libdof reads its config from (libdof only).</summary>
        public string? LibDofConfigDir =>
            ConfigBase == null ? null : Path.Combine(ConfigBase, LibDofConfigFolder);

        public override string ToString() =>
            IsLibDof
                ? $"VPX's DOF plugin (libdof) in {Folder}, config in {Path.Combine(ConfigBase!, LibDofConfigFolder)}"
                : $"DirectOutput in {Folder} ({(Is64 ? "64" : "32")}-bit)";
    }

    /// <summary>The folder libdof looks for its config in, under its base path.</summary>
    public const string LibDofConfigFolder = "directoutputconfig";

    public const string GlobalConfigName = "GlobalConfig_B2SServer.xml";

    /// <summary>
    /// Whether a libdof config folder holds a config: the global config, or the Config Tool's table configs
    /// (directoutputconfig*.ini, or the older ledcontrol*.ini), which libdof loads without a global config too.
    /// </summary>
    public static bool HasLibDofConfig(string folder)
    {
        try
        {
            return File.Exists(Path.Combine(folder, GlobalConfigName))
                || (
                    Directory.Exists(folder)
                    && (
                        Directory.EnumerateFiles(folder, "directoutputconfig*.ini").Any()
                        || Directory.EnumerateFiles(folder, "ledcontrol*.ini").Any()
                    )
                );
        }
        catch
        {
            return false; // unreadable: as good as none
        }
    }

    /// <summary>libdof's library in VPX's DOF plugin folder (64-bit only).</summary>
    public const string LibDofDll = "dof64.dll";

    /// <summary>
    /// The DOF to run, the one the player's tables use where both are there. VPX's own DOF plugin (libdof, bundled
    /// with VPX 10.8.1 and later) when it's switched on in VPX's settings; otherwise an installed .NET DirectOutput;
    /// otherwise the bundled plugin anyway, so DOF needn't be installed separately. Null when there's neither.
    /// </summary>
    public static Install? Find(AppSettings s)
    {
        var plugin = Path.Combine(s.VpxDir, "plugins", "dof");
        var ini = VpxIniLayers.Effective(s, vr: true);
        var pluginOn = ini.GetValueOrDefault(("Plugin.DOF", "Enable")) == "1";
        var dofPath = ini.GetValueOrDefault(("Plugin.DOF", "DOFPath"));
        var configBase = !string.IsNullOrWhiteSpace(dofPath)
            ? dofPath.Trim()
            : VpxIniLayers.PrefsFolder(s);
        return Choose(
            File.Exists(Path.Combine(plugin, LibDofDll)) ? plugin : null,
            configBase,
            pluginOn,
            FindDotNet
        );
    }

    /// <summary>
    /// <see cref="Find(AppSettings)"/>'s choice, given what's there (for tests): <paramref name="libDofFolder"/> is
    /// VPX's DOF plugin folder when it has libdof, <paramref name="dotNet"/> looks for an installed DirectOutput.
    /// </summary>
    public static Install? Choose(
        string? libDofFolder,
        string? configBase,
        bool pluginOn,
        Func<Install?> dotNet
    )
    {
        var libDof =
            libDofFolder != null && configBase != null
                ? new Install(libDofFolder, true, configBase)
                : null;
        if (libDof != null && pluginOn)
        {
            return libDof;
        }

        return dotNet() ?? libDof;
    }

    /// <summary>
    /// An installed .NET DirectOutput: the 64-bit one when there is one (a 32-bit-only install still works, through
    /// the 32-bit helper). Looks where DOF's COM object is registered, then in DOF's usual folder (C:\DirectOutput,
    /// with x64 / x86 subfolders since R3++, flat before). Null when it isn't installed.
    /// </summary>
    public static Install? FindDotNet() =>
        Find(RegisteredFolders(), [@"C:\DirectOutput"], File.Exists, DllIs64);

    /// <summary>
    /// <see cref="Find()"/> with the places to look given (for tests). Each DLL's bitness is read from the DLL itself
    /// (<paramref name="dllIs64"/>), which decides the helper: a folder's name or the registry view it was registered
    /// in can be wrong. Only when the DLL doesn't say does an x64 folder count as 64-bit.
    /// </summary>
    public static Install? Find(
        IEnumerable<string> registered,
        IEnumerable<string> installRoots,
        Func<string, bool> fileExists,
        Func<string, bool?> dllIs64
    )
    {
        var folders = registered.ToList();
        foreach (var root in installRoots)
        {
            folders.Add(Path.Combine(root, "x64"));
            folders.Add(Path.Combine(root, "x86"));
            folders.Add(root); // the old flat install
        }

        var found = folders
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => fileExists(Path.Combine(f, "DirectOutput.dll")))
            .Select(f => new Install(
                f,
                dllIs64(Path.Combine(f, "DirectOutput.dll"))
                    ?? string.Equals(Path.GetFileName(f), "x64", StringComparison.OrdinalIgnoreCase)
            ))
            .ToList();
        return found.FirstOrDefault(i => i.Is64) ?? found.FirstOrDefault();
    }

    /// <summary>
    /// Whether a DLL is built for 64-bit (x64 / ARM64: true) or 32-bit only (x86, or AnyCPU marked 32-bit: false),
    /// from its PE header. Null for AnyCPU without that mark, or a file that can't be read.
    /// </summary>
    public static bool? DllIs64(string dll)
    {
        try
        {
            using var stream = File.OpenRead(dll);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var headers = pe.PEHeaders;
            if (
                headers.CoffHeader.Machine
                is System.Reflection.PortableExecutable.Machine.Amd64
                    or System.Reflection.PortableExecutable.Machine.Arm64
            )
            {
                return true;
            }

            var flags = headers.CorHeader?.Flags ?? 0;
            if (
                headers.CoffHeader.Machine == System.Reflection.PortableExecutable.Machine.I386
                && (
                    headers.CorHeader == null
                    || (flags & System.Reflection.PortableExecutable.CorFlags.Requires32Bit) != 0
                    || (flags & System.Reflection.PortableExecutable.CorFlags.ILOnly) == 0
                )
            )
            {
                return false;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The folders DOF's COM object is registered from (64-bit registry view, then 32-bit).</summary>
    static IEnumerable<string> RegisteredFolders()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (
            var view in new[]
            {
                Microsoft.Win32.RegistryView.Registry64,
                Microsoft.Win32.RegistryView.Registry32,
            }
        )
        {
            string? folder = null;
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.ClassesRoot,
                    view
                );
                using var key = root.OpenSubKey($@"CLSID\{ComClsid}\InprocServer32");
                if (key?.GetValue("CodeBase") is string codeBase)
                {
                    folder = Path.GetDirectoryName(FromCodeBase(codeBase));
                }
            }
            catch
            {
                // unreadable: the usual folder is still tried
            }

            if (!string.IsNullOrEmpty(folder))
            {
                yield return folder;
            }
        }
    }

    /// <summary>
    /// A registered CodeBase (file:///C:\DirectOutput\x64\DirectOutputComObject.dll) as a path. Read as plain text, not
    /// as a URI, which would cut a folder name at a '#' or decode a '%'.
    /// </summary>
    public static string FromCodeBase(string codeBase)
    {
        var path = codeBase;
        if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            path = path["file:///".Length..];
        }
        else if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = "//" + path["file://".Length..]; // a network share: file://server/share/…
        }

        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}
