namespace VrTableManager.Lobby;

/// <summary>
/// When the lobby asks Windows for the keyboard back after a table closes (or when it starts again). Windows often
/// refuses a background window, and helper windows (backglass, DMD) can grab it while they close, so one try isn't
/// enough: the lobby tries every <see cref="Interval"/> until it has held the window for <see cref="Settled"/>,
/// or gives up after <see cref="Window"/>. Pure timing, so it's tested with plain dotnet.
/// </summary>
public sealed class FocusRetry
{
    public const double Interval = 0.25;
    public const double Window = 10; // seconds after a table closes; later, losing it is the player's doing
    public const double Settled = 2; // seconds held in a row: nothing that was closing took it away

    double _left = -1,
        _wait,
        _held;

    public bool Active => _left >= 0;

    /// <summary>How many times <see cref="Tick"/> said to try since <see cref="Start"/>.</summary>
    public int Attempts { get; private set; }

    /// <summary>True when it stopped because time ran out without the window settling in front.</summary>
    public bool GaveUp { get; private set; }

    public void Start()
    {
        _left = Window;
        _wait = _held = 0;
        Attempts = 0;
        GaveUp = false;
    }

    /// <summary>A table is starting: it must get the keyboard, so stop taking it back.</summary>
    public void Stop() => _left = -1;

    /// <summary>
    /// Call every frame with whether the lobby's window is in front. True means try to bring it there now.
    /// </summary>
    public bool Tick(double delta, bool inFront)
    {
        if (_left < 0)
        {
            return false;
        }

        _left -= delta;
        if (inFront)
        {
            _held += delta;
            if (_held >= Settled)
            {
                _left = -1;
            }

            return false;
        }

        _held = 0;
        if (_left < 0)
        {
            GaveUp = true;
            return false;
        }

        _wait -= delta;
        if (_wait > 0)
        {
            return false;
        }

        _wait = Interval;
        Attempts++;
        return true;
    }
}
