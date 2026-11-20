// The relay on this PC, for tests: the same Hub and loader page as worker.js, over Node's own HTTP server with a
// small WebSocket implementation (no packages). Usage: node local-relay.mjs [port]   (prints "relay ready <port>")
import http from "node:http";
import { createHash } from "node:crypto";
import { Hub, roomOf, loaderResponse, originOk, ROOM_RE, SECRET_RE, MAX_FROM_PC } from "./worker.js";

const port = Number(process.argv[2] || 8787);
const rooms = new Map();   // room -> { pcs: Set, phones: Set, hub }

function roomFor(name) {
  let r = rooms.get(name);
  if (!r) {
    r = { pcs: new Set(), phones: new Set() };
    r.hub = new Hub({
      pcs: () => [...r.pcs],
      phones: () => [...r.phones],
      info: (ws) => ({ slot: ws.slot, since: ws.since }),
      send: (ws, data) => { if (typeof data === "string") log(`-> ${ws.slot < 0 ? "game" : "phone " + ws.slot}: ${data}`); ws.send(data); },
      close: (ws, code, reason) => ws.close(code, reason),
    });
    rooms.set(name, r);
  }
  return r;
}

/** A WebSocket over a raw socket: text and binary frames, ping/pong, close; fragments joined. */
class Sock {
  constructor(socket, onMessage, onClose) {
    this.socket = socket; this.onMessage = onMessage; this.onClose = onClose;
    this.buf = Buffer.alloc(0); this.parts = []; this.partOp = 0; this.closed = false;
    socket.on("data", (d) => { this.buf = Buffer.concat([this.buf, d]); this.parse(); });
    socket.on("close", () => this.finish());
    socket.on("end", () => { try { socket.end(); } catch { } this.finish(); });   // (the other side shut its half)
    socket.on("error", () => this.finish());
  }
  finish() { if (this.closed) return; this.closed = true; this.onClose(this); }
  parse() {
    for (;;) {
      const b = this.buf;
      if (b.length < 2) return;
      const fin = (b[0] & 0x80) !== 0, op = b[0] & 0x0f, masked = (b[1] & 0x80) !== 0;
      let len = b[1] & 0x7f, at = 2;
      if (len === 126) { if (b.length < 4) return; len = b.readUInt16BE(2); at = 4; }
      else if (len === 127) { if (b.length < 10) return; len = Number(b.readBigUInt64BE(2)); at = 10; }
      if (len > MAX_FROM_PC + 64) { this.close(1009, "Too big."); return; }
      const need = at + (masked ? 4 : 0) + len;
      if (b.length < need) return;
      let data = b.subarray(at + (masked ? 4 : 0), need);
      if (masked) { const m = b.subarray(at, at + 4); data = Buffer.from(data); for (let i = 0; i < data.length; i++) data[i] ^= m[i & 3]; }
      this.buf = b.subarray(need);
      if (op === 8) { this.close(1000, ""); return; }
      if (op === 9) { this.frame(10, data); continue; }
      if (op === 10) continue;
      if (op === 0) this.parts.push(data); else { this.partOp = op; this.parts = [data]; }
      if (!fin) continue;
      const whole = Buffer.concat(this.parts); this.parts = [];
      if (this.partOp === 1) { const t = whole.toString("utf8"); if (t === "ping") this.send("pong"); else this.onMessage(this, t); }
      else if (this.partOp === 2) this.onMessage(this, whole.buffer.slice(whole.byteOffset, whole.byteOffset + whole.length));
    }
  }
  frame(op, payload) {
    if (this.closed) return;
    const n = payload.length;
    const head = n < 126 ? Buffer.from([0x80 | op, n]) : n < 65536 ? Buffer.from([0x80 | op, 126, n >> 8, n & 255]) : Buffer.concat([Buffer.from([0x80 | op, 127]), (() => { const x = Buffer.alloc(8); x.writeBigUInt64BE(BigInt(n)); return x; })()]);
    try { this.socket.write(Buffer.concat([head, payload])); } catch { }
  }
  send(data) { this.frame(typeof data === "string" ? 1 : 2, typeof data === "string" ? Buffer.from(data, "utf8") : Buffer.from(data)); }
  close(code, reason) {
    if (this.closed) return;
    const r = Buffer.from(reason || "", "utf8"), p = Buffer.alloc(2 + r.length);
    p.writeUInt16BE(code || 1000, 0); r.copy(p, 2);
    this.frame(8, p);
    try { this.socket.end(); } catch { }
    this.finish();
  }
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  if (req.method === "GET" && (url.pathname === "/" || url.pathname === "/index.html")) {
    const r = loaderResponse(url);
    res.writeHead(r.status, Object.fromEntries(r.headers));
    res.end(await r.text());
    return;
  }
  res.writeHead(404); res.end("Not here.");
});

server.on("upgrade", async (req, socket) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const deny = (code, text) => { socket.end(`HTTP/1.1 ${code} ${text}\r\nConnection: close\r\n\r\n`); };
  let name, pc;
  if (url.pathname === "/ws/pc") {
    const secret = req.headers["x-relay-secret"] || "";
    if (!SECRET_RE.test(secret)) return deny(403, "Forbidden");
    name = await roomOf(secret); pc = true;
  } else {
    const m = /^\/ws\/phone\/([A-Za-z0-9_-]+)$/.exec(url.pathname);
    if (!m || !ROOM_RE.test(m[1])) return deny(404, "Not Found");
    if (!originOk(req.headers.origin, new URL(`http://${req.headers.host}`))) return deny(403, "Forbidden");
    name = m[1]; pc = false;
  }
  const room = roomFor(name);
  const slot = pc ? -1 : room.hub.takeSlot();
  const accept = createHash("sha1").update(req.headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  socket.setNoDelay(true);
  const ws = new Sock(socket,
    (w, data) => (pc ? room.hub.fromPc(w, data) : room.hub.fromPhone(w, data)),
    (w) => { (pc ? room.pcs : room.phones).delete(w); room.hub.closed(w, pc); log(`${pc ? "game" : "phone " + slot} left ${name.slice(0, 6)}`); });
  ws.slot = slot;
  ws.since = Date.now();
  (pc ? room.pcs : room.phones).add(ws);
  if (pc) room.hub.pcJoined(ws); else room.hub.phoneJoined(ws);
  log(`${pc ? "game" : "phone " + slot} joined ${name.slice(0, 6)}`);
});

function log(text) { console.log(new Date().toISOString().slice(11, 23), text); }

server.listen(port, "127.0.0.1", () => console.log(`relay ready ${port}`));
