using System.IO.Compression;
using VrLobby.Tests;
using VrTableManager.Lobby;
using VrTableManager.Models;
using VrTableManager.Services;

var failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

// 1. Catalog
var games = await VpsCatalog.LoadAsync(forceRefresh: false);
var vrGames = games.Where(g => g.TableFiles!.Any(t => VpsCatalog.IsVpx(t) && VpsCatalog.IsVrReady(t))).ToList();
var fpGames = games.Where(g => g.TableFiles!.Any(t => t.TableFormat == "FP")).ToList();
Console.WriteLine($"catalog: {games.Count} machines with VPX tables, {vrGames.Count} VR-ready, {fpGames.Count} with FP tables");
Check(games.Count > 500, "catalog loads");
Check(vrGames.Count > 50, "VR filter finds tables");
var hosts = games.SelectMany(g => g.TableFiles!).Where(VpsCatalog.IsVpx)
    .Select(t => t.Urls?.FirstOrDefault()?.Url).OfType<string>()
    .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host.Replace("www.", "") : "?")
    .GroupBy(h => h).OrderByDescending(g => g.Count()).Take(8);
Console.WriteLine("top VPX hosts: " + string.Join(", ", hosts.Select(h => $"{h.Key}={h.Count()}")));

// 2. Installer with a cabinet-style pack: Tables/x.vpx, VPinMAME/roms/rom.zip, Music/a.mp3, plus a VR variant.
var root = Path.Combine(Path.GetTempPath(), "vrtm-smoke-" + Guid.NewGuid().ToString("N")[..6]);
var src = Path.Combine(root, "src");
Directory.CreateDirectory(Path.Combine(src, "Tables"));
Directory.CreateDirectory(Path.Combine(src, "VPinMAME", "roms"));
Directory.CreateDirectory(Path.Combine(src, "Music", "TestTable"));
File.WriteAllText(Path.Combine(src, "Tables", "Test Table 1.0.vpx"), "desktop");
File.WriteAllText(Path.Combine(src, "Tables", "Test Table VR 1.0.vpx"), "vr");
File.WriteAllText(Path.Combine(src, "Tables", "Test Table.directb2s"), "b2s");
File.WriteAllText(Path.Combine(src, "Music", "TestTable", "theme.mp3"), "mp3");
using (var rom = ZipFile.Open(Path.Combine(src, "VPinMAME", "roms", "tt_l1.zip"), ZipArchiveMode.Create))
    rom.CreateEntry("rom.bin");
var pack = Path.Combine(root, "pack.zip");
ZipFile.CreateFromDirectory(src, pack);

var settings = new AppSettings { TablesDir = Path.Combine(root, "tables"), VpxDir = Path.Combine(root, "vpx") };
var game = new VpsGame { Id = "g1", Name = "Test Table", Manufacturer = "Williams", Year = 1992, RomFiles = [new VpsFile { Version = "tt_l1" }] };
var tf = new VpsTableFile { Id = "t1", TableFormat = "VPX", Version = "1.0", Features = ["VR"], Authors = ["Someone"] };
var inst = await TableInstaller.InstallAsync(pack, game, tf, settings, s => Console.WriteLine("   " + s), CancellationToken.None);
var folder = Path.Combine(settings.TablesDir, "Test Table (Williams 1992)");
Check(inst.Folder == folder, "table gets its own folder named Name (Manufacturer Year)");
Check(Path.GetFileName(inst.TablePath) == "Test Table VR 1.0.vpx", "VR build chosen as the main table");
Check(File.Exists(Path.Combine(folder, "pinmame", "roms", "tt_l1.zip")), "ROM moved to pinmame/roms");
Check(File.Exists(Path.Combine(folder, "music", "TestTable", "theme.mp3")), "music folder merged");
Check(File.Exists(Path.Combine(folder, "Test Table.directb2s")), "backglass kept");
Check(inst.MissingRoms.Count == 0, "no missing ROMs");
// Store engines (Pinball FX) may add their own entries on this PC; only look at file tables.
var scanned = TableInstaller.Scan(settings).Where(t => t.IsVpx).ToList();
Check(scanned.Count == 1 && scanned[0].Manifest.GameId == "g1", "scan finds the table via its manifest");
TableInstaller.Remove(scanned[0]);
Check(!Directory.Exists(folder), "remove deletes the table folder");

// 3. Path-traversal entries are ignored
var evil = Path.Combine(root, "evil.zip");
using (var z = ZipFile.Open(evil, ZipArchiveMode.Create)) { z.CreateEntry("../../escaped.txt"); z.CreateEntry("ok.vpx"); }
TableInstaller.Extract(evil, Path.Combine(root, "x"));
Check(!File.Exists(Path.Combine(root, "..", "escaped.txt")) && File.Exists(Path.Combine(root, "x", "ok.vpx")), "zip-slip entries skipped");

// 4. VR ini
VpxSetup.EnsureVrIni(settings);
var ini = File.ReadAllText(VpxSetup.VrIniPath(settings));
Check(ini.Contains("[PlayerVR]") && ini.Contains("AskToTurnOn = 0"), "VR ini enables VR");

// VPX 10.8.1 rejects -Minimized/-ExtMinimized with -Play ("must be used in conjunction with a
// command that uses the Win32 editor"), so the launch line must be just [-Ini x] -Play table.
var vrArgs = Launcher.VpxArgs(@"C:\vpx\VrTableManager-VR.ini", @"C:\t\x.vpx");
Check(vrArgs.SequenceEqual(["-Ini", @"C:\vpx\VrTableManager-VR.ini", "-Play", @"C:\t\x.vpx"]), "VR launch args: -Ini <vr ini> -Play <table>");
Check(Launcher.VpxArgs(null, @"C:\t\x.vpx").SequenceEqual(["-Play", @"C:\t\x.vpx"]), "desktop launch args: -Play <table>");
Check(!vrArgs.Any(a => a.Contains("Minimized", StringComparison.OrdinalIgnoreCase)), "no editor-only -Minimized/-ExtMinimized flags");

// Browser downloader against a local mock table site (login must not be interrupted; one click per step).
Check(BrowserDownloader.IsAuthUrl("https://vpuniverse.com/login/") && BrowserDownloader.IsAuthUrl("https://www.vpforums.org/index.php?app=core&section=login")
      && BrowserDownloader.IsAuthUrl("https://pinballnirvana.com/forums/sign-in"), "recognises login pages");
Check(!BrowserDownloader.IsAuthUrl("https://vpuniverse.com/files/file/123-authors-edition/"), "table pages with 'author' aren't login pages");
using (var site = new MockTableSite())
{
    var profile = Path.Combine(root, "browser-profile");
    await using var browser = new BrowserDownloader(profile, headless: true);
    var statuses = new List<string>();
    var got = await browser.DownloadAsync(site.FileUrl, Path.Combine(root, "dl"), ["VR"], s => { lock (statuses) statuses.Add(s); }, CancellationToken.None);
    var log = site.Log.ToList();
    Console.WriteLine("   mock site requests: " + string.Join(", ", log));
    var loginAt = log.IndexOf("login-page");
    var postAt = log.IndexOf("login-post");
    Check(loginAt >= 0 && postAt > loginAt && !log.Skip(loginAt + 1).Take(postAt - loginAt - 1).Any(),
        "waits on the login page until the user submits (no navigation mid-login)");
    Check(site.Count("login-page") == 1 && site.Count("login-post") == 1, "login page loaded once, submitted once");
    Check(site.Count("picker") == 1, "opens the file picker once");
    Check(site.Count("file-2") == 1 && site.Count("file-1") == 0, "requests the VR file exactly once, never the desktop one");
    Check(site.Count("nav-downloads") == 0, "never follows the Downloads menu");
    Check(Path.GetFileName(got) == "Test Table VR.zip" && File.Exists(got), "saves the downloaded file");
    await browser.DisposeAsync(); // a profile can only be open in one browser at a time

    // A ROM page with every version of the machine's ROM: take them all. A new downloader on the same
    // profile must still be signed in (sign-ins persist between app runs).
    await using var browser2 = new BrowserDownloader(profile, headless: true);
    var roms = await browser2.DownloadAllAsync(site.RomPageUrl, Path.Combine(root, "dl-roms"), _ => { }, CancellationToken.None);
    Check(site.Count("rom-page-signed-out") == 0, "sign-in remembered by the browser profile");
    Check(roms.Select(Path.GetFileName).Order().SequenceEqual(["afm_11.zip", "afm_113.zip", "afm_113b.zip"])
          && site.Count("rom-picker") == 1 && site.Count("rom-afm_113b") == 1, "downloads every file on a multi-file ROM page, once each");

    var afm = new InstalledTable(new TableManifest { Engine = TableManifest.Vpx, RomNames = ["afm_113b"] },
        Path.Combine(root, "afm", "afm.vpx"), Path.Combine(root, "afm"));
    var placedAll = RomInstaller.InstallAll(roms, afm, "afm_113b");
    Check(placedAll.Count == 3 && afm.MissingRoms.Count == 0
          && File.Exists(Path.Combine(afm.Folder, "pinmame", "roms", "afm_113.zip")), "installs the whole ROM family (parent beside the clone)");

    // Two downloads at once, each in its own tab and folder. Each file takes 4 s to start on the
    // mock site, so running them one after the other would take over 8 s.
    var before2 = site.Count("file-2");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var pair = await Task.WhenAll(
        browser2.DownloadAsync(site.FileUrl, Path.Combine(root, "dl-a"), ["VR"], _ => { }, CancellationToken.None),
        browser2.DownloadAsync(site.FileUrl2, Path.Combine(root, "dl-b"), ["VR"], _ => { }, CancellationToken.None));
    sw.Stop();
    Console.WriteLine($"   two parallel downloads took {sw.Elapsed.TotalSeconds:F1} s");
    Check(pair[0] != pair[1] && File.Exists(pair[0]) && File.Exists(pair[1]) && Path.GetDirectoryName(pair[0])!.EndsWith("dl-a") && Path.GetDirectoryName(pair[1])!.EndsWith("dl-b"),
        "parallel downloads each land in their own job's folder");
    Check(site.Count("file-2") - before2 == 2 && sw.Elapsed < TimeSpan.FromSeconds(7.5), "two downloads run at the same time (not one after the other)");

    Check(!BrowserDownloader.SameFilePage("https://www.vpforums.org/index.php", "https://www.vpforums.org/index.php?app=downloads&showfile=99")
          && BrowserDownloader.SameFilePage("https://vpforums.org/index.php?s=abc123&app=downloads&showfile=99", "https://www.vpforums.org/index.php?app=downloads&showfile=99")
          && BrowserDownloader.SameFilePage("https://vpuniverse.com/files/file/721-x/", "https://vpuniverse.com/files/file/721-x"),
        "same-page check uses the query (VPForums is all /index.php) but ignores session ids");

    // VPForums (IP.Board 3): once signed in, its Sign Out link contains "section=login". The download
    // must carry on after sign-in instead of waiting for a sign-in that already happened.
    using var vpfLimit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    string? vpfFile = null;
    try { vpfFile = await browser2.DownloadAsync(site.VpfFileUrl, Path.Combine(root, "dl-vpf"), [], _ => { }, vpfLimit.Token); }
    catch (OperationCanceledException) { }
    Console.WriteLine("   VPForums-style requests: " + string.Join(", ", site.Log.Where(l => l.StartsWith("vpf"))));
    Check(site.Count("vpf-login-post") == 1 && vpfFile != null && Path.GetFileName(vpfFile) == "afm_113b.zip",
        "VPForums: the download carries on after signing in");
}

// 5. URL classification
Check(BrowserDownloader.IsDirectFileUrl("https://github.com/a/b/releases/download/v1/t.zip"), "GitHub asset is direct");
Check(!BrowserDownloader.IsDirectFileUrl("https://vpuniverse.com/files/file/24896-rise-against/"), "VPU page goes through browser");

// 6. Latest VPX release
var rel = await VpxSetup.FindLatestAsync(CancellationToken.None);
Console.WriteLine($"latest VPX: {rel?.Tag} {rel?.AssetName} {rel?.Size / 1_048_576} MB");
Check(rel?.AssetName.StartsWith("VPinballX_BGFX-") == true, "finds BGFX Windows build");
Console.WriteLine(VpxSetup.DescribeOpenXrRuntime());

// 7. Pinball FX + UEVR engine
var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}";
var libs = PfxSetup.ParseLibraryFolders(vdf);
Check(libs.SequenceEqual([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"]), "parses Steam libraryfolders.vdf");
Check(PfxSetup.ParseInstallDir("\"AppState\"\n{\n\t\"appid\"\t\t\"2328760\"\n\t\"installdir\"\t\t\"Pinball FX\"\n}") == "Pinball FX", "parses appmanifest installdir");
var epic = PfxSetup.ParseEpicManifest("""{"DisplayName":"Pinball FX","InstallLocation":"E:\\Epic\\PinballFX","CatalogNamespace":"ns","CatalogItemId":"item","AppName":"app"}""");
Check(epic?.LaunchUri == "com.epicgames.launcher://apps/ns%3Aitem%3Aapp?action=launch&silent=true" && epic.InstallDir == @"E:\Epic\PinballFX", "parses Epic manifest");
Check(PfxSetup.ParseEpicManifest("""{"DisplayName":"Pinball FX3","InstallLocation":"x"}""") == null, "ignores Pinball FX3 (not Unreal)");
var pfx = Engines.For(TableManifest.PinballFx);
Check(pfx is PinballFxUevrEngine && !pfx.ManagesFiles, "PFX engine registered and store-owned");
var fake = new InstalledTable(new TableManifest { Engine = TableManifest.PinballFx, GameName = "Pinball FX" }, root, root);
try { TableInstaller.Remove(fake); Check(false, "refuses to remove store games"); }
catch (InvalidOperationException) { Check(Directory.Exists(root), "refuses to remove store games"); }
var detected = PfxSetup.Detect();
Console.WriteLine($"this PC: Pinball FX {(detected == null ? "not found" : $"{detected.Store} at {detected.InstallDir}")}; UEVR {(PfxSetup.AutoDetectUevr() ?? "not found")}; profile {(PfxSetup.HasProfile ? "present" : "absent")}");

// 8. VR lobby: library, A–Z navigation, filters, saved state (shared Core code the Godot lobby uses)
var fx = LobbyFixture.Create(Path.Combine(root, "lobby"));
var lobbySettings = AppSettings.LoadFrom(fx.Settings);
// Store games (Pinball FX / Pinball M) installed on this PC show up too; leave them out of the fixture counts.
var lib = LobbyLibrary.Load(lobbySettings).Where(t => ZenGames.For(t.Engine) == null).ToList();
Check(lib.Count == 103, $"lobby library: 100 VPX + hand-installed + 2 FP ({lib.Count})");
var mmT = lib.Single(t => t.Id == fx.MedievalMadness);
Check(mmT.Subtitle == "Visual Pinball X  ·  Williams 1997  ·  v2.1", "subtitle: " + mmT.Subtitle);
Check(mmT.MissingRoms.SequenceEqual(["mm_109c"]) && mmT.ImgUrl == LobbyFixture.ArtUrl, "missing ROM and artwork from the manifest");
Check(lib.Any(t => t.Title == "Attack From Mars Hand" && t.Engine == TableManifest.Vpx) && lib.Count(t => t.Badge == "FP") == 2,
    "hand-installed table uses its folder name; FP tables badged");
Check(LobbyNav.LetterOf("The Getaway") == "G" && LobbyNav.LetterOf("24") == "#" && LobbyNav.LetterOf("x-men") == "X",
    "letters: 'The' skipped, digits under #, case-insensitive");

var lobbyState = LobbyState.Load(Path.Combine(root, "lobby-state.json"));
var all = LobbyNav.Apply(lib, "All", lobbyState);
Check(all.Groups.Count == 27 && all.Groups[13].Label == "M", "A–Z rail has # + 26 letters");
var mIdx = all.Groups[13].Index;
Check(all.List[mIdx].Title == "Mars Attacks", "M group starts at Mars Attacks");
int at = 0, hops = 0;
while (LobbyNav.GroupAt(all.Groups, at) != 13 && hops < 30) { at = LobbyNav.Jump(all.Groups, at, 1); hops++; }
Check(at == mIdx && hops <= 13, $"reach M in {hops} jumps instead of {mIdx} single steps");
Check(LobbyNav.Jump(all.Groups, mIdx + 2, -1) == mIdx, "up inside a group goes to its first table");
Check(LobbyNav.GroupAt(all.Groups, LobbyNav.Jump(all.Groups, mIdx, -1)) == 12, "up at a group start goes to the previous letter");
Check(LobbyNav.GroupForKey(all.Groups, 'm') == 13 && LobbyNav.GroupForKey(all.Groups, '2') == 0, "typed keys map to letters, digits to #");
var letterRuns = all.List.Select(t => LobbyNav.Letters.IndexOf(LobbyNav.LetterOf(t.Title))).ToList();
Check(letterRuns.Zip(letterRuns.Skip(1)).All(p => p.First <= p.Second)
      && all.List.FindIndex(t => t.Title == "The Getaway") < all.List.FindIndex(t => t.Title == "Gorgar"),
    "every rail letter is one contiguous run (\"The Getaway\" sorts under G, digits first)");

// Cabinet art for the VR lobby: backglass from VPS, playfield from the table image, attract sound from the table folder
VpsGame G(string id, string? img, params string?[] b2s) =>
    new() { Id = id, ImgUrl = img, B2sFiles = b2s.Select(u => new VpsFile { ImgUrl = u }).ToList() };
Check(LobbyArt.BackglassUrl(G("a", "game.webp", null, "", "b2s1.webp", "b2s2.webp")) == "b2s1.webp"
      && LobbyArt.BackglassUrl(G("b", "game.webp")) == "game.webp" && LobbyArt.BackglassUrl(G("c", null)) == null,
    "backglass: first backglass picture, else the game's image, else none");
var bgIndex = LobbyArt.BackglassIndex([G("x", null, "bx.webp"), G("y", null), G("x", null, "dup.webp")]);
Check(bgIndex.Count == 1 && bgIndex["x"] == "bx.webp", "backglass index skips games with no picture and duplicate ids");
var store = new LobbyTable("pfx", TableManifest.PinballFx, "Pinball FX", "", "Zen", 0, "header.jpg", [],
    new InstalledTable(new TableManifest { Engine = TableManifest.PinballFx }, root, root));
Check(LobbyArt.PlayfieldUrl(store) == null && LobbyArt.BackglassFor(store, bgIndex) == "header.jpg"
      && LobbyArt.PlayfieldUrl(mmT) == LobbyFixture.ArtUrl && LobbyArt.BackglassFor(mmT, bgIndex) == null,
    "store games' landscape header goes on the backglass; VPX table image on the playfield");
var mmFolder = Path.GetDirectoryName(fx.MedievalMadness)!;
Check(LobbyArt.AttractSound(mmT) == null, "no sound file: no attract sound");
Directory.CreateDirectory(Path.Combine(mmFolder, "music"));
File.WriteAllBytes(Path.Combine(mmFolder, "music", "track01.ogg"), [1, 2, 3]);
File.WriteAllBytes(Path.Combine(mmFolder, "music", "Attract Loop.mp3"), [1, 2, 3]);
Check(Path.GetFileName(LobbyArt.AttractSound(mmT)) == "Attract Loop.mp3", "attract sound prefers names like \"attract\"");
Check(LobbyArt.AttractSound(lib.First(t => t.Badge == "FP")) == null, "no attract sound for Future Pinball (shared Tables folder)");
Check(VpsCatalog.LoadCached(Path.Combine(root, "missing.json")) == null, "no cached catalog: no backglass index, no network");

// Full-resolution playfield straight out of a .vpx (a synthetic one, so this doesn't depend on installed tables)
byte[] Rec(string tag, byte[] data) => [.. BitConverter.GetBytes(4 + data.Length), .. System.Text.Encoding.ASCII.GetBytes(tag), .. data];
byte[] Str(string s) => [.. BitConverter.GetBytes(s.Length), .. System.Text.Encoding.Latin1.GetBytes(s)];
byte[] I32(int v) => BitConverter.GetBytes(v);
byte[] ImageStream(string name, int w, int h, byte[] pic) =>
    [.. Rec("NAME", Str(name)), .. Rec("WDTH", I32(w)), .. Rec("HGHT", I32(h)), .. Rec("JPEG", []),
     .. Rec("NAME", Str(name)), .. Rec("SIZE", I32(pic.Length)), .. Rec("DATA", pic), .. Rec("ENDB", []), .. Rec("ENDB", [])];
byte[] fakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
var fakeVpx = Path.Combine(root, "fake table.vpx");
using (var cfb = OpenMcdf.RootStorage.Create(fakeVpx))
{
    var gs = cfb.CreateStorage("GameStg");
    void Put(string name, byte[] bytes) { using var s = gs.CreateStream(name); s.Write(bytes, 0, bytes.Length); }
    Put("GameData", [.. Rec("LEFT", I32(0)), .. Rec("IMAG", Str("ball")), .. Rec("TOPX", I32(0))]);  // names a ball texture, like some tables
    Put("Image0", ImageStream("ball", 128, 128, [9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9]));
    Put("Image1", ImageStream("Playfield_4K", 2048, 4096, fakePng));
    Put("Image2", ImageStream("LightOverlay", 3000, 6000, [7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7]));
}
var fakeImages = VpxImages.ListImages(fakeVpx);
Check(VpxImages.PlayfieldImageName(fakeVpx) == "ball" && fakeImages.Count == 3 && fakeImages[1] is { Name: "Playfield_4K", Width: 2048, Height: 4096 },
    "reads the table's playfield name and every image's name and size from a .vpx");
Check(VpxImages.ExtractPlayfield(fakeVpx)?.SequenceEqual(fakePng) == true,
    "named playfield isn't playfield-shaped: picks the playfield-named portrait image (not a bigger light overlay)");
var artCache = Path.Combine(root, "img-cache");
var cachedPf = VpxImages.CachedPlayfield(fakeVpx, artCache);
Check(cachedPf != null && File.ReadAllBytes(cachedPf).SequenceEqual(fakePng) && VpxImages.CachedPlayfield(fakeVpx, artCache) == cachedPf,
    "extracted once into the image cache, then reused");
File.WriteAllText(Path.Combine(root, "not a table.vpx"), "this is not a compound file");
Check(VpxImages.CachedPlayfield(Path.Combine(root, "not a table.vpx"), artCache) == null, "a damaged .vpx falls back quietly (no crash)");
Check(LobbyArt.PlayfieldSource(mmT) == LobbyArt.VpxScheme + fx.MedievalMadness, "VPX tables take their playfield from the table file first");
// A table's own VR cabinet: a synthetic .vpx with two cabinet parts (raw and zlib-compressed meshes),
// a playfield bolt and a VR-room floor that must be left out, and a 10.8-style nested material.
byte[] F32(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
byte[] WStr(string s) => [.. BitConverter.GetBytes(s.Length), .. System.Text.Encoding.Unicode.GetBytes(s)];
byte[] Zip(byte[] raw) { using var ms = new MemoryStream(); using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal)) z.Write(raw); return ms.ToArray(); }
byte[] Prim(string name, float[] verts8, ushort[] idx, bool compress, float[] pos, float[] size, float rotZ = 0, float traX = 0, string? mat = null)
{
    var vb = F32(verts8);
    var ib = idx.SelectMany(BitConverter.GetBytes).ToArray();
    var recs = new List<byte>(BitConverter.GetBytes(19)); // item type: primitive
    recs.AddRange(Rec("VPOS", F32(pos[0], pos[1], pos[2], 0)));
    recs.AddRange(Rec("VSIZ", F32(size[0], size[1], size[2], 0)));
    recs.AddRange(Rec("RTV2", F32(rotZ)));
    recs.AddRange(Rec("RTV3", F32(traX)));
    recs.AddRange(Rec("NAME", WStr(name)));
    if (mat != null) recs.AddRange(Rec("MATR", Str(mat)));
    recs.AddRange(Rec("U3DM", I32(1)));
    recs.AddRange(Rec("M3VN", I32(verts8.Length / 8)));
    if (compress) { var z = Zip(vb); recs.AddRange(Rec("M3CY", I32(z.Length))); recs.AddRange(Rec("M3CX", z)); }
    else recs.AddRange(Rec("M3DX", vb));
    recs.AddRange(Rec("M3FN", I32(idx.Length)));
    if (compress) { var z = Zip(ib); recs.AddRange(Rec("M3CJ", I32(z.Length))); recs.AddRange(Rec("M3CI", z)); }
    else recs.AddRange(Rec("M3DI", ib));
    recs.AddRange(Rec("ENDB", []));
    return [.. recs];
}
// x,y,z, nx,ny,nz, u,v
float[] tri = [0, 0, 0, 0, 0, 1, 0, 0,   1, 0, 0, 0, 0, 1, 1, 0,   0, 1, 0, 0, 0, 1, 0, 1];
var cabVpx = Path.Combine(root, "cabinet table.vpx");
using (var cfb = OpenMcdf.RootStorage.Create(cabVpx))
{
    var gs = cfb.CreateStorage("GameStg");
    void Put(string name, byte[] bytes) { using var s = gs.CreateStream(name); s.Write(bytes, 0, bytes.Length); }
    byte[] chrome = [.. Rec("TYPE", I32(1)), .. Rec("NAME", Str("Chrome")), .. Rec("ROUG", F32(0.9f)), .. Rec("BASE", I32(0x00C8D2DC)), .. Rec("ENDB", [])];
    Put("GameData", [.. Rec("LEFT", F32(0)), .. Rec("TOPX", F32(0)), .. Rec("RGHT", F32(952)), .. Rec("BOTM", F32(2162)), .. Rec("MATR", chrome)]);
    // Body: scaled ×2, translated 10 along x, then turned 90° about z, then placed at (100, 200, 0).
    Put("GameItem0", Prim("PinCab_Cabinet", tri, [0, 1, 2], compress: false, pos: [100, 200, 0], size: [2, 2, 2], rotZ: 90, traX: 10, mat: "Chrome"));
    Put("GameItem1", Prim("PinCab_Legs", tri, [0, 1, 2], compress: true, pos: [100, 200, -1000], size: [100, 100, 100]));
    Put("GameItem2", Prim("Bolt12", tri, [0, 1, 2], compress: false, pos: [400, 900, 10], size: [5, 5, 5]));
    Put("GameItem3", Prim("VR_Room_Floor", tri, [0, 1, 2], compress: false, pos: [100, 200, -1100], size: [300, 300, 1]));
}
var cab = VpxCabinet.Load(cabVpx, frontZ: 0.7f);
Check(cab != null && cab.Parts.Select(p => p.Name).OrderBy(n => n).SequenceEqual(["PinCab_Cabinet", "PinCab_Legs"]),
    "VR cabinet: cabinet parts read (raw and compressed meshes); playfield bolt and room floor left out: " + string.Join(", ", cab?.Parts.Select(p => p.Name) ?? []));
if (cab != null)
{
    var body = cab.Parts.Single(p => p.Name == "PinCab_Cabinet");
    System.Numerics.Vector3 V(int i) => new(body.Positions[i * 3], body.Positions[i * 3 + 1], body.Positions[i * 3 + 2]);
    var s = VpxCabinet.VpuToMetres;
    // VPX: (1,0,0)·2 + (10,0,0) → rotate 90° → (0,12) vs (0,10): +2 along VPX y = towards the player = lobby +z.
    var e1 = V(1) - V(0);
    var e2 = V(2) - V(0);
    Check(Math.Abs(e1.X) < 1e-5 && Math.Abs(e1.Y) < 1e-5 && Math.Abs(e1.Z - 2 * s) < 1e-5 && Math.Abs(e2.X + 2 * s) < 1e-5 && Math.Abs(e2.Z) < 1e-5,
        $"VPX transform order (scale → translate → rotate → position) and axis mapping: e1={e1}, e2={e2}");
    Check(body.Indices.SequenceEqual([0, 2, 1]), "triangle winding flipped for the axis swap");
    Check(Math.Abs(body.Normals[1] - 1) < 1e-5, "normals follow the axes (VPX up → lobby up)");
    Check(body.Material is { Name: "Chrome", IsMetal: true } m && Math.Abs(m.Roughness - 0.9f) < 1e-6 && m.Rgb.R > 0.85f && m.Rgb.B < 0.8f,
        "10.8-style nested material read (metal, roughness, colour 0x00BBGGRR)");
    Check(Math.Abs(cab.Min.Y) < 1e-5 && Math.Abs(cab.Max.Z - 0.7f) < 1e-5 && Math.Abs(cab.Min.X + cab.Max.X) < 1e-5,
        "cabinet anchored: floor at 0, front edge at 0.7 m, centred across");
}
File.WriteAllText(Path.Combine(root, "desktop only.vpx"), "not a compound file");
Check(VpxCabinet.Load(Path.Combine(root, "desktop only.vpx")) == null && VpxCabinet.Load(fakeVpx) == null,
    "no VR cabinet (or a damaged file): null, so the lobby keeps its generic cabinet");
var realCab = Directory.Exists(@"C:\Users\mikem\VPinball\Tables") ? Directory.GetFiles(@"C:\Users\mikem\VPinball\Tables", "*.vpx", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("Medieval Madness")) : null;
if (realCab != null)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var model = VpxCabinet.Load(realCab);
    var size = model == null ? default : model.Max - model.Min;
    Console.WriteLine($"this PC's Medieval Madness cabinet: {model?.Parts.Count} parts, {size.X:F2}x{size.Y:F2}x{size.Z:F2} m, {sw.ElapsedMilliseconds} ms");
    Check(model != null && size.X is > 0.6f and < 0.9f && size.Y is > 1.7f and < 2.2f && model.Parts.Any(p => p.Name == "PinCab_Backglass"),
        "a real VR table's cabinet comes out real-size (≈0.7 m wide, ≈1.9 m tall) with its backglass");
}
var realAfm = Directory.Exists(@"C:\Users\mikem\VPinball\Tables") ? Directory.GetFiles(@"C:\Users\mikem\VPinball\Tables", "*.vpx", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("Attack from Mars")) : null;
if (realAfm != null)
{
    var pf = VpxImages.ExtractPlayfield(realAfm);
    var pick = VpxImages.PickPlayfield(VpxImages.ListImages(realAfm), VpxImages.PlayfieldImageName(realAfm));
    Console.WriteLine($"this PC's Attack from Mars: playfield '{pick?.Name}' {pick?.Width}x{pick?.Height}, {pf?.Length / 1024} KB");
    Check(pf != null && pick!.Height >= 2048 && pf[0] == 0x89, "a real table gives its full-resolution playfield (≥ 2048 px tall)");
}
if (VpsCatalog.LoadCached() is { } cachedGames)
{
    var realIndex = LobbyArt.BackglassIndex(cachedGames);
    Console.WriteLine($"cached VPS catalog: {cachedGames.Count} games, {realIndex.Count} with a backglass picture");
    Check(realIndex.Count > 1000, "this PC's cached VPS catalog gives backglass pictures for most games");
}
var maker = LobbyNav.Apply(lib, "Manufacturer", lobbyState);
Check(maker.Groups.Select(g => g.Label).SequenceEqual(["Bally", "Data East", "Gottlieb", "Stern", "Unknown", "Williams"]), "manufacturer groups");
var decade = LobbyNav.Apply(lib, "Decade", lobbyState);
Check(decade.Groups.Select(g => g.Label).SequenceEqual(["1970s", "1980s", "1990s", "2000s", "2010s", "2020s", "Unknown"]), "decade groups, no year last");
Check(LobbyNav.Apply(lib, "Future Pinball", lobbyState).List.Count == 2, "Future Pinball filter");
lobbyState.ToggleFavourite(all.List[5].Id);
lobbyState.ToggleFavourite(all.List[50].Id);
Check(LobbyNav.Apply(lib, "Favourites", lobbyState).List.Count == 2, "favourites filter");
lobbyState.MarkPlayed(all.List[3].Id);
lobbyState.MarkPlayed(all.List[9].Id);
lobbyState.MarkPlayed(all.List[3].Id);
Check(LobbyNav.Apply(lib, "Recently played", lobbyState).List.Select(t => t.Id).SequenceEqual([all.List[3].Id, all.List[9].Id]),
    "recently played: newest first, no duplicates");
lobbyState.Filter = "Decade";
lobbyState.View = "wall";
lobbyState.Save();
var reloaded = LobbyState.Load(Path.Combine(root, "lobby-state.json"));
Check(reloaded.Favourites.Count == 2 && reloaded.Recent.Count == 2 && reloaded.Filter == "Decade" && reloaded.View == "wall", "lobby state survives a restart");
Check(LobbyNav.RepeatInterval(0) == 0.3 && LobbyNav.RepeatInterval(3) == 0.05, "stick repeat speeds up from 0.3 s to 0.05 s");

// Lobby wording for play stats and empty filters
var t0 = new DateTime(2026, 10, 18, 20, 0, 0);
Check(LobbyText.Duration(0.4) == null && LobbyText.Duration(45) == "45 min" && LobbyText.Duration(200) == "3 h 20 min" && LobbyText.Duration(120) == "2 h",
    "time played formatting");
Check(LobbyText.Ago(t0.AddSeconds(-30), t0) == "just now" && LobbyText.Ago(t0.AddMinutes(-30), t0) == "30 min ago"
      && LobbyText.Ago(t0.AddHours(-5), t0) == "today" && LobbyText.Ago(t0.AddHours(-22), t0) == "yesterday"
      && LobbyText.Ago(t0.AddDays(-4), t0) == "4 days ago", "last-played formatting");
var textStats = new PlayStats();
textStats.RecordPlay(@"C:\T\a.vpx", t0.AddMinutes(-30), TimeSpan.FromMinutes(45));
Check(LobbyText.Stats(textStats, @"C:\T\a.vpx", t0) == "Played 1×  ·  45 min  ·  last played 30 min ago" && LobbyText.Stats(textStats, @"C:\T\b.vpx", t0) == "Not played yet",
    "stats line");
Check(LobbyText.Empty("Most played", 5).StartsWith("No plays counted") && LobbyText.Empty("Pinball M", 5).Contains("isn't installed")
      && LobbyText.Empty("All", 0).StartsWith("Install tables"), "empty-filter messages");

// Height adjustment (grip + stick in the lobby)
var h = 0.0;
for (var i = 0; i < 90; i++) h = LobbyHeight.Step(h, 1.0, 1 / 90.0);   // full stick up for 1 s
Check(Math.Abs(h - 0.3) < 1e-9, $"full stick moves 30 cm per second ({h:F3} m)");
Check(LobbyHeight.Step(h, 0.1, 1.0) == h, "stick inside the dead zone doesn't drift");
for (var i = 0; i < 900; i++) h = LobbyHeight.Step(h, -1.0, 1 / 90.0);  // 10 s down
Check(h == -LobbyHeight.Max, "clamped at −60 cm");
Check(LobbyHeight.Clamp(double.NaN) == 0 && LobbyHeight.Clamp(5) == 0.6, "bad saved values are clamped");
Check(LobbyHeight.Format(0.12) == "Height +12 cm" && LobbyHeight.Format(-0.051) == "Height −5 cm" && LobbyHeight.Format(0.001) == "Height: default",
    "readout text");
reloaded.HeightOffset = 0.12;
reloaded.Save();
Check(LobbyState.Load(Path.Combine(root, "lobby-state.json")).HeightOffset == 0.12, "height survives a restart");
File.WriteAllText(Path.Combine(root, "bad-height.json"), """{"HeightOffset": 9}""");
Check(LobbyState.Load(Path.Combine(root, "bad-height.json")).HeightOffset == LobbyHeight.Max, "out-of-range saved height is clamped on load");

// 9. Headless play mode the lobby uses: launch through the engine, wait for exit, start the lobby again.
//    A harmless system exe stands in for VPX and for the lobby.
var playArgs = PlaySession.BuildArgs(fx.MedievalMadness, vr: true, fx.Settings, @"C:\Lobby Dir\lobby.exe", ["--", "--select=C:\\T (1)\\x.vpx"]);
var parsed = PlaySession.Parse([.. playArgs]);
Check(parsed.TablePath == fx.MedievalMadness && parsed.Vr && parsed.SettingsPath == fx.Settings
      && parsed.ReturnExe == @"C:\Lobby Dir\lobby.exe" && parsed.ReturnArgs.SequenceEqual(["--", "--select=C:\\T (1)\\x.vpx"]), "play args round-trip");
var stand = Path.Combine(Environment.SystemDirectory, "where.exe");
File.Copy(stand, Path.Combine(fx.VpxDir, "VPinballX_BGFX64.exe"), overwrite: true);
// Find the repo from the test's own folder, or from the current folder when built elsewhere (-o).
string? FindRepo(string? start)
{
    while (start != null && !Directory.Exists(Path.Combine(start, "VrTableManager.Core"))) start = Path.GetDirectoryName(start);
    return start;
}
var repo = FindRepo(AppContext.BaseDirectory) ?? FindRepo(Directory.GetCurrentDirectory());
var managerExe = Path.Combine(repo!, "VrTableManager", "bin", "Debug", "net10.0", "VrTableManager.exe");
var logBefore = File.Exists(PlaySession.LogPath) ? File.ReadAllLines(PlaySession.LogPath).Length : 0;
var psi = new System.Diagnostics.ProcessStartInfo(managerExe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var a in PlaySession.BuildArgs(fx.MedievalMadness, vr: true, fx.Settings, stand, ["/Q", "where.exe"])) psi.ArgumentList.Add(a);
using (var play = System.Diagnostics.Process.Start(psi)!)
{
    await play.WaitForExitAsync();
    Check(play.ExitCode == 0, "VrTableManager.exe --play exits cleanly");
}
var newLog = File.ReadAllLines(PlaySession.LogPath).Skip(logBefore).ToList();
newLog.ForEach(l => Console.WriteLine("   log: " + l));
Check(newLog.Any(l => l.Contains("start Visual Pinball X") && l.Contains("vr=True")) && newLog.Any(l => l.Contains("table exited"))
      && newLog.Any(l => l.Contains("returned to")), "launches through the VPX engine, waits for exit, then starts the lobby again");
Check(File.ReadAllText(VpxSetup.VrIniPath(lobbySettings)).Contains("AskToTurnOn = 0"), "VR ini written for the lobby launch");

// Updates: detect a newer table version, update in place, keep NVRAM, drop the old .vpx.
{
    var us = new AppSettings { TablesDir = Path.Combine(root, "upd-tables"), VpxDir = Path.Combine(root, "vpx") };
    var ug = new VpsGame { Id = "g2", Name = "Upd Table", Manufacturer = "Bally", Year = 1980 };
    var v10 = new VpsTableFile { Id = "t2", TableFormat = "VPX", Version = "1.0", Urls = [new VpsUrl { Url = "https://example.com/t2" }] };
    var p10 = Path.Combine(root, "upd10.zip");
    using (var z = ZipFile.Open(p10, ZipArchiveMode.Create)) z.CreateEntry("Upd Table v1.0.vpx");
    var old = await TableInstaller.InstallAsync(p10, ug, v10, us, _ => { }, CancellationToken.None);
    var nvram = Path.Combine(old.Folder, "pinmame", "nvram", "upd.nv");
    Directory.CreateDirectory(Path.GetDirectoryName(nvram)!);
    File.WriteAllText(nvram, "high scores");

    ug.TableFiles = [new VpsTableFile { Id = "t2", TableFormat = "VPX", Version = "v1.0", Urls = v10.Urls }];
    Check(TableUpdates.Find(TableInstaller.Scan(us), [ug]).Count == 0, "same version (v-prefix ignored) is not an update");

    var v11 = new VpsTableFile { Id = "t2", TableFormat = "VPX", Version = "1.1", Urls = v10.Urls };
    ug.TableFiles = [v11];
    var found = TableUpdates.Find(TableInstaller.Scan(us).Where(t => t.IsVpx), [ug]);
    Check(found.Count == 1 && found[0].Label == "v1.0 → v1.1", "newer VPS version is flagged as an update");

    var p11 = Path.Combine(root, "upd11.zip");
    using (var z = ZipFile.Open(p11, ZipArchiveMode.Create)) z.CreateEntry("Upd Table v1.1.vpx");
    var upd = await TableInstaller.InstallAsync(p11, ug, v11, us, _ => { }, CancellationToken.None, found[0].Table);
    Check(File.Exists(upd.TablePath) && !File.Exists(old.TablePath), "update replaces the old .vpx");
    Check(File.Exists(nvram), "update keeps NVRAM (high scores)");
    Check(TableUpdates.Find(TableInstaller.Scan(us).Where(t => t.IsVpx), [ug]).Count == 0, "no update left after updating");
}

Check(BamCheck.ParseLatest("<p>BAM version: v.1.5-412</p>") == "1.5-412", "reads BAM version from ravarcade.pl format");
var bamLive = await BamCheck.LatestAsync(CancellationToken.None);
Console.WriteLine($"latest BAM (ravarcade.pl): {bamLive}");
Check(bamLive != null, "reads the live BAM version");
var uevrRel = await UevrSetup.FindLatestAsync(CancellationToken.None);
Console.WriteLine($"latest UEVR: {uevrRel?.Tag} {uevrRel?.Size / 1_048_576} MB");
Check(uevrRel?.Url.EndsWith("/UEVR.zip") == true, "finds the latest UEVR release");

// ---- ROMs ----
{
    // cGameName found even when it straddles the 4 MB read-chunk boundary.
    var vpxPath = Path.Combine(root, "rom-detect.vpx");
    var script = "Const cGameName = \"mm_109c\"";
    using (var f = File.Create(vpxPath))
    {
        f.Write(new byte[(4 << 20) - 10]);
        f.Write(System.Text.Encoding.ASCII.GetBytes(script));
        f.Write(new byte[1000]);
    }
    Check(RomDetector.FromVpx(vpxPath) == "mm_109c", "reads cGameName from the table script (across a chunk boundary)");
    Check(RomDetector.FromScript("x = 1") == null, "no cGameName → no ROM");

    var rt = new InstalledTable(new TableManifest { Engine = TableManifest.Vpx, RomNames = ["mm_109", "mm_109c"] },
        Path.Combine(root, "romtable", "t.vpx"), Path.Combine(root, "romtable"));
    Directory.CreateDirectory(rt.Folder);
    Check(rt.MissingRoms.Count == 2, "no ROM yet: any of VPS's candidates will do");

    // The download is the ROM itself, under a different name.
    var plain = Path.Combine(root, "Medieval Madness 1.09c.zip");
    using (var z = ZipFile.Open(plain, ZipArchiveMode.Create)) z.CreateEntry("mm_109c.u6");
    var placed = RomInstaller.Install(plain, rt, "mm_109c");
    Check(placed.Count == 1 && Path.GetFileName(placed[0]) == "mm_109c.zip" && rt.MissingRoms.Count == 0,
        "ROM download saved as pinmame/roms/<rom>.zip; table no longer needs one");

    // A pack with several ROM zips inside: only the one asked for is installed.
    var rt2 = rt with { Folder = Path.Combine(root, "romtable2") };
    var pack2 = Path.Combine(root, "rom-pack.zip");
    using (var z = ZipFile.Open(pack2, ZipArchiveMode.Create))
    {
        foreach (var n in new[] { "mm_109.zip", "mm_109c.zip" })
            using (var e = z.CreateEntry(n).Open()) e.Write([1, 2, 3]);
    }
    var placed2 = RomInstaller.Install(pack2, rt2, "mm_109c");
    Check(placed2.Select(Path.GetFileName).SequenceEqual(["mm_109c.zip"]), "ROM pack: installs only the requested ROM");

    // Table install narrows VPS's ROM list to the one the script names.
    var rs = new AppSettings { TablesDir = Path.Combine(root, "rom-tables"), VpxDir = Path.Combine(root, "vpx") };
    var rg = new VpsGame { Id = "g3", Name = "Rom Table", Manufacturer = "Williams", Year = 1997,
        RomFiles = [new VpsFile { Version = "mm_109" }, new VpsFile { Version = "mm_109c" }, new VpsFile { Version = "mm_10" }] };
    var rpack = Path.Combine(root, "romtable-pack.zip");
    using (var z = ZipFile.Open(rpack, ZipArchiveMode.Create))
    using (var e = new StreamWriter(z.CreateEntry("Rom Table.vpx").Open())) e.Write("...binary...Const cGameName = \"mm_109c\"...LoadVPM \"01560000\", \"WPC.VBS\", 3.26...");
    var rinst = await TableInstaller.InstallAsync(rpack, rg, new VpsTableFile { Id = "t3", TableFormat = "VPX" }, rs, _ => { }, CancellationToken.None);
    Check(rinst.Manifest.RomNames.SequenceEqual(["mm_109c"]), "install records the exact ROM from the script");
}

// ---- Headset check (against this PC's real OpenXR runtime) ----
{
    var real = AppSettings.Load();
    var hsNow = await HeadsetCheck.CheckAsync(real, TimeSpan.FromSeconds(20));
    Console.WriteLine($"   this PC: loader {HeadsetCheck.FindLoader(real) ?? "none"}; headset check → {hsNow.State} ({hsNow.Detail})");
    Check(HeadsetCheck.FindLoader(real) == null || hsNow.State != HeadsetState.Unknown, "headset check gets a real answer from the VR runtime");
    var none = await HeadsetCheck.CheckAsync(new AppSettings { VpxDir = Path.Combine(root, "nothing"), UevrDir = "" }, TimeSpan.FromSeconds(5));
    Check(none.State == HeadsetState.Unknown, "no OpenXR loader → don't block the launch");
}

// ---- Pinball M ----
Check(Engines.For(TableManifest.PinballM) is PinballMUevrEngine pm && !pm.ManagesFiles && pm.Game.SteamAppId == "2337640",
    "Pinball M engine registered (Steam 2337640, store-owned)");
Check(PfxSetup.ParseEpicManifest("""{"DisplayName":"Pinball FX Midnight","InstallLocation":"E:\\M","CatalogNamespace":"a","CatalogItemId":"b","AppName":"c"}""", ZenGames.PinballM) != null
      && PfxSetup.ParseEpicManifest("""{"DisplayName":"Pinball FX Midnight","InstallLocation":"E:\\M"}""") == null,
    "Epic 'Pinball FX Midnight' is Pinball M, not Pinball FX");
{
    var zenDir = Path.Combine(root, "zen", "Pinball M");
    Directory.CreateDirectory(Path.Combine(zenDir, "PinballM", "Binaries", "Win64"));
    File.WriteAllText(Path.Combine(zenDir, "PinballM", "Binaries", "Win64", "PinballM-Win64-Shipping.exe"), "");
    Check(PfxSetup.FindShippingExe(zenDir) == "PinballM-Win64-Shipping", "reads the game's process name from Binaries\\Win64");
}
var pmDetected = PfxSetup.Detect(ZenGames.PinballM);
Console.WriteLine($"   this PC: Pinball M {(pmDetected == null ? "not installed" : $"found ({pmDetected.Store}) at {pmDetected.InstallDir}, process {PfxSetup.ResolveProcessName(ZenGames.PinballM)}")}");

// ---- Favourites and play stats (shared by the manager and the lobby) ----
{
    var sp = Path.Combine(root, "stats", "play-stats.json");
    var tA = Path.Combine(root, "A.vpx"); var tB = Path.Combine(root, "B.vpx"); var tC = Path.Combine(root, "C.vpx");
    PlayStats.Mutate(s => { s.RecordPlay(tA, DateTime.Now.AddHours(-3)); s.RecordPlay(tA, DateTime.Now.AddHours(-2)); s.RecordPlay(tB, DateTime.Now.AddHours(-1)); s.SetFavourite(tC, true); }, sp);
    var st = PlayStats.Load(sp);
    Check(st.MostPlayed().SequenceEqual([tA, tB]) && st.Recent().SequenceEqual([tB, tA]) && st.Favourites().SequenceEqual([tC]),
        "most played / recently played / favourites");
    Check(st.IsFavourite(tC.ToUpperInvariant()), "table paths compare case-insensitively");

    // Several processes counting at once (manager + lobby launcher) mustn't lose plays.
    await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => PlayStats.Mutate(s => s.RecordPlay(tB, DateTime.Now), sp))));
    Check(PlayStats.Load(sp).PlaysOf(tB) == 21, "concurrent play counting loses nothing (21 plays)");
    PlayStats.Mutate(s => s.AddTime(tB, TimeSpan.FromMinutes(90)), sp);
    Check(Math.Abs(PlayStats.Load(sp).MinutesOf(tB) - 90) < 0.01, "time played adds up");

    // An old lobby-state.json with favourites/recent moves them into the shared stats.
    var legacyDir = Path.Combine(root, "legacy");
    Directory.CreateDirectory(legacyDir);
    var legacy = Path.Combine(legacyDir, "lobby-state.json");
    File.WriteAllText(legacy, $$"""{"Favourites":[{{JsonEsc(tA)}}],"Recent":[{{JsonEsc(tB)}},{{JsonEsc(tA)}}],"Filter":"Favourites","View":"wall","HeightOffset":0.05}""");
    var migrated = LobbyState.Load(legacy);
    Check(migrated.IsFavourite(tA) && migrated.Recent.SequenceEqual([tB, tA]) && migrated.View == "wall",
        "old lobby favourites and recents carried over");
    Check(!File.ReadAllText(legacy).Contains("\"Favourites\":") && !File.ReadAllText(legacy).Contains("\"Recent\":") && PlayStats.Load(Path.Combine(legacyDir, "play-stats.json")).IsFavourite(tA),
        "lobby-state.json no longer keeps its own favourites");
    Check(LobbyNav.Filters.Contains("Most played") && LobbyNav.Filters.Contains("Pinball M"), "lobby has Most played and Pinball M filters");
}

// ---- Pinball settings for UEVR (run on a copy of this PC's real Pinball M profile when there is one) ----
{
    var dir = Path.Combine(root, "uevr");
    Directory.CreateDirectory(dir);
    var cfg = Path.Combine(dir, "config.txt");
    var bak = Path.Combine(dir, "config.vrtm-original.txt");
    var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealVRMod", "PinballM-Win64-Shipping", "config.txt");
    if (File.Exists(real)) File.Copy(real, cfg);
    else File.WriteAllLines(cfg, ["Frontend_RequestedRuntime=openvr_api.dll", "VR_DecoupledPitch=false", "VR_ControllersAllowed=true", "UI_Distance=2.000000", "VR_SnapTurn=false"]);
    Console.WriteLine($"   UEVR profile test using {(File.Exists(real) ? "a copy of this PC's Pinball M profile" : "a sample profile")}");

    var before = File.ReadAllLines(cfg).Where(l => l.Contains('=')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
    var r1 = UevrPinballProfile.ApplyTo(cfg, bak);
    var after = File.ReadAllLines(cfg).Where(l => l.Contains('=')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
    var ours = UevrPinballProfile.Settings.Select(s => s.Key).ToHashSet();
    Check(r1.BackedUp && File.ReadAllLines(bak).Where(l => l.Contains('=')).Count() == before.Count, "first apply backs up the original profile");
    Check(UevrPinballProfile.Settings.All(s => after[s.Key] == s.Value), "all pinball settings written");
    Check(before.Where(kv => !ours.Contains(kv.Key)).All(kv => after.TryGetValue(kv.Key, out var v) && v == kv.Value)
          && after.Count == before.Keys.Union(ours).Count(), "every other UEVR setting left exactly as it was");
    var r2 = UevrPinballProfile.ApplyTo(cfg, bak);
    Check(!r2.BackedUp && r2.Changed.Count == 0, "second apply changes nothing and keeps the first backup");
    Check(UevrPinballProfile.RuntimeFrom(cfg) == (before.GetValueOrDefault("Frontend_RequestedRuntime", "").Contains("openxr") ? "OpenXR" : "OpenVR"),
        "reads which runtime UEVR's injector last used");
    Console.WriteLine($"   pinball settings changed {r1.Changed.Count} of {ours.Count} keys: {string.Join(", ", r1.Changed)}");
}
Console.WriteLine($"   gamepad connected: {Gamepad.IsConnected()?.ToString() ?? "unknown"}");

// ---- ROM sources and tables that don't use PinMAME ----
{
    Check(!RomSources.IsUsable("https://sternpinball.com/?post_type=game_code&s=metallica", "MTL180LE.BIN")
          && !RomSources.IsUsable("https://wp.sternpinball.com/?post_type=game_code&s=x-men+LE+1.51", "xmn_151h")
          && RomSources.IsUsable("https://vpuniverse.com/files/file/5266-metallica-limited-edition-v17/", "mtl_170h"),
        "Stern's firmware links aren't offered as ROM downloads");
    Check(RomSources.VpuSearchUrl("mtl_180hc") == "https://vpuniverse.com/search/?q=mtl_180hc&type=downloads_file", "VPUniverse search link for a missing ROM");

    var odir = Path.Combine(root, "orig");
    Directory.CreateDirectory(odir);
    var origVpx = Path.Combine(odir, "orig.vpx");
    File.WriteAllText(origVpx, "...Const cGameName = \"JAWS_50Th_Anniversary\" ' for high scores ...");
    var pmVpx = Path.Combine(odir, "pm.vpx");
    File.WriteAllText(pmVpx, "...Const cGameName = \"mtl_180hc\"...LoadVPM \"03060000\", \"sam.VBS\", 3.10...");
    Check(RomDetector.Inspect(origVpx) is { NoRomNeeded: true } && RomDetector.Inspect(pmVpx) is { GameName: "mtl_180hc", UsesPinMame: true, NoRomNeeded: false },
        "tells PinMAME tables from originals that only set cGameName");

    var os = new AppSettings { TablesDir = Path.Combine(root, "orig-tables"), VpxDir = Path.Combine(root, "vpx") };
    var opack = Path.Combine(root, "orig-pack.zip");
    using (var z = ZipFile.Open(opack, ZipArchiveMode.Create))
    using (var e = new StreamWriter(z.CreateEntry("Jaws.vpx").Open())) e.Write("...Const cGameName = \"JAWS_50Th_Anniversary\"...");
    var oinst = await TableInstaller.InstallAsync(opack, new VpsGame { Id = "gJ", Name = "Jaws", Manufacturer = "Original", Year = 2025 },
        new VpsTableFile { Id = "tJ", TableFormat = "VPX" }, os, _ => { }, CancellationToken.None);
    Check(oinst.Manifest.RomNames.Count == 0 && oinst.MissingRoms.Count == 0, "an original table installs without a ROM requirement");

    // Read-only look at this PC's real tables, if they're here.
    var mine = AppSettings.Load();
    foreach (var name in new[] { "Jaws (Original 2025)", "Metallica (Premium Monsters) (Stern 2013)" })
    {
        var tblDir = Path.Combine(mine.TablesDir, name);
        var vpx = Directory.Exists(tblDir) ? Directory.EnumerateFiles(tblDir, "*.vpx").FirstOrDefault() : null;
        if (vpx != null) Console.WriteLine($"   this PC: {name}: {RomDetector.Inspect(vpx)}");
    }
}

// ---- Downloads survive closing the app ----
{
    var qPath = Path.Combine(root, "queue", "download-queue.json");
    var q = new PendingDownloads(qPath);
    q.Add(new PendingDownload { Id = "a", Title = "Table A", GameId = "g1", TableFileId = "t1" });
    q.Add(new PendingDownload { Id = "b", Kind = PendingDownload.RomKind, Title = "ROM for B", TablePath = @"C:\T\b.vpx" });
    q.Remove("a");
    var reopened = new PendingDownloads(qPath);
    Check(reopened.Items.Count == 1 && reopened.Items[0].Id == "b" && reopened.Items[0].Kind == PendingDownload.RomKind,
        "unfinished downloads are still in the queue after a restart");
    reopened.Clear();
    Check(new PendingDownloads(qPath).Items.Count == 0, "queue clears once resumed or discarded");
    File.WriteAllText(qPath, "{ not json");
    Check(new PendingDownloads(qPath).Items.Count == 0, "a damaged queue file starts empty instead of crashing");

    var dl = Path.Combine(root, "downloads-left");
    Directory.CreateDirectory(Path.Combine(dl, "job1"));
    File.WriteAllText(Path.Combine(dl, "job1", "half.zip"), "partial");
    File.WriteAllText(Path.Combine(dl, "loose.tmp"), "x");
    Check(PendingDownloads.CleanDownloadsFolder(dl) == 2 && !Directory.EnumerateFileSystemEntries(dl).Any(),
        "leftover partial downloads are cleared at startup");

    // Half-installed tables: the marker stays until an install completes.
    var hs = new AppSettings { TablesDir = Path.Combine(root, "half-tables"), VpxDir = Path.Combine(root, "vpx") };
    var cutEarly = Path.Combine(hs.TablesDir, "Cut Early (Bally 1980)");
    Directory.CreateDirectory(cutEarly);
    File.WriteAllText(Path.Combine(cutEarly, TableInstaller.InstallingMarker),
        System.Text.Json.JsonSerializer.Serialize(new InstallMarker("gE", "tE", "Cut Early", DateTime.Now)));
    var cutLate = Path.Combine(hs.TablesDir, "Cut Late (Bally 1981)");
    Directory.CreateDirectory(cutLate);
    File.WriteAllText(Path.Combine(cutLate, "Cut Late.vpx"), "half copied");
    File.WriteAllText(Path.Combine(cutLate, TableInstaller.InstallingMarker),
        System.Text.Json.JsonSerializer.Serialize(new InstallMarker("gL", "tL", "Cut Late", DateTime.Now)));
    var halfScan = TableInstaller.Scan(hs).Where(t => t.IsVpx).ToList();
    var early = halfScan.FirstOrDefault(t => t.Folder == cutEarly);
    var late = halfScan.FirstOrDefault(t => t.Folder == cutLate);
    Check(early is { IsIncomplete: true } && early.IncompleteInstall?.TableFileId == "tE" && late is { IsIncomplete: true },
        "half-installed tables are listed as incomplete, with what to reinstall");
    Check(!LobbyLibrary.Load(hs).Any(t => t.Id.Contains("Cut ")), "the VR lobby leaves half-installed tables out");
    TableInstaller.Remove(early!);
    Check(!Directory.Exists(cutEarly), "a table cut off before its file arrived can be removed");

    var hg = new VpsGame { Id = "gL", Name = "Cut Late", Manufacturer = "Bally", Year = 1981 };
    var hpack = Path.Combine(root, "cut-late.zip");
    using (var z = ZipFile.Open(hpack, ZipArchiveMode.Create)) z.CreateEntry("Cut Late.vpx");
    var redone = await TableInstaller.InstallAsync(hpack, hg, new VpsTableFile { Id = "tL", TableFormat = "VPX" }, hs, _ => { }, CancellationToken.None);
    Check(!redone.IsIncomplete && !File.Exists(Path.Combine(cutLate, TableInstaller.InstallingMarker)), "reinstalling completes it and clears the marker");
}

static string JsonEsc(string s) => System.Text.Json.JsonSerializer.Serialize(s);

Directory.Delete(root, true);
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures;
