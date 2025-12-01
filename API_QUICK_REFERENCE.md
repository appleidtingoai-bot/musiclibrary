# 🎵 MusicAI API Quick Reference - Spotify Features

## Essential Endpoints

### 🎶 Continuous Playlist (No Auth Required)
```bash
# Get M3U8 playlist - plays all tracks sequentially
GET /api/oap/playlist/{userId}?count=20
Response: M3U8 file (application/vnd.apple.mpegurl)

# Get track metadata without URLs
GET /api/oap/playlist-metadata/{userId}?count=20
Response: { oap, tracks[], totalCount }

# Get tokenized URL for specific track
GET /api/oap/track-url?s3Key=music/afropop/track.mp3&ttlMinutes=5
Response: { hlsUrl, streamUrl, expiresIn }
```

### 📊 Adaptive Quality (No Auth Required)
```bash
# List available quality versions
GET /api/music/qualities/music/afropop/track.mp3
Response: { s3Key, qualities[], recommended }

# Get URL for specific quality
GET /api/music/quality-url?s3Key=...&quality=high&ttlMinutes=5
Response: { hlsUrl, streamUrl, quality, expiresIn }

# Get HLS master playlist (auto-switches quality)
GET /api/music/adaptive-playlist/music/afropop/track.mp3
Response: M3U8 with multiple quality streams
```

### 🔄 Queue Sync (Auth Required)
```bash
# Get user's queue state
GET /api/queue/{userId}
Headers: Authorization: Bearer <token>
Response: { exists, tracks[], currentTrackIndex, currentPositionSeconds }

# Set new queue
POST /api/queue/{userId}
Headers: Authorization: Bearer <token>
Body: { tracks[], startIndex, oapId, playlistId }
Response: { success, trackCount, playlistId }

# Update playback position
POST /api/queue/{userId}/position
Headers: Authorization: Bearer <token>
Body: { trackIndex, positionSeconds }
Response: { success }

# Clear queue
DELETE /api/queue/{userId}
Headers: Authorization: Bearer <token>
Response: { success }
```

### 📥 Offline Downloads (Auth Required)
```bash
# Request download (24hr token)
POST /api/downloads/request
Headers: Authorization: Bearer <token>
Body: { s3Key, title, artist }
Response: { downloadId, downloadUrl, expiresAt, validForHours }

# List user's downloads
GET /api/downloads
Headers: Authorization: Bearer <token>
Response: { downloads[], totalCount }

# Delete download
DELETE /api/downloads/{downloadId}
Headers: Authorization: Bearer <token>
Response: { success }
```

### 👥 Collaborative Playlists (Auth Required)
```bash
# Create playlist
POST /api/playlists
Headers: Authorization: Bearer <token>
Body: { name, description, isCollaborative }
Response: { playlistId, name, isCollaborative }

# List user's playlists
GET /api/playlists
Headers: Authorization: Bearer <token>
Response: { playlists[], totalCount }

# Get playlist details
GET /api/playlists/{playlistId}
Headers: Authorization: Bearer <token>
Response: { id, name, tracks[], collaborators[], isOwner }

# Add track to playlist
POST /api/playlists/{playlistId}/tracks
Headers: Authorization: Bearer <token>
Body: { s3Key, title, artist }
Response: { success }

# Remove track from playlist
DELETE /api/playlists/{playlistId}/tracks/{trackId}
Headers: Authorization: Bearer <token>
Response: { success }

# Add collaborator
POST /api/playlists/{playlistId}/collaborators
Headers: Authorization: Bearer <token>
Body: { userId }
Response: { success }

# Remove collaborator
DELETE /api/playlists/{playlistId}/collaborators/{userId}
Headers: Authorization: Bearer <token>
Response: { success }

# List collaborators
GET /api/playlists/{playlistId}/collaborators
Headers: Authorization: Bearer <token>
Response: { collaborators[], totalCount }
```

---

## Frontend Integration Patterns

### Pattern 1: Simple Continuous Playback
```javascript
// Simplest approach - one line!
audio.src = 'https://api.com/api/oap/playlist/user123?count=50';
audio.play();
// Backend handles all track transitions
```

### Pattern 2: Manual Queue with Pre-fetching
```javascript
// Get metadata first
const { tracks } = await fetch('/api/oap/playlist-metadata/user123?count=50')
  .then(r => r.json());

// Display queue to user
displayQueue(tracks);

// Request URL for first track
const { hlsUrl } = await fetch(`/api/oap/track-url?s3Key=${tracks[0].s3Key}`)
  .then(r => r.json());

audio.src = hlsUrl;
audio.play();

// Pre-fetch next track when current is 80% complete
audio.addEventListener('timeupdate', async () => {
  if (audio.currentTime / audio.duration > 0.8) {
    const nextTrack = tracks[currentIndex + 1];
    const { hlsUrl } = await fetch(`/api/oap/track-url?s3Key=${nextTrack.s3Key}`)
      .then(r => r.json());
    // Pre-load next track
    preloadAudio.src = hlsUrl;
  }
});
```

### Pattern 3: Cross-Device Sync
```javascript
// On app startup, restore queue
const queue = await fetch('/api/queue/user123', {
  headers: { 'Authorization': `Bearer ${token}` }
}).then(r => r.json());

if (queue.exists) {
  // Resume from saved position
  const track = queue.tracks[queue.currentTrackIndex];
  const { hlsUrl } = await fetch(`/api/oap/track-url?s3Key=${track.s3Key}`)
    .then(r => r.json());
  
  audio.src = hlsUrl;
  audio.currentTime = queue.currentPositionSeconds;
  audio.play();
}

// Update position every 15 seconds
setInterval(() => {
  fetch(`/api/queue/user123/position`, {
    method: 'POST',
    headers: { 
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      trackIndex: currentIndex,
      positionSeconds: audio.currentTime
    })
  });
}, 15000);
```

### Pattern 4: Adaptive Quality
```javascript
// Detect bandwidth and select quality
const connection = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
let quality = 'medium';

if (connection) {
  const downlink = connection.downlink; // Mbps
  if (downlink >= 5) quality = 'high';
  else if (downlink < 2) quality = 'low';
}

// Request specific quality
const { hlsUrl } = await fetch(
  `/api/music/quality-url?s3Key=${track.s3Key}&quality=${quality}`
).then(r => r.json());

audio.src = hlsUrl;

// OR use adaptive playlist (HLS player auto-switches)
audio.src = `/api/music/adaptive-playlist/${track.s3Key}`;
```

### Pattern 5: Offline Download
```javascript
async function downloadTrack(track) {
  // Request download permission
  const { downloadUrl } = await fetch('/api/downloads/request', {
    method: 'POST',
    headers: { 
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      s3Key: track.s3Key,
      title: track.title,
      artist: track.artist
    })
  }).then(r => r.json());

  // Download file
  const blob = await fetch(downloadUrl).then(r => r.blob());
  const url = URL.createObjectURL(blob);
  
  // Trigger download
  const a = document.createElement('a');
  a.href = url;
  a.download = `${track.artist} - ${track.title}.mp3`;
  a.click();
  
  // Store in IndexedDB for offline playback
  await storeInIndexedDB(track.id, blob);
}
```

---

## Authentication

### Get Token
```bash
# Login
POST /api/Onboarding/login
Body: { email, password }
Response: { token, userId, email }

# Use token in requests
curl -H "Authorization: Bearer <token>" https://api.com/api/queue/user123
```

---

## Testing Commands

### Test Continuous Playlist
```bash
curl https://tingoradiomusiclibrary.tingoai.ai/api/oap/playlist/user123?count=5
```

### Test Adaptive Quality
```bash
curl https://tingoradiomusiclibrary.tingoai.ai/api/music/qualities/music/afropop/track.mp3
```

### Test Queue (requires token)
```bash
TOKEN="your_jwt_token"

curl -X POST https://tingoradiomusiclibrary.tingoai.ai/api/queue/user123 \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"tracks":[{"id":"1","title":"Song","artist":"Artist","s3Key":"music/afropop/song.mp3"}],"startIndex":0}'

curl https://tingoradiomusiclibrary.tingoai.ai/api/queue/user123 \
  -H "Authorization: Bearer $TOKEN"
```

### Test Download (requires token)
```bash
curl -X POST https://tingoradiomusiclibrary.tingoai.ai/api/downloads/request \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"s3Key":"music/afropop/track.mp3","title":"Track","artist":"Artist"}'
```

### Test Playlist (requires token)
```bash
curl -X POST https://tingoradiomusiclibrary.tingoai.ai/api/playlists \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Road Trip","isCollaborative":true}'
```

---

## Response Examples

### Continuous Playlist (M3U8)
```m3u8
#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:600
#EXT-X-PLAYLIST-TYPE:VOD
#EXTINF:180,Artist - Track 1
https://api.com/api/music/stream/music/afropop/track1.mp3?t=token123
#EXTINF:210,Artist - Track 2
https://api.com/api/music/stream/music/rnb/track2.mp3?t=token456
#EXT-X-ENDLIST
```

### Quality Versions (JSON)
```json
{
  "s3Key": "music/afropop/track.mp3",
  "qualities": [
    { "quality": "high", "bitrate": 320, "label": "High (320kbps)", "s3Key": "music/afropop/track_high.mp3" },
    { "quality": "medium", "bitrate": 192, "label": "Medium (192kbps)", "s3Key": "music/afropop/track_medium.mp3" },
    { "quality": "low", "bitrate": 128, "label": "Low (128kbps)", "s3Key": "music/afropop/track_low.mp3" },
    { "quality": "original", "bitrate": 256, "label": "Original", "s3Key": "music/afropop/track.mp3" }
  ],
  "recommended": "medium"
}
```

### Queue State (JSON)
```json
{
  "exists": true,
  "tracks": [
    { "id": "1", "title": "Song 1", "artist": "Artist 1", "s3Key": "music/afropop/song1.mp3", "durationSeconds": 180 },
    { "id": "2", "title": "Song 2", "artist": "Artist 2", "s3Key": "music/rnb/song2.mp3", "durationSeconds": 210 }
  ],
  "currentTrackIndex": 0,
  "currentPositionSeconds": 45.3,
  "updatedAt": "2025-12-01T10:30:00Z",
  "oapId": "roman",
  "playlistId": "abc123"
}
```

---

## Error Handling

### Common Errors
```javascript
// 401 Unauthorized - Missing or invalid token
if (response.status === 401) {
  // Redirect to login
  window.location.href = '/login';
}

// 403 Forbidden - Token expired or wrong user
if (response.status === 403) {
  // Refresh token or request new one
  await refreshToken();
}

// 404 Not Found - Track or playlist doesn't exist
if (response.status === 404) {
  console.error('Resource not found');
}

// 500 Internal Server Error
if (response.status === 500) {
  console.error('Server error, try again later');
}
```

---

## Quick Links

- **Swagger UI**: https://tingoradiomusiclibrary.tingoai.ai/swagger/index.html
- **Full Documentation**: [SPOTIFY_FEATURES.md](./SPOTIFY_FEATURES.md)
- **Deployment Guide**: [DEPLOYMENT_SPOTIFY_FEATURES.md](./DEPLOYMENT_SPOTIFY_FEATURES.md)
- **CloudFront Setup**: [CLOUDFRONT_SETUP.md](./CLOUDFRONT_SETUP.md)

---

**Last Updated**: December 1, 2025  
**Version**: 2.0.0  
**Base URL**: https://tingoradiomusiclibrary.tingoai.ai
