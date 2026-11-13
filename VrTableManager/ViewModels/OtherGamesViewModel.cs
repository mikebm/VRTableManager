using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → Other games: Future Pinball, and Pinball FX / Pinball M through UEVR.
public sealed partial class OtherGamesViewModel(MainViewModel app) : ChildViewModel(app)
{
    // ---- Future Pinball ----

    [ObservableProperty]
    public partial string FpDir { get; set; } = "";

    [ObservableProperty]
    public partial bool FpRunAsAdmin { get; set; }

    [ObservableProperty]
    public partial string FpStatus { get; set; } = "";

    // ---- Pinball FX and Pinball M (UEVR) ----

    [ObservableProperty]
    public partial string UevrDir { get; set; } = "";

    [ObservableProperty]
    public partial string PfxStatus { get; set; } = "";

    partial void OnFpDirChanged(string value)
    {
        SaveSetting(s => s.FpDir = value);
        App.RefreshSetupStatus();
        App.Library.RefreshInstalled();
    }

    partial void OnUevrDirChanged(string value)
    {
        SaveSetting(s => s.UevrDir = value);
        App.RefreshSetupStatus();
        App.Library.RefreshInstalled();
    }

    partial void OnFpRunAsAdminChanged(bool value) => SaveSetting(s => s.FpRunAsAdmin = value);

    partial void OnApplyPinballUevrProfileChanged(bool value)
    {
        SaveSetting(s => s.ApplyPinballUevrProfile = value);
        App.RefreshSetupStatus();
    }

    partial void OnShowZenReminderChanged(bool value) =>
        SaveSetting(s => s.ShowZenReminder = value);

    internal override void LoadSettings(AppSettings s)
    {
        FpDir = s.FpDir;
        UevrDir = s.UevrDir;
        FpRunAsAdmin = s.FpRunAsAdmin;
        ApplyPinballUevrProfile = s.ApplyPinballUevrProfile;
        ShowZenReminder = s.ShowZenReminder;
    }

    internal void RefreshStatus()
    {
        FpStatus = Engines.For(TableManifest.FuturePinball).DescribeSetup(Settings);
        PfxStatus = Engines.For(TableManifest.PinballFx).DescribeSetup(Settings);
        PmStatus = Engines.For(TableManifest.PinballM).DescribeSetup(Settings);
        PfxProfileStatus = DescribeUevrProfile(ZenGames.PinballFx);
        PmProfileStatus = DescribeUevrProfile(ZenGames.PinballM);
        PfxHasUevrBackup = UevrPinballProfile.HasBackup(ZenGames.PinballFx);
        PmHasUevrBackup = UevrPinballProfile.HasBackup(ZenGames.PinballM);
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
        App.RefreshSetupStatus();
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
            !await Ui.ConfirmAsync(
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
        App.RefreshSetupStatus();
    }

    [ObservableProperty]
    public partial string PmStatus { get; set; } = "";

    [RelayCommand]
    async Task BrowseFpDirAsync()
    {
        if (await Ui.PickFolderAsync("Future Pinball folder") is { } d)
        {
            FpDir = d;
        }
    }

    [RelayCommand]
    async Task BrowseUevrDirAsync()
    {
        if (await Ui.PickFolderAsync("UEVR folder (contains UEVRInjector.exe)") is { } d)
        {
            UevrDir = d;
        }
    }

    /// <param name="engineId">Which Zen game the profile is for: "PFX" (Pinball FX) or "PM" (Pinball M).</param>
    [RelayCommand]
    async Task ImportUevrProfileAsync(string? engineId)
    {
        var game = ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx;
        var zip = await Ui.PickFileAsync($"Choose a UEVR profile zip for {game.Title}", "*.zip");
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
        App.RefreshSetupStatus();
    }

    [RelayCommand]
    void OpenUevrProfileFolder(string? engineId)
    {
        var dir = PfxSetup.ProfileDirFor(
            ZenGames.For(engineId ?? TableManifest.PinballFx) ?? ZenGames.PinballFx
        );
        Directory.CreateDirectory(dir);
        Ui.OpenExternal(dir);
    }
}
