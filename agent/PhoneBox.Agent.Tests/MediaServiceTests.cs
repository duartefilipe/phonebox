using PhoneBox.Agent;
using Xunit;

// The streaming service behind what is playing (Now playing screen widget). Windows reports a browser as the player
// and often puts only the service's name in the title, so the browser's window title is where the episode's name is.
public sealed class MediaServiceTests
{
    [Theory]
    [InlineData("Netflix", "netflix")]
    [InlineData("Stranger Things - Netflix", "netflix")]
    [InlineData("Some video - YouTube", "youtube")]
    [InlineData("A song - YouTube Music", "youtube-music")]
    [InlineData("The Last of Us | HBO Max", "hbo-max")]
    [InlineData("Loki - Disney+", "disney-plus")]
    [InlineData("The Boys - Prime Video", "prime-video")]
    [InlineData("Severance â€” Apple TV", "apple-tv")]
    [InlineData("A match - Globoplay", "globoplay")]
    [InlineData("Some stream - Twitch", "twitch")]
    [InlineData("A film - Plex", "plex")]
    public void RecognizesKnownServices(string text, string expected)
        => Assert.Equal(expected, MediaService.FromText(text)?.Id);

    [Theory]
    [InlineData("Maximum effort")]   // "max" inside a word is not HBO Max
    [InlineData("Plexus anatomy")]   // nor "plex" inside one
    [InlineData("A perfectly ordinary song")]
    [InlineData("")]
    [InlineData(null)]
    public void LeavesUnknownTextAlone(string? text) => Assert.Null(MediaService.FromText(text));

    // "youtube music" must win over "youtube", and "prime video" over nothing at all
    [Fact]
    public void PrefersTheLongerName()
    {
        Assert.Equal("youtube-music", MediaService.FromText("YouTube Music - playlist")?.Id);
        Assert.Equal("YouTube Music", MediaService.FromText("YouTube Music")?.Name);
    }

    [Theory]
    [InlineData("Stranger Things - Netflix - Google Chrome", "Stranger Things - Netflix")]
    [InlineData("(2) Something - Mozilla Firefox", "Something")]
    [InlineData("A page - Microsoft Edge", "A page")]
    [InlineData("No browser here", "No browser here")]
    public void ReadsTheTabOutOfAWindowTitle(string window, string expected)
        => Assert.Equal(expected, MediaService.TabTitle(window));

    // The case this exists for: Windows says the track is "Netflix", and only the window knows the episode.
    [Fact]
    public void TakesTheShowNameFromTheBrowserWindowWhenThePlayerOnlySaysTheService()
    {
        var (service, name, title) = MediaService.Describe("Chrome", "Netflix", ["Stranger Things - Netflix - Google Chrome"]);
        Assert.Equal("netflix", service);
        Assert.Equal("Netflix", name);
        Assert.Equal("Stranger Things", title);
    }

    [Fact]
    public void KeepsAPlayersOwnTrackName()
    {
        // Spotify names its track: the window title must not replace it
        var (service, _, title) = MediaService.Describe("Spotify", "Bohemian Rhapsody");
        Assert.Equal("spotify", service);
        Assert.Null(title);
    }

    [Fact]
    public void ReadsNoWindowTitleForAPlayerThatIsNotABrowser()
    {
        // a stray window title must not leak into a non-browser player's track
        var (_, _, title) = MediaService.Describe("Spotify", "Spotify", ["Stranger Things - Netflix - Google Chrome"]);
        Assert.Null(title);
    }

    [Fact]
    public void FindsTheServiceInTheWindowWhenTheTrackDoesNotNameIt()
    {
        var (service, _, title) = MediaService.Describe("Chrome", "", ["Loki - Disney+ - Google Chrome"]);
        Assert.Equal("disney-plus", service);
        Assert.Equal("Loki", title);
    }

    // The real bug this guards: a browser with several windows showed "YouTube" for a Netflix session, because the
    // other window's title happened to be the longest. The window that names the service in play is the one to read.
    [Fact]
    public void PicksTheWindowOfTheServiceThatIsPlaying()
    {
        string[] windows =
        [
            "(2003) Some Band - A Long Song Title [OFFICIAL VIDEO] - YouTube - Google Chrome",
            "Stranger Things - Netflix - Google Chrome",
            "PhoneBox - Google Chrome",
        ];
        var (service, name, title) = MediaService.Describe("Chrome", "Netflix", windows);
        Assert.Equal("netflix", service);
        Assert.Equal("Netflix", name);
        Assert.Equal("Stranger Things", title);
    }

    [Fact]
    public void WithNoServiceNamedItTakesTheWindowThatNamesOne()
    {
        string[] windows = ["A very long ordinary page title with nothing to recognize - Google Chrome", "Loki - Disney+ - Google Chrome"];
        var (service, _, title) = MediaService.Describe("Chrome", "", windows);
        Assert.Equal("disney-plus", service);
        Assert.Equal("Loki", title);
    }

    // the player named a service, but no window confirms it (its tab was closed, or it is on another desktop):
    // another window's title must not be shown as what is playing
    [Fact]
    public void DoesNotBorrowAnUnrelatedWindowsTitle()
    {
        var (service, _, title) = MediaService.Describe("Chrome", "Netflix", new[] { "Some unrelated page - Google Chrome" });
        Assert.Equal("netflix", service);
        Assert.Null(title);
    }

    [Fact]
    public void WithNoWindowAtAllThePlayersOwnNamesStand()
    {
        var (service, _, title) = MediaService.Describe("Chrome", "Netflix", Array.Empty<string>());
        Assert.Equal("netflix", service);
        Assert.Null(title);
    }

    [Fact]
    public void EnrichKeepsTheRestOfTheReading()
    {
        var snap = new MediaSnapshot("Chrome", "Netflix", "", "", true, 1000, 2000, 3, true);
        var enriched = MediaService.Enrich(snap, ["Dark - Netflix - Google Chrome"])!;
        Assert.Equal("netflix", enriched.Service);
        Assert.Equal("Dark", enriched.ShowTitle);
        Assert.Equal("Netflix", enriched.Title);     // the player's own title is left as it was
        Assert.Equal(1000, enriched.PosMs);
        Assert.Equal(3, enriched.ArtId);
        Assert.True(enriched.Playing);
    }

    [Fact]
    public void EnrichPassesNothingThrough() => Assert.Null(MediaService.Enrich(null));

    [Theory]
    [InlineData("Chrome", true)]
    [InlineData("Edge", true)]
    [InlineData("Firefox", true)]
    [InlineData("Spotify", false)]
    [InlineData(null, false)]
    public void KnowsWhichPlayersHaveAReadableWindowTitle(string? app, bool expected)
        => Assert.Equal(expected, MediaService.IsBrowser(app));
}
