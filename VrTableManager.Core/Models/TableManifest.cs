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

    /// <summary>
    /// The name the table had before you renamed it (the catalog's, or the file's), so it can go back; null when
    /// it wasn't renamed. An update keeps your name and puts the catalog's latest name here.
    /// </summary>
    public string? OriginalName { get; set; }

    /// <summary>
    /// The game's own name, for matching downloads, packs and searches against: a name you gave the table means
    /// nothing to them. <see cref="GameName"/> is the name shown.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string MatchName => OriginalName ?? GameName;
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

    /// <summary>
    /// Parent ROM sets the table's ROM also needs (a clone takes files from its parent, e.g. afm_113b
    /// needs afm_113). Only listed when PinMAME said the set isn't complete without them.
    /// </summary>
    public List<string> RomParents { get; set; } = [];

    /// <summary>The script's own name for the ROM when it's an alias ("afm_113b_me" for afm_113b), written to pinmame\alias.txt.</summary>
    public string? RomAlias { get; set; }

    /// <summary>Which version of the ROM check last ran on this table (see RomRequirements.Version); 0 = never.</summary>
    public int RomCheck { get; set; }

    /// <summary>
    /// Which version of the catalog matching last tried this table (see CatalogLink.Version); 0 = never.
    /// Only tables that didn't come from the catalog (Import tables) are tried.
    /// </summary>
    public int CatalogLink { get; set; }
    public DateTime InstalledAt { get; set; }

    /// <summary>VPS UpdatedAt (epoch ms) of the table file when it was installed; used for update checks.</summary>
    public long? SourceUpdatedAt { get; set; }

    /// <summary>The PuP pack folder the table script opens (<c>PuPlayer.Init …, "name"</c>), if it uses PinUP Player.</summary>
    public string? PupName { get; set; }

    /// <summary>The PuP pack this app installed into pupvideos\, for update checks; null when none was.</summary>
    public InstalledAddon? PupPack { get; set; }

    /// <summary>The DMD colorization this app installed (serum\ or vni\); null when none was.</summary>
    public InstalledAddon? AltColor { get; set; }

    /// <summary>The AltSound pack this app installed (altsound\); null when none was.</summary>
    public InstalledAddon? AltSound { get; set; }

    /// <summary>The .directb2s backglass this app installed beside the table (Folder = its file name); null when none was.</summary>
    public InstalledAddon? Backglass { get; set; }
}

/// <summary>
/// A catalog add-on (PuP pack, colorization, AltSound) installed as <c>&lt;table folder&gt;\&lt;Location&gt;\&lt;Folder&gt;</c>.
/// </summary>
public sealed class InstalledAddon
{
    /// <summary>VPS id of the file (pupPackFiles[].id, altColorFiles[].id, …).</summary>
    public string FileId { get; set; } = "";
    public string? Version { get; set; }
    public List<string> Authors { get; set; } = [];

    /// <summary>Folder in the table folder holding it: pupvideos, serum, vni or altsound (null = pupvideos).</summary>
    public string? Location { get; set; }

    /// <summary>Folder name under <see cref="Location"/> (the pack name, or the ROM name for colour and sound).</summary>
    public string Folder { get; set; } = "";
    public string? SourceUrl { get; set; }
    public DateTime InstalledAt { get; set; }

    /// <summary>VPS UpdatedAt (epoch ms) of the pack when it was installed.</summary>
    public long? SourceUpdatedAt { get; set; }
}
