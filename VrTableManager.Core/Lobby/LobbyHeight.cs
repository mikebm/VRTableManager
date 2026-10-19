namespace VrTableManager.Lobby;

/// <summary>
/// The lobby's height adjustment, in metres. Positive raises you (the room looks lower),
/// negative lowers you. Pure maths so it's tested with plain dotnet.
/// </summary>
public static class LobbyHeight
{
    public const double Max = 0.6;        // ±60 cm is plenty to fix a wrong floor
    public const double Speed = 0.3;      // m/s at full stick
    public const double DeadZone = 0.2;
    public const double KeyStep = 0.02;   // desktop +/- keys

    public static double Clamp(double offset) => double.IsFinite(offset) ? Math.Clamp(offset, -Max, Max) : 0;

    /// <summary>New offset after holding the stick at <paramref name="stick"/> (-1..1, up = taller) for <paramref name="delta"/> seconds.</summary>
    public static double Step(double offset, double stick, double delta) =>
        Math.Abs(stick) < DeadZone ? offset : Clamp(offset + stick * Speed * delta);

    /// <summary>"Height +12 cm", "Height −5 cm", or "Height: default".</summary>
    public static string Format(double offset)
    {
        var cm = (int)Math.Round(offset * 100);
        return cm == 0 ? "Height: default" : $"Height {(cm > 0 ? "+" : "−")}{Math.Abs(cm)} cm";
    }
}
