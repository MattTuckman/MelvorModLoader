// Phone Screen relay: lets a phone reach the game from anywhere (mobile data, another Wi-Fi).
//
// The game connects out to this relay (no router setup at home), the phone connects too, and the relay passes
// frames between them. What is in those frames is encrypted by the game and the phone with keys made from a
// secret that only they know (it travels in the QR code's '#' part, which browsers never send to a server).
// The relay sees only ciphertext, sizes and timing, and stores nothing. (It does serve the small loader page
// below, so whoever runs the relay must be trusted not to change it: see README.)
//
//   GET  /                   the loader page (connects, asks the game for the real page, shows it)
//   WS   /ws/pc              the game. Header X-Relay-Secret: its secret; room = hash of the secret.
//   WS   /ws/phone/<room>    a phone (at most 4 per room; a 5th replaces the oldest)
//
// One Durable Object per room (WebSocket Hibernation: idle connections cost nothing). Frames:
//   phone -> relay: binary payload                 relay -> game: [slot][payload]
//   game  -> relay: [slot][payload]                relay -> phone: payload
//   relay -> game : text {"t":"join"|"leave","slot":n}     relay -> phone: text {"t":"pc","on":bool}
//   anyone -> relay: text "ping"  ->  "pong" (answered without waking the room)

export const MAX_PHONES = 4;
export const MAX_FROM_PHONE = 16 * 1024;
export const MAX_FROM_PC = 512 * 1024;
export const PHONE_FRAMES_PER_10S = 200;

/** The room of a secret: base64url(SHA-256("mml-room:" + secret)), 22 characters. */
export async function roomOf(secret) {
  const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode("mml-room:" + secret)));
  let s = "";
  for (const b of hash) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "").slice(0, 22);
}

export const ROOM_RE = /^[A-Za-z0-9_-]{22}$/;
export const SECRET_RE = /^[A-Za-z0-9_-]{40,64}$/;

/**
 * The relay's whole logic, the same for Cloudflare and for the local test relay. `io` gives:
 *   pcs()  phones()  info(ws) -> {slot, since}  send(ws, data)  close(ws, code, reason)
 */
export class Hub {
  constructor(io) { this.io = io; this.rate = new Map(); }

  /** A slot for a new phone: a free one, or the oldest phone's (that one is closed: likely a dead connection). */
  takeSlot() {
    const phones = this.io.phones();
    const used = new Set(phones.map((p) => this.io.info(p).slot));
    for (let i = 0; i < MAX_PHONES; i++) if (!used.has(i)) return i;
    const oldest = phones.reduce((a, b) => (this.io.info(a).since <= this.io.info(b).since ? a : b));
    const slot = this.io.info(oldest).slot;
    this.io.close(oldest, 4001, "Replaced by a newer connection.");
    this.closed(oldest, false);
    return slot;
  }

  pcJoined(ws) {
    for (const old of this.io.pcs()) if (old !== ws) this.io.close(old, 4000, "Another game connected to this room.");
    for (const p of this.io.phones()) {
      this.io.send(ws, JSON.stringify({ t: "join", slot: this.io.info(p).slot }));
      this.io.send(p, JSON.stringify({ t: "pc", on: true }));
    }
  }

  phoneJoined(ws) {
    const on = this.io.pcs().length > 0;
    for (const pc of this.io.pcs()) this.io.send(pc, JSON.stringify({ t: "join", slot: this.io.info(ws).slot }));
    this.io.send(ws, JSON.stringify({ t: "pc", on }));
  }

  fromPc(ws, data) {
    if (typeof data === "string") return;
    const bytes = new Uint8Array(data);
    if (bytes.length < 2 || bytes.length > MAX_FROM_PC) return;
    const slot = bytes[0];
    for (const p of this.io.phones()) if (this.io.info(p).slot === slot) this.io.send(p, bytes.subarray(1));
  }

  fromPhone(ws, data) {
    if (typeof data === "string") return;
    const bytes = new Uint8Array(data);
    const slot = this.io.info(ws).slot;
    if (bytes.length < 1 || bytes.length > MAX_FROM_PHONE) { this.io.close(ws, 1009, "Too big."); return; }
    const now = Date.now(), r = this.rate.get(slot) || { from: now, n: 0 };
    if (now - r.from > 10000) { r.from = now; r.n = 0; }
    this.rate.set(slot, r);
    if (++r.n > PHONE_FRAMES_PER_10S) { this.io.close(ws, 1008, "Too many messages."); return; }
    const out = new Uint8Array(bytes.length + 1);
    out[0] = slot;
    out.set(bytes, 1);
    for (const pc of this.io.pcs()) this.io.send(pc, out);
  }

  /** A socket closed (only the current ones count: a game that was replaced does not turn the room off). */
  closed(ws, wasPc) {
    if (wasPc) {
      if (this.io.pcs().filter((p) => p !== ws).length === 0)
        for (const p of this.io.phones()) this.io.send(p, JSON.stringify({ t: "pc", on: false }));
    } else {
      const slot = this.io.info(ws).slot;
      if (this.io.phones().some((p) => p !== ws && this.io.info(p).slot === slot)) return;   // (its slot was given away)
      this.rate.delete(slot);
      for (const pc of this.io.pcs()) this.io.send(pc, JSON.stringify({ t: "leave", slot }));
    }
  }
}

/** A phone's socket must come from this relay's own page. */
export function originOk(origin, url) { return !origin || origin === url.origin; }

// ------------------------------------------------------------------------------------------------ Cloudflare

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const upgrade = (request.headers.get("Upgrade") || "").toLowerCase() === "websocket";
    if (url.pathname === "/ws/pc" && upgrade) {
      const secret = request.headers.get("X-Relay-Secret") || "";
      if (!SECRET_RE.test(secret)) return new Response("No secret.", { status: 403 });
      const room = await roomOf(secret);
      return env.ROOMS.get(env.ROOMS.idFromName(room)).fetch(new Request("https://room/pc", request));
    }
    const m = /^\/ws\/phone\/([A-Za-z0-9_-]+)$/.exec(url.pathname);
    if (m && upgrade) {
      if (!ROOM_RE.test(m[1])) return new Response("No such room.", { status: 404 });
      if (!originOk(request.headers.get("Origin"), url)) return new Response("Wrong origin.", { status: 403 });
      return env.ROOMS.get(env.ROOMS.idFromName(m[1])).fetch(new Request("https://room/phone", request));
    }
    if (request.method === "GET" && (url.pathname === "/" || url.pathname === "/index.html")) return loaderResponse(url);
    if (request.method === "GET" && url.pathname === "/manifest.webmanifest")
      return new Response(MANIFEST, { headers: { "Content-Type": "application/manifest+json", "Cache-Control": "public, max-age=3600" } });
    return new Response("Not here.", { status: 404 });
  },
};

export function loaderResponse(url) {
  const ws = (url.protocol === "https:" ? "wss://" : "ws://") + url.host;
  return new Response(LOADER_HTML, {
    headers: {
      "Content-Type": "text/html; charset=utf-8",
      "Cache-Control": "no-store",
      "Content-Security-Policy": `default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src ${ws}; img-src 'self' blob: data:; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
      "X-Content-Type-Options": "nosniff",
      "Referrer-Policy": "no-referrer",
    },
  });
}

/** One room: the game and its phones. */
export class RelayRoom {
  constructor(state) {
    this.state = state;
    this.hub = new Hub({
      pcs: () => state.getWebSockets("pc"),
      phones: () => state.getWebSockets("phone"),
      info: (ws) => ws.deserializeAttachment() || {},
      send: (ws, data) => { try { ws.send(data); } catch { } },
      close: (ws, code, reason) => { try { ws.close(code, reason); } catch { } },
    });
    state.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(request) {
    const pc = new URL(request.url).pathname === "/pc";
    const slot = pc ? -1 : this.hub.takeSlot();
    const [client, server] = Object.values(new WebSocketPair());
    this.state.acceptWebSocket(server, [pc ? "pc" : "phone"]);
    server.serializeAttachment({ pc, slot, since: Date.now() });
    if (pc) this.hub.pcJoined(server); else this.hub.phoneJoined(server);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, message) {
    if (ws.deserializeAttachment()?.pc) this.hub.fromPc(ws, message); else this.hub.fromPhone(ws, message);
  }

  async webSocketClose(ws) {
    this.hub.closed(ws, !!ws.deserializeAttachment()?.pc);
    try { ws.close(1000, "Bye."); } catch { }
  }

  async webSocketError(ws) { await this.webSocketClose(ws); }
}

const MANIFEST = JSON.stringify({
  name: "Melvor Phone Screen", short_name: "Melvor", start_url: "/", display: "standalone",
  background_color: "#141210", theme_color: "#141210",
});

// ------------------------------------------------------------------------------------------------ the loader
//
// The secure channel (protocol "mml1"), the same in PhoneScreen\RelayLink.cs:
//   K = the 32-byte key from the QR code.  room = the room id (22 characters).
//   phone -> game  [1][np: 16 random bytes]                                   hello (a new session)
//   game -> phone  [2][npc: 16][nonce: 12][AES-GCM(K, np, AAD "mml1:welcome:"+room)]   proves the game holds K
//   both derive    HKDF-SHA256(ikm K, salt np|npc, info "mml1:up:"+room or "mml1:down:"+room) -> 32-byte keys
//   then           [3][counter: 8, big-endian, from 1, strictly rising][AES-GCM(key, nonce = 0000|counter)]
//   up   (phone -> game)  plaintext: JSON {id, m, p, a, b}   (method, path, authorization, body)
//   down (game -> phone)  plaintext: [flags: bit 0 = deflate-raw][the rest: u32 BE n][JSON {id, s, t}: n bytes][body]

export const LOADER_HTML = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#141210">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<link rel="manifest" href="/manifest.webmanifest">
<title>Melvor</title>
<style id="loader-style">
html,body{margin:0;min-height:100%;background:#141210;color:#f3ead6;font:16px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}
#wait{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;min-height:100vh;padding:24px;box-sizing:border-box;text-align:center}
#wait b{font-size:20px}#wait p{margin:0;color:#c0b29c;max-width:340px}
.ldot{width:12px;height:12px;border-radius:50%;background:#c0b29c}.ldot.on{background:#8fd18a}.ldot.off{background:#e07a6a}
#wait button{min-height:46px;padding:0 18px;border-radius:10px;border:1px solid #463e34;background:#2e2922;color:#f3ead6;font:inherit;font-weight:700}
</style>
</head>
<body>
<div id="wait"><span class="ldot" id="ldot"></span><b id="ltitle">Connecting…</b><p id="lwhy"></p><button id="lforget" hidden>Scan a new code</button></div>
<script>
"use strict";
(function(){
var $=function(i){return document.getElementById(i)};
function say(dot,title,why,forget){if(!$("wait"))return;$("ldot").className="ldot "+(dot||"");$("ltitle").textContent=title;$("lwhy").textContent=why||"";$("lforget").hidden=!forget}
// the QR's '#' part: room, key, pairing code (kept on this phone; the relay never sees it)
var h={};location.hash.replace(/^#/,"").split("&").forEach(function(kv){var i=kv.indexOf("=");if(i>0)h[kv.slice(0,i)]=decodeURIComponent(kv.slice(i+1))});
var conf=null;try{conf=JSON.parse(localStorage.getItem("mmlRelay")||"null")}catch(e){}
if(/^[A-Za-z0-9_-]{22}$/.test(h.r||"")&&/^[A-Za-z0-9_-]{43}$/.test(h.k||"")){conf={r:h.r,k:h.k};try{localStorage.setItem("mmlRelay",JSON.stringify(conf))}catch(e){}}
var pairCode=/^[A-Za-z0-9]{4,20}$/.test(h.p||"")?h.p:null;
if(location.hash)history.replaceState(null,"",location.pathname);
$("lforget").addEventListener("click",function(){try{localStorage.removeItem("mmlRelay")}catch(e){}location.reload()});
if(!conf){say("off","Scan the QR code in the game","In Melvor Idle 2 open the Phone window, tick “Reach from anywhere”, and scan its code with this phone's camera.");return}
if(!window.crypto||!crypto.subtle||!window.CompressionStream){say("off","This browser is too old","Update the phone's browser (Safari 16.4 or Chrome 103 or later).");return}

function b64u(s){s=s.replace(/-/g,"+").replace(/_/g,"/");while(s.length%4)s+="=";var b=atob(s),u=new Uint8Array(b.length);for(var i=0;i<b.length;i++)u[i]=b.charCodeAt(i);return u}
function cat(a,b){var u=new Uint8Array(a.length+b.length);u.set(a,0);u.set(b,a.length);return u}
var enc=new TextEncoder(),dec=new TextDecoder(),K=b64u(conf.k),room=conf.r;
var kWelcome=crypto.subtle.importKey("raw",K,{name:"AES-GCM"},false,["decrypt"]);
var kBase=crypto.subtle.importKey("raw",K,{name:"HKDF"},false,["deriveKey"]);
function derive(salt,label){return kBase.then(function(b){return crypto.subtle.deriveKey({name:"HKDF",hash:"SHA-256",salt:salt,info:enc.encode("mml1:"+label+":"+room)},b,{name:"AES-GCM",length:256},false,[label==="up"?"encrypt":"decrypt"])})}
function nonceOf(n){var iv=new Uint8Array(12),v=new DataView(iv.buffer);v.setUint32(4,Math.floor(n/4294967296));v.setUint32(8,n>>>0);return iv}

var ws=null,pcOn=false,session=null,hello=null,seq=0,pending={},queue=[],retry=1000,pinger=null,pongWait=null,mounted=false,hiddenAt=0;
function fail(id,why){var p=pending[id];if(!p)return;delete pending[id];clearTimeout(p.timer);p.reject(new TypeError(why))}
function failAll(why){Object.keys(pending).forEach(function(id){fail(id,why)});queue.forEach(function(q){q.reject(new TypeError(why))});queue=[]}
function stateText(){return !ws||ws.readyState!==1?"relay":!pcOn?"pc":session?"on":"pc"}

// a new session each time the game is (re)connected: fresh keys, counters from 1
function startSession(){
  session=null;var np=crypto.getRandomValues(new Uint8Array(16));hello=np;
  ws.send(cat(new Uint8Array([1]),np));
}
function onWelcome(u){
  if(!hello||u.length<1+16+12+16)return;var np=hello,npc=u.slice(1,17);
  kWelcome.then(function(k){return crypto.subtle.decrypt({name:"AES-GCM",iv:u.subarray(17,29),additionalData:enc.encode("mml1:welcome:"+room)},k,u.subarray(29))}).then(function(p){
    p=new Uint8Array(p);if(p.length!==16||p.some(function(b,i){return b!==np[i]}))return;
    var salt=cat(np,npc);return Promise.all([derive(salt,"up"),derive(salt,"down")]).then(function(ks){
      if(hello!==np)return;hello=null;session={up:ks[0],down:ks[1],sent:0,seen:0};
      var q=queue;queue=[];q.forEach(function(x){send(x)});
      if(!mounted)mount();
    });
  }).catch(function(){/* not from our game: dropped */});
}
function send(x){
  var s=session;if(!s||!ws||ws.readyState!==1){x.reject(new TypeError("Not connected."));return}
  var n=++s.sent,iv=nonceOf(n);
  pending[x.id]={resolve:x.resolve,reject:x.reject,timer:setTimeout(function(){fail(x.id,"No answer from the game.")},x.wait)};
  crypto.subtle.encrypt({name:"AES-GCM",iv:iv,additionalData:enc.encode("mml1:up")},s.up,enc.encode(JSON.stringify(x.msg))).then(function(c){
    if(session!==s||!ws||ws.readyState!==1){fail(x.id,"Not connected.");return}
    var out=new Uint8Array(9+c.byteLength);out[0]=3;out.set(iv.subarray(4),1);out.set(new Uint8Array(c),9);ws.send(out);
  },function(){fail(x.id,"Could not encrypt.")});
}
function onData(u){
  var s=session;if(!s||u.length<9+16)return;
  var v=new DataView(u.buffer,u.byteOffset+1,8),n=v.getUint32(0)*4294967296+v.getUint32(4);if(n<=s.seen)return;
  crypto.subtle.decrypt({name:"AES-GCM",iv:cat(new Uint8Array(4),u.subarray(1,9)),additionalData:enc.encode("mml1:down")},s.down,u.subarray(9)).then(function(p){
    if(session!==s||n<=s.seen)return;s.seen=n;p=new Uint8Array(p);
    var rest=p.subarray(1);
    return (p[0]&1?new Response(new Blob([rest]).stream().pipeThrough(new DecompressionStream("deflate-raw"))).arrayBuffer().then(function(b){return new Uint8Array(b)}):Promise.resolve(rest)).then(function(r){
      var len=(r[0]<<24|r[1]<<16|r[2]<<8|r[3])>>>0;if(len>4096||4+len>r.length)return;
      var head=JSON.parse(dec.decode(r.subarray(4,4+len))),x=pending[head.id];if(!x)return;
      delete pending[head.id];clearTimeout(x.timer);
      x.resolve(new Response(head.s===204?null:r.slice(4+len),{status:head.s,headers:{"Content-Type":head.t||"application/octet-stream"}}));
    });
  }).catch(function(){/* tampered with, or an old session: dropped */});
}
// the page's fetch, carried through the relay: the same requests as on the home network
function tfetch(path,init){
  init=init||{};var hd=init.headers||{};
  var x={id:++seq,wait:/^\\/icon\\//.test(path)?15000:8000,msg:null};
  x.msg={id:x.id,m:init.method||"GET",p:String(path),a:hd.Authorization||hd.authorization||"",b:init.body||""};
  return new Promise(function(resolve,reject){x.resolve=resolve;x.reject=reject;
    if(session&&ws&&ws.readyState===1)send(x);
    else if(ws&&ws.readyState===1&&pcOn&&queue.length<40)queue.push(x);
    else reject(new TypeError(pcOn?"Not connected.":"The game is not connected."));
  });
}
window.melvorTunnel={remote:true,room:room,fetch:tfetch,state:stateText,
  pairCode:function(){return pairCode},paired:function(){pairCode=null}};

// the game's own page, put in place of this one (the game sends it, so it always matches the game's mod)
function mount(){
  tfetch("/").then(function(r){if(!r.ok)throw new Error(r.status);return r.text()}).then(function(html){
    if(mounted)return;mounted=true;
    var doc=new DOMParser().parseFromString(html,"text/html");
    $("loader-style").remove();
    doc.head.querySelectorAll("style").forEach(function(s){document.head.appendChild(document.importNode(s,true))});
    var scripts=[].slice.call(doc.body.querySelectorAll("script"));scripts.forEach(function(s){s.remove()});
    document.body.innerHTML="";[].slice.call(doc.body.childNodes).forEach(function(n){document.body.appendChild(document.importNode(n,true))});
    scripts.forEach(function(s){var e=document.createElement("script");e.textContent=s.textContent;document.body.appendChild(e)});
  }).catch(function(){say("","Waiting for your game…","The game answered slowly. Trying again.");setTimeout(function(){if(!mounted)mount()},3000)});
}

function connect(){
  if(ws&&ws.readyState<2)return;
  if(!mounted)say("","Connecting…","");
  try{ws=new WebSocket((location.protocol==="https:"?"wss://":"ws://")+location.host+"/ws/phone/"+room)}catch(e){schedule();return}
  ws.binaryType="arraybuffer";var me=ws;
  ws.onopen=function(){retry=1000;clearInterval(pinger);pinger=setInterval(function(){if(me.readyState===1)me.send("ping")},25000)};
  ws.onmessage=function(e){
    if(me!==ws)return;
    if(typeof e.data==="string"){
      if(e.data==="pong"){clearTimeout(pongWait);return}
      var m;try{m=JSON.parse(e.data)}catch(x){return}
      if(m.t==="pc"){pcOn=!!m.on;if(pcOn)startSession();else{session=null;failAll("The game is not connected.");
        if(!mounted)say("off","Your game is not connected","Start Melvor Idle 2 on your PC with the phone screen on and “Reach from anywhere” ticked. The PC must stay awake.",true)}}
      return}
    var u=new Uint8Array(e.data);if(u[0]===2)onWelcome(u);else if(u[0]===3)onData(u);
  };
  ws.onclose=function(e){if(me!==ws)return;clearInterval(pinger);pcOn=false;session=null;failAll("Not connected.");
    if(!mounted)say("off","No connection","Check this phone's internet connection. Trying again…");schedule()};
  ws.onerror=function(){};
}
var timer=null;
function schedule(){clearTimeout(timer);timer=setTimeout(connect,retry+Math.random()*500);retry=Math.min(retry*2,15000)}
function reconnectNow(){retry=1000;clearTimeout(timer);if(ws&&ws.readyState<2){var old=ws;ws=null;try{old.close()}catch(e){};pcOn=false;session=null;failAll("Reconnecting.")}connect()}
// a phone that slept or changed network often keeps a dead socket that still says "open": check it
function check(){if(!ws||ws.readyState!==1){reconnectNow();return}try{ws.send("ping")}catch(e){}clearTimeout(pongWait);pongWait=setTimeout(reconnectNow,3000)}
document.addEventListener("visibilitychange",function(){
  if(document.hidden){hiddenAt=Date.now();setTimeout(function(){if(document.hidden&&Date.now()-hiddenAt>=29000&&ws){var old=ws;ws=null;try{old.close()}catch(e){};pcOn=false;session=null;failAll("Paused.")}},30000)}
  else check();
});
window.addEventListener("online",check);
connect();
})();
</script>
</body>
</html>`;
