using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// My Tables → ADD A TABLE FILE…: install a table zip (or .vpx / .fpt) you already downloaded yourself.
// Which table it is comes from the catalog (guessed from the file name, you confirm), so it gets its ROM,
// add-ons, art and updates like a table installed from Get Tables.
public sealed partial class LibraryViewModel
{
    [RelayCommand]
    async Task AddTableFileAsync()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        if (
            await Ui.PickFileAsync(
                "Choose the table you downloaded",
                string.Join(";", TableFileMatch.Extensions.Select(e => "*" + e)),
                startFolder: downloads
            )
            is not { } file
        )
        {
            return;
        }

        var fp = TableFileMatch.IsFuturePinball(file);
        if (fp && string.IsNullOrWhiteSpace(Settings.FpDir))
        {
            await Ui.ChoiceAsync(
                "Future Pinball isn't set up",
                "Set the Future Pinball folder on the Setup tab first.",
                ["OK"]
            );
            return;
        }

        // Which table is it?
        var name = Path.GetFileName(file);
        if (await AskWhichTableAsync(file) is not { } which)
        {
            return;
        }

        var (game, tf) = which;

        // A VPU Remix (.dif) is made by patching a table you have: it may replace that table or go in beside it.
        // A remix already made into a .vpx (with the VPURemix tool) installs like any table.
        var remix =
            !fp && (await Task.Run(() => VpuRemix.IsPatchOnly(file)) ?? VpuRemix.IsRemix(tf));
        var bases = fp
            ? []
            // The entry the file was matched to comes first: for a .dif named after its original, that's the base.
            : App
                .Catalog.RemixBases(game, tf)
                .OrderByDescending(b => b.Exact)
                .ThenByDescending(b => b.Table.Manifest.TableFileId == tf.Id)
                .ToList();
        if (
            remix
            && await App.Catalog.PickRemixBaseAsync($"{game.Name}: VPU Remix", bases) is { } ordered
        )
        {
            bases = ordered;
        }
        else if (remix && bases.Count > 0)
        {
            return; // cancelled
        }
        InstalledTable? replacing;
        if (remix)
        {
            if (bases.Count == 0)
            {
                await Ui.ChoiceAsync(
                    $"{game.Name}: VPU Remix",
                    "This remix is a patch for another table, not a table. Install the table it patches first "
                        + "(the exact version it was made from; the remix's ReadMe links it), then add this file again.",
                    ["OK"]
                );
                return;
            }

            var pick = await App.Catalog.AskNewOrReplaceAsync(
                $"{game.Name}: VPU Remix",
                $"The remix is made by patching your {bases[0].Table.Manifest.GameName} "
                    + $"({Path.GetFileName(bases[0].Table.TablePath)}).",
                bases[0].Table,
                "\n\nIf the patch doesn't fit your copy, nothing is installed."
            );
            if (pick < 0)
            {
                return;
            }

            replacing = pick == 1 ? bases[0].Table : null;
            // Replacing a table puts the remix made from that table in its place, never one made from another.
            if (replacing != null)
            {
                bases = [bases[0]];
            }

            // A .dif matched to a catalog entry that isn't a remix (likely the base: patches are named after it)
            // mustn't take that entry's id: it would install into the base's folder and be "updated" back to it.
            if (!VpuRemix.IsRemix(tf))
            {
                tf = new VpsTableFile
                {
                    Id = "",
                    TableFormat = "VPX",
                    Features = [VpuRemix.Feature],
                    ImgUrl = tf.ImgUrl,
                };
            }
        }
        else
        {
            // Already installed: this replaces it (ROMs, high scores and settings in its folder stay).
            replacing = string.IsNullOrEmpty(game.Id)
                ? null
                : _allInstalled
                    .FirstOrDefault(i =>
                        i.Table.Manifest.GameId == game.Id
                        && i.Table.Manifest.Engine
                            == (fp ? TableManifest.FuturePinball : TableManifest.Vpx)
                    )
                    ?.Table;
            if (
                replacing != null
                && await Ui.ChoiceAsync(
                    $"{game.Name} is already installed",
                    "Replace it with this file? Its ROMs, high scores and settings stay.",
                    ["REPLACE", "CANCEL"]
                ) != 0
            )
            {
                return;
            }
        }

        await InstallFileAsync(
            file,
            game,
            tf,
            replacing,
            bases.Select(b => b.SourcePath).ToList(),
            remix
        );
    }

    /// <summary>
    /// Installs a table file you picked as a Downloads job: a new table, or in place of <paramref name="replacing"/>
    /// (an update, or a remix put in its place). <paramref name="bases"/>: the files a remix's patch is applied to.
    /// </summary>
    async Task InstallFileAsync(
        string file,
        VpsGame game,
        VpsTableFile tf,
        InstalledTable? replacing,
        IReadOnlyList<string> bases,
        bool remix
    )
    {
        var name = Path.GetFileName(file);
        // Replacing: an update, ROM fetch or add-on running for the table would be writing into the same folder.
        if (replacing != null && !TryBeginBusy(replacing.TablePath))
        {
            PlayStatus =
                $"{game.Name} has a job running (see Downloads): wait for it, then add the file again.";
            return;
        }

        var verb =
            replacing == null ? ""
            : remix ? "Replace with remix: "
            : "Update: ";
        var job = new JobItem($"{verb}{game.Name} — from {name}") { FileNames = [game.Name] };
        App.Downloads.Jobs.Insert(0, job);
        try
        {
            job.Report("Installing files…");
            var installed = await TableInstaller.InstallAsync(
                file,
                game,
                tf,
                Settings,
                job.Report,
                job.Cts.Token,
                replacing,
                bases
            );
            var getRom =
                installed.MissingRoms.Count > 0 && App.Roms.FindRomSource(installed) != null;
            job.Finish(
                (
                    installed.MissingRoms.Count == 0 ? "✓ Installed. Ready to play."
                    : getRom ? "✓ Installed. Getting its ROM next (see below)."
                    : "✓ Installed. Its ROM isn't in the catalog; use Add ROM in My Tables."
                ) + installed.InstallNote
            );
            RefreshInstalled();
            App.Catalog.ApplyFilter();
            if (getRom)
            {
                // Its ROM fetch marks the table busy itself.
                ReleaseReplacing();
                _ = App.Roms.GetRomAsync(installed);
            }
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
            ReleaseReplacing();
        }

        void ReleaseReplacing()
        {
            if (replacing != null)
            {
                EndBusy(replacing.TablePath);
                replacing = null;
            }
        }
    }

    const string SearchButton = "SEARCH THE CATALOG…";
    const string NotInCatalogButton = "NOT IN THE CATALOG";

    /// <summary>
    /// The catalog game and table file (version) the file is: one of the guesses from its name, or one found by
    /// searching the catalog as in Get Tables; or a stand-in entry when it isn't in the catalog. Null when cancelled.
    /// </summary>
    async Task<(VpsGame Game, VpsTableFile TableFile)?> AskWhichTableAsync(string file)
    {
        var name = Path.GetFileName(file);
        var games = App.Catalog._allGames;
        var guesses = TableFileMatch.Guess(file, games);
        List<string> buttons =
        [
            .. guesses.Select(g => g.DisplayTitle),
            .. (games.Count > 0 ? new[] { SearchButton } : []),
            NotInCatalogButton,
            "CANCEL",
        ];
        var message =
            games.Count == 0
                ? $"{name}\n\nThe catalog hasn't loaded, so this table can't be matched to it. Install it under its file name? It won't get ROM downloads, add-ons or update checks from the catalog."
            : guesses.Count == 0
                ? $"{name}\n\nNo catalog table matches this file name. Search the catalog for it, so it gets its ROM, add-ons and updates. "
                    + "Not in the catalog? It's installed under its file name, without ROM downloads, add-ons or update checks."
            : $"{name}\n\nPick the table it is, so it gets its ROM, add-ons and updates from the catalog. "
                + "Not listed? Search the catalog for it.";
        while (true)
        {
            var pick = await Ui.ChoiceAsync("Which table is this?", message, [.. buttons]);
            if (pick < 0 || buttons[pick] == "CANCEL")
            {
                return null;
            }

            if (buttons[pick] == NotInCatalogButton)
            {
                return TableFileMatch.NotInCatalog(file);
            }

            var game =
                buttons[pick] == SearchButton
                    ? await SearchCatalogAsync(
                        file,
                        name,
                        // Start from the likeliest name: its game shows up with the others that share it.
                        guesses.FirstOrDefault()?.Name
                            ?? ""
                    )
                    : guesses[pick];
            if (game == null)
            {
                continue; // back to the guesses
            }

            if (await AskWhichVersionAsync(game, file) is { } tf)
            {
                return (game, tf);
            }
        }
    }

    /// <summary>Search the catalog for the table, as in Get Tables (games with a table of the file's kind). Null when cancelled.</summary>
    Task<VpsGame?> SearchCatalogAsync(string file, string heading, string query) =>
        Ui.PickAsync(
            "Find the table in the catalog",
            $"{heading}\n\nSearch by name, maker, year or author, as in Get Tables.",
            q =>
                TableFileMatch
                    .Search(q, file, App.Catalog._allGames)
                    .Select(g => (g, $"{g.DisplayTitle}  ·  {TablesLabel(g, file)}"))
                    .ToList(),
            "THIS ONE",
            query
        );

    /// <summary>
    /// ⋯ → Fix table match…: link a table to the catalog game and version it really is, when Add a table file
    /// matched it to the wrong one or it went in as Not in the catalog. Its files stay as they are.
    /// </summary>
    [RelayCommand]
    async Task FixTableMatchAsync(InstalledItem item)
    {
        if (App.Catalog._allGames.Count == 0)
        {
            await Ui.ChoiceAsync(
                "The catalog hasn't loaded",
                "Fix table match needs the catalog. Try again once Get Tables has loaded it.",
                ["OK"]
            );
            return;
        }

        await MatchOneAsync(item, null);
    }

    /// <summary>
    /// Fix table match for one table. <paramref name="progress"/> ("Table 3 of 40") heads the dialogs when it's
    /// one of several (<see cref="FixUnlinkedNowAsync"/>). False when the search was closed: skipped.
    /// </summary>
    async Task<bool> MatchOneAsync(InstalledItem item, string? progress)
    {
        var games = App.Catalog._allGames;
        if (!item.CanRename)
        {
            return false;
        }

        var table = item.Table;
        var m = table.Manifest;
        var current = string.IsNullOrEmpty(m.GameId)
            ? null
            : games.FirstOrDefault(g => g.Id == m.GameId);
        var heading =
            current == null
                ? $"“{m.GameName}” isn't linked to the catalog, so it gets no ROM downloads, add-ons or update checks."
                : $"“{m.GameName}” is linked to {current.DisplayTitle}"
                    + (
                        current.TableFiles?.FirstOrDefault(f => f.Id == m.TableFileId) is { } now
                            ? $", {VersionLabel(now)}."
                            : "."
                    );
        if (progress != null)
        {
            heading = $"{progress} · close this window to skip it.\n\n{heading}";
        }
        // Start from the game it's linked to, else the likeliest from its file and folder names.
        var start =
            current?.Name
            ?? TableFileMatch.Guess(table.TablePath, games, 1).FirstOrDefault()?.Name
            ?? TableFileMatch
                .Guess(
                    Path.GetFileName(table.Folder) + Path.GetExtension(table.TablePath),
                    games,
                    1
                )
                .FirstOrDefault()
                ?.Name
            ?? "";
        VpsGame? game;
        VpsTableFile? tf;
        do
        {
            game = await SearchCatalogAsync(table.TablePath, heading, start);
            if (game == null)
            {
                return false;
            }
            start = game.Name;
            // Always asked here (when there's a choice): the version guessed from the file name may be what
            // linked it wrongly. The one it's linked to now comes first.
            tf = await AskWhichVersionAsync(
                game,
                table.TablePath,
                always: true,
                current: game.Id == m.GameId ? m.TableFileId : null
            );
        } while (tf == null);

        // A rescan while the dialogs were open replaces the item's table (a job may have saved add-ons or ROMs
        // to vrtm.json meanwhile): link the current one, not the one read before.
        table = item.Table;
        var old = table.Manifest.GameName;
        // The outcome goes in the Downloads tray, where it can be cleared like any other job.
        var job = new JobItem($"Fix table match: {old}") { FileNames = [old] };
        App.Downloads.Jobs.Insert(0, job);
        if (!item.CanRename)
        {
            job.Finish(
                "Its table file or vrtm.json is gone, so the link can't be saved. Rescan My Tables and try again.",
                error: true
            );
            return true;
        }

        // An update, add-on or ROM job on this table rewrites its vrtm.json from what it read at the start.
        if (!TryBeginBusy(table.TablePath))
        {
            job.Finish(
                "This table has a job running (see above): fix its match once that's done.",
                error: true
            );
            return true;
        }

        try
        {
            // Which ROM the script loads decides whether the new game's ROM list applies (tables run to
            // hundreds of MB: read off the UI thread). Unreadable: link it anyway and leave its ROMs alone.
            VpxRomInfo? script = null;
            if (table.IsVpx)
            {
                job.Report("Reading the table's script…");
                try
                {
                    script = await Task.Run(() => RomDetector.Inspect(table.TablePath));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    script = null;
                }
            }

            if (!TableInstaller.Relink(table, game, tf, script))
            {
                job.Finish($"✓ Already linked to {game.DisplayTitle}, v{tf.Version ?? "?"}.");
                return true;
            }
        }
        catch (Exception e)
        {
            job.Finish("Failed: " + e.Message, error: true);
            return true;
        }
        finally
        {
            EndBusy(table.TablePath);
        }

        item.Relinked(); // name, picture and everything else the card reads from the manifest
        ShowInstalled(); // its ROM, add-ons, rules and update check now go by the new game
        App.Catalog.ApplyFilter(); // Get Tables marks it installed
        job.Finish($"✓ Linked to {game.DisplayTitle}, v{tf.Version ?? "?"}.");
        return true;
    }

    /// <summary>
    /// FIX THEM NOW after AUTO-LINK: Fix table match for each table in turn. Closing a search asks whether to
    /// go on to the next or stop, so nobody is made to link a hundred tables in one go.
    /// </summary>
    internal async Task FixUnlinkedNowAsync(IReadOnlyList<InstalledItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            // Linked meanwhile (a rescan, or by hand), or its folder is gone.
            if (!IsUnlinked(items[i]) || !items[i].CanRename)
            {
                continue;
            }

            if (await MatchOneAsync(items[i], $"Table {i + 1} of {items.Count}"))
            {
                continue;
            }

            var left = items.Skip(i + 1).Count(t => IsUnlinked(t) && t.CanRename);
            if (
                left == 0
                || await Ui.ChoiceAsync(
                    "Skipped",
                    $"{items[i].Title} stays unlinked. {left} more to go: carry on with the next one, or stop and "
                        + "fix the rest later (they're under My Tables → Not linked).",
                    ["NEXT TABLE", "STOP FOR NOW"]
                ) != 0
            )
            {
                return;
            }
        }
    }

    /// <summary>"14 VPX tables": how many of the game's table files are of the file's kind.</summary>
    static string TablesLabel(VpsGame g, string file)
    {
        var n = TableFileMatch.TableFiles(g, file).Count;
        var kind = TableFileMatch.IsFuturePinball(file) ? "Future Pinball" : "VPX";
        return $"{n} {kind} table{(n == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Which of the game's table files it is. Asked only when the file name doesn't settle it (or
    /// <paramref name="always"/>, when there's more than one), since update checks compare against this one.
    /// The table file <paramref name="current"/> (by id) goes first. Null when cancelled.
    /// </summary>
    async Task<VpsTableFile?> AskWhichVersionAsync(
        VpsGame game,
        string file,
        bool always = false,
        string? current = null
    )
    {
        var files = TableFileMatch
            .TableFiles(game, file)
            .OrderByDescending(t => current != null && t.Id == current)
            .ToList();
        if (files.Count <= 1 || (!always && TableFileMatch.TableFileIsClear(game, file)))
        {
            return files.FirstOrDefault();
        }

        return await Ui.PickAsync(
            "Which version is it?",
            $"{Path.GetFileName(file)}\n\n{game.DisplayTitle} has {files.Count} tables in the catalog. "
                + "Pick the one you downloaded, so updates are checked against it. "
                + (
                    current != null && files[0].Id == current
                        ? "The one it's linked to now is first."
                        : "The likeliest is first."
                ),
            _ => files.Select(t => (t, VersionLabel(t))).ToList(),
            "THIS ONE"
        );
    }

    /// <summary>"v1.1.0 — Flupper, RobbyKingPin, Rothbauerw +17 · Hybrid, VR, VLM · updated 2024-05-01".</summary>
    static string VersionLabel(VpsTableFile t)
    {
        var authors = t.Authors ?? [];
        var parts = new List<string>
        {
            $"v{(string.IsNullOrWhiteSpace(t.Version) ? "?" : t.Version.Trim().TrimStart('v', 'V'))} — "
                + (
                    authors.Count == 0
                        ? "unknown author"
                        : string.Join(", ", authors.Take(3))
                            + (authors.Count > 3 ? $" +{authors.Count - 3}" : "")
                ),
        };
        if (t.Features is { Count: > 0 } f)
        {
            parts.Add(string.Join(", ", f));
        }

        if (t.ReleasedAt is > 0)
        {
            parts.Add(
                "updated "
                    + DateTimeOffset
                        .FromUnixTimeMilliseconds(t.ReleasedAt.Value)
                        .ToString("yyyy-MM-dd")
            );
        }

        return string.Join("  ·  ", parts);
    }
}
