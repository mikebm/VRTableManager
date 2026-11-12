using System.Runtime.InteropServices;
using System.Text;

namespace VrTableManager.Services;

/// <summary>
/// Waits for a button on a game controller or cabinet board (in joystick mode), for Setup → Controls. It reads them
/// with VPX's own SDL3 library, so a button comes back as VPX 10.8.1 writes it: <c>SDLJoy_&lt;SDL GUID&gt;_&lt;n&gt;;&lt;button&gt;</c>,
/// buttons from 0 and d-pad (hat) directions as <c>0x100 + hat × 4 + direction</c> (left, right, up, down).
/// <para>
/// The library is loaded from the VPX folder when waiting starts and let go when it stops, so VPX installed or
/// moved later is found, and the file is never held while VPX updates it. Every call is on the thread that opened it.
/// </para>
/// </summary>
public sealed class JoystickCapture : IDisposable
{
    const uint InitJoystick = 0x00000200;
    const byte HatUp = 1,
        HatRight = 2,
        HatDown = 4,
        HatLeft = 8;

    [StructLayout(LayoutKind.Sequential)]
    struct SdlGuid
    {
        public ulong A,
            B;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool SetHintFn(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool InitFn(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void QuitFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetJoysticksFn(out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void FreeFn(IntPtr p);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr OpenFn(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void CloseFn(IntPtr joystick);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate SdlGuid GuidFn(IntPtr joystick);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void GuidToStringFn(SdlGuid guid, IntPtr text, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr NameFn(IntPtr joystick);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int CountFn(IntPtr joystick);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool ButtonFn(IntPtr joystick, int button);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate byte HatFn(IntPtr joystick, int hat);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void UpdateFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetEventsFn([MarshalAs(UnmanagedType.U1)] bool enabled);

    readonly IntPtr _lib;
    readonly QuitFn _quit;
    readonly GetJoysticksFn _getJoysticks;
    readonly FreeFn _free;
    readonly OpenFn _open;
    readonly CloseFn _close;
    readonly GuidFn _guid;
    readonly GuidToStringFn _guidToString;
    readonly NameFn _name;
    readonly CountFn _buttons,
        _hats;
    readonly ButtonFn _button;
    readonly HatFn _hat;
    readonly UpdateFn _update;

    sealed class Pad
    {
        public required uint Id;
        public required IntPtr Handle;
        public required string Device; // SDLJoy_<guid>_<n>
        public required string Name;

        /// <summary>Buttons and d-pad directions held when waiting started (or since): they count once let go.</summary>
        public HashSet<int> Held = [];

        /// <summary>
        /// Until then, what's down is only noted: a just-opened controller reports its real state only after a few
        /// updates, and a button held (or a switch always on) mustn't count as pressed.
        /// </summary>
        public DateTime ReadyAt;
    }

    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    readonly List<Pad> _pads = [];
    DateTime _nextScan;
    bool _disposed;

    /// <summary>The controllers seen, by device (<c>SDLJoy_…_1</c>): their names, for the Setup tab.</summary>
    public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    JoystickCapture(IntPtr lib)
    {
        _lib = lib;
        T Fn<T>(string name)
            where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        var setHint = Fn<SetHintFn>("SDL_SetHint");
        var init = Fn<InitFn>("SDL_Init");
        _quit = Fn<QuitFn>("SDL_Quit");
        _getJoysticks = Fn<GetJoysticksFn>("SDL_GetJoysticks");
        _free = Fn<FreeFn>("SDL_free");
        _open = Fn<OpenFn>("SDL_OpenJoystick");
        _close = Fn<CloseFn>("SDL_CloseJoystick");
        _guid = Fn<GuidFn>("SDL_GetJoystickGUID");
        _guidToString = Fn<GuidToStringFn>("SDL_GUIDToString");
        _name = Fn<NameFn>("SDL_GetJoystickName");
        _buttons = Fn<CountFn>("SDL_GetNumJoystickButtons");
        _hats = Fn<CountFn>("SDL_GetNumJoystickHats");
        _button = Fn<ButtonFn>("SDL_GetJoystickButton");
        _hat = Fn<HatFn>("SDL_GetJoystickHat");
        _update = Fn<UpdateFn>("SDL_UpdateJoysticks");
        var eventsEnabled = Fn<SetEventsFn>("SDL_SetJoystickEventsEnabled");
        // The manager's window has the focus, not one of SDL's. No other hints: VPX sets none, so a controller gets
        // the same ID here as in VPX.
        setHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        var started = init(InitJoystick);
        try
        {
            if (!started)
            {
                throw new InvalidOperationException(
                    "VPX's SDL library couldn't start its joystick support."
                );
            }

            eventsEnabled(false); // states are read directly: nothing reads SDL's event queue, so don't fill it
            Scan();
        }
        catch
        {
            _quit(); // before the library is let go, with none of SDL's threads left
            throw;
        }
    }

    /// <summary>VPX's SDL3 library beside its exe (SDL364.dll in 64-bit builds), or null.</summary>
    public static string? FindLibrary(AppSettings s)
    {
        if (VpxSetup.FindExe(s) is not { } exe || Path.GetDirectoryName(exe) is not { } dir)
        {
            return null;
        }

        return new[] { "SDL364.dll", "SDL3.dll" }
            .Select(n => Path.Combine(dir, n))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Starts watching the controllers that are plugged in (and any plugged in meanwhile).</summary>
    /// <param name="error">Why it can't: no VPX yet, or its library wouldn't load.</param>
    public static JoystickCapture? TryOpen(AppSettings s, out string? error)
    {
        error = null;
        if (FindLibrary(s) is not { } path)
        {
            error =
                "Buttons on a controller or cabinet board need VPX installed first (Setup → Visual Pinball X).";
            return null;
        }

        IntPtr lib = IntPtr.Zero;
        try
        {
            lib = NativeLibrary.Load(path);
            return new JoystickCapture(lib);
        }
        catch (Exception e)
        {
            if (lib != IntPtr.Zero)
            {
                NativeLibrary.Free(lib);
            }

            error = "Couldn't read controllers through VPX's SDL library: " + e.Message;
            return null;
        }
    }

    /// <summary>How many controllers are plugged in.</summary>
    public int Count => _pads.Count;

    /// <summary>
    /// A button or d-pad direction pressed since the last call (one held when waiting started counts only once it's
    /// let go and pressed again), as VPX writes it, or null. Call it every few tens of milliseconds.
    /// </summary>
    public (string Binding, string Name)? Poll()
    {
        if (_disposed)
        {
            return null;
        }

        if (DateTime.UtcNow >= _nextScan)
        {
            Scan(); // plugged in or out since
        }

        _update();
        var now = DateTime.UtcNow;
        foreach (var pad in _pads)
        {
            var down = Down(pad);
            var pressed =
                now < pad.ReadyAt ? -1 : down.FirstOrDefault(c => !pad.Held.Contains(c), -1);
            pad.Held = down;
            if (pressed >= 0)
            {
                return ($"{pad.Device};{pressed}", pad.Name);
            }
        }
        return null;
    }

    /// <summary>
    /// Waiting starts again (after a capture or a cancel, with the library kept loaded): what's held now, or in the
    /// next moment, doesn't count until it's let go.
    /// </summary>
    public void Restart()
    {
        if (_disposed)
        {
            return;
        }

        var ready = DateTime.UtcNow + Settle;
        foreach (var pad in _pads)
        {
            pad.ReadyAt = ready;
        }
    }

    static readonly byte[] HatDirections = [HatLeft, HatRight, HatUp, HatDown]; // VPX's order

    HashSet<int> Down(Pad pad)
    {
        var down = new HashSet<int>();
        var buttons = _buttons(pad.Handle);
        for (var b = 0; b < buttons; b++)
        {
            if (_button(pad.Handle, b))
            {
                down.Add(b);
            }
        }

        var hats = _hats(pad.Handle);
        for (var h = 0; h < hats; h++)
        {
            var v = _hat(pad.Handle, h);
            var dirs = HatDirections;
            for (var d = 0; d < 4; d++)
            {
                if ((v & dirs[d]) != 0)
                {
                    down.Add(0x100 + h * 4 + d);
                }
            }
        }
        return down;
    }

    /// <summary>
    /// Opens the controllers plugged in, numbering ones that share a GUID 1, 2… in SDL's order, as VPX does.
    /// </summary>
    void Scan()
    {
        _nextScan = DateTime.UtcNow.AddSeconds(1);
        var list = _getJoysticks(out var count);
        var ids = new List<uint>();
        if (list != IntPtr.Zero)
        {
            for (var i = 0; i < count; i++)
            {
                ids.Add((uint)Marshal.ReadInt32(list, i * 4));
            }

            _free(list);
        }

        foreach (var gone in _pads.Where(p => !ids.Contains(p.Id)).ToList())
        {
            _close(gone.Handle);
            _pads.Remove(gone);
        }

        var perGuid = new Dictionary<string, int>();
        var pads = new List<Pad>();
        foreach (var id in ids)
        {
            var pad = _pads.FirstOrDefault(p => p.Id == id);
            if (pad == null)
            {
                var handle = _open(id);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                pad = new Pad
                {
                    Id = id,
                    Handle = handle,
                    Device = "",
                    Name = Marshal.PtrToStringUTF8(_name(handle)) is { Length: > 0 } name
                        ? name
                        : "Controller",
                };
                pad.ReadyAt = DateTime.UtcNow + Settle; // a button held now doesn't count until it's pressed again
            }

            var guid = Guid(pad.Handle);
            var n = perGuid[guid] = perGuid.GetValueOrDefault(guid) + 1;
            pad.Device = $"SDLJoy_{guid}_{n}";
            Names[pad.Device] = pad.Name;
            pads.Add(pad);
        }
        _pads.Clear();
        _pads.AddRange(pads);
    }

    string Guid(IntPtr handle)
    {
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            _guidToString(_guid(handle), buffer, 64);
            return (Marshal.PtrToStringUTF8(buffer) ?? "").ToLowerInvariant();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            foreach (var pad in _pads)
            {
                _close(pad.Handle);
            }

            _pads.Clear();
        }
        finally
        {
            _quit(); // all of SDL, so none of its threads outlive the library
            NativeLibrary.Free(_lib);
        }
    }
}
