using System.IO.Compression;
using System.Text;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// VPU Remixes: applying their .dif (JojoDiff) patch, refusing one that doesn't fit the table it's applied to,
/// spotting remixes in the catalog and finding the table they patch.
/// </summary>
static class VpuRemixChecks
{
    const byte Esc = 0xA7,
        Mod = 0xA6,
        Ins = 0xA5,
        Del = 0xA4,
        Eql = 0xA3,
        Bkt = 0xA2;

    static byte[] Patch(byte[] source, byte[] patch)
    {
        using var src = new MemoryStream(source);
        using var pat = new MemoryStream(patch);
        using var dst = new MemoryStream();
        VpuRemix.Patch(src, pat, dst);
        return dst.ToArray();
    }

    static bool Throws<T>(Action a)
        where T : Exception
    {
        try
        {
            a();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    // ---- a minimal JojoDiff writer, to make patches between test tables ------------------------

    static void Len(List<byte> o, long n)
    {
        if (n - 1 < 252)
        {
            o.Add((byte)(n - 1));
        }
        else if (n - 253 <= 255)
        {
            o.AddRange([252, (byte)(n - 253)]);
        }
        else if (n <= ushort.MaxValue)
        {
            o.AddRange([253, (byte)(n >> 8), (byte)n]);
        }
        else
        {
            o.AddRange([254, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
        }
    }

    static void Data(List<byte> o, byte b)
    {
        o.Add(b);
        if (b == Esc)
        {
            o.Add(Esc);
        }
    }

    /// <summary>Byte-by-byte diff: equal runs copy, different runs overwrite, the rest is inserted or skipped.</summary>
    static byte[] Diff(byte[] a, byte[] b)
    {
        var o = new List<byte>();
        var common = Math.Min(a.Length, b.Length);
        for (var i = 0; i < common; )
        {
            var j = i;
            if (a[i] == b[i])
            {
                while (j < common && a[j] == b[j])
                {
                    j++;
                }

                o.AddRange([Esc, Eql]);
                Len(o, j - i);
            }
            else
            {
                while (j < common && a[j] != b[j])
                {
                    j++;
                }

                o.AddRange([Esc, Mod]);
                for (var k = i; k < j; k++)
                {
                    Data(o, b[k]);
                }
            }
            i = j;
        }
        if (b.Length > common)
        {
            o.AddRange([Esc, Ins]);
            foreach (var x in b[common..])
            {
                Data(o, x);
            }
        }
        else if (a.Length > common)
        {
            o.AddRange([Esc, Del]);
            Len(o, a.Length - common);
        }
        return [.. o];
    }

    // ---- test tables -------------------------------------------------------------------------

    static byte[] Rec(string tag, byte[] data) =>
        [.. BitConverter.GetBytes(data.Length + 4), .. Encoding.ASCII.GetBytes(tag), .. data];

    /// <summary>A small VPX: a script, one part with a width, and a 20 KB picture (so it has regular sectors too).</summary>
    static string Vpx(string path, string script, int width, int seed = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var code = Encoding.Latin1.GetBytes(script);
        var pic = new byte[20000];
        new Random(seed).NextBytes(pic);
        using var cfb = OpenMcdf.RootStorage.Create(path);
        var gs = cfb.CreateStorage("GameStg");
        void Put(string name, byte[] bytes)
        {
            using var s = gs.CreateStream(name);
            s.Write(bytes, 0, bytes.Length);
        }
        Put(
            "GameData",
            [
                .. BitConverter.GetBytes(4),
                .. "CODE"u8,
                .. BitConverter.GetBytes(code.Length),
                .. code,
                .. Rec("NAME", [.. BitConverter.GetBytes(5), .. "Table"u8]),
                .. Rec("ENDB", []),
            ]
        );
        Put(
            "GameItem0",
            [
                .. BitConverter.GetBytes(0),
                .. Rec("WDTH", BitConverter.GetBytes((float)width)),
                .. Rec("ENDB", []),
            ]
        );
        // A decal: FONT is written like CODE (its length covers only the tag), then a saved font, not records.
        Put(
            "GameItem1",
            [
                .. BitConverter.GetBytes(10),
                .. Rec("TEXT", [.. BitConverter.GetBytes(2), .. "Hi"u8]),
                .. BitConverter.GetBytes(4),
                .. "FONT"u8,
                0x01,
                0x00,
                0x00,
                0x00,
                0x90,
                0x01,
                0x40,
                0x1F,
                0x00,
                0x00,
                5,
                .. "Arial"u8,
                .. Rec("ENDB", []),
            ]
        );
        // A wall: drag points follow DPNT, each with records and an ENDB of its own.
        Put(
            "GameItem2",
            [
                .. BitConverter.GetBytes(0),
                .. Rec("HTTP", BitConverter.GetBytes(50f)),
                .. BitConverter.GetBytes(4),
                .. "DPNT"u8,
                .. Rec("VCEN", new byte[8]),
                .. Rec("ENDB", []),
                .. Rec("ENDB", []),
            ]
        );
        Put(
            "Image0",
            [
                .. Rec("NAME", [.. BitConverter.GetBytes(2), .. "pf"u8]),
                .. Rec("DATA", pic),
                .. Rec("ENDB", []),
            ]
        );
        return path;
    }

    static InstalledTable Installed(
        string folder,
        string vpx,
        string gameId,
        string fileId,
        params string[] features
    ) =>
        new(
            new TableManifest
            {
                Engine = TableManifest.Vpx,
                GameId = gameId,
                GameName = Path.GetFileNameWithoutExtension(vpx),
                TableFileId = fileId,
                Features = [.. features],
            },
            vpx,
            folder
        );

    public static async Task<List<(bool, string)>> RunAsync(string root)
    {
        var r = new List<(bool, string)>();

        // ---- the patch format (the cases jojodiff-rs checks, which vpxtool uses for VPU patches) ----
        r.Add(
            (
                Patch([1, 2, 3], [Esc, Eql, 2]).SequenceEqual(new byte[] { 1, 2, 3 }),
                "remix patch: EQL copies the original"
            )
        );
        r.Add(
            (
                Patch([1, 2, 3, 4, 5, 6, 7], [Esc, Eql, 1, Esc, Mod, 0, 0, 0, Esc, Eql, 1])
                    .SequenceEqual(new byte[] { 1, 2, 0, 0, 0, 6, 7 }),
                "remix patch: MOD overwrites in place"
            )
        );
        r.Add(
            (
                Encoding.ASCII.GetString(
                    Patch(
                        "Hello, World!"u8.ToArray(),
                        [Esc, Eql, 6, Esc, Mod, .. "Unive"u8, Esc, Ins, .. "rse"u8, Esc, Eql, 0]
                    )
                ) == "Hello, Universe!",
                "remix patch: MOD, INS and EQL together"
            )
        );
        r.Add(
            (
                Patch([], [Esc, Ins, 1, 2, Esc, Esc, 3]).SequenceEqual(new byte[] { 1, 2, Esc, 3 }),
                "remix patch: ESC ESC is one literal ESC"
            )
        );
        r.Add(
            (
                Patch([1, 2, 3], [Esc, Eql, 2, Esc, Bkt, 2, Esc, Eql, 2])
                    .SequenceEqual(new byte[] { 1, 2, 3, 1, 2, 3 }),
                "remix patch: BKT seeks back in the original"
            )
        );
        r.Add(
            (
                Patch([1, 2, 3], [3, 2, 1]).SequenceEqual(new byte[] { 3, 2, 1 }),
                "remix patch: data without an opcode overwrites"
            )
        );
        r.Add((Patch([1], [Esc, Del, 0]).Length == 0, "remix patch: DEL skips the original"));
        var big = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
        r.Add(
            (
                Patch(big, [Esc, Eql, 252, 0]).Length == 253
                    && Patch(big, [Esc, Eql, 253, 0x01, 0x02]).Length == 258
                    && Patch(big, [Esc, Eql, 254, 0, 1, 0, 0]).Length == 65536,
                "remix patch: 8, 16 and 32-bit lengths"
            )
        );
        r.Add(
            (
                Throws<VpuRemix.MismatchException>(() => Patch([1, 2], [Esc, Eql, 9])),
                "remix patch: copying past the original's end is a mismatch, not a short table"
            )
        );
        r.Add(
            (
                Throws<VpuRemix.MismatchException>(() => Patch([1, 2], [Esc, Eql, 0, Esc, Bkt, 5])),
                "remix patch: seeking before the original's start is a mismatch"
            )
        );
        r.Add(
            (
                Throws<VpuRemix.MismatchException>(() => Patch([1, 2], [Esc, Del, 9, Esc, Eql, 0]))
                    && Throws<VpuRemix.MismatchException>(() =>
                        Patch(big, [Esc, Eql, 255, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])
                    )
                    && Throws<VpuRemix.MismatchException>(() =>
                        Patch(
                            big,
                            [
                                Esc,
                                Eql,
                                0,
                                Esc,
                                Del,
                                255,
                                0x7F,
                                0xFF,
                                0xFF,
                                0xFF,
                                0xFF,
                                0xFF,
                                0xFF,
                                0xF0,
                            ]
                        )
                    ),
                "remix patch: skipping past the original's end, or a huge length, is a mismatch (no overflow)"
            )
        );
        r.Add(
            (
                Throws<InvalidDataException>(() => Patch([1], [1, 2, Esc]))
                    && Throws<InvalidDataException>(() => Patch(big, [Esc, Eql, 254, 0])),
                "remix patch: a cut-off .dif is refused"
            )
        );
        r.Add(
            (
                Patch([], [Esc, 0x10, 7]).SequenceEqual(new byte[] { Esc, 0x10, 7 })
                    && Patch([], [Esc, Esc, 7]).SequenceEqual(new byte[] { Esc, Esc, 7 }),
                "remix patch: ESC xxx starting a run keeps both bytes (as jojodiff-rs)"
            )
        );
        r.Add(
            (
                Patch([], [Esc, Ins, 1, Esc, Ins, 2]).SequenceEqual(new byte[] { 1, Esc, Ins, 2 }),
                "remix patch: the run's own opcode inside it is data"
            )
        );
        r.Add(
            (
                Patch(big, [Esc, Eql, 255, 0, 0, 0, 0, 0, 0, 0, 3]).Length == 3,
                "remix patch: 64-bit lengths"
            )
        );
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            r.Add(
                (
                    Throws<OperationCanceledException>(() =>
                        VpuRemix.Patch(
                            new MemoryStream(big),
                            new MemoryStream([Esc, Eql, 2]),
                            new MemoryStream(),
                            cancelled.Token
                        )
                    ),
                    "remix patch: cancelling stops it"
                )
            );
        }

        // ---- applying a remix to a real table file ----
        var dir = Path.Combine(root, "remix");
        var original = Vpx(
            Path.Combine(dir, "orig", "Base Table 1.0.vpx"),
            "Sub Init : End Sub",
            50
        );
        var modded = Vpx(
            Path.Combine(dir, "mod", "Base Table Remix.vpx"),
            "Sub Init : PlaySound \"x\" : End Sub",
            75
        );
        var other = Vpx(
            Path.Combine(dir, "other", "Base Table 1.1.vpx"),
            "' version 1.1\nSub Init : End Sub ' longer now",
            50,
            seed: 2
        );
        var dif = Path.Combine(dir, "Base Table Remix.dif");
        File.WriteAllBytes(dif, Diff(File.ReadAllBytes(original), File.ReadAllBytes(modded)));
        r.Add(
            (
                VpuRemix.LooksLikeVpx(original) && VpuRemix.LooksLikeVpx(modded),
                "remix: test tables read as VPX"
            )
        );

        var made = Path.Combine(dir, "made.vpx");
        VpuRemix.Apply(original, dif, made);
        r.Add(
            (
                File.ReadAllBytes(made).SequenceEqual(File.ReadAllBytes(modded)),
                "remix: the .dif on its original gives the remixed table"
            )
        );

        var wrong = Path.Combine(dir, "wrong.vpx");
        r.Add(
            (
                Throws<VpuRemix.MismatchException>(() => VpuRemix.Apply(other, dif, wrong))
                    && !File.Exists(wrong),
                "remix: the .dif on another version of the table is refused and leaves no file"
            )
        );
        var notVpx = Path.Combine(dir, "readme.vpx");
        File.WriteAllBytes(notVpx, new byte[File.ReadAllBytes(original).Length]);
        r.Add(
            (
                Throws<VpuRemix.MismatchException>(() => VpuRemix.Apply(notVpx, dif, wrong)),
                "remix: the .dif on something that isn't a table is refused"
            )
        );
        // A sound compound file whose part's records are cut short, as copying the wrong original's bytes leaves them.
        var garbledPath = Path.Combine(dir, "garbled.vpx");
        using (var cfb = OpenMcdf.RootStorage.Create(garbledPath))
        {
            var gs = cfb.CreateStorage("GameStg");
            using (var s = gs.CreateStream("GameData"))
            {
                s.Write([.. Rec("NAME", [0, 0, 0, 0]), .. Rec("ENDB", [])]);
            }
            using (var s = gs.CreateStream("GameItem0"))
            {
                s.Write([0, 0, 0, 0, .. BitConverter.GetBytes(400), .. "WDTH"u8, 1, 2]);
            }
        }
        r.Add(
            (
                !VpuRemix.LooksLikeVpx(garbledPath),
                "remix: a table whose records don't read through isn't taken as a VPX"
            )
        );
        r.Add(
            (
                VpuRemix.ApplyToFirstFitting([other, original], dif, made) == original
                    && VpuRemix.ApplyToFirstFitting([other], dif, made) == null,
                "remix: the patch is tried on each candidate until one fits"
            )
        );
        var origLen = new FileInfo(original).Length;
        r.Add(
            (
                VpuRemix.OriginalSize(dif) is { } need
                    && need <= origLen
                    && VpuRemix.SizeFits(origLen, need)
                    && !VpuRemix.SizeFits(new FileInfo(other).Length + 100_000, need)
                    && !VpuRemix.SizeFits(need - 1, need)
                    && VpuRemix.OriginalSize(Path.Combine(dir, "gone.dif")) == null
                    && VpuRemix.SizeText(89_858_048) == "89.9 MB",
                "remix: a .dif tells the size of the table it was made from (a much bigger or smaller one isn't it)"
            )
        );
        r.Add(
            (
                VpuRemix.ApplyToFirstFitting([Path.Combine(dir, "gone.vpx"), original], dif, made)
                    == original,
                "remix: a candidate that's gone is skipped"
            )
        );
        var commentOnly = new VpsTableFile
        {
            Id = "c1",
            TableFormat = "VPX",
            Comment = "VPU Remix of the 1.0 table",
            Features = ["VR"],
        };
        r.Add(
            (
                VpuRemix.FeaturesOf(commentOnly).Contains(VpuRemix.Feature)
                    && VpuRemix.FeaturesOf(commentOnly).Contains("VR")
                    && !VpuRemix
                        .FeaturesOf(new VpsTableFile { Id = "p", Features = ["VR"] })
                        .Contains(VpuRemix.Feature),
                "remix: a remix VPS only marks in its comment is saved with the remix tag"
            )
        );
        var brokenDif = Path.Combine(dir, "broken.dif");
        File.WriteAllBytes(brokenDif, [Esc, Eql, 254, 0]);
        r.Add(
            (
                Throws<InvalidDataException>(() =>
                    VpuRemix.ApplyToFirstFitting([original], brokenDif, made)
                ),
                "remix: a broken .dif says so instead of blaming the table's version"
            )
        );

        // ---- the catalog ----
        var baseFile = new VpsTableFile
        {
            Id = "base1",
            TableFormat = "VPX",
            Version = "1.0",
            Authors = ["Author"],
        };
        var remix = new VpsTableFile
        {
            Id = "rmx1",
            TableFormat = "VPX",
            Version = "1.0",
            Authors = ["Modder"],
            Features = ["VPU Patch", "MOD"],
            ParentId = "base1",
            Comment =
                "Use in combination with original table and the VPURemix - Patching System Tool!",
        };
        var untagged = new VpsTableFile
        {
            Id = "rmx2",
            TableFormat = "VPX",
            ParentId = "base1",
            Comment = "Music MOD - Use VPURemix System to patch.",
        };
        var plain = new VpsTableFile
        {
            Id = "plain",
            TableFormat = "VPX",
            ParentId = "base1",
            Comment = "VR conversion",
        };
        var game = new VpsGame
        {
            Id = "g-rmx",
            Name = "Base Table",
            TableFiles = [baseFile, remix, untagged, plain],
        };
        var crossFile = new VpsTableFile
        {
            Id = "x1",
            TableFormat = "VPX",
            Features = ["VPU Patch"],
            ParentId = "base1",
        };
        var crossGame = new VpsGame
        {
            Id = "g-x",
            Name = "Retheme",
            TableFiles = [crossFile],
        };
        var orphan = new VpsTableFile
        {
            Id = "o1",
            TableFormat = "VPX",
            Features = ["VPU Patch"],
            ParentId = "root",
        };
        List<VpsGame> games = [crossGame, game];
        r.Add(
            (
                VpuRemix.IsRemix(remix)
                    && VpuRemix.IsRemix(untagged)
                    && !VpuRemix.IsRemix(plain)
                    && !VpuRemix.IsRemix(baseFile),
                "remix: spotted by its VPU Patch tag or a comment naming the patching tool"
            )
        );
        r.Add(
            (
                VpuRemix.BaseOf(remix, games, game) is { File.Id: "base1", Game.Id: "g-rmx" }
                    && VpuRemix.BaseOf(crossFile, games, crossGame) is { Game.Id: "g-rmx" }
                    && VpuRemix.BaseOf(orphan, games) == null,
                "remix: its base is the VPS parent, also in another game; \"root\" is none"
            )
        );

        var baseFolder = Path.Combine(dir, "tables", "Base");
        Directory.CreateDirectory(baseFolder);
        var baseVpx = Path.Combine(baseFolder, "Base Table 1.0.vpx");
        File.Copy(original, baseVpx);
        var exact = Installed(baseFolder, baseVpx, "g-rmx", "base1");
        var byHand = Installed(baseFolder, baseVpx, "g-rmx", "");
        var earlierRemix = Installed(baseFolder, baseVpx, "g-rmx", "rmx1", "VPU Patch");
        var otherRemix = Installed(baseFolder, baseVpx, "g-rmx", "rmx9", "VPU Patch");
        var otherGame = Installed(baseFolder, baseVpx, "g-zzz", "zzz");
        var cands = VpuRemix.BaseCandidates(
            remix,
            "g-rmx",
            [byHand, earlierRemix, otherRemix, otherGame, exact],
            "g-rmx"
        );
        r.Add(
            (
                cands.Count == 2
                    && cands[0].Table == exact
                    && cands[0].Exact
                    && cands[1].Table == byHand
                    && !cands[1].Exact,
                "remix: the exact base comes first, then the game's other tables; never the remix or other remixes"
            )
        );
        File.Copy(baseVpx, VrDmdFix.BackupPath(baseVpx));
        r.Add(
            (
                VpuRemix.BaseCandidates(remix, "g-rmx", [exact]).Single().SourcePath
                    == VrDmdFix.BackupPath(baseVpx),
                "remix: a table the VR DMD fix changed is patched from the original it kept"
            )
        );
        File.Delete(VrDmdFix.BackupPath(baseVpx));

        // ---- installing: the download has a .dif, not a table ----
        var settings = new AppSettings
        {
            TablesDir = Path.Combine(dir, "installed"),
            VpxDir = Path.Combine(dir, "vpx"),
        };
        var packDir = Path.Combine(dir, "pack");
        Directory.CreateDirectory(packDir);
        File.Copy(dif, Path.Combine(packDir, "Base Table Remix.dif"));
        File.WriteAllText(
            Path.Combine(packDir, "ReadMe.txt"),
            "Needs the original: https://vpuniverse.com/files/file/1-base-table/ v1.0"
        );
        var pack = Path.Combine(dir, "remix.zip");
        ZipFile.CreateFromDirectory(packDir, pack);

        var noBase = "";
        try
        {
            await TableInstaller.InstallAsync(
                pack,
                game,
                remix,
                settings,
                _ => { },
                CancellationToken.None
            );
        }
        catch (InvalidDataException e)
        {
            noBase = e.Message;
        }
        r.Add(
            (
                noBase.Contains("VPU Remix")
                    && noBase.Contains("https://vpuniverse.com/files/file/1-base-table/"),
                "remix install: without its base it says so and points at the ReadMe's link"
            )
        );

        var mismatch = "";
        try
        {
            await TableInstaller.InstallAsync(
                pack,
                game,
                remix,
                settings,
                _ => { },
                CancellationToken.None,
                remixBases: [other]
            );
        }
        catch (VpuRemix.MismatchException e)
        {
            mismatch = e.Message;
        }
        r.Add(
            (
                mismatch.Contains("exact version")
                    && (
                        !Directory.Exists(settings.TablesDir)
                        || !Directory
                            .EnumerateFiles(
                                settings.TablesDir,
                                "*.vpx",
                                SearchOption.AllDirectories
                            )
                            .Any()
                    ),
                "remix install: the wrong base version installs nothing"
            )
        );

        var inst = await TableInstaller.InstallAsync(
            pack,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            remixBases: [other, original]
        );
        r.Add(
            (
                Path.GetFileName(inst.TablePath) == "Base Table Remix.vpx"
                    && File.ReadAllBytes(inst.TablePath).SequenceEqual(File.ReadAllBytes(modded))
                    && inst.Manifest.TableFileId == "rmx1"
                    && !Directory.EnumerateFiles(inst.Folder, "*.dif").Any()
                    && inst.InstallNote?.Contains("Base Table 1.0.vpx") == true,
                "remix install: the .dif is applied to the base that fits, and the remix is listed as an installed table"
            )
        );

        // Replacing the base: the remix goes into the base's folder and the original goes.
        var baseInst = await TableInstaller.InstallAsync(
            original,
            game,
            baseFile,
            settings,
            _ => { },
            CancellationToken.None
        );
        var replaced = await TableInstaller.InstallAsync(
            pack,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            replacing: baseInst,
            remixBases: [baseInst.TablePath]
        );
        r.Add(
            (
                replaced.Folder == baseInst.Folder
                    && !File.Exists(baseInst.TablePath)
                    && File.ReadAllBytes(replaced.TablePath)
                        .SequenceEqual(File.ReadAllBytes(modded)),
                "remix install: replacing its base puts the remix in the base's folder in place of the original"
            )
        );

        // The original is kept beside it (not as a table), and the remix's updates are patched from it.
        var kept = VpuRemix.KeptPath(baseInst.TablePath);
        r.Add(
            (
                File.Exists(kept)
                    && File.ReadAllBytes(kept).SequenceEqual(File.ReadAllBytes(original))
                    && replaced.Manifest.RemixOriginal is { Manifest.TableFileId: "base1" } ro
                    && ro.File == Path.GetFileName(kept)
                    && !TableInstaller
                        .Scan(settings)
                        .Any(t => t.TablePath.EndsWith(VpuRemix.KeptSuffix))
                    && VpuRemix.BaseCandidates(remix, game.Id, [replaced]) is [var fromKept]
                    && fromKept.SourcePath == kept
                    && fromKept.Exact,
                "remix in place: the original is kept beside it, isn't listed as a table, and is the remix's base"
            )
        );
        var updated = await TableInstaller.InstallAsync(
            pack,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            replacing: replaced,
            remixBases: [kept]
        );
        r.Add(
            (
                updated.Manifest.RemixOriginal?.File == Path.GetFileName(kept)
                    && File.ReadAllBytes(kept).SequenceEqual(File.ReadAllBytes(original))
                    && File.ReadAllBytes(updated.TablePath)
                        .SequenceEqual(File.ReadAllBytes(modded)),
                "remix in place: updating the remix keeps the original it replaced (not the old remix)"
            )
        );

        var back = TableInstaller.RevertRemix(updated, settings);
        r.Add(
            (
                back.TablePath == baseInst.TablePath
                    && File.ReadAllBytes(back.TablePath).SequenceEqual(File.ReadAllBytes(original))
                    && !File.Exists(updated.TablePath)
                    && !File.Exists(kept)
                    && back.Manifest is { TableFileId: "base1", RemixOriginal: null }
                    && TableInstaller
                        .Scan(settings)
                        .Any(t =>
                            t.TablePath == back.TablePath && t.Manifest.TableFileId == "base1"
                        ),
                "remix in place: putting the original back restores its file and catalog entry, and removes the remix"
            )
        );

        // Same file name as the original (the patch is named after it), and a VR DMD fix's backup: both come back.
        var sameDir = Path.Combine(dir, "pack-same");
        Directory.CreateDirectory(sameDir);
        File.Copy(
            dif,
            Path.Combine(sameDir, Path.GetFileNameWithoutExtension(back.TablePath) + ".dif")
        );
        var packSame = Path.Combine(dir, "remix-same.zip");
        ZipFile.CreateFromDirectory(sameDir, packSame);
        File.Copy(back.TablePath, VrDmdFix.BackupPath(back.TablePath));
        File.AppendAllText(back.TablePath, "dmd-fixed");
        var fixedBytes = File.ReadAllBytes(back.TablePath);
        var inPlace = await TableInstaller.InstallAsync(
            packSame,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            replacing: back,
            remixBases: [VpuRemix.PatchSource(back.TablePath)]
        );
        var sameKept = VpuRemix.KeptPath(back.TablePath);
        var sameOk =
            inPlace.TablePath == back.TablePath
            && File.ReadAllBytes(inPlace.TablePath).SequenceEqual(File.ReadAllBytes(modded))
            && File.ReadAllBytes(sameKept).SequenceEqual(fixedBytes)
            && File.Exists(VrDmdFix.BackupPath(sameKept))
            && !File.Exists(VrDmdFix.BackupPath(inPlace.TablePath));
        var sameBack = TableInstaller.RevertRemix(inPlace, settings);
        r.Add(
            (
                sameOk
                    && File.ReadAllBytes(sameBack.TablePath).SequenceEqual(fixedBytes)
                    && File.ReadAllBytes(VrDmdFix.BackupPath(sameBack.TablePath))
                        .SequenceEqual(File.ReadAllBytes(original))
                    && !File.Exists(sameKept),
                "remix in place under the original's file name: kept first, and put back with its DMD fix backup"
            )
        );

        // A plain table put over a remix in place: the kept original goes with it.
        File.Delete(VrDmdFix.BackupPath(sameBack.TablePath));
        var again = await TableInstaller.InstallAsync(
            pack,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            replacing: sameBack,
            remixBases: [sameBack.TablePath]
        );
        var plainOver = await TableInstaller.InstallAsync(
            original,
            game,
            baseFile,
            settings,
            _ => { },
            CancellationToken.None,
            replacing: again
        );
        r.Add(
            (
                again.Manifest.RemixOriginal != null
                    && !File.Exists(VpuRemix.KeptPath(sameBack.TablePath))
                    && plainOver.Manifest.RemixOriginal == null,
                "remix in place: installing a plain table over it removes the kept original"
            )
        );
        r.Add(
            (
                VpuRemix.Title(remix) == "v1.0 by Modder"
                    && VpuRemix.Title(untagged) == "Music MOD"
                    && VpuRemix.Title(
                        new VpsTableFile
                        {
                            Comment =
                                "Cabinet MOD. Requires VPU Patcher System and original table!",
                            Version = "1.0.0",
                            Authors = ["A"],
                        }
                    ) == "Cabinet MOD — v1.0.0 by A",
                "remix: menu titles use the comment's name, not the usual patching-tool notice"
            )
        );
        const long day = 86_400_000;
        var feb2025 = new DateTimeOffset(
            2025,
            2,
            10,
            0,
            0,
            0,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        var oldRemix = new VpsTableFile { Id = "r", CreatedAt = feb2025 };
        r.Add(
            (
                VpuRemix.Predates(oldRemix, new VpsTableFile { CreatedAt = feb2025 + 600 * day })
                    && !VpuRemix.Predates(
                        oldRemix,
                        new VpsTableFile { CreatedAt = feb2025 - 30 * day }
                    )
                    && !VpuRemix.Predates(
                        oldRemix,
                        new VpsTableFile { CreatedAt = feb2025 + day / 2 }
                    )
                    && !VpuRemix.Predates(oldRemix, new VpsTableFile())
                    && !VpuRemix.Predates(
                        new VpsTableFile(),
                        new VpsTableFile { CreatedAt = feb2025 }
                    )
                    && !VpuRemix.Predates(oldRemix, null)
                    && VpuRemix.ReleaseDate(oldRemix) == "10 Feb 2025",
                "remix: one released before its base's version was made from an older one (no dates: not flagged)"
            )
        );
        var edited = new VpsTableFile { Version = "2.4.1", UpdatedAt = feb2025 + 600 * day };
        var newer = new VpsTableFile { Version = "2.4.1", CreatedAt = feb2025 + 600 * day };
        r.Add(
            (
                !VpuRemix.Predates(oldRemix, edited) // a last-edit date isn't a release date
                    && !VpuRemix.Predates(oldRemix, new VpsTableFile { CreatedAt = feb2025 + day })
                    && VpuRemix.Predates(
                        oldRemix,
                        new VpsTableFile { CreatedAt = feb2025 + day + 1 }
                    )
                    && VpuRemix.PredatesYours(oldRemix, newer, "v2.4.1")
                    && !VpuRemix.PredatesYours(oldRemix, newer, "2.1") // your older copy: the entry doesn't date it
                    && !VpuRemix.PredatesYours(oldRemix, newer, null),
                "remix: only release dates count, and only for the version you have"
            )
        );

        // Failing after the remix was written over the original's file name: the original comes back, nothing kept.
        var plainBytes = File.ReadAllBytes(plainOver.TablePath);
        var manifestFile = Path.Combine(plainOver.Folder, "vrtm.json");
        File.SetAttributes(manifestFile, FileAttributes.ReadOnly);
        var failed = false;
        try
        {
            await TableInstaller.InstallAsync(
                packSame,
                game,
                remix,
                settings,
                _ => { },
                CancellationToken.None,
                replacing: plainOver,
                remixBases: [plainOver.TablePath]
            );
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed = true;
        }
        finally
        {
            File.SetAttributes(manifestFile, FileAttributes.Normal);
        }
        r.Add(
            (
                failed
                    && File.ReadAllBytes(plainOver.TablePath).SequenceEqual(plainBytes)
                    && !File.Exists(VpuRemix.KeptPath(plainOver.TablePath))
                    && !File.Exists(
                        Path.Combine(plainOver.Folder, TableInstaller.InstallingMarker)
                    ),
                "remix in place: an install failing after it overwrote the original puts the original back"
            )
        );

        // A ready-made remix .vpx under the original's name: the original's DMD fix backup goes with the original.
        var readyZip = Path.Combine(dir, "ready.zip");
        using (var z = ZipFile.Open(readyZip, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(modded, Path.GetFileName(plainOver.TablePath));
        }
        File.WriteAllBytes(VrDmdFix.BackupPath(plainOver.TablePath), plainBytes);
        var ready = await TableInstaller.InstallAsync(
            readyZip,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            replacing: plainOver
        );
        r.Add(
            (
                File.ReadAllBytes(ready.TablePath).SequenceEqual(File.ReadAllBytes(modded))
                    && !VrDmdFix.HasBackup(ready.TablePath)
                    && VrDmdFix.HasBackup(VpuRemix.KeptPath(ready.TablePath))
                    && ready.Manifest.RemixOriginal != null,
                "remix in place: a ready-made remix .vpx keeps the original, and doesn't take its DMD fix backup"
            )
        );

        // An older install of a remix (its manifest predates the remix tag): updating it keeps no "original".
        inst.Manifest.Features = [];
        var reUpdated = await TableInstaller.InstallAsync(
            pack,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            replacing: inst,
            remixBases: [original]
        );
        r.Add(
            (
                reUpdated.Manifest.RemixOriginal == null
                    && !File.Exists(VpuRemix.KeptPath(inst.TablePath)),
                "remix: updating a remix installed as its own table never keeps the old remix as the original"
            )
        );

        // A loose table in the tables folder: a remix can't take its place (nothing could be kept), so nothing changes.
        var loose = Path.Combine(settings.TablesDir!, "Loose Base.vpx");
        File.Copy(original, loose, overwrite: true);
        var looseTable = new InstalledTable(
            new TableManifest
            {
                GameId = game.Id,
                GameName = "Loose Base",
                TableFileId = "base1",
                VpxFile = "Loose Base.vpx",
            },
            loose,
            settings.TablesDir!
        )
        {
            SharedFolder = true,
        };
        var refused = false;
        try
        {
            await TableInstaller.InstallAsync(
                pack,
                game,
                remix,
                settings,
                _ => { },
                CancellationToken.None,
                replacing: looseTable,
                remixBases: [loose]
            );
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        r.Add(
            (
                refused
                    && !TableInstaller.CanKeepOriginal(looseTable, settings)
                    && File.ReadAllBytes(loose).SequenceEqual(File.ReadAllBytes(original)),
                "remix: a loose table can't be replaced by a remix (its original couldn't be kept); it stays"
            )
        );
        File.Delete(loose);

        // A bare .dif (My Tables → ADD A TABLE FILE) works the same.
        var bare = await TableInstaller.InstallAsync(
            dif,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            remixBases: [original]
        );
        r.Add(
            (
                File.ReadAllBytes(bare.TablePath).SequenceEqual(File.ReadAllBytes(modded)),
                "remix install: a bare .dif file is applied too"
            )
        );

        // A pack with a patch per original version: the one that fits is used.
        var pack2Dir = Path.Combine(dir, "pack2");
        Directory.CreateDirectory(pack2Dir);
        File.Copy(dif, Path.Combine(pack2Dir, "Base Table Remix (for 1.0).dif"));
        File.WriteAllBytes(
            Path.Combine(pack2Dir, "Base Table Remix.dif"),
            Diff(File.ReadAllBytes(other), File.ReadAllBytes(modded))
        );
        var pack2 = Path.Combine(dir, "remix2.zip");
        ZipFile.CreateFromDirectory(pack2Dir, pack2);
        var two = await TableInstaller.InstallAsync(
            pack2,
            game,
            remix,
            settings,
            _ => { },
            CancellationToken.None,
            remixBases: [original]
        );
        r.Add(
            (
                File.ReadAllBytes(two.TablePath).SequenceEqual(File.ReadAllBytes(modded))
                    && !Directory.EnumerateFiles(two.Folder, "*.dif").Any(),
                "remix install: with several patches, the one that fits the base is used"
            )
        );

        // Add a table file: a patch (or a zip of one) is a remix; a remix already made into a .vpx is a table.
        var vpxAndDif = Path.Combine(dir, "made.zip");
        using (var z = ZipFile.Open(vpxAndDif, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(modded, "Base Table Remix.vpx");
            z.CreateEntryFromFile(dif, "Base Table Remix.dif");
        }
        r.Add(
            (
                VpuRemix.IsPatchOnly(dif) == true
                    && VpuRemix.IsPatchOnly(pack) == true
                    && VpuRemix.IsPatchOnly(modded) == false
                    && VpuRemix.IsPatchOnly(vpxAndDif) == false,
                "remix: a picked .dif or patch-only zip is a remix; a .vpx (or a zip with one) is a table"
            )
        );

        return r;
    }
}
