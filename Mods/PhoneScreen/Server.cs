using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneScreen;

/// <summary>A command from a phone, carried out on the game's own thread; <see cref="Args"/>: its plain string fields.</summary>
public sealed record PhoneCommand(string Id, string Cmd, string Character, DateTime Received, IReadOnlyDictionary<string, string> Args)
{
    public string Arg(string name) => Args.TryGetValue(name, out string? v) ? v : "";
    public long Number(string name) => long.TryParse(Arg(name), out long n) ? n : 0;
}

/// <summary>An answer to one request, written as HTTP on the home network or sent back through the relay.</summary>
public sealed record Reply(int Code, string Type, byte[] Body, string Extra = "", string Cache = "no-store");

/// <summary>
/// A deliberately small web server for the phone page: fixed routes, size and time limits, nothing served from
/// disk. It never touches the game: it hands out the latest snapshot the game's thread made, and queues commands
/// for that thread. Reading needs a session from pairing (the one-time code in the QR link).
/// </summary>
public sealed class Server
{
    const int MaxHeader = 8192, MaxBody = 2048, TimeoutMs = 5000, MaxConnections = 24;
    const int MaxFailures = 8;
    static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(10), Lockout = TimeSpan.FromSeconds(60);

    TcpListener? _listener;
    Thread? _thread;
    volatile bool _running;
    int _open;

    volatile byte[] _snapshot = Encoding.UTF8.GetBytes("{\"ready\":false}");
    volatile byte[] _catalog = Encoding.UTF8.GetBytes("{\"version\":0}");
    long _catalogVersion;
    /// <summary>When a phone last asked for the catalogue (the game makes a fresh one while phones want it).</summary>
    public DateTime CatalogWanted { get; private set; }
    readonly ConcurrentDictionary<string, byte[]> _advice = new();
    /// <summary>Areas a phone wants the Combat Advisor's verdicts for (the game works them out a few at a time).</summary>
    public ConcurrentQueue<string> AdviceWanted { get; } = new();
    readonly ConcurrentDictionary<string, byte[]> _icons = new();
    readonly ConcurrentDictionary<string, (byte[] Json, DateTime Made)> _parts = new();
    /// <summary>The phone's other screens ("gear", "farm", ...) and when a phone last asked for each.</summary>
    public ConcurrentDictionary<string, DateTime> PartsWanted { get; } = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]?>> _iconWaits = new();
    /// <summary>Pictures a phone asked for that the game has not drawn yet ("item/MelvorBase:Shrimp").</summary>
    public ConcurrentQueue<string> IconsWanted { get; } = new();
    readonly object _gate = new();
    string _code = "";
    DateTime _codeMade;
    readonly Dictionary<string, Session> _sessions = new();
    readonly Dictionary<string, (int Count, DateTime Until)> _failures = new();
    readonly Dictionary<string, Queue<DateTime>> _recent = new();

    public ConcurrentQueue<PhoneCommand> Commands { get; } = new();
    public IPAddress? Address { get; private set; }
    public int Port { get; private set; }
    public bool Running => _running;
    public string Error { get; private set; } = "";
    /// <summary>Turned on on the PC only; off whenever the server starts.</summary>
    public volatile bool ControlsAllowed;
    public Action<string> Log { get; set; } = _ => { };
    /// <summary>The page, sent as it is (it carries its own styles and script).</summary>
    public string Page { get; set; } = "";

    sealed class Session
    {
        public required string Ip;
        public DateTime LastSeen;
    }

    public string Url => Address == null ? "" : $"http://{(Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString())}:{Port}/";

    /// <summary>The link for the QR code: the page plus the one-time pairing code after '#' (never sent to a server log).</summary>
    public string PairingLink { get { lock (_gate) return Url + "#p=" + _code; } }
    public string Code { get { lock (_gate) return _code; } }

    /// <summary>Raised (on any thread) when a phone pairs or all are signed out.</summary>
    public event Action? SessionsChanged;

    /// <summary>The paired phones, as hashes of their tokens (what may be remembered on disk).</summary>
    public List<string> SessionHashes() { lock (_gate) return _sessions.Keys.ToList(); }

    /// <summary>Phones paired before (hashes of their tokens), signed in again.</summary>
    public void Restore(IEnumerable<string> hashes)
    {
        lock (_gate)
            foreach (string h in hashes.Where(h => h.Length == 64).Take(16))
                _sessions.TryAdd(h, new Session { Ip = "", LastSeen = DateTime.MinValue });
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("mml-phone:" + token)));

    /// <summary>Phones seen in the last 15 seconds.</summary>
    public int Connected { get { lock (_gate) return _sessions.Values.Count(s => DateTime.UtcNow - s.LastSeen < TimeSpan.FromSeconds(15)); } }
    public int Sessions { get { lock (_gate) return _sessions.Count; } }

    public bool Start(IPAddress address, int port)
    {
        Stop();
        Error = "";
        try
        {
            _listener = new TcpListener(address, port);
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(16);
        }
        catch (Exception e)
        {
            Error = e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ? $"Port {port} is already in use." : e.Message;
            _listener = null;
            return false;
        }
        Address = address;
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        ControlsAllowed = false;
        NewCode();
        _running = true;
        _thread = new Thread(Accept) { IsBackground = true, Name = "PhoneScreen server" };
        _thread.Start();
        Log($"Serving the phone page on {Url}");
        return true;
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _thread = null;
        lock (_gate) { _sessions.Clear(); _failures.Clear(); _recent.Clear(); }
        _advice.Clear();
        _parts.Clear();
        foreach (var wait in _iconWaits.Values) wait.TrySetResult(null);
        _iconWaits.Clear();
        ControlsAllowed = false;
        while (Commands.TryDequeue(out _)) { }
    }

    /// <summary>A new one-time pairing code (the old one stops working).</summary>
    public void NewCode()
    {
        lock (_gate)
        {
            _code = Token(10);
            _codeMade = DateTime.UtcNow;
        }
    }

    /// <summary>Signs every phone out.</summary>
    public void RevokeAll()
    {
        lock (_gate) _sessions.Clear();
        NewCode();
        SessionsChanged?.Invoke();
    }

    /// <summary>The game's thread hands over a new snapshot (JSON, already made).</summary>
    public void Publish(byte[] json) => _snapshot = json;

    /// <summary>The game's thread hands over a new catalogue (skills and targets, foods, stances, bank items).</summary>
    public void PublishCatalog(byte[] json)
    {
        long version = 0;
        try { using var doc = JsonDocument.Parse(json); if (doc.RootElement.TryGetProperty("version", out JsonElement v)) version = v.GetInt64(); } catch { }
        _catalog = json;
        Interlocked.Exchange(ref _catalogVersion, version);
    }

    /// <summary>The game's thread hands over the Combat Advisor's verdicts for one area.</summary>
    public void PublishAdvice(string area, byte[] json) => _advice[area] = json;

    /// <summary>The game's thread hands over one of the other screens, freshly made.</summary>
    public void PublishPart(string name, byte[] json) => _parts[name] = (json, DateTime.UtcNow);

    /// <summary>When that screen was made (MinValue: never, or something changed since).</summary>
    public DateTime PartMade(string name) => _parts.TryGetValue(name, out var p) ? p.Made : DateTime.MinValue;

    /// <summary>After a command: every screen is made again before it is shown.</summary>
    public void PartsChanged() { foreach (string k in _parts.Keys) if (_parts.TryGetValue(k, out var p)) _parts[k] = (p.Json, DateTime.MinValue); }

    /// <summary>The game's thread hands over a picture (null: there is none).</summary>
    public void PublishIcon(string key, byte[]? png)
    {
        if (png != null) _icons[key] = png;
        if (_iconWaits.TryRemove(key, out var wait)) wait.TrySetResult(png);
    }

    static string Token(int length)
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        foreach (byte b in bytes) sb.Append(letters[b % letters.Length]);
        return sb.ToString();
    }

    void Accept()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener!.AcceptTcpClient(); }
            catch { if (!_running) return; continue; }
            if (Interlocked.Increment(ref _open) > MaxConnections)
            {
                Interlocked.Decrement(ref _open);
                try { client.Close(); } catch { }
                continue;
            }
            Task.Run(() =>
            {
                try { Handle(client); }
                catch (Exception e) { Log("A phone request failed: " + e.Message); }
                finally
                {
                    Interlocked.Decrement(ref _open);
                    try { client.Close(); } catch { }
                }
            });
        }
    }

    // ------------------------------------------------------------------------------------------- one request

    sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body, string Ip, string Query = "");

    void Handle(TcpClient client)
    {
        client.ReceiveTimeout = TimeoutMs;
        client.SendTimeout = TimeoutMs;
        NetworkStream stream = client.GetStream();
        string ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        var (request, problem) = Read(stream, ip);
        Reply reply = request == null ? new Reply(problem, "text/plain", Encoding.UTF8.GetBytes(Reason(problem))) : Route(request, relayed: false);
        // big answers (the page, the catalogue, the shop) go compressed when the browser takes it: about a tenth the size
        if (request != null && reply.Body.Length > 2048 && !reply.Type.StartsWith("image/")
            && request.Headers.TryGetValue("accept-encoding", out string? enc) && enc.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var packed = new MemoryStream();
            using (var z = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(reply.Body);
            reply = reply with { Body = packed.ToArray(), Extra = reply.Extra + "Content-Encoding: gzip\r\nVary: Accept-Encoding\r\n" };
        }
        Send(stream, reply);
    }

    /// <summary>
    /// A request that came through the relay (already decrypted and checked): answered exactly as on the home network,
    /// pairing, sessions, limits and the controls switch included. Only the Host and Origin checks are skipped: they
    /// keep other web sites away from the home-network server, and a relayed request never reached this PC through a
    /// browser's own connection.
    /// </summary>
    public Reply Relayed(string method, string pathAndQuery, string authorization, string body, string who)
    {
        if (!_running) return Json(503, new { error = "The phone screen is off in the game." });
        string path = pathAndQuery, query = "";
        int q = path.IndexOf('?');
        if (q >= 0) { query = path[(q + 1)..]; path = path[..q]; }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (authorization != "") headers["authorization"] = authorization;
        if (method is not ("GET" or "POST") || path.Length > 300 || body.Length > MaxBody) return Json(400, new { error = "Bad request." });
        return Route(new Request(method, path, headers, body, who, query), relayed: true);
    }

    /// <summary>The same, without holding a thread while the game draws a picture (many come at once over the relay).</summary>
    public async Task<Reply> RelayedAsync(string method, string pathAndQuery, string authorization, string body, string who)
    {
        if (_running && method == "GET" && pathAndQuery.StartsWith("/icon/") && !pathAndQuery.Contains('?'))
        {
            if (!Allowed(who, picture: true)) return Json(429, new { error = "Too many requests. Wait a moment." });
            return await IconAsync(pathAndQuery["/icon/".Length..]);
        }
        return Relayed(method, pathAndQuery, authorization, body, who);
    }

    Reply Route(Request request, bool relayed)
    {
        string ip = request.Ip;
        // pictures are many and harmless (the game's own art, no save data): a bigger allowance of their own
        bool picture = request.Method == "GET" && request.Path.StartsWith("/icon/");
        // (the page itself always opens: a phone must never see a bare error instead of it)
        bool page = request.Method == "GET" && request.Path is "/" or "/index.html" or "/favicon.ico";
        if (!page && !Allowed(ip, picture)) return Json(429, new { error = "Too many requests. Wait a moment." });
        if (!relayed)
        {
            // the page only answers to its own address (no other site can reach it through the phone's browser)
            string host = request.Headers.GetValueOrDefault("host", "");
            if (!HostOk(host)) return Json(421, new { error = "Wrong host." });
            if (request.Method == "POST" && request.Headers.TryGetValue("origin", out string? origin)
                && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? from) || !HostOk(from.Authority)))
                return Json(403, new { error = "Wrong origin." });
        }

        if (picture) return Icon(request.Path["/icon/".Length..]);
        switch (request.Method, request.Path)
        {
            case ("GET", "/"):
            case ("GET", "/index.html"):
                return new Reply(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page),
                     "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n");
            case ("GET", "/api/catalog"):
            {
                if (!Signed(request)) return Json(401, new { error = "Not paired." });
                CatalogWanted = DateTime.UtcNow;
                // not made yet (the game makes it the first time a phone asks): "wait", never an empty one
                if (Interlocked.Read(ref _catalogVersion) == 0) return Json(202, new { pending = true });
                // the phone says which version it has: unchanged, a few bytes instead of the whole catalogue
                if (long.TryParse(Query(request.Query, "v"), out long have) && have > 0 && have == Interlocked.Read(ref _catalogVersion))
                    return new Reply(200, "application/json", "{\"same\":true}"u8.ToArray());
                return new Reply(200, "application/json", _catalog);
            }
            case ("GET", "/api/advise"):
            {
                if (!Signed(request)) return Json(401, new { error = "Not paired." });
                string area = Query(request.Query, "area");
                if (area.Length is 0 or > 80 || !SafeId(area)) return Json(400, new { error = "Which area?" });
                if (_advice.TryGetValue(area, out byte[]? advice)) return new Reply(200, "application/json", advice);
                if (AdviceWanted.Count < 8 && !AdviceWanted.Contains(area)) AdviceWanted.Enqueue(area);
                return Json(202, new { pending = true });
            }
            case ("GET", "/api/part"):
            {
                if (!Signed(request)) return Json(401, new { error = "Not paired." });
                string name = Query(request.Query, "n");
                if (Array.IndexOf(Parts.Names, name) < 0) return Json(400, new { error = "Which screen?" });
                PartsWanted[name] = DateTime.UtcNow;
                if (_parts.TryGetValue(name, out var part)) return new Reply(200, "application/json", part.Json);
                return Json(202, new { pending = true });
            }
            case ("POST", "/api/pair"):
                return Pair(request);
            case ("GET", "/api/state"):
            {
                if (!Signed(request)) return Json(401, new { error = "Not paired. Scan the QR code in the game again." });
                byte[] snap = _snapshot;
                var head = Encoding.UTF8.GetBytes($"{{\"now\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"controls\":{(ControlsAllowed ? "true" : "false")},\"state\":");
                var body = new byte[head.Length + snap.Length + 1];
                head.CopyTo(body, 0);
                snap.CopyTo(body, head.Length);
                body[^1] = (byte)'}';
                return new Reply(200, "application/json", body);
            }
            case ("POST", "/api/cmd"):
                if (!Signed(request)) return Json(401, new { error = "Not paired." });
                if (!ControlsAllowed) return Json(403, new { error = "Controls are off. Turn them on in the game (Phone window)." });
                return Command(request);
            case ("GET", "/favicon.ico"):
                return new Reply(204, "text/plain", Array.Empty<byte>());
            default:
                return Json(request.Method is "GET" or "POST" ? 404 : 405, new { error = "Not here." });
        }
    }

    bool HostOk(string host)
    {
        if (Address == null) return false;
        string bound = Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString();
        return string.Equals(host, $"{bound}:{Port}", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.IsLoopback(Address) && string.Equals(host, $"localhost:{Port}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>At most 40 requests in 4 seconds from one address (the page asks once a second); pictures: 150.</summary>
    bool Allowed(string ip, bool picture = false)
    {
        lock (_gate)
        {
            if (_failures.TryGetValue(ip, out var f) && f.Count >= MaxFailures && DateTime.UtcNow < f.Until) return false;
            if (ip.StartsWith("relay:") && _failures.TryGetValue("relay", out var all) && all.Count >= MaxFailures * 2 && DateTime.UtcNow < all.Until) return false;
            string key = picture ? ip + "#icons" : ip;
            if (!_recent.TryGetValue(key, out Queue<DateTime>? q)) _recent[key] = q = new Queue<DateTime>();
            DateTime now = DateTime.UtcNow;
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromSeconds(4)) q.Dequeue();
            if (q.Count >= (picture ? 150 : 40)) return false;
            q.Enqueue(now);
            return true;
        }
    }

    /// <summary>Ids the page may name: the game's own ("MelvorBase:Shrimp"), nothing else.</summary>
    static bool SafeId(string id) => id.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-' or '.');

    /// <summary>A picture of the game's (a skill, an item, a monster), drawn by the game's thread and kept.</summary>
    Reply Icon(string key) => IconAsync(key).GetAwaiter().GetResult();

    async Task<Reply> IconAsync(string key)
    {
        int slash = key.IndexOf('/');
        if (!key.EndsWith(".png") || slash <= 0) return Json(404, new { error = "No picture." });
        key = key[..^4];
        string kind = key[..slash], id = Uri.UnescapeDataString(key[(slash + 1)..]);
        if (kind is not ("skill" or "item" or "enemy" or "shop" or "prayer" or "spell" or "imbue" or "master") || id.Length is 0 or > 80 || !SafeId(id)) return Json(404, new { error = "No picture." });
        key = kind + "/" + id;
        if (!_icons.TryGetValue(key, out byte[]? png))
        {
            var wait = _iconWaits.GetOrAdd(key, k => { IconsWanted.Enqueue(k); return new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously); });
            try { png = await wait.Task.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { png = null; }
        }
        if (png == null) return new Reply(404, "text/plain", Array.Empty<byte>());
        return new Reply(200, "image/png", png, "", "public, max-age=86400");
    }

    static string Query(string query, string name)
    {
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == name) return Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return "";
    }

    void Failed(string ip)
    {
        if (ip.StartsWith("relay:")) Failed("relay");
        lock (_gate)
        {
            var f = _failures.GetValueOrDefault(ip);
            if (DateTime.UtcNow >= f.Until && f.Count >= MaxFailures) f = default;
            f.Count++;
            f.Until = DateTime.UtcNow + Lockout;
            _failures[ip] = f;
        }
    }

    bool Signed(Request r)
    {
        string auth = r.Headers.GetValueOrDefault("authorization", "");
        if (!auth.StartsWith("Bearer ")) { Failed(r.Ip); return false; }
        string token = auth["Bearer ".Length..].Trim();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(Hash(token), out Session? s)) { Failed(r.Ip); return false; }
            s.LastSeen = DateTime.UtcNow;
            return true;
        }
    }

    Reply Pair(Request r)
    {
        string code = Field(r.Body, "code");
        Reply reply;
        lock (_gate)
        {
            bool fresh = DateTime.UtcNow - _codeMade < CodeLife;
            if (code.Length == 0 || !fresh || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(_code)))
            {
                Failed(r.Ip);
                return Json(403, new { error = fresh ? "This pairing code is not valid. Scan the QR code in the game again." : "This pairing code has expired. Scan the new QR code in the game." });
            }
            string token = Token(32);
            _sessions[Hash(token)] = new Session { Ip = r.Ip, LastSeen = DateTime.UtcNow };
            _code = Token(10);              // one phone per code: the game shows a new one
            _codeMade = DateTime.UtcNow;
            reply = Json(200, new { token });
        }
        SessionsChanged?.Invoke();
        return reply;
    }

    /// <summary>What a phone may ask for; each is checked again on the game's thread.</summary>
    public static readonly HashSet<string> KnownCommands = new()
    {
        "eat", "pausequeue", "resumequeue", "start", "stop", "stopall", "food", "stance",
        "qadd", "qremove", "qmove", "qclear", "qload", "qsave", "sell",
        "equip", "unequip", "loadout", "potion", "prayer", "spell", "imbue", "slayer",
        "plant", "harvest", "buyplot", "buy", "adv", "bulk", "slots", "slotdrop", "slotwait", "cheat",
    };

    Reply Command(Request r)
    {
        var args = Fields(r.Body);
        string id = args.GetValueOrDefault("id", ""), cmd = args.GetValueOrDefault("cmd", ""), character = args.GetValueOrDefault("character", "");
        if (id.Length is 0 or > 40 || !KnownCommands.Contains(cmd)) return Json(400, new { error = "Unknown command." });
        if (args.Values.Any(v => v.Length > 120)) return Json(400, new { error = "Too long." });
        if (Commands.Count > 20) return Json(429, new { error = "Too many commands at once." });
        Commands.Enqueue(new PhoneCommand(id, cmd, character, DateTime.UtcNow, args));
        return Json(202, new { queued = true, id });
    }

    /// <summary>The body's plain fields (strings and numbers, as strings); at most 16.</summary>
    static Dictionary<string, string> Fields(string json)
    {
        var all = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return all;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (all.Count >= 16) break;
                if (p.Value.ValueKind == JsonValueKind.String) all[p.Name] = p.Value.GetString() ?? "";
                else if (p.Value.ValueKind == JsonValueKind.Number) all[p.Name] = p.Value.GetRawText();
            }
        }
        catch { }
        return all;
    }

    static string Field(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>Reads the request line, the headers (8 KB at most) and a body (2 KB at most, Content-Length only).</summary>
    static (Request?, int) Read(NetworkStream stream, string ip)
    {
        var buffer = new byte[MaxHeader + MaxBody];
        int filled = 0, end = -1;
        try
        {
            while (end < 0)
            {
                if (filled >= MaxHeader) return (null, 431);
                int n = stream.Read(buffer, filled, MaxHeader - filled);
                if (n <= 0) return (null, 400);
                filled += n;
                end = IndexOf(buffer, filled, "\r\n\r\n"u8);
            }
        }
        catch (IOException) { return (null, 408); }
        string head = Encoding.ASCII.GetString(buffer, 0, end);
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.")) return (null, 400);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return (null, 400);
            string key = line[..colon].Trim().ToLowerInvariant();
            if (headers.ContainsKey(key)) return (null, 400);       // (no repeated headers)
            headers[key] = line[(colon + 1)..].Trim();
        }
        if (headers.ContainsKey("transfer-encoding")) return (null, 501);
        int length = 0;
        if (headers.TryGetValue("content-length", out string? cl) && (!int.TryParse(cl, out length) || length < 0)) return (null, 400);
        if (length > MaxBody) return (null, 413);
        int have = filled - (end + 4);
        var body = new byte[length];
        Array.Copy(buffer, end + 4, body, 0, Math.Min(have, length));
        try
        {
            while (have < length)
            {
                int n = stream.Read(body, have, length - have);
                if (n <= 0) return (null, 400);
                have += n;
            }
        }
        catch (IOException) { return (null, 408); }
        string path = first[1], query = "";
        int q = path.IndexOf('?');
        if (q >= 0) { query = path[(q + 1)..]; path = path[..q]; }
        return (new Request(first[0], path, headers, Encoding.UTF8.GetString(body), ip, query), 0);
    }

    static int IndexOf(byte[] data, int count, ReadOnlySpan<byte> what) => data.AsSpan(0, count).IndexOf(what);

    static string Reason(int code) => code switch
    {
        200 => "OK", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 405 => "Method Not Allowed", 408 => "Request Timeout", 413 => "Payload Too Large", 421 => "Misdirected Request",
        429 => "Too Many Requests", 503 => "Service Unavailable", 431 => "Request Header Fields Too Large", 501 => "Not Implemented", _ => "Error",
    };

    static Reply Json(int code, object value) => new(code, "application/json", JsonSerializer.SerializeToUtf8Bytes(value));

    static void Send(NetworkStream stream, Reply r)
    {
        string head = $"HTTP/1.1 {r.Code} {Reason(r.Code)}\r\nContent-Type: {r.Type}\r\nContent-Length: {r.Body.Length}\r\nCache-Control: {r.Cache}\r\n"
                      + "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n" + r.Extra + "\r\n";
        try
        {
            stream.Write(Encoding.ASCII.GetBytes(head));
            stream.Write(r.Body);
            stream.Flush();
        }
        catch { }
    }
}
