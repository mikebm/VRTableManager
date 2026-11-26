using System.Text.Json;
using VrTableManager.Lobby;
using VrTableManager.Models;
using VrTableManager.Services;

static class LobbyTableCacheChecks
{
    public static IEnumerable<(bool, string)> Run(string root)
    {
        var data = Path.Combine(root, "lobby-table-cache-data");
        var was = Environment.GetEnvironmentVariable("VRTM_DATA_DIR");
        Environment.SetEnvironmentVariable("VRTM_DATA_DIR", data);
        try
        {
            var folder = Path.Combine(root, "lobby-table-cache-table");
            Directory.CreateDirectory(folder);
            var vpx = Path.Combine(folder, "Table.vpx");
            using (var cfb = OpenMcdf.RootStorage.Create(vpx))
            {
                using var gameData = cfb.CreateStorage("GameStg").CreateStream("GameData");
                gameData.Write([], 0, 0);
            }

            var table = new InstalledTable(
                new TableManifest
                {
                    Engine = TableManifest.Vpx,
                    GameName = "Lobby cache table",
                    VpxFile = Path.GetFileName(vpx),
                },
                vpx,
                folder
            );
            File.WriteAllText(
                Path.Combine(folder, "vrtm.json"),
                JsonSerializer.Serialize(table.Manifest)
            );

            var cabinets = LobbyFiles.Cabinets;
            var pictures = LobbyFiles.Pictures;
            var toys = ToyFiles.Dir;
            var cabinet = CabinetFiles.FileFor(vpx, cabinets);
            var toy = ToyFiles.FileFor(vpx, toys);
            Directory.CreateDirectory(cabinets);
            Directory.CreateDirectory(toys);
            File.WriteAllText(cabinet, "cabinet");
            File.WriteAllText(toy, "toys");

            var otherVpx = Path.Combine(root, "another-table.vpx");
            File.WriteAllText(otherVpx, "other");
            var otherCabinet = CabinetFiles.FileFor(otherVpx, cabinets);
            File.WriteAllText(otherCabinet, "other cabinet");

            _ = VpxImages.CachedPlayfield(vpx, pictures);
            var b2s = Backglasses.PathFor(table);
            File.WriteAllText(
                b2s,
                "<DirectB2SData Version=\"1.2\"><Name Value=\"x\" /></DirectB2SData>"
            );
            _ = DirectB2s.CachedBackglass(b2s, pictures);
            var pictureFiles = Directory.GetFiles(pictures);
            if (pictureFiles.Length == 0)
            {
                throw new InvalidOperationException("The test did not create picture cache entries.");
            }

            TableInstaller.Remove(table);

            yield return (
                !Directory.Exists(folder)
                    && !File.Exists(cabinet)
                    && !File.Exists(toy)
                    && pictureFiles.All(f => !File.Exists(f))
                    && File.Exists(otherCabinet),
                "removing a table clears its lobby cabinet, toys and extracted-picture caches without touching another table"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRTM_DATA_DIR", was);
        }
    }
}
