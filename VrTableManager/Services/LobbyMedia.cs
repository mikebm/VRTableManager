namespace VrTableManager.Services;

/// <summary>
/// Everything the VR lobby can show for a table: its playfield video, loading video, attract audio and wheel art.
/// A media pack has all four, so one download fills every missing piece (<see cref="TakeFrom"/>); the catalog's
/// wheel art and sound files fill what the pack lacks. Only gaps are filled: nothing already there is replaced.
/// </summary>
public static class LobbyMedia
{
    public enum Kind
    {
        /// <summary>A media pack: all four.</summary>
        Pack,

        /// <summary>The catalog's wheel art download: only the wheel.</summary>
        WheelFiles,

        /// <summary>The catalog's sound file: only the audio.</summary>
        Sound,
    }

    public const string VideoName = "playfield video",
        LoadingName = "loading video",
        AudioName = "attract audio",
        WheelName = "wheel art";

    /// <summary>What a table's lobby media still lacks.</summary>
    public sealed class Needs
    {
        public bool Video,
            Loading,
            Audio,
            Wheel;

        public bool Any => Video || Loading || Audio || Wheel;

        public static Needs Of(InstalledTable t) =>
            new()
            {
                Video = t.HasOwnFolder && LobbyVideo.Existing(t.Folder) == null,
                Loading = t.HasOwnFolder && LoadingVideo.Own(t) == null,
                Audio = t.IsVpx && MediaPackAudio.Existing(t.Folder) == null,
                Wheel = t.HasOwnFolder && WheelArt.Find(t) == null, // your own picture counts
            };

        public Needs Copy() => (Needs)MemberwiseClone();

        public bool SameAs(Needs o) =>
            Video == o.Video && Loading == o.Loading && Audio == o.Audio && Wheel == o.Wheel;

        public List<string> Names()
        {
            var n = new List<string>();
            if (Video)
            {
                n.Add(VideoName);
            }
            if (Loading)
            {
                n.Add(LoadingName);
            }
            if (Audio)
            {
                n.Add(AudioName);
            }
            if (Wheel)
            {
                n.Add(WheelName);
            }
            return n;
        }
    }

    /// <summary>
    /// The order worth downloading in. A pack is needed anyway for the videos, so it goes first and may make the
    /// small downloads unnecessary; otherwise the small ones go first and the big pack only for what they lacked.
    /// </summary>
    public static List<(MediaPackAudio.Source Source, Kind Kind)> Order(
        IEnumerable<MediaPackAudio.Source> packs,
        IEnumerable<MediaPackAudio.Source> wheelFiles,
        IEnumerable<MediaPackAudio.Source> soundFiles,
        Needs needs
    )
    {
        var p = packs.Select(s => (s, Kind.Pack));
        var w = wheelFiles.Select(s => (s, Kind.WheelFiles));
        var s = soundFiles.Select(x => (x, Kind.Sound));
        return needs.Video || needs.Loading ? [.. p, .. w, .. s] : [.. s, .. w, .. p];
    }

    /// <summary>A download of this kind can still fill something.</summary>
    public static bool Helps(Kind kind, Needs needs) =>
        kind switch
        {
            Kind.Pack => needs.Any,
            Kind.WheelFiles => needs.Wheel,
            _ => needs.Audio,
        };

    /// <summary>
    /// Takes each piece the table still needs out of one downloaded file, saving it in the table folder and clearing
    /// it in <paramref name="needs"/>. Returns the names of the pieces saved; a piece that fails is added to
    /// <paramref name="problems"/> and doesn't cost the others. Slow (it decodes videos): run it off the UI thread.
    /// </summary>
    public static List<string> TakeFrom(
        string downloaded,
        Kind kind,
        InstalledTable table,
        Needs needs,
        List<string> problems,
        Func<byte[]?> playfieldPicture,
        Action<string>? report = null,
        CancellationToken ct = default
    )
    {
        var got = new List<string>();
        var name = table.Manifest.GameName;

        // Each piece on its own: one that fails doesn't cost the others. save() is true when it saved one.
        bool Take(string what, Func<bool> save)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (save())
                {
                    got.Add(what);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                problems.Add($"the {what} failed ({e.Message})");
            }
            return false;
        }

        if (needs.Wheel && kind != Kind.Sound)
        {
            report?.Invoke("Taking the wheel out…");
            var game = WheelArt.Game.Of(table.Manifest);
            var wheelPack = kind == Kind.WheelFiles;
            if (Take(WheelName, () => WheelArt.Save(downloaded, table.Folder, game, wheelPack, ct) != null))
            {
                needs.Wheel = false;
            }
        }

        if (needs.Audio && kind != Kind.WheelFiles)
        {
            report?.Invoke("Taking the audio out…");
            if (
                Take(
                    AudioName,
                    () =>
                        MediaPackAudio.Save(downloaded, table.Folder, name, CompressedAudio.Converter)
                        != null
                )
            )
            {
                needs.Audio = false;
            }
        }

        if (needs.Loading && kind == Kind.Pack)
        {
            report?.Invoke("Making the loading screen's frames…");
            if (
                Take(
                    LoadingName,
                    () =>
                    {
                        using var video = LoadingVideo.FromPack(downloaded, name, ct);
                        var dest = LoadingVideo.TableFolder(table.Folder);
                        return video != null && VideoFrames.LoadingFromVideo(video.Path, dest, ct) != null;
                    }
                )
            )
            {
                needs.Loading = false;
            }
        }

        if (needs.Video && kind == Kind.Pack)
        {
            report?.Invoke("Making the lobby frames…");
            if (
                Take(
                    VideoName,
                    () =>
                        LobbyVideo.SavePlayfieldVideo(downloaded, table.Folder, name) is { } video
                        && VideoFrames.FromVideo(video, table.Folder, playfieldPicture(), ct) != null
                )
            )
            {
                needs.Video = false;
            }
        }

        return got;
    }

    public static string JoinAnd(IReadOnlyList<string> parts) => Join(parts, "and");

    public static string JoinOr(IReadOnlyList<string> parts) => Join(parts, "or");

    static string Join(IReadOnlyList<string> parts, string last) =>
        parts.Count <= 1
            ? string.Concat(parts)
            : string.Join(", ", parts.Take(parts.Count - 1)) + $" {last} " + parts[^1];
}
