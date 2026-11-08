using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// My Tables → ADD A TABLE FILE…: install a table zip (or .vpx / .fpt) you already downloaded yourself.
// Which table it is comes from the catalog (guessed from the file name, you confirm), so it gets its ROM,
// add-ons, art and updates like a table installed from Get Tables.
public sealed partial class MainViewModel
{
    [RelayCommand]
    async Task AddTableFileAsync()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        if (
            await _ui.PickFileAsync(
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
            await _ui.ChoiceAsync(
                "Future Pinball isn't set up",
                "Set the Future Pinball folder on the Setup tab first.",
                ["OK"]
            );
            return;
        }

        // Which table is it?
        var name = Path.GetFileName(file);
        var guesses = TableFileMatch.Guess(file, _allGames);
        VpsGame game;
        VpsTableFile? tf;
        List<string> buttons =
        [
            .. guesses.Select(g => g.DisplayTitle),
            "NOT IN THE CATALOG",
            "CANCEL",
        ];
        var message =
            _allGames.Count == 0
                ? $"{name}\n\nThe catalog hasn't loaded, so this table can't be matched to it. Install it under its file name? It won't get ROM downloads, add-ons or update checks from the catalog."
            : guesses.Count == 0
                ? $"{name}\n\nNo catalog table matches this file name. Install it under its file name? It won't get ROM downloads, add-ons or update checks from the catalog. "
                    + "(If it is in the catalog, find it in Get Tables and install it from there instead.)"
            : $"{name}\n\nPick the table it is, so it gets its ROM, add-ons and updates from the catalog. "
                + "Not listed? Choose Not in the catalog, or find it in Get Tables.";
        var pick = await _ui.ChoiceAsync("Which table is this?", message, [.. buttons]);
        if (pick < 0 || buttons[pick] == "CANCEL")
        {
            return;
        }

        if (buttons[pick] == "NOT IN THE CATALOG")
        {
            (game, tf) = TableFileMatch.NotInCatalog(file);
        }
        else
        {
            game = guesses[pick];
            tf = TableFileMatch.PickTableFile(game, file);
            if (tf == null)
            {
                return;
            }
        }

        // Already installed: this replaces it (ROMs, high scores and settings in its folder stay).
        var replacing = string.IsNullOrEmpty(game.Id)
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
            && await _ui.ChoiceAsync(
                $"{game.Name} is already installed",
                "Replace it with this file? Its ROMs, high scores and settings stay.",
                ["REPLACE", "CANCEL"]
            ) != 0
        )
        {
            return;
        }

        var job = new JobItem($"{(replacing == null ? "" : "Update: ")}{game.Name} — from {name}")
        {
            FileNames = [game.Name],
        };
        Jobs.Insert(0, job);
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
                replacing
            );
            var getRom = installed.MissingRoms.Count > 0 && FindRomSource(installed) != null;
            job.Finish(
                installed.MissingRoms.Count == 0 ? "✓ Installed. Ready to play."
                : getRom ? "✓ Installed. Getting its ROM next (see below)."
                : "✓ Installed. Its ROM isn't in the catalog; use Add ROM in My Tables."
            );
            RefreshInstalled();
            ApplyFilter();
            if (getRom)
            {
                _ = GetRomAsync(installed);
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
    }
}
