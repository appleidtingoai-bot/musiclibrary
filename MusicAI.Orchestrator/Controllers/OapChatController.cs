using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicAI.Common.Models;
using MusicAI.Orchestrator.Services;
using MusicAI.Orchestrator.Data;
using MusicAI.Infrastructure.Services;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/oap")]
    public class OapChatController : ControllerBase
    {
        private readonly List<PersonaConfig> _personas;
        private readonly NewsReadingService _newsService;
        private readonly MusicRepository? _musicRepo;
        private readonly IS3Service _s3Service;
        private readonly ILogger<OapChatController> _logger;
        private readonly UsersRepository _usersRepo;

        public OapChatController(
            List<PersonaConfig> personas,
            NewsReadingService newsService,
            MusicRepository? musicRepo,
            IS3Service s3Service,
            ILogger<OapChatController> logger,
            UsersRepository usersRepo)
        {
            _personas = personas;
            _newsService = newsService;
            _musicRepo = musicRepo;
            _s3Service = s3Service;
            _logger = logger;
            _usersRepo = usersRepo;
        }

        /// <summary>
        /// Health check endpoint to verify server is responsive
        /// </summary>
        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "ok", time = DateTime.UtcNow, oaps = _personas.Count });
        }

        /// <summary>
        /// Unified chat endpoint for all OAPs - handles conversation, mood detection, and music recommendations
        /// </summary>
        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] OapChatRequest request)
        {
            if (string.IsNullOrEmpty(request.UserId) || string.IsNullOrEmpty(request.Message))
            {
                return BadRequest(new { error = "UserId and Message are required" });
            }

            // News feature disabled until TTS is configured
            // Check manually via _newsService.IsNewsCurrentlyPlaying() if needed
            
            // Get currently active OAP based on time
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow());
            if (activePersona == null)
            {
                activePersona = _personas.First(); // Fallback to first persona
            }

            _logger.LogInformation("Chat request for user {UserId}. Active OAP: {Oap}", request.UserId, activePersona.Name);

            // Analyze user's message for mood and intent
            var analysis = AnalyzeUserMessage(request.Message);
            
            // Get OAP personality context
            var oap = GetOapContext(activePersona.Id);

            // Generate conversational response based on OAP personality
            var responseText = GenerateOapResponse(oap, request.Message, analysis);

            // Get music recommendations based on OAP personality and user mood
            var playlist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, request.UserId);

            // Convert to HLS streaming URLs (public endpoints, no auth required)
            var playlistWithUrls = playlist.Select(track => new MusicTrackDto
            {
                Id = track.Id,
                Title = track.Title,
                Artist = track.Artist,
                S3Key = track.S3Key,
                HlsUrl = $"http://localhost:5000/api/music/hls/{track.S3Key}",
                StreamUrl = $"http://localhost:5000/api/music/stream/{track.S3Key}",
                Duration = track.DurationSeconds.HasValue ? $"{track.DurationSeconds / 60}:{track.DurationSeconds % 60:00}" : null,
                Genre = track.Genre
            }).ToList();

            return Ok(new OapChatResponse
            {
                OapName = oap.Name,
                OapPersonality = oap.Personality,
                ResponseText = responseText,
                MusicPlaylist = playlistWithUrls,
                CurrentAudioUrl = playlistWithUrls.FirstOrDefault()?.HlsUrl,
                DetectedMood = analysis.Mood,
                DetectedIntent = analysis.Intent,
                IsNewsSegment = false
            });
        }

        /// <summary>
        /// Get next tracks for auto-play based on current OAP and user preferences
        /// </summary>
        [HttpGet("next-tracks/{userId}")]
        public async Task<IActionResult> GetNextTracks(string userId, [FromQuery] string? currentTrackId = null, [FromQuery] int count = 5)
        {
            // Get currently active OAP
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow()) ?? _personas.First();
            
            var analysis = new MessageAnalysis { Mood = "neutral", Intent = "listen" };
            var playlist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, userId, count);

            var tracks = playlist.Select(track => new MusicTrackDto
            {
                Id = track.Id,
                Title = track.Title,
                Artist = track.Artist,
                S3Key = track.S3Key,
                HlsUrl = $"http://localhost:5000/api/music/hls/{track.S3Key}",
                StreamUrl = $"http://localhost:5000/api/music/stream/{track.S3Key}",
                Duration = track.DurationSeconds.HasValue ? $"{track.DurationSeconds / 60}:{track.DurationSeconds % 60:00}" : null,
                Genre = track.Genre
            }).ToList();

            return Ok(new { oap = activePersona.Name, tracks, isNews = false });
        }

        /// <summary>
        /// Get current active OAP information
        /// </summary>
        [HttpGet("current")]
        public IActionResult GetCurrentOap()
        {
            // Get currently active OAP based on UTC time
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow());
            if (activePersona == null)
            {
                return Ok(new { oap = "None", personality = "Fallback Mix", isNews = false });
            }

            var oap = GetOapContext(activePersona.Id);
            return Ok(new
            {
                oap = oap.Name,
                personality = oap.Personality,
                show = oap.ShowName,
                schedule = $"{activePersona.Start:hh\\:mm} - {activePersona.End:hh\\:mm} UTC",
                isNews = false
            });
        }

        #region Helper Methods

        private MessageAnalysis AnalyzeUserMessage(string message)
        {
            var lowerMessage = message.ToLowerInvariant();

            // Mood detection
            string mood = "neutral";
            if (ContainsAny(lowerMessage, "happy", "joy", "excited", "amazing", "love", "great"))
                mood = "happy";
            else if (ContainsAny(lowerMessage, "sad", "down", "depressed", "lonely", "miss"))
                mood = "sad";
            else if (ContainsAny(lowerMessage, "angry", "mad", "frustrated", "annoyed"))
                mood = "angry";
            else if (ContainsAny(lowerMessage, "calm", "relax", "chill", "peace", "zen"))
                mood = "calm";
            else if (ContainsAny(lowerMessage, "party", "dance", "club", "groove", "lit"))
                mood = "energetic";
            else if (ContainsAny(lowerMessage, "romantic", "love", "crush", "date", "heart"))
                mood = "romantic";

            // Intent detection
            string intent = "listen";
            if (ContainsAny(lowerMessage, "recommend", "suggest", "what should", "play", "want to hear"))
                intent = "recommendation";
            else if (ContainsAny(lowerMessage, "news", "headline", "update", "happening", "today"))
                intent = "news";
            else if (ContainsAny(lowerMessage, "talk", "chat", "tell me", "advice", "help"))
                intent = "conversation";
            else if (ContainsAny(lowerMessage, "search", "find", "looking for", "artist", "song"))
                intent = "search";

            return new MessageAnalysis { Mood = mood, Intent = intent };
        }

        private bool ContainsAny(string text, params string[] keywords)
        {
            return keywords.Any(k => text.Contains(k));
        }

        private OapContext GetOapContext(string personaId)
        {
            return personaId.ToLowerInvariant() switch
            {
                "tosin" => new OapContext
                {
                    Name = "Tosin",
                    Personality = "Professional News Anchor",
                    ShowName = "News on the Hour",
                    MusicPreferences = new[] { "instrumental", "background", "neutral" },
                    Greeting = "Good day, I'm Tosin with News on the Hour.",
                    Style = "Professional, authoritative, factual"
                },
                "ifemi" or "ife mi" => new OapContext
                {
                    Name = "Ife Mi",
                    Personality = "Romantic Love & Relationships Expert",
                    ShowName = "Love Lounge",
                    MusicPreferences = new[] { "rnb", "soul", "love songs", "slow jams", "romantic"},
                    Greeting = "Hey love, it's Ife Mi. Let's talk about matters of the heart.",
                    Style = "Warm, empathetic, romantic, understanding"
                },
                "mimi" => new OapContext
                {
                    Name = "Mimi",
                    Personality = "Trendy Youth Culture Expert",
                    ShowName = "Youth Plug",
                    MusicPreferences = new[] { "pop", "hip-hop", "trending", "viral", "gen-z" },
                    Greeting = "Yo! It's Mimi, your plug for the hottest vibes!",
                    Style = "Energetic, trendy, youthful, fun"
                },
                "rotimi" => new OapContext
                {
                    Name = "Rotimi",
                    Personality = "Street Culture & Afrobeats Specialist",
                    ShowName = "Street Vibes",
                    MusicPreferences = new[] { "afrobeats", "afropop", "street", "naija", "amapiano" },
                    Greeting = "Wetin dey happen! Na Rotimi, bringing you the street vibes!",
                    Style = "Street-smart, authentic, energetic, Nigerian"
                },
                "maka" => new OapContext
                {
                    Name = "Maka",
                    Personality = "Business & Success Motivator",
                    ShowName = "Money Talks",
                    MusicPreferences = new[] { "motivational", "jazz", "highlife", "sophisticated" },
                    Greeting = "Welcome to Money Talks. I'm Maka, let's discuss success.",
                    Style = "Professional, motivating, business-focused"
                },
                "roman" => new OapContext
                {
                    Name = "Roman",
                    Personality = "Cultural Fusion Curator - Blending Afropop, Amapiano, Reggae & World Music",
                    ShowName = "The Roman Show - 24/7 Global Grooves",
                    MusicPreferences = new[] { "afropop", "amapiano", "reggae", "world", "afrobeats", "dancehall", "international", "diverse", "eclectic", "fusion" },
                    Greeting = "Welcome to The Roman Show! I'm your host Roman, curator of global sounds. Let the cultural fusion begin!",
                    Style = "Cultured, sophisticated, worldly, eclectic fusion"
                },
                "dozy" => new OapContext
                {
                    Name = "Dozy",
                    Personality = "Morning Energy & Motivation Host",
                    ShowName = "Rise & Grind",
                    MusicPreferences = new[] { "upbeat", "motivational", "morning", "energetic", "positive" },
                    Greeting = "Good morning! It's Dozy, time to rise and grind!",
                    Style = "Energetic, motivating, positive, morning vibes"
                },
                _ => new OapContext
                {
                    Name = "Tosin",
                    Personality = "Professional Host",
                    ShowName = "Radio Mix",
                    MusicPreferences = new[] { "variety", "mixed" },
                    Greeting = "Welcome to Tingo Radio!",
                    Style = "Professional, friendly"
                }
            };
        }

        private string GenerateOapResponse(OapContext oap, string userMessage, MessageAnalysis analysis)
        {
            var lowerMessage = userMessage.ToLowerInvariant();

            // Handle greetings
            if (ContainsAny(lowerMessage, "hello", "hi", "hey", "good morning", "good evening"))
            {
                return $"{oap.Greeting} {GetMoodResponse(oap, analysis.Mood)}";
            }

            // Handle news intent
            if (analysis.Intent == "news")
            {
                return "For the latest news updates, tune in to News on the Hour with Tosin, every hour on the hour!";
            }

            // Handle music recommendation
            if (analysis.Intent == "recommendation")
            {
                return oap.Name switch
                {
                    "Ife Mi" => $"Based on your vibe, I've got some beautiful {analysis.Mood} songs that'll speak to your heart. Check out this playlist I curated for you.",
                    "Mimi" => $"Okay bet! I got you with the {analysis.Mood} vibes. This playlist is straight fire! 🔥",
                    "Rotimi" => $"E choke! I don arrange some {analysis.Mood} bangers for you. This playlist go make you dance!",
                    "Maka" => $"Excellent choice. Here's a curated selection that matches your {analysis.Mood} energy and drive.",
                    "Roman" => $"Wonderful! I've selected some {analysis.Mood} tracks from across the globe for your listening pleasure.",
                    "Dozy" => $"Let's gooo! I've got the perfect {analysis.Mood} playlist to keep your energy up!",
                    _ => $"Here's a great playlist that matches your {analysis.Mood} mood!"
                };
            }

            // Handle conversation
            if (analysis.Intent == "conversation")
            {
                return oap.Name switch
                {
                    "Ife Mi" => "I'm here to listen, love. Tell me what's on your heart. Meanwhile, let me play some soulful music for you.",
                    "Mimi" => "For real though? Say less! Let's vibe while I play some tracks for you.",
                    "Rotimi" => "I dey hear you! Make we reason together as you enjoy these vibes.",
                    "Maka" => "I appreciate you sharing. Let's discuss this further while enjoying some sophisticated sounds.",
                    "Roman" => "Fascinating perspective. Let's explore this together with some wonderful music.",
                    "Dozy" => "I feel you! Let's keep that energy going with some motivating tracks!",
                    _ => "I hear you! Let me play some music that matches your vibe."
                };
            }

            // Default response
            return $"Great to connect with you! I've prepared a special {analysis.Mood} playlist just for you.";
        }

        private string GetMoodResponse(OapContext oap, string mood)
        {
            return mood switch
            {
                "happy" => oap.Name == "Ife Mi" ? "Your energy is beautiful today!" : "Love the positive vibes!",
                "sad" => oap.Name == "Ife Mi" ? "I'm here for you, let me play something to lift your spirits." : "I got you. Let the music heal.",
                "energetic" => "Let's turn up the energy!",
                "romantic" => oap.Name == "Ife Mi" ? "Ah, matters of the heart! Let's set the mood." : "I see you're feeling romantic!",
                "calm" => "Perfect timing for some chill vibes.",
                _ => "How can I make your day better with music?"
            };
        }

        private async Task<List<MusicTrack>> GetPersonalizedPlaylist(string oapId, string mood, string intent, string userId, int count = 10)
        {
            var oap = GetOapContext(oapId);
            
            List<MusicTrack> allMusic = new List<MusicTrack>();
            
            // Try database first (if available)
            if (_musicRepo != null)
            {
                try
                {
                    allMusic = await _musicRepo.GetAllAsync(0, 200);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Database unavailable, falling back to S3 direct listing");
                }
            }

            // Fallback: List all music from S3 bucket if database is empty
            if (!allMusic.Any() && _s3Service != null)
            {
                try
                {
                    _logger.LogInformation("Fetching music files directly from S3 bucket");
                    var s3Files = await _s3Service.ListObjectsAsync("music/");
                    
                    allMusic = s3Files
                        .Where(key => key.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || 
                                     key.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                                     key.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                        .Select(key => new MusicTrack
                        {
                            Id = Guid.NewGuid().ToString(),
                            Title = Path.GetFileNameWithoutExtension(key.Split('/').Last()),
                            Artist = "Unknown",
                            Genre = ExtractGenreFromS3Key(key),
                            S3Key = key,
                            S3Bucket = "tingoradiobucket",
                            ContentType = "audio/mpeg",
                            IsActive = true
                        })
                        .ToList();
                    
                    _logger.LogInformation("Loaded {Count} tracks from S3", allMusic.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to list files from S3");
                    return new List<MusicTrack>();
                }
            }

            if (!allMusic.Any())
            {
                _logger.LogWarning("No music found in database or S3");
                return new List<MusicTrack>();
            }

            // PRODUCTION MODE: Roman plays ALL music from ALL folders (no filtering)
            // Auto-shuffle Spotify-style playlist with ALL available tracks
            if (oapId.ToLowerInvariant() == "roman")
            {
                var romanPlaylist = allMusic
                    .OrderBy(_ => Guid.NewGuid())
                    .Take(count)
                    .ToList();

                _logger.LogInformation("Roman generated full-library playlist: {Count} tracks from ALL folders", romanPlaylist.Count);
                return romanPlaylist;
            }

            // Filter by OAP music preferences (for other OAPs when enabled)
            var filtered = allMusic.Where(m =>
            {
                var genre = m.Genre?.ToLowerInvariant() ?? "";
                var title = m.Title?.ToLowerInvariant() ?? "";
                var artist = m.Artist?.ToLowerInvariant() ?? "";

                // Check if music matches OAP preferences
                return oap.MusicPreferences.Any(pref =>
                    genre.Contains(pref) ||
                    title.Contains(pref) ||
                    artist.Contains(pref)
                );
            }).ToList();

            // If filtered list is too small, add variety from full library
            if (filtered.Count < count)
            {
                var additional = allMusic
                    .Where(m => !filtered.Contains(m))
                    .OrderBy(_ => Guid.NewGuid())
                    .Take(count - filtered.Count);
                filtered.AddRange(additional);
            }

            // Shuffle and take requested count
            var playlist = filtered
                .OrderBy(_ => Guid.NewGuid())
                .Take(count)
                .ToList();

            _logger.LogInformation("Generated playlist for {Oap}: {Count} tracks (Mood: {Mood}, Intent: {Intent})",
                oap.Name, playlist.Count, mood, intent);

            return playlist;
        }

        private string ExtractGenreFromS3Key(string s3Key)
        {
            // S3 key format: music/genre/filename.mp3
            // Example: music/rnb/abc123_song.mp3 -> rnb
            var parts = s3Key.Split('/');
            if (parts.Length >= 2 && parts[0] == "music")
            {
                return parts[1]; // Return genre folder name
            }
            return "mixed";
        }

        #endregion

        #region DTOs

        public class OapChatRequest
        {
            public string UserId { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string? Language { get; set; } = "en";
        }

        public class OapChatResponse
        {
            public string OapName { get; set; } = string.Empty;
            public string OapPersonality { get; set; } = string.Empty;
            public string ResponseText { get; set; } = string.Empty;
            public List<MusicTrackDto> MusicPlaylist { get; set; } = new();
            public string? CurrentAudioUrl { get; set; }
            public string? DetectedMood { get; set; }
            public string? DetectedIntent { get; set; }
            public bool IsNewsSegment { get; set; }
        }

        public class MusicTrackDto
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public string S3Key { get; set; } = string.Empty;
            public string HlsUrl { get; set; } = string.Empty;
            public string StreamUrl { get; set; } = string.Empty;
            public string? Duration { get; set; }
            public string? Genre { get; set; }
        }

        private class MessageAnalysis
        {
            public string Mood { get; set; } = "neutral";
            public string Intent { get; set; } = "listen";
        }

        private class OapContext
        {
            public string Name { get; set; } = string.Empty;
            public string Personality { get; set; } = string.Empty;
            public string ShowName { get; set; } = string.Empty;
            public string[] MusicPreferences { get; set; } = Array.Empty<string>();
            public string Greeting { get; set; } = string.Empty;
            public string Style { get; set; } = string.Empty;
        }

        #endregion
    }
}
