using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

/// <summary>
/// The window's view model: the settings, the window's dialogs and a view model for each screen and Setup section.
/// Views bind to the child view models (<c>{Binding Library.Installed}</c>, or a view whose DataContext is one); the
/// child view models reach each other through <see cref="ChildViewModel.App"/>. What's left here is what spans them:
/// startup, a restored backup, the Setup rail and closing.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    public AppSettings Settings { get; } = AppSettings.Load();

    public IUiHost Ui { get; }

    // ---- Screens ----
    public CatalogViewModel Catalog { get; }
    public LibraryViewModel Library { get; }
    public DownloadsViewModel Downloads { get; }
    public RomsViewModel Roms { get; }
    public AddonsViewModel Addons { get; }
    public LobbyMediaViewModel Media { get; }
    public PlayViewModel Play { get; }
    public LobbyViewModel Lobby { get; }

    // ---- Setup sections (Downloads, Play and Lobby above have theirs too) ----
    public VpxViewModel Vpx { get; }
    public ControlsViewModel Controls { get; }
    public HeadsetViewModel Headset { get; }
    public FitViewModel Fit { get; }
    public GraphicsViewModel Graphics { get; }
    public OtherGamesViewModel OtherGames { get; }
    public UpdatesViewModel Updates { get; }
    public MaintenanceViewModel Maintenance { get; }

    readonly ChildViewModel[] _children;

    public MainViewModel(IUiHost ui)
    {
        Ui = ui;
        if (string.IsNullOrWhiteSpace(Settings.FpDir) && FpSetup.AutoDetect() is { } fp)
        {
            Settings.FpDir = fp;
            Settings.Save();
        }
        if (string.IsNullOrWhiteSpace(Settings.UevrDir) && PfxSetup.AutoDetectUevr() is { } uevr)
        {
            Settings.UevrDir = uevr;
            Settings.Save();
        }

        // Built before any reads its settings: setting one can reach the others (a new VPX folder rescans My Tables).
        _children =
        [
            Downloads = new(this),
            Catalog = new(this),
            Library = new(this),
            Roms = new(this),
            Addons = new(this),
            Media = new(this),
            Play = new(this),
            Lobby = new(this),
            Vpx = new(this),
            Controls = new(this),
            Headset = new(this),
            Fit = new(this),
            Graphics = new(this),
            OtherGames = new(this),
            Updates = new(this),
            Maintenance = new(this),
        ];
        foreach (var c in _children)
        {
            c.LoadSettings(Settings);
        }

        SetupSection = Settings.SetupSection;
        Lobby.RefreshDefaultLoadingVideo();
        Graphics.LoadGraphics();
        Library.RefreshInstalled();
        RefreshSetupStatus();
    }

    /// <summary>At startup: settings.json is there but couldn't be read, so nothing changed here is saved (see AppSettings.Unreadable).</summary>
    public async Task WarnIfSettingsUnreadableAsync()
    {
        if (!Settings.Unreadable)
        {
            return;
        }

        await Ui.ChoiceAsync(
            "Your settings couldn't be read",
            $"VR Table Manager couldn't read {Settings.SourcePath ?? AppSettings.DefaultPath} (another program may have it open, "
                + "or Windows won't let this app read it). It's running on default settings, and changes you make won't be saved "
                + "until the file can be read again, so your own settings aren't written over. Close the other program, or check "
                + "the file's permissions, then restart VR Table Manager.",
            ["OK"]
        );
    }

    bool _offeredResume;

    /// <summary>The catalog loaded (at startup, or REFRESH): what needs it runs now.</summary>
    internal void CatalogLoaded()
    {
        Library.ShowInstalled(); // My Tables' type and theme filters come from the catalog; also runs ApplyTableUpdates
        _ = Library.LinkImportedTablesAsync();
        _ = Updates.CheckUpdatesAsync(); // VPX / UEVR / BAM, in the background
        _ = Library.LoadRuleSheetsAsync();
        _ = Library.RefreshHighScoreMapsAsync();
        if (!_offeredResume)
        {
            _offeredResume = true;
            _ = StartupPromptsAsync();
        }
    }

    [RelayCommand]
    void OpenPage(string url) => Ui.OpenExternal(url);

    /// <summary>Opens a folder, making it first if it isn't there yet.</summary>
    internal void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        { /* open what's there */
        }
        Ui.OpenExternal(dir);
    }

    /// <summary>What Setup says about each game's install and the VR runtime.</summary>
    internal void RefreshSetupStatus()
    {
        Vpx.RefreshStatus();
        OtherGames.RefreshStatus();
        Headset.RefreshVrStatus();
    }

    [RelayCommand]
    void RecheckSetup()
    {
        if (string.IsNullOrWhiteSpace(OtherGames.FpDir) && FpSetup.AutoDetect() is { } fp)
        {
            OtherGames.FpDir = fp;
        }

        if (string.IsNullOrWhiteSpace(OtherGames.UevrDir) && PfxSetup.AutoDetectUevr() is { } uevr)
        {
            OtherGames.UevrDir = uevr;
        }

        Library.RefreshInstalled();
        RefreshSetupStatus();
    }

    /// <summary>After a restore: show the restored settings (the view models' copies would otherwise overwrite them).</summary>
    internal void ReloadSettings()
    {
        var s = AppSettings.Load();
        s.ManagerExe = Settings.ManagerExe; // this install's path, not the backed-up one
        Settings.Unreadable = s.Unreadable; // a restored file that reads fine can be saved again
        // Set first: each setting below saves Settings, and would otherwise write the old choice over the restored one.
        Settings.ControlsTarget = s.ControlsTarget;
        foreach (var c in _children)
        {
            c.LoadSettings(s);
        }

        Controls.RefreshControls(); // the restored choice, and the keys in the restored VPX settings
        Graphics.LoadGraphics();
        Library.RefreshInstalled();
        RefreshSetupStatus();
    }

    // ---- Startup prompts: a new app version, then downloads left from last time ----

    /// <summary>One dialog at a time: a new app version first (updating closes the app), then unfinished downloads.</summary>
    async Task StartupPromptsAsync()
    {
        try
        {
            await Updates.StartupAppUpdateAsync();
        }
        catch
        { /* never block the resume offer */
        }
        if (!Updates.IsUpdatingApp)
        {
            await OfferResumeAsync();
        }
    }

    /// <summary>Downloads still in the saved queue are offered again after the catalog loads.</summary>
    async Task OfferResumeAsync()
    {
        var items = Downloads.LeftFromLastTime;
        if (items.Count == 0)
        {
            return;
        }

        var list =
            string.Join("\n", items.Take(8).Select(i => "• " + i.Title))
            + (items.Count > 8 ? $"\n• …and {items.Count - 8} more" : "");
        var pick = await Ui.ChoiceAsync(
            items.Count == 1 ? "Resume 1 download?" : $"Resume {items.Count} downloads?",
            $"These hadn't finished when VR Table Manager last closed:\n\n{list}\n\nThey'll start again from the beginning.",
            ["RESUME", "DISCARD"]
        );
        // Resumed jobs add themselves back. Only last time's entries go: ones for downloads started since stay.
        foreach (var i in items)
        {
            Downloads.Pending.Remove(i.Id);
        }
        if (pick != 0)
        {
            return;
        }

        var installed = TableInstaller.Scan(Settings);
        InstalledTable? Find(string? path) =>
            path == null
                ? null
                : installed.FirstOrDefault(t =>
                    string.Equals(
                        Path.GetFullPath(t.TablePath),
                        Path.GetFullPath(path),
                        StringComparison.OrdinalIgnoreCase
                    )
                );
        var skipped = new List<string>();
        foreach (var item in items)
        {
            if (item.Kind == PendingDownload.RomKind)
            {
                if (Find(item.TablePath) is { } t)
                {
                    _ = Roms.GetRomAsync(t);
                }
                else
                {
                    skipped.Add(item.Title);
                }

                continue;
            }
            if (PendingDownload.AddonKindOf(item.Kind) != null)
            {
                Addons.ResumeAddon(item, Find, skipped);
                continue;
            }
            var game = Catalog.AllGames.FirstOrDefault(g => g.Id == item.GameId);
            var tf = game?.TableFiles?.FirstOrDefault(f => f.Id == item.TableFileId);
            if (game == null || tf == null)
            {
                skipped.Add(item.Title);
                continue;
            }
            var replacing = Find(item.TablePath);
            _ = Catalog.InstallAsync(
                game,
                tf,
                replacing is { IsIncomplete: false } ? replacing : null
            );
        }
        if (skipped.Count > 0)
        {
            Catalog.CatalogStatus =
                $"Couldn't resume {string.Join(", ", skipped)}: no longer in the catalog or installed.";
        }
    }

    // ---- Closing ----

    /// <summary>
    /// Called by the window when it's really closing. Downloads that end because of the shutdown
    /// keep their saved-queue entries, so they're offered again at the next launch.
    /// </summary>
    public void PrepareToClose() => Downloads.PrepareToClose();

    public ValueTask DisposeAsync() => Downloads.DisposeAsync();
}
