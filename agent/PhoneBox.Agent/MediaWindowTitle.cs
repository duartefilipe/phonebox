#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// The window title of the browser that is playing, so the Now playing screen widget can name the service and the
/// episode (see <see cref="MediaService"/>). Windows only: read from this computer's own windows, kept in memory, and
/// only for the player that holds the media session. Titles of anything else are never read.
/// </summary>
internal static class MediaWindowTitle
{
    private const int MaxTitle = 300;
    /// <summary>A title is read at most this often: enumerating the windows costs more than the 1 s media poll should.</summary>
    private const int CacheMs = 2000;

    /// <summary>At most this many windows are reported: a browser with more says enough in the first few.</summary>
    private const int MaxWindows = 12;

    private static readonly Lock Gate = new();
    private static string? _app;
    private static List<string>? _titles;
    private static long _readAt;

    /// <summary>
    /// The playing browser's window titles, newest-looking first; empty for anything else. The caller picks the one
    /// that names the service in play (see <see cref="MediaService.Describe"/>), which is why they all come back
    /// rather than a single guess.
    /// </summary>
    internal static IReadOnlyList<string> For(string? app)
    {
        if (!MediaService.IsBrowser(app)) return [];
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (_app == app && _titles is not null && now - _readAt < CacheMs) return _titles;
        }
        var titles = Read(app!);
        lock (Gate)
        {
            _app = app;
            _titles = titles;
            _readAt = now;
        }
        return titles;
    }

    internal static void Reset()
    {
        lock (Gate) { _app = null; _titles = null; _readAt = 0; }
    }

    /// <summary>The visible window titles of that browser's processes, longest first.</summary>
    private static List<string> Read(string app)
    {
        try
        {
            var wanted = ProcessNames(app);
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcesses())
                using (p)
                    try { if (wanted.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) pids.Add((uint)p.Id); }
                    catch { } // exited meanwhile
            if (pids.Count == 0) return [];
            var found = new List<string>();
            var text = new StringBuilder(MaxTitle + 1);
            EnumWindows((window, _) =>
            {
                try
                {
                    if (!IsWindowVisible(window)) return true;
                    GetWindowThreadProcessId(window, out var pid);
                    if (!pids.Contains(pid)) return true;
                    text.Clear();
                    if (GetWindowText(window, text, text.Capacity) <= 0) return true;
                    var title = text.ToString().Trim();
                    if (title.Length > 0) found.Add(title[..Math.Min(title.Length, MaxTitle)]);
                }
                catch { }
                return found.Count < MaxWindows * 4;
            }, 0);
            // longest first: a real window over a tooltip or an off-screen helper, for the fallback that takes the first
            return found.Distinct(StringComparer.Ordinal).OrderByDescending(t => t.Length).Take(MaxWindows).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>The executable names of a browser, as <see cref="MediaAppName.Friendly"/> named it.</summary>
    private static string[] ProcessNames(string app) => app.ToLowerInvariant() switch
    {
        "chrome" => ["chrome"],
        "edge" => ["msedge"],
        "firefox" => ["firefox"],
        "brave" => ["brave"],
        "opera" => ["opera", "opera_gx"],
        "vivaldi" => ["vivaldi"],
        "chromium" => ["chromium", "chrome"],
        _ => [app],
    };

    private delegate bool EnumWindowsProc(nint window, nint param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
}
#endif
