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
        private readonly IPlaylistsRepository? _playlistsRepo;
        private readonly IS3Service _s3Service;
        private readonly ILogger<OapChatController> _logger;
        private readonly UsersRepository _usersRepo;
        private readonly IStreamTokenService _tokenService;
        private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache? _cache;
        private readonly SimpleCircuitBreaker? _breaker;
        private readonly BackgroundSignerService _bgSigner;

        public OapChatController(
            List<PersonaConfig> personas,
            NewsReadingService newsService,
            IS3Service s3Service,
            ILogger<OapChatController> logger,
            UsersRepository usersRepo,
            IStreamTokenService tokenService,
            Microsoft.Extensions.Caching.Distributed.IDistributedCache? cache,
            SimpleCircuitBreaker? breaker,
            BackgroundSignerService bgSigner,
            MusicRepository? musicRepo = null,
            IPlaylistsRepository? playlistsRepo = null)
        {
            _personas = personas ?? new List<PersonaConfig>();
            _newsService = newsService;
            _musicRepo = musicRepo;
            _playlistsRepo = playlistsRepo;
            _s3Service = s3Service;
            _logger = logger;
            _usersRepo = usersRepo;
            _tokenService = tokenService;
            _cache = cache;
            _breaker = breaker;
            _bgSigner = bgSigner;
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
            // If `userId` actually refers to a playlist id (playlist_tracks present), return that playlist
            var playlist = new List<MusicTrack>();
            try
            {
                if (_playlistsRepo != null)
                {
                    var pts = await _playlistsRepo.GetPlaylistTracksAsync(userId);
                    if (pts != null && pts.Count > 0)
                    {
                        // Convert PlaylistTrackDto -> MusicTrack for downstream processing
                        playlist = pts.Select(p => new MusicTrack
                        {
                            Id = p.Id,
                            Title = p.Title,
                            Artist = p.Artist,
                            S3Key = p.S3Key ?? string.Empty,
                            UploadedAt = p.AddedAt,
                            IsActive = true
                        }).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load playlist tracks for id {PlaylistId}", userId);
            }

            if (playlist.Count == 0)
            {
                playlist = await GetPersonalizedPlaylist(activePersona.Id, "neutral", "listen", userId, count, offset);
            }

            // Enrich playlist entries that are missing S3Key by attempting to look them up
            // in the canonical music repository (uploaded tracks metadata).
            if (_musicRepo != null)
            {
                var enriched = new List<MusicTrack>();
                foreach (var t in playlist)
                {
                    if (string.IsNullOrEmpty(t.S3Key))
                    {
                        try
                        {
                            // Try exact id lookup first
                            if (!string.IsNullOrEmpty(t.Id))
                            {
                                var byId = await _musicRepo.GetByIdAsync(t.Id);
                                if (byId != null && !string.IsNullOrEmpty(byId.S3Key))
                                {
                                    t.S3Key = byId.S3Key;
                                }
                            }

                            // Fallback: search by artist/title similarity
                            if (string.IsNullOrEmpty(t.S3Key))
                            {
                                var candidates = await _musicRepo.SearchAsync(null, null, t.Artist, 200);
                                var match = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.Title) && c.Title.Equals(t.Title, StringComparison.OrdinalIgnoreCase));
                                if (match != null && !string.IsNullOrEmpty(match.S3Key))
                                {
                                    t.S3Key = match.S3Key;
                                }
                                else
                                {
                                    // broader title-based search
                                    var byTitle = (await _musicRepo.GetAllAsync(0, 500)).FirstOrDefault(c => !string.IsNullOrEmpty(c.Title) && c.Title.Equals(t.Title, StringComparison.OrdinalIgnoreCase));
                                    if (byTitle != null && !string.IsNullOrEmpty(byTitle.S3Key)) t.S3Key = byTitle.S3Key;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"[OAP ENRICH] Failed to enrich S3Key for track id={t.Id} title={t.Title}");
                        }
                        // If still empty, attempt to locate object in S3 by matching filename patterns
                        if (string.IsNullOrEmpty(t.S3Key) && _s3Service != null)
                        {
                            try
                            {
                                dynamic s3dyn = _s3Service;
                                // try listing under music/ prefix if implementation supports ListObjectsAsync
                                var prefix = "music/";
                                IEnumerable<string>? keys = null;
                                try
                                {
                                    var obj = await s3dyn.ListObjectsAsync(prefix);
                                    if (obj is IEnumerable<string> ks) keys = ks;
                                    else if (obj is IEnumerable<object> ko) keys = ko.Select(o => o?.ToString() ?? string.Empty);
                                }
                                catch { /* listing not supported or failed */ }

                                if (keys != null)
                                {
                                    // Build filename candidate from title
                                    var titleCandidate = (t.Title ?? string.Empty).Replace(' ', '-').Replace(' ', '_').Replace("/", "-").ToLowerInvariant();
                                    var exts = new[] { ".mp3", ".m4a", ".wav", ".flac", ".aac", ".ogg" };
                                    foreach (var k in keys)
                                    {
                                        if (string.IsNullOrEmpty(k)) continue;
                                        var lower = k.ToLowerInvariant();
                                        if (lower.Contains(titleCandidate) || exts.Any(e => lower.EndsWith(e) && lower.Contains(titleCandidate)))
                                        {
                                            t.S3Key = k.TrimStart('/');
                                            _logger.LogInformation($"[OAP ENRICH] Matched S3 object for title={t.Title} -> key={t.S3Key}");
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex2)
                            {
                                _logger.LogDebug(ex2, "S3 listing fallback failed during enrichment");
                            }
                        }
                    }
                    enriched.Add(t);
                }
                playlist = enriched;
            }
            var format = Request.Query.ContainsKey("format") ? Request.Query["format"].ToString() : string.Empty;
            if (manifestOnly || string.Equals(format, "m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var combined = playlist;
                if (!GetAllowExplicitFromClaims()) combined = combined.Where(t => !IsProbablyExplicit(t)).ToList();
                var manifest = await BuildCombinedManifestAsync(combined);
                Response.Headers["Content-Disposition"] = "inline";
                return Content(manifest, "application/vnd.apple.mpegurl");
            }
            // Presign first track to improve startup latency (non-blocking fallback)
            string? firstPresigned = null;
            try
            {
                var first = playlist.FirstOrDefault(p => !string.IsNullOrEmpty(p.S3Key));
                if (first != null)
                {
                    var pres = await _bgSigner.EnqueueAsync(new MusicAI.Orchestrator.Services.BackgroundSignerService.PresignRequest { Key = first.S3Key, ExpiryMinutes = 60 });
                    if (!string.IsNullOrEmpty(pres.Url)) firstPresigned = pres.Url;
                }
            }
            catch { }

            var tracks = playlist.Select(t => new { id = t.Id, title = t.Title, artist = t.Artist, s3Key = t.S3Key, duration = t.DurationSeconds, hlsUrl = (t == playlist.FirstOrDefault() && !string.IsNullOrEmpty(firstPresigned)) ? firstPresigned : GetCdnOrOriginUrl(t.S3Key, string.Empty) }).ToList();
            // Log missing S3Key for debugging
            foreach (var t in playlist) {
                if (string.IsNullOrEmpty(t.S3Key)) {
                    _logger.LogWarning($"[OAP PLAYLIST] Track missing S3Key: id={t.Id} title={t.Title} artist={t.Artist}");
                }
            }
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
                if (_musicRepo != null)
                {
                    // If a userId is provided, prefer that user's uploaded tracks so
                    // playlists reflect what the user previously uploaded.
                    if (!string.IsNullOrEmpty(userId))
                    {
                        try
                        {
                            var byUploader = await _musicRepo.GetByUploaderAsync(userId, offset, Math.Max(1, count));
                            if (byUploader != null && byUploader.Count > 0) return byUploader;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load uploads for user {UserId}", userId);
                        }
                    }

                    return await _musicRepo.GetAllAsync(offset, Math.Max(1, count));
                }
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
