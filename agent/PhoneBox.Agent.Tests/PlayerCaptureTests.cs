using PhoneBox.Agent;
using Xunit;

// The Now playing screen widget's live video: who may ask for a picture of the player's window, and over which link.
// The capture itself is Windows-only (PlayerCapture.cs); these cover the rules that hold everywhere.
public sealed class PlayerCaptureTests
{
    private static AgentConfig WithWidget(params (string Key, object? Value)[] options)
    {
        var widget = new WidgetConfig { Id = "np", Type = "nowplaying" };
        foreach (var (key, value) in options) widget.Set(key, value);
        return new AgentConfig
        {
            Pages = [new PageConfig { Id = "cinema", Title = "Cinema", Enabled = true, Widgets = [widget] }],
        };
    }

    // the picture travels over Wi-Fi with the phone's key, like the album art and the camera's frames
    [Fact]
    public void ThePictureIsAllowedOverWiFi() => Assert.True(Lan.AllowedOverLan("/api/nowplaying/frame"));

    [Fact]
    public void ThePictureIsAuthorizedLikeTheRestOfThePhoneApi()
    {
        var lan = new LanConfig { Enabled = true, Token = "phone-key", WebhookToken = "hook-key" };
        Assert.Equal(Lan.LanAccess.Phone, Lan.AuthorizeRequest(lan, "GET", "/api/nowplaying/frame", "", "Bearer phone-key", null));
        // the webhook key opens only the webhook endpoints, and no key opens nothing
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, "GET", "/api/nowplaying/frame", "", "Bearer hook-key", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, "GET", "/api/nowplaying/frame", "", null, null));
    }

    // a hub's phone gets the picture of the computer it shows, through /api/src/<id>/...
    [Fact]
    public void ThePictureIsForwardedByAHub()
    {
        try
        {
            HubMode.Init([], _ => null);
            Assert.True(HubSources.Forwardable("GET", "/api/nowplaying/frame"));
        }
        finally { HubMode.Reset(); }
    }

    // the widget names itself and the agent checks that widget's own options (PanelWidgets.Find)
    [Fact]
    public void OnlyAWidgetOnAnEnabledPageIsFound()
    {
        var cfg = WithWidget(("live", true));
        Assert.NotNull(PanelWidgets.Find(cfg, null, "cinema/np", "nowplaying"));
        Assert.Null(PanelWidgets.Find(cfg, null, "cinema/other", "nowplaying"));
        Assert.Null(PanelWidgets.Find(cfg, null, "cinema/np", "camera")); // another widget type with the same key
        cfg.Pages[0].Enabled = false;
        Assert.Null(PanelWidgets.Find(cfg, null, "cinema/np", "nowplaying"));
    }

    [Fact]
    public void EachWidgetKeepsToItsOwnLiveOption()
    {
        Assert.False(PanelWidgets.Find(WithWidget(("live", false)), null, "cinema/np", "nowplaying")!.Bool("live"));
        Assert.True(PanelWidgets.Find(WithWidget(("live", true)), null, "cinema/np", "nowplaying")!.Bool("live"));
        // no option saved: the widget's default (on) applies, which the endpoint reads as "not false"
        Assert.Null(PanelWidgets.Find(WithWidget(), null, "cinema/np", "nowplaying")!.Bool("live"));
    }

    [Fact]
    public void TheRateIsReadFromTheWidgetAndKeptInRange()
    {
        int Rate(object? value) => MediaService.RateOf(PanelWidgets.Find(WithWidget(("fps", value)), null, "cinema/np", "nowplaying"));
        Assert.Equal(5, Rate(5));
        Assert.Equal(MediaService.MaxFps, Rate(999));   // a hand-edited file cannot ask for more
        Assert.Equal(MediaService.MinFps, Rate(0));
        Assert.Equal(MediaService.DefaultFps, Rate(null));
    }

    [Fact]
    public void TheDefaultRateIsTheGentleOne()
    {
        // 5 fps measured about 9% of one core here; 15 was 71%, so it is available but not the default
        Assert.Equal(5, MediaService.DefaultFps);
        Assert.InRange(MediaService.DefaultFps, MediaService.MinFps, MediaService.MaxFps);
    }
}
