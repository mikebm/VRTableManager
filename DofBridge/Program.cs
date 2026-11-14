using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DofBridge;

/// <summary>
/// Runs the player's DirectOutput (DOF) for the VR lobby, the way a front end does: DOF is set up with the player's
/// global config (GlobalConfig_B2SServer.xml, the one their tables use) and a front-end "ROM" (pinupmenu by default,
/// PinUP Popper's, so effects already set up for Popper's menu in the DOF Config Tool work in the lobby too).
///
/// <para>Started by the lobby with stdin / stdout redirected. Each line on stdin is a command:</para>
/// <list type="bullet">
/// <item><c>E 801</c>: pulses table element E801 (on, then off after <see cref="PulseMs"/>).</item>
/// <item><c>quit</c>: finishes DOF, turning every output off and letting go of the output boards, and exits.</item>
/// </list>
/// <para>stdin closing (the lobby quit or crashed) does the same as <c>quit</c>. Each line on stdout is news for the
/// lobby's log: <c>ready …</c> once DOF is running, <c>error …</c> when it can't be, <c>log …</c> otherwise.</para>
/// </summary>
static class Program
{
    /// <summary>How long a pulsed element stays on. DOF's own effects (durations, blinks) take it from there.</summary>
    const int PulseMs = 150;

    static readonly object Gate = new();
    static object? _pinball;
    static MethodInfo? _receive;

    static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        var dofDir = opts.TryGetValue("dof", out var d) ? d : "";
        var rom = opts.TryGetValue("rom", out var r) && r.Length > 0 ? r : "pinupmenu";
        var host = opts.TryGetValue("host", out var h) && h.Length > 0 ? h : "B2SServer";

        try
        {
            Start(dofDir, host, rom);
        }
        catch (Exception e)
        {
            Say("error " + Flatten(Inner(e)));
            // Exit for certain: threads a half-started DOF left running would otherwise keep this process alive.
            Environment.Exit(1);
            return 1;
        }

        // UTF-8, skipping a byte-order mark if the writer sends one: read in the console's code page, it would come
        // through as three stray characters in front of the first command.
        using var input = new StreamReader(
            Console.OpenStandardInput(),
            new UTF8Encoding(false),
            true
        );
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 1 && int.TryParse(parts[1], out var number))
            {
                Pulse(parts[0][0], number);
            }
            else if (line.Length > 0)
            {
                Say("log unknown command: " + line);
            }
        }

        Finish();
        // As above: the lobby waits for this process to end to know the boards are free.
        Environment.Exit(0);
        return 0;
    }

    static void Start(string dofDir, string host, string rom)
    {
        var dll = Path.Combine(dofDir, "DirectOutput.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("DirectOutput.dll not found in " + dofDir);
        }

        // DOF's own drivers (FTDI, libusb …) sit beside DirectOutput.dll, not beside this program, and relative paths
        // in a config are read from the working directory, as when VPX or a front end runs DOF from its folder.
        SetDllDirectory(dofDir);
        Directory.SetCurrentDirectory(dofDir);

        var asm = Assembly.LoadFrom(dll);
        var pinballType =
            asm.GetType("DirectOutput.Pinball", throwOnError: true)
            ?? throw new InvalidOperationException("No DirectOutput.Pinball");
        var config = GlobalConfigFile(asm, dofDir, host);

        var setup = Method(pinballType, "Setup", typeof(string), typeof(string), typeof(string));
        var init = Method(pinballType, "Init");
        var receive = Method(pinballType, "ReceiveData", typeof(char), typeof(int), typeof(int));
        Method(pinballType, "Finish"); // checked now: a DOF without it couldn't let go of the boards

        var pinball = Activator.CreateInstance(pinballType)!;
        setup.Invoke(pinball, new object[] { config ?? "", "", rom });
        init.Invoke(pinball, null);

        lock (Gate)
        {
            _pinball = pinball;
            _receive = receive;
        }

        var version = asm.GetName().Version?.ToString() ?? "?";
        Say(
            $"ready DOF {version}, rom {rom}, global config {(config ?? "none (DOF's defaults)")}, {(Environment.Is64BitProcess ? "64" : "32")}-bit"
        );
    }

    /// <summary>
    /// The global config DOF itself would use for <paramref name="host"/> (GlobalConfig_B2SServer.xml for the player's
    /// tables): DOF's own lookup when this DOF has it (DirectOutputHandler, R3++), otherwise the Config folder beside
    /// or above the DLL. Null when there's none, and DOF runs on its defaults.
    /// </summary>
    static string? GlobalConfigFile(Assembly asm, string dofDir, string host)
    {
        var handler = asm.GetType("DirectOutput.DirectOutputHandler");
        var lookup = handler?.GetMethod(
            "GetGlobalConfigFileName",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string) },
            null
        );
        if (lookup?.Invoke(null, new object[] { host }) is string found && File.Exists(found))
        {
            return Path.GetFullPath(found);
        }

        var name = "GlobalConfig_" + host + ".xml";
        foreach (
            var candidate in new[]
            {
                Path.Combine(dofDir, "Config", name),
                Path.Combine(dofDir, "..", "Config", name),
                Path.Combine(dofDir, name),
            }
        )
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    static void Pulse(char type, int number)
    {
        if (!Send(type, number, 1))
        {
            return;
        }

        // Kept in Pending until it fires: a timer nothing refers to can be collected before it does, leaving the
        // element on.
        var timer = default(Timer);
        timer = new Timer(
            _ =>
            {
                Send(type, number, 0);
                lock (Pending)
                {
                    Pending.Remove(timer!);
                }

                timer!.Dispose();
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite
        );
        lock (Pending)
        {
            Pending.Add(timer);
        }

        timer.Change(PulseMs, Timeout.Infinite); // started only once it's in Pending and `timer` is set
    }

    static readonly HashSet<Timer> Pending = new();

    /// <summary>A public method of DOF's Pinball class, or an error naming the one this DOF hasn't got.</summary>
    static MethodInfo Method(Type type, string name, params Type[] args) =>
        type.GetMethod(name, args)
        ?? throw new MissingMethodException(
            $"This DOF has no {type.Name}.{name}({string.Join(", ", args.Select(a => a.Name))}); it may be too old or too new for the VR lobby"
        );

    static bool Send(char type, int number, int value)
    {
        lock (Gate)
        {
            if (_pinball == null || _receive == null)
            {
                return false;
            }

            try
            {
                _receive.Invoke(_pinball, new object[] { type, number, value });
                return true;
            }
            catch (Exception e)
            {
                Say($"log {type}{number}={value} failed: {Flatten(Inner(e))}");
                return false;
            }
        }
    }

    /// <summary>Every output off and the boards let go, so the table's own DOF can open them.</summary>
    static void Finish()
    {
        lock (Gate)
        {
            if (_pinball == null)
            {
                return;
            }

            try
            {
                _pinball.GetType().GetMethod("Finish", Type.EmptyTypes)!.Invoke(_pinball, null);
                Say("log DOF finished");
            }
            catch (Exception e)
            {
                Say("log DOF finish failed: " + Flatten(Inner(e)));
            }

            _pinball = null;
            _receive = null;
        }
    }

    static Dictionary<string, string> ParseArgs(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i].Substring(2);
                opts[key] =
                    i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                        ? args[++i]
                        : "";
            }
        }

        return opts;
    }

    static Exception Inner(Exception e) =>
        e is TargetInvocationException { InnerException: { } inner } ? inner : e;

    static string Flatten(Exception e) =>
        $"{e.GetType().Name}: {e.Message}".Replace('\r', ' ').Replace('\n', ' ');

    static void Say(string line)
    {
        lock (Console.Out)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string lpPathName);
}
