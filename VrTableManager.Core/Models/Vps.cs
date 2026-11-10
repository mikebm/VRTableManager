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

    /// <summary>PinUP Player packs: videos for the backglass/DMD/topper, almost all on VPUniverse (sign-in).</summary>
    public List<VpsFile>? PupPackFiles { get; set; }

    /// <summary>DMD colorizations: Serum (.cRZ) or Pin2DMD (.pal/.vni, or an encrypted .pac).</summary>
    public List<VpsAltColorFile>? AltColorFiles { get; set; }

    /// <summary>Replacement sound packs for PinMAME (AltSound / G-Sound / legacy PinSound layout).</summary>
    public List<VpsFile>? AltSoundFiles { get; set; }

    /// <summary>Wheel art (the game's logo for a front end's selection wheel), as download pages (mostly VPUniverse).</summary>
    public List<VpsFile>? WheelArtFiles { get; set; }

    /// <summary>Instruction cards (images of the real machine's rule card), as VPUniverse pages.</summary>
    public List<VpsFile>? RuleFiles { get; set; }

    [JsonIgnore]
    public string DisplayTitle =>
        Year is > 0 ? $"{Name} ({Manufacturer} {Year})"
        : string.IsNullOrWhiteSpace(Manufacturer) ? Name
        : $"{Name} ({Manufacturer})";
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

public sealed class VpsAltColorFile : VpsFile
{
    /// <summary>Free text: "Serum", "Serum 2.0", "Pin2DMD", "Pin2DMD - PAC - 64 Colors", "PB Patch", …</summary>
    public string? Type { get; set; }

    /// <summary>The ROM it colours ("mm_109c").</summary>
    public string? Folder { get; set; }
    public string? FileName { get; set; }
}

public sealed class VpsUrl
{
    public string Url { get; set; } = "";
    public bool Broken { get; set; }
}
