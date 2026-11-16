using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// My Tables: newer catalog versions of installed tables, and their rule sheets.
public sealed partial class LibraryViewModel
{
    [ObservableProperty]
    public partial int TableUpdateCount { get; set; }

    [ObservableProperty]
    public partial string TableUpdateText { get; set; } = "Checking after the catalog loads…";

    /// <summary>Table updates come from the already-loaded catalog, so this is free to call often.</summary>
    internal void ApplyTableUpdates()
    {
        if (App.Catalog._allGames.Count == 0)
        {
            return;
        }

        var updates = TableUpdates
            .Find(_allInstalled.Select(i => i.Table), App.Catalog._allGames)
            .ToDictionary(u => u.Table.TablePath, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allInstalled)
        {
            item.Update = updates.GetValueOrDefault(item.Table.TablePath);
            item.RomSource = App.Roms.FindRomSource(item.Table);
            item.HasSternSource = App.Roms.IsSternTable(item.Table);
            item.AttractAudioSources = App.Media.AttractAudioSourcesFor(item.Table);
            item.WheelArtSources = App.Media.WheelArtSourcesFor(item.Table);
            item.RemixChoices = App.Catalog.RemixChoicesFor(item);
            _ = App.Roms.RecheckRomNeedAsync(item.Table);
        }
        TableUpdateCount = updates.Count;
        // Only tables linked to a catalog version (installed from the catalog, AUTO-LINK, Fix table match) can be
        // checked. Say how many others there are, or 18 of 381 reads as "all 18".
        var tracked = _allInstalled.Count(i => i.Table.Manifest.TableFileId != "");
        var untracked = UnlinkedCount = _allInstalled.Count(IsUnlinked);
        AutoLinkableCount = _allInstalled.Count(i => IsUnlinked(i) && i.Table.IsVpx && i.CanRename);
        var others =
            untracked == 0 ? ""
            : untracked == 1
                ? " 1 other table isn't linked to a catalog version, so it can't be checked."
            : $" {untracked} other tables aren't linked to a catalog version, so they can't be checked.";
        TableUpdateText = updates.Count switch
        {
            0 when tracked == 0 => untracked == 0 ? "No tables installed from the catalog yet."
            : untracked == 1
                ? "Your table isn't linked to a catalog version, so it can't be checked."
            : $"None of your {untracked} tables are linked to a catalog version, so they can't be checked.",
            0 => (
                tracked == 1
                    ? "Your 1 linked table is up to date."
                    : $"All {tracked} linked tables are up to date."
            ) + others,
            // Name them when they fit on the line; SHOW THEM lists them all either way.
            1 => $"{updates.Values.First().Table.Manifest.GameName} has a newer version.",
            <= 3 =>
                $"{string.Join(", ", updates.Values.Select(u => u.Table.Manifest.GameName).Order())} have newer versions.",
            _ => $"{updates.Count} tables have newer versions.",
        };
        if (InstalledFilter is UpdatesFilter or UnlinkedFilter)
        {
            FilterInstalled(); // one was updated or linked, or a new version came out
        }
        App.Addons.ApplyAddons();
        ApplyRules();
        App.Updates.UpdateCount =
            TableUpdateCount
            + App.Addons.AddonUpdateCount
            + (App.Updates.HasVpxUpdate ? 1 : 0)
            + (App.Updates.HasUevrUpdate ? 1 : 0)
            + (App.Updates.HasAppUpdate ? 1 : 0);
    }

    // ---- AUTO-LINK: tables that didn't come from the catalog (Import tables, copied in by hand) ----

    /// <summary>VPX and Future Pinball tables with no catalog version, so update checks skip them.</summary>
    [ObservableProperty]
    public partial int UnlinkedCount { get; set; }

    /// <summary>The unlinked tables AUTO-LINK can try (VPX with a vrtm.json of their own): shows its button.</summary>
    [ObservableProperty]
    public partial int AutoLinkableCount { get; set; }

    [ObservableProperty]
    public partial bool IsAutoLinking { get; set; }

    /// <summary>What AUTO-LINK is doing ("Linking 120 of 450…"), shown beside its button while it runs.</summary>
    [ObservableProperty]
    public partial string AutoLinkProgress { get; set; } = "";

    static bool IsUnlinked(InstalledItem i) =>
        i.Table.Manifest.TableFileId == ""
        && i.Table.Manifest.Engine is TableManifest.Vpx or TableManifest.FuturePinball;

    /// <summary>
    /// Links every table it can to its catalog version (<see cref="CatalogLink.AutoLink"/>), then says how many
    /// are left and offers to list them, to link by hand with ⋯ → Fix table match….
    /// </summary>
    [RelayCommand]
    internal async Task AutoLinkTablesAsync()
    {
        var games = App.Catalog._allGames;
        if (games.Count == 0)
        {
            await Ui.ChoiceAsync(
                "The catalog hasn't loaded",
                "AUTO-LINK needs the catalog. Try again once Get Tables has loaded it.",
                ["OK"]
            );
            return;
        }

        if (IsAutoLinking)
        {
            return;
        }

        IsAutoLinking = true;
        AutoLinkProgress = "Getting ready…";
        List<InstalledItem> toFix = [];
        try
        {
            await _gameLinking;
            // Tables with a job running (update, add-on, ROM, Fix table match) are left alone: the job saves
            // vrtm.json from what it read at its start, and would undo the link, or this would undo its work.
            var tables = _allInstalled
                .Select(i => i.Table)
                .Where(t => !_busyTables.Contains(t.TablePath))
                .ToList();
            // Progress<T> posts back to the UI thread; shown as a count, as a big library takes a while.
            var linking = true; // a report still queued when the run ends mustn't overwrite what comes next
            var progress = new Progress<int>(done =>
            {
                if (linking)
                {
                    AutoLinkProgress = $"Linking {done} of {tables.Count}…";
                }
            });
            IProgress<int> report = progress;
            AutoLinkProgress = $"Linking 0 of {tables.Count}…";
            var (linked, changed) = await Task.Run(() =>
            {
                var n = 0;
                var done = 0;
                var lastReport = Environment.TickCount64;
                var touched = new List<InstalledTable>();
                foreach (var scanned in tables)
                {
                    if (Environment.TickCount64 - lastReport >= 200)
                    {
                        lastReport = Environment.TickCount64;
                        report.Report(done);
                    }
                    done++;
                    try
                    {
                        // As saved now, not as scanned: a rename meanwhile mustn't be written back over.
                        var t = TableInstaller.Fresh(scanned);
                        var r = CatalogLink.AutoLink(t, games);
                        if (
                            r
                            is CatalogLink.AutoLinkResult.Skipped
                                or CatalogLink.AutoLinkResult.NoGame
                        )
                        {
                            continue;
                        }
                        TableInstaller.SaveManifest(t);
                        touched.Add(t);
                        if (r == CatalogLink.AutoLinkResult.Linked)
                        {
                            n++;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Read-only folder or the file's in use: left for next time.
                    }
                }
                return (n, touched);
            });

            linking = false;
            AutoLinkProgress = "Reading the tables back…";
            // Read them back from disk: a rescan while this ran (a download finishing, the lobby closing) may have
            // replaced the tables with copies read before they were saved, which would still show as unlinked.
            if (changed.Count > 0)
            {
                RefreshInstalled(); // re-reads vrtm.json and pictures
            }
            ApplyTableUpdates();
            AutoLinkProgress = "";

            var left = _allInstalled.Where(IsUnlinked).ToList();
            var fixable = left.Count(i => i.CanRename);
            var message =
                (
                    linked == 0 ? "No more tables could be linked surely."
                    : linked == 1
                        ? "Linked 1 table to its catalog version: it's checked for updates now."
                    : $"Linked {linked} tables to their catalog versions: they're checked for updates now."
                )
                + (
                    left.Count == 0
                        ? " Every table is linked."
                        : $"\n\n{left.Count} {(left.Count == 1 ? "table still isn't" : "tables still aren't")} linked. "
                            + "AUTO-LINK only links VPX tables, and only when the author and version in the file point "
                            + "to one catalog table clearly."
                            + (
                                fixable > 0
                                    ? "\n\nFIX THEM NOW goes through them one at a time, the likeliest game first, "
                                        + "and you can stop whenever you like. LATER leaves them under My Tables → "
                                        + "Not linked, to fix with ⋯ → Fix table match…, or come back here."
                                    : ""
                            )
                );
            var choice = await Ui.ChoiceAsync(
                "Auto-link tables",
                message,
                fixable > 0 ? ["FIX THEM NOW", "SHOW THEM", "LATER"] : ["OK"]
            );
            if (fixable > 0 && choice == 0)
            {
                toFix =
                [
                    .. left.Where(i => i.CanRename)
                        .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
                ];
            }
            else if (fixable > 0 && choice == 1)
            {
                ShowUnlinkedTables();
            }
        }
        finally
        {
            IsAutoLinking = false;
            AutoLinkProgress = "";
        }

        if (toFix.Count > 0)
        {
            await FixUnlinkedNowAsync(toFix);
        }
    }

    /// <summary>My Tables, filtered to the tables with no catalog version (AUTO-LINK's SHOW THEM).</summary>
    [RelayCommand]
    void ShowUnlinkedTables()
    {
        _refillingChoices = true;
        InstalledSearch = "";
        InstalledEngine = InstalledEngines[0];
        _refillingChoices = false;
        ClearInstalledFilters();
        InstalledFilter = UnlinkedFilter;
        App.MainTab = 0;
    }

    // ---- Rules: The Pinball Primer's page for the machine and the catalog's instruction cards (My Tables → RULES) ----

    List<RuleSheets.PrimerEntry> _primer = [];

    internal async Task LoadRuleSheetsAsync()
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
            var game = App.Catalog.CatalogGame(m.GameId);
            item.Rules = RuleSheets
                .LinksFor(game?.Name ?? m.GameName, game?.Year ?? m.Year, game, _primer)
                .Select(l => new RuleChoice(item, l.Label, l.Url))
                .ToList();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task UpdateTableAsync(InstalledItem item)
    {
        if (item.Update is not { } u)
        {
            return;
        }

        await App.Catalog.InstallAsync(u.Game, u.File, u.Table);
    }

    [RelayCommand]
    internal async Task UpdateAllTablesAsync()
    {
        // One at a time: the browser downloader handles one site page at a time anyway.
        foreach (var u in _allInstalled.Select(i => i.Update).OfType<TableUpdate>().ToList())
        {
            await App.Catalog.InstallAsync(u.Game, u.File, u.Table);
        }
    }
}
