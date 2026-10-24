using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

/// <summary>What the view model needs from the window (pickers, dialogs, minimize/restore).</summary>
public interface IUiHost
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickFileAsync(string title, string pattern);
    Task<bool> ConfirmAsync(string title, string message);
    /// <summary>Shows a message with several buttons; returns the index pressed, or -1 if the window was closed.</summary>
    Task<int> ChoiceAsync(string title, string message, string[] buttons);
    void HideForPlay();
    void ShowAfterPlay();
    void OpenExternal(string pathOrUrl);
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    readonly IUiHost _ui;
    readonly BrowserDownloader _browser = new();
    /// <summary>Saved queue: downloads not finished when the app closed are offered again at the next launch.</summary>
    readonly PendingDownloads _pending = new();
    bool _offeredResume;
    List<VpsGame> _allGames = [];

    public AppSettings Settings { get; } = AppSettings.Load();

    public MainViewModel(IUiHost ui)
    {
        _ui = ui;
        if (string.IsNullOrWhiteSpace(Settings.FpDir) && FpSetup.AutoDetect() is { } fp)
        {
            Settings.FpDir = fp;
            Settings.Save();
        }
        if (string.IsNullOrWhiteSpace(Settings.UevrDir) && PfxSetup.AutoDetectUevr() is { } uevr)
        {
            Settings.UevrDir = uevr;
            Settings.Save();
        }
        VpxDir = Settings.VpxDir;
        TablesDir = Settings.TablesDir;
        FpDir = Settings.FpDir;
        UevrDir = Settings.UevrDir;
        HideAdult = Settings.HideAdult;
        ReturnAfterPlay = Settings.ReturnAfterPlay;
        FpRunAsAdmin = Settings.FpRunAsAdmin;
        ApplyPinballUevrProfile = Settings.ApplyPinballUevrProfile;
        ShowZenReminder = Settings.ShowZenReminder;
        MaxParallelDownloads = Math.Clamp(Settings.MaxParallelDownloads, 1, 4);
        // Partial files from downloads cut off when the app last closed. (Their queue entries are
        // kept and offered again once the catalog has loaded.)
        PendingDownloads.CleanDownloadsFolder();
        RefreshRate = RefreshRates.FirstOrDefault(r => r.StartsWith(Settings.VrRefreshRate.ToString())) ?? RefreshRates[0];
        RefreshInstalled();
        RefreshSetupStatus();
    }

    // ---- Catalog -------------------------------------------------------------

    public ObservableCollection<GameItem> Games { get; } = [];
    public ObservableCollection<TableVersionItem> Versions { get; } = [];
    public string[] EngineFilters { get; } = ["VPX", "Future Pinball", "All"];

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool VrOnly { get; set; } = true;
    [ObservableProperty] public partial string EngineFilter { get; set; } = "VPX";
    [ObservableProperty] public partial GameItem? SelectedGame { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string CatalogStatus { get; set; } = "";

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnVrOnlyChanged(bool value) => ApplyFilter();
    partial void OnEngineFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedGameChanged(GameItem? value) => ShowVersions(value);

    [RelayCommand]
    public async Task LoadCatalogAsync(bool force = false)
    {
        IsLoading = true;
        CatalogStatus = "Loading the Virtual Pinball Spreadsheet…";
        try
        {
            _allGames = await Task.Run(() => VpsCatalog.LoadAsync(force));
            ApplyFilter();
            ApplyTableUpdates();
            _ = CheckUpdatesAsync(); // VPX / UEVR / BAM, in the background
            if (!_offeredResume) { _offeredResume = true; _ = OfferResumeAsync(); }
        }
        catch (Exception e)
        {
            CatalogStatus = "Couldn't load the catalog: " + e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    Task RefreshCatalog() => LoadCatalogAsync(true);

    bool MatchesEngine(VpsTableFile t) => EngineFilter switch
    {
        "VPX" => VpsCatalog.IsVpx(t),
        "Future Pinball" => GameItem.IsFp(t),
        _ => VpsCatalog.IsVpx(t) || GameItem.IsFp(t),
    };

    bool IsVisible(VpsTableFile t) =>
        MatchesEngine(t)
        && (!HideAdult || !VpsCatalog.IsAdult(t))
        // FP VR goes through BAM-OpenVR, which works with most FP tables, so the VR filter is VPX-only.
        && (!VrOnly || GameItem.IsFp(t) || VpsCatalog.IsVrReady(t));

    // The card grid isn't virtualized, so show results a page at a time (each card loads artwork).
    const int PageSize = 60;
    List<VpsGame> _filtered = [];
    HashSet<string> _installedIds = [];

    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty] public partial string CountLabel { get; set; } = "";
    [ObservableProperty] public partial int MatchCount { get; set; }
    [ObservableProperty] public partial int TotalCount { get; set; }

    void ApplyFilter()
    {
        _installedIds = _allInstalled.Select(i => i.Table.Manifest.GameId).ToHashSet();
        var q = SearchText.Trim();
        _filtered = _allGames
            .Where(g => g.TableFiles!.Any(IsVisible))
            .Where(g => q == "" || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (g.Manufacturer?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (g.TableFiles?.Any(t => t.Authors?.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)) == true) ?? false))
            .ToList();

        var selectedId = SelectedGame?.Game.Id;
        Games.Clear();
        AddPage();
        SelectedGame = Games.FirstOrDefault(g => g.Game.Id == selectedId);
        CatalogStatus = $"{_filtered.Count} of {_allGames.Count} machines";
        MatchCount = _filtered.Count;
        TotalCount = _allGames.Count;
    }

    void AddPage()
    {
        foreach (var g in _filtered.Skip(Games.Count).Take(PageSize))
            Games.Add(new GameItem(g, _installedIds.Contains(g.Id)));
        HasMore = Games.Count < _filtered.Count;
        CountLabel = $"{Games.Count} shown · {_filtered.Count} of {_allGames.Count}";
    }

    [RelayCommand]
    void ShowMore() => AddPage();

    [RelayCommand]
    void CloseDetails() => SelectedGame = null;

    void ShowVersions(GameItem? item)
    {
        Versions.Clear();
        if (item == null) return;
        var files = item.Game.TableFiles!
            .Where(IsVisible)
            .OrderByDescending(t => t.UpdatedAt ?? 0);
        foreach (var f in files)
        {
            var file = f;
            Versions.Add(new TableVersionItem(item.Game, file, new AsyncRelayCommand(() => InstallAsync(item.Game, file))));
        }
    }

    // ---- Install -------------------------------------------------------------

    public ObservableCollection<JobItem> Jobs { get; } = [];

    async Task<bool> InstallCoreAsync(VpsGame game, VpsTableFile tf, InstalledTable? replacing)
    {
        var url = tf.Urls?.FirstOrDefault(u => !u.Broken)?.Url;
        if (url == null) return false;

        var verb = replacing == null ? "" : "Update: ";
        var job = new JobItem($"{verb}{game.Name} — {string.Join(", ", tf.Authors ?? [])}");
        Jobs.Insert(0, job);
        _pending.Add(new PendingDownload
        {
            Id = job.Id, Kind = PendingDownload.TableKind, Title = job.Title,
            GameId = game.Id, TableFileId = tf.Id, TablePath = replacing?.TablePath,
        });
        string? file = null;
        try
        {
            // Prefer files that mention VR when a page lists several builds.
            string[] hint = VpsCatalog.HasFeature(tf, "VR") ? ["VR"] : [];
            file = await FetchAsync(url, job, hint);

            job.Report("Installing files…");
            var installed = await TableInstaller.InstallAsync(file, game, tf, Settings, job.Report, job.Cts.Token, replacing);
            var done = replacing == null ? "Installed" : $"Updated to v{tf.Version}";
            var getRom = installed.MissingRoms.Count > 0 && FindRomSource(installed) != null;
            // This job is done; the ROM (if any) gets its own entry in the downloads list.
            job.Finish(installed.MissingRoms.Count == 0 ? $"✓ {done}. Ready to play."
                : getRom ? $"✓ {done}. Getting its ROM next (see below)."
                : $"✓ {done}. The catalog has no ROM download for it; use Add ROM in My Tables.");
            RefreshInstalled();
            ApplyFilter();
            if (getRom) _ = GetRomAsync(installed);
            return true;
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
            return false;
        }
        catch (Exception e)
        {
            job.Finish("Failed: " + e.Message, error: true);
            return false;
        }
        finally
        {
            CleanJobDownloads(job);
            // Finished, failed or cancelled: nothing to resume. (Not when it was cut off by the app
            // closing: that entry stays so the download is offered again next time.)
            if (!_closing) _pending.Remove(job.Id);
        }
    }

    /// <summary>Downloads a file straight (GitHub etc.) or through the sign-in browser (table sites).</summary>
    Task<string> FetchAsync(string url, JobItem job, string[] hint) => WithDownloadSlotAsync(job, async () =>
    {
        var downloads = JobDownloadDir(job);
        if (BrowserDownloader.IsDirectFileUrl(url))
        {
            var file = Path.Combine(downloads, TableInstaller.SafeName(Path.GetFileName(new Uri(url).LocalPath)));
            job.Report("Downloading…");
            await Http.DownloadFileAsync(url, file, job.ProgressReporter, job.Cts.Token);
            return file;
        }
        return await _browser.DownloadAsync(url, downloads, hint, job.Report, job.Cts.Token);
    });

    // ---- Download queue ----------------------------------------------------------
    // Downloads run a few at a time (Setup → "Downloads at once", default 2); the rest wait in the
    // queue. Unpacking and installing don't take a slot, so the next download starts meanwhile.

    public int[] ParallelDownloadChoices { get; } = [1, 2, 3, 4];
    [ObservableProperty] public partial int MaxParallelDownloads { get; set; }
    SemaphoreSlim _downloadSlots = new(2);

    partial void OnMaxParallelDownloadsChanged(int value)
    {
        value = Math.Clamp(value, 1, 4);
        Settings.MaxParallelDownloads = value;
        Settings.Save();
        // Jobs already holding a slot release it to the old gate; new jobs use the new size.
        _downloadSlots = new SemaphoreSlim(value);
    }

    async Task<T> WithDownloadSlotAsync<T>(JobItem job, Func<Task<T>> download)
    {
        var slots = _downloadSlots;
        if (slots.CurrentCount == 0) job.Report("Queued: waiting for a free download slot…");
        await slots.WaitAsync(job.Cts.Token);
        try { return await download(); }
        finally { slots.Release(); }
    }

    /// <summary>Each job downloads into its own folder, so parallel downloads with the same file name can't collide.</summary>
    static string JobDownloadDir(JobItem job) =>
        Directory.CreateDirectory(Path.Combine(AppSettings.DataDir, "downloads", job.Id)).FullName;

    static void CleanJobDownloads(JobItem job)
    {
        try { Directory.Delete(Path.Combine(AppSettings.DataDir, "downloads", job.Id), recursive: true); } catch { }
    }

    // ---- ROMs ------------------------------------------------------------------

    /// <summary>The catalog's ROM download (usually on VPForums) for a table's missing ROM.</summary>
    VpsFile? FindRomSource(InstalledTable t)
    {
        // Missing ROM first; otherwise the one the table already uses (to fetch its whole ROM family).
        var missing = t.MissingRoms.Count > 0 ? t.MissingRoms : t.Manifest.RomNames;
        if (missing.Count == 0 || t.Manifest.GameId == "") return null;
        var game = _allGames.FirstOrDefault(g => g.Id == t.Manifest.GameId);
        var roms = game?.RomFiles?.Where(r => UsableRomUrl(r) != null).ToList() ?? [];
        foreach (var name in missing)
            if (roms.FirstOrDefault(r => string.Equals(r.Version, name, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return null;
    }

    readonly HashSet<string> _romRechecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tables installed before the app checked for PinMAME may list a ROM they don't use (originals
    /// that set cGameName for other things). Re-reads the script once per table and corrects it.
    /// </summary>
    async Task RecheckRomNeedAsync(InstalledTable t)
    {
        if (t.Manifest.GameId == "" || !File.Exists(t.TablePath) || !_romRechecked.Add(t.TablePath)) return;
        var info = await Task.Run(() => RomDetector.Inspect(t.TablePath));
        List<string>? fixedNames =
            info.NoRomNeeded ? [] :
            info.GameName is { } g && !t.Manifest.RomNames.SequenceEqual([g], StringComparer.OrdinalIgnoreCase) ? [g] :
            null;
        if (fixedNames == null) return;
        t.Manifest.RomNames = fixedNames;
        try { TableInstaller.SaveManifest(t); } catch { /* still corrected for this session */ }
        RefreshInstalled();
    }

    [RelayCommand]
    void SearchRom(InstalledItem item)
    {
        if (item.Table.MissingRoms.FirstOrDefault() is { } rom) _ui.OpenExternal(RomSources.VpuSearchUrl(rom));
    }

    /// <summary>First link that's a real ROM download (not Stern's firmware site, not a .bin).</summary>
    static string? UsableRomUrl(VpsFile r) =>
        r.Urls?.FirstOrDefault(u => !u.Broken && RomSources.IsUsable(u.Url, r.Version))?.Url;

    // Per-table commands run for several tables at once (a shared command would otherwise grey out
    // every card's button while one download runs); only the busy table's buttons are disabled.
    [RelayCommand(AllowConcurrentExecutions = true)]
    Task GetRom(InstalledItem item) => GetRomAsync(item.Table);

    async Task GetRomAsync(InstalledTable table)
    {
        if (!TryBeginBusy(table.TablePath)) return; // already fetching this table's ROM
        try { await GetRomCoreAsync(table); }
        finally { EndBusy(table.TablePath); }
    }

    /// <param name="replacing">The installed table this replaces (updates), so its old file is removed.</param>
    async Task<bool> InstallAsync(VpsGame game, VpsTableFile tf, InstalledTable? replacing = null)
    {
        if (replacing == null) return await InstallCoreAsync(game, tf, null);
        if (!TryBeginBusy(replacing.TablePath)) return false; // already updating this table
        try { return await InstallCoreAsync(game, tf, replacing); }
        finally { EndBusy(replacing.TablePath); }
    }

    /// <summary>Tables with a ROM fetch, update or reinstall running (by table path).</summary>
    readonly HashSet<string> _busyTables = new(StringComparer.OrdinalIgnoreCase);

    bool TryBeginBusy(string tablePath)
    {
        if (!_busyTables.Add(tablePath)) return false;
        SetBusy(tablePath, true);
        return true;
    }

    void EndBusy(string tablePath)
    {
        _busyTables.Remove(tablePath);
        SetBusy(tablePath, false);
    }

    void SetBusy(string tablePath, bool on)
    {
        foreach (var i in _allInstalled.Where(i => string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)))
            i.IsBusy = on;
    }

    async Task GetRomCoreAsync(InstalledTable table)
    {
        // Older installs list every ROM VPS knows; read the script to find the one this table loads.
        if (table.Manifest.RomNames.Count > 1 && RomDetector.FromVpx(table.TablePath) is { } exact)
        {
            table.Manifest.RomNames = [exact];
            try { TableInstaller.SaveManifest(table); } catch { /* still usable this session */ }
        }

        var job = new JobItem($"ROM for {table.Manifest.GameName}");
        Jobs.Insert(0, job);
        _pending.Add(new PendingDownload
        {
            Id = job.Id, Kind = PendingDownload.RomKind, Title = job.Title,
            GameId = table.Manifest.GameId, TablePath = table.TablePath,
        });
        string? file = null;
        try
        {
            var src = FindRomSource(table)
                ?? throw new InvalidOperationException($"The catalog has no download for {string.Join(" / ", table.MissingRoms)}.zip. Use Add ROM with a file you have.");
            var url = UsableRomUrl(src)!;
            // ROM pages often hold every version of the machine's ROM; clones need their parent,
            // so take them all (they're small).
            List<string> files = BrowserDownloader.IsDirectFileUrl(url)
                ? [file = await FetchAsync(url, job, [src.Version!])]
                : await WithDownloadSlotAsync(job, () => _browser.DownloadAllAsync(url, JobDownloadDir(job), job.Report, job.Cts.Token));
            job.Report("Installing ROM…");
            var placed = await Task.Run(() => RomInstaller.InstallAll(files, table, src.Version!));
            var stillMissing = table.MissingRoms.Count > 0;
            job.Finish(stillMissing
                ? $"Installed {placed.Count} ROM file(s), but none is {string.Join(" / ", table.MissingRoms)}.zip. Use Add ROM if you have it."
                : placed.Count == 1 ? $"✓ Installed {Path.GetFileName(placed[0])}. Ready to play."
                : $"✓ Installed {placed.Count} ROM files ({string.Join(", ", placed.Select(Path.GetFileNameWithoutExtension))}). Ready to play.",
                error: stillMissing);
            RefreshInstalled();
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        catch (Exception e)
        {
            job.Finish("Failed: " + e.Message, error: true);
        }
        finally
        {
            CleanJobDownloads(job);
            // Finished, failed or cancelled: nothing to resume. (Not when it was cut off by the app
            // closing: that entry stays so the download is offered again next time.)
            if (!_closing) _pending.Remove(job.Id);
        }
    }

    [RelayCommand]
    void ClearFinishedJobs()
    {
        foreach (var j in Jobs.Where(j => j.IsDone || j.IsError).ToList()) Jobs.Remove(j);
    }

    [RelayCommand]
    async Task OpenSiteAsync(string url)
    {
        try { await _browser.OpenForSignInAsync(url); }
        catch (Exception e) { CatalogStatus = "Couldn't open the browser: " + e.Message; }
    }

    [RelayCommand]
    void OpenPage(string url) => _ui.OpenExternal(url);

    // ---- My tables -------------------------------------------------------------

    public ObservableCollection<InstalledItem> Installed { get; } = [];
    [ObservableProperty] public partial string PlayStatus { get; set; } = "";

    public string[] InstalledFilters { get; } = ["All", "Favourites", "Most played", "Recently played"];
    [ObservableProperty] public partial string InstalledFilter { get; set; } = "All";
    [ObservableProperty] public partial string InstalledEmptyText { get; set; } = "";
    [ObservableProperty] public partial int InstalledTotal { get; set; }
    partial void OnInstalledFilterChanged(string value) => ShowInstalled();

    List<InstalledItem> _allInstalled = [];
    PlayStats _stats = new();

    [RelayCommand]
    void RefreshInstalled()
    {
        // Re-read stats every time: the VR lobby and its launcher write them too.
        _stats = PlayStats.Load();
        _allInstalled = TableInstaller.Scan(Settings).Select(t => new InstalledItem(t)).ToList();
        foreach (var item in _allInstalled)
        {
            item.ApplyStats(_stats);
            item.IsBusy = _busyTables.Contains(item.Table.TablePath); // keep a running fetch's button greyed after a rescan
        }
        ShowInstalled();
    }

    /// <summary>Fills the My Tables grid from the scan, using the chosen filter and order.</summary>
    void ShowInstalled()
    {
        IEnumerable<InstalledItem> q = InstalledFilter switch
        {
            "Favourites" => _allInstalled.Where(i => i.IsFavourite),
            "Most played" => _allInstalled.Where(i => i.Plays > 0).OrderByDescending(i => i.Plays).ThenByDescending(i => i.LastPlayed),
            "Recently played" => _allInstalled.Where(i => i.LastPlayed != null).OrderByDescending(i => i.LastPlayed),
            // Favourites first, then A–Z.
            _ => _allInstalled.OrderByDescending(i => i.IsFavourite).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
        };
        Installed.Clear();
        foreach (var i in q) Installed.Add(i);
        InstalledTotal = _allInstalled.Count;
        InstalledEmptyText = Installed.Count > 0 || _allInstalled.Count == 0 ? ""
            : InstalledFilter switch
            {
                "Favourites" => "No favourites yet. Tap the ☆ on a table (or press the right grip on it in the VR lobby).",
                _ => "Nothing played yet. Your plays from here and from the VR lobby show up in this list.",
            };
        ApplyTableUpdates();
    }

    [RelayCommand]
    void ToggleFavourite(InstalledItem item)
    {
        var on = !item.IsFavourite;
        try { _stats = PlayStats.Mutate(st => st.SetFavourite(item.Table.TablePath, on)); }
        catch (Exception e) { PlayStatus = "Couldn't save the favourite: " + e.Message; return; }
        item.ApplyStats(_stats);
        if (InstalledFilter != "Most played" && InstalledFilter != "Recently played") ShowInstalled();
    }

    [RelayCommand]
    Task PlayVr(InstalledItem item) => PlayAsync(item, vr: true);

    [RelayCommand]
    Task PlayDesktop(InstalledItem item) => PlayAsync(item, vr: false);

    async Task PlayAsync(InstalledItem item, bool vr)
    {
        var engine = Engines.For(item.Table.Manifest.Engine);
        try
        {
            if (vr && !await EnsureHeadsetAsync()) { PlayStatus = ""; return; }
            if (vr && ZenGames.For(engine.Id) is { } zen && Settings.ShowZenReminder && !await ZenReminderAsync(zen)) { PlayStatus = ""; return; }
            PlayStatus = $"Starting {item.Title}…";
            using var proc = await engine.LaunchAsync(Settings, item.Table, vr, CancellationToken.None);
            PlayStatus = $"Playing {item.Title}…";
            var started = DateTime.Now;
            var path = item.Table.TablePath;
            TrySaveStats(st => st.RecordPlay(path, started));
            item.ApplyStats(_stats);
            if (!ReturnAfterPlay)
            {
                // Still add the time played, in the background.
                _ = proc.WaitForExitAsync().ContinueWith(_ => TrySaveStats(st => st.AddTime(path, DateTime.Now - started)), TaskScheduler.Default);
                return;
            }
            // Pick-and-return loop: get out of the way, come back when the table closes.
            _ui.HideForPlay();
            await proc.WaitForExitAsync();
            TrySaveStats(st => st.AddTime(path, DateTime.Now - started));
            item.ApplyStats(_stats);
            PlayStatus = $"Finished {item.Title}. Pick your next table.";
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            PlayStatus = "Launch cancelled (administrator prompt declined).";
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't start the table: " + e.Message;
        }
        finally
        {
            _ui.ShowAfterPlay();
        }
    }

    static string MetaLinkApp => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Oculus", "Support", "oculus-client", "OculusClient.exe");

    /// <summary>
    /// Before a VR launch: is a headset actually connected? Without one, VPX quietly opens on the
    /// monitor ("XR_ERROR_FORM_FACTOR_UNAVAILABLE" in its log), so ask the user to start Link first.
    /// Returns false if they cancel. When the check can't tell, it doesn't get in the way.
    /// </summary>
    async Task<bool> EnsureHeadsetAsync()
    {
        while (true)
        {
            PlayStatus = "Checking your headset…";
            var hs = await HeadsetCheck.CheckAsync(Settings, TimeSpan.FromSeconds(15));
            if (hs.State is HeadsetState.Ready or HeadsetState.Unknown) return true;

            var hasLinkApp = File.Exists(MetaLinkApp);
            var message = hs.State == HeadsetState.NotConnected
                ? "Your Quest isn't connected to the PC, so the table would open on your monitor instead.\n\n" +
                  "1. Open Meta Quest Link on this PC.\n" +
                  "2. Connect the Quest with a USB-C cable, or use Air Link (same Wi-Fi).\n" +
                  "3. In the headset: Quick Settings → Link → choose your PC → Launch.\n\n" +
                  "When you're in the grey Link home, press Try again."
                : "There's no VR runtime on this PC. Install the Meta Quest Link app, connect your Quest, " +
                  "and set Meta Quest Link as the active OpenXR runtime (Link app → Settings → General).";
            List<string> buttons = ["TRY AGAIN"];
            if (hasLinkApp) buttons.Add("OPEN META QUEST LINK");
            buttons.AddRange(["PLAY ANYWAY", "CANCEL"]);

            var pick = await _ui.ChoiceAsync("Headset not connected", message, [.. buttons]);
            var label = pick >= 0 ? buttons[pick] : "CANCEL";
            switch (label)
            {
                case "TRY AGAIN": continue;
                case "OPEN META QUEST LINK": _ui.OpenExternal(MetaLinkApp); continue;
                case "PLAY ANYWAY": return true;
                default: return false;
            }
        }
    }

    /// <summary>
    /// Pinball FX / M through UEVR: a short checklist before launching (gamepad, controllers down,
    /// OpenXR in the injector). Returns false if the user cancels.
    /// </summary>
    async Task<bool> ZenReminderAsync(ZenGame g)
    {
        var pad = Gamepad.IsConnected();
        var runtime = UevrPinballProfile.LastRuntime(g);
        var lines = new List<string>
        {
            pad switch
            {
                true => "✓ Gamepad connected. Play and use the menus with it.",
                false => "⚠ No gamepad found. Connect an Xbox-style controller: the Quest controllers are switched off for this game (they confuse it and can crash it).",
                _ => "• Use a gamepad to play and for the menus.",
            },
            "• Put the Quest controllers down so nothing gets pressed by accident.",
            runtime == "OpenVR"
                ? "⚠ Last time UEVR ran through OpenVR (SteamVR). In the UEVR window that opens, choose OpenXR before it injects; it's smoother for these games."
                : "• In the UEVR window that opens, keep OpenXR selected.",
            "• UEVR's own menu: hold both thumbsticks for a moment (or press Insert). A quick click no longer opens it.",
            Settings.ApplyPinballUevrProfile
                ? "• Pinball settings are applied: level table, menus in front of you. Change them on the Setup tab."
                : "• Pinball settings are switched off on the Setup tab, so UEVR uses its own.",
        };
        string[] buttons = ["PLAY", "PLAY, DON'T SHOW AGAIN", "CANCEL"];
        var pick = await _ui.ChoiceAsync($"Before {g.Title} starts in VR", string.Join("\n\n", lines), buttons);
        if (pick == 1) ShowZenReminder = false;
        return pick is 0 or 1;
    }

    // ---- VR lobby ------------------------------------------------------------------

    [ObservableProperty] public partial bool IsLobbyRunning { get; set; }

    /// <summary>
    /// One click: get Godot (first time), rebuild the lobby if its code changed, start it, and get
    /// out of the way until you're done. The lobby quits while a table runs (handing the headset to
    /// the table) and is restarted afterwards, so this waits until neither is running.
    /// </summary>
    [RelayCommand]
    async Task LaunchLobbyAsync()
    {
        if (IsLobbyRunning) return;
        var lobbyDir = LobbyRunner.FindLobbyDir();
        if (lobbyDir == null)
        {
            PlayStatus = "Couldn't find the VrLobby project next to this app.";
            return;
        }
        if (!await EnsureHeadsetAsync()) { PlayStatus = ""; return; }

        IsLobbyRunning = true;
        var job = new JobItem("VR lobby");
        Jobs.Insert(0, job);
        var hidden = false;
        try
        {
            var godot = await LobbyRunner.EnsureGodotAsync(job.ProgressReporter, job.Report, job.Cts.Token);
            if (LobbyRunner.NeedsBuild(lobbyDir)) await LobbyRunner.BuildAsync(lobbyDir, job.Report, job.Cts.Token);
            await LobbyRunner.ImportIfNeededAsync(godot, lobbyDir, job.Report, job.Cts.Token);

            using var lobby = LobbyRunner.Start(godot, lobbyDir);
            job.Finish("✓ Lobby started. Put your headset on; this window comes back when you quit the lobby.");
            PlayStatus = "VR lobby running…";
            _ui.HideForPlay();
            hidden = true;
            await WaitForLobbySessionAsync(lobby, Path.GetFileNameWithoutExtension(godot));
            PlayStatus = "Back from the VR lobby.";
        }
        catch (OperationCanceledException) { job.Finish("Cancelled", error: true); }
        catch (Exception e)
        {
            job.Finish("Couldn't start the lobby: " + e.Message, error: true);
            PlayStatus = "";
        }
        finally
        {
            IsLobbyRunning = false;
            if (hidden) _ui.ShowAfterPlay();
            RefreshInstalled(); // favourites and plays may have changed in the lobby
        }
    }

    /// <summary>Waits for the lobby to exit, then keeps waiting while a table it launched (and the lobby it restarts) runs.</summary>
    static async Task WaitForLobbySessionAsync(System.Diagnostics.Process lobby, string godotProcessName)
    {
        var lobbyStarted = lobby.StartTime;
        await lobby.WaitForExitAsync();
        var self = Environment.ProcessId;
        bool StartedSinceLobby(System.Diagnostics.Process p)
        {
            try { return p.Id != self && p.StartTime >= lobbyStarted; } catch { return false; }
        }
        bool Busy() =>
            System.Diagnostics.Process.GetProcessesByName(godotProcessName).Length > 0
            // "VrTableManager.exe --play …" is how the lobby launches a table (another manager window
            // you opened yourself started earlier, so it doesn't count).
            || System.Diagnostics.Process.GetProcessesByName("VrTableManager").Any(StartedSinceLobby);
        // Allow a few seconds for the hand-over between the lobby quitting and the table (or lobby) starting.
        var idleSince = DateTime.UtcNow;
        while (DateTime.UtcNow - idleSince < TimeSpan.FromSeconds(4))
        {
            await Task.Delay(500);
            if (Busy()) idleSince = DateTime.UtcNow;
        }
    }

    void TrySaveStats(Action<PlayStats> change)
    {
        try { _stats = PlayStats.Mutate(change); }
        catch { /* play counting must never get in the way of playing */ }
    }

    [RelayCommand]
    void OpenTableFolder(InstalledItem item) => _ui.OpenExternal(item.Table.Folder);

    [RelayCommand]
    async Task AddRomAsync(InstalledItem item)
    {
        var file = await _ui.PickFileAsync($"Choose the ROM zip for {item.Title}", "*.zip");
        if (file == null) return;
        TableInstaller.AddRom(item.Table, file);
        RefreshInstalled();
    }

    [RelayCommand]
    async Task RemoveAsync(InstalledItem item)
    {
        var what = item.Table.IsVpx ? $"the folder\n{item.Table.Folder}" : $"\n{item.Table.TablePath}";
        if (!await _ui.ConfirmAsync("Remove table", $"Delete {item.Title}?\n\nThis removes {what}")) return;
        try
        {
            TableInstaller.Remove(item.Table);
            RefreshInstalled();
            ApplyFilter();
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't remove: " + e.Message;
        }
    }

    // ---- Setup -----------------------------------------------------------------

    public string[] RefreshRates { get; } = ["0 (headset default)", "72 Hz", "80 Hz", "90 Hz", "120 Hz"];

    [ObservableProperty] public partial string VpxDir { get; set; }
    [ObservableProperty] public partial string TablesDir { get; set; }
    [ObservableProperty] public partial string FpDir { get; set; }
    [ObservableProperty] public partial bool HideAdult { get; set; }
    [ObservableProperty] public partial bool ReturnAfterPlay { get; set; }
    [ObservableProperty] public partial bool FpRunAsAdmin { get; set; }
    [ObservableProperty] public partial string RefreshRate { get; set; }
    [ObservableProperty] public partial string VpxStatus { get; set; } = "";
    [ObservableProperty] public partial string FpStatus { get; set; } = "";
    [ObservableProperty] public partial string UevrDir { get; set; }
    [ObservableProperty] public partial string PfxStatus { get; set; } = "";
    [ObservableProperty] public partial string LatestVpx { get; set; } = "";
    [ObservableProperty] public partial bool IsInstallingVpx { get; set; }
    [ObservableProperty] public partial double VpxProgress { get; set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Persist setup fields as they change.
        switch (e.PropertyName)
        {
            case nameof(VpxDir): Settings.VpxDir = VpxDir; break;
            case nameof(TablesDir): Settings.TablesDir = TablesDir; break;
            case nameof(FpDir): Settings.FpDir = FpDir; break;
            case nameof(UevrDir): Settings.UevrDir = UevrDir; break;
            case nameof(HideAdult): Settings.HideAdult = HideAdult; ApplyFilter(); break;
            case nameof(ReturnAfterPlay): Settings.ReturnAfterPlay = ReturnAfterPlay; break;
            case nameof(FpRunAsAdmin): Settings.FpRunAsAdmin = FpRunAsAdmin; break;
            case nameof(ApplyPinballUevrProfile): Settings.ApplyPinballUevrProfile = ApplyPinballUevrProfile; RefreshSetupStatus(); break;
            case nameof(ShowZenReminder): Settings.ShowZenReminder = ShowZenReminder; break;
            case nameof(RefreshRate): Settings.VrRefreshRate = int.TryParse(RefreshRate?.Split(' ')[0], out var r) ? r : 0; break;
            default: return;
        }
        Settings.Save();
        if (e.PropertyName is nameof(VpxDir) or nameof(TablesDir) or nameof(FpDir) or nameof(UevrDir))
        {
            RefreshSetupStatus();
            RefreshInstalled();
        }
    }

    void RefreshSetupStatus()
    {
        VpxStatus = Engines.For(TableManifest.Vpx).DescribeSetup(Settings);
        FpStatus = Engines.For(TableManifest.FuturePinball).DescribeSetup(Settings);
        PfxStatus = Engines.For(TableManifest.PinballFx).DescribeSetup(Settings);
        PmStatus = Engines.For(TableManifest.PinballM).DescribeSetup(Settings);
        PfxProfileStatus = DescribeUevrProfile(ZenGames.PinballFx);
        PmProfileStatus = DescribeUevrProfile(ZenGames.PinballM);
        PfxHasUevrBackup = UevrPinballProfile.HasBackup(ZenGames.PinballFx);
        PmHasUevrBackup = UevrPinballProfile.HasBackup(ZenGames.PinballM);
    }

    [ObservableProperty] public partial bool PfxHasUevrBackup { get; set; }
    [ObservableProperty] public partial bool PmHasUevrBackup { get; set; }

    [ObservableProperty] public partial string PfxProfileStatus { get; set; } = "";
    [ObservableProperty] public partial string PmProfileStatus { get; set; } = "";
    [ObservableProperty] public partial bool ApplyPinballUevrProfile { get; set; }
    [ObservableProperty] public partial bool ShowZenReminder { get; set; }

    /// <summary>The pinball UEVR settings, with why each one is set (shown on Setup).</summary>
    public string PinballProfileSummary { get; } = string.Join("\n",
        UevrPinballProfile.Settings.Select(s => $"• {s.Key} = {s.Value}: {s.Why}"));

    string DescribeUevrProfile(ZenGame g)
    {
        var parts = new List<string>
        {
            UevrPinballProfile.IsApplied(g) ? "Pinball settings are in its UEVR profile."
                : Settings.ApplyPinballUevrProfile ? "Pinball settings will be applied at the next VR launch."
                : "Pinball settings are off; UEVR uses its own.",
        };
        if (UevrPinballProfile.LastRuntime(g) is { } rt)
            parts.Add(rt == "OpenVR" ? "Last run used OpenVR: pick OpenXR in the UEVR window next time." : "Last run used OpenXR.");
        if (UevrPinballProfile.HasBackup(g)) parts.Add("Your original profile is backed up.");
        return string.Join(" ", parts);
    }

    [RelayCommand]
    void ApplyPinballProfile(string? engineId)
    {
        var g = ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx;
        string msg;
        try
        {
            var r = UevrPinballProfile.Apply(g);
            msg = r.Changed.Count == 0 ? "✓ Already on pinball defaults."
                : $"✓ Reset {r.Changed.Count} UEVR setting(s) to pinball defaults{(r.BackedUp ? " (original backed up)" : "")}.";
        }
        catch (Exception e) { msg = "Couldn't update the UEVR profile: " + e.Message; }
        RefreshSetupStatus();
        if (g == ZenGames.PinballM) PmProfileStatus = msg + " " + PmProfileStatus;
        else PfxProfileStatus = msg + " " + PfxProfileStatus;
    }

    [RelayCommand]
    async Task RestoreUevrOriginalAsync(string? engineId)
    {
        var g = ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx;
        if (!UevrPinballProfile.HasBackup(g)) return;
        if (!await _ui.ConfirmAsync("Restore UEVR profile",
                $"Put back {g.Title}'s UEVR profile as it was before this app changed it?\n\n" +
                (Settings.ApplyPinballUevrProfile ? "Pinball settings will also be switched off, so they aren't re-applied at the next launch." : "")))
            return;
        UevrPinballProfile.RestoreOriginal(g);
        ApplyPinballUevrProfile = false;
        RefreshSetupStatus();
    }

    [ObservableProperty] public partial string PmStatus { get; set; } = "";

    [RelayCommand]
    public async Task CheckVpxUpdateAsync()
    {
        try
        {
            var rel = await VpxSetup.FindLatestAsync(CancellationToken.None);
            var current = VpxSetup.InstalledVersion(Settings);
            LatestVpx = rel == null ? "No VPX release found for this OS."
                : current == rel.Tag ? $"Up to date ({rel.Tag})."
                : $"Latest: {rel.Tag} ({rel.Size / 1_048_576} MB)";
        }
        catch (Exception e)
        {
            LatestVpx = "Couldn't check GitHub: " + e.Message;
        }
    }

    [RelayCommand]
    async Task InstallVpxAsync()
    {
        IsInstallingVpx = true;
        try
        {
            var rel = await VpxSetup.FindLatestAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("No VPX release found for this OS.");
            var progress = new Progress<double>(p => VpxProgress = p * 100);
            await VpxSetup.InstallAsync(Settings, rel, progress, s => Avalonia.Threading.Dispatcher.UIThread.Post(() => LatestVpx = s), CancellationToken.None);
            LatestVpx = $"Installed {rel.Tag}.";
        }
        catch (Exception e)
        {
            LatestVpx = "Install failed: " + e.Message;
        }
        finally
        {
            IsInstallingVpx = false;
            RefreshSetupStatus();
            await CheckUpdatesAsync();
        }
    }

    [RelayCommand]
    async Task BrowseVpxDirAsync() { if (await _ui.PickFolderAsync("VPX install folder") is { } d) VpxDir = d; }

    [RelayCommand]
    async Task BrowseTablesDirAsync() { if (await _ui.PickFolderAsync("Tables folder") is { } d) TablesDir = d; }

    [RelayCommand]
    async Task BrowseFpDirAsync() { if (await _ui.PickFolderAsync("Future Pinball folder") is { } d) FpDir = d; }

    [RelayCommand]
    async Task BrowseUevrDirAsync() { if (await _ui.PickFolderAsync("UEVR folder (contains UEVRInjector.exe)") is { } d) UevrDir = d; }

    /// <param name="engineId">Which Zen game the profile is for: "PFX" (Pinball FX) or "PM" (Pinball M).</param>
    [RelayCommand]
    async Task ImportUevrProfileAsync(string? engineId)
    {
        var game = ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx;
        var zip = await _ui.PickFileAsync($"Choose a UEVR profile zip for {game.Title}", "*.zip");
        if (zip == null) return;
        try { PfxSetup.ImportProfile(zip, game); }
        catch (Exception e)
        {
            var msg = "Couldn't import the profile: " + e.Message;
            if (game == ZenGames.PinballM) PmStatus = msg; else PfxStatus = msg;
            return;
        }
        RefreshSetupStatus();
    }

    [RelayCommand]
    void OpenUevrProfileFolder(string? engineId)
    {
        var dir = PfxSetup.ProfileDirFor(ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx);
        Directory.CreateDirectory(dir);
        _ui.OpenExternal(dir);
    }

    [RelayCommand]
    void RecheckSetup()
    {
        if (string.IsNullOrWhiteSpace(FpDir) && FpSetup.AutoDetect() is { } fp) FpDir = fp;
        if (string.IsNullOrWhiteSpace(UevrDir) && PfxSetup.AutoDetectUevr() is { } uevr) UevrDir = uevr;
        RefreshInstalled();
        RefreshSetupStatus();
    }

    // ---- Updates ---------------------------------------------------------------

    [ObservableProperty] public partial int TableUpdateCount { get; set; }
    [ObservableProperty] public partial string TableUpdateText { get; set; } = "Checking after the catalog loads…";
    [ObservableProperty] public partial string VpxUpdateText { get; set; } = "Not checked yet.";
    [ObservableProperty] public partial bool HasVpxUpdate { get; set; }
    [ObservableProperty] public partial string UevrUpdateText { get; set; } = "Not checked yet.";
    [ObservableProperty] public partial bool HasUevrUpdate { get; set; }
    [ObservableProperty] public partial string UevrButtonText { get; set; } = "INSTALL UEVR";
    [ObservableProperty] public partial string BamUpdateText { get; set; } = "Not checked yet.";
    [ObservableProperty] public partial bool IsCheckingUpdates { get; set; }
    [ObservableProperty] public partial bool IsUpdating { get; set; }
    [ObservableProperty] public partial int UpdateCount { get; set; }
    [ObservableProperty] public partial string LastChecked { get; set; } = "";

    ToolRelease? _uevrLatest;

    /// <summary>Table updates come from the already-loaded catalog, so this is free to call often.</summary>
    void ApplyTableUpdates()
    {
        if (_allGames.Count == 0) return;
        var updates = TableUpdates.Find(_allInstalled.Select(i => i.Table), _allGames)
            .ToDictionary(u => u.Table.TablePath, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allInstalled)
        {
            item.Update = updates.GetValueOrDefault(item.Table.TablePath);
            item.RomSource = FindRomSource(item.Table);
            if (item.NeedsRom && item.RomSource == null) _ = RecheckRomNeedAsync(item.Table);
        }
        TableUpdateCount = updates.Count;
        var tracked = _allInstalled.Count(i => i.Table.Manifest.TableFileId != "");
        TableUpdateText = updates.Count switch
        {
            0 when tracked == 0 => "No tables installed from the catalog yet.",
            0 => tracked == 1 ? "Your table is up to date." : $"All {tracked} tables are up to date.",
            1 => "1 table has a newer version.",
            _ => $"{updates.Count} tables have newer versions.",
        };
        UpdateCount = TableUpdateCount + (HasVpxUpdate ? 1 : 0) + (HasUevrUpdate ? 1 : 0);
    }

    /// <summary>Checks GitHub (VPX, UEVR) and ravarcade.pl (BAM). Runs once at startup after the catalog loads.</summary>
    [RelayCommand]
    public async Task CheckUpdatesAsync()
    {
        IsCheckingUpdates = true;
        try
        {
            var ct = CancellationToken.None;
            var vpxTask = Try(() => VpxSetup.FindLatestAsync(ct));
            var uevrTask = Try(() => UevrSetup.FindLatestAsync(ct));
            var bamTask = Try(() => BamCheck.LatestAsync(ct));
            await Task.WhenAll(vpxTask, uevrTask, bamTask);

            // VPX
            var (vpx, vpxErr) = vpxTask.Result;
            var vpxHave = VpxSetup.InstalledVersion(Settings);
            HasVpxUpdate = vpx != null && vpxHave != null && vpxHave != vpx.Tag;
            VpxUpdateText = vpxErr != null ? "Couldn't check GitHub: " + vpxErr
                : vpx == null ? "No VPX release found for this OS."
                : vpxHave == null ? $"Not installed. Latest is {vpx.Tag}."
                : HasVpxUpdate ? $"{vpxHave} → {vpx.Tag}"
                : $"Up to date ({vpxHave}).";

            // UEVR
            var (uevr, uevrErr) = uevrTask.Result;
            _uevrLatest = uevr;
            var uevrHave = UevrSetup.InstalledVersion(Settings);
            HasUevrUpdate = uevr != null && uevrHave != null && uevrHave != uevr.Tag;
            UevrButtonText = uevrHave == null ? "INSTALL UEVR" : "UPDATE UEVR";
            UevrUpdateText = uevrErr != null ? "Couldn't check GitHub: " + uevrErr
                : uevr == null ? "No UEVR release found."
                : uevrHave == null ? $"Not installed. Latest is {uevr.Tag} (only needed for Pinball FX)."
                : uevrHave == "unknown" ? $"Installed (version unknown). Latest is {uevr.Tag}; updating makes it trackable."
                : HasUevrUpdate ? $"{uevrHave} → {uevr.Tag}"
                : $"Up to date ({uevrHave}).";

            // BAM: report only.
            var (bam, bamErr) = bamTask.Result;
            var bamHave = BamCheck.InstalledVersion(Settings);
            BamUpdateText = bamErr != null ? "Couldn't reach ravarcade.pl: " + bamErr
                : bam == null ? "Couldn't read the latest version from ravarcade.pl."
                : bamHave == null ? $"Latest is v{bam}. BAM isn't installed (only needed for Future Pinball)."
                : $"Latest is v{bam}; yours: {bamHave}. Install from the BAM page if yours is older.";

            ApplyTableUpdates();
            LastChecked = $"Checked {DateTime.Now:t}";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    static async Task<(T? value, string? error)> Try<T>(Func<Task<T?>> f) where T : class
    {
        try { return (await f(), null); }
        catch (Exception e) { return (null, e.Message); }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task UpdateTableAsync(InstalledItem item)
    {
        if (item.Update is not { } u) return;
        await InstallAsync(u.Game, u.File, u.Table);
    }

    [RelayCommand]
    async Task UpdateAllTablesAsync()
    {
        // One at a time: the browser downloader handles one site page at a time anyway.
        foreach (var u in _allInstalled.Select(i => i.Update).OfType<TableUpdate>().ToList())
            await InstallAsync(u.Game, u.File, u.Table);
    }

    [RelayCommand]
    async Task InstallUevrAsync()
    {
        var job = new JobItem("UEVR");
        Jobs.Insert(0, job);
        try
        {
            var rel = _uevrLatest ?? await UevrSetup.FindLatestAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("No UEVR release found.");
            job.Report($"Downloading UEVR {rel.Tag}…");
            await UevrSetup.InstallAsync(Settings, rel, job.ProgressReporter, job.Cts.Token);
            UevrDir = Settings.UevrDir;
            job.Finish($"✓ UEVR {rel.Tag} installed. Your Pinball FX / Pinball M profiles are untouched.");
        }
        catch (Exception e)
        {
            job.Finish("Failed: " + e.Message, error: true);
        }
        finally
        {
            RefreshSetupStatus();
            await CheckUpdatesAsync();
        }
    }

    [RelayCommand]
    async Task UpdateAllAsync()
    {
        IsUpdating = true;
        try
        {
            if (HasVpxUpdate) await InstallVpxAsync();
            if (HasUevrUpdate) await InstallUevrAsync();
            await UpdateAllTablesAsync();
            await CheckUpdatesAsync();
        }
        finally
        {
            IsUpdating = false;
        }
    }

    // ---- Resume after a restart / closing with downloads running ------------------------

    volatile bool _closing;

    /// <summary>Downloads still running or waiting in the queue.</summary>
    public int RunningJobCount => Jobs.Count(j => !j.IsDone && !j.IsError);

    /// <summary>Downloads still in the saved queue are offered again after the catalog loads.</summary>
    async Task OfferResumeAsync()
    {
        var items = _pending.Items;
        if (items.Count == 0) return;
        var list = string.Join("\n", items.Take(8).Select(i => "• " + i.Title)) + (items.Count > 8 ? $"\n• …and {items.Count - 8} more" : "");
        var pick = await _ui.ChoiceAsync(
            items.Count == 1 ? "Resume 1 download?" : $"Resume {items.Count} downloads?",
            $"These hadn't finished when VR Table Manager last closed:\n\n{list}\n\nThey'll start again from the beginning.",
            ["RESUME", "DISCARD"]);
        _pending.Clear(); // resumed jobs add themselves back
        if (pick != 0) return;

        var installed = TableInstaller.Scan(Settings);
        InstalledTable? Find(string? path) => path == null ? null
            : installed.FirstOrDefault(t => string.Equals(Path.GetFullPath(t.TablePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var skipped = new List<string>();
        foreach (var item in items)
        {
            if (item.Kind == PendingDownload.RomKind)
            {
                if (Find(item.TablePath) is { } t) _ = GetRomAsync(t);
                else skipped.Add(item.Title);
                continue;
            }
            var game = _allGames.FirstOrDefault(g => g.Id == item.GameId);
            var tf = game?.TableFiles?.FirstOrDefault(f => f.Id == item.TableFileId);
            if (game == null || tf == null) { skipped.Add(item.Title); continue; }
            var replacing = Find(item.TablePath);
            _ = InstallAsync(game, tf, replacing is { IsIncomplete: false } ? replacing : null);
        }
        if (skipped.Count > 0)
            CatalogStatus = $"Couldn't resume {string.Join(", ", skipped)}: no longer in the catalog or installed.";
    }

    /// <summary>Reinstalls a table whose install was cut off (INCOMPLETE on My Tables).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task ReinstallAsync(InstalledItem item)
    {
        var marker = item.Table.IncompleteInstall;
        var gameId = marker?.GameId ?? item.Table.Manifest.GameId;
        var fileId = marker?.TableFileId ?? item.Table.Manifest.TableFileId;
        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        var tf = game?.TableFiles?.FirstOrDefault(f => f.Id == fileId);
        if (game == null || tf == null)
        {
            PlayStatus = $"Couldn't find {item.Title} in the catalog to reinstall it. Remove it and install it again from Get Tables.";
            return;
        }
        // An interrupted update still has the old table file; replace it like a normal update.
        var replacing = File.Exists(item.Table.TablePath) ? item.Table : null;
        if (replacing != null) { await InstallAsync(game, tf, replacing); return; }
        // Cut off before the table file arrived: nothing to replace, but still mark this card busy.
        if (!TryBeginBusy(item.Table.TablePath)) return;
        try { await InstallAsync(game, tf); }
        finally { EndBusy(item.Table.TablePath); }
    }

    /// <summary>
    /// Called by the window when it's really closing. Downloads that end because of the shutdown
    /// keep their saved-queue entries, so they're offered again at the next launch.
    /// </summary>
    public void PrepareToClose() => _closing = true;

    public ValueTask DisposeAsync() => _browser.DisposeAsync();
}
