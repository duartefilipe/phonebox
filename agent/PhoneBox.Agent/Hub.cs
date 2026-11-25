using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>A computer whose agent this hub reads (Settings › Computers): the pairing details the phone would keep.</summary>
public sealed class HubSource
{
    /// <summary>The computer agent's certificate fingerprint: pins its TLS, and names it in pages and widgets.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Hosts { get; set; } = [];
    public int Port { get; set; } = 8766;
    /// <summary>The computer's Wi-Fi key; never sent to a phone.</summary>
    public string Token { get; set; } = "";
}

public sealed class HubConfig
{
    public List<HubSource> Sources { get; set; } = [];
}

/// <summary>
/// Hub mode: the agent runs on a home server (Docker) instead of a desktop. The phones connect to it over Wi-Fi, it reads
/// the computers' agents the way a phone would (<see cref="HubSources"/>), and its settings page is reachable from the
/// network on <see cref="AdminPort"/>, over TLS and behind a password (<see cref="HubAdmin"/>).
/// </summary>
public static class HubMode
{
    public static bool Enabled { get; private set; }
    public static string? Password { get; private set; }
    public static int AdminPort { get; private set; } = 8767;
    /// <summary>The settings page is open to the network only with a password set.</summary>
    public static bool AdminActive => Enabled && !string.IsNullOrEmpty(Password);

    public static void Init(string[] args, Func<string, string?> env)
    {
        Enabled = args.Contains("--hub") || env("PHONEBOX_HUB") is "1" or "true" or "yes";
        Password = env("PHONEBOX_PASSWORD");
        AdminPort = int.TryParse(env("PHONEBOX_ADMIN_PORT"), out var p) && p is >= 1024 and <= 65535 ? p : 8767;
    }

    internal static void Reset() { Enabled = false; Password = null; AdminPort = 8767; }
}

/// <summary>Password login for the hub's settings page: a session cookie, refused after a burst of wrong passwords.</summary>
public sealed class HubAdmin(Func<long>? clock = null)
{
    public const string Cookie = "pbhub";
    public const long SessionMs = 30L * 24 * 3600_000;
    private const int MaxFailures = 5;
    private const long LockMs = 60_000;

    private readonly Func<long> _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly ConcurrentDictionary<string, long> _sessions = new();
    private readonly Lock _lock = new();
    private int _failures;
    private long _lockedUntil;

    /// <summary>A new session id for a right password; null for a wrong one, or while locked after too many.</summary>
    public string? Login(string? given, string? password)
    {
        lock (_lock)
        {
            if (_clock() < _lockedUntil || string.IsNullOrEmpty(password)) return null;
            var ok = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given ?? "")), SHA256.HashData(Encoding.UTF8.GetBytes(password)));
            if (!ok)
            {
                if (++_failures >= MaxFailures) { _lockedUntil = _clock() + LockMs; _failures = 0; }
                return null;
            }
            _failures = 0;
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[id] = _clock() + SessionMs;
        return id;
    }

    public bool Valid(string? session)
    {
        if (string.IsNullOrEmpty(session) || !_sessions.TryGetValue(session, out var until)) return false;
        if (_clock() < until) return true;
        _sessions.TryRemove(session, out _);
        return false;
    }

    public void Logout(string? session) { if (session is not null) _sessions.TryRemove(session, out _); }

    public static string LoginPage(bool failed) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>PhoneBox hub</title><style>
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0f14;color:#e6edf3;font:16px system-ui,sans-serif}
        form{display:grid;gap:12px;width:min(320px,90vw)}input,button{font:inherit;padding:10px 12px;border-radius:8px;border:1px solid #30363d}
        input{background:#0d1117;color:inherit}button{background:#2dd4bf;color:#04201c;border:0;font-weight:600}p{margin:0;color:#f87171}
        </style></head><body><form method="post" action="/hub/login"><h1 style="margin:0 0 8px;font-size:22px">PhoneBox hub</h1>
        <input type="password" name="password" placeholder="Password" autocomplete="current-password" autofocus required>
        {{(failed ? "<p>Wrong password, or too many tries: wait a minute.</p>" : "")}}<button type="submit">Sign in</button></form></body></html>
        """;
}

/// <summary>
/// Reads every computer of <see cref="HubConfig"/> once a second, as a phone would: its state over HTTPS, the
/// certificate pinned to the stored fingerprint, with its Wi-Fi key. The phones get each computer's state in the hub's
/// own (<c>sources</c>), and their requests for one computer go through <see cref="ForwardAsync"/>.
/// </summary>
public sealed class HubSources(ConfigStore config, ILogger<HubSources> log) : BackgroundService
{
    /// <summary>Failed reads in a row before a computer counts as off (about 3 s).</summary>
    internal const int OffAfter = 2;
    /// <summary>State fields that belong to the hub itself, not to a computer.</summary>
    private static readonly string[] HubOwned = ["events", "evSeq", "cfgVersion", "pushVersion", "update", "rules", "rulesPaused", "sources", "ts"];

    private sealed class Live
    {
        public string Name = "";
        public bool Online;
        public int Failures;
        public string? Power;
        public long Seen;
        public JsonObject? State;
        public int Preferred;
    }

    private readonly ConcurrentDictionary<string, Live> _live = new();
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    /// <summary>Each computer with whether it answers, why it left ("shutdown", "sleep") and its last state.</summary>
    public IReadOnlyList<object> StateList()
    {
        var list = new List<object>();
        foreach (var s in config.Get().Hub.Sources)
        {
            var live = _live.GetValueOrDefault(s.Id);
            list.Add(new { id = s.Id, name = s.Name, online = live?.Online == true, power = live?.Power, state = live?.Online == true ? live.State : null });
        }
        return list;
    }

    /// <summary>For the settings page: the computers without their states or keys.</summary>
    public IReadOnlyList<object> Summary() => config.Get().Hub.Sources
        .Select(s => (object)new { id = s.Id, name = s.Name, hosts = s.Hosts, port = s.Port, online = _live.GetValueOrDefault(s.Id)?.Online == true })
        .ToList();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!HubMode.Enabled) return;
        log.LogInformation("hub mode: reading the paired computers");
        while (!ct.IsCancellationRequested)
        {
            var sources = config.Get().Hub.Sources;
            foreach (var gone in _live.Keys.Except(sources.Select(s => s.Id)).ToList()) _live.TryRemove(gone, out _);
            await Task.WhenAll(sources.Select(s => PollAsync(s, ct)));
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(HubSource source, CancellationToken ct)
    {
        var live = _live.GetOrAdd(source.Id, _ => new Live());
        live.Name = source.Name;
        var wasOnline = live.Online;
        var wasPower = live.Power;
        try
        {
            using var reply = await SendAsync(source, live, HttpMethod.Get, "/api/state", null, null, TimeSpan.FromSeconds(2), ct);
            if (reply is null || !reply.IsSuccessStatusCode) throw new HttpRequestException("no state");
            var node = JsonNode.Parse(await reply.Content.ReadAsStringAsync(ct))?.AsObject() ?? throw new JsonException("empty state");
            foreach (var key in HubOwned) node.Remove(key);
            live.Power = node["power"]?.GetValue<string?>();
            live.State = node;
            live.Online = true;
            live.Failures = 0;
            live.Seen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (++live.Failures >= OffAfter) live.Online = false;
        }
        if (wasOnline != live.Online || wasPower != live.Power)
        {
            log.LogInformation("hub: {Name} is {State}", source.Name, live.Online ? "on" : "off");
            StateSignal.Poke();
        }
    }

    /// <summary>One request to a computer, over the address that answered last; null when none answers.</summary>
    private async Task<HttpResponseMessage?> SendAsync(HubSource source, Live? live, HttpMethod method, string pathAndQuery,
        byte[]? body, string? contentType, TimeSpan timeout, CancellationToken ct)
    {
        var client = Client(source);
        var start = live?.Preferred ?? 0;
        for (var n = 0; n < source.Hosts.Count; n++)
        {
            var i = (start + n) % source.Hosts.Count;
            using var req = new HttpRequestMessage(method, $"https://{Bracket(source.Hosts[i])}:{source.Port}{pathAndQuery}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", source.Token);
            if (body is not null)
            {
                req.Content = new ByteArrayContent(body);
                if (contentType is not null) req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                var reply = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (live is not null) live.Preferred = i;
                return reply;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* the next address */ }
        }
        return null;
    }

    private static string Bracket(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    private HttpClient Client(HubSource source) => _clients.GetOrAdd(source.Id + "|" + source.Token, _ => new HttpClient(PinnedHandler(source.Id))
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    /// <summary>TLS that trusts only the certificate with this SHA-256 fingerprint (the agent's self-signed one).</summary>
    internal static HttpClientHandler PinnedHandler(string fingerprint) => new()
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert is not null && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant()),
                Encoding.ASCII.GetBytes(fingerprint.ToLowerInvariant())),
    };

    /// <summary>Paths a phone may send to a computer through the hub: its own phone API, less what is the hub's.</summary>
    public static bool Forwardable(string method, PathString path) =>
        method is "GET" or "POST" && Lan.AllowedOverLan(path) && !Lan.IsWebhook(path)
        && !path.StartsWithSegments("/api/stream") && !path.StartsWithSegments("/api/trackpad") && !path.StartsWithSegments("/api/src")
        && !path.StartsWithSegments("/api/pair") && !path.StartsWithSegments("/api/config") && !path.StartsWithSegments("/api/state");

    public const int MaxForwardBody = 64 * 1024;

    /// <summary>GET or POST /api/src/{id}/... from a phone: the same request to that computer, its answer passed back.</summary>
    public async Task ForwardAsync(HttpContext ctx, string id, string rest)
    {
        var path = new PathString("/api/" + rest);
        var source = config.Get().Hub.Sources.FirstOrDefault(s => s.Id == id);
        if (source is null || !Forwardable(ctx.Request.Method, path)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        byte[]? body = null;
        if (ctx.Request.Method == "POST")
        {
            if (ctx.Request.ContentLength is > MaxForwardBody) { ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            if (ms.Length > MaxForwardBody) { ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
            body = ms.ToArray();
        }
        using var reply = await SendAsync(source, _live.GetValueOrDefault(id), new HttpMethod(ctx.Request.Method),
            path + ctx.Request.QueryString, body, ctx.Request.ContentType, TimeSpan.FromSeconds(30), ctx.RequestAborted);
        if (reply is null) { ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        ctx.Response.StatusCode = (int)reply.StatusCode;
        if (reply.Content.Headers.ContentType is { } type) ctx.Response.ContentType = type.ToString();
        await reply.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    /// <summary>The pairing link of a computer's "Pair a phone over Wi-Fi" QR code, read into its parts.</summary>
    public sealed record PairLink(string Pc, string Fingerprint, int Port, List<string> Hosts, string Code);

    public static PairLink? ParseLink(string? link)
    {
        var hash = link?.IndexOf('#') ?? -1;
        if (hash < 0) return null;
        var fields = link![(hash + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        string? F(string k) => fields.TryGetValue(k, out var v) ? Uri.UnescapeDataString(v) : null;
        var fp = F("fp")?.ToLowerInvariant();
        var hosts = (fields.GetValueOrDefault("h") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString)
            .Where(h => h.Length is > 0 and <= 253).Distinct().Take(8).ToList();
        if (F("v") != "1" || fp is null || fp.Length != 64 || !fp.All(Uri.IsHexDigit) || !int.TryParse(F("port"), out var port)
            || port is < 1 or > 65535 || hosts.Count == 0 || F("c") is not { Length: > 0 and <= 64 } code) return null;
        return new PairLink(F("pc") ?? "", fp, port, hosts, code);
    }

    /// <summary>Pairs with a computer from its pairing link and stores it; an error message, or null when it was added.</summary>
    public async Task<string?> AddAsync(string? link, CancellationToken ct)
    {
        if (ParseLink(link) is not { } p) return "This is not a PhoneBox pairing link";
        if (config.Get().Hub.Sources.Any(s => s.Id == p.Fingerprint)) return "This computer is already added";
        using var client = new HttpClient(PinnedHandler(p.Fingerprint)) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var host in p.Hosts)
        {
            try
            {
                using var reply = await client.PostAsync($"https://{Bracket(host)}:{p.Port}/api/pair/claim",
                    new StringContent(JsonSerializer.Serialize(new { code = p.Code }), Encoding.UTF8, "application/json"), ct);
                if (reply.StatusCode == HttpStatusCode.Forbidden) return "The computer refused the code: start a new pairing there and copy the new link";
                if (!reply.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
                var r = doc.RootElement;
                var token = r.GetProperty("token").GetString();
                if (string.IsNullOrEmpty(token)) return "The computer sent no key";
                var hosts = r.TryGetProperty("hosts", out var hs) && hs.ValueKind == JsonValueKind.Array
                    ? hs.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : p.Hosts;
                var source = new HubSource
                {
                    Id = p.Fingerprint,
                    Name = (r.TryGetProperty("pc", out var pc) ? pc.GetString() : null) is { Length: > 0 } n ? n : p.Pc,
                    Hosts = hosts.Count > 0 ? hosts : p.Hosts,
                    Port = r.TryGetProperty("port", out var port) && port.TryGetInt32(out var pt) ? pt : p.Port,
                    Token = token,
                };
                return Update(c => c.Hub.Sources.Add(source));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* the next address */ }
        }
        return "The computer did not answer: check that its Wi-Fi option is on and that this server can reach it";
    }

    public string? Remove(string id) => config.Get().Hub.Sources.Any(s => s.Id == id)
        ? Update(c => c.Hub.Sources.RemoveAll(s => s.Id == id))
        : "No such computer";

    private string? Update(Action<AgentConfig> change)
    {
        var copy = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(config.Get(), ConfigStore.WriteOpts), ConfigStore.ReadOpts)!;
        change(copy);
        return config.Save(JsonSerializer.Serialize(copy, ConfigStore.WriteOpts));
    }
}
