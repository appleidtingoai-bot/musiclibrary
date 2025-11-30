# Automated News Reading System

## Overview
Tosin OAP now automatically reads news headlines every hour for 5 minutes, then switches to Ife Mi (Love & Relationships OAP).

## Architecture

### 1. News Service (`NewsService.cs`)
- Fetches top headlines from NewsAPI.org (Nigeria focus)
- Formats news into radio-style script with intro/outro
- Cleans text for TTS (removes HTML, special characters)
- Falls back to mock headlines if API unavailable

### 2. News Reading Service (`NewsReadingService.cs`)
- Background service runs every hour on the hour
- Generates TTS audio from news script
- Tracks 5-minute news playback window
- Returns "ifemi" (Ife Mi persona) as next OAP after news

### 3. News Controller (`NewsController.cs`)
- `GET /api/news/headlines` - Fetch current headlines (no auth)
- `GET /api/news/status` - Check if news playing, get audio URL, next OAP
- `POST /api/news/trigger` - Manual news generation (Admin only)
- `GET /api/news/script` - Preview formatted news script

### 4. Stream Controller Integration
- Checks `NewsReadingService.IsNewsCurrentlyPlaying()` before routing
- If news playing: Redirects to news audio URL
- If news just ended: Routes to Ife Mi (nextOapId = "ifemi")
- Otherwise: Routes to scheduled persona

## Configuration

### Required Environment Variables

```powershell
# Optional: NewsAPI.org key (has mock fallback)
$env:NEWS_API_KEY = "your-newsapi-org-key-here"

# TTS Service (choose one)
# Option 1: Azure Cognitive Services
$env:AZURE_SPEECH_KEY = "your-azure-speech-key"
$env:AZURE_SPEECH_REGION = "westus"

# Option 2: Google Cloud TTS
$env:GOOGLE_APPLICATION_CREDENTIALS = "path\to\service-account.json"

# Option 3: AWS Polly
$env:AWS_ACCESS_KEY_ID = "your-aws-access-key"
$env:AWS_SECRET_ACCESS_KEY = "your-aws-secret-key"
$env:AWS_REGION = "us-east-1"

# Option 4: ElevenLabs (best quality)
$env:ELEVENLABS_API_KEY = "your-elevenlabs-key"
$env:ELEVENLABS_VOICE_ID = "21m00Tcm4TlvDq8ikWAM" # Rachel voice
```

### Get API Keys

**NewsAPI.org** (Optional - has mock fallback):
1. Sign up at https://newsapi.org/
2. Free tier: 100 requests/day
3. Copy API key to `NEWS_API_KEY` env var

**Azure Speech** (Recommended - good quality, affordable):
1. Create Azure account at https://portal.azure.com
2. Create "Speech Service" resource
3. Copy key and region from Keys and Endpoint
4. Free tier: 5 hours/month

**ElevenLabs** (Best quality - premium):
1. Sign up at https://elevenlabs.io/
2. Get API key from Profile → API Keys
3. Choose voice from Voice Library (Rachel recommended)
4. Free tier: 10,000 characters/month

## TTS Integration Status

### Current Status: **PLACEHOLDER**

The `GenerateNewsAudioAsync` method in `NewsReadingService.cs` currently has a placeholder implementation:

```csharp
private async Task<string> GenerateNewsAudioAsync(string script, CancellationToken cancellationToken)
{
    // TODO: Integrate actual TTS service
    _logger.LogWarning("TTS service not configured. Using placeholder URL.");
    
    // When implemented:
    // 1. Call TTS service with script
    // 2. Save generated audio to S3: news/tosin-news-{timestamp}.mp3
    // 3. Return HLS URL: http://localhost:5000/api/admin/stream-hls/news/tosin-news-{timestamp}.mp3
    
    return "http://localhost:5000/api/fallback/stream"; // Temporary
}
```

### Implementation Steps

#### Option 1: Azure Speech (Recommended)

```csharp
// Add NuGet package: Microsoft.CognitoServices.Speech
using Microsoft.CognitiveServices.Speech;

private async Task<string> GenerateNewsAudioAsync(string script, CancellationToken cancellationToken)
{
    var apiKey = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
    var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
    
    if (string.IsNullOrEmpty(apiKey))
    {
        _logger.LogWarning("Azure Speech not configured. Using fallback.");
        return "http://localhost:5000/api/fallback/stream";
    }

    var config = SpeechConfig.FromSubscription(apiKey, region);
    config.SpeechSynthesisVoiceName = "en-NG-EzinneNeural"; // Nigerian female voice
    config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio16Khz32KBitRateMonoMp3);

    using var synthesizer = new SpeechSynthesizer(config, null);
    var result = await synthesizer.SpeakTextAsync(script);

    if (result.Reason == ResultReason.SynthesizingAudioCompleted)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
        var s3Key = $"news/tosin-news-{timestamp}.mp3";
        
        // Upload to S3 (inject IS3Service via constructor)
        await _s3Service.UploadFileAsync(s3Key, new MemoryStream(result.AudioData), "audio/mpeg");
        
        var hlsUrl = $"http://localhost:5000/api/admin/stream-hls/{s3Key}";
        _logger.LogInformation("Generated news audio: {Url}", hlsUrl);
        return hlsUrl;
    }
    else
    {
        _logger.LogError("TTS failed: {Reason}", result.Reason);
        return "http://localhost:5000/api/fallback/stream";
    }
}
```

#### Option 2: ElevenLabs (Best Quality)

```csharp
// Add NuGet package: Newtonsoft.Json or System.Text.Json
using System.Net.Http.Json;

private async Task<string> GenerateNewsAudioAsync(string script, CancellationToken cancellationToken)
{
    var apiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
    var voiceId = Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID") ?? "21m00Tcm4TlvDq8ikWAM";
    
    if (string.IsNullOrEmpty(apiKey))
    {
        _logger.LogWarning("ElevenLabs not configured. Using fallback.");
        return "http://localhost:5000/api/fallback/stream";
    }

    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("xi-api-key", apiKey);
    
    var requestBody = new
    {
        text = script,
        model_id = "eleven_monolingual_v1",
        voice_settings = new
        {
            stability = 0.5,
            similarity_boost = 0.75
        }
    };

    var response = await client.PostAsJsonAsync(
        $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}",
        requestBody,
        cancellationToken
    );

    if (response.IsSuccessStatusCode)
    {
        var audioBytes = await response.Content.ReadAsByteArrayAsync();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
        var s3Key = $"news/tosin-news-{timestamp}.mp3";
        
        await _s3Service.UploadFileAsync(s3Key, new MemoryStream(audioBytes), "audio/mpeg");
        
        var hlsUrl = $"http://localhost:5000/api/admin/stream-hls/{s3Key}";
        _logger.LogInformation("Generated news audio: {Url}", hlsUrl);
        return hlsUrl;
    }
    else
    {
        _logger.LogError("ElevenLabs TTS failed: {Status}", response.StatusCode);
        return "http://localhost:5000/api/fallback/stream";
    }
}
```

## Testing

### 1. Test News Fetching
```powershell
curl http://localhost:5000/api/news/headlines
```

### 2. Test News Script
```powershell
curl http://localhost:5000/api/news/script
```

### 3. Trigger News Manually (Admin)
```powershell
$token = "your-admin-jwt-token"
curl -X POST http://localhost:5000/api/news/trigger `
  -H "Authorization: Bearer $token"
```

### 4. Check News Status
```powershell
curl http://localhost:5000/api/news/status
```

Expected response:
```json
{
  "isNewsPlaying": true,
  "newsStartedAt": "2024-01-15T14:00:00Z",
  "newsEndsAt": "2024-01-15T14:05:00Z",
  "audioUrl": "http://localhost:5000/api/admin/stream-hls/news/tosin-news-202401151400.mp3",
  "nextOap": "ifemi"
}
```

### 5. Test Stream Routing
```powershell
# During news playback (redirects to news audio)
curl -I http://localhost:5000/api/stream/stream/user123

# After news ends (redirects to Ife Mi)
curl -I http://localhost:5000/api/stream/stream/user123
```

## Schedule

- **Every hour on the hour** (00:00, 01:00, 02:00, etc.)
- News plays for **5 minutes fixed**
- After news: Switch to **Ife Mi** (Love & Relationships OAP)

## Audio Quality

Generated news audio uses:
- **Format**: MP3
- **Bitrate**: 32 kbps (Azure) or adaptive (ElevenLabs)
- **Sample Rate**: 16 kHz
- **Delivery**: HLS streaming with M3U8 manifests
- **Caching**: 1 hour (same as music)

## Troubleshooting

### News not playing at scheduled time
- Check logs: `Background news reading service started`
- Verify server time is correct (uses UTC)
- Ensure `NewsReadingService` is registered as HostedService

### TTS placeholder URL returned
- Check TTS API key environment variable is set
- Verify TTS service credentials are valid
- Check logs for TTS error messages

### S3 upload fails
- Verify S3 credentials in environment
- Check bucket permissions (write access to `news/` prefix)
- Ensure S3Service is injected in NewsReadingService

### Stream not redirecting to news
- Check `GET /api/news/status` shows `isNewsPlaying: true`
- Verify `NewsReadingService` is injected in StreamController
- Check StreamController logs for routing decisions

## Next Steps

1. **Choose TTS provider** (Azure recommended for balance of quality/cost)
2. **Add API keys** to environment variables or appsettings.Development.json
3. **Implement TTS integration** in `GenerateNewsAudioAsync()`
4. **Inject IS3Service** into NewsReadingService constructor
5. **Test manual trigger** with `POST /api/news/trigger`
6. **Verify hourly schedule** by checking logs
7. **Monitor audio quality** and adjust TTS voice settings

## Cost Estimates

**NewsAPI.org**: Free (100 requests/day = 100 news reads)

**Azure Speech**: 
- Free tier: 5 hours/month
- Standard: $1 per hour
- Estimate: ~$0.08 per 5-minute news segment

**ElevenLabs**:
- Free tier: 10,000 characters/month (~20 news reads)
- Creator: $5/month (30,000 characters)
- Pro: $22/month (100,000 characters)
- Estimate: ~500 characters per news script

**S3 Storage**:
- ~1 MB per 5-minute MP3 (32 kbps)
- 24 news reads/day = ~24 MB/day = ~720 MB/month
- S3: $0.023 per GB = ~$0.02/month

## References

- NewsAPI.org: https://newsapi.org/docs
- Azure Speech: https://learn.microsoft.com/azure/cognitive-services/speech-service/
- ElevenLabs: https://docs.elevenlabs.io/api-reference/
- AWS Polly: https://docs.aws.amazon.com/polly/
- Google Cloud TTS: https://cloud.google.com/text-to-speech/docs
