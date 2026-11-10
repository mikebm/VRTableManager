using VrTableManager.Lobby;

/// <summary>
/// The VR lobby's ring at every cabinet size and count Setup offers (LobbyLayout): the defaults keep the old ring,
/// and placed as the lobby places them, no two cabinets overlap and none reaches the wall.
/// </summary>
static class LobbyLayoutChecks
{
    /// <summary>A cabinet's footprint on the floor: its four corners, from <see cref="LobbyLayout.Place"/>.</summary>
    static (double X, double Z)[] Footprint(LobbyRing ring, double offset)
    {
        var (x, z, yaw, _) = LobbyLayout.Place(ring, offset);
        var hw = LobbyLayout.CabinetWidth * ring.Scale / 2;
        var hl = ring.CabinetLength / 2;
        var (sin, cos) = Math.SinCos(yaw);
        // Godot's turn about Y: x' = x cos + z sin, z' = −x sin + z cos.
        return
        [
            .. new[] { (-hw, -hl), (hw, -hl), (hw, hl), (-hw, hl) }.Select(c =>
                (x + c.Item1 * cos + c.Item2 * sin, z - c.Item1 * sin + c.Item2 * cos)
            ),
        ];
    }

    /// <summary>How far apart the two end cabinets' front centres are, in cabinet widths.</summary>
    static double EndsGap(LobbyRing ring)
    {
        var behind = 2 * Math.PI - 2 * ring.Side * ring.Step;
        return 2 * ring.RingFront * Math.Sin(behind / 2) / (LobbyLayout.CabinetWidth * ring.Scale);
    }

    /// <summary>Separating-axis test for two convex quads: true when they share any floor.</summary>
    static bool Overlap((double X, double Z)[] a, (double X, double Z)[] b)
    {
        foreach (var poly in new[] { a, b })
        {
            for (var i = 0; i < poly.Length; i++)
            {
                var (x1, z1) = poly[i];
                var (x2, z2) = poly[(i + 1) % poly.Length];
                var (nx, nz) = (z1 - z2, x2 - x1); // the edge's normal
                double Min((double X, double Z)[] p) => p.Min(q => q.X * nx + q.Z * nz);
                double Max((double X, double Z)[] p) => p.Max(q => q.X * nx + q.Z * nz);
                if (Max(a) <= Min(b) || Max(b) <= Min(a))
                {
                    return false;
                }
            }
        }
        return true;
    }

    public static List<(bool, string)> Run()
    {
        var results = new List<(bool, string)>();
        void Check(bool ok, string name) => results.Add((ok, name));

        var def = LobbyRing.Default;
        Check(
            def.Count == 11
                && Math.Abs(def.Step - Math.PI * 34 / 180) < 1e-9
                && Math.Abs(def.RingFront - 2.1) < 1e-9
                && def.Scale == 1,
            "lobby layout: the defaults are the old ring (11 cabinets, 34° apart, 2.1 m away, real size)"
        );
        Check(
            LobbyLayout.ClampCount(0) == 5
                && LobbyLayout.ClampCount(6) == 7
                && LobbyLayout.ClampCount(11) == 11
                && LobbyLayout.ClampCount(99) == 21,
            "lobby layout: cabinet counts are odd and 5–21"
        );
        Check(
            LobbyLayout.ClampScale(10) == 75
                && LobbyLayout.ClampScale(102) == 100
                && LobbyLayout.ClampScale(500) == 125,
            "lobby layout: sizes are 75–125 % in 5 % steps"
        );
        Check(
            LobbyLayout.Counts.SequenceEqual([5, 7, 9, 11, 13, 15, 17, 19, 21])
                && LobbyLayout.Scales.First() == 75
                && LobbyLayout.Scales.Last() == 125
                && LobbyLayout.Scales.Count == 11,
            "lobby layout: Setup offers every count and size"
        );

        var overlaps = new List<string>();
        var walls = new List<string>();
        var fewer = new List<string>();
        var ends = new List<string>();
        foreach (var count in LobbyLayout.Counts)
        {
            foreach (var pct in LobbyLayout.Scales)
            {
                var ring = LobbyLayout.Ring(count, pct);
                var name = $"{count} at {pct}%";
                if (ring.Count < count)
                {
                    fewer.Add($"{name} → {ring.Count}");
                }
                if (
                    ring.Count % 2 == 0
                    || ring.Count > count
                    || ring.Count < LobbyLayout.MinCount
                    || ring.Side * ring.Step > LobbyLayout.MaxSpread + 1e-9
                    || ring.RingFront < LobbyLayout.BaseRingFront - 1e-9
                )
                {
                    overlaps.Add(name + " (bad ring)");
                }
                // The lamp row and its chrome edge stand 0.41 m behind the cabinets, inside the wall.
                if (ring.RingBack + 0.41 >= LobbyLayout.WallRadius)
                {
                    walls.Add(name);
                }
                // The two ends face each other behind you: the default ring's already cross there (by about
                // 25 cm, as they always have), so other rings only have to keep them no closer than that.
                if (EndsGap(ring) < EndsGap(def) - 1e-9)
                {
                    ends.Add(name);
                }
                // Still and part-way through a turn: every pair of neighbours.
                foreach (var scroll in new[] { 0, 0.25, 0.5, 0.75 })
                {
                    var offsets = Enumerable
                        .Range(-ring.Side, ring.Count)
                        .Select(i => i - scroll)
                        .ToList();
                    var feet = offsets.Select(o => Footprint(ring, o)).ToList();
                    for (var i = 0; i + 1 < feet.Count; i++)
                    {
                        var next = i + 1;
                        if (Overlap(feet[i], feet[next]))
                        {
                            overlaps.Add($"{name} at {offsets[i]:0.##} / {offsets[next]:0.##}");
                        }
                    }
                }
            }
        }
        Check(
            overlaps.Count == 0,
            "lobby layout: no neighbouring cabinets overlap at any size and count, still or turning"
                + (overlaps.Count > 0 ? ": " + string.Join(", ", overlaps.Take(6)) : "")
        );
        Check(
            ends.Count == 0,
            "lobby layout: the two ends behind you are never closer than in the default ring"
                + (ends.Count > 0 ? ": " + string.Join(", ", ends.Take(6)) : "")
        );
        Check(
            walls.Count == 0,
            "lobby layout: the ring and its lamps stay inside the wall"
                + (walls.Count > 0 ? ": " + string.Join(", ", walls) : "")
        );
        // Big cabinets in a full ring don't all fit in the room; fewer are shown, and Setup says so.
        var big = LobbyLayout.Ring(21, 125);
        Check(
            big.Count < 21
                && fewer.Count > 0
                && LobbyLayout.Describe(big, 21).Contains($"Only {big.Count} fit"),
            $"lobby layout: 21 cabinets at 125 % become {big.Count}, and Setup says only that many fit"
        );
        Check(
            LobbyLayout.Ring(21, 100).Count == 21
                && LobbyLayout.Ring(21, 100).RingFront > def.RingFront,
            "lobby layout: 21 real-size cabinets all fit, round a bigger ring"
        );
        Check(
            LobbyLayout.Describe(def, 11) == "11 cabinets, 34° apart, the ring 2.1 m from you.",
            "lobby layout: Setup's summary of the default ring"
        );
        return results;
    }
}
