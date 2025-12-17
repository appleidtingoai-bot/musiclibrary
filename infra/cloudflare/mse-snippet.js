// Minimal MediaSource (MSE) snippet for near-gapless playback.
// Usage: fetch initial segments via your audio proxy (/media/<key>) and append to SourceBuffer.

const audio = document.querySelector('audio');
const mse = new MediaSource();
audio.src = URL.createObjectURL(mse);

async function appendTrack(mse, key) {
  const resp = await fetch(`/media/${encodeURIComponent(key)}`);
  if (!resp.ok) throw new Error('fetch failed');
  const contentType = resp.headers.get('Content-Type') || 'audio/mpeg';
  if (!mse) return;
  return new Promise(async (resolve, reject) => {
    mse.addEventListener('sourceopen', async () => {
      const sb = mse.addSourceBuffer(contentType);
      const buf = await resp.arrayBuffer();
      sb.appendBuffer(buf);
      sb.addEventListener('updateend', () => {
        resolve();
      }, { once: true });
    }, { once: true });
  });
}

// Example: play two tracks sequentially with prefetch of next track's first bytes
async function playSequence(keys) {
  for (let i = 0; i < keys.length; i++) {
    await appendTrack(mse, keys[i]);
    // prefetch next track first bytes (Range header) to warm cache
    if (i + 1 < keys.length) {
      fetch(`/media/${encodeURIComponent(keys[i+1])}`, { headers: { Range: 'bytes=0-262143' } });
    }
  }
  audio.play();
}
