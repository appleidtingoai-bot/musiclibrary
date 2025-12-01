# Deployment Guide - Spotify Features Update

## 📋 Summary

This update adds **7 major Spotify-like streaming features** to MusicAI Orchestrator:

1. ✅ **Continuous Playlist** - Single M3U8 URL plays entire queue
2. ✅ **Adaptive Bitrate** - Multiple quality versions (320/192/128kbps)
3. ✅ **Cross-Device Sync** - Resume playback via Redis queue state
4. ✅ **Offline Downloads** - Long-lived tokens for offline content
5. ✅ **Collaborative Playlists** - Shared playlists with contributors
6. ✅ **Chunk-Based Streaming** - HLS segment conversion
7. ✅ **CDN Integration** - CloudFront setup documentation

---

## 🔧 Prerequisites

### Server Requirements
- ✅ PostgreSQL 15+ (already installed)
- ✅ Redis 7+ (for queue sync - **NEW**)
- ✅ FFmpeg (for audio processing - **NEW**)
- ✅ .NET 8 SDK (already installed)

### AWS Requirements
- ✅ S3 bucket (tingoradiobucket - already configured)
- ⏳ CloudFront distribution (optional - see CLOUDFRONT_SETUP.md)

---

## 🚀 Deployment Steps

### Step 1: Install FFmpeg on Server

```bash
# SSH into server
ssh user@3.21.104.88

# Install FFmpeg
sudo apt update
sudo apt install ffmpeg -y

# Verify installation
ffmpeg -version
# Should show: ffmpeg version 4.4+ ...
```

### Step 2: Install Redis (for Queue Sync)

```bash
# Install Redis
sudo apt install redis-server -y

# Start Redis
sudo systemctl start redis-server
sudo systemctl enable redis-server

# Verify Redis is running
redis-cli ping
# Should return: PONG
```

### Step 3: Run Database Migration

```bash
# On local machine, copy migration file to server
scp database_migration_playlists.sql user@3.21.104.88:/tmp/

# SSH into server
ssh user@3.21.104.88

# Run migration
psql -h tingoai.c7e2gg6q461a.us-east-2.rds.amazonaws.com \
     -U postgres \
     -d musicai \
     -f /tmp/database_migration_playlists.sql

# Verify tables were created
psql -h tingoai.c7e2gg6q461a.us-east-2.rds.amazonaws.com \
     -U postgres \
     -d musicai \
     -c "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name LIKE 'playlist%';"

# Expected output:
#        table_name        
# -------------------------
#  playlists
#  playlist_collaborators
#  playlist_tracks
```

### Step 4: Update Environment Variables

Add to `.env` file on **local machine**:

```bash
# Redis connection (for queue sync)
REDIS_CONNECTION=localhost:6379

# CloudFront (optional - for CDN)
# CLOUDFRONT_DOMAIN=d1234abcd.cloudfront.net

# Existing variables (keep as is)
CONNECTIONSTRINGS__DEFAULT=Host=tingoai.c7e2gg6q461a.us-east-2.rds.amazonaws.com;Database=musicai;Username=postgres;Password=...
AWS__S3BUCKET=tingoradiobucket
AWS__REGION=us-east-2
AWS__AccessKey=...
AWS__SecretKey=...
JWT_SECRET=...
ADMIN_SUPER_SECRET=...
Cors__AllowedOrigins=http://localhost:3000,https://tingoradio.ai,https://tingoradiomusiclibrary.tingoai.ai
```

### Step 5: Build Application Locally

```powershell
# On local Windows machine

# Clean and rebuild (ensures updated MusicAI.Infrastructure is compiled)
dotnet clean
dotnet build MusicAI.Orchestrator/MusicAI.Orchestrator.csproj -c Release

# Check for errors
# Should show: Build succeeded. 0 Error(s)

# Publish application
dotnet publish MusicAI.Orchestrator/MusicAI.Orchestrator.csproj `
    -c Release `
    -o publish

# Verify new services were included
Get-ChildItem publish\*.dll | Select-String -Pattern "QueueService|QualityService|AudioProcessing"
```

### Step 6: Deploy to Server

```powershell
# Stop service on server first
ssh user@3.21.104.88 "sudo systemctl stop musicai-orchestrator"

# Copy published files to server
scp -r publish/* user@3.21.104.88:/var/www/musiclibrary/publish/

# Copy .env file with Redis config
scp .env user@3.21.104.88:/var/www/musiclibrary/publish/.env

# Update Redis connection on server (if Redis is on same machine)
ssh user@3.21.104.88 "echo 'REDIS_CONNECTION=localhost:6379' >> /var/www/musiclibrary/publish/.env"

# Restart service
ssh user@3.21.104.88 "sudo systemctl start musicai-orchestrator"

# Check status
ssh user@3.21.104.88 "sudo systemctl status musicai-orchestrator"
# Should show: active (running)
```

### Step 7: Verify Deployment

```bash
# Check logs for new services registered
ssh user@3.21.104.88 "sudo journalctl -u musicai-orchestrator -f"

# Look for these lines:
# ✓ Loaded environment overrides
# ✓ AWS S3 configured
# ✓ Music library repositories registered successfully
# ✓ Now listening on: http://localhost:5000

# Test new endpoints
curl https://tingoradiomusiclibrary.tingoai.ai/api/oap/playlist/user123?count=5
# Should return M3U8 manifest

curl https://tingoradiomusiclibrary.tingoai.ai/api/oap/playlist-metadata/user123?count=5
# Should return track metadata JSON
```

---

## 🧪 Testing New Features

### Test 1: Continuous Playlist

```bash
# Get M3U8 playlist
curl https://tingoradiomusiclibrary.tingoai.ai/api/oap/playlist/user123?count=10

# Expected response (M3U8 file):
# #EXTM3U
# #EXT-X-VERSION:3
# #EXT-X-TARGETDURATION:600
# #EXT-X-PLAYLIST-TYPE:VOD
# #EXTINF:180,Artist - Track
# https://tingoradiomusiclibrary.tingoai.ai/api/music/stream/music/afropop/track.mp3?t=...
# #EXT-X-ENDLIST

# Play in browser (works with HTML5 audio or video.js)
open https://tingoradiomusiclibrary.tingoai.ai/api/oap/playlist/user123?count=10
```

### Test 2: Adaptive Quality

```bash
# Get available qualities for a track
curl https://tingoradiomusiclibrary.tingoai.ai/api/music/qualities/music/afropop/track.mp3

# Expected response:
# {
#   "qualities": [
#     {"quality": "high", "bitrate": 320, "label": "High (320kbps)"},
#     {"quality": "original", "bitrate": 256, "label": "Original"}
#   ],
#   "recommended": "medium"
# }
```

### Test 3: Queue Sync (requires authentication)

```bash
# Get auth token first
TOKEN=$(curl -X POST https://tingoradiomusiclibrary.tingoai.ai/api/Onboarding/login \
  -H "Content-Type: application/json" \
  -d '{"email":"test@example.com","password":"password"}' \
  | jq -r '.token')

# Set queue
curl -X POST https://tingoradiomusiclibrary.tingoai.ai/api/queue/user123 \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "tracks": [
      {"id":"1","title":"Song 1","artist":"Artist 1","s3Key":"music/afropop/song1.mp3"}
    ],
    "startIndex": 0
  }'

# Get queue
curl https://tingoradiomusiclibrary.tingoai.ai/api/queue/user123 \
  -H "Authorization: Bearer $TOKEN"

# Expected response:
# {
#   "exists": true,
#   "tracks": [...],
#   "currentTrackIndex": 0,
#   "currentPositionSeconds": 0
# }
```

### Test 4: Offline Download

```bash
# Request download
curl -X POST https://tingoradiomusiclibrary.tingoai.ai/api/downloads/request \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "s3Key": "music/afropop/track.mp3",
    "title": "Track Title",
    "artist": "Artist Name"
  }'

# Expected response:
# {
#   "downloadId": "dl-abc123",
#   "downloadUrl": "https://tingoradiomusiclibrary.tingoai.ai/api/music/stream/music/afropop/track.mp3?t=long_token",
#   "expiresAt": "2025-12-02T10:30:00Z",
#   "validForHours": 24
# }

# List downloads
curl https://tingoradiomusiclibrary.tingoai.ai/api/downloads \
  -H "Authorization: Bearer $TOKEN"
```

### Test 5: Collaborative Playlists

```bash
# Create playlist
curl -X POST https://tingoradiomusiclibrary.tingoai.ai/api/playlists \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Road Trip Mix",
    "description": "Best songs for driving",
    "isCollaborative": true
  }'

# Expected response:
# {
#   "playlistId": "pl-abc123",
#   "name": "Road Trip Mix",
#   "isCollaborative": true
# }

# Add track to playlist
curl -X POST https://tingoradiomusiclibrary.tingoai.ai/api/playlists/pl-abc123/tracks \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "s3Key": "music/afropop/track.mp3",
    "title": "Track Title",
    "artist": "Artist Name"
  }'

# Get playlist
curl https://tingoradiomusiclibrary.tingoai.ai/api/playlists/pl-abc123 \
  -H "Authorization: Bearer $TOKEN"
```

---

## 📊 New API Endpoints Added

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/oap/playlist/{userId}` | GET | Continuous M3U8 playlist |
| `/api/oap/playlist-metadata/{userId}` | GET | Track list without URLs |
| `/api/oap/track-url` | GET | On-demand tokenized URL |
| `/api/music/qualities/{s3Key}` | GET | Available quality versions |
| `/api/music/quality-url` | GET | Specific quality URL |
| `/api/music/adaptive-playlist/{s3Key}` | GET | HLS master playlist |
| `/api/queue/{userId}` | GET | Get queue state |
| `/api/queue/{userId}` | POST | Set queue |
| `/api/queue/{userId}/position` | POST | Update position |
| `/api/queue/{userId}` | DELETE | Clear queue |
| `/api/downloads/request` | POST | Request offline download |
| `/api/downloads` | GET | List downloads |
| `/api/downloads/{downloadId}` | DELETE | Remove download |
| `/api/playlists` | POST | Create playlist |
| `/api/playlists` | GET | List playlists |
| `/api/playlists/{id}` | GET | Get playlist details |
| `/api/playlists/{id}/tracks` | POST | Add track |
| `/api/playlists/{id}/tracks/{trackId}` | DELETE | Remove track |
| `/api/playlists/{id}/collaborators` | POST | Add collaborator |
| `/api/playlists/{id}/collaborators/{userId}` | DELETE | Remove collaborator |
| `/api/playlists/{id}/collaborators` | GET | List collaborators |

**Total**: 21 new endpoints added ✅

---

## 📁 New Files Created

| File | Description |
|------|-------------|
| `Services/QueueService.cs` | Redis-based queue state management |
| `Services/QualityService.cs` | Adaptive bitrate quality service |
| `Services/AudioProcessingService.cs` | FFmpeg HLS conversion |
| `Controllers/QueueController.cs` | Queue management endpoints |
| `Controllers/DownloadsController.cs` | Offline download endpoints |
| `Controllers/PlaylistsController.cs` | Collaborative playlist endpoints |
| `Controllers/MusicQualityController.cs` | Quality/adaptive endpoints |
| `Data/PlaylistsRepository.cs` | Playlist database operations |
| `database_migration_playlists.sql` | Database schema for playlists |
| `CLOUDFRONT_SETUP.md` | CloudFront CDN setup guide |
| `SPOTIFY_FEATURES.md` | Complete feature documentation |

**Total**: 11 new files ✅

---

## 🔄 Modified Files

| File | Changes |
|------|---------|
| `Program.cs` | Registered new services (Queue, Quality, AudioProcessing, Playlists) |
| `OapChatController.cs` | Added 3 new endpoints (playlist, playlist-metadata, track-url) |
| `IS3Service` interface | Added ObjectExistsAsync, DownloadFileAsync, UploadFileAsync overload |
| `S3Service.cs` | Implemented new interface methods |

---

## ⚠️ Known Issues & Fixes

### Issue 1: Compilation Errors

If you see errors about missing methods in `IS3Service`:

```bash
# Solution: Clean and rebuild both projects
dotnet clean
dotnet restore
dotnet build MusicAI.Infrastructure/MusicAI.Infrastructure.csproj
dotnet build MusicAI.Orchestrator/MusicAI.Orchestrator.csproj
```

### Issue 2: Redis Connection Failed

If you see "Redis connection refused":

```bash
# Check Redis is running
sudo systemctl status redis-server

# Check Redis config allows connections
sudo nano /etc/redis/redis.conf
# Ensure: bind 127.0.0.1 ::1

# Restart Redis
sudo systemctl restart redis-server
```

### Issue 3: FFmpeg Not Found

If you see "ffmpeg: command not found":

```bash
# Install FFmpeg
sudo apt install ffmpeg -y

# Verify PATH
which ffmpeg
# Should return: /usr/bin/ffmpeg
```

### Issue 4: Database Migration Failed

If playlist tables don't exist:

```bash
# Check database connection
psql -h tingoai.c7e2gg6q461a.us-east-2.rds.amazonaws.com -U postgres -d musicai -c "SELECT version();"

# Re-run migration with verbose output
psql -h tingoai.c7e2gg6q461a.us-east-2.rds.amazonaws.com \
     -U postgres \
     -d musicai \
     -a -f /tmp/database_migration_playlists.sql
```

---

## 🎯 Post-Deployment Checklist

- [ ] FFmpeg installed and accessible
- [ ] Redis running and accepting connections
- [ ] Database migration completed (3 new tables)
- [ ] .env file updated with REDIS_CONNECTION
- [ ] Application rebuilt and published
- [ ] Service restarted on server
- [ ] Logs show new services registered
- [ ] Swagger UI shows new endpoints
- [ ] Test continuous playlist endpoint
- [ ] Test queue sync with authentication
- [ ] Test collaborative playlist creation

---

## 📚 Documentation

- **Full Features Guide**: [SPOTIFY_FEATURES.md](./SPOTIFY_FEATURES.md)
- **CloudFront Setup**: [CLOUDFRONT_SETUP.md](./CLOUDFRONT_SETUP.md)
- **API Documentation**: https://tingoradiomusiclibrary.tingoai.ai/swagger/index.html

---

## 🆘 Troubleshooting

### Application Won't Start

```bash
# Check logs
sudo journalctl -u musicai-orchestrator -n 100 --no-pager

# Common issues:
# - Missing REDIS_CONNECTION env var
# - PostgreSQL connection timeout
# - Missing playlist tables
```

### Endpoints Return 404

```bash
# Verify routing in Swagger
open https://tingoradiomusiclibrary.tingoai.ai/swagger/index.html

# Check controller is loaded
sudo journalctl -u musicai-orchestrator | grep -i "controller"
```

### Queue Sync Not Working

```bash
# Test Redis directly
redis-cli ping
redis-cli KEYS "queue:*"

# Check connection string
cat /var/www/musiclibrary/publish/.env | grep REDIS
```

---

## 📞 Support

For issues:
1. Check logs: `sudo journalctl -u musicai-orchestrator -f`
2. Review documentation: `SPOTIFY_FEATURES.md`
3. Test endpoints in Swagger UI
4. Verify environment variables in `.env`

---

**Deployment Date**: December 1, 2025  
**Version**: 2.0.0  
**Status**: Ready for deployment ✅
