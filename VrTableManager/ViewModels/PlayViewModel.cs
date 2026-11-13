using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Playing a table from My Tables (checks before a VR launch, play counts, what VPX logged), per-table VR settings,
// and Setup → Play.
public sealed partial class PlayViewModel(MainViewModel app) : ChildViewModel(app)
{
    // ---- Setup → Play ----

    /// <summary>The window hides while a table runs and comes back when it closes.</summary>
    [ObservableProperty]
    public partial bool ReturnAfterPlay { get; set; }

    /// <summary>VR play uses VPX's built-in PinMAME, which reads ROMs from each table's own folder.</summary>
    [ObservableProperty]
    public partial bool UseBuiltInPinmame { get; set; }

    /// <summary>VR play shows PuP packs through VPX's PinUP Player plugin.</summary>
    [ObservableProperty]
    public partial bool UsePupPlugin { get; set; }

    /// <summary>VR play turns on VPX's FlexDMD and B2S plugins.</summary>
    [ObservableProperty]
    public partial bool UseDisplayPlugins { get; set; }

    /// <summary>VR play adds a backglass display to tables without one, for PuP packs and B2S backglasses.</summary>
    [ObservableProperty]
    public partial bool VrAutoBackglass { get; set; }

    /// <summary>Desktop play turns off VPX's bloom.</summary>
    [ObservableProperty]
    public partial bool DesktopBloomOff { get; set; }

    /// <summary>A table's hand-set VR room switch is turned on for VR play and off for desktop play.</summary>
    [ObservableProperty]
    public partial bool AutoVrRoom { get; set; }

    partial void OnReturnAfterPlayChanged(bool value) => SaveSetting(s => s.ReturnAfterPlay = value);

    partial void OnUseBuiltInPinmameChanged(bool value) => SaveSetting(s => s.UseBuiltInPinmame = value);

    partial void OnUsePupPluginChanged(bool value) => SaveSetting(s => s.UsePupPlugin = value);

    partial void OnUseDisplayPluginsChanged(bool value) => SaveSetting(s => s.UseDisplayPlugins = value);

    partial void OnVrAutoBackglassChanged(bool value) => SaveSetting(s => s.VrAutoBackglass = value);

    partial void OnDesktopBloomOffChanged(bool value) => SaveSetting(s => s.DesktopBloomOff = value);

    partial void OnAutoVrRoomChanged(bool value) => SaveSetting(s => s.AutoVrRoom = value);

    internal override void LoadSettings(AppSettings s)
    {
        ReturnAfterPlay = s.ReturnAfterPlay;
        UseBuiltInPinmame = s.UseBuiltInPinmame;
        UsePupPlugin = s.UsePupPlugin;
        UseDisplayPlugins = s.UseDisplayPlugins;
        VrAutoBackglass = s.VrAutoBackglass;
        DesktopBloomOff = s.DesktopBloomOff;
        AutoVrRoom = s.AutoVrRoom;
    }

    // ---- Playing ----

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
                App.Library.PlayStatus = "";
                return;
            }
            if (
                vr
                && ZenGames.For(engine.Id) is { } zen
                && Settings.ShowZenReminder
                && !await ZenReminderAsync(zen)
            )
            {
                App.Library.PlayStatus = "";
                return;
            }
            App.Library.PlayStatus = $"Starting {item.Title}…";
            proc = await engine.LaunchAsync(Settings, item.Table, vr, CancellationToken.None);
            App.Library.PlayStatus = $"Playing {item.Title}…";
            var started = DateTime.Now;
            var path = item.Table.TablePath;
            TrySaveStats(st => st.RecordPlay(path, started));
            item.ApplyStats(App.Library._stats);
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
                            if (item.Table.IsVpx)
                            {
                                VpxIniLayers.Finish(running); // waits for the save back, so the panels read it
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    ShowVpxSettingsAfterPlay(item)
                                );
                            }
                            running.Dispose();
                        },
                        TaskScheduler.Default
                    );
                return;
            }
            // Pick-and-return loop: get out of the way, come back when the table closes.
            Ui.HideForPlay();
            await proc.WaitForExitAsync();
            TrySaveStats(st => st.AddTime(path, DateTime.Now - started));
            item.ApplyStats(App.Library._stats);
            App.Library.PlayStatus = $"Finished {item.Title}. Pick your next table.";
            Ui.ShowAfterPlay();
            if (item.Table.IsVpx)
            {
                // Save what VPX changed now, not on the background wait, so the panel and card show it.
                VpxIniLayers.Finish(proc);
                ShowVpxSettingsAfterPlay(item);
            }

            await ExplainVpxExitAsync(item, started);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            App.Library.PlayStatus = "Launch cancelled (administrator prompt declined).";
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = "Couldn't start the table: " + e.Message;
        }
        finally
        {
            if (!waitsInBackground)
            {
                proc?.Dispose();
            }

            Ui.ShowAfterPlay();
        }
    }

    /// <summary>
    /// After a VPX table closes and what it changed is saved: the Setup panels show it without a restart. Keys
    /// remapped in VPX's menu, graphics changed in its F12 menu (LoadGraphics also reloads the table position, which
    /// VPX saves when it closes), and this table's own VR height.
    /// </summary>
    void ShowVpxSettingsAfterPlay(InstalledItem item)
    {
        App.Controls.RefreshControls();
        App.Graphics.LoadGraphics();
        item.TableVrHeight = VrFit.ReadTableHeight(item.Table.TablePath);
    }

    /// <summary>
    /// Before a VR launch: is a headset actually connected? Without one, VPX quietly opens on the
    /// monitor ("XR_ERROR_FORM_FACTOR_UNAVAILABLE" in its log), so ask the user to start Link first.
    /// Returns false if they cancel. When the check can't tell, it doesn't get in the way.
    /// </summary>
    internal async Task<bool> EnsureHeadsetAsync()
    {
        while (true)
        {
            App.Library.PlayStatus = "Checking your headset…";
            var hs = await HeadsetCheck.CheckAsync(Settings, TimeSpan.FromSeconds(15));
            if (hs.State is HeadsetState.Ready or HeadsetState.Unknown)
            {
                return true;
            }

            var hasLinkApp = File.Exists(HeadsetViewModel.MetaLinkApp);
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

            var pick = await Ui.ChoiceAsync("Headset not connected", message, [.. buttons]);
            var label = pick >= 0 ? buttons[pick] : "CANCEL";
            switch (label)
            {
                case "TRY AGAIN":
                    continue;
                case "OPEN META QUEST LINK":
                    Ui.OpenExternal(HeadsetViewModel.MetaLinkApp);
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
        var pick = await Ui.ChoiceAsync(
            $"Before {g.Title} starts in VR",
            string.Join("\n\n", lines),
            buttons
        );
        if (pick == 1)
        {
            App.OtherGames.ShowZenReminder = false;
        }

        return pick is 0 or 1;
    }

    void TrySaveStats(Action<PlayStats> change)
    {
        try
        {
            App.Library._stats = PlayStats.Mutate(change);
        }
        catch (Exception e)
        {
            // Play counting must never get in the way of playing: just say so. (Also called from the background wait.)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                App.Library.PlayStatus = "Couldn't save the play count and time: " + e.Message
            );
        }
    }

    // ---- Per-table VR height ---------------------------------------------------------------

    [RelayCommand]
    void ClearTableVrHeight(InstalledItem? item)
    {
        if (item == null)
        {
            return;
        }

        try
        {
            VrFit.WriteTableHeight(item.Table.TablePath, null);
            item.TableVrHeight = null;
            App.Library.PlayStatus =
                $"{item.Title} sits at the same height as every table again in VR.";
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = "Couldn't save the table's settings: " + e.Message;
        }
    }

    // ---- Per-table render scale ------------------------------------------------------------

    [RelayCommand]
    void SetTableScale(object? parameter)
    {
        if (parameter is not TableScaleChoice c)
        {
            return;
        }

        try
        {
            VpxGraphics.WriteTableScale(c.Item.Table.TablePath, c.Scale);
            c.Item.TableScale = c.Scale;
            App.Library.PlayStatus = c.Scale is { } s
                ? $"{c.Item.Title} will render at {GraphicsViewModel.ScaleText(s)} (other tables use {App.Graphics.GfxRenderScale})."
                : $"{c.Item.Title} uses the global render scale again ({App.Graphics.GfxRenderScale}).";
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = "Couldn't save the table's settings: " + e.Message;
        }
    }

    // ---- Pre-launch checks -------------------------------------------------------------------

    /// <summary>
    /// Everything that would make a VR launch fail or play badly, asked about once before starting:
    /// the headset, the OpenXR runtime, the table file and its ROM. Returns false if the user cancels.
    /// </summary>
    async Task<bool> PreflightAsync(InstalledItem item, IPinballEngine engine)
    {
        if (!await EnsureHeadsetAsync())
        {
            return false;
        }

        if (item.Table.IsVpx && !File.Exists(item.Table.TablePath))
        {
            await Ui.ChoiceAsync(
                "Table file missing",
                $"{item.Table.TablePath}\n\nisn't there any more. Reinstall the table from Get Tables.",
                ["OK"]
            );
            return false;
        }

        if (item.NeedsRom)
        {
            List<string> buttons = item.CanGetRom
                ? ["GET ROM", "PLAY ANYWAY", "CANCEL"]
                : ["ADD ROM", "PLAY ANYWAY", "CANCEL"];
            var pick = await Ui.ChoiceAsync(
                "This table's ROM isn't installed",
                $"{item.RomStatus}. Without it the table shows a PinMAME error and closes.",
                [.. buttons]
            );
            switch (pick < 0 ? "CANCEL" : buttons[pick])
            {
                case "GET ROM":
                    _ = App.Roms.GetRomAsync(item.Table);
                    return false;
                case "ADD ROM":
                    await App.Roms.AddRomAsync(item);
                    return false;
                case "PLAY ANYWAY":
                    break;
                default:
                    return false;
            }
        }

        if (!await OfferDmdFixAsync(item))
        {
            return false;
        }

        // Future Pinball's BAM uses OpenVR (SteamVR) on purpose; the runtime check is for OpenXR games.
        var usesOpenXr = engine.Id != TableManifest.FuturePinball;
        if (
            usesOpenXr
            && Settings.WarnSteamVrRuntime
            && OpenXrRuntimes.Active() is { IsSteamVr: true }
        )
        {
            var meta = OpenXrRuntimes.List().FirstOrDefault(r => r.IsMeta);
            List<string> buttons =
                meta != null
                    ? ["SWITCH TO META QUEST LINK", "PLAY ANYWAY", "DON'T ASK AGAIN", "CANCEL"]
                    : ["PLAY ANYWAY", "DON'T ASK AGAIN", "CANCEL"];
            var pick = await Ui.ChoiceAsync(
                "SteamVR is the active OpenXR runtime",
                "With a Quest, SteamVR adds an extra layer on top of Quest Link or Virtual Desktop, which often costs frames and adds stutter.\n\n"
                    + (
                        meta != null
                            ? "Switching to Meta Quest Link needs administrator permission once. "
                            : ""
                    )
                    + "You can change this any time on Setup → Headset.",
                [.. buttons]
            );
            switch (pick < 0 ? "CANCEL" : buttons[pick])
            {
                case "SWITCH TO META QUEST LINK":
                    if (!await OpenXrRuntimes.SetActiveAsync(meta!))
                    {
                        App.Library.PlayStatus =
                            "The runtime wasn't changed; playing through SteamVR.";
                    }

                    App.Headset.RefreshVrStatus();
                    break;
                case "DON'T ASK AGAIN":
                    App.Headset.WarnSteamVrRuntime = false;
                    break;
                case "PLAY ANYWAY":
                    break;
                default:
                    return false;
            }
        }

        if (usesOpenXr)
        {
            await App.Headset.ApplyAswAsync();
        }

        return true;
    }

    /// <summary>After a VPX table closes: if it crashed or ended within seconds, show what its log says.</summary>
    async Task ExplainVpxExitAsync(InstalledItem item, DateTime started)
    {
        if (!Settings.ExplainFailedLaunches || !item.Table.IsVpx)
        {
            return;
        }

        var quick = DateTime.Now - started < TimeSpan.FromSeconds(20);
        if (VpxLog.Find(Settings) is not { } log || VpxLog.LastSession(log) is not { } session)
        {
            return;
        }

        if (session.Started is { } t && t < started.AddSeconds(-10))
        {
            return; // not this run's log
        }

        if (!quick && session.ClosedCleanly)
        {
            return;
        }
        // A quick, clean close with nothing wrong (you just left the table): only harmless log lines, no dialog.
        if (session.ClosedCleanly && session.Problems.Count == 0 && session.Hints.Count == 0)
        {
            return;
        }

        await App.Maintenance.ShowLogSummaryAsync(
            quick && session.ClosedCleanly
                ? $"{item.Title} closed straight away"
                : $"{item.Title} stopped unexpectedly",
            log,
            session
        );
    }
}

/// <summary>One entry in a card's "Render scale" menu.</summary>
public sealed record TableScaleChoice(InstalledItem Item, string Label, double? Scale);
