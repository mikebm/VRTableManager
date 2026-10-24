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
    public string? ImgUrl => Game.ImgUrl ?? Game.TableFiles?.FirstOrDefault(t => t.ImgUrl != null)?.ImgUrl;
    public bool IsInstalled { get; } = installed;
    public int VrCount => Game.TableFiles?.Count(t => VpsCatalog.IsVpx(t) && VpsCatalog.IsVrReady(t)) ?? 0;
    public bool HasFp => Game.TableFiles?.Any(IsFp) == true;
    public bool IsVr => VrCount > 0;
    public bool HasImage => !string.IsNullOrEmpty(ImgUrl);

    public string MakerYear =>
        string.Join(" · ", new[] { Game.Manufacturer, Game.Year?.ToString() }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>"JPSalas +4", like the reference catalog cards.</summary>
    public string AuthorsShort
    {
        get
        {
            var authors = Game.TableFiles?.SelectMany(t => t.Authors ?? []).Distinct().ToList() ?? [];
            return authors.Count switch { 0 => "", 1 => authors[0], _ => $"{authors[0]} +{authors.Count - 1}" };
        }
    }

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            var maker = string.Join(" ", new[] { Game.Manufacturer, Game.Year?.ToString() }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (maker != "") parts.Add(maker);
            if (VrCount > 0) parts.Add(VrCount == 1 ? "1 VR table" : $"{VrCount} VR tables");
            if (HasFp) parts.Add("Future Pinball");
            if (IsInstalled) parts.Add("✓ installed");
            return string.Join(" · ", parts);
        }
    }

    public static bool IsFp(VpsTableFile t) => string.Equals(t.TableFormat, "FP", StringComparison.OrdinalIgnoreCase);
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
    public bool IsVr => VpsCatalog.IsVpx(File) ? VpsCatalog.IsVrReady(File) : VpsCatalog.HasFeature(File, "VR");

    public string Heading
    {
        get
        {
            var authors = File.Authors is { Count: > 0 } a ? string.Join(", ", a) : "Unknown author";
            var edition = string.IsNullOrWhiteSpace(File.Edition) ? "" : $" — {File.Edition}";
            return $"{authors}{edition}";
        }
    }

    public string Details
    {
        get
        {
            var host = Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "no link";
            var updated = File.UpdatedAt is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(File.UpdatedAt.Value).ToString("yyyy-MM-dd") : "";
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
        ScaleChoices = [new(this, "Same as all tables", null), .. new[] { 0.6, 0.7, 0.8, 0.9, 1.1, 1.2, 1.3 }.Select(s => new TableScaleChoice(this, $"{s * 100:0}%", s))];
    }

    public InstalledTable Table { get; }
    /// <summary>For the card's menu: flyouts sit outside the window's visual tree, so they bind commands through this.</summary>
    public MainViewModel? Owner { get; }
    public bool IsVpx => Table.IsVpx;

    // ---- Disk use and per-table render scale ----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DiskInfo))] public partial long SizeBytes { get; set; }
    /// <summary>This table's own render scale (its .ini), or null for the global one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DiskInfo), nameof(ScaleMenuHeader))] public partial double? TableScale { get; set; }
    public IReadOnlyList<TableScaleChoice> ScaleChoices { get; }
    public string ScaleMenuHeader => TableScale is { } s ? $"Render scale: {s * 100:0}% (this table)" : "Render scale: same as all tables";

    /// <summary>"GRAND CHAMPION  ABC  1,234,567,890" from the table's NVRAM, or "" (no ROM, never played, no map).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHighScore))] public partial string TopScore { get; set; } = "";
    /// <summary>The rest of the table (ranks 1–4), for the tooltip.</summary>
    [ObservableProperty] public partial string MoreScores { get; set; } = "";
    public bool HasHighScore => TopScore != "";

    /// <summary>"1.2 GB · renders at 80%"</summary>
    public string DiskInfo => string.Join(" · ", new[]
    {
        SizeBytes > 0 ? DiskUsage.Format(SizeBytes) : null,
        TableScale is { } s ? $"renders at {s * 100:0}%" : null,
    }.Where(x => x != null));
    public string Title => Table.Manifest.GameName;
    public string? ImgUrl => Table.Manifest.ImgUrl;
    public bool HasImage => !string.IsNullOrEmpty(ImgUrl);
    public string EngineBadge => Table.Manifest.Engine switch
    {
        TableManifest.FuturePinball => "FUTURE PINBALL",
        TableManifest.PinballFx => "PINBALL FX",
        TableManifest.PinballM => "PINBALL M",
        _ => "VPX",
    };
    public bool CanPlayDesktop => Engines.For(Table.Manifest.Engine).SupportsDesktopMode && !Table.IsIncomplete;
    public bool CanRemove => Engines.For(Table.Manifest.Engine).ManagesFiles;

    public string Subtitle
    {
        get
        {
            var engine = Engines.For(Table.Manifest.Engine).DisplayName;
            var version = Table.Manifest.Version is { } v ? $" · v{v}" : "";
            return $"{engine}{version} · {Table.Manifest.VpxFile}";
        }
    }

    public string RomStatus => Table.MissingRoms.Count switch
    {
        0 => Table.Manifest.RomNames.Count > 0 ? "ROM ready" : "",
        1 => $"Needs ROM {Table.MissingRoms[0]}.zip",
        _ => "Needs one of these ROMs: " + string.Join(", ", Table.MissingRoms.Select(r => r + ".zip")),
    };

    /// <summary>The catalog's download for the missing ROM (set by the view model), if it has one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGetRom), nameof(CanGetAllRoms), nameof(RomUnavailable), nameof(RomUnavailableText))]
    public partial VpsFile? RomSource { get; set; }

    /// <summary>Needs a ROM the catalog has no download for.</summary>
    public bool RomUnavailable => NeedsRom && RomSource == null;

    public string RomUnavailableText => !RomUnavailable ? "" :
        $"The catalog has no download for {string.Join(" / ", Table.MissingRoms.Select(r => r + ".zip"))}. " +
        "Some ROMs (often …h or …hc versions) are made by players from Stern's code rather than downloaded. " +
        "Search VPUniverse for it, or use Add ROM if you have the file.";

    /// <summary>ROM missing and the catalog has a download for it.</summary>
    public bool CanGetRom => NeedsRom && RomSource != null;

    /// <summary>ROM present; offer to fetch every version from its ROM page (clones need their parent).</summary>
    public bool CanGetAllRoms => !NeedsRom && RomSource != null;

    public bool NeedsRom => Table.MissingRoms.Count > 0;

    /// <summary>Its install was cut off (app closed mid-copy): offer Reinstall instead of Play.</summary>
    public bool IsIncomplete => Table.IsIncomplete;

    /// <summary>A ROM fetch, update or reinstall for this table is running: its buttons are disabled.</summary>
    [ObservableProperty] public partial bool IsBusy { get; set; }
    public bool CanPlay => !IsIncomplete;

    // ---- Favourites and play history (shared with the VR lobby through PlayStats) ----

    [ObservableProperty] public partial bool IsFavourite { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayInfo))] public partial int Plays { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayInfo))] public partial DateTime? LastPlayed { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayInfo))] public partial double MinutesPlayed { get; set; }

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
            if (Plays == 0) return LastPlayed is { } picked ? $"Picked {Ago(picked)}" : "Not played yet";
            var parts = new List<string> { Plays == 1 ? "Played once" : $"Played {Plays}×" };
            if (MinutesPlayed >= 1)
                parts.Add(MinutesPlayed >= 60 ? $"{(int)(MinutesPlayed / 60)} h {(int)(MinutesPlayed % 60)} m" : $"{(int)MinutesPlayed} m");
            if (LastPlayed is { } last) parts.Add(Ago(last));
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
    [ObservableProperty] public partial string Status { get; set; } = "Queued";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; } = true;
    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial bool IsError { get; set; }

    public CancellationTokenSource Cts { get; } = new();
    /// <summary>Short unique id (names this job's temporary download folder).</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];

    bool IsFinished => IsDone || IsError;

    /// <summary>
    /// Thread-safe status update (browser and download callbacks run off the UI thread). Applied at
    /// once on the UI thread, so a queued update can't land after the final "Installed" message;
    /// ignored once the job has finished.
    /// </summary>
    public void Report(string status) => OnUi(() => { if (!IsFinished) Status = status; });

    public IProgress<double> ProgressReporter => new Progress<double>(p => OnUi(() =>
    {
        if (IsFinished) return;
        IsIndeterminate = false;
        Progress = p * 100;
    }));

    /// <summary>Marks the job finished: final message, progress bar full and stopped.</summary>
    public void Finish(string status, bool error = false) => OnUi(() =>
    {
        Status = status;
        IsIndeterminate = false;
        Progress = 100;
        if (error) IsError = true; else IsDone = true;
    });

    static void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    [RelayCommand]
    void Cancel() => Cts.Cancel();
}
