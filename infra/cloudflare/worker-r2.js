// worker-r2.js
// Cloudflare Worker: proxy S3 using presigned URLs provided by your Orchestrator
// Provides: auth cookie issuance + media serving with edge cache and Range support.
// Environment bindings required (in Wrangler / Cloudflare):
// - ORCHESTRATOR_URL (e.g. https://api.yourdomain.com)
// Secrets required:
// - EDGE_SIGN_KEY (HMAC key for signing edge cookie)
// - JWT_SECRET (optional for HS256 validation)

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

  // GET /media/* -> validate cookie and proxy & cache from Orchestrator-presigned S3 URL
  if (path.startsWith('/media/')) {
    const cookies = parseCookies(req.headers.get('Cookie'));
    const edge = cookies[EDGE_COOKIE];
    const ok = await verifyEdge(edge);
    if (!ok) return new Response('Unauthorized', { status: 401 });

    // Map URL path to object key (strip leading /media/)
    const key = path.replace(/^\/media\//, '');
    if (!key) return new Response('Not found', { status: 404 });
    const cache = caches.default;
    const allowedOrigin = req.headers.get('Origin') || '*';
    // Build a cache key that doesn't include cookies
    const cacheKey = new Request(`${url.origin}${url.pathname}`, { method: 'GET' });

    // First: ask Orchestrator for a presigned URL and metadata
    const presignUrl = (typeof ORCHESTRATOR_URL !== 'undefined' ? ORCHESTRATOR_URL : '') + '/api/media/presign?key=' + encodeURIComponent(key);
    // Forward user's auth (Authorization header or MusicAI.Auth cookie) to presign endpoint so server can validate
    const forwardHeaders = {};
    const authHdr = req.headers.get('Authorization');
    if (authHdr) forwardHeaders['Authorization'] = authHdr;
    else if (cookies['MusicAI.Auth']) forwardHeaders['Cookie'] = `MusicAI.Auth=${encodeURIComponent(cookies['MusicAI.Auth'])}`;
    // If worker has an ORCHESTRATOR_API_KEY secret, authenticate the presign request with it
    // Use a custom header so the orchestrator's JWT middleware doesn't try to parse it.
    if (typeof ORCHESTRATOR_API_KEY !== 'undefined' && ORCHESTRATOR_API_KEY) {
      forwardHeaders['X-Orchestrator-Key'] = ORCHESTRATOR_API_KEY;
    }

    let presignResp;
    try {
      console.log('Fetching presign URL from orchestrator', presignUrl);
      presignResp = await fetch(presignUrl, { method: 'GET', headers: forwardHeaders });
      console.log('Presign response status', presignResp.status);
    } catch (e) {
      console.error('Error contacting presign endpoint', e);
      return new Response('Error contacting presign endpoint', { status: 502 });
    }
    if (!presignResp.ok) {
      console.error('Presign endpoint returned non-OK', presignResp.status);
      return new Response('Could not obtain presigned URL', { status: presignResp.status });
    }
    let meta;
    try { meta = await presignResp.json(); } catch (e) { console.error('Bad presign response', e); return new Response('Bad presign response', { status: 502 }); }
    console.log('Presign metadata', meta);
    const presigned = meta.url;
    const contentType = meta.contentType || 'application/octet-stream';
    const size = Number(meta.contentLength || 0);
    const etag = meta.etag || null;

    // If we already cached the full object, serve ranges from cache
    const cachedFull = await cache.match(cacheKey);
    const rangeHeader = req.headers.get('Range');

    // Helper: serve a slice from an ArrayBuffer response
    async function serveRangeFromBuffer(buf, start, end) {
      const total = buf.byteLength;
      const s = start ?? 0;
      const e = (typeof end === 'number') ? end : (total - 1);
      if (s > e || s < 0) return new Response('Range Not Satisfiable', { status: 416 });
      const slice = buf.slice(s, e + 1);
      const h = new Headers();
      h.set('Content-Type', contentType);
      h.set('Accept-Ranges', 'bytes');
      h.set('Content-Range', `bytes ${s}-${e}/${total}`);
      h.set('Content-Length', String(slice.byteLength));
      h.set('Cache-Control', `public, max-age=${SEG_TTL}`);
      h.set('Access-Control-Allow-Origin', 'https://www.tingoradio.ai');
      if (etag) h.set('ETag', etag);
      return new Response(slice, { status: 206, headers: h });
    }

    try {
      if (rangeHeader) {
        // If full object cached, slice and return
        if (cachedFull) {
          const cloned = cachedFull.clone();
          const buf = await cloned.arrayBuffer();
          const m = /bytes=(\d+)-(\d+)?/.exec(rangeHeader);
          if (!m) return new Response('Bad Range', { status: 400 });
          const start = Number(m[1]);
          const end = m[2] ? Number(m[2]) : undefined;
          const resp = await serveRangeFromBuffer(buf, start, end);
          // keep cached copy in background
          return resp;
        }

        // If object small enough, fetch full, cache it, then serve slice
        const MAX_CACHEABLE = 50 * 1024 * 1024; // 50 MB
        if (size > 0 && size <= MAX_CACHEABLE) {
          const originResp = await fetch(presigned, { method: 'GET' });
          console.log('Origin fetch (full) status', originResp.status, { key, presigned: presigned && presigned.slice(0,120) });
          if (!originResp.ok) {
            console.error('Origin returned error for full fetch', { status: originResp.status });
            return new Response('Origin error', { status: originResp.status });
          }
          const buf = await originResp.arrayBuffer();
          const fullResp = new Response(buf.slice(0), { status: 200, headers: { 'Content-Type': contentType, 'Cache-Control': `public, max-age=${SEG_TTL}`, 'Access-Control-Allow-Origin': allowedOrigin } });
          if (etag) fullResp.headers.set('ETag', etag);
          event.waitUntil(cache.put(cacheKey, fullResp.clone()));
          const m = /bytes=(\d+)-(\d+)?/.exec(rangeHeader);
          if (!m) return new Response('Bad Range', { status: 400 });
          const start = Number(m[1]);
          const end = m[2] ? Number(m[2]) : undefined;
          return await serveRangeFromBuffer(buf, start, end);
        }

        // Large object: forward Range to origin (do not buffer)
        const proxied = await fetch(presigned, { method: 'GET', headers: { 'Range': rangeHeader } });
        console.log('Origin fetch (range) status', proxied.status, { range: rangeHeader });
        // Ensure CORS and caching hints
        const headers = new Headers(proxied.headers);
        headers.set('Access-Control-Allow-Origin', allowedOrigin);
        headers.set('Cache-Control', headers.get('Cache-Control') || `public, max-age=${SEG_TTL}`);
        return new Response(proxied.body, { status: proxied.status, headers });
      }

      // No Range: try cache, then fetch full object and cache
      if (cachedFull) return cachedFull;
      const originResp = await fetch(presigned, { method: 'GET' });
      console.log('Origin fetch (full, no-range) status', originResp.status, { key });
      if (!originResp.ok) {
        console.error('Origin returned error for full fetch', { status: originResp.status });
        return new Response('Origin error', { status: originResp.status });
      }
      const headers = new Headers(originResp.headers);
      headers.set('Access-Control-Allow-Origin', allowedOrigin);
      headers.set('Cache-Control', headers.get('Cache-Control') || `public, max-age=${SEG_TTL}`);
      if (etag) headers.set('ETag', etag);
      const resp = new Response(originResp.body, { status: originResp.status, headers });
      console.log('Caching media at edge', cacheKey.url);
      event.waitUntil(cache.put(cacheKey, resp.clone()));
      return resp;
    } catch (e) {
      return new Response('Error fetching media', { status: 502 });
    }
  }

  return new Response('Not found', { status: 404 });
}
