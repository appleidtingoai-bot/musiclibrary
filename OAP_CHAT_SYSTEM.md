# Unified OAP Chat System - Complete Implementation

## ✅ What's Implemented

### Single Unified Endpoint for All OAPs
All OAPs (Ife Mi, Mimi, Rotimi, Maka, Roman, Dozy) now use ONE endpoint that:
- Automatically switches based on time of day
- Understands user mood and intent from their messages
- Returns personalized music playlists as HLS URLs
- Gets interrupted by Tosin's news (every hour)
- Responds in each OAP's unique personality

## API Endpoints

### 1. POST `/api/oap/chat` - Main Chat Endpoint
**Description**: Chat with the currently active OAP, get music recommendations

**Request**:
```json
{
  "userId": "user123",
  "message": "Hey, I'm feeling romantic tonight",
  "language": "en"
}
```

**Response**:
```json
{
  "oapName": "Ife Mi",
  "oapPersonality": "Romantic Love & Relationships Expert",
  "responseText": "Hey love, it's Ife Mi. Your energy is beautiful today! Based on your vibe, I've got some beautiful romantic songs that'll speak to your heart.",
  "musicPlaylist": [
    {
      "id": "track-123",
      "title": "Love Nwantiti",
      "artist": "CKay",
      "s3Key": "music/ckay-love-nwantiti.mp3",
      "hlsUrl": "http://localhost:5000/api/admin/stream-hls/music/ckay-love-nwantiti.mp3",
      "streamUrl": "http://localhost:5000/api/admin/stream-proxy/music/ckay-love-nwantiti.mp3",
      "duration": "3:45",
      "genre": "afrobeats"
    }
  ],
  "currentAudioUrl": "http://localhost:5000/api/admin/stream-hls/music/ckay-love-nwantiti.mp3",
  "detectedMood": "romantic",
  "detectedIntent": "recommendation",
  "isNewsSegment": false
}
```

### 2. GET `/api/oap/next-tracks/{userId}` - Auto-Play Queue
**Description**: Get next tracks for continuous playback

**Query Parameters**:
- `currentTrackId` (optional): Current playing track
- `count` (optional, default=5): Number of tracks to return

**Response**:
```json
{
  "oap": "Ife Mi",
  "tracks": [...],
  "isNews": false
}
```

### 3. GET `/api/oap/current` - Get Active OAP
**Description**: Check which OAP is currently active

**Response**:
```json
{
  "oap": "Ife Mi",
  "personality": "Romantic Love & Relationships Expert",
  "show": "Love Lounge",
  "schedule": "22:00 - 02:00 UTC",
  "isNews": false
}
```

## OAP Personalities & Music Preferences

### Ife Mi (Love Lounge) - 10PM-2AM UTC
- **Personality**: Romantic, warm, empathetic
- **Music**: R&B, soul, love songs, slow jams
- **Greeting**: "Hey love, it's Ife Mi. Let's talk about matters of the heart."
- **Style**: Understanding, romantic, emotional support

### Mimi (Youth Plug) - 4PM-8PM UTC
- **Personality**: Trendy, energetic, fun
- **Music**: Pop, hip-hop, trending, viral, Gen-Z hits
- **Greeting**: "Yo! It's Mimi, your plug for the hottest vibes!"
- **Style**: Youthful slang, high energy, relatable

### Rotimi (Street Vibes) - 12PM-4PM UTC
- **Personality**: Street-smart, authentic, Nigerian
- **Music**: Afrobeats, Afropop, street music, Amapiano
- **Greeting**: "Wetin dey happen! Na Rotimi, bringing you the street vibes!"
- **Style**: Pidgin English, authentic Nigerian, relatable

### Maka (Money Talks) - 8PM-10PM UTC
- **Personality**: Business-focused, motivating, professional
- **Music**: Motivational, jazz, highlife, sophisticated
- **Greeting**: "Welcome to Money Talks. I'm Maka, let's discuss success."
- **Style**: Professional, inspiring, wealth-focused

### Roman (The Roman Show) - 6PM-10PM UTC
- **Personality**: Cultured, sophisticated, worldly
- **Music**: International, world music, eclectic, diverse
- **Greeting**: "Greetings, I'm Roman. Let's explore music from around the world."
- **Style**: Intellectual, cultured, global perspective

### Dozy (Rise & Grind) - 6AM-10AM UTC
- **Personality**: Energetic, motivating, positive
- **Music**: Upbeat, motivational, morning vibes, energetic
- **Greeting**: "Good morning! It's Dozy, time to rise and grind!"
- **Style**: High energy, motivational, morning inspiration

### Tosin (News on the Hour) - Every Hour for 5 Minutes
- **Personality**: Professional, authoritative, factual
- **Music**: Instrumental, background, neutral
- **Greeting**: "Good day, I'm Tosin with News on the Hour."
- **Style**: Professional news anchor, interrupts all OAPs

## Mood Detection

The system automatically detects user mood from their messages:

- **Happy**: "happy", "joy", "excited", "amazing", "love", "great"
- **Sad**: "sad", "down", "depressed", "lonely", "miss"
- **Angry**: "angry", "mad", "frustrated", "annoyed"
- **Calm**: "calm", "relax", "chill", "peace", "zen"
- **Energetic**: "party", "dance", "club", "groove", "lit"
- **Romantic**: "romantic", "love", "crush", "date", "heart"

## Intent Detection

The system understands what users want:

- **Recommendation**: "recommend", "suggest", "what should", "play", "want to hear"
- **News**: "news", "headline", "update", "happening", "today"
- **Conversation**: "talk", "chat", "tell me", "advice", "help"
- **Search**: "search", "find", "looking for", "artist", "song"
- **Listen**: Default - just wants to listen to music

## How It Works

### 1. Time-Based OAP Switching
```
00:00-06:00: Tosin (News interrupts every hour for 5 minutes)
06:00-10:00: Dozy (Rise & Grind)
10:00-12:00: Fallback Mix
12:00-16:00: Rotimi (Street Vibes)
16:00-20:00: Mimi (Youth Plug)
20:00-22:00: Maka (Money Talks)
22:00-02:00: Ife Mi (Love Lounge)
```

### 2. News Interruption
- Every hour on the hour, Tosin takes over for 5 minutes
- All chat requests during news return Tosin's news segment
- After 5 minutes, automatically switches back to scheduled OAP
- After Tosin's news: switches to Ife Mi (Love Lounge)

### 3. Music Playlist Generation
1. User sends chat message
2. System analyzes mood and intent
3. Gets currently active OAP based on time
4. Filters music library by OAP's preferences
5. Returns personalized playlist with HLS URLs
6. Browser can play directly using HTML5 audio

### 4. Personality-Based Responses
Each OAP responds differently to the same message:

**User**: "I need music recommendations"

**Ife Mi**: "Based on your vibe, I've got some beautiful songs that'll speak to your heart."

**Mimi**: "Okay bet! I got you with the vibes. This playlist is straight fire! 🔥"

**Rotimi**: "E choke! I don arrange some bangers for you. This playlist go make you dance!"

**Maka**: "Excellent choice. Here's a curated selection that matches your energy and drive."

**Roman**: "Wonderful! I've selected some tracks from across the globe for your listening pleasure."

**Dozy**: "Let's gooo! I've got the perfect playlist to keep your energy up!"

## Testing Examples

### Test 1: Chat with Ife Mi (Love OAP)
```powershell
$body = @{
    userId = "test-user-123"
    message = "I miss my ex, feeling lonely tonight"
    language = "en"
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5000/api/oap/chat" `
  -Method Post `
  -Body $body `
  -ContentType "application/json"
```

**Expected**: Romantic response from Ife Mi with love songs

### Test 2: Chat with Mimi (Youth OAP)
```powershell
$body = @{
    userId = "test-user-456"
    message = "Yo! What's the hottest track right now?"
    language = "en"
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5000/api/oap/chat" `
  -Method Post `
  -Body $body `
  -ContentType "application/json"
```

**Expected**: Trendy youth response with pop/hip-hop tracks

### Test 3: Chat with Rotimi (Street OAP)
```powershell
$body = @{
    userId = "test-user-789"
    message = "Abeg play some naija vibes"
    language = "en"
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5000/api/oap/chat" `
  -Method Post `
  -Body $body `
  -ContentType "application/json"
```

**Expected**: Street-style response with Afrobeats/Amapiano

### Test 4: Get Current OAP
```powershell
Invoke-RestMethod -Uri "http://localhost:5000/api/oap/current"
```

**Expected**: Shows which OAP is currently active based on time

### Test 5: Get Next Tracks (Auto-Play)
```powershell
Invoke-RestMethod -Uri "http://localhost:5000/api/oap/next-tracks/test-user-123?count=5"
```

**Expected**: 5 tracks from current OAP's music preferences

## Integration with Frontend

### HTML5 Audio Player Example
```html
<div id="oap-player">
  <h2 id="oap-name">Loading...</h2>
  <p id="oap-response"></p>
  
  <audio id="audio-player" controls>
    <source src="" type="audio/mpeg">
  </audio>
  
  <div id="playlist"></div>
  
  <input type="text" id="chat-input" placeholder="Chat with the OAP...">
  <button onclick="sendMessage()">Send</button>
</div>

<script>
async function sendMessage() {
  const message = document.getElementById('chat-input').value;
  const response = await fetch('http://localhost:5000/api/oap/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      userId: 'user-123',
      message: message,
      language: 'en'
    })
  });
  
  const data = await response.json();
  
  // Update UI
  document.getElementById('oap-name').textContent = data.oapName;
  document.getElementById('oap-response').textContent = data.responseText;
  
  // Play first track
  if (data.currentAudioUrl) {
    const audio = document.getElementById('audio-player');
    audio.src = data.currentAudioUrl;
    audio.play();
  }
  
  // Show playlist
  const playlist = document.getElementById('playlist');
  playlist.innerHTML = data.musicPlaylist.map(track => 
    `<div onclick="playTrack('${track.hlsUrl}')">
      ${track.title} - ${track.artist}
    </div>`
  ).join('');
}

function playTrack(url) {
  const audio = document.getElementById('audio-player');
  audio.src = url;
  audio.play();
}

// Auto-play next track when current ends
document.getElementById('audio-player').addEventListener('ended', async () => {
  const response = await fetch('http://localhost:5000/api/oap/next-tracks/user-123?count=1');
  const data = await response.json();
  
  if (data.tracks.length > 0) {
    playTrack(data.tracks[0].hlsUrl);
  }
});

// Poll current OAP every 5 minutes
setInterval(async () => {
  const response = await fetch('http://localhost:5000/api/oap/current');
  const data = await response.json();
  document.getElementById('oap-name').textContent = data.oap;
}, 300000);
</script>
```

## Key Features

✅ **Single Unified Endpoint** - One endpoint handles all OAPs
✅ **Automatic OAP Switching** - Changes based on time of day
✅ **News Interruption** - Tosin's news takes priority every hour
✅ **Mood Detection** - Understands user's emotional state
✅ **Intent Recognition** - Knows what user wants (recommendation, conversation, etc.)
✅ **Personality-Based Responses** - Each OAP has unique voice
✅ **Music Filtering** - Returns tracks matching OAP preferences
✅ **HLS Streaming** - All music URLs are browser-ready
✅ **Auto-Play Queue** - Continuous playback with next-tracks endpoint
✅ **Context Awareness** - OAPs stay within their personality/scope

## System Flow

```
User sends message
      ↓
Check if news playing? → YES → Return Tosin with news audio
      ↓ NO
Get current OAP based on time
      ↓
Analyze message (mood + intent)
      ↓
Filter music library by OAP preferences
      ↓
Generate personality-based response
      ↓
Return playlist with HLS URLs
      ↓
Browser plays music
```

## Benefits

1. **One Integration Point**: Frontend only needs to hit one endpoint
2. **Automatic Switching**: No manual OAP selection needed
3. **Personalized Experience**: Each OAP curates music to their style
4. **Real Radio Feel**: News interrupts, OAPs switch on schedule
5. **Spotify-Level Quality**: HLS streaming, auto-play, queue management
6. **Scalable**: Easy to add new OAPs with different personalities

## Next Steps (Optional Enhancements)

1. **ML-Based Mood Detection**: Use sentiment analysis API
2. **User Preference Learning**: Track what users like per OAP
3. **Cross-Fading**: Smooth transitions between tracks
4. **Live Chat**: Real-time WebSocket for instant responses
5. **Voice Messages**: Users can send voice to OAPs
6. **OAP Voice Synthesis**: Generate TTS responses in each OAP's voice
7. **Collaborative Playlists**: Users contribute to OAP playlists
8. **Social Features**: Share what you're listening to

The system is production-ready for all OAPs except Tosin (needs TTS integration for news audio).
