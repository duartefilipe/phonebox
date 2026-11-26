using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>A rectangle in pixels of a captured window (origin at the window's top-left).</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    /// <summary>The part of this rectangle inside a <paramref name="width"/> × <paramref name="height"/> picture.</summary>
    public PixelRect Clamp(int width, int height)
    {
        var x = Math.Clamp(X, 0, width);
        var y = Math.Clamp(Y, 0, height);
        return new(x, y, Math.Clamp(Right, x, width) - x, Math.Clamp(Bottom, y, height) - y);
    }

    public PixelRect Intersect(PixelRect o)
    {
        var x = Math.Max(X, o.X);
        var y = Math.Max(Y, o.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, o.Right) - x), Math.Max(0, Math.Min(Bottom, o.Bottom) - y));
    }

    /// <summary>Even origin and size, as 4:2:0 video needs; it shrinks inward so it never leaves the original.</summary>
    public PixelRect Even()
    {
        var x = (X + 1) & ~1;
        var y = (Y + 1) & ~1;
        return new(x, y, Math.Max(0, (Right - x) & ~1), Math.Max(0, (Bottom - y) & ~1));
    }

    /// <summary>Close enough to be the same crop: no edge moved by more than <paramref name="slack"/> pixels.</summary>
    public bool Near(PixelRect o, int slack) =>
        Math.Abs(X - o.X) <= slack && Math.Abs(Y - o.Y) <= slack && Math.Abs(Right - o.Right) <= slack && Math.Abs(Bottom - o.Bottom) <= slack;
}

/// <summary>
/// What the PhoneBox browser extension says about the video playing in a tab (extras/browser-extension): where its
/// picture is in the page, so the Now playing screen can show the video alone instead of the whole browser window.
/// Rectangles are in CSS pixels of the tab's viewport; <see cref="Dpr"/> turns them into the screen's pixels.
/// </summary>
internal sealed record VideoReport(
    string Title, double Dpr, double ViewportWidth, double ViewportHeight,
    double X, double Y, double Width, double Height,
    double? InnerScreenX, double? InnerScreenY, bool Visible, bool Playing, bool Fullscreen, long At);

/// <summary>
/// The latest report of each tab, matched to the captured window by its title: a browser puts the page's title in its
/// window's ("Stranger Things | Netflix - Google Chrome"), and only the tab in front has its title there. No platform
/// code, so the rules are tested everywhere.
/// </summary>
internal static class VideoReports
{
    /// <summary>A report older than this no longer describes the page (the extension repeats it while a video plays).</summary>
    internal const long FreshMs = 3000;
    private const int MaxTabs = 32;
    private const int MaxTitle = 300;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, VideoReport> ByTitle = new(StringComparer.Ordinal);

    /// <summary>
    /// A report from the extension's JSON, or null when it is not one: a missing or absurd number is refused rather than
    /// guessed, since a wrong rectangle would crop the wrong part of the screen.
    /// </summary>
    internal static VideoReport? Parse(JsonElement e, long now)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        static double? Num(JsonElement o, string k) =>
            o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
        static bool Flag(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        var title = e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() ?? "" : "";
        if (title.Length == 0) return null;
        if (title.Length > MaxTitle) title = title[..MaxTitle];
        if (Num(e, "dpr") is not { } dpr || dpr is < 0.25 or > 8) return null;
        if (Num(e, "vw") is not { } vw || Num(e, "vh") is not { } vh || vw is < 1 or > 20000 || vh is < 1 or > 20000) return null;
        if (Num(e, "x") is not { } x || Num(e, "y") is not { } y || Num(e, "w") is not { } w || Num(e, "h") is not { } h) return null;
        if (w < 1 || h < 1 || w > 40000 || h > 40000 || Math.Abs(x) > 40000 || Math.Abs(y) > 40000) return null;
        return new VideoReport(title, dpr, vw, vh, x, y, w, h, Num(e, "sx"), Num(e, "sy"),
            Flag(e, "visible"), Flag(e, "playing"), Flag(e, "fullscreen"), now);
    }

    internal static void Add(VideoReport r)
    {
        lock (Gate)
        {
            if (ByTitle.Count >= MaxTabs && !ByTitle.ContainsKey(r.Title))
            {
                var oldest = ByTitle.MinBy(kv => kv.Value.At).Key;
                ByTitle.Remove(oldest);
            }
            ByTitle[r.Title] = r;
        }
    }

    /// <summary>The tab a page left (closed, navigated away, video gone): its picture is no longer cropped to.</summary>
    internal static void Remove(string title)
    {
        lock (Gate) ByTitle.Remove(title.Trim());
    }

    /// <summary>
    /// A report from the extension: a POST of this one path from a browser extension's own origin. Web pages cannot
    /// send it (their origin is the site's), and the settings page has no reason to.
    /// </summary>
    internal static bool IsExtensionRequest(string method, PathString path, string? origin) =>
        method == "POST" && path == "/api/nowplaying/rect" && IsExtensionOrigin(origin);

    internal static bool IsExtensionOrigin(string? origin) =>
        origin is not null && (origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
            || origin.StartsWith("moz-extension://", StringComparison.Ordinal)
            || origin.StartsWith("extension://", StringComparison.Ordinal))
        && origin.IndexOf('/', origin.IndexOf("://", StringComparison.Ordinal) + 3) < 0;

    internal static void Clear()
    {
        lock (Gate) ByTitle.Clear();
    }

    /// <summary>
    /// The visible tab whose title the window's carries, fresh enough to trust; a playing one first, then the longest
    /// title (a short one, "YouTube", is contained in too many windows' titles to decide alone).
    /// </summary>
    internal static VideoReport? For(string? windowTitle, long now)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return null;
        lock (Gate)
            return ByTitle.Values
                .Where(r => r.Visible && now - r.At <= FreshMs && windowTitle.Contains(r.Title, StringComparison.Ordinal))
                .OrderByDescending(r => r.Playing).ThenByDescending(r => r.Title.Length).ThenByDescending(r => r.At)
                .FirstOrDefault();
    }
}

/// <summary>Turning a report into the part of the captured window that holds the video.</summary>
internal static class VideoCrop
{
    /// <summary>
    /// The video's picture in window pixels. <paramref name="client"/> is the window's client area within the captured
    /// picture; <paramref name="viewport"/> the page's area when the browser says where it is (a Chromium content
    /// window), else it is worked out: from the page's own idea of its place on screen (Firefox), or as the bottom of
    /// the client area (toolbars are on top) on whichever side scores higher (a side panel may be on either).
    /// </summary>
    internal static PixelRect? FromReport(VideoReport r, PixelRect client, PixelRect? viewport, (int X, int Y)? clientOnScreen,
        int frameWidth, int frameHeight, Func<PixelRect, double>? score = null)
    {
        var vw = (int)Math.Round(r.ViewportWidth * r.Dpr);
        var vh = (int)Math.Round(r.ViewportHeight * r.Dpr);
        PixelRect port;
        if (viewport is { IsEmpty: false } known) port = known;
        else if (r.InnerScreenX is { } sx && r.InnerScreenY is { } sy && clientOnScreen is { } origin)
            port = new(client.X + (int)Math.Round(sx * r.Dpr) - origin.X, client.Y + (int)Math.Round(sy * r.Dpr) - origin.Y, vw, vh);
        else
        {
            var y = client.Bottom - vh;
            var left = new PixelRect(client.X, y, vw, vh);
            var right = new PixelRect(client.Right - vw, y, vw, vh);
            port = score is not null && client.Width - vw > 8 && score(Inner(r, right)) > score(Inner(r, left)) ? right : left;
        }
        var video = Inner(r, port).Intersect(port).Clamp(frameWidth, frameHeight).Even();
        // a sliver is not a video: a rectangle off the page or a misread report shows the whole window instead
        return video.Width >= 32 && video.Height >= 32 ? video : null;
    }

    private static PixelRect Inner(VideoReport r, PixelRect port) => new(
        port.X + (int)Math.Round(r.X * r.Dpr), port.Y + (int)Math.Round(r.Y * r.Dpr),
        (int)Math.Round(r.Width * r.Dpr), (int)Math.Round(r.Height * r.Dpr));

    /// <summary>How far two shapes may differ (in aspect ratio) and still share one encoder, the rest made up by bars.</summary>
    internal const double ShapeSlack = 0.12;

    /// <summary>True when a picture of the first size fits the second with bars of at most <see cref="ShapeSlack"/>.</summary>
    internal static bool SameShape(int width, int height, int encWidth, int encHeight)
    {
        if (width <= 0 || height <= 0 || encWidth <= 0 || encHeight <= 0) return false;
        var a = width / (double)height;
        var b = encWidth / (double)encHeight;
        return Math.Abs(a - b) / b <= ShapeSlack;
    }

    /// <summary>Where a <paramref name="width"/> × <paramref name="height"/> picture goes in a larger frame: as big as fits, centred, even.</summary>
    internal static PixelRect Letterbox(int width, int height, int frameWidth, int frameHeight)
    {
        if (width <= 0 || height <= 0) return new(0, 0, frameWidth, frameHeight);
        var scale = Math.Min(frameWidth / (double)width, frameHeight / (double)height);
        var w = Math.Min(frameWidth, Math.Max(2, (int)Math.Round(width * scale) & ~1));
        var h = Math.Min(frameHeight, Math.Max(2, (int)Math.Round(height * scale) & ~1));
        return new PixelRect(((frameWidth - w) / 2) & ~1, ((frameHeight - h) / 2) & ~1, w, h);
    }

    /// <summary>
    /// The size a <paramref name="width"/> × <paramref name="height"/> picture is sent at: kept whole up to the box,
    /// shrunk to fit it otherwise, aspect kept, even on both sides (4:2:0 video).
    /// </summary>
    internal static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        // a tall picture gets the same number of pixels as a wide one, not a box turned on its side
        if (height > width) (maxWidth, maxHeight) = (maxHeight, maxWidth);
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        var w = Math.Max(16, (int)Math.Round(width * scale) & ~1);
        var h = Math.Max(16, (int)Math.Round(height * scale) & ~1);
        return (w, h);
    }
}
