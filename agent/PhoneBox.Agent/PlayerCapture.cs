#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// The picture of the player that is playing, for the Now playing screen widget's live video. The window of the media
/// session's own app is copied with <c>PrintWindow</c> and sent on as JPEG frames, the way the Camera widget sends a
/// camera's.
/// </summary>
/// <remarks>
/// <para>Windows only, and only while a widget with the option on is on an enabled page that the phone is showing: the
/// capture starts on the first request and stops a few seconds after the last one, so nothing is captured when the
/// panel is on another page or asleep. With <c>onlyWhilePlaying</c> (the default) a paused player stops it too.</para>
/// <para>The window is found by its handle, not its title: a browser's title changes with every tab, and a capture
/// keyed on it would die as soon as you switched tabs.</para>
/// </remarks>
public sealed class PlayerCapture : IDisposable
{
    public const string WidgetType = "nowplaying";
    /// <summary>Frames a second the widget may ask for (<see cref="MediaService"/>, so the rules are testable).</summary>
    public const int MinFps = MediaService.MinFps, MaxFps = MediaService.MaxFps, DefaultFps = MediaService.DefaultFps;
    /// <summary>The picture's width sent to the phone; taller than wide is scaled to fit the same pixel count.</summary>
    internal const int Width = 960;
    /// <summary>Stops this long after the last request (the page left the screen, the panel went to sleep).</summary>
    internal const long KeepMs = 4000;
    /// <summary>A frame older than this is not shown as live.</summary>
    internal const long FreshMs = 3000;
    /// <summary>How long a request waits for the next frame (<see cref="MediaService.FrameWaitMs"/>, so it is testable).</summary>
    internal const int WaitMs = MediaService.FrameWaitMs;
    /// <summary>JPEG quality: a video frame carries noise, so a lower one saves much and costs little.</summary>
    private const long Quality = 55;

    private readonly Lock _gate = new();
    private readonly ILogger<PlayerCapture> _log;
    private readonly Func<long> _now;
    private Thread? _worker;
    private volatile bool _disposed;

    private nint _window;             // the window being captured (0 = none)
    private int _fps = DefaultFps;
    private long _wantedUntil;
    private byte[]? _frame;
    private long _frameAt, _frameSeq;
    private string _state = "starting";
    private readonly Dictionary<string, long> _served = new(StringComparer.Ordinal);
    /// <summary>Poked on every grab, so the requests waiting for a frame wake the moment one exists.</summary>
    private readonly FrameSignal _signal = new();

    public PlayerCapture(ILogger<PlayerCapture> log) : this(log, null) { }

    internal PlayerCapture(ILogger<PlayerCapture> log, Func<long>? now)
    {
        _log = log;
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>States the panel knows: it falls back to the cover art for anything but "live".</summary>
    internal const string Idle = "idle", NoWindow = "no-window", Off = "off", Starting = "starting";

    /// <summary>
    /// The next picture for the widget the phone names: the newest one it has not been sent, waiting up to
    /// <see cref="WaitMs"/> for one to be grabbed. Answering as the frames are made is what keeps them evenly spaced;
    /// a panel that picks its own moment to ask beats against the capture's clock and the picture judders.
    /// </summary>
    public async Task<(int Status, byte[]? Jpeg, string State)> FrameAsync(AgentConfig cfg, Func<MediaSnapshot?> media,
        string? deviceId, string? widgetKey, bool fresh, CancellationToken ct)
    {
        var answer = Frame(cfg, media(), deviceId, widgetKey, fresh);
        if (answer.Status != 204) return answer;
        // nothing new for this widget yet: hold the request until the next grab instead of sending it away empty
        try { if (!await _signal.WaitAsync(WaitMs, ct)) return answer; }
        catch (OperationCanceledException) { return answer; }
        return Frame(cfg, media(), deviceId, widgetKey, fresh);
    }

    /// <summary>
    /// The newest picture of the player, for the widget the phone names ("pageId/widgetId"). 200 with a JPEG, 204 when
    /// this widget already has the newest one, or a status with one of the states above.
    /// </summary>
    public (int Status, byte[]? Jpeg, string State) Frame(AgentConfig cfg, MediaSnapshot? media, string? deviceId, string? widgetKey, bool fresh = false)
    {
        if (PanelWidgets.Find(cfg, deviceId, widgetKey, WidgetType) is not { } widget) return (404, null, NoWindow);
        // every widget keeps to its own option: another one with live video on does not open this one's
        if (widget.Bool("live") is false) return (403, null, Off);
        var fps = MediaService.RateOf(widget);
        var onlyPlaying = widget.Bool("liveWhilePlaying") != false;
        if (media is null || (onlyPlaying && !media.Playing)) { Stop(); return (503, null, Idle); }

        var now = _now();
        lock (_gate)
        {
            if (_disposed) return (503, null, Starting);
            // the service and the track name the window to mirror: a browser's other windows must not be picked
            var window = PlayerWindow.For(media.App, media.ServiceName, media.ShowTitle, media.Title);
            if (window == 0) { StopLocked(); return (503, null, NoWindow); }
            if (window != _window || fps != _fps)
            {
                _window = window;
                _fps = fps;
                _frame = null;
                _state = Starting;
            }
            _wantedUntil = now + KeepMs;
            StartLocked();
            if (_frame is { } jpeg && now - _frameAt <= FreshMs)
            {
                var asker = (deviceId ?? "") + "\n" + widgetKey;
                if (!fresh && _served.TryGetValue(asker, out var had) && had == _frameSeq) return (204, null, "live");
                if (_served.Count > 64) _served.Clear();
                _served[asker] = _frameSeq;
                return (200, jpeg, "live");
            }
            return (503, null, _state);
        }
    }

    public void Stop() { lock (_gate) StopLocked(); }

    private void StopLocked()
    {
        _wantedUntil = 0;
        _window = 0;
        _frame = null;
        _served.Clear();
    }

    private void StartLocked()
    {
        if (_worker is not null) return;
        _worker = new Thread(Loop) { IsBackground = true, Name = "PhoneBox player capture" };
        _worker.Start();
    }

    /// <summary>Grabs the window at the asked rate while someone is asking; ends a moment after they stop.</summary>
    private void Loop()
    {
        try
        {
            while (!_disposed)
            {
                nint window;
                int fps;
                lock (_gate)
                {
                    if (_now() > _wantedUntil) { _worker = null; return; } // nobody is watching: the thread ends
                    window = _window;
                    fps = _fps;
                }
                if (window == 0) { Thread.Sleep(200); continue; }
                var started = Stopwatch.GetTimestamp();
                var jpeg = Grab(window);
                lock (_gate)
                {
                    if (jpeg is null)
                    {
                        // the window went away (closed, or the app stopped drawing): the panel shows the cover again
                        if (_state == "live") _log.LogDebug("player capture: no picture from the window");
                        _state = NoWindow;
                        _frame = null;
                    }
                    else
                    {
                        _frame = jpeg;
                        _frameAt = _now();
                        _frameSeq++;
                        _state = "live";
                    }
                }
                // the waiting requests take this frame now: their spacing becomes the capture's own, which is even
                _signal.Poke();
                var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var wait = (int)Math.Max(1, 1000.0 / Math.Clamp(fps, MinFps, MaxFps) - spent);
                Thread.Sleep(wait);
            }
        }
        catch (Exception ex) { _log.LogDebug("player capture: {Msg}", ex.Message); }
        finally { lock (_gate) _worker = null; }
    }

    /// <summary>One frame of a window as JPEG, or null when it could not be drawn.</summary>
    private static byte[]? Grab(nint window)
    {
        try
        {
            if (!PlayerWindow.ClientSize(window, out var w, out var h) || w < 16 || h < 16) return null;
            var scale = Math.Min(1.0, Width / (double)w);
            var tw = Math.Max(16, (int)Math.Round(w * scale));
            var th = Math.Max(16, (int)Math.Round(h * scale));

            using var full = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try
                {
                    // PW_RENDERFULLCONTENT: the hardware-composed content too (a browser's video is drawn that way),
                    // and the window need not be in front
                    if (!PlayerWindow.PrintWindow(window, hdc, PlayerWindow.RenderFullContent)) return null;
                }
                finally { g.ReleaseHdc(hdc); }
            }
            using var small = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(full, 0, 0, tw, th);
            }
            using var ms = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            using var quality = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Quality);
            parameters.Param[0] = quality;
            small.Save(ms, JpegEncoder.Value, parameters);
            return ms.ToArray();
        }
        catch (Exception) { return null; }
    }

    private static readonly Lazy<ImageCodecInfo> JpegEncoder = new(() =>
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid));

    public void Dispose()
    {
        _disposed = true;
        lock (_gate) StopLocked();
        _signal.Poke(); // no frame is coming: let the waiting requests answer instead of holding on
    }
}

/// <summary>Finding the window of the app that holds the media session, by handle rather than by title.</summary>
internal static class PlayerWindow
{
    internal const uint RenderFullContent = 3;

    /// <summary>
    /// The window the track is playing in. A browser has several windows, and the one with the video is not simply the
    /// biggest: a wide settings window beat a 1920×1080 Netflix one and the panel mirrored the wrong thing. So the
    /// window whose title names what is playing (the service, or the track) is preferred, and only with nothing to go
    /// on does the biggest stand in.
    /// </summary>
    /// <param name="app">The player's friendly name (<see cref="MediaAppName.Friendly"/>).</param>
    /// <param name="hints">Words that name what is playing: the service and the track's title.</param>
    internal static nint For(string? app, params string?[] hints)
    {
        if (string.IsNullOrWhiteSpace(app)) return 0;
        try
        {
            var names = ProcessNames(app);
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcesses())
                using (p)
                    try { if (names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) pids.Add((uint)p.Id); }
                    catch { }
            if (pids.Count == 0) return 0;

            var wanted = hints.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.Trim().ToLowerInvariant())
                .Where(h => h.Length >= 3).ToArray();
            nint best = 0, named = 0;
            long bestArea = 0, namedArea = 0;
            var text = new StringBuilder(320);
            EnumWindows((window, _) =>
            {
                try
                {
                    if (!IsWindowVisible(window) || IsIconic(window)) return true;
                    GetWindowThreadProcessId(window, out var pid);
                    if (!pids.Contains(pid)) return true;
                    if (!ClientSize(window, out var w, out var h)) return true;
                    long area = (long)w * h;
                    if (area > bestArea) { bestArea = area; best = window; }
                    if (wanted.Length == 0) return true;
                    text.Clear();
                    if (GetWindowText(window, text, text.Capacity) <= 0) return true;
                    var title = text.ToString().ToLowerInvariant();
                    if (wanted.Any(title.Contains) && area > namedArea) { namedArea = area; named = window; }
                }
                catch { }
                return true;
            }, 0);
            return named != 0 ? named : best;
        }
        catch (Exception) { return 0; }
    }

    internal static bool ClientSize(nint window, out int width, out int height)
    {
        width = height = 0;
        if (!GetClientRect(window, out var r)) return false;
        width = r.Right - r.Left;
        height = r.Bottom - r.Top;
        return width > 0 && height > 0;
    }

    /// <summary>The executable names of a player, as <see cref="MediaAppName.Friendly"/> named it.</summary>
    private static string[] ProcessNames(string app) => app.ToLowerInvariant() switch
    {
        "chrome" => ["chrome"],
        "edge" => ["msedge"],
        "firefox" => ["firefox"],
        "brave" => ["brave"],
        "opera" => ["opera", "opera_gx"],
        "vivaldi" => ["vivaldi"],
        "chromium" => ["chromium", "chrome"],
        "vlc" => ["vlc"],
        "media player" => ["Microsoft.Media.Player", "wmplayer"],
        "spotify" => ["Spotify"],
        _ => [app],
    };

    private delegate bool EnumWindowsProc(nint window, nint param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint window, nint hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
}
#endif
