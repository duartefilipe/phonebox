using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

// The Now playing screen's live video shows the video, not the whole browser window: where the browser extension says
// the video is (VideoReports, VideoCrop), else the part of the window that moves (MotionCrop).
public sealed class VideoCropTests
{
    private static VideoReport Report(string title = "Stranger Things | Netflix", double dpr = 1.5, double x = 100, double y = 50,
        double w = 640, double h = 360, bool visible = true, bool playing = true, long at = 1000, double? sx = null, double? sy = null) =>
        new(title, dpr, 1280, 720, x, y, w, h, sx, sy, visible, playing, false, at);

    [Fact]
    public void AReportIsReadFromTheExtensionsJson()
    {
        using var doc = JsonDocument.Parse("""{"title":" Film ","dpr":2,"vw":800,"vh":600,"x":10,"y":20,"w":300,"h":200,"visible":true,"playing":true,"sx":5,"sy":80}""");
        var r = VideoReports.Parse(doc.RootElement, 42)!;
        Assert.Equal("Film", r.Title);
        Assert.Equal(2, r.Dpr);
        Assert.Equal((10, 20, 300, 200), (r.X, r.Y, r.Width, r.Height));
        Assert.Equal((5.0, 80.0), (r.InnerScreenX, r.InnerScreenY));
        Assert.True(r.Visible && r.Playing);
        Assert.Equal(42, r.At);
    }

    [Theory]
    [InlineData("""{"dpr":1,"vw":800,"vh":600,"x":0,"y":0,"w":10,"h":10}""")]                 // no title
    [InlineData("""{"title":"a","dpr":0,"vw":800,"vh":600,"x":0,"y":0,"w":10,"h":10}""")]     // no scale
    [InlineData("""{"title":"a","dpr":1,"vw":800,"vh":600,"x":0,"y":0,"w":0,"h":10}""")]      // no size
    [InlineData("""{"title":"a","dpr":1,"vw":800,"vh":600,"x":"0","y":0,"w":10,"h":10}""")]   // a string for a number
    [InlineData("""{"title":"a","dpr":1,"vw":800,"vh":600,"x":1e9,"y":0,"w":10,"h":10}""")]   // absurd
    [InlineData("""[1,2,3]""")]
    public void AMalformedReportIsRefusedRatherThanGuessed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(VideoReports.Parse(doc.RootElement, 0));
    }

    [Fact]
    public void TheReportIsMatchedToTheWindowByItsTitle()
    {
        VideoReports.Clear();
        try
        {
            VideoReports.Add(Report("YouTube", at: 1000));
            VideoReports.Add(Report("Cats - YouTube", at: 1000));
            VideoReports.Add(Report("Hidden tab", visible: false, at: 1000));
            // the longest title the window's contains wins: "YouTube" alone is in every YouTube window's title
            Assert.Equal("Cats - YouTube", VideoReports.For("Cats - YouTube - Google Chrome", 1500)!.Title);
            Assert.Null(VideoReports.For("Hidden tab - Google Chrome", 1500));          // not the tab on screen
            Assert.Null(VideoReports.For("Something else - Google Chrome", 1500));
            Assert.Null(VideoReports.For("Cats - YouTube - Google Chrome", 1000 + VideoReports.FreshMs + 1)); // stale
            VideoReports.Remove("Cats - YouTube");
            Assert.Equal("YouTube", VideoReports.For("Cats - YouTube - Google Chrome", 1500)!.Title);
        }
        finally { VideoReports.Clear(); }
    }

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnop", true)]
    [InlineData("moz-extension://1b2c3d4e-0000-4000-8000-000000000000", true)]
    [InlineData("https://www.youtube.com", false)]
    [InlineData("http://127.0.0.1:8765", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("chrome-extension://abc/evil", false)]
    public void OnlyAnExtensionMayReport(string? origin, bool allowed)
    {
        Assert.Equal(allowed, VideoReports.IsExtensionRequest("POST", "/api/nowplaying/rect", origin));
        // and only that one path, and only POST
        Assert.False(VideoReports.IsExtensionRequest("POST", "/api/config", origin));
        Assert.False(VideoReports.IsExtensionRequest("GET", "/api/nowplaying/rect", origin));
    }

    [Fact]
    public void AChromiumPageIsWhereItsContentWindowIs()
    {
        // the window's client area starts 1 px in; the page (content window) 80 px down, 1280×720 CSS px at 1.5×
        var client = new PixelRect(1, 0, 1920, 1160);
        var viewport = new PixelRect(1, 80, 1920, 1080);
        var crop = VideoCrop.FromReport(Report(), client, viewport, null, 1922, 1161)!.Value;
        Assert.Equal(new PixelRect(1 + 150, 80 + 75, 960, 540).Even(), crop);
    }

    [Fact]
    public void AFirefoxPageIsWhereItSaysItIsOnScreen()
    {
        // the page reports its own place on screen in CSS px; the client area is at (100, 200) on screen
        var client = new PixelRect(8, 31, 1920, 1170);
        var crop = VideoCrop.FromReport(Report(sx: 100, sy: 190), client, null, (100, 200), 2000, 1300)!.Value;
        // page at client.X + 150 - 100, client.Y + 285 - 200; the video 150, 75 in from there
        Assert.Equal(new PixelRect(8 + 50 + 150, 31 + 85 + 75, 960, 540).Even(), crop);
    }

    [Fact]
    public void WithoutAPlaceThePageIsAtTheBottomOnTheBusierSide()
    {
        var client = new PixelRect(0, 0, 2400, 1200);
        // the page (1920×1080 px) leaves 480 px of side panel; the bottom of the client area, toolbars on top
        var left = VideoCrop.FromReport(Report(), client, null, null, 2400, 1200, _ => 0)!.Value;
        Assert.Equal(120 + 76, left.Y); // 75 px down the page, evened to 76
        Assert.Equal(150, left.X);
        // a side panel on the left: the page is on the right, where the video moves
        var right = VideoCrop.FromReport(Report(), client, null, null, 2400, 1200, r => r.X >= 480 ? 1 : 0)!.Value;
        Assert.Equal(480 + 150, right.X);
    }

    [Fact]
    public void AVideoOffThePageIsNotACrop()
    {
        var client = new PixelRect(0, 0, 1920, 1080);
        Assert.Null(VideoCrop.FromReport(Report(x: 5000, y: 5000), client, client, null, 1920, 1080));
        // half off the page: only the part on the page is used
        var half = VideoCrop.FromReport(Report(x: -320, y: 0, w: 640, h: 360, dpr: 1), client, client, null, 1920, 1080)!.Value;
        Assert.Equal(new PixelRect(0, 0, 320, 360), half);
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720, 1280, 720)]
    [InlineData(1280, 720, 1280, 720, 1280, 720)]
    [InlineData(800, 600, 1280, 720, 800, 600)]     // smaller than the box: kept whole
    [InlineData(1000, 1000, 1280, 720, 720, 720)]
    [InlineData(1080, 1920, 1280, 720, 720, 1280)]  // tall: the box is turned, not shrunk to 405 px wide
    [InlineData(1921, 1081, 1920, 1080, 1918, 1080)] // even on both sides
    public void ThePictureFitsTheBoxEvenly(int w, int h, int bw, int bh, int ew, int eh) =>
        Assert.Equal((ew, eh), VideoCrop.Fit(w, h, bw, bh));

    [Fact]
    public void ARectangleIsEvenedInward()
    {
        var r = new PixelRect(3, 5, 101, 51).Even();
        Assert.Equal(new PixelRect(4, 6, 100, 50), r);
        Assert.True(r.X >= 3 && r.Right <= 104 && r.Bottom <= 56);
    }

    // ---------------------------------------------------------------- MotionCrop

    /// <summary>A 640×360 BGRA picture: a still page with a moving block where the video is.</summary>
    private static byte[] Picture(int frame, PixelRect video, int width = 640, int height = 360)
    {
        var p = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var inside = x >= video.X && x < video.Right && y >= video.Y && y < video.Bottom;
                var v = inside ? (byte)((x * 3 + y * 5 + frame * 37) & 0xff) : (byte)200;
                p[i] = p[i + 1] = p[i + 2] = v;
                p[i + 3] = 255;
            }
        return p;
    }

    private static MotionCrop Watch(PixelRect video, int frames, MotionCrop? m = null)
    {
        m ??= new MotionCrop();
        var grid = new byte[MotionCrop.Cols * MotionCrop.Rows];
        for (var f = 0; f < frames; f++)
        {
            var p = Picture(f, video);
            MotionCrop.Sample(p, 640 * 4, 640, 360, grid);
            m.Update(grid, 640, 360);
        }
        return m;
    }

    [Fact]
    public void ThePartThatMovesIsFound()
    {
        var video = new PixelRect(160, 60, 320, 180);
        var m = Watch(video, Settled);
        var crop = Assert.NotNull(m.Crop);
        // within a cell (4 px here) of the real edges
        Assert.True(crop.Near(video, 8), $"{crop} vs {video}");
        Assert.True(m.Score(video) > m.Score(new PixelRect(0, 0, 100, 50)));
    }

    [Fact]
    public void NothingIsTakenBeforeItSettles()
    {
        var m = Watch(new PixelRect(160, 60, 320, 180), MotionCrop.Warmup - 2);
        Assert.Null(m.Crop);
    }

    [Fact]
    public void AStillPageIsNoVideo()
    {
        var m = new MotionCrop();
        var grid = new byte[MotionCrop.Cols * MotionCrop.Rows];
        var still = Picture(0, new PixelRect(0, 0, 0, 0));
        for (var f = 0; f < 40; f++)
        {
            MotionCrop.Sample(still, 640 * 4, 640, 360, grid);
            m.Update(grid, 640, 360);
        }
        Assert.Null(m.Crop);
    }

    private static readonly int Settled = Math.Max(MotionCrop.GrowAfter, MotionCrop.Warmup) + 5;

    [Fact]
    public void AScrolledPageKeepsTheCropFoundBefore()
    {
        var video = new PixelRect(160, 60, 320, 180);
        var m = Watch(video, Settled);
        // the whole page moves (scrolled) for a while: that says nothing about where the video is
        Watch(new PixelRect(0, 0, 640, 360), MotionCrop.ShrinkAfter + 10, m);
        Assert.True(m.Crop!.Value.Near(video, 8), $"{m.Crop} vs {video}");
    }

    [Fact]
    public void AWholeWindowOnTheMoveIsNoCrop()
    {
        var m = Watch(new PixelRect(0, 0, 640, 360), Settled + 20);
        Assert.Null(m.Crop);
    }

    [Theory]
    [InlineData(948, 540, 948, 576, true)]   // a few rows more: bars, same encoder
    [InlineData(846, 540, 948, 540, true)]
    [InlineData(568, 324, 720, 810, false)]  // wide against tall: a new encoder
    [InlineData(1280, 720, 720, 1280, false)]
    public void ASmallChangeOfShapeKeepsTheEncoder(int w, int h, int ew, int eh, bool same) =>
        Assert.Equal(same, VideoCrop.SameShape(w, h, ew, eh));

    [Fact]
    public void ThePictureIsFittedWithBars()
    {
        var into = VideoCrop.Letterbox(1280, 720, 948, 576);
        Assert.Equal(948, into.Width);
        Assert.Equal(532, into.Height);              // 948 / 16 * 9 = 533.25, even
        Assert.Equal(22, into.Y);                    // centred: (576 - 532) / 2
        Assert.Equal(0, into.X);
        Assert.Equal(new PixelRect(0, 0, 640, 360), VideoCrop.Letterbox(640, 360, 640, 360));
    }

    [Fact]
    public void AStillSceneDoesNotShrinkTheCropToOneActor()
    {
        var video = new PixelRect(160, 60, 320, 180);
        var m = Watch(video, Settled);
        // then only a small part of the video moves for a while (shorter than the heat lasts)
        Watch(new PixelRect(280, 120, 80, 60), MotionCrop.ShrinkAfter / 2, m);
        Assert.True(m.Crop!.Value.Near(video, 8), $"{m.Crop} shrank");
    }
}
