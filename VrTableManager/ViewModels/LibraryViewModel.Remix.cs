using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// My Tables → MORE → VPU Remixes: a remix made from the card's table (from the catalog, or a file you downloaded),
// as a new table or in its place; and, for a remix in a table's place, putting the original back.
public sealed partial class LibraryViewModel
{
    /// <summary>MORE → VPU Remixes → Get from the catalog → one remix.</summary>
    [RelayCommand]
    Task InstallRemixChoice(RemixChoice c) =>
        App.Catalog.InstallRemixOntoAsync(c.Game, c.File, c.Item.Table);

    /// <summary>
    /// MORE → VPU Remixes → Add a remix file you downloaded: a .dif (or a zip of one) is applied to this table; a
    /// remix already made into a .vpx goes in as it is. Either way it's a new table or takes this one's place.
    /// </summary>
    [RelayCommand]
    async Task AddRemixFile(InstalledItem item)
    {
        var onto = item.Table;
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        if (
            await Ui.PickFileAsync(
                $"Choose the {onto.Manifest.GameName} remix you downloaded",
                "*.dif;*.vpx;*.zip;*.7z;*.rar",
                startFolder: downloads
            )
            is not { } file
        )
        {
            return;
        }

        // Which catalog remix it is, so it gets its picture and updates; one that isn't listed goes in by itself.
        var title = $"{onto.Manifest.GameName}: VPU Remix";
        var shown = item.RemixChoices.Take(3).ToList();
        VpsGame game;
        VpsTableFile tf;
        var pick =
            shown.Count == 0
                ? shown.Count
                : await Ui.ChoiceAsync(
                    title,
                    $"{Path.GetFileName(file)}\n\nWhich of the catalog's remixes is it? One that isn't listed "
                        + "installs without update checks.",
                    [.. shown.Select(c => c.Label), "NOT LISTED", "CANCEL"]
                );
        if (pick < 0 || pick > shown.Count)
        {
            return;
        }

        if (pick < shown.Count)
        {
            (game, tf) = (shown[pick].Game, shown[pick].File);
        }
        else
        {
            var id = CatalogViewModel.BaseManifest(onto);
            game =
                App.Catalog.CatalogGame(id.GameId)
                ?? new VpsGame
                {
                    Id = "",
                    Name = id.MatchName,
                    Manufacturer = id.Manufacturer,
                    Year = id.Year,
                };
            // Not the original's id: it would install into its folder and be "updated" back to it.
            tf = new VpsTableFile
            {
                Id = "",
                TableFormat = "VPX",
                Features = [VpuRemix.Feature],
                ImgUrl = id.ImgUrl,
            };
        }

        if (VpuRemix.CandidateFor(onto, tf) is not { } candidate)
        {
            PlayStatus =
                $"{onto.Manifest.GameName}: the table file to patch is gone (rescan My Tables, or install the "
                + "original again from Get Tables).";
            return;
        }

        // Say so now when it's plainly for another table: the catalog names another base, or the patch was made from
        // a table of another size (a .dif's patch reads its whole original).
        var against = new List<string>();
        if (
            !candidate.Exact
            && VpuRemix.BaseOf(tf, App.Catalog.AllGames, game) is { } parent
            && parent.File.Id != tf.Id
        )
        {
            against.Add(
                $"The catalog says it's made from {parent.Game.Name} {VpuRemix.Describe(parent.File)}, which isn't "
                    + $"your {onto.Manifest.GameName} (Get Tables can install that one)."
            );
        }
        if (
            file.EndsWith(VpuRemix.PatchExt, StringComparison.OrdinalIgnoreCase)
            && await Task.Run(() => VpuRemix.OriginalSize(file)) is { } need
            && File.Exists(candidate.SourcePath)
            && new FileInfo(candidate.SourcePath).Length is var len
            && !VpuRemix.SizeFits(len, need)
        )
        {
            against.Add(
                $"The patch was made from a table of about {VpuRemix.SizeText(need)}; your "
                    + $"{onto.Manifest.GameName} is {VpuRemix.SizeText(len)}, so it's for another table or version. "
                    + "If the page offers several .dif files, each is named after the table it fits."
            );
        }
        if (
            against.Count > 0
            && await Ui.ChoiceAsync(
                title,
                $"{Path.GetFileName(file)} almost certainly won't fit your {onto.Manifest.GameName}.\n\n"
                    + string.Join("\n\n", against),
                ["TRY ANYWAY", "CANCEL"]
            ) != 0
        )
        {
            return;
        }

        var choice = await App.Catalog.AskNewOrReplaceAsync(
            title,
            $"A .dif patch is applied to your {onto.Manifest.GameName}; a remix already made into a .vpx goes in "
                + "as it is.",
            onto,
            "\n\nIf the patch doesn't fit your copy, nothing is installed."
        );
        if (choice < 0)
        {
            return;
        }

        await InstallFileAsync(
            file,
            game,
            tf,
            choice == 1 ? onto : null,
            [candidate.SourcePath],
            remix: true
        );
    }

    /// <summary>MORE → VPU Remixes → Put the original back: the table the remix replaced, kept beside it.</summary>
    [RelayCommand]
    async Task RevertRemix(InstalledItem item)
    {
        var remix = item.Table;
        if (remix.Manifest.RemixOriginal?.Manifest is not { } original)
        {
            return;
        }

        if (
            await Ui.ChoiceAsync(
                $"{remix.Manifest.GameName}: put the original back",
                $"Put {original.GameName}"
                    + (string.IsNullOrWhiteSpace(original.Version) ? "" : $" v{original.Version}")
                    + " back in place of the remix? The remix's table file is removed; the table's ROMs, high "
                    + "scores, settings, add-ons and play stats stay.",
                ["PUT IT BACK", "CANCEL"]
            ) != 0
        )
        {
            return;
        }

        if (!TryBeginBusy(remix.TablePath))
        {
            PlayStatus =
                $"{remix.Manifest.GameName} has a job running (see Downloads): wait for it, then try again.";
            return;
        }

        try
        {
            var back = await Task.Run(() => TableInstaller.RevertRemix(remix, Settings));
            PlayStatus =
                $"Put {original.GameName} back in place of the remix. Its VR lobby video is the remix's: record "
                + "it again if they look different."
                + back.InstallNote;
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't put the original back: " + e.Message;
        }
        finally
        {
            EndBusy(remix.TablePath);
        }
        RefreshInstalled();
        App.Catalog.ApplyFilter();
    }
}
