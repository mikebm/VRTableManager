using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Get Tables: the Virtual Pinball Spreadsheet catalog, its filters and pages, and installing a table from it.
public sealed partial class CatalogViewModel(MainViewModel app) : ChildViewModel(app)
{
    // ---- Catalog -------------------------------------------------------------

    /// <summary>Every machine in the catalog (empty until it has loaded).</summary>
    public IReadOnlyList<VpsGame> AllGames => _allGames;

    internal List<VpsGame> _allGames = [];

    Dictionary<string, VpsGame> _gamesById = [];
    List<VpsGame>? _gamesByIdFor;

    /// <summary>
    /// The catalog game with this id (the first, if listed twice), or null. A dictionary, rebuilt when the catalog
    /// is: a rescan asks this several times per table, and walking a catalog of thousands each time adds up.
    /// </summary>
    internal VpsGame? CatalogGame(string id)
    {
        if (!ReferenceEquals(_gamesByIdFor, _allGames))
        {
            _gamesById = _allGames.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
            _gamesByIdFor = _allGames;
        }
        return id != "" && _gamesById.TryGetValue(id, out var g) ? g : null;
    }

    /// <summary>Setup → Downloads: tables VPS marks as adult stay out of Get Tables.</summary>
    [ObservableProperty]
    public partial bool HideAdult { get; set; }

    partial void OnHideAdultChanged(bool value)
    {
        Settings.HideAdult = value;
        Settings.Save();
        ApplyFilter();
    }

    internal override void LoadSettings(AppSettings s) => HideAdult = s.HideAdult;

    // ---- A–Z rail ----

    public ObservableCollection<LetterChoice> CatalogLetters { get; } = [];

    [ObservableProperty]
    public partial bool ShowCatalogLetters { get; set; }

    void UpdateCatalogLetters(bool alphabetical)
    {
        ShowCatalogLetters = alphabetical && _filtered.Count >= LetterRails.From;
        LetterRails.Fill(CatalogLetters, _filtered.Select(g => g.Name));
    }

    /// <summary>Lights the rail's letters for the cards on screen.</summary>
    public void SetActiveLetters(IReadOnlySet<string> letters) =>
        LetterRails.SetActive(CatalogLetters, letters);

    /// <summary>
    /// The card a letter jumps to. A page or two past what's shown, those pages are added; further (or before the
    /// first card shown), the cards start at the letter instead, since laying out every card up to "W" of a
    /// 2,000-table catalog freezes the window. SHOW EARLIER TABLES brings back the ones before.
    /// </summary>
    public GameItem? FirstCatalogAt(string letter)
    {
        var index = _filtered.FindIndex(g => Lobby.LobbyNav.LetterOf(g.Name) == letter);
        if (index < 0)
        {
            return null;
        }

        if (index < _pageStart || index >= _pageStart + Games.Count + 2 * PageSize)
        {
            _pageStart = index;
            Games.Clear();
            AddPage();
        }
        else
        {
            while (_pageStart + Games.Count <= index && HasMore)
            {
                AddPage();
            }
        }

        var shown = index - _pageStart;
        return shown >= 0 && shown < Games.Count ? Games[shown] : null;
    }

    // ---- Machines and their table files ----

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

    [RelayCommand]
    public async Task LoadCatalogAsync(bool force = false)
    {
        IsLoading = true;
        CatalogStatus = "Loading the Virtual Pinball Spreadsheet…";
        try
        {
            _allGames = await Task.Run(() => VpsCatalog.LoadAsync(force));
            App.Downloads.KnownGames = _allGames.Select(g => g.Name);
            ApplyFilter();
            App.CatalogLoaded();
            if (VpsCatalog.UsingSavedCopy)
            {
                _ = RetryCatalogAsync();
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
                    App.Downloads.Assisted.KnownGames = _allGames.Select(g => g.Name);
                    ApplyFilter();
                    App.Library.ShowInstalled(); // My Tables' type and theme filters come from the catalog; also runs ApplyTableUpdates
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

    internal void ApplyFilter()
    {
        _installedIds = App.Library._allInstalled.Select(i => i.Table.Manifest.GameId).ToHashSet();
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
                _ => matches.OrderBy(g => LetterRails.SortKey(g.Name), StringComparer.OrdinalIgnoreCase),
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
        App.Downloads.Jobs.Insert(0, job);
        App.Downloads.Pending.Add(
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
            file = await App.Downloads.FetchAsync(url, job, hint);

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
            var getRom = installed.MissingRoms.Count > 0 && App.Roms.FindRomSource(installed) != null;
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
            App.Library.RefreshInstalled();
            ApplyFilter();
            if (getRom)
            {
                // After this returns: an update holds the table busy until then, and GetRomAsync needs it free.
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _ = App.Roms.GetRomAsync(installed),
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
            DownloadsViewModel.CleanJobDownloads(job);
            // Finished, failed or cancelled: nothing to resume. (Not when it was cut off by the app
            // closing: that entry stays so the download is offered again next time.)
            App.Downloads.FinishPending(job);
        }
    }

    /// <param name="replacing">The installed table this replaces (updates), so its old file is removed.</param>
    internal async Task<bool> InstallAsync(VpsGame game, VpsTableFile tf, InstalledTable? replacing = null)
    {
        if (replacing == null)
        {
            // Nothing installed to mark busy yet: the catalog file itself, so a second INSTALL (after picking the game
            // again, or a resumed download) doesn't install it into the same folder at the same time.
            var key = $"install:{game.Id}|{tf.Id}";
            if (!App.Library.TryBeginBusy(key))
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
                App.Library.EndBusy(key);
            }
        }

        if (!App.Library.TryBeginBusy(replacing.TablePath))
        {
            return false; // already updating this table
        }

        try
        {
            return await InstallCoreAsync(game, tf, replacing);
        }
        finally
        {
            App.Library.EndBusy(replacing.TablePath);
        }
    }
}
