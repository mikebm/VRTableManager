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
