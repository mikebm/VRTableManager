namespace VrTableManager.Models;

/// <summary>
/// Written as vrtm.json into every table folder this app installs, so it can
/// match installed tables back to VPS entries and check for updates.
/// </summary>
public sealed class TableManifest
{
    public const string Vpx = "VPX";
    public const string FuturePinball = "FP";
    public const string PinballFx = "PFX";
    public const string PinballM = "PM";

    /// <summary>"VPX" or "FP".</summary>
    public string Engine { get; set; } = Vpx;
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string? Manufacturer { get; set; }
    public int? Year { get; set; }
    public string TableFileId { get; set; } = "";
    public string? Version { get; set; }
    public List<string> Authors { get; set; } = [];
    public List<string> Features { get; set; } = [];
    public string? SourceUrl { get; set; }
    public string? ImgUrl { get; set; }
    /// <summary>File name (relative to the table folder) that "Play" launches.</summary>
    public string VpxFile { get; set; } = "";
    /// <summary>ROM names the table expects in pinmame/roms (from VPS).</summary>
    public List<string> RomNames { get; set; } = [];
    public DateTime InstalledAt { get; set; }
    /// <summary>VPS UpdatedAt (epoch ms) of the table file when it was installed; used for update checks.</summary>
    public long? SourceUpdatedAt { get; set; }
}
