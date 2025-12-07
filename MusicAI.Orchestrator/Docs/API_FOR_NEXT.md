API usage notes for Next.js frontend

This file documents the key orchestrator API endpoints and example usage from a Next.js frontend (client-side), so you can deploy the API to AWS and test from your Next app.

Key endpoints

- GET /api/oap/playlist/{userId}?manifestOnly=true
  - Returns a JSON payload with `continuousManifest` (HLS text) or playlist metadata and `controls` links.
  - Example (client-side fetch):

    const res = await fetch(`/api/oap/playlist/${encodeURIComponent(userId)}?manifestOnly=true`);
    const payload = await res.json();
    // payload.continuousManifest contains an m3u8 text or a newline list of object URLs

- GET /api/oap/stream-proxy?s3Key={s3Key}
  - Same-origin proxy that streams a presigned S3 object. Use this as the `src` for an <audio> element to avoid CORS on private S3 objects.
  - Supports `Range` requests and will return 206 Partial Content when ranges are supplied.
  - Example usage in browser audio element:

    const url = `/api/oap/stream-proxy?s3Key=${encodeURIComponent(s3Key)}`;
    audioElement.src = url; // can be a direct <audio> src or used by WebAudio fetch

- GET /api/oap/track-url?s3Key={s3Key}
  - Returns JSON with `streamUrl` (presigned S3 URL) and `expiresIn`. Useful if you prefer direct presigned URLs (beware of CORS).

- Control endpoints (POST)
  - POST /api/oap/control/{userId}/play
  - POST /api/oap/control/{userId}/pause
  - POST /api/oap/control/{userId}/skip
  - POST /api/oap/control/{userId}/previous
  - POST /api/oap/control/{userId}/volume (body: { volume: 0-1 })
  - POST /api/oap/control/{userId}/shuffle (body: { enabled: true/false })
  - Example:

    await fetch(`/api/oap/control/${encodeURIComponent(userId)}/play`, { method: 'POST' });

- WebSocket (optional realtime state)
  - ws://{host}/api/oap/ws/{userId}
  - Connect from the client to receive player state updates.

Next.js example (React component) — simple playlist fetch + audio playback using proxy

import { useState } from 'react';

export default function Player({ userId }) {
  const [playlist, setPlaylist] = useState([]);

  async function fetchPlaylist() {
    const res = await fetch(`/api/oap/playlist/${encodeURIComponent(userId)}?manifestOnly=true`);
    const j = await res.json();
    // If j.continuousManifest contains HLS, you can either serve it to hls.js or parse the lines
    // For direct object playback, extract URLs and map to the stream-proxy
    const lines = (j.continuousManifest || '').split(/\r?\n/).map(l=>l.trim()).filter(Boolean);
    const urls = [];
    for (let i=0;i<lines.length;i++) {
      const L = lines[i];
      if (!L.startsWith('#') && L.match(/\.(mp3|wav|m4a)(\?|$)/i)) urls.push(L);
    }
    // convert to proxy URLs
    const proxies = urls.map(u => {
      try {
        const parsed = new URL(u);
        const pathname = parsed.pathname.replace(/^\//, '');
        return `/api/oap/stream-proxy?s3Key=${encodeURIComponent(pathname)}`;
      } catch(e) { return u; }
    });
    setPlaylist(proxies);
  }

  return (
    <div>
      <button onClick={fetchPlaylist}>Fetch Playlist</button>
      {playlist.map((p, idx) => (
        <div key={idx}>
          <audio controls src={p} preload="none"/>
        </div>
      ))}
    </div>
  );
}

CORS and deployment notes

- During development the orchestrator logs show CORS enabled for `http://localhost:3000` by default. Ensure your Next.js origin is allowed by the API CORS settings when deployed.
- For production, prefer serving HLS via CloudFront and use signed cookies (or signed URLs) rather than per-track presigned S3 URLs in the playlist JSON. The orchestrator has env flags `CDN_DOMAIN`, `CLOUDFRONT_DOMAIN` and CloudFront signing config to integrate later.

Autoplay and user gestures

- Browsers require a user gesture before audio playback is allowed. Make sure to fetch playlists and call `audio.play()` after a button click or user interaction in your Next.js UI.

Security

- The control endpoints and proxy were allowed for anonymous access during local development. Secure them (authentication/authorization) before deploying to production.

If you'd like, I can:
- Add a minimal example Next.js page in this repo that calls the API and demonstrates playback using the proxy endpoint.
- Or, add server-side logging / a small diagnostic HEAD endpoint to help debugging presign/CORS in AWS.

Tell me which you'd like next.