namespace VrTableManager.Services;

/// <summary>
/// Everything the VR lobby can show for a table: its playfield video, loading video, attract audio and wheel art.
/// A media pack usually has all four, so one download can fill every missing piece (<see cref="TakeFrom"/>); the
/// catalog's wheel art and sound files fill what packs lack. The manager's Get… commands all go through this.
/// </summary>
public static class LobbyMedia
{
    public enum Kind
    {
        /// <summary>A media pack: any of the four.</summary>
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

    /// <summary>A set of the four pieces: what a table lacks, or what to get.</summary>
    public sealed class Needs
    {
        public bool Video,
            Loading,
            Audio,
            Wheel;

        public bool Any => Video || Loading || Audio || Wheel;

        /// <summary>What a table with a folder of its own lacks (a loose table lacks nothing: it can't keep any).</summary>
        public static Needs Of(InstalledTable t) =>
            t.HasOwnFolder
                ? new()
                {
                    Video = LobbyVideo.Existing(t.Folder) == null,
                    Loading = LoadingVideo.Own(t) == null,
                    Audio = MediaPackAudio.Existing(t.Folder) == null,
                    Wheel = WheelArt.Find(t) == null, // your own picture counts
                }
                : new();

        /// <summary>Only the pieces that are in both.</summary>
        public Needs And(Needs o) =>
            new()
            {
                Video = Video && o.Video,
                Loading = Loading && o.Loading,
                Audio = Audio && o.Audio,
                Wheel = Wheel && o.Wheel,
            };

        public Needs Or(Needs o) =>
            new()
            {
                Video = Video || o.Video,
                Loading = Loading || o.Loading,
                Audio = Audio || o.Audio,
                Wheel = Wheel || o.Wheel,
            };

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

    /// <summary>Something that went wrong, and which piece it cost (null: a whole download).</summary>
    public sealed record Problem(string? Piece, string Text);

    /// <summary>A download of this kind can fill something still wanted.</summary>
    public static bool Helps(Kind kind, Needs wanted) =>
        kind switch
        {
            Kind.Pack => wanted.Any,
            Kind.WheelFiles => wanted.Wheel,
            _ => wanted.Audio,
        };

    /// <summary>
    /// The next download worth making, of those not tried yet, or null when none can fill anything wanted. While a
    /// video is wanted the media packs go first (they may make the small downloads unnecessary); otherwise the small
    /// wheel art and sound files go first, and a big pack only for what they lacked. Asked again after each download,
    /// so a pack that filled the videos but not the wheel is followed by the small wheel download, not another pack.
    /// </summary>
    public static (MediaPackAudio.Source Source, Kind Kind)? Next(
        IEnumerable<(MediaPackAudio.Source Source, Kind Kind)> left,
        Needs wanted
    ) =>
        left.Where(s => Helps(s.Kind, wanted))
            .OrderBy(s => Rank(s.Kind, wanted))
            .Select(s => ((MediaPackAudio.Source, Kind)?)s)
            .FirstOrDefault();

    static int Rank(Kind kind, Needs wanted) =>
        (wanted.Video || wanted.Loading, kind) switch
        {
            (true, Kind.Pack) => 0,
            (true, Kind.WheelFiles) => 1,
            (true, _) => 2,
            (false, Kind.Sound) => 0,
            (false, Kind.WheelFiles) => 1,
            _ => 2,
        };

    /// <summary>
    /// Takes each piece in <paramref name="take"/> out of one downloaded file, saving it in the table folder (replacing
    /// one already there) and clearing it in <paramref name="take"/>. Pieces saved are added to <paramref name="got"/>
    /// as they're saved, so they're known even when this is cancelled part way. A piece that fails goes into
    /// <paramref name="problems"/> and doesn't cost the others. Slow (it decodes videos): run it off the UI thread.
    /// </summary>
    /// <param name="from">The download, for problems: "media pack", "wheel art by …".</param>
    public static void TakeFrom(
        string downloaded,
        Kind kind,
        string from,
        InstalledTable table,
        Needs take,
        List<string> got,
        List<Problem> problems,
        Func<byte[]?> playfieldPicture,
        Action<string>? report = null,
        CancellationToken ct = default
    )
    {
        var name = table.Manifest.MatchName;

        // save() is true when it saved the piece; it may add its own problem when it found one that won't work.
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
                problems.Add(new(what, $"the {from}'s {what} failed ({e.Message})"));
            }
            return false;
        }

        // A video was found but Windows couldn't decode it: say so, rather than "there's none".
        bool Unplayable(string what, string file)
        {
            problems.Add(
                new(what, $"Windows couldn't play the {from}'s {what} ({Path.GetExtension(file)})")
            );
            return false;
        }

        if (take.Wheel && kind != Kind.Sound)
        {
            report?.Invoke("Taking the wheel out…");
            var game = WheelArt.Game.Of(table.Manifest);
            var wheelPack = kind == Kind.WheelFiles;
            take.Wheel = !Take(
                WheelName,
                () => WheelArt.Save(downloaded, table.Folder, game, wheelPack, ct) != null
            );
        }

        if (take.Audio && kind != Kind.WheelFiles)
        {
            report?.Invoke("Taking the audio out…");
            take.Audio = !Take(
                AudioName,
                () =>
                    MediaPackAudio.Save(downloaded, table.Folder, name, CompressedAudio.Converter)
                    != null
            );
        }

        if (take.Loading && kind == Kind.Pack)
        {
            report?.Invoke("Making the loading screen's frames…");
            take.Loading = !Take(
                LoadingName,
                () =>
                {
                    using var video = LoadingVideo.FromPack(downloaded, name, ct);
                    if (video == null)
                    {
                        return false;
                    }
                    var dest = LoadingVideo.TableFolder(table.Folder);
                    return VideoFrames.LoadingFromVideo(video.Path, dest, ct) != null
                        || Unplayable(LoadingName, video.Entry);
                }
            );
        }

        if (take.Video && kind == Kind.Pack)
        {
            report?.Invoke("Making the lobby frames…");
            take.Video = !Take(
                VideoName,
                () =>
                {
                    if (
                        LobbyVideo.SavePlayfieldVideo(downloaded, table.Folder, name)
                        is not { } video
                    )
                    {
                        return false;
                    }
                    return VideoFrames.FromVideo(video, table.Folder, playfieldPicture(), ct)
                            != null
                        || Unplayable(VideoName, video);
                }
            );
        }
    }

    /// <summary>Why a table still has no playfield video after the catalog's downloads: said before offering to record one.</summary>
    public static string WhyNoVideo(IEnumerable<Problem> problems, bool hadPack)
    {
        var why = problems.Where(p => p.Piece is null or VideoName).Select(p => p.Text).ToList();
        return why.Count > 0 ? "No playfield video: " + string.Join("; ", why) + "."
            : hadPack ? "The media pack has no playfield video for this table."
            : "The catalog has no media pack for this table.";
    }

    /// <summary>What GET LOBBY MEDIA says when it's done.</summary>
    /// <param name="filled">Tables that got at least one piece.</param>
    /// <param name="stillMissing">Tables still missing at least one piece.</param>
    /// <param name="needRom">Of those, tables without a playfield video that VPX couldn't record (no ROM yet).</param>
    /// <param name="notRecorded">Of those, tables without a playfield video you chose not to record.</param>
    /// <param name="skipped">Tables whose job you cancelled (or that were busy), skipped.</param>
    public static string BatchSummary(
        int filled,
        int recorded,
        int stillMissing,
        int needRom,
        int notRecorded,
        int skipped,
        bool stopped
    )
    {
        static string Tables(int n) => $"{n} table{(n == 1 ? "" : "s")}";
        var s = $"Added VR lobby media to {Tables(filled)}";
        s += recorded > 0 ? $" ({recorded} recorded in VPX)." : ".";
        if (stillMissing > 0)
        {
            var why = new List<string>();
            if (needRom > 0)
            {
                why.Add($"{needRom} need a ROM before VPX can record them");
            }
            if (notRecorded > 0)
            {
                why.Add($"{notRecorded} weren't recorded");
            }
            s +=
                $" {Tables(stillMissing)} still miss something"
                + (why.Count > 0 ? " (" + string.Join(", ", why) + ")" : "")
                + ": their MORE menu has the single Get and Use my own… items.";
        }
        if (skipped > 0)
        {
            s += $" Skipped {Tables(skipped)}.";
        }
        if (stopped)
        {
            s += " Stopped before the end.";
        }
        return s;
    }

    public static string JoinAnd(IReadOnlyList<string> parts) => Join(parts, "and");

    public static string JoinOr(IReadOnlyList<string> parts) => Join(parts, "or");

    static string Join(IReadOnlyList<string> parts, string last) =>
        parts.Count <= 1
            ? string.Concat(parts)
            : string.Join(", ", parts.Take(parts.Count - 1)) + $" {last} " + parts[^1];
}
