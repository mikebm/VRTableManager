using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// VR tables whose cabinet DMD showed the PuP pack's FullDMD under VPX 10.8.0 and stays dark under 10.8.1
// (vpinball/vpinball#4226): asked about before a VR launch, fixed in the table file with a backup kept.
public sealed partial class MainViewModel
{
    /// <summary>
    /// Before a VR launch: offer to fix the table's PuP DMD when it needs it. Returns false if the user cancels
    /// the launch. Says nothing when the table doesn't need it, or VPX is older than 10.8.1 (which still
    /// shows the DMD the old way and wouldn't understand the fix).
    /// </summary>
    async Task<bool> OfferDmdFixAsync(InstalledItem item)
    {
        var path = item.Table.TablePath;
        if (!item.Table.IsVpx || !Settings.UsePupPlugin || !File.Exists(path))
        {
            return true;
        }

        if (Settings.DmdFixDeclined.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (PupPacks.StateOf(item.Table).Folders.Count == 0)
        {
            return true;
        }

        if (!VrDmdFix.EngineSupportsFix(VpxSetup.InstalledVersion(Settings)))
        {
            return true;
        }

        var flashers = await Task.Run(() => VrDmdFix.Find(path));
        if (flashers.Count == 0)
        {
            return true;
        }

        // Short labels: four buttons fit on one row.
        List<string> buttons = ["FIX AND PLAY", "PLAY AS IS", "DON'T ASK AGAIN", "CANCEL"];
        var pick = await _ui.ChoiceAsync(
            "This table's PuP DMD won't show in VR",
            $"{item.Title}'s VR cabinet shows its PuP pack's DMD the way VPX 10.8.0 did it, which VPX 10.8.1 no longer "
                + "supports, so the DMD stays dark.\n\n"
                + "The fix changes one setting in the table file: the display "
                + string.Join(", ", flashers.Select(f => $"\"{f.Name}\""))
                + " shows VPX's Score View, where the PuP pack draws its DMD.\n\n"
                + "The original file is kept next to the table, and the table's MORE menu can put it back.\n\n"
                + "Don't ask again: only for this table.",
            [.. buttons]
        );
        switch (pick < 0 ? "CANCEL" : buttons[pick])
        {
            case "FIX AND PLAY":
                PlayStatus = $"Fixing {item.Title}'s DMD…";
                try
                {
                    await Task.Run(() => VrDmdFix.Apply(path));
                }
                catch (Exception e)
                {
                    var go = await _ui.ChoiceAsync(
                        "The fix didn't work",
                        $"The table wasn't changed: {e.Message}",
                        ["PLAY ANYWAY", "CANCEL"]
                    );
                    return go == 0;
                }
                item.HasDmdFix = true;
                return true;
            case "DON'T ASK AGAIN":
                Settings.DmdFixDeclined.Add(path);
                Settings.Save();
                item.DmdFixDeclined = true;
                return true;
            case "PLAY AS IS":
                return true;
            default:
                return false;
        }
    }

    [RelayCommand]
    async Task UndoDmdFix(InstalledItem item)
    {
        var path = item.Table.TablePath;
        try
        {
            // A table update since the fix brought a new, unfixed file: the backup is an older version, not its original.
            if (await Task.Run(() => VrDmdFix.Find(path)).ConfigureAwait(true) is { Count: > 0 })
            {
                File.Delete(VrDmdFix.BackupPath(path));
                PlayStatus =
                    $"{item.Title} has been updated since the DMD fix; removed the old backup.";
            }
            else
            {
                VrDmdFix.Undo(path);
                if (!Settings.DmdFixDeclined.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    Settings.DmdFixDeclined.Add(path);
                    Settings.Save();
                }
                PlayStatus =
                    $"Put {item.Title}'s original table file back. MORE → \"Fix the PuP DMD for VR\" applies the fix again.";
            }
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't undo the DMD fix: " + e.Message;
            return;
        }
        item.HasDmdFix = false;
        item.DmdFixDeclined = true;
    }

    /// <summary>
    /// From the MORE menu, for a table whose fix was undone or declined: apply it now (no dialog, it was asked
    /// for). VPX's version and the PuP pack aren't checked here: the user knows they want it.
    /// </summary>
    [RelayCommand]
    async Task ApplyDmdFix(InstalledItem item)
    {
        var path = item.Table.TablePath;
        Settings.DmdFixDeclined.RemoveAll(p =>
            string.Equals(p, path, StringComparison.OrdinalIgnoreCase)
        );
        Settings.Save();
        item.DmdFixDeclined = false;
        try
        {
            var fixedOnes = await Task.Run(() => VrDmdFix.Apply(path));
            if (fixedOnes.Count == 0)
            {
                PlayStatus =
                    $"{item.Title} doesn't need the PuP DMD fix (no PuP display set up the VPX 10.8.0 way).";
                return;
            }
            item.HasDmdFix = true;
            PlayStatus =
                $"Fixed {item.Title}'s PuP DMD for VR; the original is kept and MORE can put it back.";
        }
        catch (Exception e)
        {
            PlayStatus = "The DMD fix didn't work; the table wasn't changed: " + e.Message;
        }
    }
}
