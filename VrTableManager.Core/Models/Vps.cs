using System.Text.Json.Serialization;

namespace VrTableManager.Models;

// Shapes of the Virtual Pinball Spreadsheet database (vpsdb.json).
// Only the fields this app uses are mapped; everything else is ignored.

public sealed class VpsGame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Manufacturer { get; set; }
    public int? Year { get; set; }
    public string? Type { get; set; }
    public List<string>? Theme { get; set; }
    public List<string>? Designers { get; set; }
    public string? ImgUrl { get; set; }
    public long? UpdatedAt { get; set; }
    public List<VpsTableFile>? TableFiles { get; set; }
    public List<VpsFile>? RomFiles { get; set; }
    public List<VpsFile>? B2sFiles { get; set; }
    /// <summary>Front-end media packs (PinUP Popper / PinballX videos, art and table audio).</summary>
    public List<VpsFile>? MediaPackFiles { get; set; }
    /// <summary>Table audio: mostly direct .mp3 links (Dropbox), sometimes sound-replacement packs.</summary>
    public List<VpsFile>? SoundFiles { get; set; }

    [JsonIgnore]
    public string DisplayTitle => Year is > 0
        ? $"{Name} ({Manufacturer} {Year})"
        : string.IsNullOrWhiteSpace(Manufacturer) ? Name : $"{Name} ({Manufacturer})";
}

public class VpsFile
{
    public string Id { get; set; } = "";
    public string? Version { get; set; }
    public List<string>? Authors { get; set; }
    public List<VpsUrl>? Urls { get; set; }
    public List<string>? Features { get; set; }
    public string? Comment { get; set; }
    public string? ImgUrl { get; set; }
    public long? UpdatedAt { get; set; }
}

public sealed class VpsTableFile : VpsFile
{
    public string? TableFormat { get; set; }
    public string? Edition { get; set; }
}

public sealed class VpsUrl
{
    public string Url { get; set; } = "";
    public bool Broken { get; set; }
}
