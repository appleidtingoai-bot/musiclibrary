using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using MusicAI.Common.Models;
using MusicAI.Orchestrator.Services;
using MusicAI.Orchestrator.Data;
using MusicAI.Infrastructure.Services;

// (Removed duplicate OapChatController definition to resolve ambiguity)
namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/oap")]
    [Authorize]
    public class OapChatController : ControllerBase
    {
        private readonly List<PersonaConfig> _personas;
        private readonly NewsReadingService _newsService;
        private readonly MusicRepository? _musicRepo;
        private readonly IS3Service _s3Service;
        private readonly ILogger<OapChatController> _logger;
        private readonly UsersRepository _usersRepo;
        private readonly IStreamTokenService _tokenService;
        private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache? _cache;
        private readonly SimpleCircuitBreaker? _breaker;

        public OapChatController(
            List<PersonaConfig> personas,
            NewsReadingService newsService,
            IS3Service s3Service,
            ILogger<OapChatController> logger,
            UsersRepository usersRepo,
            IStreamTokenService tokenService,
            Microsoft.Extensions.Caching.Distributed.IDistributedCache? cache,
            SimpleCircuitBreaker? breaker,
            MusicRepository? musicRepo = null)
        {
            _personas = personas ?? new List<PersonaConfig>();
            _newsService = newsService;
            _musicRepo = musicRepo;
            _s3Service = s3Service;
            _logger = logger;
            _usersRepo = usersRepo;
            _tokenService = tokenService;
            _cache = cache;
            _breaker = breaker;
        }

        [AllowAnonymous]
        [HttpGet("health")]
        public IActionResult Health() => Ok(new { status = "ok", time = DateTime.UtcNow, oaps = _personas?.Count ?? 0 });

        private bool GetAllowExplicitFromClaims()
        {
            try
            {
                var c = User?.FindFirst("allow_explicit")?.Value;
                if (!string.IsNullOrEmpty(c) && (c == "1" || c.Equals("true", StringComparison.OrdinalIgnoreCase))) return true;
            }
            catch { }
            return false;
        }

        private string GetCdnOrOriginUrl(string? s3Key, string encoded = "")
        {
            if (string.IsNullOrEmpty(s3Key)) return string.Empty;
            var key = s3Key!;
            var cdn = Environment.GetEnvironmentVariable("CDN_DOMAIN")?.TrimEnd('/');
            if (!string.IsNullOrEmpty(cdn)) return $"https://{cdn}/media/{key.TrimStart('/')}";
            var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
            if (!string.IsNullOrEmpty(cfDomain)) return $"https://{cfDomain}/{key.TrimStart('/')}";
            return $"{Request.Scheme}://{Request.Host}/api/music/hls/{Uri.EscapeDataString(key)}{encoded}";
        }

        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] OapChatRequest request)
        {
            if (string.IsNullOrEmpty(request.UserId) || string.IsNullOrEmpty(request.Message)) return BadRequest(new { error = "UserId and Message are required" });
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow()) ?? _personas.FirstOrDefault();
            if (activePersona == null) return Ok(new { message = "No OAPs configured" });
            var analysis = AnalyzeUserMessage(request.Message);
            var requestedCount = request.All ? 0 : Math.Max(1, request.Count);
            var playlist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, request.UserId, requestedCount);

            var presignExpiry = TimeSpan.FromMinutes(60);
            var expiryEnv = Environment.GetEnvironmentVariable("MUSIC_PRESIGN_EXPIRY_MINUTES");
            if (!string.IsNullOrEmpty(expiryEnv) && int.TryParse(expiryEnv, out var ev)) presignExpiry = TimeSpan.FromMinutes(ev);

            var playlistWithUrls = new List<MusicTrackDto>();
            foreach (var track in playlist)
            {
                var allowExplicit = GetAllowExplicitFromClaims();
                var selectedKey = (!allowExplicit && track.HasCleanVariant && !string.IsNullOrEmpty(track.CleanS3Key)) ? (track.CleanS3Key ?? track.S3Key) : track.S3Key;
                selectedKey ??= string.Empty;
                var token = SafeGenerateToken(selectedKey, TimeSpan.FromHours(24), allowExplicit);
                var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";
                var hls = GetCdnOrOriginUrl(selectedKey, encoded);
                string stream = string.Empty;
                if (_s3Service != null)
                {
                    try
                    {
                        var pres = await _s3Service.GetPresignedUrlAsync(selectedKey, presignExpiry);
                        if (!string.IsNullOrEmpty(pres)) stream = pres;
                    }
                    catch { }
                }

                string? durationStr = null;
                if (track.DurationSeconds.HasValue)
                {
                    var ds = track.DurationSeconds.Value;
                    durationStr = $"{ds / 60}:{ds % 60:00}";
                }

                playlistWithUrls.Add(new MusicTrackDto
                {
                    Id = track.Id,
                    Title = track.Title,
                    Artist = track.Artist,
                    S3Key = selectedKey,
                    HlsUrl = hls,
                    StreamUrl = stream,
                    Duration = durationStr,
                    Genre = track.Genre
                });
            }

            var responseObj = new OapChatResponse { OapName = activePersona.Name, OapPersonality = activePersona.Name ?? string.Empty, ResponseText = GenerateOapResponse(activePersona, request.Message, analysis), MusicPlaylist = playlistWithUrls, CurrentAudioUrl = playlistWithUrls.FirstOrDefault()?.HlsUrl, DetectedMood = analysis.Mood, DetectedIntent = analysis.Intent, IsNewsSegment = false, BufferConfig = new BufferConfigDto { MinBufferSeconds = 3, MaxBufferSeconds = 30, PrefetchTracks = 2, ResumeBufferSeconds = 5 } };
            responseObj.ContinuousHlsUrl = $"{Request.Scheme}://{Request.Host}/api/oap/playlist/{request.UserId}?count={requestedCount}&offset={request.Offset}&format=m3u8";
            return Ok(responseObj);
        }

        [HttpGet("playlist/{userId}")]
        public async Task<IActionResult> GetContinuousPlaylist(string userId, [FromQuery] int count = 5, [FromQuery] bool light = false, [FromQuery] int offset = 0, [FromQuery] bool manifestOnly = false)
        {
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow()) ?? _personas.FirstOrDefault();
            if (activePersona == null) return Ok(new { oap = "None", tracks = new List<object>() });
            var playlist = await GetPersonalizedPlaylist(activePersona.Id, "neutral", "listen", userId, count, offset);
            var format = Request.Query.ContainsKey("format") ? Request.Query["format"].ToString() : string.Empty;
            if (manifestOnly || string.Equals(format, "m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var combined = playlist;
                if (!GetAllowExplicitFromClaims()) combined = combined.Where(t => !IsProbablyExplicit(t)).ToList();
                var manifest = await BuildCombinedManifestAsync(combined);
                Response.Headers["Content-Disposition"] = "inline";
                return Content(manifest, "application/vnd.apple.mpegurl");
            }
            var tracks = playlist.Select(t => new { id = t.Id, title = t.Title, artist = t.Artist, s3Key = t.S3Key, duration = t.DurationSeconds, hlsUrl = GetCdnOrOriginUrl(t.S3Key, string.Empty) }).ToList();
            return Ok(new { oap = activePersona.Name, tracks, isNews = false });
        }

        [AllowAnonymous]
        [HttpGet("track-url")]
        public async Task<IActionResult> GetTrackUrl([FromQuery] string s3Key, [FromQuery] int ttlMinutes = 5)
        {
            if (string.IsNullOrEmpty(s3Key)) return BadRequest(new { error = "s3Key is required" });
            string key = s3Key!;
            var token = SafeGenerateToken(key, TimeSpan.FromMinutes(ttlMinutes), GetAllowExplicitFromClaims());
            var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";
            var hlsUrl = GetCdnOrOriginUrl(key, encoded);
            string streamUrl = hlsUrl;
            if (_s3Service != null) { try { var pres = await _s3Service.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(ttlMinutes)); if (!string.IsNullOrEmpty(pres)) streamUrl = pres; } catch { } }
            return Ok(new { hlsUrl, streamUrl, expiresIn = ttlMinutes * 60 });
        }

        [HttpGet("current")]
        public IActionResult GetCurrentOap()
        {
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow());
            if (activePersona == null) return Ok(new { oap = "None", personality = "Fallback Mix", isNews = false });
            var oap = new OapContext { Name = activePersona!.Name, Personality = activePersona.Name ?? string.Empty, ShowName = activePersona.Name };
            return Ok(new { oap = oap.Name, personality = oap.Personality, show = oap.ShowName, isNews = false });
        }

        private string GenerateOapResponse(PersonaConfig oap, string message, MessageAnalysis analysis) => $"{oap.Name} says: Thanks for your message! I'll play something for your {analysis.Mood} mood.";

        private MessageAnalysis AnalyzeUserMessage(string message)
        {
            var m = new MessageAnalysis();
            if (string.IsNullOrWhiteSpace(message)) return m;
            var lower = message.ToLowerInvariant();
            if (lower.Contains("happy") || lower.Contains("cheer")) m.Mood = "happy";
            if (lower.Contains("sad") || lower.Contains("down")) m.Mood = "sad";
            if (lower.Contains("dance") || lower.Contains("party")) m.Intent = "dance";
            return m;
        }

        private bool IsProbablyExplicit(MusicTrack t)
        {
            if (t == null) return false;
            var s = (t.Title ?? string.Empty) + " " + (t.S3Key ?? string.Empty);
            s = s.ToLowerInvariant();
            if (s.Contains("explicit") || s.Contains("nsfw") || s.Contains("(explicit)")) return true;
            var profanity = new[] { "fuck", "shit", "bitch", "asshole", "nigga" };
            foreach (var p in profanity) if (s.Contains(p)) return true;
            return false;
        }

        private async Task<List<MusicTrack>> GetPersonalizedPlaylist(string oapId, string mood, string intent, string userId, int count = 10, int offset = 0)
        {
            try
            {
                if (_musicRepo != null) return await _musicRepo.GetAllAsync(offset, Math.Max(1, count));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to get personalized playlist"); }
            return new List<MusicTrack>();
        }

        private Task<string> BuildCombinedManifestAsync(List<MusicTrack> combinedPlaylist)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#EXTM3U");
            sb.AppendLine("#EXT-X-VERSION:3");
            sb.AppendLine("#EXT-X-TARGETDURATION:10");
            sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
            sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
            foreach (var track in combinedPlaylist)
            {
                var baseKey = track.S3Key ?? string.Empty;
                if (string.IsNullOrEmpty(baseKey)) continue;
                var dur = track.DurationSeconds ?? 10;
                sb.AppendLine($"#EXTINF:{dur:F1},{(track.Title ?? string.Empty).Replace('\n', ' ')}");
                var token = SafeGenerateToken(baseKey, TimeSpan.FromHours(1), GetAllowExplicitFromClaims());
                var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";
                sb.AppendLine(GetCdnOrOriginUrl(baseKey, encoded));
            }
            sb.AppendLine("#EXT-X-ENDLIST");
            return Task.FromResult(sb.ToString());
        }

        private string SafeGenerateToken(string key, TimeSpan ttl, bool allowExplicit)
        {
            try { return _tokenService?.GenerateToken(key ?? string.Empty, ttl, allowExplicit) ?? string.Empty; } catch { return string.Empty; }
        }

        // DTOs moved to Controllers/OapDtos.cs to keep this controller focused
    }
}
