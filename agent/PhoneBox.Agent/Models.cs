namespace PhoneBox.Agent;

// Data shared by the Windows and the Linux / macOS implementations.

public sealed record MediaSnapshot(string App, string Title, string Artist, string Album, bool Playing,
    long PosMs, long DurMs, int ArtId, bool CanSeek)
{
    /// <summary>When the position was read (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>); 0 = not dated.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long SampledAt { get; init; }

    /// <summary>
    /// The streaming service behind this track, as an id the panel draws a logo for ("netflix", "youtube",
    /// "hbo-max"…), or null when it is not one of the known ones. See <see cref="MediaService"/>.
    /// </summary>
    public string? Service { get; init; }

    /// <summary>The service's name as written ("Netflix", "Prime Video"); null together with <see cref="Service"/>.</summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// What the browser's window calls what is playing, when the player itself said no more than the service's name
    /// ("Netflix" for every episode). Null for a player that names its own track, and on every platform but Windows.
    /// </summary>
    public string? ShowTitle { get; init; }

    /// <summary>
    /// How old PosMs is at the moment this is serialized. The phone adds it to what it counts from the arrival, so a
    /// sample that waited for the next 1 s tick (and one a second old when the monitor read it) is not shown late.
    /// </summary>
    public long AgeMs => SampledAt == 0 ? 0 : Math.Max(0, (long)(System.Diagnostics.Stopwatch.GetElapsedTime(SampledAt).TotalMilliseconds));
}

public sealed record AlbumArt(byte[] Bytes, string Mime);

public sealed record AudioSnapshot(int Volume, bool Muted, bool CanControl = true);
