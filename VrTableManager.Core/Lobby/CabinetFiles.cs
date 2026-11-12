using System.Security.Cryptography;
using System.Text;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.Lobby;

/// <summary>
/// Where the VR lobby keeps each table's cabinet once it has read it from the .vpx (VrLobby's CabinetCache, which
/// uses these names), so the manager can tell which tables the lobby hasn't read yet (PREPARE TABLES).
/// </summary>
public static class CabinetFiles
{
    public static string DefaultDir => Path.Combine(AppSettings.DataDir, "cabinet-cache");

    public static string FileFor(string vpx, string dir) =>
        Path.Combine(
            dir,
            Convert.ToHexString(
                SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(vpx).ToLowerInvariant()))
            ) + ".cab"
        );

    /// <summary>A table whose cabinet the lobby reads from its .vpx (a VPX table with its file there).</summary>
    public static bool HasCabinet(InstalledTable t) =>
        !t.IsIncomplete
        && t.Manifest.Engine == TableManifest.Vpx
        && t.TablePath.EndsWith(".vpx", StringComparison.OrdinalIgnoreCase)
        && File.Exists(t.TablePath);

    /// <summary>
    /// How many of these tables the lobby hasn't read yet: no cabinet file at all. (One left from an older version
    /// of the table counts as read here; PREPARE TABLES itself checks the version and reads it again.)
    /// </summary>
    public static int Unprepared(IEnumerable<InstalledTable> tables, string? dir = null)
    {
        dir ??= DefaultDir;
        return tables
            .Where(HasCabinet)
            .Select(t => t.TablePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(vpx => !File.Exists(FileFor(vpx, dir)));
    }
}
