using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

// One layout across the hub and the computers it reads (Layout, HubConfig.SyncLayout): what counts as a change, what
// travels, and what a computer keeps to itself.
public sealed class LayoutSyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-layout").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ConfigStore Store(AgentConfig cfg, string name = "config.json")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, ConfigStore.WriteOpts));
        return new ConfigStore(path);
    }

    private static AgentConfig WithPages(params string[] titles) => new()
    {
        Pages = titles.Select(title => new PageConfig
        {
            Id = title.ToLowerInvariant(), Title = title, Icon = "▫️",
            Widgets = [new WidgetConfig { Id = "clock", Type = "clock", X = 0, Y = 0, W = 6, H = 4 }],
        }).ToList(),
    };

    [Fact]
    public void TheSameLayoutHasTheSameFingerprintAndADifferentOneDoesNot()
    {
        var a = Store(WithPages("Home", "Music"), "a.json").Get();
        var b = Store(WithPages("Home", "Music"), "b.json").Get();
        var c = Store(WithPages("Home", "Cinema"), "c.json").Get();
        Assert.Equal(Layout.Hash(a), Layout.Hash(b));
        Assert.NotEqual(Layout.Hash(a), Layout.Hash(c));
    }

    // the home page names a page, so it belongs to the layout: moving it alone is still a change
    [Fact]
    public void TheHomePageIsPartOfTheLayout()
    {
        var cfg = WithPages("Home", "Music");
        var before = Layout.Hash(Store(cfg, "before.json").Get());
        cfg.Display.HomePage = "music";
        Assert.NotEqual(before, Layout.Hash(Store(cfg, "after.json").Get()));
    }

    [Fact]
    public void OnlyRealLayoutsAreRead()
    {
        Assert.NotNull(Layout.Read("""{"pages":[{"id":"home","title":"Home","widgets":[]}]}"""));
        Assert.Null(Layout.Read("not json"));
        Assert.Null(Layout.Read(""));
        // an empty page list would wipe the pages and have the defaults built in their place
        Assert.Null(Layout.Read("""{"pages":[]}"""));
        Assert.Null(Layout.Read("{}"));
        Assert.Null(Layout.Read("{\"pages\":[{\"id\":\"home\"}" + new string(' ', Layout.MaxBytes) + "]}"));
    }

    [Fact]
    public void SavingALayoutReplacesThePagesAndKeepsEverythingElse()
    {
        var mine = WithPages("Home");
        mine.Calendars.Add(new CalendarSource { Name = "Work", Url = "https://example.com/secret.ics" });
        mine.Lan.Token = "my-own-key";
        var store = Store(mine);

        var incoming = Layout.Read("""
            {"pages":[{"id":"cinema","title":"Cinema","icon":"\u25ab\ufe0f","widgets":[{"id":"np","type":"nowplaying","x":0,"y":0,"w":12,"h":6}]}],"homePage":"cinema"}
            """)!;
        Assert.Null(store.SaveLayout(incoming));

        var after = store.Get();
        Assert.Equal(["cinema"], after.Pages.Select(p => p.Id));
        Assert.Equal("nowplaying", after.Pages[0].Widgets![0].Type);
        Assert.Equal("cinema", after.Display.HomePage);
        // a computer's own secrets and feeds are not part of a layout
        Assert.Equal("my-own-key", after.Lan.Token);
        Assert.Equal("https://example.com/secret.ics", Assert.Single(after.Calendars).Url);
    }

    // after storing a layout the agent records what it now holds, so the hub's next read is not seen as a local change
    [Fact]
    public void SavingALayoutRecordsWhatBothSidesNowHold()
    {
        var store = Store(WithPages("Home"));
        Assert.Null(store.Get().Hub.LayoutHash);
        Assert.Null(store.SaveLayout(Layout.Read("""{"pages":[{"id":"cinema","title":"Cinema","widgets":[]}]}""")!));
        var after = store.Get();
        Assert.Equal(Layout.Hash(after), after.Hub.LayoutHash);
    }

    [Fact]
    public void MarkingASyncLeavesThePagesAlone()
    {
        var store = Store(WithPages("Home", "Music"));
        var before = Layout.Hash(store.Get());
        Assert.Null(store.MarkLayoutSynced("abc123"));
        var after = store.Get();
        Assert.Equal("abc123", after.Hub.LayoutHash);
        Assert.Equal(before, Layout.Hash(after)); // the pages did not move
        Assert.Equal(["home", "music"], after.Pages.Select(p => p.Id));
    }

    [Fact]
    public void MarkingASyncAlsoRecordsOneComputersOwnLayout()
    {
        var cfg = WithPages("Home");
        cfg.Hub.Sources.Add(new HubSource { Id = "fp-one", Name = "Desk PC" });
        cfg.Hub.Sources.Add(new HubSource { Id = "fp-two", Name = "Laptop" });
        var store = Store(cfg);
        Assert.Null(store.MarkLayoutSynced("hub-hash", hub =>
        {
            if (hub.Sources.FirstOrDefault(s => s.Id == "fp-one") is { } one) one.LayoutHash = "pc-hash";
        }));
        var after = store.Get();
        Assert.Equal("hub-hash", after.Hub.LayoutHash);
        Assert.Equal("pc-hash", after.Hub.Sources.Single(s => s.Id == "fp-one").LayoutHash);
        Assert.Null(after.Hub.Sources.Single(s => s.Id == "fp-two").LayoutHash);
    }

    [Fact]
    public void KeepingOneLayoutIsOnByDefault() => Assert.True(new HubConfig().SyncLayout);

    // a hub reads and writes it with the computer's paired key; a phone never does, and it is not relayed
    [Fact]
    public void TheLayoutTravelsOverWiFiButIsNeverForwarded()
    {
        try
        {
            Assert.True(Lan.AllowedOverLan("/api/layout"));
            HubMode.Init([], _ => null);
            Assert.False(HubSources.Forwardable("GET", "/api/layout"));
            Assert.False(HubSources.Forwardable("POST", "/api/layout"));
        }
        finally { HubMode.Reset(); }
    }

    [Fact]
    public void TheLayoutIsAuthorizedLikeTheRestOfThePhoneApi()
    {
        var lan = new LanConfig { Enabled = true, Token = "phone-key", WebhookToken = "hook-key" };
        Assert.Equal(Lan.LanAccess.Phone, Lan.AuthorizeRequest(lan, "GET", "/api/layout", "", "Bearer phone-key", null));
        Assert.Equal(Lan.LanAccess.Phone, Lan.AuthorizeRequest(lan, "PUT", "/api/layout", "", "Bearer phone-key", null));
        // the webhook key opens only the webhook endpoints, and no key opens nothing
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, "PUT", "/api/layout", "", "Bearer hook-key", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, "GET", "/api/layout", "", null, null));
    }
}
