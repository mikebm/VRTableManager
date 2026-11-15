using VrTableManager.Lobby;

/// <summary>Finding the player's DOF for the VR lobby (LobbyDof.Find): which folder, and which bitness of helper.</summary>
static class LobbyDofChecks
{
    public static IEnumerable<(bool, string)> Run(string root)
    {
        static string Dll(string folder) =>
            Path.GetFullPath(Path.Combine(folder, "DirectOutput.dll"));

        static Func<string, bool> Has(params string[] folders) =>
            f =>
                folders.Any(d =>
                    string.Equals(Dll(d), Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase)
                );

        // What each DLL's header says: 64-bit, 32-bit, or nothing (AnyCPU / unreadable).
        static Func<string, bool?> Bits(params (string Folder, bool? Is64)[] dlls) =>
            f =>
                dlls.FirstOrDefault(d =>
                    string.Equals(
                        Dll(d.Folder),
                        Path.GetFullPath(f),
                        StringComparison.OrdinalIgnoreCase
                    )
                ).Is64;

        var dof = @"C:\DirectOutput";
        var x64 = @"C:\DirectOutput\x64";
        var x86 = @"C:\DirectOutput\x86";

        yield return (
            LobbyDof.Find([], [dof], Has(x64, x86), Bits((x64, true), (x86, false)))
                is { Is64: true } both
                && both.Folder.EndsWith(@"\x64", StringComparison.OrdinalIgnoreCase),
            "lobby DOF: an x64 + x86 install runs the 64-bit DOF"
        );

        yield return (
            LobbyDof.Find([], [dof], Has(x86), Bits((x86, false))) is { Is64: false } only32
                && only32.Folder.EndsWith(@"\x86", StringComparison.OrdinalIgnoreCase),
            "lobby DOF: a 32-bit-only install runs through the 32-bit helper"
        );

        yield return (
            LobbyDof.Find([], [dof], Has(dof), Bits((dof, false))) is { Is64: false } flat
                && string.Equals(flat.Folder, dof, StringComparison.OrdinalIgnoreCase),
            "lobby DOF: an old flat install (DLL in C:\\DirectOutput) is found"
        );

        // The DLL decides, not where it was found: a 32-bit DOF registered in the 64-bit view, or a 64-bit DOF in a
        // flat folder.
        yield return (
            LobbyDof.Find([@"D:\Pin\DOF"], [], Has(@"D:\Pin\DOF"), Bits((@"D:\Pin\DOF", false)))
                is { Is64: false }
                && LobbyDof.Find([], [dof], Has(dof), Bits((dof, true))) is { Is64: true },
            "lobby DOF: the DLL's own header picks the 32- or 64-bit helper"
        );

        // A DLL that doesn't say (AnyCPU, unreadable): an x64 folder counts as 64-bit, anything else as 32-bit.
        yield return (
            LobbyDof.Find([], [dof], Has(x64), _ => null) is { Is64: true }
                && LobbyDof.Find([], [dof], Has(dof), _ => null) is { Is64: false },
            "lobby DOF: without a word from the DLL, the folder's name decides"
        );

        yield return (
            LobbyDof.Find(
                [@"D:\Pinball\DOF\x64"],
                [dof],
                Has(@"D:\Pinball\DOF\x64"),
                Bits((@"D:\Pinball\DOF\x64", true))
            )
                is { Is64: true } reg
                && reg.Folder.StartsWith(@"D:\Pinball", StringComparison.OrdinalIgnoreCase),
            "lobby DOF: found where its COM object is registered"
        );

        yield return (
            LobbyDof.Find([@"D:\Old\x64"], [dof], Has(x86), Bits((x86, false)))
                is { Is64: false } stale
                && stale.Folder.EndsWith(@"\x86", StringComparison.OrdinalIgnoreCase),
            "lobby DOF: a registration whose DLL is gone is skipped"
        );

        yield return (
            LobbyDof.Find([], [dof], _ => false, _ => null) == null,
            "lobby DOF: none installed, none found"
        );

        // Which DOF: VPX's plugin (libdof) when VPX has it on, else an installed DirectOutput, else the plugin anyway.
        var plugin = @"C:\VPinball\VPinballX\plugins\dof";
        var prefs = @"C:\Users\p\AppData\Roaming\VPinballX\10.8";
        var dotNet = new LobbyDof.Install(x64, true);
        yield return (
            LobbyDof.Choose(plugin, prefs, pluginOn: true, () => dotNet)
                is { IsLibDof: true, Is64: true } on
                && on.Folder == plugin
                && on.LibDofGlobalConfig
                    == Path.Combine(prefs, "directoutputconfig", "GlobalConfig_B2SServer.xml"),
            "lobby DOF: VPX's DOF plugin switched on wins over an installed DirectOutput"
        );
        yield return (
            LobbyDof.Choose(plugin, prefs, pluginOn: false, () => dotNet) == dotNet,
            "lobby DOF: with VPX's plugin off, an installed DirectOutput (the tables' DOF) is used"
        );
        yield return (
            LobbyDof.Choose(plugin, prefs, pluginOn: false, () => null) is { IsLibDof: true },
            "lobby DOF: without DirectOutput installed, VPX's bundled plugin is used"
        );
        yield return (
            LobbyDof.Choose(null, prefs, pluginOn: true, () => dotNet) == dotNet
                && LobbyDof.Choose(null, prefs, pluginOn: false, () => null) == null,
            "lobby DOF: a VPX without the plugin falls back to DirectOutput, or none"
        );

        yield return (
            LobbyDof.FromCodeBase("file:///C:/DirectOutput/x64/DirectOutputComObject.dll")
                == @"C:\DirectOutput\x64\DirectOutputComObject.dll"
                && LobbyDof.FromCodeBase(
                    @"file:///D:\Pin #1\DOF 100%\x64\DirectOutputComObject.dll"
                ) == @"D:\Pin #1\DOF 100%\x64\DirectOutputComObject.dll"
                && LobbyDof.FromCodeBase("file://server/share/DOF/DirectOutputComObject.dll")
                    == @"\\server\share\DOF\DirectOutputComObject.dll",
            "lobby DOF: a registered CodeBase reads as a path ('#', '%', network share)"
        );

        // libdof's config: the global config or the Config Tool's table configs count; an empty or missing folder doesn't.
        var cfg = Path.Combine(root, "lobby-dof-config");
        Directory.CreateDirectory(cfg);
        var empty =
            !LobbyDof.HasLibDofConfig(cfg) && !LobbyDof.HasLibDofConfig(Path.Combine(cfg, "nope"));
        File.WriteAllText(Path.Combine(cfg, "directoutputconfig51.ini"), "");
        var iniOnly = LobbyDof.HasLibDofConfig(cfg);
        File.Delete(Path.Combine(cfg, "directoutputconfig51.ini"));
        File.WriteAllText(Path.Combine(cfg, LobbyDof.GlobalConfigName), "<GlobalConfig />");
        var xmlOnly = LobbyDof.HasLibDofConfig(cfg);
        Directory.Delete(cfg, true);
        yield return (
            empty && iniOnly && xmlOnly,
            "lobby DOF: libdof's config is the global config or the Config Tool's .ini files"
        );

        // Which effects: a config whose pinupmenu row sets nothing up for the lobby (a real one, from a cabinet where
        // nothing fired) gets the test table's toys; one that sets Popper's menu up keeps it.
        var ini = Path.Combine(root, "lobby-dof-effects");
        Directory.CreateDirectory(ini);
        var emptyMenu = Path.Combine(ini, "directoutputconfig11.ini");
        File.WriteAllText(
            emptyMenu,
            "[TableVariables]\npinupmenu,ON\n\n[Config DOF]\n"
                + "pinupmenu,0,0,0,0,0,0,0,0,0,0,S3 300 I40/S4 300 I40/S5 300 I40/S7 300 I40/S8 300 I40/W39 500 10 I40/W40 250 5 I40,0\n"
                + "dof_test,E101,E103,E105,E106,E108,E102,E104,E107,E109,E110,E119 1500 I40,E112 1500\n"
        );
        var setUpMenu = Path.Combine(ini, "directoutputconfig30.ini");
        File.WriteAllText(
            setUpMenu,
            "[Config DOF]\npinupmenu,E800,0,0,0,0,E801,0,0,0,0,E806 300 I40,0\ndof_test,E101,0,0,0,0,E102,0,0,0,0,0,0\n"
        );
        string? Line(string file, string rom) => LobbyDof.ConfigLine([file], rom);
        var basic = LobbyDof.ChooseEffects("auto", rom => Line(emptyMenu, rom));
        var popper = LobbyDof.ChooseEffects("auto", rom => Line(setUpMenu, rom));
        var neither = LobbyDof.ChooseEffects("auto", _ => null);
        var forced = LobbyDof.ChooseEffects("popper", rom => Line(emptyMenu, rom));
        var forcedBasic = LobbyDof.ChooseEffects("basic", _ => null);
        var tableVariablesSkipped =
            Line(emptyMenu, "pinupmenu")?.StartsWith("pinupmenu,0,") == true;
        Directory.Delete(ini, true);
        yield return (
            basic is { Rom: "dof_test", Basic: true }
                && popper is { Rom: "pinupmenu", Basic: false }
                && neither is { Rom: "pinupmenu", Basic: false }
                && forced is { Rom: "pinupmenu" }
                && forcedBasic is { Rom: "dof_test", Basic: true }
                && tableVariablesSkipped,
            "lobby DOF effects: automatic picks Popper's menu only when the config sets it up, else the test table"
        );
        yield return (
            basic.Elements(LobbyDof.GamePrior).SequenceEqual([101])
                && basic.Elements(LobbyDof.GameNext).SequenceEqual([102])
                && basic.Elements(LobbyDof.GameStart).SequenceEqual([112, 119])
                && popper.Elements(LobbyDof.GameNext).SequenceEqual([LobbyDof.GameNext])
                && !LobbyDof.UsesMenuEvents("pinupmenu,0,E8000,E1801")
                && LobbyDof.UsesMenuEvents("pinupmenu,0,E822 Blink"),
            "lobby DOF effects: basic turns flippers into the test table's flippers, Start into strobe and shaker"
        );

        // Bitness from a real file: not a DLL at all says nothing.
        var notDll = Path.Combine(root, "lobby-dof-not-a-dll.dll");
        File.WriteAllText(notDll, "not a PE file");
        yield return (
            LobbyDof.DllIs64(notDll) == null,
            "lobby DOF: a file that isn't a DLL gives no bitness"
        );
        File.Delete(notDll);
    }
}
