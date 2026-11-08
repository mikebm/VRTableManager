using Godot;
using VrTableManager.Lobby;

namespace VrLobby;

/// <summary>
/// Godot key events as SDL scancodes, the codes VPX stores for its controls (<see cref="CabinetButtons"/>).
/// Uses the physical key, so a cabinet board's Left Shift is Left Shift whatever the keyboard layout.
/// </summary>
public static class CabinetKeys
{
    public static int? ToSdl(InputEventKey k)
    {
        var key = k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;
        var right = k.Location == KeyLocation.Right;
        return key switch
        {
            >= Key.A and <= Key.Z => 4 + (int)(key - Key.A),
            >= Key.Key1 and <= Key.Key9 => 30 + (int)(key - Key.Key1),
            Key.Key0 => 39,
            >= Key.F1 and <= Key.F12 => 58 + (int)(key - Key.F1),
            >= Key.Kp1 and <= Key.Kp9 => 89 + (int)(key - Key.Kp1),
            Key.Kp0 => 98,
            Key.Shift => right ? CabinetButtons.Sdl.RShift : CabinetButtons.Sdl.LShift,
            Key.Ctrl => right ? CabinetButtons.Sdl.RCtrl : CabinetButtons.Sdl.LCtrl,
            Key.Alt => right ? 230 : CabinetButtons.Sdl.LAlt,
            Key.Meta => right ? 231 : 227,
            Key.Enter => CabinetButtons.Sdl.Enter,
            Key.KpEnter => 88,
            Key.Escape => 41,
            Key.Backspace => 42,
            Key.Tab => 43,
            Key.Space => 44,
            Key.Minus => 45,
            Key.Equal => 46,
            Key.Bracketleft => 47,
            Key.Bracketright => 48,
            Key.Backslash => 49,
            Key.Semicolon => 51,
            Key.Apostrophe => 52,
            Key.Quoteleft => 53,
            Key.Comma => 54,
            Key.Period => 55,
            Key.Slash => 56,
            Key.Capslock => 57,
            Key.Insert => 73,
            Key.Home => 74,
            Key.Pageup => 75,
            Key.Delete => 76,
            Key.End => 77,
            Key.Pagedown => 78,
            Key.Right => 79,
            Key.Left => 80,
            Key.Down => 81,
            Key.Up => 82,
            Key.KpDivide => 84,
            Key.KpMultiply => 85,
            Key.KpSubtract => 86,
            Key.KpAdd => 87,
            Key.KpPeriod => 99,
            _ => null,
        };
    }
}
