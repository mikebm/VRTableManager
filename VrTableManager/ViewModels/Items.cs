using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

public sealed class GameItem(VpsGame game, bool installed)
{
    public VpsGame Game { get; } = game;
    public string Title => Game.Name;
    public string? ImgUrl =>
        Game.ImgUrl ?? Game.TableFiles?.FirstOrDefault(t => t.ImgUrl != null)?.ImgUrl;
    public bool IsInstalled { get; } = installed;
    public int VrCount =>
        Game.TableFiles?.Count(t => VpsCatalog.IsVpx(t) && VpsCatalog.IsVrReady(t)) ?? 0;
    public bool HasFp => Game.TableFiles?.Any(IsFp) == true;
    public bool IsVr => VrCount > 0;
    public bool HasImage => !string.IsNullOrEmpty(ImgUrl);

    public string MakerYear =>
        string.Join(
            " · ",
            new[] { Game.Manufacturer, Game.Year?.ToString() }.Where(s =>
                !string.IsNullOrWhiteSpace(s)
            )
        );

    /// <summary>"JPSalas +4", like the reference catalog cards.</summary>
    public string AuthorsShort
    {
        get
        {
            var authors =
                Game.TableFiles?.SelectMany(t => t.Authors ?? []).Distinct().ToList() ?? [];
            return authors.Count switch
            {
                0 => "",
                1 => authors[0],
                _ => $"{authors[0]} +{authors.Count - 1}",
            };
        }
    }

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            var maker = string.Join(
                " ",
                new[] { Game.Manufacturer, Game.Year?.ToString() }.Where(s =>
                    !string.IsNullOrWhiteSpace(s)
                )
            );
            if (maker != "")
            {
                parts.Add(maker);
            }

            if (VrCount > 0)
            {
                parts.Add(VrCount == 1 ? "1 VR table" : $"{VrCount} VR tables");
            }

            if (HasFp)
            {
                parts.Add("Future Pinball");
            }

            if (IsInstalled)
            {
                parts.Add("✓ installed");
            }

            return string.Join(" · ", parts);
        }
    }

    public static bool IsFp(VpsTableFile t) =>
        string.Equals(t.TableFormat, "FP", StringComparison.OrdinalIgnoreCase);
}

public sealed class TableVersionItem
{
    public TableVersionItem(VpsGame game, VpsTableFile file, IAsyncRelayCommand install)
    {
        Game = game;
        File = file;
        Install = install;
    }

    public VpsGame Game { get; }
    public VpsTableFile File { get; }
    public IAsyncRelayCommand Install { get; }

    public string Url => File.Urls?.FirstOrDefault(u => !u.Broken)?.Url ?? "";
    public bool HasUrl => Url != "";
    public string Engine => GameItem.IsFp(File) ? "Future Pinball" : "VPX";
    public bool IsVr =>
        VpsCatalog.IsVpx(File) ? VpsCatalog.IsVrReady(File) : VpsCatalog.HasFeature(File, "VR");

    public string Heading
    {
        get
        {
            var authors = File.Authors is { Count: > 0 } a
                ? string.Join(", ", a)
                : "Unknown author";
            var edition = string.IsNullOrWhiteSpace(File.Edition) ? "" : $" — {File.Edition}";
            return $"{authors}{edition}";
        }
    }

    public string Details
    {
        get
        {
            var host = Uri.TryCreate(Url, UriKind.Absolute, out var u)
                ? u.Host.Replace("www.", "")
                : "no link";
            var updated = File.UpdatedAt is > 0
                ? DateTimeOffset
                    .FromUnixTimeMilliseconds(File.UpdatedAt.Value)
                    .ToString("yyyy-MM-dd")
                : "";
            return $"{Engine} · v{File.Version ?? "?"} · {host}{(updated == "" ? "" : " · " + updated)}";
        }
    }

    public string Features => File.Features is { Count: > 0 } f ? string.Join("  ·  ", f) : "";
    public IReadOnlyList<string> FeatureList => File.Features ?? [];
    public string? Comment => File.Comment;
}

public sealed partial class InstalledItem : ObservableObject
{
    public InstalledItem(InstalledTable table, MainViewModel? owner = null)
    {
        Table = table;
        Owner = owner;
        ScaleChoices =
        [
            new(this, "Same as all tables", null),
            .. new[] { 0.6, 0.7, 0.8, 0.9, 1.1, 1.2, 1.3 }.Select(s => new TableScaleChoice(
                this,
                $"{s * 100:0}%",
                s
            )),
        ];
        Pup = new AddonSlot(this, AddonKind.Pup);
        Colour = new AddonSlot(this, AddonKind.AltColor);
        Sound = new AddonSlot(this, AddonKind.AltSound);
    }

    public InstalledTable Table { get; }

    /// <summary>For the card's menu: flyouts sit outside the window's visual tree, so they bind commands through this.</summary>
    public MainViewModel? Owner { get; }
    public bool IsVpx => Table.IsVpx;

    /// <summary>A VPX table in its own folder: a lobby video is kept per folder, and loose .vpx files share one.</summary>
    public bool CanHaveLobbyVideo => Table.HasOwnFolder;

    // ---- Disk use and per-table render scale ----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DiskInfo))]
    public partial long SizeBytes { get; set; }

    /// <summary>This table's own render scale (its .ini), or null for the global one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DiskInfo), nameof(ScaleMenuHeader))]
    public partial double? TableScale { get; set; }
    public IReadOnlyList<TableScaleChoice> ScaleChoices { get; }
    public string ScaleMenuHeader =>
        TableScale is { } s
            ? $"Render scale: {s * 100:0}% (this table)"
            : "Render scale: same as all tables";

    /// <summary>The height this table keeps for itself in VR (cm, its .ini's TableZ), or null (VrFit.KeepTableHeight).</summary>
    [
        ObservableProperty,
        NotifyPropertyChangedFor(nameof(DiskInfo), nameof(HasVrHeight), nameof(VrHeightMenuHeader))
    ]
    public partial double? TableVrHeight { get; set; }
    public bool HasVrHeight => TableVrHeight != null;
    public string VrHeightMenuHeader =>
        TableVrHeight is { } h ? $"Clear its VR height ({VrHeightText(h)})" : "";

    static string VrHeightText(double cm) =>
        cm >= 0 ? $"{Math.Round(cm):0} cm higher" : $"{Math.Round(-cm):0} cm lower";

    /// <summary>"GRAND CHAMPION  ABC  1,234,567,890" from the table's NVRAM, or "" (no ROM, never played, no map).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHighScore))]
    public partial string TopScore { get; set; } = "";

    /// <summary>The rest of the table (ranks 1–4), for the tooltip.</summary>
    [ObservableProperty]
    public partial string MoreScores { get; set; } = "";
    public bool HasHighScore => TopScore != "";

    /// <summary>"1.2 GB · renders at 80%"</summary>
    public string DiskInfo =>
        string.Join(
            " · ",
            new[]
            {
                SizeBytes > 0 ? DiskUsage.Format(SizeBytes) : null,
                TableScale is { } s ? $"renders at {s * 100:0}%" : null,
                TableVrHeight is { } h ? $"sits {VrHeightText(h)} in VR" : null,
            }.Where(x => x != null)
        );
    public string Title => Table.Manifest.GameName;

    /// <summary>The card picture: yours (<see cref="UserArtwork"/>) when you chose one, else the catalog's.</summary>
    public string? ImgUrl => UserCard ?? Table.Manifest.ImgUrl;
    public bool HasImage => !string.IsNullOrEmpty(ImgUrl);

    // Your own pictures (art keys, null = none); set by the view model.
    [
        ObservableProperty,
        NotifyPropertyChangedFor(nameof(ImgUrl), nameof(HasImage), nameof(HasUserArtwork))
    ]
    public partial string? UserCard { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUserArtwork))]
    public partial string? UserPlayfield { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUserArtwork))]
    public partial string? UserBackglass { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUserArtwork))]
    public partial string? UserWheel { get; set; }
    public bool HasUserArtwork =>
        UserCard != null || UserPlayfield != null || UserBackglass != null || UserWheel != null;

    public void RefreshUserArtwork()
    {
        UserCard = UserArtwork.Key(Table, UserArtwork.Kind.Card);
        UserPlayfield = UserArtwork.Key(Table, UserArtwork.Kind.Playfield);
        UserBackglass = UserArtwork.Key(Table, UserArtwork.Kind.Backglass);
        UserWheel = UserArtwork.Key(Table, UserArtwork.Kind.Wheel);
    }

    public string EngineBadge =>
        Table.Manifest.Engine switch
        {
            TableManifest.FuturePinball => "FUTURE PINBALL",
            TableManifest.PinballFx => "PINBALL FX",
            TableManifest.PinballM => "PINBALL M",
            _ => "VPX",
        };
    public bool CanPlayDesktop =>
        Engines.For(Table.Manifest.Engine).SupportsDesktopMode && !Table.IsIncomplete;
    public bool CanRemove => Engines.For(Table.Manifest.Engine).ManagesFiles;

    /// <summary>The page the table was installed from (the author's post), when it was a web page.</summary>
    public string? SourcePage =>
        Uri.TryCreate(Table.Manifest.SourceUrl, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.ToString()
            : null;
    public bool HasSourcePage => SourcePage != null;

    /// <summary>"Open its page on VPUniverse": names the site, since only some of them take ratings.</summary>
    public string SourcePageMenuHeader => $"Open its page on {SiteName(SourcePage)}";

    static string SiteName(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return "the web";
        }

        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? u.Host[4..]
            : u.Host;
        return host.ToLowerInvariant() switch
        {
            "vpuniverse.com" => "VPUniverse",
            "vpforums.org" => "VPForums",
            "pinballnirvana.com" => "Pinball Nirvana",
            "github.com" => "GitHub",
            _ => host,
        };
    }

    public string Subtitle
    {
        get
        {
            var engine = Engines.For(Table.Manifest.Engine).DisplayName;
            var version = Table.Manifest.Version is { } v ? $" · v{v}" : "";
            return $"{engine}{version} · {Table.Manifest.VpxFile}";
        }
    }

    public string RomStatus =>
        Table.MissingRoms.Count switch
        {
            0 => Table.Manifest.RomNames.Count > 0 ? "ROM ready" : "",
            1 => $"Needs ROM {Table.MissingRoms[0]}.zip",
            _ => "Needs one of these ROMs: "
                + string.Join(", ", Table.MissingRoms.Select(r => r + ".zip")),
        };

    /// <summary>The catalog's download for the missing ROM (set by the view model), if it has one.</summary>
    [
        ObservableProperty,
        NotifyPropertyChangedFor(
            nameof(CanGetRom),
            nameof(CanGetAllRoms),
            nameof(RomUnavailable),
            nameof(RomUnavailableText)
        )
    ]
    public partial VpsFile? RomSource { get; set; }

    /// <summary>A Stern machine: Get ROM tries Stern's own free game code first (set by the view model).</summary>
    [
        ObservableProperty,
        NotifyPropertyChangedFor(
            nameof(CanGetRom),
            nameof(RomUnavailable),
            nameof(RomUnavailableText)
        )
    ]
    public partial bool HasSternSource { get; set; }

    /// <summary>Needs a ROM nobody we know of offers.</summary>
    public bool RomUnavailable => NeedsRom && RomSource == null && !HasSternSource;

    public string RomUnavailableText =>
        !RomUnavailable
            ? ""
            : $"The catalog has no download for {string.Join(" / ", Table.MissingRoms.Select(r => r + ".zip"))}. "
                + "Some ROMs (often modified ones ending in c, like …hc) are made by players rather than published. "
                + "Search VPUniverse for it, or use Add ROM if you have the file.";

    /// <summary>ROM missing and the catalog has a download for it.</summary>
    public bool CanGetRom => NeedsRom && (RomSource != null || HasSternSource);

    /// <summary>ROM present; offer to fetch every version from its ROM page (clones need their parent).</summary>
    /// <remarks>
    /// Once PinMAME has checked the set (<see cref="RomRequirements"/>), a complete set needs nothing
    /// more and a missing parent shows as Needs ROM, so this is only for tables it couldn't check.
    /// </remarks>
    public bool CanGetAllRoms =>
        !NeedsRom && RomSource != null && Table.Manifest.RomCheck < RomRequirements.Version;

    public bool NeedsRom => Table.MissingRoms.Count > 0;

    /// <summary>Where the catalog has attract audio for this table (set by the view model); empty when it has none.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGetAttractAudio))]
    public partial IReadOnlyList<MediaPackAudio.Source> AttractAudioSources { get; set; } = [];

    public bool CanGetAttractAudio => Table.IsVpx && AttractAudioSources.Count > 0;

    partial void OnAttractAudioSourcesChanged(IReadOnlyList<MediaPackAudio.Source> value) =>
        HasMediaPack = value.Any(s => !s.IsDirect);

    /// <summary>A music\attract.* is in the table folder (the VR lobby plays it when the cabinet is selected).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AttractAudioMenuHeader))]
    public partial bool HasAttractAudio { get; set; }

    public string AttractAudioMenuHeader =>
        HasAttractAudio
            ? "Replace attract audio (from media pack)"
            : "Get attract audio for the VR lobby";

    /// <summary>The catalog has a media pack for this table (its playfield video is the lobby video's first choice).</summary>
    [
        ObservableProperty,
        NotifyPropertyChangedFor(nameof(LobbyVideoMenuHeader), nameof(CanGetLoadingVideo))
    ]
    public partial bool HasMediaPack { get; set; }

    /// <summary>A loading video can be kept in this table's own folder (VPX tables in a folder of their own).</summary>
    public bool CanHaveLoadingVideo => Table.HasOwnFolder;

    /// <summary>medias\loading-video is in the table folder (the VR lobby plays it while the table starts).</summary>
    [
        ObservableProperty,
        NotifyPropertyChangedFor(nameof(LoadingVideoMenuHeader), nameof(OwnLoadingVideoMenuHeader))
    ]
    public partial bool HasLoadingVideo { get; set; }

    public bool CanGetLoadingVideo => CanHaveLoadingVideo && HasMediaPack;

    public string LoadingVideoMenuHeader =>
        HasLoadingVideo
            ? "Replace loading video (media pack)"
            : "Get a loading video for the VR lobby (media pack)";

    public string OwnLoadingVideoMenuHeader =>
        HasLoadingVideo ? "Use my own loading video instead…" : "Use my own loading video…";

    /// <summary>Where the catalog has wheel art for this table: its wheel art downloads, then its media packs (set by the view model).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGetWheelArt))]
    public partial IReadOnlyList<(
        MediaPackAudio.Source Source,
        bool WheelPack
    )> WheelArtSources { get; set; } = [];

    public bool CanGetWheelArt => Table.HasOwnFolder && WheelArtSources.Count > 0;

    /// <summary>A medias\(Wheel) picture is in the table folder (the VR lobby shows it over the cabinet).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WheelArtMenuHeader))]
    public partial bool HasWheelArt { get; set; }

    public string WheelArtMenuHeader =>
        HasWheelArt ? "Replace wheel art (from the catalog)" : "Get wheel art for the VR lobby";

    /// <summary>medias\lobby-video is in the table folder (the VR lobby plays it on the selected cabinet's playfield).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LobbyVideoMenuHeader))]
    public partial bool HasLobbyVideo { get; set; }

    /// <summary>Cards smaller than Large: only Play and MORE show, everything else is in MORE's menu (set by the view model).</summary>
    [ObservableProperty]
    public partial bool Compact { get; set; }

    /// <summary>The table's PuP DMD was fixed for VPX 10.8.1 and its original is kept (<see cref="VrDmdFix"/>).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApplyDmdFix))]
    public partial bool HasDmdFix { get; set; }

    /// <summary>The user undid the fix or said not to ask: the prompt is off, so MORE offers the fix instead.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApplyDmdFix))]
    public partial bool DmdFixDeclined { get; set; }

    public bool CanApplyDmdFix => DmdFixDeclined && !HasDmdFix;

    public string LobbyVideoMenuHeader =>
        (HasLobbyVideo ? "Replace lobby video" : "Get a playfield video for the VR lobby")
        + (HasMediaPack ? " (media pack)" : " (record in VPX)");

    // ---- Add-ons: PuP pack, DMD colours, AltSound (each in its own folder of the table folder) ----

    public AddonSlot Pup { get; }
    public AddonSlot Colour { get; }
    public AddonSlot Sound { get; }
    public IReadOnlyList<AddonSlot> Addons => [Pup, Colour, Sound];

    /// <summary>Where to read the rules (set by the view model): The Pinball Primer, then instruction cards.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRules))]
    public partial IReadOnlyList<RuleChoice> Rules { get; set; } = [];

    public bool HasRules => Rules.Count > 0;

    /// <summary>Its install was cut off (app closed mid-copy): offer Reinstall instead of Play.</summary>
    public bool IsIncomplete => Table.IsIncomplete;

    /// <summary>A ROM fetch, update or reinstall for this table is running: its buttons are disabled.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }
    public bool CanPlay => !IsIncomplete;

    // ---- Favourites and play history (shared with the VR lobby through PlayStats) ----

    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayInfo))]
    public partial int Plays { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayInfo))]
    public partial DateTime? LastPlayed { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayInfo))]
    public partial double MinutesPlayed { get; set; }

    public void ApplyStats(PlayStats stats)
    {
        IsFavourite = stats.IsFavourite(Table.TablePath);
        Plays = stats.PlaysOf(Table.TablePath);
        LastPlayed = stats.LastPlayedOf(Table.TablePath);
        MinutesPlayed = stats.MinutesOf(Table.TablePath);
    }

    /// <summary>"Played 12× · 3 h 20 m · 2 days ago"</summary>
    public string PlayInfo
    {
        get
        {
            if (Plays == 0)
            {
                return LastPlayed is { } picked ? $"Picked {Ago(picked)}" : "Not played yet";
            }

            var parts = new List<string> { Plays == 1 ? "Played once" : $"Played {Plays}×" };
            if (MinutesPlayed >= 1)
            {
                parts.Add(
                    MinutesPlayed >= 60
                        ? $"{(int)(MinutesPlayed / 60)} h {(int)(MinutesPlayed % 60)} m"
                        : $"{(int)MinutesPlayed} m"
                );
            }

            if (LastPlayed is { } last)
            {
                parts.Add(Ago(last));
            }

            return string.Join(" · ", parts);
        }
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 2 ? "just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : d.TotalDays < 2 ? "yesterday"
            : d.TotalDays < 30 ? $"{(int)d.TotalDays} days ago"
            : t.ToString("d MMM yyyy");
    }

    /// <summary>Set by the view model when VPS lists a newer version of this table.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateLabel))]
    public partial TableUpdate? Update { get; set; }

    public bool HasUpdate => Update != null;
    public string UpdateLabel => Update is { } u ? $"New version: {u.Label}" : "";
}

public sealed partial class JobItem : ObservableObject
{
    public JobItem(string title) => Title = title;

    public string Title { get; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Queued";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; } = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRunning))]
    public partial bool IsDone { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRunning))]
    public partial bool IsError { get; set; }

    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Short unique id (names this job's temporary download folder).</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];

    bool IsFinished => IsDone || IsError;

    /// <summary>
    /// Thread-safe status update (browser and download callbacks run off the UI thread). Applied at
    /// once on the UI thread, so a queued update can't land after the final "Installed" message;
    /// ignored once the job has finished.
    /// </summary>
    public void Report(string status) =>
        OnUi(() =>
        {
            if (!IsFinished)
            {
                Status = status;
            }
        });

    public IProgress<double> ProgressReporter =>
        new Progress<double>(p =>
            OnUi(() =>
            {
                if (IsFinished)
                {
                    return;
                }

                IsIndeterminate = false;
                Progress = p * 100;
            })
        );

    /// <summary>Marks the job finished: final message, progress bar full and stopped.</summary>
    public void Finish(string status, bool error = false) =>
        OnUi(() =>
        {
            Status = status;
            IsIndeterminate = false;
            Progress = 100;
            if (error)
            {
                IsError = true;
            }
            else
            {
                IsDone = true;
            }

            if (!error)
            {
                Succeeded?.Invoke(this);
            }
        });

    /// <summary>Raised on the UI thread when the job finishes without an error.</summary>
    public event Action<JobItem>? Succeeded;

    readonly List<string> _sourceFiles = [];

    /// <summary>A file this job took from outside its own folder (the copy your browser saved).</summary>
    public void AddSourceFile(string path)
    {
        lock (_sourceFiles)
        {
            _sourceFiles.Add(path);
        }
    }

    /// <summary>The files from <see cref="AddSourceFile"/>, handed over once.</summary>
    public List<string> TakeSourceFiles()
    {
        lock (_sourceFiles)
        {
            var l = _sourceFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _sourceFiles.Clear();
            return l;
        }
    }

    /// <summary>Still working (not done, failed or cancelled): CANCEL shows; once finished, ✕ takes it off the list.</summary>
    public bool IsRunning => !IsDone && !IsError;

    static void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    }

    [RelayCommand]
    void Cancel() => Cts.Cancel();

    // ---- Waiting for you to download in your browser (AssistedDownloader) ----

    /// <summary>The file page you're downloading from in your browser; null when not waiting on you.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingForUser))]
    public partial string? WaitingUrl { get; set; }

    /// <summary>The page has several files to take (a ROM page): show DONE.</summary>
    [ObservableProperty]
    public partial bool WaitingForMany { get; set; }
    public bool IsWaitingForUser => WaitingUrl != null;

    /// <summary>Names the downloaded file may carry (the game, its ROMs, "pup"): tells files apart when several jobs wait.</summary>
    public IReadOnlyList<string> FileNames { get; init; } = [];

    /// <summary>The file types this job takes (".vpx", ".zip" …); empty for any.</summary>
    public IReadOnlyList<string> FileTypes { get; init; } = [];

    /// <summary>Only take a file whose name carries one of <see cref="FileNames"/> (ROMs: any other set is another table's).</summary>
    public bool NamesMustMatch { get; init; }

    public sealed record Handoff(string? File, bool Done);

    readonly object _handoffLock = new();
    Handoff? _handoff;
    TaskCompletionSource _handoffArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when you point at a file or press DONE.</summary>
    public Task HandoffArrived
    {
        get
        {
            lock (_handoffLock)
            {
                return _handoffArrived.Task;
            }
        }
    }

    public void BeginWaiting(string url, bool many) =>
        OnUi(() =>
        {
            WaitingUrl = url;
            WaitingForMany = many;
        });

    public void EndWaiting() =>
        OnUi(() =>
        {
            WaitingUrl = null;
            WaitingForMany = false;
        });

    /// <summary>A file you saved somewhere other than the Downloads folder.</summary>
    public void HandOff(string file) => SetHandoff(new Handoff(file, false));

    public Handoff? TakeHandoff()
    {
        lock (_handoffLock)
        {
            var h = _handoff;
            if (h == null)
            {
                return null;
            }

            _handoff = null;
            _handoffArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return h;
        }
    }

    void SetHandoff(Handoff h)
    {
        lock (_handoffLock)
        {
            _handoff = h;
            _handoffArrived.TrySetResult();
        }
    }

    [RelayCommand]
    void OpenWaitingPage()
    {
        if (WaitingUrl is { } url)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
                );
            }
            catch { }
        }
    }

    [RelayCommand]
    void DoneWaiting() => SetHandoff(new Handoff(null, true));
}

/// <summary>One kind of add-on on a table card (PuP pack, DMD colours or AltSound): what the catalog has, what's installed, updates.</summary>
public sealed partial class AddonSlot : ObservableObject
{
    public AddonSlot(InstalledItem item, AddonKind kind)
    {
        Item = item;
        Kind = kind;
    }

    public InstalledItem Item { get; }
    public AddonKind Kind { get; }

    public string Name =>
        Kind switch
        {
            AddonKind.Pup => "PuP pack",
            AddonKind.AltColor => "DMD colours",
            _ => "AltSound",
        };
    public string ButtonText =>
        Kind switch
        {
            AddonKind.Pup => "PUP ▾",
            AddonKind.AltColor => "COLOUR DMD ▾",
            _ => "ALTSOUND ▾",
        };
    public string Tip =>
        Kind switch
        {
            AddonKind.Pup =>
                "PinUP Player pack: videos for the cabinet's backglass and DMD, from the catalog",
            AddonKind.AltColor =>
                "Colour for the DMD (Serum or Pin2DMD colorization), from the catalog",
            _ => "Replacement sounds and music for the ROM (AltSound / G-Sound), from the catalog",
        };

    /// <summary>The catalog's files of this kind for the table (set by the view model); empty when it has none.</summary>
    [
        ObservableProperty,
        NotifyPropertyChangedFor(nameof(HasChoices), nameof(ShowButton), nameof(MenuHeader))
    ]
    public partial IReadOnlyList<AddonChoice> Choices { get; set; } = [];

    /// <summary>What's installed (set by the view model).</summary>
    [
        ObservableProperty,
        NotifyPropertyChangedFor(
            nameof(IsInstalled),
            nameof(ShowButton),
            nameof(MenuHeader),
            nameof(FileMenuHeader),
            nameof(Status)
        )
    ]
    public partial AddonState State { get; set; } = AddonState.None;

    /// <summary>A newer catalog version of the installed one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUpdate), nameof(Status))]
    public partial AddonUpdate? Update { get; set; }

    /// <summary>A pack you already downloaded can be installed from its file (PuP, for a table in its own folder).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowButton))]
    public partial bool CanAddFile { get; set; }

    /// <summary>The table script uses this kind of add-on (its PuP pack), so the menu shows even with nothing in the catalog.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowButton))]
    public partial bool ScriptWantsIt { get; set; }

    public bool HasChoices => Choices.Count > 0;
    public bool IsInstalled => State.IsInstalled;
    public bool HasUpdate => Update != null;
    public bool ShowButton => HasChoices || IsInstalled || (CanAddFile && ScriptWantsIt);
    public string FileMenuHeader =>
        IsInstalled
            ? $"Replace with a {Name} file you downloaded…"
            : $"Install a {Name} file you downloaded…";
    public string MenuHeader =>
        IsInstalled ? $"Replace {Name}"
        : Choices.Count == 1 ? $"Get {Name}"
        : $"Get {Name} ({Choices.Count} in the catalog)";

    /// <summary>"DMD colours v2.0 installed (Serum)" / "AltSound: new version v1.0 → v1.1" / "" (none).</summary>
    public string Status =>
        Update is { } u ? $"{Name}: new version {u.Label}"
        : State.Tracked is { } t
            ? $"{Name}{(t.Version is { } v ? " v" + v.Trim().TrimStart('v', 'V') : "")} installed{Format(t.Location)}"
        : State.IsInstalled ? $"{Name} installed ({string.Join(", ", State.Folders)})"
        : "";

    static string Format(string? location) =>
        location switch
        {
            AltPacks.SerumFolder => " (Serum)",
            AltPacks.VniFolder => " (Pin2DMD)",
            _ => "",
        };
}

/// <summary>A rules page in a table card's RULES menu.</summary>
public sealed record RuleChoice(InstalledItem Item, string Label, string Url);
