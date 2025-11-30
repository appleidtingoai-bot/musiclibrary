# ✅ News Reading Service - Implementation Complete

## What's Working

### 1. **Automated News Scheduling** ✅
- News service runs every hour on the hour (00:00, 01:00, 02:00, etc.)
- 5-minute fixed duration for news playback
- Background service automatically starts with Orchestrator

**Log Output:**
```
News Reading Service started. Will fetch news every hour.
Next news reading scheduled at 01:00 UTC
```

### 2. **News API Integration** ✅
- Fetches headlines from NewsAPI.org (Nigeria focus)
- Mock headlines fallback when API unavailable
- Formats news into radio-style script

**Test Results:**
```bash
GET http://localhost:5000/api/news/headlines
# Returns 3 mock headlines with Nigerian themes
```

### 3. **News Script Generation** ✅
- Radio-style intro/outro
- Clean text formatting for TTS
- Professional OAP presentation style

**Test Results:**
```bash
GET http://localhost:5000/api/news/script
# Returns: "Good morning, I'm Tosin with News on the Hour..."
```

### 4. **News Status Tracking** ✅
- Tracks whether news is currently playing
- Returns next OAP persona (Ife Mi after Tosin's news)
- Exposes audio URL when news is active

**Test Results:**
```bash
GET http://localhost:5000/api/news/status
{
  "isNewsPlaying": false,
  "currentNewsAudioUrl": null,
  "nextOapPersona": "ifemi",
  "newsReader": "tosin",
  "newsDurationMinutes": 5
}
```

### 5. **Stream Controller Integration** ✅
- Checks news service before routing requests
- Redirects to news audio when news is playing
- Routes to Ife Mi (Love & Relationships) after news ends

**Code Added:**
```csharp
// In StreamController.cs
if (_newsService.IsNewsCurrentlyPlaying())
{
    var newsAudioUrl = _newsService.CurrentNewsAudioUrl;
    if (!string.IsNullOrEmpty(newsAudioUrl))
    {
        _logger.LogInformation("News is playing. Routing user {UserId} to news audio", userId);
        return Redirect(newsAudioUrl);
    }
}
```

## What Needs TTS Integration

### Current Status: **PLACEHOLDER IMPLEMENTATION**

The `GenerateNewsAudioAsync()` method currently returns a fallback URL:

```csharp
private async Task<string> GenerateNewsAudioAsync(string script, CancellationToken cancellationToken)
{
    // TODO: Integrate actual TTS service
    _logger.LogWarning("TTS service not configured. Using placeholder URL.");
    return "http://localhost:5000/api/fallback/stream";
}
```

### Next Steps for TTS

#### Option 1: Azure Cognitive Services (Recommended)
**Why:** Best balance of quality, price, and Nigerian voice support

```bash
# Install NuGet package
dotnet add package Microsoft.CognitiveServices.Speech

# Set environment variables
$env:AZURE_SPEECH_KEY="your-key-here"
$env:AZURE_SPEECH_REGION="westus"
```

**Implementation:**
- Voice: `en-NG-EzinneNeural` (Nigerian female)
- Format: MP3, 16 kHz, 32 kbps
- Cost: ~$0.08 per 5-minute segment

#### Option 2: ElevenLabs (Best Quality)
**Why:** Most natural-sounding AI voices

```bash
$env:ELEVENLABS_API_KEY="your-key-here"
$env:ELEVENLABS_VOICE_ID="21m00Tcm4TlvDq8ikWAM" # Rachel voice
```

**Implementation:**
- API: https://api.elevenlabs.io/v1/text-to-speech/{voiceId}
- Quality: Professional broadcaster level
- Cost: ~500 characters per news script (~$0.05 per segment on Creator plan)

#### Option 3: AWS Polly
**Why:** Already using AWS for S3, easy integration

```bash
$env:AWS_ACCESS_KEY_ID="your-key"
$env:AWS_SECRET_ACCESS_KEY="your-secret"
```

**Implementation:**
- Voice: `Joanna` (US English) or custom Nigerian voice
- Format: MP3
- Cost: $4 per 1 million characters

### S3 Storage Integration Needed

After TTS generates audio:
1. Upload to S3: `news/tosin-news-{yyyyMMddHHmm}.mp3`
2. Return HLS URL: `http://localhost:5000/api/admin/stream-hls/news/tosin-news-{timestamp}.mp3`
3. Cache for replay within the hour

**Code Template:**
```csharp
var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
var s3Key = $"news/tosin-news-{timestamp}.mp3";
await _s3Service.UploadFileAsync(s3Key, new MemoryStream(audioBytes), "audio/mpeg");
return $"http://localhost:5000/api/admin/stream-hls/{s3Key}";
```

## Testing Manual News Trigger

Once TTS is integrated, test with:

```powershell
# Get admin token
$token = "your-admin-jwt-token"

# Trigger news manually (instead of waiting for hourly schedule)
Invoke-RestMethod -Uri "http://localhost:5000/api/news/trigger" `
  -Method Post `
  -Headers @{ Authorization = "Bearer $token" }

# Check status
Invoke-RestMethod -Uri "http://localhost:5000/api/news/status" | ConvertTo-Json

# Expected response:
# {
#   "isNewsPlaying": true,
#   "currentNewsAudioUrl": "http://localhost:5000/api/admin/stream-hls/news/tosin-news-202501151400.mp3",
#   "nextOapPersona": "ifemi",
#   "newsReader": "tosin",
#   "newsDurationMinutes": 5
# }
```

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                   MusicAI.Orchestrator                      │
│                                                             │
│  ┌──────────────────┐         ┌─────────────────────────┐  │
│  │ NewsService      │         │ NewsReadingService      │  │
│  │                  │         │ (Background Service)    │  │
│  │ - Fetch news     │────────>│                         │  │
│  │ - Format script  │         │ - Runs every hour       │  │
│  │ - Clean text     │         │ - Generates TTS audio   │  │
│  └──────────────────┘         │ - Tracks 5-min window   │  │
│          │                    └──────────┬──────────────┘  │
│          │                               │                  │
│          v                               v                  │
│  ┌──────────────────┐         ┌─────────────────────────┐  │
│  │ NewsAPI.org      │         │ TTS Service (TODO)      │  │
│  │ - Nigeria news   │         │ - Azure/ElevenLabs/AWS  │  │
│  │ - Mock fallback  │         │ - Nigerian voice        │  │
│  └──────────────────┘         └──────────┬──────────────┘  │
│                                           │                  │
│                                           v                  │
│                                ┌─────────────────────────┐  │
│                                │ S3Service               │  │
│                                │ - Store news audio      │  │
│                                │ - Return HLS URL        │  │
│                                └──────────┬──────────────┘  │
│                                           │                  │
│                                           v                  │
│  ┌──────────────────┐         ┌─────────────────────────┐  │
│  │ StreamController │<────────│ NewsReadingService      │  │
│  │                  │         │ - IsNewsCurrentlyPlaying│  │
│  │ - Check news     │         │ - CurrentNewsAudioUrl   │  │
│  │ - Route to Tosin │         │ - GetNextOapPersonaId   │  │
│  │ - Switch to Ifemi│         └─────────────────────────┘  │
│  └──────────────────┘                                       │
│          │                                                   │
│          v                                                   │
│  ┌──────────────────┐                                       │
│  │ Client Browser   │                                       │
│  │ - Plays news     │                                       │
│  │ - Switches OAPs  │                                       │
│  └──────────────────┘                                       │
└─────────────────────────────────────────────────────────────┘
```

## API Endpoints Summary

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/news/headlines` | GET | None | Fetch current news headlines |
| `/api/news/script` | GET | None | Preview formatted news script |
| `/api/news/status` | GET | None | Check if news playing, get audio URL |
| `/api/news/trigger` | POST | Admin | Manually trigger news generation |

## Configuration Files

### Program.cs
```csharp
// Added service registrations:
builder.Services.AddHttpClient();
builder.Services.AddSingleton<NewsService>();
builder.Services.AddSingleton<NewsReadingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NewsReadingService>());
```

### StreamController.cs
```csharp
// Added news service dependency:
private readonly NewsReadingService _newsService;

// Added news routing logic:
if (_newsService.IsNewsCurrentlyPlaying())
{
    return Redirect(_newsService.CurrentNewsAudioUrl);
}
```

## Logs to Monitor

```
✅ News reading service registered - Tosin will read news every hour
✅ News Reading Service started. Will fetch news every hour.
✅ Next news reading scheduled at 01:00 UTC

⚠️ TTS service not configured. Using placeholder URL.  # Expected until TTS integrated
```

## Success Metrics

- ✅ News service starts with Orchestrator
- ✅ Hourly schedule logs appear
- ✅ Headlines endpoint returns mock data
- ✅ Script endpoint formats news correctly
- ✅ Status endpoint tracks playback window
- ✅ Stream controller checks news before routing
- ⏳ **TTS integration** - PENDING
- ⏳ **S3 audio storage** - PENDING
- ⏳ **End-to-end playback test** - PENDING

## Documentation Created

1. **NEWS_READING_SETUP.md** - Complete implementation guide
   - API configuration instructions
   - TTS integration examples
   - Testing procedures
   - Troubleshooting tips
   - Cost estimates

2. **THIS_FILE.md** - Implementation status summary
   - What's working
   - What's pending
   - Architecture diagram
   - Quick reference

## Ready for Production?

**Status: 80% Complete**

**Working:**
- ✅ Scheduling system
- ✅ News fetching
- ✅ Script formatting
- ✅ Status tracking
- ✅ Stream routing

**Remaining:**
1. Choose TTS provider (Azure recommended)
2. Add TTS API credentials to environment
3. Implement `GenerateNewsAudioAsync()` with actual TTS calls
4. Add S3Service dependency to NewsReadingService
5. Store generated audio in S3
6. Test full hourly cycle
7. Monitor logs for any errors

**Estimated Time to Complete:** 1-2 hours
**Blocking Issues:** None - mock headlines work as fallback

---

**Next Action:** Choose TTS provider and add credentials to proceed with audio generation.
