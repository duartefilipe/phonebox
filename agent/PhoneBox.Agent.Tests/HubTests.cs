using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class HubTests
{
    private const string Fp = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";

    [Fact]
    public void APairingLinkIsReadIntoItsParts()
    {
        var link = PairingCodes.Link("DESK PC", Fp, 8766, ["192.168.1.20", "100.101.102.103"], "code_1-x");
        var p = HubSources.ParseLink(link);
        Assert.NotNull(p);
        Assert.Equal("DESK PC", p.Pc);
        Assert.Equal(Fp, p.Fingerprint);
        Assert.Equal(8766, p.Port);
        Assert.Equal(["192.168.1.20", "100.101.102.103"], p.Hosts);
        Assert.Equal("code_1-x", p.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/pair")] // no fragment
    [InlineData("https://x/pair/#v=2&pc=a&fp=" + Fp + "&port=8766&h=1.2.3.4&c=k")] // unknown version
    [InlineData("https://x/pair/#v=1&pc=a&fp=abc&port=8766&h=1.2.3.4&c=k")] // short fingerprint
    [InlineData("https://x/pair/#v=1&pc=a&fp=" + Fp + "&port=99999&h=1.2.3.4&c=k")] // bad port
    [InlineData("https://x/pair/#v=1&pc=a&fp=" + Fp + "&port=8766&h=&c=k")] // no address
    [InlineData("https://x/pair/#v=1&pc=a&fp=" + Fp + "&port=8766&h=1.2.3.4")] // no code
    public void AnythingElseIsNoLink(string? link) => Assert.Null(HubSources.ParseLink(link));

    [Fact]
    public void OnlyTheComputersPhoneApiIsForwarded()
    {
        HubMode.Init([], _ => null);
        Assert.True(HubSources.Forwardable("GET", "/api/media"));
        Assert.True(HubSources.Forwardable("POST", "/api/volume"));
        Assert.True(HubSources.Forwardable("GET", "/api/procs"));
        // the hub's own, settings-only, long-lived or unsafe ones are not
        Assert.False(HubSources.Forwardable("GET", "/api/settings"));
        Assert.False(HubSources.Forwardable("PUT", "/api/media"));
        Assert.False(HubSources.Forwardable("GET", "/api/stream"));
        Assert.False(HubSources.Forwardable("GET", "/api/trackpad"));
        Assert.False(HubSources.Forwardable("POST", "/api/pair/claim"));
        Assert.False(HubSources.Forwardable("POST", "/api/push/x"));
        Assert.False(HubSources.Forwardable("GET", "/api/config"));
        Assert.False(HubSources.Forwardable("GET", "/api/state"));
        Assert.False(HubSources.Forwardable("GET", "/api/src/other/api/media"));
    }

    [Fact]
    public void ThePhoneMayAskTheHubForAComputerOnlyInHubMode()
    {
        try
        {
            HubMode.Init([], _ => null);
            Assert.False(Lan.AllowedOverLan("/api/src/abc/media"));
            HubMode.Init([], k => k == "PHONEBOX_HUB" ? "1" : null);
            Assert.True(Lan.AllowedOverLan("/api/src/abc/media"));
        }
        finally { HubMode.Reset(); }
    }

    [Fact]
    public void HubModeComesFromTheEnvironmentOrTheCommandLine()
    {
        try
        {
            HubMode.Init(["--hub"], _ => null);
            Assert.True(HubMode.Enabled);
            Assert.False(HubMode.AdminActive); // no password: the settings page stays local
            HubMode.Init([], k => k switch { "PHONEBOX_HUB" => "true", "PHONEBOX_PASSWORD" => "s3cret", "PHONEBOX_ADMIN_PORT" => "9443", _ => null });
            Assert.True(HubMode.AdminActive);
            Assert.Equal(9443, HubMode.AdminPort);
            HubMode.Init([], k => k == "PHONEBOX_ADMIN_PORT" ? "80" : null);
            Assert.Equal(8767, HubMode.AdminPort);
        }
        finally { HubMode.Reset(); }
    }

    [Fact]
    public void TheRightPasswordOpensASessionThatExpires()
    {
        long now = 1_000;
        var admin = new HubAdmin(() => now);
        Assert.Null(admin.Login("wrong", "s3cret"));
        var session = admin.Login("s3cret", "s3cret");
        Assert.NotNull(session);
        Assert.True(admin.Valid(session));
        Assert.False(admin.Valid("someone-else"));
        now += HubAdmin.SessionMs;
        Assert.False(admin.Valid(session));
    }

    [Fact]
    public void TooManyWrongPasswordsLockTheLoginForAMinute()
    {
        long now = 1_000;
        var admin = new HubAdmin(() => now);
        for (var i = 0; i < 5; i++) Assert.Null(admin.Login("guess" + i, "s3cret"));
        Assert.Null(admin.Login("s3cret", "s3cret")); // even the right one, while locked
        now += 60_000;
        Assert.NotNull(admin.Login("s3cret", "s3cret"));
    }

    [Fact]
    public void ASignedOutSessionNoLongerOpens()
    {
        var admin = new HubAdmin();
        var session = admin.Login("p", "p");
        admin.Logout(session);
        Assert.False(admin.Valid(session));
    }

    [Fact]
    public void NoPasswordMeansNoLogin() => Assert.Null(new HubAdmin().Login("", null));
}
