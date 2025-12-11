Cloudflare R2 + Worker scaffold

Files provided:
- `worker-r2.js` - Worker script that issues an edge cookie (`Edge-Play`) and serves `/media/*` from an R2 bucket named `tingomusiclibrary` (configured in `wrangler.toml`).
- `wrangler.toml` - wrangler config template. Fill `account_id` before publishing.

Quick steps to publish:
1. Install wrangler:
   ```powershell
   npm install -g wrangler
   wrangler login
   ```
2. Add secrets:
   ```powershell
   wrangler secret put EDGE_SIGN_KEY
   wrangler secret put JWT_SECRET
   ```
3. Publish (after filling `account_id` in `wrangler.toml`):
   ```powershell
   wrangler publish infra/cloudflare/worker-r2.js --name musicai-cdn-worker
   ```

Notes:
- Worker expects binding `R2_MEDIA` with bucket name `tingomusiclibrary` (already set in `wrangler.toml`). Configure the binding in Cloudflare dashboard or via wrangler.
- The Worker uses a simple HS256 JWT check when `JWT_SECRET` is present. For production, use RS256 + JWKS.
- The Worker stores responses in the edge cache (`caches.default`) and sets `Cache-Control` on media items.
- Range support is implemented by slicing the R2 object into memory; this is fine for HLS segments (small sizes). For very large files use a streaming approach and more advanced Range handling.
