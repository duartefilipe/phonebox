using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// Why the phone is about to lose this agent, so the panel can show its "PC off" page at once instead of after the
/// unreachable delay. "shutdown" (Windows ends the session: shut down, restart, sign out), "sleep" (Windows suspends)
/// and "stop" (the agent itself was quit; the computer stays on). The current reason rides in every state; the stream
/// also sends it as a "bye" event right before it closes. Null when nothing is known.
/// </summary>
public static class PowerSignal
{
    public const string Shutdown = "shutdown", Sleep = "sleep", Stop = "stop";

    /// <summary>
    /// Windows sends no event when a shutdown is cancelled (an app refused to close) and none when a resume is missed,
    /// so a reason older than this no longer counts: an agent still running after it was not going anywhere.
    /// </summary>
    internal const long ExpiryMs = 60_000;

    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;
    /// <summary>Whether the operating system itself is shutting down (Linux: systemd says "stopping"); replaceable in tests.</summary>
    internal static Func<bool> SystemStopping { get; set; } = LinuxSystemStopping;

    private static readonly object Gate = new();
    private static string? _reason;
    private static long _at;
    private static volatile bool _quit;

    /// <summary>The reason while it is fresh, for the phone's state.</summary>
    public static string? Current
    {
        get { lock (Gate) return _reason is not null && Clock() - _at < ExpiryMs ? _reason : null; }
    }

    public static void Set(string? reason)
    {
        lock (Gate)
        {
            if (_reason == reason) { _at = Clock(); return; }
            _reason = reason;
            _at = Clock();
        }
        StateSignal.Poke();
    }

    /// <summary>The user quit the agent (tray or menu bar): the computer stays on.</summary>
    public static void Quitting() => _quit = true;

    /// <summary>
    /// The reason sent in the stream's last message, when the agent stops: a shutdown or sleep already announced, then a
    /// quit or restart the agent started itself, then a shutdown the operating system started. Null when the agent
    /// cannot tell (a console Ctrl+C, a service manager stopping it, or macOS, which says nothing either way).
    /// </summary>
    public static string? LeaveReason()
    {
        if (Current is { } reason and not Stop) return reason;
        if (_quit || AppRestart.Requested) return Stop;
        if (AdbReverse.SessionEnding) return Shutdown;
        try { if (SystemStopping()) return Shutdown; } catch { }
        return null;
    }

    internal static void Reset() { lock (Gate) { _reason = null; _at = 0; } _quit = false; }

    private static bool LinuxSystemStopping()
    {
        if (!OperatingSystem.IsLinux()) return false;
        using var p = Process.Start(new ProcessStartInfo("systemctl", "is-system-running")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        });
        if (p is null) return false;
        var read = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(1000)) { try { p.Kill(); } catch { } return false; }
        return read.Result.Trim() == "stopping";
    }
}

/// <summary>Turns Windows session and power events into <see cref="PowerSignal"/> reasons.</summary>
public sealed class PowerWatch(ILogger<PowerWatch> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
#if WINDOWS
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => { log.LogInformation("the session is ending: telling the phone"); PowerSignal.Set(PowerSignal.Shutdown); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) { log.LogInformation("the computer is going to sleep: telling the phone"); PowerSignal.Set(PowerSignal.Sleep); }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume) PowerSignal.Set(null);
        };
#else
        log.LogDebug("session and power events are followed on Windows only");
#endif
        return Task.CompletedTask;
    }
}
