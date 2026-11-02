using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// Rules links (RuleSheets): reading The Pinball Primer's game list and matching catalog names to it, plus the
/// FlexDMD/B2S plugin switch in the VR ini.
/// </summary>
public static class RuleSheetChecks
{
    public static IEnumerable<(bool ok, string name)> Run(string root)
    {
        const string html = """
            <h3>A</h3>
            <a href="addams_G4ODR.html">Addams Family, The (Bally Williams, DMD, 1992)</a><br>
            <a href="afm_G4do5.html">Attack From Mars (Bally Williams, DMD, 1995)</a><br>
            <a href="getaway_Grx8Y.html">Getaway: High Speed II, The (Bally Williams, DMD, 1992)</a><br>
            <a href="metallica_GRBE4.html">Metallica (Stern Pinball, DMD, 2013)</a><br>
            <a href="jaws_GXXXX.html">Jaws (Stern Pinball, LCD, 2024)</a><br>
            <a href="bbb_G1.html">Bram Stoker&#39;s Dracula (Williams, DMD, 1993)</a><br>
            <a href="index.html">main page.</a>
            """;
        var entries = RuleSheets.Parse(html);
        yield return (entries.Count == 6 && entries[0] is { Name: "Addams Family, The", Manufacturer: "Bally Williams", Year: 1992, Url: "https://pinballprimer.github.io/addams_G4ODR.html" }
                && entries[5].Name == "Bram Stoker's Dracula",
            "Pinball Primer list parsed (name, maker, year, page; HTML entities decoded; other links ignored)");

        string? P(string name, int? year) => RuleSheets.PrimerUrl(name, year, entries);
        yield return (P("The Addams Family", 1992)?.EndsWith("addams_G4ODR.html") == true
                && P("Attack from Mars", 1995)?.EndsWith("afm_G4do5.html") == true
                && P("The Getaway - High Speed II", 1992)?.EndsWith("getaway_Grx8Y.html") == true,
            "names match across \"The\", case and punctuation");
        yield return (P("Metallica (Premium Monsters)", 2013)?.EndsWith("metallica_GRBE4.html") == true,
            "an edition name matches the machine it's an edition of");
        yield return (P("Jaws", 2025) == null && P("Addams Family", 1994) == null, "a different year (an original or a remake) doesn't match");
        var t3 = RuleSheets.Parse("""<a href="terminator3_GR91N.html">Terminator 3: Rise of the Machines (Stern Pinball, DMD, 2003)</a><a href="pinballstern_GrZXj.html">Pinball (Stern, SS, 1977)</a>""");
        yield return (RuleSheets.PrimerUrl("JP's Terminator 3", 2003, t3)?.EndsWith("terminator3_GR91N.html") == true
                && RuleSheets.PrimerUrl("Pinball FX", null, t3) == null,
            "a table author's name in front is tried without it; partial names need matching years");

        var game = new VpsGame
        {
            Name = "Attack from Mars", Year = 1995,
            RuleFiles =
            [
                new VpsFile { Comment = "French", Authors = ["pascal40"], UpdatedAt = 9, Urls = [new VpsUrl { Url = "https://vpuniverse.com/files/file/32208-fr/" }] },
                new VpsFile { Authors = ["kilazz"], UpdatedAt = 5, Urls = [new VpsUrl { Url = "https://vpuniverse.com/files/file/31947-afm/" }] },
            ],
        };
        var links = RuleSheets.LinksFor(game.Name, game.Year, game, entries);
        yield return (links.Select(l => l.Label).SequenceEqual(["Rules and strategy (The Pinball Primer)", "Instruction card by kilazz", "Instruction card by pascal40 (French)"]),
            "rules menu: the Primer first, then instruction cards (other languages last)");

        // VR ini turns FlexDMD and B2S on (or off).
        var vs = new AppSettings { VpxDir = Path.Combine(root, "display-vpx") };
        string? Read(string plugin) => IniFile.Read(VpxSetup.VrIniPath(vs)).GetValueOrDefault(("Plugin." + plugin, "Enable"));
        VpxSetup.EnsureVrIni(vs);
        var on = (Read("FlexDMD"), Read("B2S"));
        vs.UseDisplayPlugins = false;
        VpxSetup.EnsureVrIni(vs);
        yield return (on == ("1", "1") && (Read("FlexDMD"), Read("B2S")) == ("0", "0"), "VR ini enables VPX's FlexDMD and B2S plugins, and the setting turns them off");
    }
}
