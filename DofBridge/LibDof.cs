using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DofBridge;

/// <summary>
/// libdof, the native C++ port of DOF that VPX 10.8.1 and later ship as their DOF plugin (plugins\dof\dof64.dll). It
/// exports C++ classes only, so they're called by their decorated (MSVC) names: on x64 a member function is a plain
/// function taking the object first. Its <c>DOF::DOF</c> object holds one pointer; the memory given it is generous.
///
/// <para>libdof reads its config from <c>&lt;base path&gt;\directoutputconfig</c> (GlobalConfig_B2SServer.xml, the
/// cabinet's XML and the Config Tool's .ini files), the base path being VPX's DOF Path setting, else VPX's preferences
/// folder: the same one VPX's plugin gives it, so tables and lobby share one config.</para>
/// </summary>
sealed class LibDof : IDisposable
{
    const string Ctor = "??0DOF@0@QEAA@XZ";
    const string Dtor = "??1DOF@0@QEAA@XZ";
    const string InitName = "?Init@DOF@1@QEAAXPEBD0@Z";
    const string DataReceiveName = "?DataReceive@DOF@1@QEAAXDHH@Z";
    const string FinishName = "?Finish@DOF@1@QEAAXXZ";
    const string ConfigInstance = "?GetInstance@Config@DOF@@SAPEAV12@XZ";
    const string ConfigSetBasePath = "?SetBasePath@Config@DOF@@QEAAXPEBD@Z";
    const string ConfigSetLogLevel = "?SetLogLevel@Config@DOF@@QEAAXW4DOF_LogLevel@@@Z";
    const string ConfigSetLogCallback =
        "?SetLogCallback@Config@DOF@@QEAAXP6AXW4DOF_LogLevel@@PEBDPEAD@Z@Z";

    /// <summary>Far more than DOF::DOF's one pointer, so a later libdof with a field or two more still fits.</summary>
    const int ObjectBytes = 256;

    // DOF_LogLevel
    const int LogInfo = 0,
        LogWarn = 1,
        LogError = 2,
        LogDebug = 3;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr ThisFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ThisVoidFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void InitFn(IntPtr self, byte[] tableFilename, byte[] romName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void DataReceiveFn(IntPtr self, byte type, int number, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr StaticFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetStringFn(IntPtr self, byte[] value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetIntFn(IntPtr self, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetPtrFn(IntPtr self, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void LogFn(int level, IntPtr format, IntPtr args);

    readonly IntPtr _module;
    readonly IntPtr _dof;
    readonly DataReceiveFn _dataReceive;
    readonly ThisVoidFn _finish;
    readonly ThisVoidFn _dtor;

    // Kept for as long as libdof may call it: a collected delegate would crash it on its next log line.
    static LogFn? _logCallback;
    static Action<string>? _logTarget;

    bool _finished;

    /// <summary>Loads libdof from <paramref name="folder"/> and starts it with the config under <paramref name="basePath"/>.</summary>
    public LibDof(string folder, string basePath, string rom, Action<string> log)
    {
        var dll = Path.Combine(folder, "dof64.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("dof64.dll not found in " + folder);
        }

        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("VPX's DOF plugin is 64-bit only");
        }

        // Its drivers (hidapi, libusb, libftdi …) sit beside it.
        Native.SetDllDirectory(folder);
        _module = Native.LoadLibraryEx(dll, IntPtr.Zero, Native.LOAD_WITH_ALTERED_SEARCH_PATH);
        if (_module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "couldn't load " + dll);
        }

        var config = Fn<StaticFn>(ConfigInstance)();
        _logTarget = log;
        _logCallback = OnLog;
        Fn<SetPtrFn>(ConfigSetLogCallback)(
            config,
            Marshal.GetFunctionPointerForDelegate(_logCallback)
        );
        Fn<SetIntFn>(ConfigSetLogLevel)(config, LogInfo);
        Fn<SetStringFn>(ConfigSetBasePath)(config, Str(basePath));

        var init = Fn<InitFn>(InitName);
        _dataReceive = Fn<DataReceiveFn>(DataReceiveName);
        _finish = Fn<ThisVoidFn>(FinishName);
        _dtor = Fn<ThisVoidFn>(Dtor);
        var ctor = Fn<ThisFn>(Ctor);

        _dof = Marshal.AllocHGlobal(ObjectBytes);
        for (var i = 0; i < ObjectBytes; i++)
        {
            Marshal.WriteByte(_dof, i, 0);
        }

        ctor(_dof);
        // No table file: the lobby is a front end, known to DOF by its ROM name only (as .NET DOF's Setup gets "").
        init(_dof, Str(""), Str(rom));
    }

    public void DataReceive(char type, int number, int value) =>
        _dataReceive(_dof, (byte)type, number, value);

    /// <summary>Every output off and the boards let go.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _finish(_dof);
    }

    public void Dispose()
    {
        Finish();
        _dtor(_dof);
        Marshal.FreeHGlobal(_dof);
        // The library stays loaded: its threads may still be winding down, and the process is about to exit.
    }

    T Fn<T>(string name)
        where T : Delegate
    {
        var p = Native.GetProcAddress(_module, name);
        if (p == IntPtr.Zero)
        {
            throw new MissingMethodException(
                $"VPX's DOF plugin has no {name}; it may be too old or too new for the VR lobby"
            );
        }

        return (T)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    /// <summary>A NUL-terminated string in the ANSI code page, as libdof's narrow paths are read on Windows.</summary>
    static byte[] Str(string s)
    {
        var bytes = Encoding.Default.GetBytes(s);
        Array.Resize(ref bytes, bytes.Length + 1);
        return bytes;
    }

    static void OnLog(int level, IntPtr format, IntPtr args)
    {
        if (level == LogDebug || _logTarget == null)
        {
            return;
        }

        try
        {
            var buffer = new byte[2048];
            var n = Native._vsnprintf(buffer, (UIntPtr)(buffer.Length - 1), format, args);
            var text = Encoding.Default.GetString(buffer, 0, n < 0 ? buffer.Length - 1 : n);
            var tag = level switch
            {
                LogWarn => "warning: ",
                LogError => "error: ",
                _ => "",
            };
            _logTarget("log libdof " + tag + text.Replace('\r', ' ').Replace('\n', ' ').TrimEnd());
        }
        catch
        {
            // a log line must never take DOF down
        }
    }

    static class Native
    {
        public const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Ansi,
            ExactSpelling = true,
            SetLastError = true
        )]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int _vsnprintf(
            byte[] buffer,
            UIntPtr count,
            IntPtr format,
            IntPtr args
        );
    }
}
