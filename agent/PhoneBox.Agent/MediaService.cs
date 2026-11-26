namespace PhoneBox.Agent;

/// <summary>
/// The streaming service behind what is playing, for the Now playing screen widget. Windows reports a browser as the
/// player ("Chrome") and often puts the service's own name in the track title ("Netflix"), which says nothing about
/// the episode. The service is recognized from the player's name, the track title and, for a browser, the title of its
/// window, which is where the real name sits ("Stranger Things - Netflix - Google Chrome").
/// Kept apart from <see cref="MediaMonitor"/> so it compiles, and is tested, on every platform.
/// </summary>
internal static class MediaService
{
    /// <summary>A known service: the id the panel draws a logo for, and the name it writes.</summary>
    internal sealed record Known(string Id, string Name);

    /// <summary>
    /// The Now playing screen widget's live video (<c>PlayerCapture</c>, Windows only): the rates a widget may ask
    /// for, and the one it uses unless its options say otherwise. Here rather than in the capture itself so the rules
    /// compile, and are tested, on every platform. 5 a second measured about 9% of one core while 15 took 71%, so the
    /// gentle rate is the default and the fast one is a choice.
    /// </summary>
    internal const int MinFps = 1, MaxFps = 15, DefaultFps = 5;

    /// <summary>
    /// How long a frame request waits for the next grab before it answers "nothing new" (<see cref="FrameSignal"/>).
    /// Must outlast one interval at <see cref="MinFps"/>, so a waiting panel is woken by a frame rather than by this.
    /// </summary>
    internal const int FrameWaitMs = 1500;

    /// <summary>The rate a widget asks for, kept to what the capture allows.</summary>
    internal static int RateOf(WidgetConfig? widget) =>
        Math.Clamp(widget?.Int("fps") ?? DefaultFps, MinFps, MaxFps);

    /// <summary>What a window or track title has to contain, longest first so "youtube music" wins over "youtube".</summary>
    private static readonly (string Needle, Known Service)[] Services =
    [
        ("youtube music", new("youtube-music", "YouTube Music")),
        ("prime video", new("prime-video", "Prime Video")),
        ("amazon prime", new("prime-video", "Prime Video")),
        ("disney+", new("disney-plus", "Disney+")),
        ("disneyplus", new("disney-plus", "Disney+")),
        ("apple tv", new("apple-tv", "Apple TV+")),
        ("hbo max", new("hbo-max", "HBO Max")),
        ("globoplay", new("globoplay", "Globoplay")),
        ("crunchyroll", new("crunchyroll", "Crunchyroll")),
        ("soundcloud", new("soundcloud", "SoundCloud")),
        ("paramount+", new("paramount-plus", "Paramount+")),
        ("netflix", new("netflix", "Netflix")),
        ("youtube", new("youtube", "YouTube")),
        ("spotify", new("spotify", "Spotify")),
        ("twitch", new("twitch", "Twitch")),
        ("deezer", new("deezer", "Deezer")),
        ("tidal", new("tidal", "Tidal")),
        ("vimeo", new("vimeo", "Vimeo")),
        ("plex", new("plex", "Plex")),
        ("max", new("hbo-max", "HBO Max")),
    ];

    /// <summary>The players whose window title names the service: only these have their titles read.</summary>
    private static readonly string[] Browsers = ["Chrome", "Edge", "Firefox", "Brave", "Opera", "Vivaldi", "Chromium"];

    internal static bool IsBrowser(string? app) => app is not null && Browsers.Contains(app, StringComparer.OrdinalIgnoreCase);

    /// <summary>The window-title tails a browser adds, dropped before the title is read.</summary>
    private static readonly string[] BrowserSuffixes =
    [
        " - Google Chrome", " — Google Chrome", " - Chromium", " - Microsoft Edge", " - Microsoft Edge", " — Microsoft Edge",
        " - Mozilla Firefox", " — Mozilla Firefox", " - Brave", " - Opera", " - Vivaldi",
    ];

    /// <summary>What a browser tab is called, without the browser's own name and without a notification count.</summary>
    internal static string TabTitle(string windowTitle)
    {
        var title = (windowTitle ?? "").Trim();
        foreach (var suffix in BrowserSuffixes)
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { title = title[..^suffix.Length].TrimEnd(); break; }
        // "(2) Something" — Chrome and Firefox put the unread count of the tab's site in front
        if (title.StartsWith('(') && title.IndexOf(')') is var close && close is > 1 and < 7
            && title[1..close].All(char.IsAsciiDigit)) title = title[(close + 1)..].TrimStart();
        return title;
    }

    /// <summary>The service a text names, or null: the longest needle it contains, as a whole word.</summary>
    internal static Known? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lower = text.ToLowerInvariant();
        foreach (var (needle, service) in Services)
            if (Contains(lower, needle)) return service;
        return null;
    }

    /// <summary>
    /// <paramref name="needle"/> in <paramref name="lower"/> as a word of its own: "max" must not match "Maximum" or
    /// "Climax", and "plex" must not match "Plexus". A needle's own punctuation ("disney+") still counts as part of it.
    /// </summary>
    private static bool Contains(string lower, string needle)
    {
        for (var at = lower.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = lower.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            var beforeOk = at == 0 || !char.IsLetterOrDigit(lower[at - 1]);
            var after = at + needle.Length;
            var afterOk = after >= lower.Length || !char.IsLetterOrDigit(lower[after]);
            if (beforeOk && afterOk) return true;
        }
        return false;
    }

    /// <summary>
    /// The service and the name to show for a track. <paramref name="windowTitles"/> are the player's window titles,
    /// read only for a browser (empty otherwise). The window's title is preferred over the one the player reports when
    /// the player only gives the service's name ("Netflix"), which is what Windows reports for every Netflix episode.
    /// </summary>
    /// <remarks>
    /// A browser usually has several windows. The one that belongs to the track is the one whose tab names the service
    /// the player (or the track) already named: picking merely the longest title showed "YouTube" for a Netflix
    /// session, because another window happened to have a longer title. Only when nothing names a service is the
    /// longest title used, as the fullest guess at the window in play.
    /// </remarks>
    internal static (string? Service, string? ServiceName, string? Title) Describe(string? app, string? trackTitle, IEnumerable<string>? windowTitles)
    {
        var known = FromText(trackTitle) ?? FromText(app);
        if (!IsBrowser(app)) return (known?.Id, known?.Name, null);

        var tabs = (windowTitles ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(TabTitle)
            .Where(t => t.Length > 0).ToList();
        if (tabs.Count == 0) return (known?.Id, known?.Name, null);

        // the window of the service already known, else the one that names any service, else the fullest title
        var tab = known is not null ? tabs.FirstOrDefault(t => FromText(t)?.Id == known.Id) : null;
        if (tab is null && known is null)
        {
            tab = tabs.FirstOrDefault(t => FromText(t) is not null) ?? tabs.OrderByDescending(t => t.Length).First();
            known = FromText(tab);
        }
        // the player named a service no window confirms (its tab is gone or on another screen): its own title stands
        if (tab is null) return (known?.Id, known?.Name, null);

        // the tab is the episode ("Stranger Things - Netflix"); drop the service's tail so only the name is left
        if (known is not null) tab = StripService(tab, known.Name);
        // a player that said no more than the service's name has nothing to lose to the tab's title
        var trackIsServiceOnly = string.IsNullOrWhiteSpace(trackTitle)
            || (known is not null && trackTitle!.Trim().Equals(known.Name, StringComparison.OrdinalIgnoreCase));
        return (known?.Id, known?.Name, trackIsServiceOnly && tab.Length > 0 ? tab : null);
    }

    /// <summary>The player's and the track's own names only, for a platform that reads no window titles.</summary>
    internal static (string? Service, string? ServiceName, string? Title) Describe(string? app, string? trackTitle)
        => Describe(app, trackTitle, null);

    /// <summary>
    /// The same reading with its service filled in. <paramref name="windowTitles"/> are the player's window titles
    /// where the platform can read them (Windows); without them only the player's and the track's own names are used.
    /// </summary>
    internal static MediaSnapshot? Enrich(MediaSnapshot? snap, IEnumerable<string>? windowTitles = null)
    {
        if (snap is null) return null;
        var (service, serviceName, title) = Describe(snap.App, snap.Title, windowTitles);
        return snap with { Service = service, ServiceName = serviceName, ShowTitle = title };
    }

    /// <summary>"Stranger Things - Netflix" → "Stranger Things"; a title that is only the service name is left alone.</summary>
    private static string StripService(string title, string serviceName)
    {
        foreach (var dash in new[] { " - ", " — ", " | ", " · " })
        {
            var tail = dash + serviceName;
            if (title.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return title[..^tail.Length].TrimEnd();
            var head = serviceName + dash;
            if (title.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return title[head.Length..].TrimStart();
        }
        return title;
    }
}
