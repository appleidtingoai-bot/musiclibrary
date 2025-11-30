# Secure Music Streaming System - Quick Reference

## Overview
**Admin POSTs** music to S3 bucket → **OAPs GET** secure, non-copyable streaming URLs

## Key Security Features
✅ **One-time use tokens** - URLs expire after first play  
✅ **30-second expiration** - Tokens invalid after 30 seconds  
✅ **IP validation** - Token locked to requesting IP address  
✅ **Referer checking** - Prevents direct linking  
✅ **S3 proxy** - Presigned URLs never exposed to client  
✅ **No cache headers** - Browser won't cache streaming URLs  

---

## Admin Workflow (POST Music)

### 1. Login
```bash
POST /api/admin/login
Content-Type: application/json

{
  "email": "admin@example.com",
  "password": "yourpassword"
}

Response: { "token": "abc123...", "isSuper": true }
```

### 2. Upload Music to Folder
```bash
POST /api/admin/bulk-upload?folder=hip-hop
Headers:
  X-Admin-Token: abc123...
  Content-Type: multipart/form-data

Body: [50-100 music files]

Response:
{
  "folder": "Hip Hop",
  "folderKey": "hip-hop",
  "uploadedCount": 50,
  "uploaded": [...]
}
```

**Available Folders:** hip-hop, rnb, gospel, afrobeats, jazz, electronic, pop, rock, country, reggae, classical, blues, latin, soul, funk, indie, folk, house, ambient, lo-fi

---

## OAP Workflow (GET Secure Stream)

### Step 1: Request Streaming Token
```bash
GET /api/oap/get-stream-token?mood=energetic&random=true

Response:
{
  "folder": "Hip Hop",
  "folderKey": "hip-hop",
  "fileName": "track.mp3",
  "streamUrl": "http://localhost:5000/api/oap/stream?token=xyz789...",
  "expiresIn": 30,
  "warning": "This URL is one-time use only and expires in 30 seconds.",
  "metadata": {
    "moods": ["energetic", "confident", "bold"],
    "vibes": ["urban", "street", "powerful"]
  }
}
```

**Query Parameters:**
- `folder` - Specific folder (e.g., `hip-hop`, `jazz`)
- `mood` - Filter by mood (e.g., `energetic`, `relaxed`)
- `vibe` - Filter by vibe (e.g., `urban`, `chill`)
- `random` - Random selection (default: true)

### Step 2: Stream Music
```bash
GET /api/oap/stream?token=xyz789...

Response: Audio stream (audio/mpeg)
```

**Security:**
- Token is **one-time use** - invalid after first stream
- Token **expires in 30 seconds** if not used
- Token is **IP-locked** - only works from requesting IP
- Token is **consumed immediately** - cannot be copied to another tab

---

## OAP Helper Endpoints

### List Available Folders
```bash
GET /api/oap/folders?mood=energetic

Response:
{
  "totalFolders": 6,
  "folders": [
    {
      "name": "hip-hop",
      "displayName": "Hip Hop",
      "description": "Urban beats, rap, and hip hop culture",
      "moods": ["energetic", "confident", "bold", "rebellious"],
      "vibes": ["urban", "street", "powerful", "rhythmic"]
    }
  ]
}
```

### Get Available Moods
```bash
GET /api/oap/moods

Response:
{
  "totalMoods": 35,
  "moods": ["artistic", "bold", "celebratory", ...]
}
```

### Get Available Vibes
```bash
GET /api/oap/vibes

Response:
{
  "totalVibes": 40,
  "vibes": ["acoustic", "alternative", "atmospheric", ...]
}
```

---

## Implementation Flow

```
┌─────────────┐
│    Admin    │
└──────┬──────┘
       │ POST /api/admin/bulk-upload?folder=hip-hop
       │ Headers: X-Admin-Token: xxx
       ▼
┌─────────────────┐
│   S3 Bucket     │
│  music/hip-hop/ │
│  music/jazz/    │
│  music/gospel/  │
└─────────────────┘
       ▲
       │ Secure Proxy (presigned URL hidden)
       │
┌──────┴──────┐
│    OAP      │
└─────────────┘
  1. GET /api/oap/get-stream-token?mood=energetic
     → Returns one-time token + streamUrl
  
  2. GET /api/oap/stream?token=xyz789
     → Streams music to browser
     → Token consumed and destroyed
```

---

## Security Architecture

### Token Lifecycle
1. OAP requests token for music (`/get-stream-token`)
2. System generates **cryptographically secure** token
3. Token stored with:
   - Music key (S3 path)
   - OAP identifier
   - IP address
   - Creation timestamp
4. Token returned in `streamUrl`
5. OAP uses token to stream (`/stream?token=xyz`)
6. System validates:
   - Token exists
   - Not expired (< 30 seconds old)
   - Not already used
   - IP matches
   - Referer matches
7. Token **immediately consumed** and deleted
8. Music streamed via secure proxy
9. S3 presigned URL **never exposed** to client

### Why URLs Cannot Be Copied

❌ **Copy URL to new tab** → Token already consumed, returns 401  
❌ **Copy URL to another browser** → IP validation fails, returns 401  
❌ **Wait 30+ seconds** → Token expired, returns 401  
❌ **Direct link from external site** → Referer check fails, returns 403  
❌ **Inspect network to get S3 URL** → Server proxies, S3 URL never sent to client  

---

## Response Headers (Security)

All streaming responses include:
```
X-Content-Type-Options: nosniff
Cache-Control: no-store, no-cache, must-revalidate, private
Pragma: no-cache
Expires: 0
Accept-Ranges: bytes
```

This prevents:
- Browser caching of streaming URLs
- MIME type sniffing attacks
- Proxy caching

---

## OAP Personality Mapping

| OAP Mood | Recommended Query |
|----------|-------------------|
| Energetic/Pumped Up | `?mood=energetic` |
| Relaxed/Chill | `?mood=relaxed` |
| Romantic/Smooth | `?mood=romantic` |
| Spiritual/Uplifting | `?mood=uplifting` |
| Creative/Artistic | `?mood=creative` |
| Dancing/Party | `?mood=dancing` |

| OAP Vibe | Recommended Query |
|----------|-------------------|
| Urban/Street | `?vibe=urban` |
| Smooth/Sultry | `?vibe=smooth` |
| Chill/Laid-back | `?vibe=chill` |
| Powerful/Bold | `?vibe=powerful` |
| Rhythmic/Groovy | `?vibe=rhythmic` |

---

## Error Responses

### 401 Unauthorized (Token Invalid)
```json
{
  "error": "Invalid, expired, or already used token",
  "message": "Please request a new streaming token via /api/oap/get-stream-token"
}
```

### 404 Not Found (No Music Available)
```json
{
  "error": "No music available in selected folder"
}
```

### 400 Bad Request (Missing Token)
```json
{
  "error": "Token required"
}
```

---

## Testing Example

```bash
# 1. Admin uploads music
curl -X POST "http://localhost:5000/api/admin/bulk-upload?folder=hip-hop" \
  -H "X-Admin-Token: your-admin-token" \
  -F "file1=@track1.mp3" \
  -F "file2=@track2.mp3"

# 2. OAP requests streaming token
curl "http://localhost:5000/api/oap/get-stream-token?mood=energetic&random=true"

# Response:
# {
#   "streamUrl": "http://localhost:5000/api/oap/stream?token=xyz789abc...",
#   "expiresIn": 30
# }

# 3. OAP streams music (use streamUrl from step 2)
curl "http://localhost:5000/api/oap/stream?token=xyz789abc..." --output music.mp3

# 4. Try to reuse token (will fail)
curl "http://localhost:5000/api/oap/stream?token=xyz789abc..."
# Response: 401 Unauthorized
```

---

## Browser Integration

```javascript
// OAP client code
async function playMusic(mood) {
  // Step 1: Get streaming token
  const response = await fetch(`/api/oap/get-stream-token?mood=${mood}&random=true`);
  const data = await response.json();
  
  // Step 2: Play music using token URL
  const audio = new Audio(data.streamUrl);
  audio.play();
  
  // Note: streamUrl is one-time use only
  // If user seeks/pauses, the same token will work
  // But copying URL to new tab will NOT work
}

// Usage
playMusic('energetic'); // Gets random energetic track and plays it
```

---

## Summary

**Admin:** POST uploads music to S3 bucket organized by folders  
**OAP:** GET streaming token → Stream music via secure proxy  
**Security:** One-time tokens, IP validation, no URL copying, S3 URLs hidden  
**Result:** Secure, non-copyable music streaming directly to browser speakers  

✅ **Simple** - Two-step process for OAPs  
✅ **Secure** - Multiple layers of protection  
✅ **Fast** - Direct streaming with range support  
✅ **Organized** - 20 mood/vibe-based folders  
