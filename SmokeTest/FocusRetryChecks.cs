using VrTableManager.Lobby;

/// <summary>The lobby taking the keyboard back after a table (FocusRetry): every time, retried, and letting go.</summary>
static class FocusRetryChecks
{
    /// <summary>Runs <paramref name="seconds"/> of 60 fps frames; returns how many said to try.</summary>
    static int Run(FocusRetry f, double seconds, Func<double, bool> inFront)
    {
        var tries = 0;
        for (var t = 0.0; t < seconds; t += 1.0 / 60)
        {
            if (f.Tick(1.0 / 60, inFront(t)))
            {
                tries++;
            }
        }
        return tries;
    }

    public static IEnumerable<(bool, string)> Run()
    {
        var f = new FocusRetry();
        yield return (
            !f.Active && !f.Tick(0.1, false),
            "lobby keyboard: does nothing until a table closes"
        );

        f.Start();
        yield return (f.Tick(1.0 / 60, false), "lobby keyboard: tries on the first frame");

        // Windows keeps saying no: it retries a few times a second, then gives up and says so.
        f.Start();
        var tries = Run(f, FocusRetry.Window + 1, _ => false);
        yield return (
            tries >= 30 && tries <= 45 && !f.Active && f.GaveUp,
            $"lobby keyboard: retries about 4 times a second, then gives up ({tries} tries)"
        );

        // In front from the start: no tries, and done once it's held there.
        f.Start();
        tries = Run(f, FocusRetry.Settled + 0.1, _ => true);
        yield return (
            tries == 0 && !f.Active && !f.GaveUp,
            "lobby keyboard: stops once the window stays in front"
        );

        // A closing helper window takes it away again after a second: it's taken back.
        f.Start();
        tries = Run(f, 6, t => t < 1 || t > 1.6);
        yield return (
            tries > 0 && !f.Active && !f.GaveUp,
            "lobby keyboard: takes it back when something grabs it soon after"
        );

        // The second table, and the third: each return starts afresh (the first return isn't the only one).
        for (var round = 2; round <= 3; round++)
        {
            f.Start();
            yield return (
                f.Active && f.Tick(1.0 / 60, false) && f.Attempts == 1,
                $"lobby keyboard: tries again after table {round}"
            );
        }

        // A table starting: it stops at once, so the table keeps the keyboard.
        f.Start();
        f.Stop();
        yield return (
            !f.Tick(1.0 / 60, false) && !f.Active,
            "lobby keyboard: a table starting stops it"
        );
    }
}
