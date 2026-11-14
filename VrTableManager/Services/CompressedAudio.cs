using NAudio.MediaFoundation;
using NAudio.Wave;

namespace VrTableManager.Services;

/// <summary>
/// Turns AAC audio (the sound track of a media pack's .mp4/.f4v videos, or .m4a files) into a short
/// MP3 the VR lobby can play: Godot plays MP3, Ogg and WAV but not AAC. Uses Windows' own Media
/// Foundation decoder and MP3 encoder, so nothing extra is installed; elsewhere it does nothing.
/// </summary>
public static class CompressedAudio
{
    public static readonly string[] Exts =
    [
        ".m4a",
        ".aac",
        ".mp4",
        ".m4v",
        ".mov",
        ".f4v",
        ".wma",
        ".wmv",
    ];

    /// <summary>Longest clip kept: plenty for a selection sound, and small (about 1.4 MB at 192 kbps).</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(60);
    const float SilenceLevel = 0.01f;
    static readonly TimeSpan MaxLeadIn = TimeSpan.FromSeconds(20),
        FadeOut = TimeSpan.FromSeconds(3);

    public static MediaPackAudio.Converter Converter => new(Exts, ToLobbyAudio);

    /// <summary>
    /// The file's sound from its first non-silent moment, up to <see cref="MaxLength"/>, faded out and
    /// written into <paramref name="workDir"/> as .mp3 (or .wav if the MP3 encoder refuses the format).
    /// Null when there's no audio track, it can't be decoded, or it's silent.
    /// </summary>
    public static string? ToLobbyAudio(string input, string workDir) =>
        ToLobbyAudio(input, workDir, MaxLength, trimLeadIn: true);

    /// <param name="maxLength">Longest clip kept.</param>
    /// <param name="trimLeadIn">Start at the first sound (attract music); false keeps the start, so the sound stays in time with a video.</param>
    public static string? ToLobbyAudio(
        string input,
        string workDir,
        TimeSpan maxLength,
        bool trimLeadIn
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? copy = null;
        try
        {
            // Flash video (.f4v) is MP4 inside; Media Foundation goes by the extension.
            if (Path.GetExtension(input).Equals(".f4v", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(
                    input,
                    copy = Path.Combine(
                        workDir,
                        "f4v-" + Guid.NewGuid().ToString("N")[..8] + ".mp4"
                    )
                );
            }

            MediaFoundationApi.Startup();
            using var reader = new MediaFoundationReader(copy ?? input);
            var source = reader.ToSampleProvider();
            var (samples, rate, channels) = ReadClip(source, maxLength, trimLeadIn);
            if (samples == null)
            {
                return null;
            }

            var pcm = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                BitConverter.TryWriteBytes(
                    pcm.AsSpan(i * 2),
                    (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue)
                );
            }

            var format = new WaveFormat(rate, 16, channels);
            var name = Path.Combine(workDir, "converted-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                using var raw = new RawSourceWaveStream(new MemoryStream(pcm), format);
                MediaFoundationEncoder.EncodeToMp3(raw, name + ".mp3", 192_000);
                if (new FileInfo(name + ".mp3").Length > 0)
                {
                    return name + ".mp3";
                }
            }
            catch (Exception)
            { /* no MP3 encoder for this rate/layout: WAV is still playable */
            }
            try
            {
                File.Delete(name + ".mp3"); // an empty or half-written one, so it isn't left beside the WAV
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            using (var raw = new RawSourceWaveStream(new MemoryStream(pcm), format))
            {
                WaveFileWriter.CreateWaveFile(name + ".wav", raw);
            }

            return name + ".wav";
        }
        catch (Exception)
        {
            return null; // no audio stream, or a codec Windows doesn't have
        }
        finally
        {
            if (copy != null)
            {
                try
                {
                    File.Delete(copy);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// A WAV clip as a 128 kbps MP3 (a ROM's attract music, <see cref="RomAudio.ToMp3"/>), with Windows' own Media
    /// Foundation encoder: written beside <paramref name="mp3"/> under a name of its own, read back and checked to
    /// last as long as the WAV, then moved into place. False (and nothing left behind) when it can't be done; the
    /// WAV is never touched.
    /// </summary>
    public static bool WavToMp3(string wav, string mp3)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // Media Foundation picks the container by the extension, so the temp name ends in .mp3 too.
        var tmp = Path.Combine(
            Path.GetDirectoryName(mp3)!,
            $"{Path.GetFileNameWithoutExtension(mp3)}.{Guid.NewGuid():N}.tmp.mp3"
        );
        try
        {
            MediaFoundationApi.Startup();
            TimeSpan length;
            using (var reader = new WaveFileReader(wav))
            {
                length = reader.TotalTime;
                MediaFoundationEncoder.EncodeToMp3(reader, tmp, 128_000);
            }
            using (var check = new MediaFoundationReader(tmp))
            {
                // MP3 frames pad the ends a little: anything much shorter is a broken file.
                if (
                    new FileInfo(tmp).Length == 0
                    || Math.Abs((check.TotalTime - length).TotalSeconds) > 0.5
                )
                {
                    throw new InvalidDataException("the MP3 doesn't match the recording");
                }
            }
            File.Move(tmp, mp3, overwrite: true);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch { }
            return false;
        }
    }

    /// <summary>Interleaved samples (at most stereo) from the first sound on, faded out; null when silent.</summary>
    static (float[]? samples, int rate, int channels) ReadClip(
        ISampleProvider source,
        TimeSpan maxLength,
        bool trimLeadIn
    )
    {
        var inChannels = source.WaveFormat.Channels;
        var channels = Math.Min(inChannels, 2);
        var rate = source.WaveFormat.SampleRate;
        var frames = new List<float>((int)(maxLength.TotalSeconds * rate * channels));
        var buffer = new float[rate * inChannels]; // one second
        long leadIn = 0,
            maxLeadIn = (long)(MaxLeadIn.TotalSeconds * rate),
            maxFrames = (long)(maxLength.TotalSeconds * rate);
        var started = !trimLeadIn;
        int n;
        while (
            frames.Count / channels < maxFrames && (n = source.Read(buffer, 0, buffer.Length)) > 0
        )
        {
            for (
                var f = 0;
                f + inChannels <= n && frames.Count / channels < maxFrames;
                f += inChannels
            )
            {
                if (!started)
                {
                    var loud = false;
                    for (var c = 0; c < inChannels; c++)
                    {
                        loud |= Math.Abs(buffer[f + c]) > SilenceLevel;
                    }

                    if (!loud && ++leadIn < maxLeadIn)
                    {
                        continue;
                    }

                    started = true;
                }
                for (var c = 0; c < channels; c++)
                {
                    frames.Add(buffer[f + c]);
                }
            }
        }
        var samples = frames.ToArray();
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        if (peak <= SilenceLevel)
        {
            return (null, rate, channels);
        }

        // Quiet recordings are brought up to a normal level; the end fades out instead of cutting off.
        var gain = peak < 0.5f ? 0.9f / peak : 1f;
        var fadeFrames = Math.Min((long)(FadeOut.TotalSeconds * rate), samples.Length / channels);
        var total = samples.Length / channels;
        for (var f = 0; f < total; f++)
        {
            var g = gain * (f >= total - fadeFrames ? (total - f) / (float)fadeFrames : 1f);
            for (var c = 0; c < channels; c++)
            {
                samples[f * channels + c] *= g;
            }
        }
        return (samples, rate, channels);
    }
}
