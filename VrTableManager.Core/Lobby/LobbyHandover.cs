namespace VrTableManager.Lobby;

/// <summary>What the headset is doing with the lobby, from its OpenXR session state.</summary>
public enum LobbyShown
{
    /// <summary>In the headset and getting input (FOCUSED).</summary>
    Focused,

    /// <summary>Still drawn in the headset, but another app has the input (VISIBLE).</summary>
    Visible,

    /// <summary>Not drawn any more (SYNCHRONIZED, STOPPING, IDLE …).</summary>
    Hidden,
}

/// <summary>
/// When the lobby's loading screen steps aside for a table that's starting. The table's own OpenXR session takes
/// the headset's focus as soon as it starts, which for VPX is before the table has finished loading. While the
/// runtime still draws the lobby (VISIBLE), the loading screen, video and all, stays up so you aren't left looking
/// at nothing; it steps aside once the lobby isn't drawn any more, or after <see cref="VisibleGrace"/> in any case,
/// so its sound can't go on under the table.
/// </summary>
public static class LobbyHandover
{
    /// <summary>Out of the headset for this long: the table has it.</summary>
    public const double HiddenGrace = 0.5;

    /// <summary>Still drawn, but not focused, for this long: step aside anyway.</summary>
    public const double VisibleGrace = 20;

    /// <param name="running">The table's engine is running (its status says so).</param>
    /// <param name="shown">The lobby's state in the headset now.</param>
    /// <param name="notFocusedFor">Seconds since the lobby lost focus while the table was running.</param>
    public static bool StepAside(bool running, LobbyShown shown, double notFocusedFor) =>
        running
        && shown != LobbyShown.Focused
        && notFocusedFor >= (shown == LobbyShown.Visible ? VisibleGrace : HiddenGrace);
}
