#if WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// The video of the player that is playing, for the Now playing screen widget: the picture of the player's window,
/// cropped to the video itself, sent to the phone as H.264 (<see cref="StreamAsync"/>) or, for a panel that cannot
/// decode it, as JPEG pictures (<see cref="FrameAsync"/>).
/// </summary>
/// <remarks>
/// <para>Windows only, and only while a widget with the option on is on an enabled page that the phone is showing: the
/// capture starts with the first request or stream and stops a few seconds after the last one, so nothing is captured
/// when the panel is on another page or asleep. With <c>liveWhilePlaying</c> (the default) a paused player stops it.</para>
/// <para>The window is read with Windows.Graphics.Capture (<see cref="WindowCapture"/>), on the graphics card, at the
/// player's own frame rate; <c>PrintWindow</c> remains for a Windows too old for it. What is sent is only the video:
/// where the browser extension says it is, else the part of the window that keeps moving (<see cref="MotionCrop"/>),
/// else the whole window. Each stream has its own encoder, so a viewer that joins starts on a key frame at once.</para>
/// <para>The window is found by its handle, not its title: a browser's title changes with every tab.</para>
/// </remarks>
public sealed class PlayerCapture : IDisposable
{
    public const string WidgetType = "nowplaying";
    /// <summary>Frames a second the JPEG pictures may come at (<see cref="MediaService"/>, so the rules are testable).</summary>
    public const int MinFps = MediaService.MinFps, MaxFps = MediaService.MaxFps, DefaultFps = MediaService.DefaultFps;
    /// <summary>The JPEG picture's width sent to the phone; taller than wide is scaled to fit the same pixel count.</summary>
    internal const int Width = 960;
    /// <summary>Stops this long after the last JPEG request (the page left the screen, the panel went to sleep).</summary>
    internal const long KeepMs = 4000;
    /// <summary>A JPEG picture older than this is not shown as live.</summary>
    internal const long FreshMs = 3000;
    /// <summary>How long a JPEG request waits for the next grab (<see cref="MediaService.FrameWaitMs"/>, so it is testable).</summary>
    internal const int WaitMs = MediaService.FrameWaitMs;
    /// <summary>JPEG quality: a video frame carries noise, so a lower one saves much and costs little.</summary>
    private const long Quality = 55;
    /// <summary>The window's layout (client area, the page's place, its title) is read again this often.</summary>
    private const long LayoutEveryMs = 400;
    /// <summary>The motion grid is updated this often (<see cref="MotionCrop"/> counts on about 10 a second).</summary>
    private const long MotionEveryMs = 100;

    private readonly Lock _gate = new();
    private readonly ILogger<PlayerCapture> _log;
    private readonly Func<long> _now;
    private Thread? _worker;
    private volatile bool _disposed;

    private nint _window;             // the window being captured (0 = none)
    private int _jpegFps = DefaultFps;
    private bool _jpegCrop = true;
    private long _jpegWantedUntil;
    private long _jpegNextDue;
    private byte[]? _frame;
    private long _frameAt, _frameSeq;
    private string _state = Starting;
    private readonly Dictionary<string, long> _served = new(StringComparer.Ordinal);
    private readonly List<VideoClient> _clients = [];
    /// <summary>Poked on every JPEG grab, so the requests waiting for a picture wake the moment one exists.</summary>
    private readonly FrameSignal _signal = new();

    public PlayerCapture(ILogger<PlayerCapture> log) : this(log, null) { }

    internal PlayerCapture(ILogger<PlayerCapture> log, Func<long>? now)
    {
        _log = log;
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>States the panel knows: it falls back to the cover art for anything but "live".</summary>
    internal const string Idle = "idle", NoWindow = "no-window", Off = "off", Starting = "starting", JpegOnly = "jpeg-only",
        NoEncoder = "no-encoder";

    /// <summary>
    /// Whether this widget may have the picture now, and which window holds it: 200, or a status and one of the states
    /// above. Points the capture at the player's window as a side effect.
    /// </summary>
    private (int Status, string State, WidgetConfig? Widget) Check(AgentConfig cfg, MediaSnapshot? media, string? deviceId, string? widgetKey)
    {
        if (PanelWidgets.Find(cfg, deviceId, widgetKey, WidgetType) is not { } widget) return (404, NoWindow, null);
        // every widget keeps to its own option: another one with live video on does not open this one's
        if (widget.Bool("live") is false) return (403, Off, widget);
        var onlyPlaying = widget.Bool("liveWhilePlaying") != false;
        if (media is null || (onlyPlaying && !media.Playing)) return (503, Idle, widget);
        // the service and the track name the window to mirror: a browser's other windows must not be picked
        var window = PlayerWindow.For(media.App, media.ServiceName, media.ShowTitle, media.Title);
        if (window == 0) return (503, NoWindow, widget);
        lock (_gate)
        {
            if (_disposed) return (503, Starting, widget);
            if (window != _window)
            {
                _window = window;
                _frame = null;
                _state = Starting;
                foreach (var c in _clients) c.NeedKey = true;
            }
        }
        return (200, "live", widget);
    }

    // ---------------------------------------------------------------- H.264 stream

    /// <summary>
    /// The video as one long response (<see cref="VideoWire"/>): a configuration, then H.264 access units as they are
    /// encoded, until the phone hangs up or the picture goes away (a state message says why, and the stream ends).
    /// </summary>
    public async Task StreamAsync(HttpContext ctx, Func<AgentConfig> cfg, Func<MediaSnapshot?> media, string? deviceId,
        string? widgetKey, CancellationToken ct)
    {
        var res = ctx.Response;
        res.Headers.CacheControl = "no-store";
        res.Headers.XContentTypeOptions = "nosniff";
        var (status, state, widget) = Check(cfg(), media(), deviceId, widgetKey);
        if (status == 200 && !MediaService.AllowsVideo(widget)) (status, state) = (409, JpegOnly);
        if (status != 200)
        {
            if (status == 503 && state == Idle) Stop();
            res.StatusCode = status;
            await res.WriteAsJsonAsync(new { state }, ct);
            return;
        }

        var client = new VideoClient((deviceId ?? "") + "\n" + widgetKey, MediaService.VideoRateOf(widget),
            MediaService.VideoBoxOf(widget), MediaService.CropsToVideo(widget));
        lock (_gate)
        {
            _clients.Add(client);
            StartLocked();
        }
        try
        {
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            res.ContentType = VideoWire.ContentType;
            await res.StartAsync(ct);
            var lastWrite = _now();
            var lastCheck = _now();
            while (!ct.IsCancellationRequested && !_disposed)
            {
                await client.WaitAsync(250, ct);
                var wrote = false;
                while (client.TryTake(out var message))
                {
                    await res.Body.WriteAsync(message, ct);
                    wrote = true;
                }
                var now = _now();
                if (client.Failed)
                {
                    await res.Body.WriteAsync(VideoWire.Json(VideoWire.State, new { state = NoEncoder }), ct);
                    await res.Body.FlushAsync(ct);
                    break;
                }
                if (now - lastCheck >= 500)
                {
                    lastCheck = now;
                    var (s, st, w) = Check(cfg(), media(), deviceId, widgetKey);
                    if (s == 200 && !MediaService.AllowsVideo(w)) (s, st) = (409, JpegOnly);
                    if (s != 200)
                    {
                        await res.Body.WriteAsync(VideoWire.Json(VideoWire.State, new { state = st }), ct);
                        await res.Body.FlushAsync(ct);
                        break;
                    }
                    client.Update(MediaService.VideoRateOf(w), MediaService.VideoBoxOf(w), MediaService.CropsToVideo(w));
                }
                if (!wrote && now - lastWrite >= 1000)
                {
                    // a paused player sends no pictures: this keeps the phone's transport from giving up on the stream
                    await res.Body.WriteAsync(VideoWire.Message(VideoWire.Ping, false, 0, []), ct);
                    wrote = true;
                }
                if (wrote)
                {
                    await res.Body.FlushAsync(ct);
                    lastWrite = now;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // the phone hung up
        finally
        {
            client.Closed = true; // the capture thread lets go of its encoder
        }
    }

    // ---------------------------------------------------------------- JPEG pictures

    /// <summary>
    /// The next JPEG picture for the widget the phone names: the newest one it has not been sent, waiting up to
    /// <see cref="WaitMs"/> for one to be grabbed. Answering as the frames are made is what keeps them evenly spaced.
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
    /// The newest JPEG picture of the player, for the widget the phone names ("pageId/widgetId"). 200 with a JPEG, 204
    /// when this widget already has the newest one, or a status with one of the states above.
    /// </summary>
    public (int Status, byte[]? Jpeg, string State) Frame(AgentConfig cfg, MediaSnapshot? media, string? deviceId, string? widgetKey, bool fresh = false)
    {
        var (status, state, widget) = Check(cfg, media, deviceId, widgetKey);
        if (status != 200)
        {
            if (state is Idle or NoWindow) Stop();
            return (status, null, state);
        }
        var fps = MediaService.RateOf(widget);
        var now = _now();
        lock (_gate)
        {
            if (fps != _jpegFps) { _jpegFps = fps; _frame = null; }
            _jpegCrop = MediaService.CropsToVideo(widget);
            _jpegWantedUntil = now + KeepMs;
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

    /// <summary>Stops the JPEG pictures (a stream ends on its own when its phone hangs up).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _jpegWantedUntil = 0;
            _frame = null;
            _served.Clear();
            if (_clients.Count == 0) _window = 0;
        }
    }

    private void StartLocked()
    {
        if (_worker is not null) return;
        _worker = new Thread(Loop) { IsBackground = true, Name = "PhoneBox player capture" };
        _worker.Start();
    }

    // ---------------------------------------------------------------- the capture thread

    private sealed class Work
    {
        public IFrameSource? Source;
        public nint Window;
        public WindowGeometry.Layout? Layout;
        public long LayoutAt, MotionAt;
        public readonly MotionCrop Motion = new();
        public readonly byte[] Grid = new byte[MotionCrop.Cols * MotionCrop.Rows];
        public PixelRect? Crop;
        public string CropBy = "window";
        public long Start = Stopwatch.GetTimestamp();
        public byte[] Nv12 = [];
        public readonly List<EncodedFrame> Encoded = [];
        public long LastPicture;
    }

    /// <summary>Reads the window while someone is watching; ends a moment after they stop.</summary>
    private void Loop()
    {
        var w = new Work();
        TimeBeginPeriod(1); // 4 ms naps, not the default 15.6 ms: the pictures are taken close to when they are drawn
        try
        {
            while (!_disposed)
            {
                nint window;
                VideoClient[] clients;
                bool jpegDue;
                var now = _now();
                lock (_gate)
                {
                    foreach (var gone in _clients.Where(c => c.Closed).ToList()) { gone.Dispose(); _clients.Remove(gone); }
                    if (_clients.Count == 0 && now > _jpegWantedUntil) { _worker = null; return; } // nobody is watching
                    window = _window;
                    clients = [.. _clients];
                    jpegDue = now <= _jpegWantedUntil && now >= _jpegNextDue;
                }
                if (window == 0) { Thread.Sleep(50); continue; }
                if (!EnsureSource(w, window)) { Thread.Sleep(250); continue; }

                w.Source!.Poll();
                var due = clients.Where(c => c.Due(now)).ToArray();
                if (due.Length > 0 || jpegDue)
                {
                    var read = false;
                    try
                    {
                        unsafe
                        {
                            read = w.Source.TryRead((bgra, stride, width, height) =>
                                Process(w, bgra, stride, width, height, now, due, jpegDue));
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug("player capture: {Msg}", ex.Message);
                        DropSource(w);
                    }
                    if (read) w.LastPicture = now;
                    else if (now - w.LastPicture > 5000 && w.Source is { Closed: true }) DropSource(w);
                }
                // a hardware encoder finishes a picture some milliseconds after taking it: send it on as soon as it is out
                foreach (var c in clients) c.Poll(w.Encoded);
                Thread.Sleep(4);
            }
        }
        catch (Exception ex) { _log.LogDebug("player capture: {Msg}", ex.Message); }
        finally
        {
            TimeEndPeriod(1);
            DropSource(w);
            lock (_gate)
            {
                _worker = null;
                foreach (var c in _clients) c.DisposeEncoder();
            }
        }
    }

    private bool EnsureSource(Work w, nint window)
    {
        if (w.Source is not null && w.Window == window && !w.Source.Closed) return true;
        DropSource(w);
        w.Window = window;
        w.Motion.Reset();
        w.Crop = null;
        try
        {
            w.Source = WindowCapture.Supported ? new GraphicsSource(WindowCapture.Start(window)) : new PrintSource(window);
        }
        catch (Exception ex)
        {
            _log.LogDebug("player capture: Windows.Graphics.Capture unavailable ({Msg}); drawing the window instead", ex.Message);
            w.Source = new PrintSource(window);
        }
        w.LastPicture = _now();
        return true;
    }

    private static void DropSource(Work w)
    {
        try { w.Source?.Dispose(); } catch { }
        w.Source = null;
        w.Layout = null;
    }

    /// <summary>One picture of the window: where the video is in it, then each due viewer's frame and the JPEG.</summary>
    private unsafe void Process(Work w, byte* bgra, int stride, int width, int height, long now, VideoClient[] due, bool jpegDue)
    {
        if (now - w.LayoutAt >= LayoutEveryMs || w.Layout is null)
        {
            w.Layout = WindowGeometry.Of(w.Window);
            w.LayoutAt = now;
        }
        if (now - w.MotionAt >= MotionEveryMs)
        {
            MotionCrop.Sample(new ReadOnlySpan<byte>(bgra, stride * height), stride, width, height, w.Grid);
            w.Motion.Update(w.Grid, width, height);
            w.MotionAt = now;
        }
        var full = new PixelRect(0, 0, width & ~1, height & ~1);
        var video = VideoArea(w, width, height, now) ?? full;
        // a crop that moves by a few pixels stays where it was: every change of size restarts the encoder
        if (w.Crop is not { } kept || !kept.Near(video, 6)) w.Crop = video;
        var crop = w.Crop.Value;

        var timeUs = (long)Stopwatch.GetElapsedTime(w.Start).TotalMicroseconds;
        (PixelRect Rect, int W, int H)? converted = null;
        foreach (var c in due)
        {
            c.Taken(now);
            var area = c.Crop ? crop : full;
            var (ow, oh) = VideoCrop.Fit(area.Width, area.Height, c.Box.Width, c.Box.Height);
            if (ow < 16 || oh < 16) continue;
            if (!c.EnsureEncoder(ow, oh, _log)) continue;
            var size = Nv12.Size(ow, oh);
            if (converted is not { } done || done.Rect != area || done.W != ow || done.H != oh)
            {
                if (w.Nv12.Length < size) w.Nv12 = new byte[size];
                fixed (byte* dst = w.Nv12) Nv12.FromBgra(bgra, stride, area, dst, ow, oh);
                converted = (area, ow, oh);
            }
            c.Encode(w.Nv12.AsSpan(0, size), timeUs, w.Encoded, c.Crop ? w.CropBy : "window");
        }

        if (jpegDue)
        {
            var jpeg = Jpeg(bgra, stride, width, height, _jpegCrop ? crop : full);
            lock (_gate)
            {
                _jpegNextDue = now + 1000 / Math.Clamp(_jpegFps, MinFps, MaxFps) - 2;
                _frame = jpeg;
                _frameAt = _now();
                _frameSeq++;
                _state = "live";
            }
            // the waiting requests take this picture now: their spacing becomes the capture's own, which is even
            _signal.Poke();
        }
    }

    /// <summary>Where the video is: the extension's word for a browser tab, else the part that moves, else nothing.</summary>
    private static PixelRect? VideoArea(Work w, int width, int height, long now)
    {
        if (w.Layout is { } layout)
        {
            var client = w.Source is PrintSource ? new PixelRect(0, 0, layout.Client.Width, layout.Client.Height) : layout.Client;
            var viewport = layout.Viewport is { } v && w.Source is PrintSource
                ? new PixelRect(v.X - layout.Client.X, v.Y - layout.Client.Y, v.Width, v.Height) : layout.Viewport;
            if (VideoReports.For(layout.Title, now) is { } report
                && VideoCrop.FromReport(report, client, viewport, layout.ClientOnScreen, width, height, w.Motion.Score) is { } rect)
            {
                w.CropBy = "extension";
                return rect;
            }
        }
        if (w.Motion.Crop is { } moving)
        {
            w.CropBy = "motion";
            return moving.Clamp(width, height).Even();
        }
        w.CropBy = "window";
        return null;
    }

    private static unsafe byte[]? Jpeg(byte* bgra, int stride, int width, int height, PixelRect area)
    {
        try
        {
            using var src = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, (nint)bgra);
            var scale = Math.Min(1.0, Width / (double)Math.Max(area.Width, area.Height * 16 / 9));
            var tw = Math.Max(16, (int)Math.Round(area.Width * scale));
            var th = Math.Max(16, (int)Math.Round(area.Height * scale));
            using var small = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, tw, th), new Rectangle(area.X, area.Y, area.Width, area.Height), GraphicsUnit.Pixel);
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
        lock (_gate)
        {
            _jpegWantedUntil = 0;
            foreach (var c in _clients) c.Closed = true;
        }
        _signal.Poke(); // no frame is coming: let the waiting requests answer instead of holding on
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);

    // ---------------------------------------------------------------- one viewer of the stream

    /// <summary>
    /// One phone's stream: its own encoder (so it starts on a key frame), its pace, and the messages waiting to be
    /// written. A phone that falls more than a second behind loses what is queued and starts again from a key frame,
    /// rather than watching an ever older picture.
    /// </summary>
    private sealed class VideoClient(string key, int fps, (int Width, int Height) box, bool crop)
    {
        public string Key { get; } = key;
        public int Fps { get; private set; } = fps;
        public (int Width, int Height) Box { get; private set; } = box;
        public bool Crop { get; private set; } = crop;
        public volatile bool Closed;
        public volatile bool Failed;
        public volatile bool NeedKey;

        private H264Encoder? _encoder;
        private long _nextDue;
        private bool _configSent, _waitKey, _encoderBroken;
        private string _cropBy = "window";
        private readonly ConcurrentQueue<byte[]> _queue = new();
        private readonly SemaphoreSlim _ready = new(0);
        private int _queued;

        public void Update(int fps, (int Width, int Height) box, bool crop)
        {
            Fps = fps;
            Box = box;
            Crop = crop;
        }

        /// <summary>A frame for this viewer is due (a hair early, so a source at exactly its rate is never skipped).</summary>
        public bool Due(long now) => !Closed && !Failed && now >= _nextDue;

        public void Taken(long now)
        {
            var interval = 1000 / Math.Max(1, Fps);
            _nextDue = Math.Max(_nextDue + interval, now + interval - interval / 4);
        }

        public bool EnsureEncoder(int width, int height, ILogger log)
        {
            if (_encoder is { } e && e.Width == width && e.Height == height && e.Fps == Fps) return true;
            DisposeEncoder();
            try
            {
                _encoder = H264Encoder.Create(width, height, Fps, MediaService.VideoBitrate(width, height, Fps), !_encoderBroken, log);
                _configSent = false;
                NeedKey = false;
                return true;
            }
            catch (Exception ex) when (!_encoderBroken)
            {
                // one more go with the software encoder before giving up: a graphics driver may refuse this size
                log.LogDebug("video: {Msg}", ex.Message);
                _encoderBroken = true;
                return EnsureEncoder(width, height, log);
            }
            catch (Exception ex)
            {
                log.LogWarning("video: no H.264 encoder ({Msg}); the panel shows JPEG pictures", ex.Message);
                Failed = true;
                Wake();
                return false;
            }
        }

        public void Encode(ReadOnlySpan<byte> nv12, long timeUs, List<EncodedFrame> encoded, string cropBy)
        {
            if (_encoder is not { } e) return;
            if (NeedKey)
            {
                NeedKey = false;
                if (!e.RequestKeyFrame()) { DisposeEncoder(); return; } // the next picture makes a new encoder, which starts on a key
            }
            _cropBy = cropBy;
            encoded.Clear();
            try { e.Encode(nv12, timeUs, encoded); }
            catch (Exception)
            {
                // a hardware encoder that fails mid-stream: the software one takes over at the next picture
                _encoderBroken = true;
                DisposeEncoder();
                return;
            }
            Emit(e, encoded);
        }

        /// <summary>Sends on what a hardware encoder finished since the last look (called every few milliseconds).</summary>
        public void Poll(List<EncodedFrame> encoded)
        {
            if (_encoder is not { } e || Closed) return;
            encoded.Clear();
            try { e.Poll(encoded); }
            catch (Exception)
            {
                _encoderBroken = true;
                DisposeEncoder();
                return;
            }
            Emit(e, encoded);
        }

        private void Emit(H264Encoder e, List<EncodedFrame> encoded)
        {
            var cropBy = _cropBy;
            foreach (var f in encoded)
            {
                if (!_configSent)
                {
                    if (!f.Key) continue; // a decoder can only start on a key frame
                    Send(VideoWire.Json(VideoWire.Config, new
                    {
                        codec = VideoWire.CodecOf(f.Data) ?? "avc1.4D401F",
                        width = e.Width,
                        height = e.Height,
                        encoder = e.Name,
                        hardware = e.Hardware,
                        crop = cropBy,
                    }));
                    _configSent = true;
                }
                if (_waitKey && !f.Key) continue;
                _waitKey = false;
                Send(VideoWire.Message(VideoWire.Chunk, f.Key, f.TimeUs, f.Data));
            }
        }

        private void Send(byte[] message)
        {
            if (Interlocked.Increment(ref _queued) > Math.Max(15, Fps))
            {
                // too far behind: what is queued is dropped, and the picture resumes at the next key frame
                while (_queue.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
                _waitKey = true;
                NeedKey = true;
                if (message[0] == VideoWire.Chunk) { Interlocked.Decrement(ref _queued); return; }
            }
            _queue.Enqueue(message);
            Wake();
        }

        private void Wake()
        {
            if (_ready.CurrentCount == 0) _ready.Release();
        }

        public async Task WaitAsync(int ms, CancellationToken ct)
        {
            try { await _ready.WaitAsync(ms, ct); }
            catch (OperationCanceledException) { }
        }

        public bool TryTake(out byte[] message)
        {
            if (_queue.TryDequeue(out message!)) { Interlocked.Decrement(ref _queued); return true; }
            return false;
        }

        public void DisposeEncoder()
        {
            _encoder?.Dispose();
            _encoder = null;
            _configSent = false;
        }

        public void Dispose()
        {
            DisposeEncoder();
            _ready.Dispose();
        }
    }
}

/// <summary>Where the capture's pictures come from.</summary>
internal interface IFrameSource : IDisposable
{
    bool Closed { get; }
    /// <summary>Lets go of pictures that are no longer the newest, so a free buffer is always there for the next one.</summary>
    void Poll();
    bool TryRead(FrameReader read);
}

/// <summary>Windows.Graphics.Capture: the compositor's picture of the window.</summary>
internal sealed class GraphicsSource(WindowCapture capture) : IFrameSource
{
    public bool Closed => capture.Closed;
    public void Poll() => capture.Poll();
    public bool TryRead(FrameReader read) => capture.TryRead(read);
    public void Dispose() => capture.Dispose();
}

/// <summary>
/// For a Windows without Windows.Graphics.Capture: the window asked to draw its client area into a bitmap, as before.
/// Slower, and it makes some players stutter, so it is only the fallback.
/// </summary>
internal sealed class PrintSource(nint window) : IFrameSource
{
    public bool Closed { get; private set; }
    public void Poll() { }

    public unsafe bool TryRead(FrameReader read)
    {
        if (!PlayerWindow.ClientSize(window, out var w, out var h) || w < 16 || h < 16) { Closed = true; return false; }
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                // PW_CLIENTONLY | PW_RENDERFULLCONTENT: the hardware-composed content too, and the window need not be in front
                if (!PlayerWindow.PrintWindow(window, hdc, PlayerWindow.RenderFullContent)) { Closed = true; return false; }
            }
            finally { g.ReleaseHdc(hdc); }
        }
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { read((byte*)data.Scan0, data.Stride, w, h); }
        finally { bmp.UnlockBits(data); }
        return true;
    }

    public void Dispose() { }
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
