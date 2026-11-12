using System.Text.Json;

namespace VrTableManager.Services;

/// <summary>
/// How a lobby launch is going, written by <see cref="PlaySession"/> for the VR lobby, which stays in the
/// headset on a loading screen while the table starts. The lobby reads it to know when the table failed or
/// closed before it ever reached the headset, so it can say so instead of leaving you in an empty view.
/// </summary>
public sealed class PlayStatus
{
    public const string Starting = "starting",
        Running = "running",
        Failed = "failed",
        Exited = "exited";

    public string State { get; set; } = Starting;
    public string Message { get; set; } = "";
    public DateTime Time { get; set; }

    public static string FileName => "play-status.json";

    /// <summary>Next to the settings file the lobby uses (the default data folder when none is given).</summary>
    public static string PathBeside(string? settingsFile) =>
        Path.Combine(
            string.IsNullOrEmpty(settingsFile)
                ? AppSettings.DataDir
                : Path.GetDirectoryName(Path.GetFullPath(settingsFile))!,
            FileName
        );

    /// <summary>Written to a temporary file and moved into place, so a reader never sees half a file.</summary>
    public static void Write(string path, string state, string message = "")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Tried again for a moment while the lobby has the file open.
            PlayStats.WriteAtomic(
                path,
                JsonSerializer.Serialize(
                    new PlayStatus
                    {
                        State = state,
                        Message = message,
                        Time = DateTime.Now,
                    }
                )
            );
        }
        catch
        { /* the status is a courtesy to the lobby; it must never stop a table */
        }
    }

    public static PlayStatus? Read(string path)
    {
        try
        {
            return PlayStats.ReadShared(path) is { } text
                ? JsonSerializer.Deserialize<PlayStatus>(text)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch { }
    }
}
