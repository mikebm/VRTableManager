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

    /// <param name="preferredName">A file name to filter to first (e.g. "afm_113b.zip"); <paramref name="pattern"/> stays as the other choice.</param>
    /// <param name="startFolder">Folder the picker opens in, when it exists.</param>
    Task<string?> PickFileAsync(
        string title,
        string pattern,
        string? preferredName = null,
        string? startFolder = null
    );

    /// <param name="okLabel">The button that says yes (most confirmations are for deleting something).</param>
    Task<bool> ConfirmAsync(string title, string message, string okLabel = "Delete");

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    Task<string?> AskTextAsync(string title, string message, string initial, string okLabel);

    /// <summary>Shows a message with several buttons; returns the index pressed, or -1 if the window was closed.</summary>
    /// <param name="details">Optional monospaced text (e.g. log lines) shown in a scrolling box under the message.</param>
    Task<int> ChoiceAsync(string title, string message, string[] buttons, string? details = null);
    Task<string?> PickSaveFileAsync(
        string title,
        string suggestedName,
        string pattern,
        string? startFolder = null
    );
    void HideForPlay();
    void ShowAfterPlay();
    void OpenExternal(string pathOrUrl);
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    readonly IUiHost _ui;
    readonly BrowserDownloader _browser = new();

    /// <summary>Table sites: you download in your own browser, the app takes it from there (see SitePolicy).</summary>
    readonly AssistedDownloader _assisted;

    /// <summary>Saved queue: downloads not finished when the app closed are offered again at the next launch.</summary>
    readonly PendingDownloads _pending = new();

    /// <summary>The saved queue as it was at launch: downloads started since (while the startup checks ran) aren't resumed.</summary>
    readonly IReadOnlyList<PendingDownload> _leftFromLastTime;
    bool _offeredResume;
    List<VpsGame> _allGames = [];

    public AppSettings Settings { get; } = AppSettings.Load();

    public MainViewModel(IUiHost ui)
    {
        _ui = ui;
        _leftFromLastTime = _pending.Items;
        _assisted = new AssistedDownloader(ui.OpenExternal);
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
        VpxUseDoNotUseBuilds = Settings.VpxUseDoNotUseBuilds;
        VpxUseNightlyBuilds = Settings.VpxUseNightlyBuilds;
        FpRunAsAdmin = Settings.FpRunAsAdmin;
        ApplyPinballUevrProfile = Settings.ApplyPinballUevrProfile;
        ShowZenReminder = Settings.ShowZenReminder;
        MaxParallelDownloads = Math.Clamp(Settings.MaxParallelDownloads, 1, 4);
        DeleteDownloadsAfterInstall = Settings.DeleteDownloadsAfterInstall;
        Jobs.CollectionChanged += (_, e) =>
        {
            foreach (JobItem j in e.NewItems ?? Array.Empty<JobItem>())
            {
                j.Succeeded += OnJobSucceeded;
            }
        };
        // Partial files from downloads cut off when the app last closed. (Their queue entries are
        // kept and offered again once the catalog has loaded.)
        PendingDownloads.CleanDownloadsFolder();
        RefreshRate =
            RefreshRates.FirstOrDefault(r => r.StartsWith(Settings.VrRefreshRate.ToString()))
            ?? RefreshRates[0];
        AswMode = AswModes.Contains(Settings.MetaAswMode) ? Settings.MetaAswMode : AswModes[0];
        WarnSteamVrRuntime = Settings.WarnSteamVrRuntime;
        CardSize = Settings.CardSize;
        SetupSection = Settings.SetupSection;
        ExplainFailedLaunches = Settings.ExplainFailedLaunches;
        UseBuiltInPinmame = Settings.UseBuiltInPinmame;
        UsePupPlugin = Settings.UsePupPlugin;
        UseDisplayPlugins = Settings.UseDisplayPlugins;
        VrAutoBackglass = Settings.VrAutoBackglass;
        DesktopBloomOff = Settings.DesktopBloomOff;
        _loadingGraphics = true; // loading, not a change: no status line
        UseVrGraphics = Settings.UseVrGraphics;
        _loadingGraphics = false;
        AutoVrRoom = Settings.AutoVrRoom;
        LobbyRecordsRomAudio = Settings.LobbyRecordsRomAudio;
        LobbyWaitsForTable = Settings.LobbyWaitsForTable;
        LobbyStaysOpen = Settings.LobbyStaysOpen;
        LobbyLoadingVideos = Settings.LobbyLoadingVideos;
        RefreshDefaultLoadingVideo();
        LobbyCabinetCount = Settings.LobbyCabinetCount;
        LobbyCabinetScale = Settings.LobbyCabinetScalePercent;
        LoadGraphics();
        RefreshInstalled();
        RefreshSetupStatus();
    }

    // ---- Catalog -------------------------------------------------------------

    public ObservableCollection<GameItem> Games { get; } = [];
    public ObservableCollection<TableVersionItem> Versions { get; } = [];
    public string[] EngineFilters { get; } = ["VPX", "Future Pinball", "All"];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool VrOnly { get; set; } = true;

    [ObservableProperty]
    public partial string EngineFilter { get; set; } = "VPX";

    [ObservableProperty]
    public partial GameItem? SelectedGame { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string CatalogStatus { get; set; } = "";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnVrOnlyChanged(bool value) => ApplyFilter();

    partial void OnEngineFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedGameChanged(GameItem? value) => ShowVersions(value);

    /// <summary>At startup: settings.json is there but couldn't be read, so nothing changed here is saved (see AppSettings.Unreadable).</summary>
    public async Task WarnIfSettingsUnreadableAsync()
    {
        if (!Settings.Unreadable)
        {
            return;
        }

        await _ui.ChoiceAsync(
            "Your settings couldn't be read",
            $"VR Table Manager couldn't read {Settings.SourcePath ?? AppSettings.DefaultPath} (another program may have it open, "
                + "or Windows won't let this app read it). It's running on default settings, and changes you make won't be saved "
                + "until the file can be read again, so your own settings aren't written over. Close the other program, or check "
                + "the file's permissions, then restart VR Table Manager.",
            ["OK"]
        );
    }

    [RelayCommand]
    public async Task LoadCatalogAsync(bool force = false)
    {
        IsLoading = true;
        CatalogStatus = "Loading the Virtual Pinball Spreadsheet…";
        try
        {
            _allGames = await Task.Run(() => VpsCatalog.LoadAsync(force));
            _assisted.KnownGames = _allGames.Select(g => g.Name);
            ApplyFilter();
            ShowInstalled(); // My Tables' type and theme filters come from the catalog; also runs ApplyTableUpdates
            _ = LinkImportedTablesAsync();
            _ = CheckUpdatesAsync(); // VPX / UEVR / BAM, in the background
            _ = LoadRuleSheetsAsync();
            _ = RefreshHighScoreMapsAsync();
            if (VpsCatalog.UsingSavedCopy)
            {
                _ = RetryCatalogAsync();
            }
            if (!_offeredResume)
            {
                _offeredResume = true;
                _ = StartupPromptsAsync();
            }
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

    bool _catalogRetrying;

    /// <summary>
    /// The spreadsheet couldn't be reached, so the saved copy is showing. Try again quietly every few minutes and
    /// swap the new catalog in once it's back.
    /// </summary>
    async Task RetryCatalogAsync()
    {
        if (_catalogRetrying)
        {
            return;
        }

        _catalogRetrying = true;
        try
        {
            while (VpsCatalog.UsingSavedCopy)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                if (IsLoading)
                {
                    continue;
                }

                List<VpsGame> games;
                try
                {
                    games = await Task.Run(() => VpsCatalog.LoadAsync(true));
                }
                catch
                {
                    continue;
                }
                if (!VpsCatalog.UsingSavedCopy)
                {
                    _allGames = games;
                    _assisted.KnownGames = _allGames.Select(g => g.Name);
                    ApplyFilter();
                    ShowInstalled(); // My Tables' type and theme filters come from the catalog; also runs ApplyTableUpdates
                }
            }
        }
        finally
        {
            _catalogRetrying = false;
        }
    }

    bool MatchesEngine(VpsTableFile t) =>
        EngineFilter switch
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

    /// <summary>
    /// Where the shown cards start in the results: 0, unless the A–Z rail jumped far down the list
    /// (<see cref="FirstCatalogAt"/>), which starts them at the letter rather than laying out every card before it.
    /// </summary>
    int _pageStart;

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    /// <summary>The shown cards start after the first result: SHOW EARLIER TABLES brings back the page before.</summary>
    [ObservableProperty]
    public partial bool HasEarlier { get; set; }

    [ObservableProperty]
    public partial string CountLabel { get; set; } = "";

    [ObservableProperty]
    public partial int MatchCount { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    void ApplyFilter()
    {
        _installedIds = _allInstalled.Select(i => i.Table.Manifest.GameId).ToHashSet();
        var search = TableSearch.Parse(SearchText);
        var scored = _allGames
            .Where(g => g.TableFiles!.Any(IsVisible))
            .Where(MatchesCatalogFilters)
            .Select(g => (Game: g, Score: search?.Score(_gameIndex.GetValue(g, IndexGame)) ?? 1))
            .Where(x => x.Score > 0)
            .ToList();
        var matches = scored.Select(x => x.Game);
        _filtered = (
            CatalogSort switch
            {
                // Searching: best matches first (the other orders still apply as chosen).
                "A–Z" when search != null => scored
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.Game),
                "Recently updated" => matches.OrderByDescending(g =>
                    g.TableFiles!.Where(IsVisible).Max(t => t.UpdatedAt ?? 0)
                ),
                "Newest machines" => matches
                    .OrderByDescending(g => g.Year ?? 0)
                    .ThenBy(g => g.Name),
                "Oldest machines" => matches
                    .OrderBy(g => g.Year is > 0 ? g.Year : int.MaxValue)
                    .ThenBy(g => g.Name),
                _ => matches.OrderBy(g => SortKey(g.Name), StringComparer.OrdinalIgnoreCase),
            }
        ).ToList();
        UpdateManufacturers();
        UpdateCatalogLetters(CatalogSort == CatalogSorts[0] && search == null);

        var selectedId = SelectedGame?.Game.Id;
        _pageStart = 0;
        Games.Clear();
        AddPage();
        SelectedGame = Games.FirstOrDefault(g => g.Game.Id == selectedId);
        CatalogStatus = $"{_filtered.Count} of {_allGames.Count} machines";
        if (VpsCatalog.UsingSavedCopy && VpsCatalog.SavedAt is { } saved)
        {
            CatalogStatus += $" · spreadsheet offline, using the copy from {saved:d MMM HH:mm}";
        }
        MatchCount = _filtered.Count;
        TotalCount = _allGames.Count;
    }

    // ---- Catalog filters: maker, era, type, order ----

    const string AnyMaker = TableFilters.AnyMaker,
        AnyEra = TableFilters.AnyEra,
        AnyType = TableFilters.AnyType,
        AnyTheme = TableFilters.AnyTheme;
    public ObservableCollection<string> Manufacturers { get; } = [AnyMaker];
    public ObservableCollection<string> Themes { get; } = [AnyTheme];
    public string[] Eras { get; } = TableFilters.Eras;
    public string[] MachineTypes { get; } = TableFilters.Types;
    public string[] CatalogSorts { get; } =
    ["A–Z", "Recently updated", "Newest machines", "Oldest machines"];

    [ObservableProperty]
    public partial string ManufacturerFilter { get; set; } = AnyMaker;

    [ObservableProperty]
    public partial string EraFilter { get; set; } = AnyEra;

    [ObservableProperty]
    public partial string TypeFilter { get; set; } = AnyType;

    [ObservableProperty]
    public partial string ThemeFilter { get; set; } = AnyTheme;

    [ObservableProperty]
    public partial string CatalogSort { get; set; } = "A–Z";

    [ObservableProperty]
    public partial bool HasCatalogFilters { get; set; }

    partial void OnManufacturerFilterChanged(string value)
    {
        if (value != null)
            ApplyFilter();
    }

    partial void OnEraFilterChanged(string value) => ApplyFilter();

    partial void OnTypeFilterChanged(string value) => ApplyFilter();

    partial void OnThemeFilterChanged(string value)
    {
        if (value != null)
            ApplyFilter();
    }

    partial void OnCatalogSortChanged(string value) => ApplyFilter();

    static TableFacts FactsOf(VpsGame g) => new(g.Manufacturer, g.Year, g.Type, g.Theme);

    bool MatchesCatalogFilters(VpsGame g) =>
        TableFilters.Matches(FactsOf(g), ManufacturerFilter, EraFilter, TypeFilter, ThemeFilter);

    /// <summary>Search fields for a catalog machine, cleaned up once per machine (a refresh brings new ones).</summary>
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<VpsGame, SearchEntry> _gameIndex =
        new();

    static SearchEntry IndexGame(VpsGame g) =>
        TableSearch.Index(
            new SearchFields(
                g.Name,
                g.Manufacturer,
                g.Year,
                g.TableFiles?.SelectMany(t => t.Authors ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase),
                g.Theme,
                g.Designers
            )
        );

    /// <summary>Makers with at least 3 machines and themes with at least 20, most first (built once the catalog has loaded).</summary>
    void UpdateManufacturers()
    {
        HasCatalogFilters =
            ManufacturerFilter != AnyMaker
            || EraFilter != AnyEra
            || TypeFilter != AnyType
            || ThemeFilter != AnyTheme;
        if (Manufacturers.Count > 1 || _allGames.Count == 0)
        {
            return;
        }

        foreach (
            var m in TableFilters
                .Choices(AnyMaker, _allGames.Select(g => g.Manufacturer), 3)
                .Skip(1)
        )
        {
            Manufacturers.Add(m);
        }

        foreach (
            var t in TableFilters
                .Choices(AnyTheme, _allGames.SelectMany(g => g.Theme ?? []), 20)
                .Skip(1)
        )
        {
            Themes.Add(t);
        }
    }

    [RelayCommand]
    void ClearCatalogFilters()
    {
        ManufacturerFilter = AnyMaker;
        EraFilter = AnyEra;
        TypeFilter = AnyType;
        ThemeFilter = AnyTheme;
    }

    void AddPage()
    {
        foreach (var g in _filtered.Skip(_pageStart + Games.Count).Take(PageSize))
        {
            Games.Add(new GameItem(g, _installedIds.Contains(g.Id)));
        }

        UpdatePaging();
    }

    /// <summary>Brings back the page of results before the first one shown (after an A–Z jump).</summary>
    [RelayCommand]
    void ShowEarlier()
    {
        var n = Math.Min(PageSize, _pageStart);
        _pageStart -= n;
        for (var i = 0; i < n; i++)
        {
            var g = _filtered[_pageStart + i];
            Games.Insert(i, new GameItem(g, _installedIds.Contains(g.Id)));
        }

        UpdatePaging();
    }

    void UpdatePaging()
    {
        HasMore = _pageStart + Games.Count < _filtered.Count;
        HasEarlier = _pageStart > 0;
        CountLabel =
            _pageStart > 0
                ? $"{_pageStart + 1}–{_pageStart + Games.Count} shown · {_filtered.Count} of {_allGames.Count}"
                : $"{Games.Count} shown · {_filtered.Count} of {_allGames.Count}";
    }

    [RelayCommand]
    void ShowMore() => AddPage();

    [RelayCommand]
    void CloseDetails() => SelectedGame = null;

    void ShowVersions(GameItem? item)
    {
        Versions.Clear();
        if (item == null)
        {
            return;
        }

        var files = item.Game.TableFiles!.Where(IsVisible).OrderByDescending(t => t.UpdatedAt ?? 0);
        foreach (var f in files)
        {
            var file = f;
            Versions.Add(
                new TableVersionItem(
                    item.Game,
                    file,
                    new AsyncRelayCommand(() => InstallAsync(item.Game, file))
                )
            );
        }
    }

    // ---- Install -------------------------------------------------------------

    public ObservableCollection<JobItem> Jobs { get; } = [];

    async Task<bool> InstallCoreAsync(VpsGame game, VpsTableFile tf, InstalledTable? replacing)
    {
        var url = tf.Urls?.FirstOrDefault(u => !u.Broken)?.Url;
        if (url == null)
        {
            return false;
        }

        var verb = replacing == null ? "" : "Update: ";
        var job = new JobItem($"{verb}{game.Name} — {string.Join(", ", tf.Authors ?? [])}")
        {
            FileNames = [game.Name],
            FileTypes = [".vpx", ".zip", ".7z", ".rar"],
        };
        Jobs.Insert(0, job);
        _pending.Add(
            new PendingDownload
            {
                Id = job.Id,
                Kind = PendingDownload.TableKind,
                Title = job.Title,
                GameId = game.Id,
                TableFileId = tf.Id,
                TablePath = replacing?.TablePath,
            }
        );
        string? file = null;
        try
        {
            // Prefer files that mention VR when a page lists several builds.
            string[] hint = VpsCatalog.HasFeature(tf, "VR") ? ["VR"] : [];
            file = await FetchAsync(url, job, hint);

            job.Report("Installing files…");
            var installed = await TableInstaller.InstallAsync(
                file,
                game,
                tf,
                Settings,
                job.Report,
                job.Cts.Token,
                replacing
            );
            var done = replacing == null ? "Installed" : $"Updated to v{tf.Version}";
            var getRom = installed.MissingRoms.Count > 0 && FindRomSource(installed) != null;
            // "incl. PuP" tables mostly offer the pack as a second file on their page, which the table download doesn't bring.
            var pupNote =
                VpsCatalog.HasFeature(tf, PupPacks.BundledFeature)
                && !PupPacks.StateOf(installed).IsInstalled
                    ? " Its PuP pack is a separate file on the table's page: get it from PUP on its card."
                    : "";
            // This job is done; the ROM (if any) gets its own entry in the downloads list.
            job.Finish(
                (
                    installed.MissingRoms.Count == 0 ? $"✓ {done}. Ready to play."
                    : getRom ? $"✓ {done}. Getting its ROM next (see below)."
                    : $"✓ {done}. The catalog has no ROM download for it; use Add ROM in My Tables."
                ) + pupNote
            );
            RefreshInstalled();
            ApplyFilter();
            if (getRom)
            {
                // After this returns: an update holds the table busy until then, and GetRomAsync needs it free.
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _ = GetRomAsync(installed),
                    Avalonia.Threading.DispatcherPriority.Background
                );
            }

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
            if (!_closing)
            {
                _pending.Remove(job.Id);
            }
        }
    }

    public string DownloadsFolderText =>
        $"Files are picked up from {_assisted.DownloadsDir}. Saved somewhere else? Use I SAVED IT on the download.";

    /// <summary>A job waiting on you in the browser: you saved the file somewhere other than the Downloads folder.</summary>
    [RelayCommand]
    async Task PickDownloadedFileAsync(JobItem job)
    {
        if (
            await _ui.PickFileAsync(
                "The file you downloaded",
                "*.*",
                startFolder: _assisted.DownloadsDir
            ) is
            { } file
        )
        {
            job.HandOff(file);
        }
    }

    /// <summary>
    /// Downloads a file straight (GitHub etc.); from a table site, opens its page in your browser and
    /// takes the file you download there (or drives the sign-in browser, for a site that allows it).
    /// Downloads in your browser don't take a download slot: the browser does the downloading, and you may
    /// have several pages counting down at once.
    /// </summary>
    Task<string> FetchAsync(string url, JobItem job, string[] hint, string? what = null)
    {
        var downloads = JobDownloadDir(job);
        if (!BrowserDownloader.IsDirectFileUrl(url) && !SitePolicy.AllowsAutomation(url))
        {
            return _assisted.DownloadAsync(url, downloads, job, what);
        }

        return WithDownloadSlotAsync(
            job,
            async () =>
            {
                if (!BrowserDownloader.IsDirectFileUrl(url))
                {
                    return await _browser.DownloadAsync(
                        url,
                        downloads,
                        hint,
                        job.Report,
                        job.Cts.Token
                    );
                }

                var file = Path.Combine(
                    downloads,
                    TableInstaller.SafeName(Path.GetFileName(new Uri(url).LocalPath))
                );
                job.Report("Downloading…");
                await Http.DownloadFileAsync(url, file, job.ProgressReporter, job.Cts.Token);
                return file;
            }
        );
    }

    // ---- Download queue ----------------------------------------------------------
    // Downloads run a few at a time (Setup → "Downloads at once", default 2); the rest wait in the
    // queue. Unpacking and installing don't take a slot, so the next download starts meanwhile.

    public int[] ParallelDownloadChoices { get; } = [1, 2, 3, 4];

    [ObservableProperty]
    public partial int MaxParallelDownloads { get; set; }
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
        if (slots.CurrentCount == 0)
        {
            job.Report("Queued: waiting for a free download slot…");
        }

        await slots.WaitAsync(job.Cts.Token);
        try
        {
            return await download();
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>Each job downloads into its own folder, so parallel downloads with the same file name can't collide.</summary>
    static string JobDownloadDir(JobItem job) =>
        Directory.CreateDirectory(Path.Combine(AppSettings.DataDir, "downloads", job.Id)).FullName;

    /// <summary>
    /// A job installed what it downloaded: delete the copy your browser saved (Setup → "Delete downloads after
    /// installing"). Only on success: a failed install keeps the file, so you can try again with it.
    /// </summary>
    void OnJobSucceeded(JobItem job)
    {
        if (!Settings.DeleteDownloadsAfterInstall)
        {
            return;
        }

        foreach (var f in job.TakeSourceFiles())
        {
            try
            {
                File.Delete(f);
            }
            catch
            { /* open elsewhere or already gone: leave it */
            }
        }
    }

    /// <summary>Takes a finished or failed job off the downloads list.</summary>
    [RelayCommand]
    void DismissJob(JobItem job)
    {
        if (job.IsDone || job.IsError)
        {
            Jobs.Remove(job);
        }
    }

    static void CleanJobDownloads(JobItem job)
    {
        try
        {
            Directory.Delete(
                Path.Combine(AppSettings.DataDir, "downloads", job.Id),
                recursive: true
            );
        }
        catch { }
    }

    // ---- ROMs ------------------------------------------------------------------

    /// <summary>The catalog's ROM download (usually on VPForums) for a table's missing ROM.</summary>
    VpsFile? FindRomSource(InstalledTable t)
    {
        // Missing ROM first; otherwise the one the table already uses (to fetch its whole ROM family).
        var missing = t.MissingRoms.Count > 0 ? t.MissingRoms : t.Manifest.RomNames;
        if (missing.Count == 0)
        {
            return null;
        }

        var game = _allGames.FirstOrDefault(g => g.Id == t.Manifest.GameId);
        bool Matches(VpsFile r, string name) =>
            string.Equals(r.Version, name, StringComparison.OrdinalIgnoreCase)
            && UsableRomUrl(r) != null;
        foreach (var name in missing)
        {
            // This table's own catalog entry first; a parent set or a borrowed ROM (an original running
            // Attack from Mars's ROM) is often only listed under the machine it came from.
            if (game?.RomFiles?.FirstOrDefault(r => Matches(r, name)) is { } own)
            {
                return own;
            }

            if (
                _allGames.SelectMany(g => g.RomFiles ?? []).FirstOrDefault(r => Matches(r, name)) is
                { } other
            )
            {
                return other;
            }
        }
        return null;
    }

    /// <summary>Stern-made machine, or the catalog points at Stern's game code for it.</summary>
    bool IsSternTable(InstalledTable t)
    {
        if (!t.IsVpx)
        {
            return false;
        }

        var game = _allGames.FirstOrDefault(g => g.Id == t.Manifest.GameId);
        return string.Equals(
                t.Manifest.Manufacturer ?? game?.Manufacturer,
                "Stern",
                StringComparison.OrdinalIgnoreCase
            )
            || (game?.RomFiles ?? []).Any(r =>
                r.Urls?.Any(u => !u.Broken && SternGameCode.IsSternUrl(u.Url)) == true
            );
    }

    /// <summary>The catalog's links to Stern's game code search, or a search by the machine's name.</summary>
    List<string> SternSearchUrls(InstalledTable t)
    {
        var game = _allGames.FirstOrDefault(g => g.Id == t.Manifest.GameId);
        var urls = (game?.RomFiles ?? [])
            .SelectMany(r => r.Urls ?? [])
            .Where(u => !u.Broken && SternGameCode.IsSternUrl(u.Url))
            .Select(u => u.Url)
            .Distinct()
            .ToList();
        if (urls.Count == 0)
        {
            // PinMAME's name for the machine searches better than the catalog's ("Avatar" vs "James Cameron's Avatar").
            var rom = t.Manifest.RomNames.FirstOrDefault();
            var name =
                rom != null
                    ? PinmameInfo.Get(Settings, Path.Combine(t.Folder, "pinmame"), rom)?.Description
                    : null;
            name = name?.Split(" (")[0].Replace(", The", "") ?? t.Manifest.GameName;
            urls.Add(SternGameCode.SearchUrl(name));
        }
        return urls;
    }

    readonly HashSet<string> _romRechecked = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _romCheckOne = new(1);

    /// <summary>
    /// Works out once per table what its ROM really needs (see <see cref="RomRequirements"/>): tables
    /// installed by older versions may list a commented-out ROM name, miss an alias or a parent set.
    /// Reading a big table file takes a moment, so it's done in the background, one table at a time.
    /// </summary>
    async Task RecheckRomNeedAsync(InstalledTable t)
    {
        // VPS tags table files that run without a ROM. Tables installed before that was read, or linked to the
        // catalog after an import, don't have the tag in their manifest yet; take it from the catalog.
        if (
            t.IsVpx
            && t.Manifest.RomNames.Count > 0
            && !RomRequirements.TaggedNoRom(t.Manifest)
            && t.Manifest.TableFileId != ""
            && _allGames
                .FirstOrDefault(g => g.Id == t.Manifest.GameId)
                ?.TableFiles?.FirstOrDefault(f => f.Id == t.Manifest.TableFileId)
                is { } vpsFile
            && VpsCatalog.HasFeature(vpsFile, RomRequirements.NoRomFeature)
        )
        {
            t.Manifest.Features = [.. t.Manifest.Features, RomRequirements.NoRomFeature];
            t.Manifest.RomNames = [];
            t.Manifest.RomParents = [];
            t.Manifest.RomAlias = null;
            // Rescan only once it's saved: a rescan reads the manifest back from disk, so after a failed save it
            // would find the old one and land here again.
            if (TrySaveManifest(t))
            {
                RefreshInstalledSoon();
            }

            return;
        }
        // A parent listed as missing may be out of date: it was noted before the table's own ROM was there, or the
        // ROM was copied in by hand since. Asking PinMAME again takes milliseconds.
        if (
            t.IsVpx
            && t.Manifest.RomParents.Count > 0
            && t.MissingRoms.Count > 0
            && t.Manifest.RomParents.Contains(t.MissingRoms[0], StringComparer.OrdinalIgnoreCase)
        )
        {
            var before = string.Join(",", t.Manifest.RomParents);
            await Task.Run(() => RomRequirements.RefreshParents(t, Settings));
            if (before != string.Join(",", t.Manifest.RomParents))
            {
                if (TrySaveManifest(t))
                {
                    RefreshInstalledSoon();
                }

                return;
            }
        }
        if (
            !t.IsVpx
            || t.Manifest.RomCheck >= RomRequirements.Version
            || !File.Exists(t.TablePath)
            || !_romRechecked.Add(t.TablePath)
        )
        {
            return;
        }
        // A loose .vpx straight in the tables folder has no folder (or manifest) of its own to update.
        if (
            string.Equals(
                Path.GetFullPath(t.Folder).TrimEnd('\\', '/'),
                Path.GetFullPath(Settings.TablesDir).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return;
        }

        await _romCheckOne.WaitAsync();
        bool changed;
        try
        {
            changed = await Task.Run(() => RomRequirements.Update(t, Settings));
        }
        finally
        {
            _romCheckOne.Release();
        }
        if (TrySaveManifest(t) && changed)
        {
            RefreshInstalledSoon();
        }
    }

    /// <summary>False when the manifest couldn't be written (read-only folder, file in use): still corrected for this session.</summary>
    static bool TrySaveManifest(InstalledTable t)
    {
        try
        {
            TableInstaller.SaveManifest(t);
            return true;
        }
        catch
        {
            return false;
        }
    }

    bool _refreshQueued;

    /// <summary>
    /// <see cref="RefreshInstalled"/> once the current work is done, however many tables ask for it. The ROM recheck
    /// calls this: <see cref="RefreshInstalled"/> starts that recheck itself, so a direct call would nest one rescan in another.
    /// </summary>
    void RefreshInstalledSoon()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                _refreshQueued = false;
                RefreshInstalled();
            },
            Avalonia.Threading.DispatcherPriority.Background
        );
    }

    [RelayCommand]
    void SearchRom(InstalledItem item)
    {
        if (item.Table.MissingRoms.FirstOrDefault() is { } rom)
        {
            _ui.OpenExternal(RomSources.VpuSearchUrl(rom));
        }
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
        if (!TryBeginBusy(table.TablePath))
        {
            return; // already fetching this table's ROM
        }

        try
        {
            await GetRomCoreAsync(table);
        }
        finally
        {
            EndBusy(table.TablePath);
        }
    }

    /// <param name="replacing">The installed table this replaces (updates), so its old file is removed.</param>
    async Task<bool> InstallAsync(VpsGame game, VpsTableFile tf, InstalledTable? replacing = null)
    {
        if (replacing == null)
        {
            // Nothing installed to mark busy yet: the catalog file itself, so a second INSTALL (after picking the game
            // again, or a resumed download) doesn't install it into the same folder at the same time.
            var key = $"install:{game.Id}|{tf.Id}";
            if (!TryBeginBusy(key))
            {
                CatalogStatus = $"{game.Name} is already being installed (see Downloads).";
                return false;
            }

            try
            {
                return await InstallCoreAsync(game, tf, null);
            }
            finally
            {
                EndBusy(key);
            }
        }

        if (!TryBeginBusy(replacing.TablePath))
        {
            return false; // already updating this table
        }

        try
        {
            return await InstallCoreAsync(game, tf, replacing);
        }
        finally
        {
            EndBusy(replacing.TablePath);
        }
    }

    /// <summary>Tables with a ROM fetch, update or reinstall running (by table path).</summary>
    readonly HashSet<string> _busyTables = new(StringComparer.OrdinalIgnoreCase);

    bool TryBeginBusy(string tablePath)
    {
        if (!_busyTables.Add(tablePath))
        {
            return false;
        }

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
        foreach (
            var i in _allInstalled.Where(i =>
                string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            i.IsBusy = on;
        }
    }

    async Task GetRomCoreAsync(InstalledTable table)
    {
        HashSet<string> family;
        try
        {
            // Older installs list every ROM VPS knows; read the script to find the one this table loads.
            // Off the UI thread: tables run to hundreds of MB.
            if (
                table.Manifest.RomNames.Count > 1
                && await Task.Run(() => RomDetector.FromVpx(table.TablePath)) is { } exact
            )
            {
                table.Manifest.RomNames = [exact];
                try
                {
                    TableInstaller.SaveManifest(table);
                }
                catch
                { /* still usable this session */
                }
            }

            // The table's ROM set and its parents: the only ROM zips that belong to it.
            family = await Task.Run(() => RomFamily(table));
        }
        catch (Exception e)
        {
            // The table file is open elsewhere (the VPX editor) or gone.
            PlayStatus = $"Couldn't read {table.Manifest.GameName}'s ROM needs: {e.Message}";
            return;
        }
        var job = new JobItem($"ROM for {table.Manifest.GameName}")
        {
            FileNames = [.. table.MissingRoms, .. family, table.Manifest.GameName],
            FileTypes = [".zip", ".7z", ".rar"],
            NamesMustMatch = true,
        };
        Jobs.Insert(0, job);
        _pending.Add(
            new PendingDownload
            {
                Id = job.Id,
                Kind = PendingDownload.RomKind,
                Title = job.Title,
                GameId = table.Manifest.GameId,
                TablePath = table.TablePath,
            }
        );
        string? file = null;
        try
        {
            // Stern tables: Stern's own free game code first (no sign-in, checked by PinMAME before it's kept).
            if (IsSternTable(table) && table.MissingRoms.Count > 0)
            {
                var urls = SternSearchUrls(table);
                var fromStern = new List<string>();
                string? aliasNote = null;
                foreach (var rom in table.MissingRoms.ToList())
                {
                    if (
                        await WithDownloadSlotAsync(
                            job,
                            () =>
                                SternGameCode.InstallAsync(
                                    table,
                                    rom,
                                    urls,
                                    Settings,
                                    job.Report,
                                    job.ProgressReporter,
                                    job.Cts.Token
                                )
                        ) is
                        { } zip
                    )
                    {
                        fromStern.Add(zip);
                    }
                    else if (await SternOtherVersionAsync(table, rom, urls, job) is { } other)
                    {
                        fromStern.Add(other.zip);
                        aliasNote =
                            $" Stern only has {other.version}; the table asks for {rom}, so PinMAME is set to run {other.version} in its place.";
                    }
                }
                if (fromStern.Count > 0)
                {
                    await Task.Run(() => RomRequirements.RefreshParents(table, Settings));
                    try
                    {
                        TableInstaller.SaveManifest(table);
                    }
                    catch { }
                    if (table.MissingRoms.Count == 0)
                    {
                        job.Finish(
                            $"✓ Installed {string.Join(", ", fromStern.Select(Path.GetFileName))} from Stern's site.{aliasNote} Ready to play."
                        );
                        RefreshInstalled();
                        return;
                    }
                }
                job.Report("Stern's site doesn't have it; trying the catalog's other sources…");
            }

            var src =
                FindRomSource(table)
                ?? throw new InvalidOperationException(
                    $"The catalog has no download for {string.Join(" / ", table.MissingRoms)}.zip. Use Add ROM with a file you have."
                );
            var url = UsableRomUrl(src)!;
            // ROM pages often hold every version of the machine's ROM; clones need their parent,
            // so take them all (they're small).
            List<string> files =
                BrowserDownloader.IsDirectFileUrl(url)
                    ? [file = await FetchAsync(url, job, [src.Version!])]
                : SitePolicy.AllowsAutomation(url)
                    ? await WithDownloadSlotAsync(
                        job,
                        () =>
                            _browser.DownloadAllAsync(
                                url,
                                JobDownloadDir(job),
                                job.Report,
                                job.Cts.Token
                            )
                    )
                : await _assisted.DownloadAllAsync(
                    url,
                    JobDownloadDir(job),
                    job,
                    got => RomSetComplete(table, got, src.Version!)
                );
            files = await Task.Run(() => DropOtherMachinesRoms(files, table, family, job));
            job.Report("Installing ROM…");
            var placed = await Task.Run(() => RomInstaller.InstallAll(files, table, src.Version!));
            // With the files in place PinMAME can tell whether the set is complete (parents included).
            job.Report("Checking the ROM set…");
            await Task.Run(() => RomRequirements.RefreshParents(table, Settings));
            try
            {
                TableInstaller.SaveManifest(table);
            }
            catch { }
            var stillMissing = table.MissingRoms.Count > 0;
            var needsParent =
                stillMissing
                && table.MissingRoms.All(r =>
                    table.Manifest.RomParents.Contains(r, StringComparer.OrdinalIgnoreCase)
                );
            job.Finish(
                stillMissing
                    ? needsParent
                        ? $"Installed {string.Join(", ", placed.Select(Path.GetFileName))}. It's a version of {string.Join(" / ", table.MissingRoms)} and also needs "
                            + $"{string.Join(" / ", table.MissingRoms.Select(r => r + ".zip"))} beside it: use Get ROM again, or Add ROM if you have it."
                        : $"Installed {placed.Count} ROM file(s), but none is {string.Join(" / ", table.MissingRoms)}.zip. Use Add ROM if you have it."
                    : placed.Count == 1
                        ? $"✓ Installed {Path.GetFileName(placed[0])}. Ready to play."
                        : $"✓ Installed {placed.Count} ROM files ({string.Join(", ", placed.Select(Path.GetFileNameWithoutExtension))}). Ready to play.",
                error: stillMissing
            );
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
            if (!_closing)
            {
                _pending.Remove(job.Id);
            }
        }
    }

    /// <summary>
    /// Stern's site only keeps a machine's latest code (AC/DC 1.70), while a table may ask for an older
    /// version (acd_168h). When no other source has that exact version, install the version Stern has (the
    /// set it's a clone of, per PinMAME) and alias the table's name to it in pinmame\alias.txt, which PinMAME
    /// honours even for a real set name. Only for the table's own ROM, not a parent it lacks.
    /// </summary>
    async Task<(string zip, string version)?> SternOtherVersionAsync(
        InstalledTable table,
        string rom,
        List<string> urls,
        JobItem job
    )
    {
        if (
            !table.Manifest.RomNames.Contains(rom, StringComparer.OrdinalIgnoreCase)
            || FindRomSource(table) != null
        )
        {
            return null;
        }

        var pinmameDir = Path.Combine(table.Folder, "pinmame");
        var others = await Task.Run(() =>
            PinmameInfo.Family(Settings, pinmameDir, rom).Skip(1).Select(g => g.Name).ToList()
        );
        foreach (var version in others)
        {
            if (
                await WithDownloadSlotAsync(
                    job,
                    () =>
                        SternGameCode.InstallAsync(
                            table,
                            version,
                            urls,
                            Settings,
                            job.Report,
                            job.ProgressReporter,
                            job.Cts.Token
                        )
                )
                is not { } zip
            )
            {
                continue;
            }

            RomRequirements.WriteAlias(pinmameDir, rom, version);
            table.Manifest.RomAlias = rom;
            table.Manifest.RomNames = [version];
            table.Manifest.RomParents = [];
            return (zip, version);
        }
        return null;
    }

    /// <summary>
    /// Would the table's ROM load with these downloads added? They're installed into a scratch copy of the
    /// table's pinmame\roms (alongside what's already there, e.g. a parent) and PinMAME checks the set, the
    /// way it will when the table starts. So a ROM page's wait ends by itself once you've got what's needed:
    /// the ROM, plus its parent when it's a clone that needs one. Without PinMAME, the ROM zip being there will do.
    /// </summary>
    bool RomSetComplete(InstalledTable t, IReadOnlyList<string> downloads, string fallbackName)
    {
        var rom = t.Manifest.RomNames.FirstOrDefault() ?? fallbackName;
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "vrtm-romcheck-" + Guid.NewGuid().ToString("N")[..8]
        );
        try
        {
            var roms = Path.Combine(tmp, "pinmame", "roms");
            Directory.CreateDirectory(roms);
            var own = Path.Combine(t.Folder, "pinmame", "roms");
            if (Directory.Exists(own))
            {
                foreach (var z in Directory.EnumerateFiles(own, "*.zip"))
                {
                    File.Copy(z, Path.Combine(roms, Path.GetFileName(z)), true);
                }
            }

            RomInstaller.InstallAll(
                downloads,
                new InstalledTable(t.Manifest, t.TablePath, tmp),
                rom
            );
            if (!File.Exists(Path.Combine(roms, rom + ".zip")))
            {
                return false;
            }

            return PinmameInfo.Get(Settings, Path.Combine(tmp, "pinmame"), rom) is not { } g
                || g.Found;
        }
        catch
        {
            return false;
        } // not a ROM yet (a half-read archive, a stray file): keep waiting
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch { }
        }
    }

    /// <summary>The table's ROM names and, when VPX's PinMAME can say, the parent sets they're built on.</summary>
    HashSet<string> RomFamily(InstalledTable t)
    {
        var names = new HashSet<string>(
            [.. t.Manifest.RomNames, .. t.Manifest.RomParents, .. t.MissingRoms],
            StringComparer.OrdinalIgnoreCase
        );
        var pinmameDir = Path.Combine(t.Folder, "pinmame");
        foreach (var rom in t.Manifest.RomNames.ToList())
        {
            foreach (var g in PinmameInfo.Family(Settings, pinmameDir, rom))
            {
                names.Add(g.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// Leaves out downloaded ROM zips that PinMAME knows as another machine's set (dungdrag.zip handed to
    /// Beavis and Butt-Head, which runs beav_butt): installed under the table's ROM name, they'd look like
    /// its ROM and fail when the table starts. Versions of the same machine (clones and parents) stay.
    /// Throws when nothing that's left belongs to the table.
    /// </summary>
    List<string> DropOtherMachinesRoms(
        List<string> files,
        InstalledTable t,
        HashSet<string> family,
        JobItem job
    )
    {
        var pinmameDir = Path.Combine(t.Folder, "pinmame");
        var keep = new List<string>();
        var other = new List<(string file, PinmameGame game)>();
        foreach (var f in files)
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (!f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || family.Contains(stem))
            {
                keep.Add(f);
                continue;
            }
            var g = PinmameInfo.Get(Settings, pinmameDir, stem);
            // Not a set PinMAME knows (a site's own file name): it may well be the ROM, renamed when installed.
            if (
                g == null
                || family.Contains(g.Name)
                || (g.CloneOf != null && family.Contains(g.CloneOf))
            )
            {
                keep.Add(f);
            }
            else
            {
                other.Add((f, g));
            }
        }
        if (other.Count == 0)
        {
            return keep;
        }

        var which = string.Join(
            ", ",
            other.Select(o => $"{Path.GetFileName(o.file)} ({o.game.Description})")
        );
        if (keep.Count == 0)
        {
            throw new InvalidOperationException(
                $"Got {which}: that's another machine's ROM, not {string.Join(" / ", t.MissingRoms.DefaultIfEmpty(t.Manifest.RomNames.FirstOrDefault() ?? "this table's"))}.zip. Nothing was installed."
            );
        }

        job.Report($"Skipping {which}: another machine's ROM.");
        return keep;
    }

    [RelayCommand]
    void ClearFinishedJobs()
    {
        foreach (var j in Jobs.Where(j => j.IsDone || j.IsError).ToList())
        {
            Jobs.Remove(j);
        }
    }

    [RelayCommand]
    async Task OpenSiteAsync(string url)
    {
        try
        {
            if (SitePolicy.AllowsAutomation(url))
            {
                await _browser.OpenForSignInAsync(url);
            }
            else
            {
                _ui.OpenExternal(url);
            }
        }
        catch (Exception e)
        {
            CatalogStatus = "Couldn't open the browser: " + e.Message;
        }
    }

    [RelayCommand]
    void OpenPage(string url) => _ui.OpenExternal(url);

    // ---- My tables -------------------------------------------------------------

    public ObservableCollection<InstalledItem> Installed { get; } = [];

    [ObservableProperty]
    public partial string PlayStatus { get; set; } = "";

    public string[] InstalledFilters { get; } =
    ["All", "Favourites", UpdatesFilter, "Most played", "Recently played"];

    /// <summary>The filter that shows only tables with a newer version (the updates banner's SHOW THEM).</summary>
    const string UpdatesFilter = "Updates";

    [RelayCommand]
    void ShowTableUpdates()
    {
        // All of them, as the banner counts them: no search, engine or other filter narrowing the list.
        _refillingChoices = true;
        InstalledSearch = "";
        InstalledEngine = InstalledEngines[0];
        _refillingChoices = false;
        ClearInstalledFilters();
        InstalledFilter = UpdatesFilter;
    }

    [ObservableProperty]
    public partial string InstalledFilter { get; set; } = "All";

    [ObservableProperty]
    public partial string InstalledEmptyText { get; set; } = "";

    [ObservableProperty]
    public partial int InstalledTotal { get; set; }

    partial void OnInstalledFilterChanged(string value) => InstalledFilterChanged(value);

    List<InstalledItem> _allInstalled = [];
    PlayStats _stats = new();

    [RelayCommand]
    void RefreshInstalled()
    {
        // Re-read stats every time: the VR lobby and its launcher write them too.
        _stats = PlayStats.Load();
        _allInstalled = TableInstaller
            .Scan(Settings)
            .Select(t => new InstalledItem(t, this))
            .ToList();
        LoadCollections();
        foreach (var item in _allInstalled)
        {
            item.ApplyStats(_stats);
            item.IsBusy = _busyTables.Contains(item.Table.TablePath); // keep a running fetch's button greyed after a rescan
            item.RefreshUserArtwork();
            item.Compact = IsCompactCards;
            if (item.Table.IsVpx)
            {
                item.TableScale = VpxGraphics.ReadTableScale(item.Table.TablePath);
                item.TableVrHeight = VrFit.ReadTableHeight(item.Table.TablePath);
                item.HasAttractAudio = MediaPackAudio.Existing(item.Table.Folder) != null;
                item.HasLobbyVideo =
                    item.CanHaveLobbyVideo && LobbyVideo.Existing(item.Table.Folder) != null;
                item.HasLoadingVideo = LoadingVideo.Own(item.Table) != null;
                item.HasWheelArt =
                    item.Table.HasOwnFolder && WheelArt.Saved(item.Table.Folder) != null;
                item.HasDmdFix = VrDmdFix.HasBackup(item.Table.TablePath);
                item.DmdFixDeclined = Settings.DmdFixDeclined.Contains(
                    item.Table.TablePath,
                    StringComparer.OrdinalIgnoreCase
                );
                RefreshAddonState(item);
                item.AttractAudioSources = AttractAudioSourcesFor(item.Table);
                item.WheelArtSources = WheelArtSourcesFor(item.Table);
            }
        }
        ShowInstalled();
        _ = RefreshSizesAsync();
        _ = LinkImportedTablesAsync();
    }

    /// <summary>
    /// Tables copied in by Import tables have no catalog entry, so no picture, backglass, add-ons or rules:
    /// link the ones that can be matched surely (CatalogLink), once per table. Needs the catalog loaded.
    /// </summary>
    async Task LinkImportedTablesAsync()
    {
        if (_allGames.Count == 0)
        {
            return;
        }

        var games = _allGames;
        var tables = _allInstalled.Select(i => i.Table).ToList();
        var linked = await Task.Run(() =>
        {
            var changed = new List<InstalledTable>();
            foreach (var t in tables)
            {
                try
                {
                    if (CatalogLink.TryLink(t, games))
                    {
                        TableInstaller.SaveManifest(t);
                        if (t.Manifest.GameId != "")
                        {
                            changed.Add(t);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Read-only folder or the file's in use: try again next time.
                }
            }
            return changed;
        });
        if (linked.Count == 0)
        {
            return;
        }

        foreach (var item in _allInstalled.Where(i => linked.Contains(i.Table)))
        {
            item.RefreshUserArtwork(); // re-reads ImgUrl
        }
        ShowInstalled(); // add-ons, rules and the type / theme filters now know the game
    }

    public string[] InstalledEngines { get; } =
    ["All engines", "VPX", "Future Pinball", "Pinball FX", "Pinball M"];
    public string[] InstalledSorts { get; } = ["A–Z", "Recently installed", "Largest first"];

    [ObservableProperty]
    public partial string InstalledSearch { get; set; } = "";

    [ObservableProperty]
    public partial string InstalledEngine { get; set; } = "All engines";

    [ObservableProperty]
    public partial string InstalledSort { get; set; } = "A–Z";

    partial void OnInstalledSearchChanged(string value) => InstalledFilterChanged(value);

    partial void OnInstalledEngineChanged(string value) => InstalledFilterChanged(value);

    partial void OnInstalledSortChanged(string value) => FilterInstalled();

    bool MatchesInstalledEngine(InstalledItem i) =>
        InstalledEngine switch
        {
            "VPX" => i.Table.Manifest.Engine == TableManifest.Vpx,
            "Future Pinball" => i.Table.Manifest.Engine == TableManifest.FuturePinball,
            "Pinball FX" => i.Table.Manifest.Engine == TableManifest.PinballFx,
            "Pinball M" => i.Table.Manifest.Engine == TableManifest.PinballM,
            _ => true,
        };

    /// <summary>Fills the My Tables grid from the scan, and refreshes what each table has waiting (updates, add-ons, rules).</summary>
    void ShowInstalled()
    {
        FilterInstalled();
        ApplyTableUpdates();
    }

    /// <summary>
    /// Fills the My Tables grid using the chosen search, filters and order. Typing in the search box runs this on
    /// every keystroke, so it leaves the update check alone, and the cards alone when the list hasn't changed.
    /// </summary>
    void FilterInstalled()
    {
        UpdateInstalledChoices();
        HasInstalledFilters =
            InstalledMakerFilter != TableFilters.AnyMaker
            || InstalledEraFilter != TableFilters.AnyEra
            || InstalledTypeFilter != TableFilters.AnyType
            || InstalledThemeFilter != TableFilters.AnyTheme
            || IsCollectionChosen;
        var search = TableSearch.Parse(InstalledSearch);
        var scores = new Dictionary<InstalledItem, double>();
        var pool = _allInstalled
            .Where(MatchesInstalledEngine)
            .Where(MatchesInstalledFilters)
            .Where(MatchesInstalledCollection)
            .Where(i => search == null || (scores[i] = search.Score(SearchEntryOf(i))) > 0)
            .ToList();
        IEnumerable<InstalledItem> q = InstalledFilter switch
        {
            "Favourites" when search != null => pool.Where(i => i.IsFavourite)
                .OrderByDescending(i => scores[i]),
            "Favourites" => pool.Where(i => i.IsFavourite)
                .OrderBy(i => SortKey(i.Title), StringComparer.OrdinalIgnoreCase),
            UpdatesFilter => pool.Where(i => i.HasUpdate)
                .OrderBy(i => SortKey(i.Title), StringComparer.OrdinalIgnoreCase),
            "Most played" => pool.Where(i => i.Plays > 0)
                .OrderByDescending(i => i.Plays)
                .ThenByDescending(i => i.LastPlayed),
            "Recently played" => pool.Where(i => i.LastPlayed != null)
                .OrderByDescending(i => i.LastPlayed),
            _ => InstalledSort switch
            {
                "Recently installed" => pool.OrderByDescending(i => i.Table.Manifest.InstalledAt),
                "Largest first" => pool.OrderByDescending(i => i.SizeBytes),
                // Searching: best matches first.
                _ when search != null => pool.OrderByDescending(i => scores[i])
                    .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
                // Favourites first, then A–Z.
                _ => pool.OrderByDescending(i => i.IsFavourite)
                    .ThenBy(i => SortKey(i.Title), StringComparer.OrdinalIgnoreCase),
            },
        };
        var shown = q.ToList();
        if (!Installed.SequenceEqual(shown))
        {
            Installed.Clear();
            foreach (var i in shown)
            {
                Installed.Add(i);
            }
        }
        UpdateInstalledLetters(
            search == null
                && InstalledFilter switch
                {
                    "Favourites" or UpdatesFilter => true,
                    "Most played" or "Recently played" => false,
                    _ => InstalledSort == InstalledSorts[0],
                }
        );

        InstalledTotal = _allInstalled.Count;
        InstalledEmptyText =
            Installed.Count > 0 || _allInstalled.Count == 0 ? ""
            : !_allInstalled.Any(MatchesInstalledCollection)
                ? InstalledCollectionChoice switch
                {
                    { IsSmart: true } c =>
                        $"Nothing in “{c.Name}” right now. It fills itself as your tables and plays change.",
                    { } c =>
                        $"Nothing in “{c.Name}” yet. Add tables from a card's ⋯ menu → Collections.",
                    _ => "",
                }
            : search != null || HasInstalledFilters || InstalledEngine != InstalledEngines[0]
                ? "No installed tables match the search and filters."
            : InstalledFilter switch
            {
                "Favourites" =>
                    "No favourites yet. Tap the ☆ on a table (or press the right grip on it in the VR lobby).",
                UpdatesFilter when _allGames.Count == 0 =>
                    "Updates are checked once the catalog has loaded.",
                UpdatesFilter =>
                    "No updates: every table from the catalog is on its newest version.",
                _ =>
                    "Nothing played yet. Your plays from here and from the VR lobby show up in this list.",
            };
        OnPropertyChanged(nameof(CanSaveSmart)); // follows the search and every filter
    }

    [RelayCommand]
    void ToggleFavourite(InstalledItem item)
    {
        var on = !item.IsFavourite;
        try
        {
            _stats = PlayStats.Mutate(st => st.SetFavourite(item.Table.TablePath, on));
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't save the favourite: " + e.Message;
            return;
        }
        item.ApplyStats(_stats);
        if (InstalledFilter != "Most played" && InstalledFilter != "Recently played")
        {
            FilterInstalled();
        }
    }

    [RelayCommand]
    Task PlayVr(InstalledItem item) => PlayAsync(item, vr: true);

    [RelayCommand]
    Task PlayDesktop(InstalledItem item) => PlayAsync(item, vr: false);

    async Task PlayAsync(InstalledItem item, bool vr)
    {
        var engine = Engines.For(item.Table.Manifest.Engine);
        System.Diagnostics.Process? proc = null;
        var waitsInBackground = false; // the background wait disposes it, once the table has closed
        try
        {
            if (vr && !await PreflightAsync(item, engine))
            {
                PlayStatus = "";
                return;
            }
            if (
                vr
                && ZenGames.For(engine.Id) is { } zen
                && Settings.ShowZenReminder
                && !await ZenReminderAsync(zen)
            )
            {
                PlayStatus = "";
                return;
            }
            PlayStatus = $"Starting {item.Title}…";
            proc = await engine.LaunchAsync(Settings, item.Table, vr, CancellationToken.None);
            PlayStatus = $"Playing {item.Title}…";
            var started = DateTime.Now;
            var path = item.Table.TablePath;
            TrySaveStats(st => st.RecordPlay(path, started));
            item.ApplyStats(_stats);
            if (!ReturnAfterPlay)
            {
                // Still add the time played, in the background. The process stays undisposed until then: disposing it
                // stops watching for the exit, and the wait would never end.
                System.Diagnostics.Process running = proc;
                waitsInBackground = true;
                _ = running
                    .WaitForExitAsync()
                    .ContinueWith(
                        _ =>
                        {
                            TrySaveStats(st => st.AddTime(path, DateTime.Now - started));
                            running.Dispose();
                        },
                        TaskScheduler.Default
                    );
                return;
            }
            // Pick-and-return loop: get out of the way, come back when the table closes.
            _ui.HideForPlay();
            await proc.WaitForExitAsync();
            TrySaveStats(st => st.AddTime(path, DateTime.Now - started));
            item.ApplyStats(_stats);
            PlayStatus = $"Finished {item.Title}. Pick your next table.";
            _ui.ShowAfterPlay();
            if (item.Table.IsVpx)
            {
                // Save what VPX changed now, not on the background wait, so the panel and card show it.
                VpxIniLayers.Finish(proc);
                RefreshControls(); // keys remapped in VPX's menu are saved only now, after the window came back
                LoadVrView(); // VPX saves the table position when it closes
                item.TableVrHeight = VrFit.ReadTableHeight(item.Table.TablePath);
            }

            await ExplainVpxExitAsync(item, started);
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
            if (!waitsInBackground)
            {
                proc?.Dispose();
            }

            _ui.ShowAfterPlay();
        }
    }

    static string MetaLinkApp =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Oculus",
            "Support",
            "oculus-client",
            "OculusClient.exe"
        );

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
            if (hs.State is HeadsetState.Ready or HeadsetState.Unknown)
            {
                return true;
            }

            var hasLinkApp = File.Exists(MetaLinkApp);
            var message =
                hs.State == HeadsetState.NotConnected
                    ? "Your Quest isn't connected to the PC, so the table would open on your monitor instead.\n\n"
                        + "1. Open Meta Quest Link on this PC.\n"
                        + "2. Connect the Quest with a USB-C cable, or use Air Link (same Wi-Fi).\n"
                        + "3. In the headset: Quick Settings → Link → choose your PC → Launch.\n\n"
                        + "When you're in the grey Link home, press Try again."
                    : "There's no VR runtime on this PC. Install the Meta Quest Link app, connect your Quest, "
                        + "and set Meta Quest Link as the active OpenXR runtime (Link app → Settings → General).";
            List<string> buttons = ["TRY AGAIN"];
            if (hasLinkApp)
            {
                buttons.Add("OPEN META QUEST LINK");
            }

            buttons.AddRange(["PLAY ANYWAY", "CANCEL"]);

            var pick = await _ui.ChoiceAsync("Headset not connected", message, [.. buttons]);
            var label = pick >= 0 ? buttons[pick] : "CANCEL";
            switch (label)
            {
                case "TRY AGAIN":
                    continue;
                case "OPEN META QUEST LINK":
                    _ui.OpenExternal(MetaLinkApp);
                    continue;
                case "PLAY ANYWAY":
                    return true;
                default:
                    return false;
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
                false =>
                    "⚠ No gamepad found. Connect an Xbox-style controller: the Quest controllers are switched off for this game (they confuse it and can crash it).",
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
        var pick = await _ui.ChoiceAsync(
            $"Before {g.Title} starts in VR",
            string.Join("\n\n", lines),
            buttons
        );
        if (pick == 1)
        {
            ShowZenReminder = false;
        }

        return pick is 0 or 1;
    }

    // ---- VR lobby ------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsLobbyRunning { get; set; }

    /// <summary>
    /// One click: get Godot (first time), rebuild the lobby if its code changed, start it, and get
    /// out of the way until you're done. The lobby quits while a table runs (handing the headset to
    /// the table) and is restarted afterwards, so this waits until neither is running.
    /// </summary>
    [RelayCommand]
    async Task LaunchLobbyAsync()
    {
        if (IsBatchRecording && _batchUsesVpx)
        {
            PlayStatus =
                "Tables are being recorded in VPX: wait for them, or press STOP, then launch the VR lobby.";
            return;
        }

        if (IsLobbyRunning || !await LobbyVideoTipAsync())
        {
            return;
        }

        var lobbyDir = LobbyRunner.FindLobbyDir();
        if (lobbyDir == null)
        {
            PlayStatus = "Couldn't find the VrLobby project next to this app.";
            return;
        }
        // The lobby runs on this app's own .NET; from source (or where that can't be set up), on the installed one.
        // Off the UI thread: it runs mklink and copies hostfxr.dll.
        var (dotnetRoot, noBundled) = await Task.Run(() =>
        {
            var r = LobbyDotnet.Prepare(
                AppContext.BaseDirectory,
                LobbyDotnet.DefaultRoot,
                Environment.Version.ToString(),
                out var why
            );
            return (r, why);
        });
        if (dotnetRoot == null && !LobbyDotnet.HasLobbyRuntime(LobbyDotnet.DotnetRoots()))
        {
            // Without one Godot opens a black, empty lobby with no error to go on.
            var missing = new JobItem("VR lobby");
            Jobs.Insert(0, missing);
            missing.Finish(
                $"The VR lobby couldn't use this app's own .NET ({noBundled}) and no .NET 10 or newer is installed. Install \".NET Runtime 10\" for Windows x64 from "
                    + LobbyDotnet.RuntimeUrl
                    + ", then start the lobby again.",
                error: true
            );
            PlayStatus = "";
            return;
        }
        if (!await EnsureHeadsetAsync())
        {
            PlayStatus = "";
            return;
        }

        IsLobbyRunning = true;
        // Listed under Downloads only while it's being prepared (first-time Godot download, a rebuild) or if it
        // fails: once the lobby is up there's nothing to follow, and a finished entry would need clearing.
        var job = new JobItem("VR lobby");
        Jobs.Insert(0, job);
        var hidden = false;
        try
        {
            var godot = await LobbyRunner.EnsureGodotAsync(
                job.ProgressReporter,
                job.Report,
                job.Cts.Token
            );
            if (LobbyRunner.NeedsBuild(lobbyDir))
            {
                await LobbyRunner.BuildAsync(lobbyDir, job.Report, job.Cts.Token);
            }

            await LobbyRunner.ImportIfNeededAsync(
                godot,
                lobbyDir,
                dotnetRoot,
                job.Report,
                job.Cts.Token
            );

            using var lobby = LobbyRunner.Start(godot, lobbyDir, dotnetRoot);
            Jobs.Remove(job);
            PlayStatus =
                "VR lobby running. Put your headset on; this window comes back when you quit the lobby.";
            _ui.HideForPlay();
            hidden = true;
            await WaitForLobbySessionAsync(lobby, Path.GetFileNameWithoutExtension(godot));
            PlayStatus = "Back from the VR lobby.";
        }
        catch (OperationCanceledException)
        {
            Jobs.Remove(job);
            PlayStatus = "VR lobby cancelled.";
        }
        catch (Exception e)
        {
            job.Finish("Couldn't start the lobby: " + e.Message, error: true);
            PlayStatus = "";
        }
        finally
        {
            IsLobbyRunning = false;
            if (hidden)
            {
                _ui.ShowAfterPlay();
            }

            RefreshInstalled(); // favourites and plays may have changed in the lobby
        }
    }

    /// <summary>Waits for the lobby to exit, then keeps waiting while a table it launched (and the lobby it restarts) runs.</summary>
    static async Task WaitForLobbySessionAsync(
        System.Diagnostics.Process lobby,
        string godotProcessName
    )
    {
        var lobbyStarted = lobby.StartTime;
        await lobby.WaitForExitAsync();
        var self = Environment.ProcessId;
        bool StartedSinceLobby(System.Diagnostics.Process p)
        {
            try
            {
                return p.Id != self && p.StartTime >= lobbyStarted;
            }
            catch
            {
                return false;
            }
        }
        bool Busy() =>
            System.Diagnostics.Process.GetProcessesByName(godotProcessName).Length > 0
            // "VrTableManager.exe --play …" is how the lobby launches a table (another manager window
            // you opened yourself started earlier, so it doesn't count).
            || System
                .Diagnostics.Process.GetProcessesByName("VrTableManager")
                .Any(StartedSinceLobby);
        // Allow a few seconds for the hand-over between the lobby quitting and the table (or lobby) starting.
        var idleSince = DateTime.UtcNow;
        while (DateTime.UtcNow - idleSince < TimeSpan.FromSeconds(4))
        {
            await Task.Delay(500);
            if (Busy())
            {
                idleSince = DateTime.UtcNow;
            }
        }
    }

    void TrySaveStats(Action<PlayStats> change)
    {
        try
        {
            _stats = PlayStats.Mutate(change);
        }
        catch (Exception e)
        {
            // Play counting must never get in the way of playing: just say so. (Also called from the background wait.)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                PlayStatus = "Couldn't save the play count and time: " + e.Message
            );
        }
    }

    [RelayCommand]
    void OpenTableFolder(InstalledItem item) => _ui.OpenExternal(item.Table.Folder);

    [RelayCommand]
    async Task AddRomAsync(InstalledItem item)
    {
        // Filter to the file it needs, starting where browsers save downloads.
        var wanted =
            item.Table.MissingRoms.FirstOrDefault()
            ?? item.Table.Manifest.RomNames.FirstOrDefault();
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        var file = await _ui.PickFileAsync(
            $"Choose the ROM zip for {item.Title}",
            "*.zip",
            wanted != null ? wanted + ".zip" : null,
            downloads
        );
        if (file == null)
        {
            return;
        }

        try
        {
            TableInstaller.AddRom(item.Table, file);
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't add the ROM: " + e.Message;
            return;
        }
        RefreshInstalled();
    }

    [RelayCommand]
    async Task RemoveAsync(InstalledItem item)
    {
        // A ROM fetch, update, add-on or recording for this table would write into the folder as it goes.
        var busyText =
            $"{item.Title} has a job running (see Downloads): wait for it, or cancel it, then remove the table.";
        if (_busyTables.Contains(item.Table.TablePath))
        {
            PlayStatus = busyText;
            return;
        }

        var what = item.Table.IsVpx
            ? $"the folder\n{item.Table.Folder}"
            : $"\n{item.Table.TablePath}";
        if (!await _ui.ConfirmAsync("Remove table", $"Delete {item.Title}?\n\nThis removes {what}"))
        {
            return;
        }

        if (_busyTables.Contains(item.Table.TablePath))
        {
            PlayStatus = busyText; // one started while you were asked
            return;
        }

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

    public string[] RefreshRates { get; } =
    ["0 (headset default)", "72 Hz", "80 Hz", "90 Hz", "120 Hz"];

    [ObservableProperty]
    public partial string VpxDir { get; set; }

    [ObservableProperty]
    public partial string TablesDir { get; set; }

    [ObservableProperty]
    public partial string FpDir { get; set; }

    [ObservableProperty]
    public partial bool HideAdult { get; set; }

    [ObservableProperty]
    public partial bool DeleteDownloadsAfterInstall { get; set; }

    [ObservableProperty]
    public partial bool ReturnAfterPlay { get; set; }

    [ObservableProperty]
    public partial bool VpxUseDoNotUseBuilds { get; set; }

    [ObservableProperty]
    public partial bool VpxUseNightlyBuilds { get; set; }

    [ObservableProperty]
    public partial bool FpRunAsAdmin { get; set; }

    [ObservableProperty]
    public partial string RefreshRate { get; set; }

    [ObservableProperty]
    public partial string VpxStatus { get; set; } = "";

    [ObservableProperty]
    public partial string FpStatus { get; set; } = "";

    [ObservableProperty]
    public partial string UevrDir { get; set; }

    [ObservableProperty]
    public partial string PfxStatus { get; set; } = "";

    [ObservableProperty]
    public partial string LatestVpx { get; set; } = "";

    [ObservableProperty]
    public partial bool IsInstallingVpx { get; set; }

    [ObservableProperty]
    public partial double VpxProgress { get; set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Persist setup fields as they change.
        switch (e.PropertyName)
        {
            case nameof(VpxDir):
                Settings.VpxDir = VpxDir;
                break;
            case nameof(TablesDir):
                Settings.TablesDir = TablesDir;
                break;
            case nameof(FpDir):
                Settings.FpDir = FpDir;
                break;
            case nameof(UevrDir):
                Settings.UevrDir = UevrDir;
                break;
            case nameof(HideAdult):
                Settings.HideAdult = HideAdult;
                ApplyFilter();
                break;
            case nameof(DeleteDownloadsAfterInstall):
                Settings.DeleteDownloadsAfterInstall = DeleteDownloadsAfterInstall;
                break;
            case nameof(ReturnAfterPlay):
                Settings.ReturnAfterPlay = ReturnAfterPlay;
                break;
            case nameof(VpxUseDoNotUseBuilds):
                Settings.VpxUseDoNotUseBuilds = VpxUseDoNotUseBuilds;
                break;
            case nameof(VpxUseNightlyBuilds):
                Settings.VpxUseNightlyBuilds = VpxUseNightlyBuilds;
                break;
            case nameof(FpRunAsAdmin):
                Settings.FpRunAsAdmin = FpRunAsAdmin;
                break;
            case nameof(ApplyPinballUevrProfile):
                Settings.ApplyPinballUevrProfile = ApplyPinballUevrProfile;
                RefreshSetupStatus();
                break;
            case nameof(ShowZenReminder):
                Settings.ShowZenReminder = ShowZenReminder;
                break;
            case nameof(RefreshRate):
                Settings.VrRefreshRate = int.TryParse(RefreshRate?.Split(' ')[0], out var r)
                    ? r
                    : 0;
                break;
            case nameof(AswMode):
                Settings.MetaAswMode = AswMode;
                break;
            case nameof(WarnSteamVrRuntime):
                Settings.WarnSteamVrRuntime = WarnSteamVrRuntime;
                break;
            case nameof(ExplainFailedLaunches):
                Settings.ExplainFailedLaunches = ExplainFailedLaunches;
                break;
            case nameof(UseBuiltInPinmame):
                Settings.UseBuiltInPinmame = UseBuiltInPinmame;
                break;
            case nameof(UsePupPlugin):
                Settings.UsePupPlugin = UsePupPlugin;
                break;
            case nameof(UseDisplayPlugins):
                Settings.UseDisplayPlugins = UseDisplayPlugins;
                break;
            case nameof(VrAutoBackglass):
                Settings.VrAutoBackglass = VrAutoBackglass;
                break;
            case nameof(UseVrGraphics):
                Settings.UseVrGraphics = UseVrGraphics;
                if (!_loadingGraphics)
                {
                    LoadGraphics(); // now shows what VR play will use: the panel's, or VPX's own
                    GraphicsStatus = UseVrGraphics
                        ? "✓ VR play uses these settings again from the next table."
                        : "✓ VR play uses VPX's own graphics settings from the next table. These are kept for when you turn them back on.";
                }
                break;
            case nameof(DesktopBloomOff):
                Settings.DesktopBloomOff = DesktopBloomOff;
                break;
            case nameof(AutoVrRoom):
                Settings.AutoVrRoom = AutoVrRoom;
                break;
            case nameof(LobbyRecordsRomAudio):
                Settings.LobbyRecordsRomAudio = LobbyRecordsRomAudio;
                break;
            case nameof(LobbyWaitsForTable):
                Settings.LobbyWaitsForTable = LobbyWaitsForTable;
                break;
            case nameof(LobbyStaysOpen):
                Settings.LobbyStaysOpen = LobbyStaysOpen;
                break;
            case nameof(LobbyLoadingVideos):
                Settings.LobbyLoadingVideos = LobbyLoadingVideos;
                break;
            default:
                return;
        }
        Settings.Save();
        if (e.PropertyName == nameof(VpxDir))
        {
            LoadGraphics();
        }

        if (
            e.PropertyName
            is nameof(VpxDir)
                or nameof(TablesDir)
                or nameof(FpDir)
                or nameof(UevrDir)
        )
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
        RefreshVrStatus();
    }

    [ObservableProperty]
    public partial bool PfxHasUevrBackup { get; set; }

    [ObservableProperty]
    public partial bool PmHasUevrBackup { get; set; }

    [ObservableProperty]
    public partial string PfxProfileStatus { get; set; } = "";

    [ObservableProperty]
    public partial string PmProfileStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool ApplyPinballUevrProfile { get; set; }

    [ObservableProperty]
    public partial bool ShowZenReminder { get; set; }

    /// <summary>The pinball UEVR settings, with why each one is set (shown on Setup).</summary>
    public string PinballProfileSummary { get; } =
        string.Join(
            "\n",
            UevrPinballProfile.Settings.Select(s => $"• {s.Key} = {s.Value}: {s.Why}")
        );

    string DescribeUevrProfile(ZenGame g)
    {
        var parts = new List<string>
        {
            UevrPinballProfile.IsApplied(g) ? "Pinball settings are in its UEVR profile."
            : Settings.ApplyPinballUevrProfile
                ? "Pinball settings will be applied at the next VR launch."
            : "Pinball settings are off; UEVR uses its own.",
        };
        if (UevrPinballProfile.LastRuntime(g) is { } rt)
        {
            parts.Add(
                rt == "OpenVR"
                    ? "Last run used OpenVR: pick OpenXR in the UEVR window next time."
                    : "Last run used OpenXR."
            );
        }

        if (UevrPinballProfile.HasBackup(g))
        {
            parts.Add("Your original profile is backed up.");
        }

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
            msg =
                r.Changed.Count == 0
                    ? "✓ Already on pinball defaults."
                    : $"✓ Reset {r.Changed.Count} UEVR setting(s) to pinball defaults{(r.BackedUp ? " (original backed up)" : "")}.";
        }
        catch (Exception e)
        {
            msg = "Couldn't update the UEVR profile: " + e.Message;
        }
        RefreshSetupStatus();
        if (g == ZenGames.PinballM)
        {
            PmProfileStatus = msg + " " + PmProfileStatus;
        }
        else
        {
            PfxProfileStatus = msg + " " + PfxProfileStatus;
        }
    }

    [RelayCommand]
    async Task RestoreUevrOriginalAsync(string? engineId)
    {
        var g = ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx;
        if (!UevrPinballProfile.HasBackup(g))
        {
            return;
        }

        if (
            !await _ui.ConfirmAsync(
                "Restore UEVR profile",
                $"Put back {g.Title}'s UEVR profile as it was before this app changed it?\n\n"
                    + (
                        Settings.ApplyPinballUevrProfile
                            ? "Pinball settings will also be switched off, so they aren't re-applied at the next launch."
                            : ""
                    ),
                "Restore"
            )
        )
        {
            return;
        }

        try
        {
            UevrPinballProfile.RestoreOriginal(g);
        }
        catch (Exception e)
        {
            var msg = "Couldn't restore the UEVR profile: " + e.Message;
            if (g == ZenGames.PinballM)
            {
                PmStatus = msg;
            }
            else
            {
                PfxStatus = msg;
            }

            return;
        }
        ApplyPinballUevrProfile = false;
        RefreshSetupStatus();
    }

    [ObservableProperty]
    public partial string PmStatus { get; set; } = "";

    [RelayCommand]
    public async Task CheckVpxUpdateAsync()
    {
        try
        {
            var releases = await VpxSetup.FindReleasesAsync(
                CancellationToken.None,
                Settings.VpxUseDoNotUseBuilds,
                Settings.VpxUseNightlyBuilds
            );
            var (text, hasUpdate) = VpxSetup.DescribeUpdate(
                releases,
                VpxSetup.InstalledVersion(Settings),
                Settings.VpxKeptBuild
            );
            LatestVpx = hasUpdate ? $"{text} ({releases.Latest!.Size / 1_048_576} MB)" : text;
        }
        catch (Exception e)
        {
            LatestVpx = "Couldn't check GitHub: " + e.Message;
        }
    }

    CancellationTokenSource? _vpxInstallCts;

    /// <summary>The bar can't show progress (checking GitHub, extracting): it just shows activity.</summary>
    [ObservableProperty]
    public partial bool VpxProgressUnknown { get; set; }

    [RelayCommand]
    void CancelVpxInstall() => _vpxInstallCts?.Cancel();

    /// <summary>The build list: releases and (with nightly builds on) recent nightlies, to go back to an older one.</summary>
    public ObservableCollection<VpxBuildChoice> VpxBuilds { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallChosenVpxCommand))]
    public partial VpxBuildChoice? ChosenVpxBuild { get; set; }

    [ObservableProperty]
    public partial bool HasVpxBuilds { get; set; }

    [RelayCommand]
    async Task LoadVpxBuildsAsync()
    {
        LatestVpx = "Getting the list of VPX builds from GitHub…";
        try
        {
            var builds = await VpxSetup.FindBuildsAsync(
                CancellationToken.None,
                Settings.VpxUseDoNotUseBuilds,
                Settings.VpxUseNightlyBuilds
            );
            VpxBuilds.Clear();
            foreach (var b in builds)
            {
                VpxBuilds.Add(b);
            }
            HasVpxBuilds = VpxBuilds.Count > 0;
            MarkInstalledVpxBuild();
            ChosenVpxBuild =
                VpxBuilds.FirstOrDefault(b => b.Installed) ?? VpxBuilds.FirstOrDefault();
            LatestVpx = HasVpxBuilds
                ? $"{VpxBuilds.Count} builds. Pick one and click INSTALL THIS BUILD."
                    + (
                        Settings.VpxUseNightlyBuilds
                            ? ""
                            : " Turn on nightly builds below to list those too."
                    )
                : "No VPX builds found for this OS.";
        }
        catch (Exception e)
        {
            LatestVpx = "Couldn't check GitHub: " + e.Message;
        }
    }

    /// <summary>Marks which build in the list is the installed one (after the list loads, and after an install).</summary>
    void MarkInstalledVpxBuild()
    {
        var installed = VpxSetup.InstalledVersion(Settings);
        var chosen = ChosenVpxBuild;
        for (var i = 0; i < VpxBuilds.Count; i++)
        {
            var b = VpxBuilds[i];
            var now = VpxSetup.IsInstalled(b, installed);
            if (b.Installed != now)
            {
                VpxBuilds[i] = b with { Installed = now };
                if (ReferenceEquals(b, chosen))
                {
                    ChosenVpxBuild = VpxBuilds[i];
                }
            }
        }
    }

    bool CanInstallChosenVpx() => ChosenVpxBuild != null;

    /// <summary>The installed build can be picked too: installing it again repairs a damaged VPX folder.</summary>
    public string ChosenVpxButtonText =>
        ChosenVpxBuild is { Installed: true } ? "REINSTALL THIS BUILD" : "INSTALL THIS BUILD";

    partial void OnChosenVpxBuildChanged(VpxBuildChoice? value) =>
        OnPropertyChanged(nameof(ChosenVpxButtonText));

    /// <summary>
    /// Installs the build picked from the list, older or newer, and keeps it: newer builds aren't offered as
    /// updates until INSTALL / UPDATE VPX is clicked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallChosenVpx))]
    Task InstallChosenVpxAsync() =>
        RunVpxInstallAsync(async ct =>
        {
            if (ChosenVpxBuild is not { } build)
            {
                return null;
            }
            LatestVpx = $"Finding the download for {build.Label}…";
            if (await VpxSetup.ResolveAsync(build, ct) is not { } rel)
            {
                LatestVpx =
                    "GitHub no longer has that nightly's download (they expire after a while). Pick a newer one.";
                return null;
            }
            Settings.VpxKeptBuild = rel.Tag;
            Settings.Save();
            return rel;
        });

    [RelayCommand]
    Task InstallVpxAsync() =>
        RunVpxInstallAsync(async ct =>
        {
            // The newest build again: a build picked from the list stops being kept.
            if (Settings.VpxKeptBuild != null)
            {
                Settings.VpxKeptBuild = null;
                Settings.Save();
            }
            LatestVpx = "Checking GitHub for the newest VPX build…";
            var releases = await VpxSetup.FindReleasesAsync(
                ct,
                Settings.VpxUseDoNotUseBuilds,
                Settings.VpxUseNightlyBuilds
            );
            var installed = VpxSetup.InstalledVersion(Settings);
            var (text, hasUpdate) = VpxSetup.DescribeUpdate(releases, installed);
            // Only install what's offered: no "DO NOT USE" build unless allowed, never an older one over a newer install.
            if (releases.Latest is not { } rel || (installed != null && !hasUpdate))
            {
                LatestVpx = text;
                return null;
            }
            return rel;
        });

    /// <summary>Installs the build <paramref name="pick"/> chooses (null: nothing to install; it says why).</summary>
    async Task RunVpxInstallAsync(Func<CancellationToken, Task<VpxRelease?>> pick)
    {
        if (IsInstallingVpx)
        {
            return; // UPDATE ALL while an install from the VPX section runs: one at a time into the VPX folder
        }

        IsInstallingVpx = true;
        VpxProgress = 0;
        VpxProgressUnknown = true;
        using var cts = _vpxInstallCts = new CancellationTokenSource();
        try
        {
            if (await pick(cts.Token) is not { } rel)
            {
                return;
            }
            var progress = new Progress<double>(p =>
            {
                VpxProgressUnknown = false;
                VpxProgress = p * 100;
            });
            await VpxSetup.InstallAsync(
                Settings,
                rel,
                progress,
                s =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LatestVpx = s;
                        if (s.StartsWith("Extracting"))
                        {
                            VpxProgressUnknown = true;
                        }
                    }),
                cts.Token
            );
            LatestVpx =
                $"Installed {rel.Tag}."
                + (
                    Settings.VpxKeptBuild == rel.Tag
                        ? " Newer builds won't be offered as updates until you click INSTALL / UPDATE VPX."
                        : ""
                );
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            LatestVpx = "Install cancelled.";
        }
        catch (Exception e)
        {
            LatestVpx =
                "Install failed: "
                + e.Message
                + e switch
                {
                    UnauthorizedAccessException =>
                        $" Windows didn't allow writing to {Settings.VpxDir}. Pick a folder you own (Browse), or allow VR Table Manager in Windows Security → Ransomware protection → Controlled folder access.",
                    IOException
                        when e.HResult
                            is unchecked((int)0x80070020)
                                or unchecked((int)0x80070021) =>
                        " A VPX file is in use: close VPX (and the VR lobby) and try again.",
                    HttpRequestException or TimeoutException =>
                        " If it keeps failing, download the Windows BGFX zip yourself from github.com/vpinball/vpinball/releases, unzip it into a folder and choose that folder above.",
                    _ => "",
                };
        }
        finally
        {
            _vpxInstallCts = null;
            VpxProgressUnknown = false;
            IsInstallingVpx = false;
            MarkInstalledVpxBuild();
            RefreshSetupStatus();
            await CheckUpdatesAsync();
        }
    }

    [RelayCommand]
    async Task BrowseVpxDirAsync()
    {
        if (await _ui.PickFolderAsync("VPX install folder") is { } d)
        {
            VpxDir = d;
        }
    }

    [RelayCommand]
    async Task BrowseTablesDirAsync()
    {
        if (await _ui.PickFolderAsync("Tables folder") is { } d)
        {
            TablesDir = d;
        }
    }

    [ObservableProperty]
    public partial string ImportStatus { get; set; } = "";

    /// <summary>Copies the tables of another VPX install (its folder or its Tables folder) into the tables folder.</summary>
    [RelayCommand]
    async Task ImportTablesAsync()
    {
        if (await _ui.PickFolderAsync("Another VPX install, or its Tables folder") is not { } src)
        {
            return;
        }

        if (TableImport.FindTablesDir(src) == null)
        {
            ImportStatus = "No .vpx tables in that folder or its Tables folder.";
            return;
        }
        var job = new JobItem(
            "Copy tables from " + Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar))
        );
        Jobs.Insert(0, job);
        ImportStatus = "Copying tables…";
        try
        {
            var r = await Task.Run(() =>
                TableImport.Import(src, Settings, job.Report, job.Cts.Token)
            );
            var text =
                $"✓ Copied {r.Copied.Count} table{(r.Copied.Count == 1 ? "" : "s")}."
                + (r.Skipped.Count > 0 ? $" {r.Skipped.Count} already here, left as they are." : "")
                + (r.Failed.Count > 0 ? $" Couldn't copy: {string.Join("; ", r.Failed)}" : "");
            job.Finish(text, error: r.Copied.Count == 0 && r.Failed.Count > 0);
            ImportStatus = text;
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
            ImportStatus = "Cancelled.";
        }
        catch (Exception e)
        {
            job.Finish("Couldn't copy tables: " + e.Message, error: true);
            ImportStatus = "Couldn't copy tables: " + e.Message;
        }
        finally
        {
            RefreshInstalled();
        }
    }

    [RelayCommand]
    async Task BrowseFpDirAsync()
    {
        if (await _ui.PickFolderAsync("Future Pinball folder") is { } d)
        {
            FpDir = d;
        }
    }

    [RelayCommand]
    async Task BrowseUevrDirAsync()
    {
        if (await _ui.PickFolderAsync("UEVR folder (contains UEVRInjector.exe)") is { } d)
        {
            UevrDir = d;
        }
    }

    /// <param name="engineId">Which Zen game the profile is for: "PFX" (Pinball FX) or "PM" (Pinball M).</param>
    [RelayCommand]
    async Task ImportUevrProfileAsync(string? engineId)
    {
        var game = ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx;
        var zip = await _ui.PickFileAsync($"Choose a UEVR profile zip for {game.Title}", "*.zip");
        if (zip == null)
        {
            return;
        }

        try
        {
            PfxSetup.ImportProfile(zip, game);
        }
        catch (Exception e)
        {
            var msg = "Couldn't import the profile: " + e.Message;
            if (game == ZenGames.PinballM)
            {
                PmStatus = msg;
            }
            else
            {
                PfxStatus = msg;
            }

            return;
        }
        RefreshSetupStatus();
    }

    [RelayCommand]
    void OpenUevrProfileFolder(string? engineId)
    {
        var dir = PfxSetup.ProfileDirFor(
            ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx
        );
        Directory.CreateDirectory(dir);
        _ui.OpenExternal(dir);
    }

    [RelayCommand]
    void RecheckSetup()
    {
        if (string.IsNullOrWhiteSpace(FpDir) && FpSetup.AutoDetect() is { } fp)
        {
            FpDir = fp;
        }

        if (string.IsNullOrWhiteSpace(UevrDir) && PfxSetup.AutoDetectUevr() is { } uevr)
        {
            UevrDir = uevr;
        }

        RefreshInstalled();
        RefreshSetupStatus();
    }

    // ---- Updates ---------------------------------------------------------------

    [ObservableProperty]
    public partial int TableUpdateCount { get; set; }

    [ObservableProperty]
    public partial string TableUpdateText { get; set; } = "Checking after the catalog loads…";

    [ObservableProperty]
    public partial string VpxUpdateText { get; set; } = "Not checked yet.";

    [ObservableProperty]
    public partial bool HasVpxUpdate { get; set; }

    [ObservableProperty]
    public partial string UevrUpdateText { get; set; } = "Not checked yet.";

    [ObservableProperty]
    public partial bool HasUevrUpdate { get; set; }

    [ObservableProperty]
    public partial string UevrButtonText { get; set; } = "INSTALL UEVR";

    [ObservableProperty]
    public partial string BamUpdateText { get; set; } = "Not checked yet.";

    [ObservableProperty]
    public partial bool IsCheckingUpdates { get; set; }

    [ObservableProperty]
    public partial bool IsUpdating { get; set; }

    [ObservableProperty]
    public partial int UpdateCount { get; set; }

    [ObservableProperty]
    public partial string LastChecked { get; set; } = "";

    ToolRelease? _uevrLatest;

    /// <summary>Table updates come from the already-loaded catalog, so this is free to call often.</summary>
    void ApplyTableUpdates()
    {
        if (_allGames.Count == 0)
        {
            return;
        }

        var updates = TableUpdates
            .Find(_allInstalled.Select(i => i.Table), _allGames)
            .ToDictionary(u => u.Table.TablePath, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allInstalled)
        {
            item.Update = updates.GetValueOrDefault(item.Table.TablePath);
            item.RomSource = FindRomSource(item.Table);
            item.HasSternSource = IsSternTable(item.Table);
            item.AttractAudioSources = AttractAudioSourcesFor(item.Table);
            item.WheelArtSources = WheelArtSourcesFor(item.Table);
            _ = RecheckRomNeedAsync(item.Table);
        }
        TableUpdateCount = updates.Count;
        var tracked = _allInstalled.Count(i => i.Table.Manifest.TableFileId != "");
        TableUpdateText = updates.Count switch
        {
            0 when tracked == 0 => "No tables installed from the catalog yet.",
            0 => tracked == 1
                ? "Your table is up to date."
                : $"All {tracked} tables are up to date.",
            // Name them when they fit on the line; SHOW THEM lists them all either way.
            1 => $"{updates.Values.First().Table.Manifest.GameName} has a newer version.",
            <= 3 =>
                $"{string.Join(", ", updates.Values.Select(u => u.Table.Manifest.GameName).Order())} have newer versions.",
            _ => $"{updates.Count} tables have newer versions.",
        };
        if (InstalledFilter == UpdatesFilter)
        {
            FilterInstalled(); // one was updated, or a new version came out
        }
        ApplyAddons();
        ApplyRules();
        UpdateCount =
            TableUpdateCount
            + AddonUpdateCount
            + (HasVpxUpdate ? 1 : 0)
            + (HasUevrUpdate ? 1 : 0)
            + (HasAppUpdate ? 1 : 0);
    }

    // ---- Rules: The Pinball Primer's page for the machine and the catalog's instruction cards (My Tables → RULES) ----

    List<RuleSheets.PrimerEntry> _primer = [];

    async Task LoadRuleSheetsAsync()
    {
        _primer = await Task.Run(() => RuleSheets.LoadAsync());
        ApplyRules();
    }

    void ApplyRules()
    {
        // Store games (Pinball FX, Pinball M) are whole collections, not one machine.
        foreach (
            var item in _allInstalled.Where(i =>
                i.Table.Manifest.Engine is TableManifest.Vpx or TableManifest.FuturePinball
            )
        )
        {
            var m = item.Table.Manifest;
            var game = m.GameId == "" ? null : _allGames.FirstOrDefault(g => g.Id == m.GameId);
            item.Rules = RuleSheets
                .LinksFor(game?.Name ?? m.GameName, game?.Year ?? m.Year, game, _primer)
                .Select(l => new RuleChoice(item, l.Label, l.Url))
                .ToList();
        }
    }

    /// <summary>Checks GitHub (VPX, UEVR) and ravarcade.pl (BAM). Runs once at startup after the catalog loads.</summary>
    [RelayCommand]
    public async Task CheckUpdatesAsync()
    {
        IsCheckingUpdates = true;
        try
        {
            var ct = CancellationToken.None;
            var vpxTask = Try<VpxReleases>(async () =>
                await VpxSetup.FindReleasesAsync(
                    ct,
                    Settings.VpxUseDoNotUseBuilds,
                    Settings.VpxUseNightlyBuilds
                )
            );
            var uevrTask = Try(() => UevrSetup.FindLatestAsync(ct));
            var bamTask = Try(() => BamCheck.LatestAsync(ct));
            var appTask = CheckAppUpdateAsync();
            await Task.WhenAll(vpxTask, uevrTask, bamTask, appTask);

            // VPX
            var (vpx, vpxErr) = vpxTask.Result;
            var (vpxText, vpxUpdate) =
                vpx == null
                    ? ("", false)
                    : VpxSetup.DescribeUpdate(
                        vpx,
                        VpxSetup.InstalledVersion(Settings),
                        Settings.VpxKeptBuild
                    );
            HasVpxUpdate = vpxUpdate;
            VpxUpdateText = vpxErr != null ? "Couldn't check GitHub: " + vpxErr : vpxText;

            // UEVR
            var (uevr, uevrErr) = uevrTask.Result;
            _uevrLatest = uevr;
            var uevrHave = UevrSetup.InstalledVersion(Settings);
            HasUevrUpdate = uevr != null && uevrHave != null && uevrHave != uevr.Tag;
            UevrButtonText = uevrHave == null ? "INSTALL UEVR" : "UPDATE UEVR";
            UevrUpdateText =
                uevrErr != null ? "Couldn't check GitHub: " + uevrErr
                : uevr == null ? "No UEVR release found."
                : uevrHave == null
                    ? $"Not installed. Latest is {uevr.Tag} (only needed for Pinball FX)."
                : uevrHave == "unknown"
                    ? $"Installed (version unknown). Latest is {uevr.Tag}; updating makes it trackable."
                : HasUevrUpdate ? $"{uevrHave} → {uevr.Tag}"
                : $"Up to date ({uevrHave}).";

            // BAM: report only.
            var (bam, bamErr) = bamTask.Result;
            var bamHave = BamCheck.InstalledVersion(Settings);
            BamUpdateText =
                bamErr != null ? "Couldn't reach ravarcade.pl: " + bamErr
                : bam == null ? "Couldn't read the latest version from ravarcade.pl."
                : bamHave == null
                    ? $"Latest is v{bam}. BAM isn't installed (only needed for Future Pinball)."
                : $"Latest is v{bam}; yours: {bamHave}. Install from the BAM page if yours is older.";

            ApplyTableUpdates();
            LastChecked = $"Checked {DateTime.Now:t}";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    static async Task<(T? value, string? error)> Try<T>(Func<Task<T?>> f)
        where T : class
    {
        try
        {
            return (await f(), null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task UpdateTableAsync(InstalledItem item)
    {
        if (item.Update is not { } u)
        {
            return;
        }

        await InstallAsync(u.Game, u.File, u.Table);
    }

    [RelayCommand]
    async Task UpdateAllTablesAsync()
    {
        // One at a time: the browser downloader handles one site page at a time anyway.
        foreach (var u in _allInstalled.Select(i => i.Update).OfType<TableUpdate>().ToList())
        {
            await InstallAsync(u.Game, u.File, u.Table);
        }
    }

    bool _installingUevr;

    [RelayCommand]
    async Task InstallUevrAsync()
    {
        if (_installingUevr)
        {
            return; // UPDATE ALL while UPDATE UEVR runs
        }

        _installingUevr = true;
        var job = new JobItem("UEVR");
        Jobs.Insert(0, job);
        try
        {
            var rel =
                _uevrLatest
                ?? await UevrSetup.FindLatestAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("No UEVR release found.");
            job.Report($"Downloading UEVR {rel.Tag}…");
            await UevrSetup.InstallAsync(Settings, rel, job.ProgressReporter, job.Cts.Token);
            UevrDir = Settings.UevrDir;
            job.Finish(
                $"✓ UEVR {rel.Tag} installed. Your Pinball FX / Pinball M profiles are untouched."
            );
        }
        catch (Exception e)
        {
            job.Finish("Failed: " + e.Message, error: true);
        }
        finally
        {
            _installingUevr = false;
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
            if (HasVpxUpdate)
            {
                await InstallVpxAsync();
            }

            if (HasUevrUpdate)
            {
                await InstallUevrAsync();
            }

            await UpdateAllTablesAsync();
            await UpdateAllAddonsAsync();
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
    /// <summary>One dialog at a time: a new app version first (updating closes the app), then unfinished downloads.</summary>
    async Task StartupPromptsAsync()
    {
        try
        {
            await StartupAppUpdateAsync();
        }
        catch
        { /* never block the resume offer */
        }
        if (!IsUpdatingApp)
        {
            await OfferResumeAsync();
        }
    }

    async Task OfferResumeAsync()
    {
        var items = _leftFromLastTime;
        if (items.Count == 0)
        {
            return;
        }

        var list =
            string.Join("\n", items.Take(8).Select(i => "• " + i.Title))
            + (items.Count > 8 ? $"\n• …and {items.Count - 8} more" : "");
        var pick = await _ui.ChoiceAsync(
            items.Count == 1 ? "Resume 1 download?" : $"Resume {items.Count} downloads?",
            $"These hadn't finished when VR Table Manager last closed:\n\n{list}\n\nThey'll start again from the beginning.",
            ["RESUME", "DISCARD"]
        );
        // Resumed jobs add themselves back. Only last time's entries go: ones for downloads started since stay.
        foreach (var i in items)
        {
            _pending.Remove(i.Id);
        }
        if (pick != 0)
        {
            return;
        }

        var installed = TableInstaller.Scan(Settings);
        InstalledTable? Find(string? path) =>
            path == null
                ? null
                : installed.FirstOrDefault(t =>
                    string.Equals(
                        Path.GetFullPath(t.TablePath),
                        Path.GetFullPath(path),
                        StringComparison.OrdinalIgnoreCase
                    )
                );
        var skipped = new List<string>();
        foreach (var item in items)
        {
            if (item.Kind == PendingDownload.RomKind)
            {
                if (Find(item.TablePath) is { } t)
                {
                    _ = GetRomAsync(t);
                }
                else
                {
                    skipped.Add(item.Title);
                }

                continue;
            }
            if (PendingDownload.AddonKindOf(item.Kind) != null)
            {
                ResumeAddon(item, Find, skipped);
                continue;
            }
            var game = _allGames.FirstOrDefault(g => g.Id == item.GameId);
            var tf = game?.TableFiles?.FirstOrDefault(f => f.Id == item.TableFileId);
            if (game == null || tf == null)
            {
                skipped.Add(item.Title);
                continue;
            }
            var replacing = Find(item.TablePath);
            _ = InstallAsync(game, tf, replacing is { IsIncomplete: false } ? replacing : null);
        }
        if (skipped.Count > 0)
        {
            CatalogStatus =
                $"Couldn't resume {string.Join(", ", skipped)}: no longer in the catalog or installed.";
        }
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
            PlayStatus =
                $"Couldn't find {item.Title} in the catalog to reinstall it. Remove it and install it again from Get Tables.";
            return;
        }
        // An interrupted update still has the old table file; replace it like a normal update.
        var replacing = File.Exists(item.Table.TablePath) ? item.Table : null;
        if (replacing != null)
        {
            await InstallAsync(game, tf, replacing);
            return;
        }
        // Cut off before the table file arrived: nothing to replace, but still mark this card busy.
        if (!TryBeginBusy(item.Table.TablePath))
        {
            return;
        }

        try
        {
            await InstallAsync(game, tf);
        }
        finally
        {
            EndBusy(item.Table.TablePath);
        }
    }

    /// <summary>
    /// Called by the window when it's really closing. Downloads that end because of the shutdown
    /// keep their saved-queue entries, so they're offered again at the next launch.
    /// </summary>
    public void PrepareToClose() => _closing = true;

    public ValueTask DisposeAsync() => _browser.DisposeAsync();
}
