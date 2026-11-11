namespace VrTableManager.Lobby;

/// <summary>
/// Where the playfield starts in a recorded lobby video's frame. VPX's attract recording (LobbyVideo, cabinet view
/// fitted to the window) shows the playfield edge to edge across, but the back board's art stands up behind it and
/// fills the top of the frame (about 9% on JAWS). Stretched over the lobby's playfield, that band painted a squashed
/// copy of the back art onto the back of the playfield. This finds the band by matching the frame against the
/// table's own playfield picture, so the lobby can cut it off as it plays: nothing is recorded again. Pure maths on
/// grey pictures, so SmokeTest checks it.
/// </summary>
public static class PlayfieldMatch
{
    /// <summary>Size the frame is compared at (the lobby's portrait frames, scaled down).</summary>
    public const int FrameW = 90,
        FrameH = 160;

    /// <summary>Size the playfield picture is compared at.</summary>
    public const int PlayfieldW = 40,
        PlayfieldH = 90;

    /// <summary>A band to cut is at least this much of the frame's height, and at most <see cref="MaxTrim"/>.</summary>
    public const double MinTrim = 0.03,
        MaxTrim = 0.2;

    /// <summary>How much the playfield picture must resemble the uncut frame for the match to be trusted at all.</summary>
    public const double MinResemblance = 0.12;

    /// <summary>
    /// The share of the frame's height (0..<see cref="MaxTrim"/>) above the playfield, or 0 when there's no clear
    /// band. A rendered frame and the flat playfield picture never match closely (lighting, toys, ramps), so two
    /// things must hold: the picture already resembles the uncut frame (Apollo 13's doesn't, and its best "band"
    /// cut real playfield), and cutting the band improves the match by at least <paramref name="minGain"/>.
    /// </summary>
    /// <param name="frame">Grey levels 0..1, <see cref="FrameW"/>×<see cref="FrameH"/>, rows from the top.</param>
    /// <param name="playfield">Grey levels 0..1, <see cref="PlayfieldW"/>×<see cref="PlayfieldH"/>, rows from the top.</param>
    public static double TopTrim(float[] frame, float[] playfield, double minGain = 0.04)
    {
        if (frame.Length != FrameW * FrameH || playfield.Length != PlayfieldW * PlayfieldH)
        {
            return 0;
        }
        var uncut = Score(frame, playfield, 0);
        double best = uncut,
            bestTop = 0;
        for (var top = 1; top <= (int)(FrameH * MaxTrim); top++)
        {
            var s = Score(frame, playfield, top);
            if (s > best)
            {
                (best, bestTop) = (s, top);
            }
        }
        var trim = bestTop / FrameH;
        return trim >= MinTrim && uncut >= MinResemblance && best - uncut >= minGain ? trim : 0;
    }

    /// <summary>How well the playfield matches the frame below row <paramref name="top"/> (normalised cross-correlation).</summary>
    static double Score(float[] frame, float[] playfield, int top)
    {
        double sa = 0,
            sb = 0,
            saa = 0,
            sbb = 0,
            sab = 0;
        var n = 0;
        var rows = FrameH - top;
        for (var j = 0; j < PlayfieldH; j++)
        {
            var fy = top + (int)((j + 0.5) / PlayfieldH * rows);
            for (var i = 0; i < PlayfieldW; i++)
            {
                var fx = (int)((i + 0.5) / PlayfieldW * FrameW);
                double a = playfield[j * PlayfieldW + i],
                    b = frame[fy * FrameW + fx];
                sa += a;
                sb += b;
                saa += a * a;
                sbb += b * b;
                sab += a * b;
                n++;
            }
        }
        var cov = sab / n - sa / n * (sb / n);
        var va = saa / n - sa * sa / n / n;
        var vb = sbb / n - sb * sb / n / n;
        return cov / Math.Sqrt(Math.Max(va * vb, 1e-12));
    }
}
