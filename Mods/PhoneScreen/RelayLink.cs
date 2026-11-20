using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneScreen;

/// <summary>
/// The game's side of "Reach from anywhere": one outgoing connection to the relay (nothing to open at home), and
/// an encrypted channel to each phone through it. The relay passes frames along and sees only ciphertext. A phone's
/// requests are answered by <see cref="Server"/> exactly as on the home network (pairing, sessions, controls).
/// The protocol ("mml1") is written out in Relay\worker.js, above the loader page.
/// </summary>
public sealed class RelayLink
{
    const int MaxFromPhone = 17 * 1024, MaxBody = 480 * 1024;
    static readonly byte[] WelcomeAd = Encoding.UTF8.GetBytes("mml1:welcome:"), UpAd = Encoding.UTF8.GetBytes("mml1:up"), DownAd = Encoding.UTF8.GetBytes("mml1:down");

    readonly Server _server;
    readonly Action<string> _log;
    readonly ConcurrentDictionary<int, Channel> _channels = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    CancellationTokenSource? _cts;
    ClientWebSocket? _ws;
    string _relay = "", _secret = "";
    byte[] _key = Array.Empty<byte>();
    long _generation;

    public RelayLink(Server server, Action<string> log) { _server = server; _log = log; }

    /// <summary>The room (from the secret), as the relay names it.</summary>
    public string Room { get; private set; } = "";
    public bool Running => _cts != null;
    /// <summary>For the window: "Connecting…", "Online", or why not.</summary>
    public string Status { get; private set; } = "Off";
    public bool Online { get; private set; }
    /// <summary>Phones with an open channel now.</summary>
    public int Phones => _channels.Count;

    /// <summary>The link for the QR code: the relay's page, and after '#' (never sent to the relay) room, key, pairing code.</summary>
    public string Link(string code) => $"{_relay.TrimEnd('/')}/#r={Room}&k={B64u(_key)}&p={code}";

    sealed class Channel
    {
        public required int Slot;
        public required long Generation;
        public required AesGcm Up, Down;
        public long Seen, Sent;
        public required string Who;
        public readonly SemaphoreSlim Busy = new(6, 6);
    }

    public static string NewSecret() => B64u(RandomNumberGenerator.GetBytes(32));

    public static string RoomOf(string secret) => B64u(SHA256.HashData(Encoding.UTF8.GetBytes("mml-room:" + secret)))[..22];

    public void Start(string relayUrl, string secret, string key)
    {
        Stop();
        _relay = relayUrl;
        _secret = secret;
        _key = FromB64u(key);
        if (_key.Length != 32) throw new ArgumentException("The relay key must be 32 bytes.");
        Room = RoomOf(secret);
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        Status = "Connecting…";
        Task.Run(() => Run(ct));
    }

    public void Stop()
    {
        // a proper goodbye first (the relay then tells the phones at once), then the hard stop
        if (_ws is { State: WebSocketState.Open } open && _sendLock.Wait(200))
        {
            try { open.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye.", CancellationToken.None).Wait(500); } catch { }
            finally { _sendLock.Release(); }
        }
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { _ws?.Abort(); } catch { }
        _ws = null;
        foreach (Channel c in _channels.Values) { c.Up.Dispose(); c.Down.Dispose(); }
        _channels.Clear();
        Online = false;
        Status = "Off";
    }

    // ------------------------------------------------------------------------------------------- the connection

    async Task Run(CancellationToken ct)
    {
        int wait = 2000;
        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("X-Relay-Secret", _secret);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                var uri = new Uri(_relay.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/ws/pc");
                using (var connectBy = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectBy.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(uri, connectBy.Token);
                }
                if (ct.IsCancellationRequested) return;
                _ws = ws;
                Online = true;
                Status = "Online";
                _log("Connected to the relay.");
                wait = 2000;
                await Receive(ws, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Status = $"Cannot reach the relay ({Short(e)}). Trying again in {wait / 1000} s.";
            }
            finally
            {
                // (a stopped link leaves the state alone: a new one may already be online)
                if (!ct.IsCancellationRequested)
                {
                    Online = false;
                    foreach (Channel c in _channels.Values) { c.Up.Dispose(); c.Down.Dispose(); }
                    _channels.Clear();
                }
                try { ws.Abort(); ws.Dispose(); } catch { }
                if (_ws == ws) _ws = null;
            }
            if (ct.IsCancellationRequested) break;
            if (Status == "Online") Status = $"The relay closed the connection. Trying again in {wait / 1000} s.";
            try { await Task.Delay(wait + Random.Shared.Next(500), ct); } catch { break; }
            wait = Math.Min(wait * 2, 60000);
        }
    }

    static string Short(Exception e) => (e.InnerException ?? e).Message.Split('\n')[0].Trim().TrimEnd('.');

    async Task Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[MaxFromPhone + 64];
        DateTime lastHeard = DateTime.UtcNow;
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // a ping every 25 s; nothing heard for 75 s (a sleeping PC, a changed network): start again
        _ = Task.Run(async () =>
        {
            while (!watch.IsCancellationRequested)
            {
                try { await Task.Delay(25000, watch.Token); } catch { return; }
                if (DateTime.UtcNow - lastHeard > TimeSpan.FromSeconds(75)) { try { ws.Abort(); } catch { } return; }
                await SendText(ws, "ping");
            }
        });
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                int filled = 0;
                WebSocketReceiveResult r;
                do
                {
                    if (filled >= buffer.Length) throw new InvalidDataException("A frame from the relay was too big.");
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, filled, buffer.Length - filled), ct);
                    filled += r.Count;
                } while (!r.EndOfMessage);
                lastHeard = DateTime.UtcNow;
                if (r.MessageType == WebSocketMessageType.Close) { Status = "The relay closed the connection" + (r.CloseStatusDescription is { Length: > 0 } d ? ": " + d : "."); return; }
                if (r.MessageType == WebSocketMessageType.Text) { Control(Encoding.UTF8.GetString(buffer, 0, filled)); continue; }
                if (filled < 2) continue;
                try { Frame(ws, buffer[0], buffer.AsSpan(1, filled - 1).ToArray()); }
                catch (Exception e) { _log("A phone frame was dropped: " + e.Message); }
            }
        }
        finally { watch.Cancel(); }
    }

    void Control(string text)
    {
        if (text == "pong") return;
        try
        {
            using var doc = JsonDocument.Parse(text);
            string t = doc.RootElement.GetProperty("t").GetString() ?? "";
            int slot = doc.RootElement.GetProperty("slot").GetInt32();
            if (t is "join" or "leave" && _channels.TryRemove(slot, out Channel? old)) { old.Up.Dispose(); old.Down.Dispose(); }
        }
        catch { }
    }

    // ------------------------------------------------------------------------------------------- the channel

    void Frame(ClientWebSocket ws, byte slot, byte[] f)
    {
        switch (f[0])
        {
            case 1 when f.Length == 17:        // hello: a new channel (fresh keys)
            {
                byte[] np = f[1..17], npc = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
                var proof = new byte[16];
                var tag = new byte[16];
                using (var k = new AesGcm(_key, 16)) k.Encrypt(nonce, np, proof, tag, Ad(WelcomeAd, Room));
                byte[] salt = Join(np, npc);
                var channel = new Channel
                {
                    Slot = slot, Generation = Interlocked.Increment(ref _generation),
                    Up = new AesGcm(HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt, Encoding.UTF8.GetBytes("mml1:up:" + Room)), 16),
                    Down = new AesGcm(HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt, Encoding.UTF8.GetBytes("mml1:down:" + Room)), 16),
                    Who = "relay:" + Interlocked.Read(ref _generation),
                };
                if (_channels.TryGetValue(slot, out Channel? old)) { old.Up.Dispose(); old.Down.Dispose(); }
                _channels[slot] = channel;
                _ = SendFrame(ws, slot, Join(new byte[] { 2 }, npc, nonce, proof, tag));
                return;
            }
            case 3 when f.Length >= 9 + 16:   // a request
            {
                if (!_channels.TryGetValue(slot, out Channel? c)) return;
                long n = (long)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(1, 8));
                if (n <= c.Seen) return;                                                   // (old, or played again)
                var plain = new byte[f.Length - 9 - 16];
                c.Up.Decrypt(Nonce(n), f.AsSpan(9, plain.Length), f.AsSpan(f.Length - 16), plain, UpAd);   // throws if tampered with
                c.Seen = n;
                Request(ws, c, plain);
                return;
            }
        }
    }

    void Request(ClientWebSocket ws, Channel c, byte[] plain)
    {
        long id;
        string method, path, auth, body;
        using (var doc = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 4 }))
        {
            JsonElement e = doc.RootElement;
            id = e.GetProperty("id").GetInt64();
            method = e.GetProperty("m").GetString() ?? "";
            path = e.GetProperty("p").GetString() ?? "";
            auth = e.TryGetProperty("a", out JsonElement a) ? a.GetString() ?? "" : "";
            body = e.TryGetProperty("b", out JsonElement b) ? b.GetString() ?? "" : "";
        }
        if (auth.Length > 200 || !path.StartsWith('/')) return;
        _ = Task.Run(async () =>
        {
            if (!await c.Busy.WaitAsync(20000)) return;
            try
            {
                Reply reply = await _server.RelayedAsync(method, path, auth, body, c.Who);
                await Answer(ws, c, id, reply);
            }
            catch (Exception e) { _log("A relayed request failed: " + e.Message); }
            finally { c.Busy.Release(); }
        });
    }

    /// <summary>[flags][u32 n][JSON head][body], deflated when large and not a picture; encrypted with the next counter.</summary>
    async Task Answer(ClientWebSocket ws, Channel c, long id, Reply r)
    {
        byte[] body = r.Body.Length > MaxBody ? Encoding.UTF8.GetBytes("{\"error\":\"Too big for the relay.\"}") : r.Body;
        int code = r.Body.Length > MaxBody ? 500 : r.Code;
        byte[] head = JsonSerializer.SerializeToUtf8Bytes(new { id, s = code, t = r.Type });
        var inner = new byte[4 + head.Length + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(inner, (uint)head.Length);
        head.CopyTo(inner, 4);
        body.CopyTo(inner, 4 + head.Length);
        byte flags = 0;
        if (inner.Length > 1024 && !r.Type.StartsWith("image/"))
        {
            using var packed = new MemoryStream();
            using (var z = new DeflateStream(packed, CompressionLevel.Fastest, true)) z.Write(inner);
            if (packed.Length < inner.Length) { inner = packed.ToArray(); flags = 1; }
        }
        byte[] plain = Join(new[] { flags }, inner);
        await _sendLock.WaitAsync();
        try
        {
            // the counter is taken and the frame sent under one lock: the phone wants them in order
            if (!_channels.TryGetValue(c.Slot, out Channel? now) || now.Generation != c.Generation || ws.State != WebSocketState.Open) return;
            long n = ++c.Sent;
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            c.Down.Encrypt(Nonce(n), plain, cipher, tag, DownAd);
            var frame = new byte[1 + 1 + 8 + cipher.Length + 16];
            frame[0] = (byte)c.Slot;
            frame[1] = 3;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)n);
            cipher.CopyTo(frame, 10);
            tag.CopyTo(frame, 10 + cipher.Length);
            await ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch { }
        finally { _sendLock.Release(); }
    }

    async Task SendFrame(ClientWebSocket ws, byte slot, byte[] payload)
    {
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(Join(new[] { slot }, payload), WebSocketMessageType.Binary, true, CancellationToken.None); }
        catch { }
        finally { _sendLock.Release(); }
    }

    async Task SendText(ClientWebSocket ws, string text)
    {
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { _sendLock.Release(); }
    }

    // ------------------------------------------------------------------------------------------- small pieces

    static byte[] Nonce(long n)
    {
        var iv = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(4), (ulong)n);
        return iv;
    }

    static byte[] Ad(byte[] label, string room) => Join(label, Encoding.UTF8.GetBytes(room));

    static byte[] Join(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] p in parts) length += p.Length;
        var all = new byte[length];
        int at = 0;
        foreach (byte[] p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }

    public static string B64u(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64u(string s)
    {
        try
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
        }
        catch { return Array.Empty<byte>(); }
    }
}
