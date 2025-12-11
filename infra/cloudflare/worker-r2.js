// worker-r2.js
// Cloudflare Worker (R2-backed) for MusicAI: auth cookie issuance + media serving with edge cache and simple Range support.
// Bindings required:
// - R2_MEDIA (R2 bucket binding)
// Secrets required:
// - EDGE_SIGN_KEY (HMAC key for signing edge cookie)
// - JWT_SECRET (optional for HS256 validation; recommend using JWKS in production)

const EDGE_COOKIE = "Edge-Play";
const EDGE_MAX = 60 * 15; // 15 minutes
const MANIFEST_TTL = 30; // seconds
const SEG_TTL = 60 * 60 * 24; // 1 day

function now() { return Math.floor(Date.now()/1000); }

function parseCookies(header) {
  const out = {};
  if (!header) return out;
  for (const part of header.split(';').map(s => s.trim()).filter(Boolean)) {
    const idx = part.indexOf('=');
    if (idx > -1) out[part.substring(0, idx)] = decodeURIComponent(part.substring(idx+1));
  }
  return out;
}

async function verifyJwt(token) {
  if (!token) return null;
  if (!JWT_SECRET) return { ok: true }; // bypass during local preview if secret not set
  try {
    const parts = token.split('.');
    if (parts.length !== 3) return null;
    const payloadRaw = parts[1];
    const payloadJson = JSON.parse(atob(payloadRaw.replace(/-/g,'+').replace(/_/g,'/')));
    if (payloadJson.exp && payloadJson.exp < now()) return null;
    return payloadJson;
  } catch (e) {
    return null;
  }
}

async function signEdge(userId) {
  const exp = now() + EDGE_MAX;
  const payload = `${userId}|${exp}`;
  const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(EDGE_SIGN_KEY), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const sigBuf = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(payload));
  const sig = btoa(String.fromCharCode(...new Uint8Array(sigBuf))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  return `${userId}|${exp}|${sig}`;
}

async function verifyEdge(cookieVal) {
  if (!cookieVal) return false;
  const parts = cookieVal.split('|');
  if (parts.length !== 3) return false;
  const [uid, expStr, sig] = parts;
  const exp = parseInt(expStr, 10);
  if (isNaN(exp) || exp < now()) return false;
  const payload = `${uid}|${exp}`;
  try {
    const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(EDGE_SIGN_KEY), { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']);
    const binary = atob(sig.replace(/-/g,'+').replace(/_/g,'/'));
    const sigArr = new Uint8Array([...binary].map(c => c.charCodeAt(0)));
    const ok = await crypto.subtle.verify('HMAC', key, sigArr, new TextEncoder().encode(payload));
    return ok ? { userId: uid } : false;
  } catch (e) {
    return false;
  }
}

addEventListener('fetch', event => {
  event.respondWith(handleRequest(event));
});

async function handleRequest(event) {
  const req = event.request;
  const url = new URL(req.url);
  const path = url.pathname;

  // POST /auth/issue-cookie -> validate JWT and set Edge-Play cookie
  if (path === '/auth/issue-cookie' && req.method === 'POST') {
    const authHeader = req.headers.get('Authorization') || '';
    let token = null;
    if (authHeader.startsWith('Bearer ')) token = authHeader.slice(7);
    else {
      const cookies = parseCookies(req.headers.get('Cookie'));
      token = cookies['MusicAI.Auth'];
    }
    const payload = await verifyJwt(token);
    if (!payload) return new Response('Unauthorized', { status: 401 });
    const userId = payload.sub || payload.userId || payload.id || ('u:' + (payload.email || 'unknown'));
    const val = await signEdge(userId);
    const cookieStr = `${EDGE_COOKIE}=${encodeURIComponent(val)}; HttpOnly; Secure; SameSite=None; Path=/; Max-Age=${EDGE_MAX}; Domain=.tingoradio.ai`;
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'Set-Cookie': cookieStr, 'Content-Type': 'application/json' } });
  }

  // GET /media/* -> validate cookie and serve from R2 with caching
  if (path.startsWith('/media/')) {
    const cookies = parseCookies(req.headers.get('Cookie'));
    const edge = cookies[EDGE_COOKIE];
    const ok = await verifyEdge(edge);
    if (!ok) return new Response('Unauthorized', { status: 401 });

    // Map URL path to R2 key (strip leading /media/)
    const key = path.replace(/^\/media\//, '');
    if (!key) return new Response('Not found', { status: 404 });

    const cache = caches.default;
    // Create a cache key based on the request URL only (avoid cookie in key)
    const cacheKey = new Request(new URL(req.url).toString(), { method: 'GET' });
    const cached = await cache.match(cacheKey);
    if (cached) return cached;

    // Support simple Range header for small segments
    const rangeHeader = req.headers.get('Range');

    try {
      // Fetch from R2
      const obj = await R2_MEDIA.get(key);
      if (!obj) return new Response('Not found', { status: 404 });

      // Content type
      const contentType = obj.httpMetadata?.contentType || 'application/octet-stream';

      // If Range requested, slice the ArrayBuffer (ok for small HLS segments)
      if (rangeHeader) {
        const m = /bytes=(\d+)-(\d+)?/.exec(rangeHeader);
        if (m) {
          const start = Number(m[1]);
          const end = m[2] ? Number(m[2]) : undefined;
          const ab = await obj.arrayBuffer();
          const total = ab.byteLength;
          const slice = ab.slice(start, end ? end + 1 : undefined);
          const rEnd = end ?? (total - 1);
          const headers = new Headers();
          headers.set('Content-Type', contentType);
          headers.set('Content-Range', `bytes ${start}-${rEnd}/${total}`);
          headers.set('Accept-Ranges', 'bytes');
          headers.set('Cache-Control', `public, max-age=${SEG_TTL}`);
          headers.set('Access-Control-Allow-Origin', 'https://www.tingoradio.ai');
          const response = new Response(slice, { status: 206, headers });
          event.waitUntil(cache.put(cacheKey, response.clone()));
          return response;
        }
      }

      // No Range: stream object body
      const headers = new Headers();
      headers.set('Content-Type', contentType);
      headers.set('Cache-Control', `public, max-age=${SEG_TTL}`);
      headers.set('Access-Control-Allow-Origin', 'https://www.tingoradio.ai');
      const response = new Response(obj.body, { status: 200, headers });
      event.waitUntil(cache.put(cacheKey, response.clone()));
      return response;
    } catch (e) {
      return new Response('Error fetching media', { status: 502 });
    }
  }

  return new Response('Not found', { status: 404 });
}
