using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Keeping one layout on the hub and on every computer it reads. The hub owns the pages: a change made in its own
/// settings reaches each computer, and a change made in a computer's own settings is taken up by the hub and passed
/// on to the others, so the panel shows the same thing wherever it is edited.
/// </summary>
/// <remarks>
/// Each side is compared with the layout they last agreed on (<see cref="HubSource.LayoutHash"/>, the hub's own in
/// <see cref="HubConfig.LayoutHash"/>), so "changed" means changed since the last sync rather than merely different.
/// With changes on both sides since then the hub wins, which is the rule the setting states.
/// </remarks>
public static class Layout
{
    /// <summary>How much of a layout may travel between a hub and a computer.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>What travels: the pages and the home page, which names one of them.</summary>
    public sealed class Payload
    {
        public List<PageConfig> Pages { get; set; } = [];
        /// <summary>The panel's home page (Settings › Display); null leaves the receiving side's own.</summary>
        public string? HomePage { get; set; }
    }

    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Payload Of(AgentConfig c) => new() { Pages = c.Pages, HomePage = c.Display.HomePage };

    /// <summary>
    /// A layout's fingerprint: the same pages give the same text on both sides, because both have been through
    /// <c>ConfigStore.Normalize</c> before they are compared.
    /// </summary>
    public static string Hash(Payload layout)
    {
        var json = JsonSerializer.Serialize(layout, Opts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();
    }

    public static string Hash(AgentConfig c) => Hash(Of(c));

    /// <summary>The layout a request carries, or null when it is not one (too big, not JSON, or no pages at all).</summary>
    public static Payload? Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(json, ConfigStore.ReadOpts);
            // an empty list would wipe the pages and have the defaults built in their place: refused, not applied
            return payload is { Pages.Count: > 0 } ? payload : null;
        }
        catch (JsonException) { return null; }
    }
}
