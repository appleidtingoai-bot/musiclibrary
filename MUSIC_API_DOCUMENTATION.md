# Music Management System - API Documentation

## Overview
This system provides a comprehensive music management infrastructure for OAPs (On-Air Personalities) to discover and play music based on their personality, mood, and vibes.

## Architecture

### Components
1. **MusicFolder Model** - Defines 20 genre/vibe categories with mood/vibe metadata
2. **AdminController** - Admin endpoints for music upload and management
3. **MusicDiscoveryController** - Public endpoints for OAPs to discover and stream music

### Music Folders (20 Categories)

| Folder | Display Name | Moods | Vibes |
|--------|--------------|-------|-------|
| `hip-hop` | Hip Hop | energetic, confident, bold, rebellious | urban, street, powerful, rhythmic |
| `rnb` | R&B | romantic, smooth, sensual, emotional | smooth, sultry, groovy, intimate |
| `gospel` | Gospel | uplifting, spiritual, joyful, hopeful | inspirational, powerful, soulful, blessed |
| `afrobeats` | Afrobeats | energetic, joyful, dancing, celebratory | vibrant, rhythmic, cultural, infectious |
| `jazz` | Jazz | relaxed, sophisticated, contemplative, smooth | elegant, improvisational, classy, timeless |
| `electronic` | Electronic | energetic, futuristic, intense, euphoric | digital, synthetic, pulsing, hypnotic |
| `pop` | Pop | happy, upbeat, catchy, fun | mainstream, accessible, melodic, commercial |
| `rock` | Rock | energetic, rebellious, passionate, intense | raw, powerful, guitar-driven, authentic |
| `country` | Country | nostalgic, storytelling, heartfelt, honest | rustic, traditional, down-to-earth, wholesome |
| `reggae` | Reggae | relaxed, positive, carefree, peaceful | island, laid-back, rhythmic, chill |
| `classical` | Classical | contemplative, peaceful, sophisticated, emotional | elegant, timeless, refined, majestic |
| `blues` | Blues | melancholic, soulful, emotional, reflective | raw, authentic, heartfelt, gritty |
| `latin` | Latin | passionate, energetic, romantic, festive | spicy, rhythmic, cultural, dancing |
| `soul` | Soul | emotional, passionate, heartfelt, nostalgic | deep, authentic, groovy, timeless |
| `funk` | Funk | energetic, fun, groovy, upbeat | funky, rhythmic, danceable, groovy |
| `indie` | Indie | creative, introspective, unique, artistic | alternative, experimental, authentic, hipster |
| `folk` | Folk | peaceful, storytelling, nostalgic, warm | acoustic, authentic, earthy, traditional |
| `house` | House | energetic, euphoric, dancing, uplifting | clubby, groovy, electronic, pulsing |
| `ambient` | Ambient | relaxed, meditative, peaceful, contemplative | atmospheric, spacey, ethereal, calm |
| `lo-fi` | Lo-Fi | relaxed, focused, nostalgic, cozy | chill, mellow, nostalgic, study |

---

## Admin Endpoints

### 1. Login
**Endpoint:** `POST /api/admin/login`

**Purpose:** Authenticate and obtain admin token

**Request:**
```json
{
  "email": "admin@example.com",
  "password": "yourpassword"
}
```

**Response:**
```json
{
  "token": "abc123...",
  "expires": "2025-12-06T14:00:00Z",
  "isSuper": true
}
```

**Usage:** Store the token and include it in subsequent requests as `X-Admin-Token` header.

---

### 2. Get Music Folders
**Endpoint:** `GET /api/admin/music-folders`

**Headers:** 
```
X-Admin-Token: <your-token>
```

**Query Parameters:**
- `mood` (optional) - Filter by mood (e.g., `energetic`, `relaxed`)
- `vibe` (optional) - Filter by vibe (e.g., `urban`, `chill`)

**Response:**
```json
{
  "totalFolders": 20,
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

---

### 3. Bulk Upload Music
**Endpoint:** `POST /api/admin/bulk-upload?folder={folderName}`

**Headers:** 
```
X-Admin-Token: <your-token>
Content-Type: multipart/form-data
```

**Query Parameters:**
- `folder` (required) - Folder name (e.g., `hip-hop`, `jazz`, `gospel`)

**Request:** Form data with multiple files (1-100+ files supported)

**Response:**
```json
{
  "folder": "Hip Hop",
  "folderKey": "hip-hop",
  "totalFiles": 50,
  "uploadedCount": 48,
  "rejectedCount": 2,
  "rejected": [
    {
      "file": "bad-file.mp3",
      "reason": "Profanity detected in filename"
    }
  ],
  "uploaded": [
    {
      "file": "track1.mp3",
      "key": "music/hip-hop/abc123_track1.mp3",
      "url": "https://...",
      "folder": "hip-hop",
      "displayName": "Hip Hop"
    }
  ]
}
```

**Features:**
- No minimum file requirement (removed 100-file minimum)
- 20 concurrent uploads for speed
- Automatic profanity filtering
- Retry logic with exponential backoff
- Support for S3 or local filesystem

- Support for S3 or local filesystem

### 4. Smart Bulk Upload (Auto-Categorization)
**Endpoint:** `POST /api/admin/smart-bulk-upload`

**Headers:** 
```
X-Admin-Token: <your-token>
Content-Type: multipart/form-data
```

**Query Parameters:**
- `genreHint` (optional) - Hint to help categorization (e.g., "energetic", "gospel")

**Request:** Form data with multiple files (optimized for 25 concurrent uploads)

**Response:**
```json
{
  "message": "Smart categorization and upload complete",
  "totalFiles": 10,
  "uploadedCount": 10,
  "categoryDistribution": {
    "hip-hop": 6,
    "rnb": 4
  },
  "categorizations": [
    {
      "file": "drake-song.mp3",
      "folder": "hip-hop",
      "confidence": 80,
      "reasoning": "Found 'rap' in filename"
    }
  ]
}
```

**Features:**
- **Intelligent Analysis:** Automatically detects genre/vibe from filenames and metadata
- **High Performance:** 25 concurrent upload streams (25% faster than standard)
- **Auto-Organization:** Sorts files into correct folders automatically
- **Security:** Profanity filtering and secure S3 storage

---

### 5. Get Folder Contents
**Endpoint:** `GET /api/admin/folder-contents/{folderName}`

**Headers:** 
```
X-Admin-Token: <your-token>
```

**Response:**
```json
{
  "folder": "Hip Hop",
  "folderKey": "hip-hop",
  "fileCount": 48,
  "files": [
    {
      "fileName": "track1.mp3",
      "size": 5242880,
      "key": "music/hip-hop/track1.mp3"
    }
  ]
}
```

---

## OAP Discovery Endpoints (Public)

### 1. Discover Music
**Endpoint:** `GET /api/music/discover`

**Query Parameters:**
- `mood` (optional) - Filter by mood
- `vibe` (optional) - Filter by vibe

**Example:** `GET /api/music/discover?mood=energetic`

**Response:**
```json
{
  "totalFolders": 6,
  "query": {
    "mood": "energetic",
    "vibe": null
  },
  "folders": [
    {
      "name": "hip-hop",
      "displayName": "Hip Hop",
      "description": "Urban beats, rap, and hip hop culture",
      "moods": ["energetic", "confident", "bold", "rebellious"],
      "vibes": ["urban", "street", "powerful", "rhythmic"],
      "accessUrl": "/api/music/folder/hip-hop"
    }
  ]
}
```

---

### 2. Get Available Moods
**Endpoint:** `GET /api/music/moods`

**Response:**
```json
{
  "totalMoods": 35,
  "moods": ["artistic", "bold", "carefree", "celebratory", ...]
}
```

---

### 3. Get Available Vibes
**Endpoint:** `GET /api/music/vibes`

**Response:**
```json
{
  "totalVibes": 40,
  "vibes": ["accessible", "acoustic", "alternative", "atmospheric", ...]
}
```

---

### 4. Get Folder Music
**Endpoint:** `GET /api/music/folder/{folderName}`

**Query Parameters:**
- `limit` (optional) - Limit number of tracks returned
- `random` (optional) - Randomize track order (boolean)

**Example:** `GET /api/music/folder/hip-hop?limit=10&random=true`

**Response:**
```json
{
  "folder": "Hip Hop",
  "folderKey": "hip-hop",
  "description": "Urban beats, rap, and hip hop culture",
  "moods": ["energetic", "confident", "bold", "rebellious"],
  "vibes": ["urban", "street", "powerful", "rhythmic"],
  "trackCount": 10,
  "tracks": [
    {
      "fileName": "track1.mp3",
      "size": 5242880,
      "key": "music/hip-hop/track1.mp3",
      "streamUrl": "/api/music/stream?key=music/hip-hop/track1.mp3"
    }
  ]
}
```

---

### 5. Stream Music
**Endpoint:** `GET /api/music/stream?key={musicKey}`

**Example:** `GET /api/music/stream?key=music/hip-hop/track1.mp3`

**Response:** Audio stream (supports range requests for seeking)

**Content-Type:** `audio/mpeg`, `audio/wav`, `audio/mp4`, or `audio/flac`

---

### 6. Get Random Track
**Endpoint:** `GET /api/music/random`

**Query Parameters:**
- `mood` (optional) - Filter by mood
- `vibe` (optional) - Filter by vibe

**Example:** `GET /api/music/random?mood=relaxed`

**Response:**
```json
{
  "folder": "Jazz",
  "folderKey": "jazz",
  "fileName": "smooth-jazz.mp3",
  "key": "music/jazz/smooth-jazz.mp3",
  "streamUrl": "/api/music/stream?key=music/jazz/smooth-jazz.mp3",
  "mood": "relaxed",
  "vibe": null
}
```

---

## OAP Usage Workflow

### Scenario 1: OAP wants energetic music
```
1. GET /api/music/discover?mood=energetic
   → Returns hip-hop, afrobeats, electronic, pop, rock, funk, house

2. GET /api/music/folder/afrobeats?random=true&limit=5
   → Returns 5 random Afrobeats tracks

3. Stream music: /api/music/stream?key=music/afrobeats/track123.mp3
```

### Scenario 2: OAP wants surprise selection
```
1. GET /api/music/random?vibe=chill
   → Returns a random track from folders with "chill" vibe (reggae, ambient, lo-fi)

2. Stream the returned track directly
```

### Scenario 3: Admin uploads new music
```
1. POST /api/admin/login
   → Get token

2. GET /api/admin/music-folders
   → See available folders

3. POST /api/admin/bulk-upload?folder=gospel
   → Upload 50 gospel tracks with header: X-Admin-Token: <token>

4. GET /api/admin/folder-contents/gospel
   → Verify uploads succeeded
```

---

## Technical Details

### Concurrency
- Bulk uploads use 20 parallel streams for optimal speed
- Can handle 50-100+ files simultaneously

### File Support
- MP3, WAV, M4A, FLAC formats
- Range request support for seeking
- Automatic content-type detection

### Storage
- S3 bucket: `music/{folder-name}/{guid}_{filename}`
- Local fallback: `uploads/{folder-name}/{filename}`

### Security
- Admin endpoints require `X-Admin-Token` header
- Profanity filtering on filenames
- Path validation to prevent directory traversal
- Session-based token management with 7-day expiry

---

## Error Responses

### 401 Unauthorized
```json
{
  "error": "admin required"
}
```
**Fix:** Include `X-Admin-Token` header with valid token

### 400 Bad Request
```json
{
  "error": "Invalid folder. Use /api/admin/music-folders to see available folders.",
  "availableFolders": ["hip-hop", "jazz", ...]
}
```

### 404 Not Found
```json
{
  "error": "Music file not found"
}
```

---

## Best Practices for OAPs

1. **Discover First:** Use `/api/music/discover` with mood/vibe filters to find relevant folders
2. **Cache Metadata:** Moods and vibes lists are static - cache them client-side
3. **Use Random:** The `/api/music/random` endpoint is perfect for spontaneous selections
4. **Personality Mapping:**
   - Bold/Confident OAPs → hip-hop, rock, electronic
   - Smooth/Romantic OAPs → rnb, soul, jazz
   - Uplifting/Positive OAPs → gospel, pop, reggae
   - Creative/Unique OAPs → indie, folk, ambient

---

## Next Steps

1. **For Admins:**
   - Login via `/api/admin/login`
   - Upload music to appropriate folders based on genre/vibe
   - Verify uploads via `/api/admin/folder-contents/{folder}`

2. **For OAP Integration:**
   - Implement personality → mood/vibe mapping
   - Use discovery endpoints to find matching music
   - Stream audio via `/api/music/stream`

3. **For Enhanced Features:**
   - Implement S3 `ListObjects` for better folder exploration
   - Add music metadata (artist, album, duration)
   - Create playlists based on mood combinations
   - Add listening analytics for OAP preferences
