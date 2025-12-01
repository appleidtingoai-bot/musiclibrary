# Spotify-Standard Streaming Features

This document describes the Spotify-like streaming features implemented in MusicAI Orchestrator.

## ✨ Features Overview

| Feature | Status | Description |
|---------|--------|-------------|
| **Continuous Playlist** | ✅ Implemented | Single M3U8 URL plays entire queue sequentially |
| **Adaptive Bitrate** | ✅ Implemented | Multiple quality versions (320/192/128kbps) |
| **Cross-Device Sync** | ✅ Implemented | Resume playback across devices via Redis queue |
| **Offline Downloads** | ✅ Implemented | Long-lived tokens for offline content |
| **Collaborative Playlists** | ✅ Implemented | Shared playlists with multiple contributors |
| **Chunk-Based Streaming** | ✅ Implemented | HLS segment conversion with FFmpeg |
| **CDN Integration** | ✅ Documented | CloudFront setup guide for edge caching |

---

## 🎵 1. Continuous Playlist (Single-URL Playback)

### What It Does
Frontend plays **one URL** instead of managing array of tracks client-side. Backend handles track transitions automatically.

### Endpoints

#### `GET /api/oap/playlist/{userId}?count=20`
Returns M3U8 manifest with all tracks as sequential segments.

**Response** (M3U8 file):
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

**Usage**:
```javascript
// Frontend (single line!)
audio.src = 'https://api.com/api/oap/playlist/user123?count=50';
audio.play();
// Plays all 50 tracks automatically, backend handles transitions
```

---

## 📊 2. Adaptive Bitrate Streaming

### What It Does
Serves different quality versions based on user's network speed. HLS player automatically switches qualities mid-stream.

### Endpoints

#### `GET /api/music/qualities/{s3Key}`
Lists available quality versions for a track.

**Response**:
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

#### `GET /api/music/quality-url?s3Key=...&quality=high&ttlMinutes=5`
Gets tokenized URL for specific quality.

#### `GET /api/music/adaptive-playlist/{s3Key}`
Returns HLS master playlist with all quality streams (player auto-switches).

**Response** (M3U8 file):
```m3u8
#EXTM3U
#EXT-X-VERSION:3
#EXT-X-STREAM-INF:BANDWIDTH=320000,RESOLUTION=1x1
https://api.com/api/music/hls/music/afropop/track_high.mp3?t=token
#EXT-X-STREAM-INF:BANDWIDTH=192000,RESOLUTION=1x1
https://api.com/api/music/hls/music/afropop/track_medium.mp3?t=token
#EXT-X-STREAM-INF:BANDWIDTH=128000,RESOLUTION=1x1
https://api.com/api/music/hls/music/afropop/track_low.mp3?t=token
```

### How to Generate Multiple Qualities

Use `AudioProcessingService` to convert original MP3 to multiple bitrates:

```csharp
var audioService = serviceProvider.GetService<IAudioProcessingService>();
var qualities = await audioService.GenerateMultipleQualitiesAsync("music/afropop/original.mp3");

// Creates:
// - music/afropop/original_high.mp3 (320kbps)
// - music/afropop/original_medium.mp3 (192kbps)
// - music/afropop/original_low.mp3 (128kbps)
```

**Requirements**: FFmpeg must be installed on server.

---

## 🔄 3. Cross-Device Sync (Queue State)

### What It Does
Saves playback position to Redis. User can pause on phone, resume on laptop at exact same spot.

### Endpoints

#### `GET /api/queue/{userId}`
Gets user's current queue state.

**Response**:
```json
{
  "exists": true,
  "tracks": [
    { "id": "1", "title": "Song 1", "artist": "Artist 1", "s3Key": "music/afropop/song1.mp3" },
    { "id": "2", "title": "Song 2", "artist": "Artist 2", "s3Key": "music/rnb/song2.mp3" }
  ],
  "currentTrackIndex": 1,
  "currentPositionSeconds": 45.3,
  "updatedAt": "2025-12-01T10:30:00Z",
  "oapId": "roman",
  "playlistId": "abc123"
}
```

#### `POST /api/queue/{userId}`
Sets new queue (when user starts playing playlist).

**Request**:
```json
{
  "tracks": [
    { "id": "1", "title": "Song 1", "artist": "Artist 1", "s3Key": "music/afropop/song1.mp3", "durationSeconds": 180 }
  ],
  "startIndex": 0,
  "oapId": "roman",
  "playlistId": "abc123"
}
```

#### `POST /api/queue/{userId}/position`
Updates current position (called every 10-30 seconds by frontend).

**Request**:
```json
{
  "trackIndex": 2,
  "positionSeconds": 67.8
}
```

#### `DELETE /api/queue/{userId}`
Clears queue.

### Frontend Implementation

```javascript
// When user plays track
await fetch('/api/queue/user123', {
  method: 'POST',
  body: JSON.stringify({
    tracks: playlist,
    startIndex: 0,
    playlistId: 'morning-mix'
  })
});

// Update position every 15 seconds
setInterval(async () => {
  await fetch('/api/queue/user123/position', {
    method: 'POST',
    body: JSON.stringify({
      trackIndex: currentIndex,
      positionSeconds: audio.currentTime
    })
  });
}, 15000);

// On app startup, resume from saved position
const queue = await fetch('/api/queue/user123').then(r => r.json());
if (queue.exists) {
  audio.src = queue.tracks[queue.currentTrackIndex].hlsUrl;
  audio.currentTime = queue.currentPositionSeconds;
  audio.play();
}
```

---

## 📥 4. Offline Downloads

### What It Does
Users can download tracks with long-lived tokens (24 hours) for offline playback.

### Endpoints

#### `POST /api/downloads/request`
Requests download permission for a track.

**Request**:
```json
{
  "s3Key": "music/afropop/track.mp3",
  "title": "Track Title",
  "artist": "Artist Name"
}
```

**Response**:
```json
{
  "downloadId": "dl-abc123",
  "downloadUrl": "https://api.com/api/music/stream/music/afropop/track.mp3?t=long_lived_token",
  "expiresAt": "2025-12-02T10:30:00Z",
  "validForHours": 24
}
```

#### `GET /api/downloads`
Lists all user's downloads.

**Response**:
```json
{
  "downloads": [
    {
      "id": "dl-abc123",
      "title": "Track Title",
      "artist": "Artist Name",
      "requestedAt": "2025-12-01T10:30:00Z",
      "expiresAt": "2025-12-02T10:30:00Z",
      "downloadUrl": "https://api.com/..."
    }
  ],
  "totalCount": 5
}
```

#### `DELETE /api/downloads/{downloadId}`
Removes download record.

### Frontend Implementation

```javascript
// Request download
const { downloadUrl } = await fetch('/api/downloads/request', {
  method: 'POST',
  body: JSON.stringify({
    s3Key: track.s3Key,
    title: track.title,
    artist: track.artist
  })
}).then(r => r.json());

// Download file
const blob = await fetch(downloadUrl).then(r => r.blob());
const url = URL.createObjectURL(blob);
const a = document.createElement('a');
a.href = url;
a.download = `${track.artist} - ${track.title}.mp3`;
a.click();
```

**Note**: Downloads use 24-hour tokens instead of 2-minute tokens for streaming.

---

## 👥 5. Collaborative Playlists

### What It Does
Users can create shared playlists and invite collaborators to add/remove tracks.

### Endpoints

#### `POST /api/playlists`
Creates new playlist.

**Request**:
```json
{
  "name": "Road Trip Mix",
  "description": "Best songs for driving",
  "isCollaborative": true
}
```

**Response**:
```json
{
  "playlistId": "pl-abc123",
  "name": "Road Trip Mix",
  "isCollaborative": true
}
```

#### `GET /api/playlists`
Lists user's playlists (owned + collaborative).

#### `GET /api/playlists/{playlistId}`
Gets playlist details with tracks and collaborators.

**Response**:
```json
{
  "id": "pl-abc123",
  "name": "Road Trip Mix",
  "isCollaborative": true,
  "isOwner": true,
  "tracks": [
    { "id": "1", "title": "Song 1", "artist": "Artist 1", "addedBy": "John Doe", "position": 1 }
  ],
  "collaborators": [
    { "id": "user2", "email": "jane@example.com", "name": "Jane Doe" }
  ]
}
```

#### `POST /api/playlists/{playlistId}/tracks`
Adds track to playlist (owner + collaborators can add).

**Request**:
```json
{
  "s3Key": "music/afropop/track.mp3",
  "title": "Track Title",
  "artist": "Artist Name"
}
```

#### `DELETE /api/playlists/{playlistId}/tracks/{trackId}`
Removes track from playlist (owner only).

#### `POST /api/playlists/{playlistId}/collaborators`
Adds collaborator to playlist (owner only).

**Request**:
```json
{
  "userId": "user2"
}
```

#### `DELETE /api/playlists/{playlistId}/collaborators/{userId}`
Removes collaborator from playlist (owner only).

#### `GET /api/playlists/{playlistId}/collaborators`
Lists collaborators.

### Database Setup

Run the SQL migration script:

```bash
psql -h your-db-host -U postgres -d musicai -f database_migration_playlists.sql
```

This creates:
- `playlists` table
- `playlist_collaborators` table
- `playlist_tracks` table
- Indexes for performance

---

## 🎞️ 6. Chunk-Based HLS Streaming

### What It Does
Converts MP3 files to HLS segments (.ts chunks) for progressive download. Enables seek without downloading entire file.

### Service: `AudioProcessingService`

#### Convert MP3 to HLS

```csharp
var audioService = serviceProvider.GetService<IAudioProcessingService>();
var result = await audioService.ConvertToHlsAsync(
    "music/afropop/track.mp3", 
    "music/afropop/track-hls"
);

// Creates:
// - music/afropop/track-hls/playlist.m3u8 (manifest)
// - music/afropop/track-hls/segment_000.ts
// - music/afropop/track-hls/segment_001.ts
// - ... (10-second chunks)
```

**Requirements**:
- FFmpeg installed on server
- Write access to temp directory

### Configuration

Segment length: 10 seconds (configurable in `AudioProcessingService.cs`)

### Frontend Usage

```javascript
// Play HLS manifest
audio.src = 'https://api.com/api/music/hls/music/afropop/track-hls/playlist.m3u8?t=token';
// Player downloads chunks progressively, enables instant seek
```

---

## 🌍 7. CDN Integration (CloudFront)

### What It Does
Caches audio files at edge locations globally for lower latency and reduced S3 costs.

### Setup Guide

See **[CLOUDFRONT_SETUP.md](./CLOUDFRONT_SETUP.md)** for detailed instructions.

**Quick Steps**:
1. Create CloudFront distribution with S3 origin
2. Configure Origin Access Control (OAC)
3. Update S3 bucket policy
4. Set `CLOUDFRONT_DOMAIN` environment variable
5. Update application to use CloudFront URLs

### Performance Benefits

| Metric | Direct S3 | With CloudFront |
|--------|-----------|-----------------|
| Average Latency (Global) | 200-500ms | 50-150ms |
| Cache Hit Rate | N/A | 85-95% |
| S3 Data Transfer Costs | $0.09/GB | $0.02/GB (after cache) |

---

## 🏗️ Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                         Frontend                                 │
│  - Plays continuous playlist (single M3U8 URL)                   │
│  - Adapts quality based on bandwidth                             │
│  - Syncs queue position to Redis every 15s                       │
│  - Downloads tracks for offline with 24hr tokens                 │
└──────────────────┬───────────────────────────────────────────────┘
                   │
                   │ HTTPS
                   │
┌──────────────────▼───────────────────────────────────────────────┐
│                   CloudFront CDN (Edge Cache)                    │
│  - Caches popular tracks globally                                │
│  - Reduces latency by 70%                                        │
│  - Handles adaptive bitrate switching                            │
└──────────────────┬───────────────────────────────────────────────┘
                   │
                   │ Cache Miss? Fetch from origin
                   │
┌──────────────────▼───────────────────────────────────────────────┐
│              MusicAI Orchestrator (API)                          │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │ OapChatController                                          │ │
│  │  - GET /api/oap/playlist/{userId}      (M3U8 manifest)    │ │
│  │  - GET /api/oap/playlist-metadata/...   (track list)      │ │
│  │  - GET /api/oap/track-url               (on-demand token) │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │ MusicQualityController                                     │ │
│  │  - GET /api/music/qualities/{s3Key}     (list qualities)  │ │
│  │  - GET /api/music/quality-url           (specific quality)│ │
│  │  - GET /api/music/adaptive-playlist/... (HLS master)      │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │ QueueController                                            │ │
│  │  - GET/POST /api/queue/{userId}         (cross-device)    │ │
│  │  - POST /api/queue/{userId}/position    (sync position)   │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │ DownloadsController                                        │ │
│  │  - POST /api/downloads/request          (24hr token)      │ │
│  │  - GET /api/downloads                   (list downloads)  │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │ PlaylistsController                                        │ │
│  │  - POST /api/playlists                  (create shared)   │ │
│  │  - POST /api/playlists/{id}/tracks      (add track)       │ │
│  │  - POST /api/playlists/{id}/collaborators (invite)        │ │
│  └────────────────────────────────────────────────────────────┘ │
└──────────────────┬───────────────────────────────────────────────┘
                   │
        ┌──────────┴────────────┐
        │                       │
┌───────▼──────┐       ┌────────▼─────────┐
│ PostgreSQL   │       │   Redis Cache    │
│              │       │                  │
│ - Users      │       │ - Queue State    │
│ - Playlists  │       │ - Downloads      │
│ - Tracks     │       │ - Sessions       │
└──────────────┘       └──────────────────┘
                   │
        ┌──────────┴────────────┐
        │                       │
┌───────▼──────┐       ┌────────▼─────────┐
│ S3 Bucket    │       │   FFmpeg         │
│              │       │                  │
│ - Original   │       │ - HLS Segments   │
│ - High 320k  │       │ - Quality Conv   │
│ - Medium 192k│       │                  │
│ - Low 128k   │       │                  │
└──────────────┘       └──────────────────┘
```

---

## 🚀 Deployment Checklist

### 1. Install Dependencies

```bash
# FFmpeg (required for audio processing)
sudo apt install ffmpeg -y

# Verify installation
ffmpeg -version
```

### 2. Database Migration

```bash
# Run playlist tables migration
psql -h your-db-host -U postgres -d musicai -f database_migration_playlists.sql
```

### 3. Environment Variables

Add to `.env`:

```bash
# Redis (for queue sync)
REDIS_CONNECTION=localhost:6379

# CloudFront (optional, for CDN)
CLOUDFRONT_DOMAIN=d1234abcd.cloudfront.net

# Existing variables
CONNECTIONSTRINGS__DEFAULT=Host=...;Database=musicai;...
AWS__S3BUCKET=tingoradiobucket
AWS__REGION=us-east-2
JWT_SECRET=your_secret_key
```

### 4. Build and Deploy

```bash
# Build application
dotnet publish MusicAI.Orchestrator/MusicAI.Orchestrator.csproj -c Release -o /var/www/musiclibrary/publish

# Copy .env file
scp .env user@server:/var/www/musiclibrary/publish/.env

# Restart service
sudo systemctl restart musicai-orchestrator
```

### 5. Test Features

```bash
# Test continuous playlist
curl https://api.com/api/oap/playlist/user123?count=10

# Test adaptive quality
curl https://api.com/api/music/qualities/music/afropop/track.mp3

# Test queue sync
curl -X POST https://api.com/api/queue/user123 \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"tracks":[...],"startIndex":0}'

# Test download request
curl -X POST https://api.com/api/downloads/request \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"s3Key":"music/afropop/track.mp3"}'
```

---

## 📈 Monitoring

### Key Metrics to Track

1. **Cache Hit Rate** (CloudFront): Should be > 80%
2. **Queue Sync Frequency**: Monitor Redis operations/sec
3. **Quality Distribution**: Track which bitrates users prefer
4. **Download Requests**: Monitor offline download patterns
5. **Collaborative Activity**: Track playlist shares and collaborations

### CloudWatch Dashboards

Monitor:
- API Gateway latency
- S3 request counts
- Redis connection pool
- PostgreSQL query performance

---

## 🔒 Security Considerations

1. **Token Expiry**: Streaming tokens = 2 min, Download tokens = 24 hours
2. **CORS**: Restrict origins in CORS policy
3. **Rate Limiting**: Implement rate limits on download requests
4. **Collaborator Permissions**: Only owners can remove tracks
5. **Queue Privacy**: Users can only access their own queues

---

## 💡 Best Practices

### Frontend

1. **Pre-fetch Next Track**: Request token for track N+1 when N is 80% complete
2. **Update Position Regularly**: Sync queue position every 15-30 seconds
3. **Handle Token Expiry**: Refresh URLs when tokens near expiration
4. **Adaptive Quality**: Detect bandwidth and request appropriate quality

### Backend

1. **Cache Popular Tracks**: Use CloudFront for frequently accessed content
2. **Batch Processing**: Generate multiple qualities during upload, not on-demand
3. **Queue TTL**: Expire queue state after 7 days of inactivity
4. **Download Limits**: Limit downloads per user per day (e.g., 50)

---

## 🆘 Troubleshooting

### Issue: Queue not syncing across devices

**Solution**: Verify Redis connection, check `REDIS_CONNECTION` environment variable

### Issue: Adaptive quality not working

**Solution**: Generate quality variants first using `AudioProcessingService.GenerateMultipleQualitiesAsync()`

### Issue: HLS segments not playing

**Solution**: Verify FFmpeg is installed, check temp directory permissions

### Issue: Collaborative playlist 403 error

**Solution**: Run database migration script to create playlist tables

---

## 📚 API Reference

Full API documentation available at:
- Swagger UI: `https://your-api.com/swagger/index.html`
- Postman Collection: [Download here](./postman_collection.json)

---

## 🎯 Roadmap

### Completed ✅
- Continuous playlist endpoint
- Adaptive bitrate streaming
- Cross-device queue sync
- Offline downloads
- Collaborative playlists
- HLS chunk-based streaming
- CloudFront CDN documentation

### Planned 🔮
- Pre-loading next 3 tracks in background
- Lyrics display API
- Social sharing for playlists
- Analytics dashboard
- Podcast support
- Live radio streams

---

## 📞 Support

For issues or questions:
- GitHub Issues: [Create issue](https://github.com/your-repo/issues)
- Documentation: [Full docs](./README.md)
- Email: support@tingoradio.ai

---

**Last Updated**: December 1, 2025
**Version**: 2.0.0
**Status**: Production Ready ✅
