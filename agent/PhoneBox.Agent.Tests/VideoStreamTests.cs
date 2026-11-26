using System.Buffers.Binary;
using System.Text;
using PhoneBox.Agent;
using Xunit;

// The Now playing screen's H.264 stream: the pictures as NV12 for the encoder (Nv12), the messages on the wire
// (VideoWire), and the options and routes that decide who gets it.
public sealed class VideoStreamTests
{
    private static unsafe byte[] Convert(byte[] bgra, int width, int height, PixelRect crop, int ow, int oh)
    {
        var dst = new byte[Nv12.Size(ow, oh)];
        fixed (byte* s = bgra)
        fixed (byte* d = dst)
            Nv12.FromBgra(s, width * 4, crop, d, ow, oh);
        return dst;
    }

    private static byte[] Solid(int width, int height, byte b, byte g, byte r)
    {
        var p = new byte[width * height * 4];
        for (var i = 0; i < p.Length; i += 4) { p[i] = b; p[i + 1] = g; p[i + 2] = r; p[i + 3] = 255; }
        return p;
    }

    [Theory]
    // BT.709 studio range: black 16/128/128, white 235/128/128, pure red 63/102/240, pure blue 32/240/118
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(255, 0, 0, 63, 102, 240)]
    [InlineData(0, 0, 255, 32, 240, 118)]
    public void ColoursBecomeBt709StudioRange(byte r, byte g, byte b, int y, int u, int v)
    {
        var nv12 = Convert(Solid(8, 8, b, g, r), 8, 8, new PixelRect(0, 0, 8, 8), 4, 4);
        Assert.InRange(nv12[0], y - 1, y + 1);
        Assert.InRange(nv12[16], u - 1, u + 1);
        Assert.InRange(nv12[17], v - 1, v + 1);
    }

    [Fact]
    public void OnlyTheCropIsConverted()
    {
        // white picture with a black square: the crop of the square alone is black all over
        var p = Solid(64, 64, 255, 255, 255);
        for (var y = 16; y < 48; y++)
            for (var x = 16; x < 48; x++)
                p[(y * 64 + x) * 4] = p[(y * 64 + x) * 4 + 1] = p[(y * 64 + x) * 4 + 2] = 0;
        var nv12 = Convert(p, 64, 64, new PixelRect(16, 16, 32, 32), 16, 16);
        Assert.All(nv12.AsSpan(0, 16 * 16).ToArray(), luma => Assert.InRange(luma, 16, 18));
    }

    [Fact]
    public unsafe void BarsAroundAPictureAreBlack()
    {
        var white = Solid(16, 16, 255, 255, 255);
        var dst = new byte[Nv12.Size(16, 16)];
        fixed (byte* s = white)
        fixed (byte* d = dst)
            Nv12.FromBgra(s, 16 * 4, new PixelRect(0, 0, 16, 16), d, 16, 16, new PixelRect(0, 4, 16, 8));
        Assert.Equal(16, dst[0]);                  // the bar above: black
        Assert.InRange(dst[4 * 16], 233, 236);     // the picture: white
        Assert.Equal(16, dst[15 * 16 + 15]);       // the bar below: black
        Assert.Equal(128, dst[16 * 16]);           // no colour in the bars
    }

    [Fact]
    public void AnOddSizeIsRefused() =>
        Assert.Throws<ArgumentException>(() => Convert(Solid(8, 8, 0, 0, 0), 8, 8, new PixelRect(0, 0, 8, 8), 5, 4));

    [Fact]
    public void AMessageCarriesItsTypeKeyLengthAndTime()
    {
        var m = VideoWire.Message(VideoWire.Chunk, true, 123_456_789, [1, 2, 3]);
        Assert.Equal(VideoWire.HeaderSize + 3, m.Length);
        Assert.Equal(VideoWire.Chunk, m[0]);
        Assert.Equal(1, m[1]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(4)));
        Assert.Equal(123_456_789, BinaryPrimitives.ReadInt64LittleEndian(m.AsSpan(8)));
        Assert.Equal([1, 2, 3], m[16..]);
    }

    [Fact]
    public void AJsonMessageIsUtf8()
    {
        var m = VideoWire.Json(VideoWire.State, new { state = "idle" });
        Assert.Equal(VideoWire.State, m[0]);
        Assert.Equal("""{"state":"idle"}""", Encoding.UTF8.GetString(m, 16, m.Length - 16));
    }

    // an access unit as the encoders here write it: delimiter, SPS (Main, level 3.1), PPS, IDR slice
    private static readonly byte[] KeyUnit =
    [
        0, 0, 0, 1, 0x09, 0x10,
        0, 0, 0, 1, 0x67, 0x4D, 0x00, 0x1F, 0x93, 0x28,
        0, 0, 1, 0x68, 0xEE, 0x38, 0x80,
        0, 0, 0, 1, 0x65, 0x88, 0x80, 0x10,
    ];

    [Fact]
    public void TheUnitsAreSplitAtTheirStartCodes()
    {
        var nals = VideoWire.Nals(KeyUnit);
        Assert.Equal([9, 7, 8, 5], nals.Select(n => n.Type));
        Assert.Equal(6, nals[1].Length); // 67 4D 00 1F 93 28, the next start code's zero not counted
        Assert.True(VideoWire.HasSps(KeyUnit));
        Assert.False(VideoWire.HasSps([0, 0, 0, 1, 0x41, 0x9A, 0x04]));
    }

    [Fact]
    public void TheCodecStringComesFromTheSps()
    {
        Assert.Equal("avc1.4D001F", VideoWire.CodecOf(KeyUnit));
        Assert.Null(VideoWire.CodecOf([0, 0, 0, 1, 0x41, 0x9A, 0x04]));
    }

    // ---------------------------------------------------------------- options and routes

    private static WidgetConfig Widget(params (string Key, object? Value)[] options)
    {
        var w = new WidgetConfig { Id = "np", Type = "nowplaying" };
        foreach (var (k, v) in options) w.Set(k, v);
        return w;
    }

    [Fact]
    public void TheVideoRateHasItsOwnKeySoAnOldJpegRateDoesNotHoldItBack()
    {
        Assert.Equal(MediaService.VideoDefaultFps, MediaService.VideoRateOf(Widget(("fps", 5))));
        Assert.Equal(30, MediaService.VideoDefaultFps);
        Assert.Equal(24, MediaService.VideoRateOf(Widget(("videoFps", 24))));
        Assert.Equal(MediaService.VideoMaxFps, MediaService.VideoRateOf(Widget(("videoFps", 999))));
        Assert.Equal(MediaService.VideoMinFps, MediaService.VideoRateOf(Widget(("videoFps", 0))));
    }

    [Fact]
    public void TheQualityPicksTheBox()
    {
        Assert.Equal((1280, 720), MediaService.VideoBoxOf(Widget()));
        Assert.Equal((854, 480), MediaService.VideoBoxOf(Widget(("liveQuality", "480"))));
        Assert.Equal((1920, 1080), MediaService.VideoBoxOf(Widget(("liveQuality", "1080"))));
        Assert.Equal((1280, 720), MediaService.VideoBoxOf(Widget(("liveQuality", "4k")))); // unknown: the default
    }

    [Fact]
    public void TheVideoIsCroppedAndEncodedUnlessTheWidgetSaysOtherwise()
    {
        Assert.True(MediaService.CropsToVideo(Widget()));
        Assert.False(MediaService.CropsToVideo(Widget(("liveCrop", "window"))));
        Assert.True(MediaService.AllowsVideo(Widget()));
        Assert.False(MediaService.AllowsVideo(Widget(("liveCodec", "jpeg"))));
    }

    [Fact]
    public void TheBitRateIsSaneForEverySize()
    {
        Assert.InRange(MediaService.VideoBitrate(1280, 720, 30), 2_000_000, 4_000_000);
        Assert.Equal(1_000_000, MediaService.VideoBitrate(320, 180, 10));   // a floor for small pictures
        Assert.Equal(12_000_000, MediaService.VideoBitrate(1920, 1080, 60)); // a ceiling Wi-Fi carries
    }

    [Fact]
    public void TheStreamTravelsLikeThePictures()
    {
        Assert.True(Lan.AllowedOverLan("/api/nowplaying/video"));
        var lan = new LanConfig { Enabled = true, Token = "phone-key", WebhookToken = "hook-key" };
        Assert.Equal(Lan.LanAccess.Phone, Lan.AuthorizeRequest(lan, "GET", "/api/nowplaying/video", "", "Bearer phone-key", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, "GET", "/api/nowplaying/video", "", "Bearer hook-key", null));
        // the extension's reports are for this computer only: never over Wi-Fi, never through a hub
        Assert.False(Lan.AllowedOverLan("/api/nowplaying/rect"));
        try
        {
            HubMode.Init([], _ => null);
            Assert.True(HubSources.Forwardable("GET", "/api/nowplaying/video"));
            Assert.False(HubSources.Forwardable("POST", "/api/nowplaying/rect"));
        }
        finally { HubMode.Reset(); }
    }
}
