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
        var vpxPrefs = Path.Combine(root, "lobby-table-cache-vpx-prefs");
        var wasVpxPrefs = Environment.GetEnvironmentVariable("VRTM_VPX_PREFS");
        Environment.SetEnvironmentVariable("VRTM_DATA_DIR", data);
        Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", vpxPrefs);
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
            var tableTextureCache = Path.Combine(folder, "cache", "Table");
            var prefsTextureCache = Path.Combine(vpxPrefs, "Cache", "Table");
            var versionedTextureCache = Path.Combine(vpxPrefs, "10.8", "Cache", "Table");
            var otherTextureCache = Path.Combine(vpxPrefs, "Cache", "Another Table");
            Directory.CreateDirectory(cabinets);
            Directory.CreateDirectory(toys);
            File.WriteAllText(cabinet, "cabinet");
            File.WriteAllText(toy, "toys");
            Directory.CreateDirectory(tableTextureCache);
            Directory.CreateDirectory(prefsTextureCache);
            Directory.CreateDirectory(versionedTextureCache);
            Directory.CreateDirectory(otherTextureCache);
            File.WriteAllText(Path.Combine(tableTextureCache, "used_textures.xml"), "table");
            File.WriteAllText(Path.Combine(prefsTextureCache, "texture.vpxtex"), "table");
            File.WriteAllText(Path.Combine(versionedTextureCache, "texture.vpxtex"), "table");
            File.WriteAllText(Path.Combine(otherTextureCache, "texture.vpxtex"), "other");

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
                    && !Directory.Exists(tableTextureCache)
                    && !Directory.Exists(prefsTextureCache)
                    && !Directory.Exists(versionedTextureCache)
                    && File.Exists(Path.Combine(otherTextureCache, "texture.vpxtex"))
                    && pictureFiles.All(f => !File.Exists(f))
                    && File.Exists(otherCabinet),
                "removing a table clears its lobby and VPX texture caches without touching another table's cache"
            );

            Directory.Delete(toys);
            File.Delete(otherCabinet);
            Directory.Delete(cabinets);
            Directory.Delete(pictures);
            var missingToysFolder = Path.Combine(root, "lobby-table-cache-missing-toys");
            Directory.CreateDirectory(missingToysFolder);
            var missingToysVpx = Path.Combine(missingToysFolder, "Table.vpx");
            File.WriteAllText(missingToysVpx, "table");
            var missingToysTable = new InstalledTable(
                new TableManifest
                {
                    Engine = TableManifest.Vpx,
                    GameName = "Lobby cache table without toys",
                    VpxFile = Path.GetFileName(missingToysVpx),
                },
                missingToysVpx,
                missingToysFolder
            );
            TableInstaller.Remove(missingToysTable);

            yield return (
                !File.Exists(missingToysVpx)
                    && !Directory.Exists(cabinets)
                    && !Directory.Exists(toys)
                    && !Directory.Exists(pictures),
                "removing a table tolerates missing lobby cache folders"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRTM_DATA_DIR", was);
            Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", wasVpxPrefs);
        }
    }
}
