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
        if (!item.Table.IsVpx || !Settings.UsePupPlugin || !File.Exists(path)) return true;
        if (Settings.DmdFixDeclined.Contains(path, StringComparer.OrdinalIgnoreCase)) return true;
        if (PupPacks.StateOf(item.Table).Folders.Count == 0) return true;
        if (!VrDmdFix.EngineSupportsFix(VpxSetup.InstalledVersion(Settings))) return true;
        var flashers = await Task.Run(() => VrDmdFix.Find(path));
        if (flashers.Count == 0) return true;

        List<string> buttons = ["FIX AND PLAY", "PLAY WITHOUT FIXING", "DON'T ASK FOR THIS TABLE", "CANCEL"];
        var pick = await _ui.ChoiceAsync("This table's PuP DMD won't show in VR",
            $"{item.Title}'s VR cabinet shows its PuP pack's DMD the way VPX 10.8.0 did it, which VPX 10.8.1 no longer " +
            "supports, so the DMD stays dark.\n\n" +
            "The fix changes one setting in the table file (the display " + string.Join(", ", flashers.Select(f => $"\"{f.Name}\"")) +
            " shows VPX's Score View, where the PuP pack draws its DMD). The original is kept next to the table as\n" +
            Path.GetFileName(VrDmdFix.BackupPath(path)) + "\nand the table's MORE menu can put it back.",
            [.. buttons]);
        switch (pick < 0 ? "CANCEL" : buttons[pick])
        {
            case "FIX AND PLAY":
                PlayStatus = $"Fixing {item.Title}'s DMD…";
                try { await Task.Run(() => VrDmdFix.Apply(path)); }
                catch (Exception e)
                {
                    var go = await _ui.ChoiceAsync("The fix didn't work", $"The table wasn't changed: {e.Message}", ["PLAY ANYWAY", "CANCEL"]);
                    return go == 0;
                }
                item.HasDmdFix = true;
                return true;
            case "DON'T ASK FOR THIS TABLE":
                Settings.DmdFixDeclined.Add(path);
                Settings.Save();
                return true;
            case "PLAY WITHOUT FIXING": return true;
            default: return false;
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
                PlayStatus = $"{item.Title} has been updated since the DMD fix; removed the old backup.";
            }
            else
            {
                VrDmdFix.Undo(path);
                if (!Settings.DmdFixDeclined.Contains(path, StringComparer.OrdinalIgnoreCase)) { Settings.DmdFixDeclined.Add(path); Settings.Save(); }
                PlayStatus = $"Put {item.Title}'s original table file back (the PuP DMD fix is undone and won't be offered again).";
            }
        }
        catch (Exception e) { PlayStatus = "Couldn't undo the DMD fix: " + e.Message; return; }
        item.HasDmdFix = false;
    }
}
