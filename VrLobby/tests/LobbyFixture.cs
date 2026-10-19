using System.Text.Json;

namespace VrLobby.Tests;

/// <summary>
/// A fake VR Table Manager install with 100 VPX tables, 2 Future Pinball tables, a hand-installed
/// table and an empty folder. Plain .NET (no Godot), shared by the lobby's self-test and SmokeTest.
/// </summary>
public static class LobbyFixture
{
    public static readonly string[] Titles =
    [
        "24", "1-2-3", "Addams Family", "Attack from Mars", "Avatar", "Bad Cats", "Banzai Run", "Black Knight",
        "Bram Stoker's Dracula", "Cactus Canyon", "Centaur", "Champion Pub", "Cirqus Voltaire", "Creature from the Black Lagoon",
        "Cyclone", "Demolition Man", "Dirty Harry", "Doctor Who", "Earthshaker", "Eight Ball Deluxe", "Elvira and the Party Monsters",
        "F-14 Tomcat", "Fathom", "Firepower", "Fish Tales", "Flash Gordon", "Funhouse", "Getaway", "Gorgar", "Grand Lizard",
        "Harlem Globetrotters", "Haunted House", "High Speed", "Hook", "Hurricane", "Indiana Jones", "Iron Maiden",
        "Jack-Bot", "Johnny Mnemonic", "Judge Dredd", "Junk Yard", "Kiss", "King Kong", "Lethal Weapon 3", "Lights Camera Action",
        "Lord of the Rings", "Mars Attacks", "Medieval Madness", "Metallica", "Monster Bash", "Mousin' Around", "Mystery Castle",
        "Nine Ball", "No Fear", "No Good Gofers", "Old Chicago", "Orbitor 1", "Party Zone", "Pinbot", "Pirates of the Caribbean",
        "Police Force", "Popeye", "Quicksilver", "Radical", "Red & Ted's Road Show", "Revenge from Mars", "Riverboat Gambler",
        "Safe Cracker", "Scared Stiff", "Space Shuttle", "Spider-Man", "Star Trek", "Swords of Fury", "Tales of the Arabian Nights",
        "Taxi", "Terminator 2", "The Addams Family Gold", "The Getaway", "Theatre of Magic", "Twilight Zone", "Uncle Sam",
        "Viking", "Vector", "Victory", "Whirlwind", "White Water", "Who Dunnit", "World Cup Soccer", "X-Men", "Xenon",
        "Yamanaka", "Young Frankenstein", "Zaccaria Fire", "Zissou", "Zodiac", "Zombie Yeti", "Medusa", "Mata Hari", "Monaco", "Meteor",
    ];
    public static readonly string[] Makers = ["Williams", "Bally", "Gottlieb", "Stern", "Data East"];
    public const string ArtUrl = "https://example.com/mm.png";

    public record Paths(string Root, string Settings, string VpxDir, string TablesDir, string FpDir, string MedievalMadness);

    public static Paths Create(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var vpx = Path.Combine(root, "VPinball X");
        var tables = Path.Combine(root, "Tables");
        var fp = Path.Combine(root, "Future Pinball");
        Write(Path.Combine(vpx, "VPinballX_GL64.exe"), "");
        Write(Path.Combine(vpx, "VPinballX_BGFX64.exe"), "");

        string mm = "";
        for (var i = 0; i < Titles.Length; i++)
        {
            var title = Titles[i];
            var folder = Path.Combine(tables, title.Replace("'", "").Replace("&", "and"));
            Write(Path.Combine(folder, "table.vpx"), "");
            var isMm = title == "Medieval Madness";
            Write(Path.Combine(folder, "vrtm.json"), JsonSerializer.Serialize(new
            {
                Engine = "VPX", GameId = "g" + i, GameName = title, Manufacturer = isMm ? "Williams" : Makers[i % Makers.Length],
                Year = isMm ? 1997 : 1975 + i * 7 % 45, Version = isMm ? "2.1" : null, VpxFile = "table.vpx",
                ImgUrl = isMm ? ArtUrl : null, RomNames = isMm ? new[] { "mm_109c" } : Array.Empty<string>(),
            }));
            if (isMm) mm = Path.Combine(folder, "table.vpx");
        }
        // Installed by hand (no manifest), and a folder with no table in it.
        Write(Path.Combine(tables, "Attack From Mars Hand", "AFM.vpx"), "");
        Write(Path.Combine(tables, "Empty folder", "readme.txt"), "");

        Write(Path.Combine(fp, "Future Pinball.exe"), "");
        Write(Path.Combine(fp, "BAM", "FPLoader.exe"), "");
        Write(Path.Combine(fp, "Tables", "Blood Machines.fpt"), "");
        Write(Path.Combine(fp, "Tables", "Blood Machines.vrtm.json"),
            JsonSerializer.Serialize(new { Engine = "FP", GameName = "Blood Machines", Year = 2020, VpxFile = "Blood Machines.fpt" }));
        Write(Path.Combine(fp, "Tables", "Plain.fpt"), "");

        var settings = Path.Combine(root, "settings.json");
        Write(settings, JsonSerializer.Serialize(new { VpxDir = vpx, TablesDir = tables, FpDir = fp, FpRunAsAdmin = true }));
        return new Paths(root, settings, vpx, tables, fp, mm);
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
