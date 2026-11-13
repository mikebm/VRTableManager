using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// My Tables: the installed tables, their cards and what the cards do that is not playing, ROMs, add-ons or lobby media.
public sealed partial class LibraryViewModel(MainViewModel app) : ChildViewModel(app)
{
    bool _refreshQueued;

    /// <summary>
    /// <see cref="RefreshInstalled"/> once the current work is done, however many tables ask for it. The ROM recheck
    /// calls this: <see cref="RefreshInstalled"/> starts that recheck itself, so a direct call would nest one rescan in another.
    /// </summary>
    internal void RefreshInstalledSoon()
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

    /// <summary>Tables with a ROM fetch, update or reinstall running (by table path).</summary>
    readonly HashSet<string> _busyTables = new(StringComparer.OrdinalIgnoreCase);

    internal bool TryBeginBusy(string tablePath)
    {
        if (!_busyTables.Add(tablePath))
        {
            return false;
        }

        SetBusy(tablePath, true);
        return true;
    }

    internal void EndBusy(string tablePath)
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

    internal List<InstalledItem> _allInstalled = [];
    internal PlayStats _stats = new();

    [RelayCommand]
    internal void RefreshInstalled()
    {
        // Re-read stats every time: the VR lobby and its launcher write them too.
        _stats = PlayStats.Load();
        // Tables found again keep their item (and so their card): a rescan runs after every table you play, install
        // or fix, and building 300 cards afresh each time is what made a big library slow.
        var before = new Dictionary<string, InstalledItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var old in _allInstalled)
        {
            before.TryAdd(old.Table.TablePath, old);
        }
        _allInstalled = TableInstaller
            .Scan(Settings)
            .Select(t =>
            {
                if (before.Remove(t.TablePath, out var kept))
                {
                    kept.Rescanned(t);
                    return kept;
                }
                return new InstalledItem(t, App);
            })
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
                App.Addons.RefreshAddonState(item);
                item.AttractAudioSources = App.Media.AttractAudioSourcesFor(item.Table);
                item.WheelArtSources = App.Media.WheelArtSourcesFor(item.Table);
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
    internal async Task LinkImportedTablesAsync()
    {
        if (App.Catalog._allGames.Count == 0)
        {
            return;
        }

        var games = App.Catalog._allGames;
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
    internal void ShowInstalled()
    {
        FilterInstalled();
        ApplyTableUpdates();
    }

    /// <summary>
    /// Makes the grid's list match <paramref name="shown"/> with as few changes as it takes: a card leaves only when its
    /// table does, so narrowing a search or a rescan doesn't rebuild the cards that stay.
    /// </summary>
    void SyncInstalled(List<InstalledItem> shown)
    {
        if (Installed.SequenceEqual(shown))
        {
            return;
        }

        var keep = shown.ToHashSet();
        for (var i = Installed.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Installed[i]))
            {
                Installed.RemoveAt(i);
            }
        }
        for (var i = 0; i < shown.Count; i++)
        {
            if (i < Installed.Count && Installed[i] == shown[i])
            {
                continue;
            }

            var at = Installed.IndexOf(shown[i]);
            if (at > i)
            {
                Installed.Move(at, i);
            }
            else
            {
                Installed.Insert(i, shown[i]);
            }
        }
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
                .OrderBy(i => LetterRails.SortKey(i.Title), StringComparer.OrdinalIgnoreCase),
            UpdatesFilter => pool.Where(i => i.HasUpdate)
                .OrderBy(i => LetterRails.SortKey(i.Title), StringComparer.OrdinalIgnoreCase),
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
                    .ThenBy(i => LetterRails.SortKey(i.Title), StringComparer.OrdinalIgnoreCase),
            },
        };
        var shown = q.ToList();
        SyncInstalled(shown);
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
                UpdatesFilter when App.Catalog._allGames.Count == 0 =>
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
    void OpenTableFolder(InstalledItem item) => Ui.OpenExternal(item.Table.Folder);

    /// <summary>
    /// ⋯ → Rename…: the name shown in My Tables and the VR lobby. It's kept in the table's vrtm.json; the catalog
    /// link (GameId), collections, play history and pictures don't go by the name, so they all stay.
    /// </summary>
    [RelayCommand]
    async Task RenameTable(InstalledItem item)
    {
        if (!item.CanRename)
        {
            return;
        }

        var shown = item.Table.Manifest;
        var message =
            shown.OriginalName != null && shown.OriginalName != shown.GameName
                ? $"A new name for “{shown.GameName}”. Leave it blank to go back to “{shown.OriginalName}”."
                : $"A new name for “{shown.GameName}”.";
        if (await Ui.AskTextAsync("Rename", message, shown.GameName, "RENAME") is not { } typed)
        {
            return;
        }

        // An update, add-on or ROM job on this table rewrites its vrtm.json from what it read at the start, which
        // would undo the new name: wait for it. A rescan may also have replaced the table while the box was open,
        // so everything below reads the current one.
        var table = item.Table;
        if (!TryBeginBusy(table.TablePath))
        {
            PlayStatus = $"“{table.Manifest.GameName}” is busy. Rename it once that's done.";
            return;
        }

        var old = table.Manifest.GameName;
        try
        {
            if (!item.CanRename || !TableInstaller.Rename(table, typed))
            {
                return;
            }
        }
        catch (Exception e)
        {
            PlayStatus = $"Couldn't rename “{old}”: {e.Message}";
            return;
        }
        finally
        {
            EndBusy(table.TablePath);
        }

        item.NameChanged();
        FilterInstalled(); // A–Z order and search go by the name
        PlayStatus =
            table.Manifest.OriginalName == null
                ? $"“{old}” is back to “{table.Manifest.GameName}”."
                : $"Renamed “{old}” to “{table.Manifest.GameName}”.";
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
        if (!await Ui.ConfirmAsync("Remove table", $"Delete {item.Title}?\n\nThis removes {what}"))
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
            App.Catalog.ApplyFilter();
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't remove: " + e.Message;
        }
    }

    /// <summary>Reinstalls a table whose install was cut off (INCOMPLETE on My Tables).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task ReinstallAsync(InstalledItem item)
    {
        var marker = item.Table.IncompleteInstall;
        var gameId = marker?.GameId ?? item.Table.Manifest.GameId;
        var fileId = marker?.TableFileId ?? item.Table.Manifest.TableFileId;
        var game = App.Catalog._allGames.FirstOrDefault(g => g.Id == gameId);
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
            await App.Catalog.InstallAsync(game, tf, replacing);
            return;
        }
        // Cut off before the table file arrived: nothing to replace, but still mark this card busy.
        if (!TryBeginBusy(item.Table.TablePath))
        {
            return;
        }

        try
        {
            await App.Catalog.InstallAsync(game, tf);
        }
        finally
        {
            EndBusy(item.Table.TablePath);
        }
    }

    [RelayCommand]
    void OpenRomFolder(InstalledItem item) =>
        App.OpenFolder(Path.Combine(item.Table.Folder, "pinmame", "roms"));

    [RelayCommand]
    void OpenNvramFolder(InstalledItem item) =>
        App.OpenFolder(Path.Combine(item.Table.Folder, "pinmame", "nvram"));

    [RelayCommand]
    void OpenTableIni(InstalledItem item)
    {
        var ini = VpxGraphics.TableIniPath(item.Table.TablePath);
        if (!File.Exists(ini))
        {
            try
            {
                File.WriteAllText(
                    ini,
                    "; Settings for this table only. They override VPX's own settings.\r\n"
                );
            }
            catch (Exception e)
            {
                PlayStatus = $"Couldn't make {item.Title}'s settings file: " + e.Message;
                return;
            }
        }

        Ui.OpenExternal(ini);
    }

    [RelayCommand]
    void ClearTableCache(InstalledItem item)
    {
        var dir = Path.Combine(item.Table.Folder, "cache");
        var freed = Directory.Exists(dir) ? DiskUsage.SizeOf(dir) : 0;
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't clear the cache (is the table running?): " + e.Message;
            return;
        }
        PlayStatus =
            $"Cleared {item.Title}'s texture cache ({DiskUsage.Format(freed)}). Its next start rebuilds it.";
        _ = RefreshSizesAsync();
    }

    // ---- Disk use on the cards ---------------------------------------------------------------

    [ObservableProperty]
    public partial string InstalledSizeText { get; set; } = "";
    int _sizeGeneration;

    /// <summary>Works out each table's size and high scores in the background (big folders take a moment).</summary>
    async Task RefreshSizesAsync()
    {
        var gen = ++_sizeGeneration;
        var items = _allInstalled.ToList();
        var sizes = await Task.Run(() =>
            items
                .Select(i =>
                    i.Table.Manifest.Engine
                        is Models.TableManifest.Vpx
                            or Models.TableManifest.FuturePinball
                        ? DiskUsage.SizeOf(i.Table, Settings)
                        : 0
                )
                .ToList()
        );
        var scores = await Task.Run(() =>
            items
                .Select(i =>
                    i.Table.IsVpx ? Lobby.LobbyText.HighScoreLines(HighScores.Read(i.Table)) : []
                )
                .ToList()
        );
        if (gen != _sizeGeneration)
        {
            return; // a newer scan replaced these items
        }

        for (var i = 0; i < items.Count; i++)
        {
            items[i].SizeBytes = sizes[i];
            items[i].TopScore = scores[i].FirstOrDefault() ?? "";
            items[i].MoreScores = scores[i].Count > 1 ? scores[i][1].Replace("  ·  ", "\n") : "";
        }
        InstalledSizeText = DiskUsage.Format(sizes.Sum());
        if (InstalledSort == "Largest first")
        {
            FilterInstalled();
        }
    }

    /// <summary>Downloads the NVRAM maps the first time (and monthly), then shows the scores they unlock.</summary>
    internal async Task RefreshHighScoreMapsAsync()
    {
        var hadMaps = Directory.Exists(HighScores.MapsDir);
        if (await HighScores.EnsureMapsAsync(CancellationToken.None) && !hadMaps)
        {
            await RefreshSizesAsync();
        }
    }

}
