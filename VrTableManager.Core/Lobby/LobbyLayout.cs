namespace VrTableManager.Lobby;

/// <summary>
/// Where the VR lobby's ring of cabinets stands, from the two choices on Setup → VR lobby: how big the cabinets
/// are and how many stand in the ring. Pure maths (no Godot), so SmokeTest checks it; the lobby places its
/// cabinets from the <see cref="LobbyRing"/> this returns. The defaults give exactly the ring the lobby always
/// had: 11 cabinets, 34° apart, their fronts 2.1 m from you.
/// </summary>
public static class LobbyLayout
{
    public const int DefaultScalePercent = 100;
    public const int MinScalePercent = 75; // smaller and the backglass and DMD get hard to read from the ring
    public const int MaxScalePercent = 125; // bigger and the backbox towers over you (2.4 m)
    public const int ScaleStepPercent = 5;

    public const int DefaultCount = 11;
    public const int MinCount = 5;
    public const int MaxCount = 21; // each cabinet costs draw calls and memory, and a Quest has to keep up

    /// <summary>The cabinet's footprint at 100 %, in metres (VrLobby's CabinetKit.Width and Length).</summary>
    public const double CabinetWidth = 0.74,
        CabinetLength = 1.40;

    /// <summary>How far the ring's cabinet fronts stand from you at the defaults (metres).</summary>
    public const double BaseRingFront = 2.1;

    /// <summary>The selected cabinet steps out of the ring to about arm's length (its front, in metres).</summary>
    public const double SelectedFront = 0.9;

    /// <summary>The angle between neighbours at the defaults.</summary>
    public static readonly double BaseStep = Deg(34);

    /// <summary>
    /// The last cabinet either side stops this far round, so the two ends of the ring don't meet behind you.
    /// It's where the default ring ends (5 × 34°).
    /// </summary>
    public static readonly double MaxSpread = Deg(170);

    /// <summary>The room's wall (VrLobby's room cylinder).</summary>
    public const double WallRadius = 7.0;

    /// <summary>
    /// Kept clear between the backs of the cabinets and the wall: the lamp row and its chrome edge sit 0.35–0.41 m
    /// behind the cabinets, and they mustn't touch the wall either.
    /// </summary>
    public const double WallClearance = 0.6;

    /// <summary>
    /// Room between neighbours' sides at their fronts, beyond the cabinets' own width: what the default ring
    /// leaves (2 × 2.1 m × sin 17° − 0.74 m ≈ 0.49 m), so a bigger cabinet keeps the same gap and the ring grows.
    /// </summary>
    public static readonly double Gap = 2 * BaseRingFront * Math.Sin(BaseStep / 2) - CabinetWidth;

    /// <summary>
    /// The two ends of the ring face each other across the gap behind you (5 × 34° either side leaves 20°). In the
    /// default ring their front corners already cross there, by about 25 cm, right behind you; other sizes and
    /// counts keep them no closer than that, relative to their width, so it never gets worse than it was.
    /// </summary>
    static readonly double EndsRatio =
        2 * BaseRingFront * Math.Sin((2 * Math.PI - 10 * BaseStep) / 2) / CabinetWidth;

    /// <summary>How far the ring moves back at a time while it's checked for neighbours touching.</summary>
    const double GrowStep = 0.02;

    static double Deg(double d) => d * Math.PI / 180;

    public static int ClampScale(int percent)
    {
        var p = Math.Clamp(percent, MinScalePercent, MaxScalePercent);
        // In whole steps, so a hand-edited 103 doesn't give a size Setup can't show.
        return (int)Math.Round(p / (double)ScaleStepPercent) * ScaleStepPercent;
    }

    /// <summary>An odd count in range: the selected cabinet stands in the middle, with as many either side.</summary>
    public static int ClampCount(int count)
    {
        var n = Math.Clamp(count, MinCount, MaxCount);
        return n % 2 == 0 ? n + 1 : n;
    }

    /// <summary>The counts Setup offers.</summary>
    public static IReadOnlyList<int> Counts { get; } =
    [.. Enumerable.Range(MinCount, MaxCount - MinCount + 1).Where(n => n % 2 == 1)];

    /// <summary>The sizes Setup offers, in percent.</summary>
    public static IReadOnlyList<int> Scales { get; } =
    [
        .. Enumerable
            .Range(0, (MaxScalePercent - MinScalePercent) / ScaleStepPercent + 1)
            .Select(i => MinScalePercent + i * ScaleStepPercent),
    ];

    /// <summary>
    /// The ring for <paramref name="count"/> cabinets at <paramref name="scalePercent"/> % (both clamped). More
    /// cabinets than fit at 34° apart move closer together round a bigger ring, so they still don't touch; if that
    /// would push them into the wall, fewer are shown (<see cref="LobbyRing.Count"/> says how many).
    /// </summary>
    public static LobbyRing Ring(int count, int scalePercent)
    {
        var scale = ClampScale(scalePercent) / 100.0;
        var width = CabinetWidth * scale;
        var pitch = width + Gap; // between neighbours' centres, at the front of the ring
        var maxFront = WallRadius - WallClearance - CabinetLength * scale;
        var side = (ClampCount(count) - 1) / 2;
        while (true)
        {
            var step = Math.Min(BaseStep, MaxSpread / side);
            var behind = 2 * Math.PI - 2 * side * step; // between the two ends, behind you
            var front = Math.Max(
                BaseRingFront,
                Math.Max(
                    pitch / (2 * Math.Sin(step / 2)),
                    EndsRatio * width / (2 * Math.Sin(behind / 2))
                )
            );
            var ring = new LobbyRing(side, step, front, scale);
            // The spacing above is measured at the cabinets' fronts; neighbours, turned and part-way through a
            // turn of the ring, are checked as they really stand, and the ring moves back until none touch.
            while (NeighboursTouch(ring) && ring.RingFront <= maxFront)
            {
                ring = ring with { RingFront = ring.RingFront + GrowStep };
            }
            // The smallest ring always fits (5 cabinets at 125 % stand 2.4 m away, the wall 7 m), so this ends.
            if (ring.RingFront <= maxFront || side <= (MinCount - 1) / 2)
            {
                return ring;
            }
            side--;
        }
    }

    /// <summary>
    /// True when two neighbouring cabinets share floor anywhere: standing still, or a quarter, half or three
    /// quarters of the way through a turn of the ring. (The two ends, behind you, are kept apart by <see cref="EndsRatio"/>.)
    /// </summary>
    public static bool NeighboursTouch(LobbyRing ring)
    {
        foreach (var scroll in new[] { 0, 0.25, 0.5, 0.75 })
        {
            (double X, double Z)[]? previous = null;
            for (var i = -ring.Side; i <= ring.Side; i++)
            {
                var feet = Footprint(ring, i - scroll);
                if (previous != null && Overlap(previous, feet))
                {
                    return true;
                }
                previous = feet;
            }
        }
        return false;
    }

    /// <summary>A cabinet's four corners on the floor, <paramref name="offset"/> steps round the ring.</summary>
    public static (double X, double Z)[] Footprint(LobbyRing ring, double offset)
    {
        var (x, z, yaw, _) = Place(ring, offset);
        var hw = CabinetWidth * ring.Scale / 2;
        var hl = ring.CabinetLength / 2;
        var (sin, cos) = Math.SinCos(yaw);
        var corners = new (double X, double Z)[4];
        var local = new[] { (-hw, -hl), (hw, -hl), (hw, hl), (-hw, hl) };
        for (var i = 0; i < 4; i++)
        {
            // A turn about the vertical, as Godot does it: x' = x cos + z sin, z' = −x sin + z cos.
            var (lx, lz) = local[i];
            corners[i] = (x + lx * cos + lz * sin, z - lx * sin + lz * cos);
        }
        return corners;
    }

    /// <summary>Separating-axis test for two convex quads: true when they overlap (touching edges don't count).</summary>
    static bool Overlap((double X, double Z)[] a, (double X, double Z)[] b)
    {
        foreach (var poly in new[] { a, b })
        {
            for (var i = 0; i < poly.Length; i++)
            {
                var (x1, z1) = poly[i];
                var (x2, z2) = poly[(i + 1) % poly.Length];
                double nx = z1 - z2,
                    nz = x2 - x1;
                double minA = double.MaxValue,
                    maxA = double.MinValue,
                    minB = double.MaxValue,
                    maxB = double.MinValue;
                foreach (var (x, z) in a)
                {
                    minA = Math.Min(minA, x * nx + z * nz);
                    maxA = Math.Max(maxA, x * nx + z * nz);
                }
                foreach (var (x, z) in b)
                {
                    minB = Math.Min(minB, x * nx + z * nz);
                    maxB = Math.Max(maxB, x * nx + z * nz);
                }
                if (maxA <= minB || maxB <= minA)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Where a cabinet stands, <paramref name="offset"/> steps round the ring from the selected one (fractional
    /// while the ring turns): its centre on the floor (x right, z towards you, you at the origin), the turn about
    /// the vertical in radians (0 faces you from straight ahead), and how far it has stepped forward (1 for the
    /// selected cabinet, 0 on the ring).
    /// </summary>
    public static (double X, double Z, double Yaw, double Focus) Place(
        LobbyRing ring,
        double offset
    )
    {
        var a = offset * ring.Step;
        var focus = Math.Clamp(1 - Math.Abs(offset), 0, 1);
        var front = ring.RingFront + (SelectedFront - ring.RingFront) * focus;
        var dist = front + ring.CabinetLength / 2;
        // Every cabinet's front faces you, wherever it stands on the ring (or part-way through a turn of it).
        return (Math.Sin(a) * dist, -Math.Cos(a) * dist, -a, focus);
    }

    /// <summary>"11 cabinets, 34° apart, 2.1 m from you": shown under the controls on Setup.</summary>
    public static string Describe(LobbyRing ring, int requestedCount)
    {
        var text =
            $"{ring.Count} cabinets, {Math.Round(ring.Step * 180 / Math.PI)}° apart, "
            + $"the ring {ring.RingFront:0.0} m from you.";
        return ClampCount(requestedCount) > ring.Count
            ? text + $" Only {ring.Count} fit in the room at this size."
            : text;
    }
}

/// <summary>
/// The lobby's ring of cabinets: <see cref="Side"/> either side of the selected one, <see cref="Step"/> radians
/// apart, their fronts <see cref="RingFront"/> metres from you, each drawn at <see cref="Scale"/> × its real size.
/// </summary>
public readonly record struct LobbyRing(int Side, double Step, double RingFront, double Scale)
{
    public int Count => Side * 2 + 1;

    /// <summary>How deep one cabinet is at this size.</summary>
    public double CabinetLength => LobbyLayout.CabinetLength * Scale;

    /// <summary>How far the backs of the ring's cabinets are from you.</summary>
    public double RingBack => RingFront + CabinetLength;

    public static LobbyRing Default =>
        LobbyLayout.Ring(LobbyLayout.DefaultCount, LobbyLayout.DefaultScalePercent);
}
