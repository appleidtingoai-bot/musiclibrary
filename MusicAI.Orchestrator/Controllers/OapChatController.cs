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

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/oap")]
    [Authorize] // Require authentication for all music/playlist endpoints
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
        private readonly MusicAI.Infrastructure.Services.SimpleCircuitBreaker? _breaker;

        // Read allow_explicit flag from authenticated user's claims if present
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

        public OapChatController(
            List<PersonaConfig> personas,
            NewsReadingService newsService,
            IS3Service s3Service,
            ILogger<OapChatController> logger,
            UsersRepository usersRepo,
            IStreamTokenService tokenService,
            Microsoft.Extensions.Caching.Distributed.IDistributedCache? cache,
            MusicAI.Infrastructure.Services.SimpleCircuitBreaker? breaker,
            MusicRepository? musicRepo = null)
        {
            _personas = personas;
            _newsService = newsService;
            _musicRepo = musicRepo;
            _s3Service = s3Service;
            _logger = logger;
            _usersRepo = usersRepo;
            _tokenService = tokenService;
            _cache = cache;
            _breaker = breaker;
        }

        // Helper: prefer a generic CDN domain if provided, else fall back to CloudFront if configured, else origin controller URL
        private bool IsCdnAvailable()
        {
            var cdn = Environment.GetEnvironmentVariable("CDN_DOMAIN");
            return !string.IsNullOrEmpty(cdn) || MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured;
        }

        private string GetCdnOrOriginUrl(string? s3Key, string encoded = "")
        {
            if (string.IsNullOrEmpty(s3Key)) return string.Empty;
            var key = s3Key!;
            var cdn = Environment.GetEnvironmentVariable("CDN_DOMAIN")?.TrimEnd('/');
            if (!string.IsNullOrEmpty(cdn))
            {
                return $"https://{cdn}/{key.TrimStart('/')}";
            }

            if (MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured)
            {
                var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                if (!string.IsNullOrEmpty(cfDomain))
                {
                    return $"https://{cfDomain}/{key.TrimStart('/')}";
                }
            }

            // Fallback to controller-origin HLS endpoint (include encoded query if provided)
            return $"{Request.Scheme}://{Request.Host}/api/music/hls/{Uri.EscapeDataString(key)}{encoded}";
        }

        /// <summary>
        /// Health check endpoint to verify server is responsive
        /// </summary>
        [HttpGet("health")]
        [AllowAnonymous]
        public IActionResult Health()
        {
            return Ok(new { status = "ok", time = DateTime.UtcNow, oaps = _personas?.Count ?? 0 });
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

            // Determine requested playlist size (support returning entire library when All==true)
            var requestedCount = request.All ? 0 : Math.Max(1, request.Count);

            // Get music recommendations based on OAP personality and user mood
            var playlist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, request.UserId, requestedCount);

            // Convert to HLS streaming URLs (public endpoints, no auth required)
            // Use long-lived tokens for HLS so the frontend can prefetch and buffer across sessions.
            var playlistWithUrls = playlist.Select(track =>
            {
                var allowExplicit = GetAllowExplicitFromClaims();
                var token = _tokenService?.GenerateToken(track.S3Key ?? string.Empty, TimeSpan.FromHours(24), allowExplicit) ?? string.Empty;
                var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";

                var hls = GetCdnOrOriginUrl(track.S3Key, encoded);

                return new MusicTrackDto
                {
                    Id = track.Id,
                    Title = track.Title,
                    Artist = request.Light ? string.Empty : track.Artist,
                    S3Key = track.S3Key ?? string.Empty,
                    HlsUrl = hls,
                    // Do not provide progressive stream URLs by default — frontend should use HLS variants only.
                    StreamUrl = string.Empty,
                    Duration = track.DurationSeconds.HasValue ? $"{track.DurationSeconds / 60}:{track.DurationSeconds % 60:00}" : null,
                    Genre = track.Genre
                };
            }).ToList();

            // If PlayNowS3Key is provided, ensure that track is first and set CurrentAudioUrl accordingly
            string? currentAudio = playlistWithUrls.FirstOrDefault()?.HlsUrl;
            if (!string.IsNullOrEmpty(request.PlayNowS3Key))
            {
                var found = playlistWithUrls.FirstOrDefault(t => string.Equals(t.S3Key, request.PlayNowS3Key, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    // Move to front
                    playlistWithUrls.Remove(found);
                    playlistWithUrls.Insert(0, found);
                    currentAudio = found.HlsUrl;
                }
                else
                {
                    // Not present in generated playlist: create a lightweight DTO for the requested S3 key
                    var allowExplicit = GetAllowExplicitFromClaims();
                    var token = _tokenService?.GenerateToken(request.PlayNowS3Key ?? string.Empty, TimeSpan.FromHours(24), allowExplicit) ?? string.Empty;
                    var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";
                    string hlsUrl;
                    try
                    {
                        hlsUrl = GetCdnOrOriginUrl(request.PlayNowS3Key, encoded);
                    }
                    catch
                    {
                        hlsUrl = $"{Request.Scheme}://{Request.Host}/api/music/hls/{request.PlayNowS3Key}{encoded}";
                    }
                    var title = System.IO.Path.GetFileNameWithoutExtension((request.PlayNowS3Key ?? string.Empty).Split('/').Last());
                    var playNowDto = new MusicTrackDto
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = title,
                        Artist = string.Empty,
                        S3Key = request.PlayNowS3Key,
                        HlsUrl = hlsUrl,
                        StreamUrl = string.Empty,
                        Genre = null,
                        Duration = null
                    };

                    playlistWithUrls.Insert(0, playNowDto);
                    currentAudio = playNowDto.HlsUrl;
                }
            }

            var responseObj = new OapChatResponse
            {
                OapName = oap.Name,
                OapPersonality = oap.Personality,
                ResponseText = request.EnableChat ? responseText : string.Empty,
                MusicPlaylist = playlistWithUrls,
                CurrentAudioUrl = currentAudio,
                DetectedMood = analysis.Mood,
                DetectedIntent = analysis.Intent,
                IsNewsSegment = false,
                BufferConfig = new BufferConfigDto
                {
                    MinBufferSeconds = 3,
                    MaxBufferSeconds = 30,
                    PrefetchTracks = 2,
                    ResumeBufferSeconds = 5
                }
            };

            // Provide a single combined/continuous HLS manifest URL for the client
            responseObj.ContinuousHlsUrl = $"{Request.Scheme}://{Request.Host}/api/oap/playlist/{request.UserId}?count={requestedCount}&offset={request.Offset}&format=m3u8";

            // If CloudFront cookies are configured, issue them so the client can fetch unsigned CloudFront URLs referenced in the combined manifest
            try
            {
                if (MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured)
                {
                    var cf = MusicAI.Infrastructure.Services.CloudFrontCookieSigner.GetSignedCookies("*", TimeSpan.FromMinutes(60));
                    if (cf.HasValue)
                    {
                        var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
                        {
                            HttpOnly = false,
                            Secure = true,
                            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.None,
                            Expires = DateTimeOffset.UtcNow.AddMinutes(60),
                            Path = "/"
                        };
                        Response.Cookies.Append("CloudFront-Policy", cf.Value.policy, cookieOptions);
                        Response.Cookies.Append("CloudFront-Signature", cf.Value.signature, cookieOptions);
                        Response.Cookies.Append("CloudFront-Key-Pair-Id", cf.Value.keyPairId, cookieOptions);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate CloudFront cookies for chat response");
            }

            return Ok(responseObj);
        }

        /// <summary>
        /// Get next tracks for auto-play based on current OAP and user preferences
        /// </summary>
        [HttpGet("next-tracks/{userId}")]
        public async Task<IActionResult> GetNextTracks(string userId, [FromQuery] string? currentTrackId = null, [FromQuery] int count = 5, [FromQuery] int offset = 0)
        {
            // Get currently active OAP
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow()) ?? _personas.First();
            
            var analysis = new MessageAnalysis { Mood = "neutral", Intent = "listen" };
            var playlist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, userId, count, offset);

                var tracks = playlist.Select(track =>
            {
                var allowExplicit = GetAllowExplicitFromClaims();
                var token = _tokenService?.GenerateToken(track.S3Key ?? string.Empty, TimeSpan.FromHours(24), allowExplicit) ?? string.Empty;
                var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";
                return new MusicTrackDto
                {
                    Id = track.Id,
                    Title = track.Title,
                    Artist = track.Artist,
                    S3Key = track.S3Key ?? string.Empty,
                    HlsUrl = GetCdnOrOriginUrl(track.S3Key, encoded),
                    StreamUrl = string.Empty,
                    Duration = track.DurationSeconds.HasValue ? $"{track.DurationSeconds / 60}:{track.DurationSeconds % 60:00}" : null,
                    Genre = track.Genre
                };
            }).ToList();

            return Ok(new { oap = activePersona.Name, tracks, isNews = false });
        }

        /// <summary>
        /// [Spotify-like] Lightweight playlist - returns small initial batch (5 tracks) for instant playback
        /// Optimized for quick load (under 35KB). Includes adaptive buffering configuration.
        /// Frontend requests more tracks as needed for infinite playback.
        /// </summary>
        
        [HttpGet("playlist/{userId}")]
        public async Task<IActionResult> GetContinuousPlaylist(string userId, [FromQuery] int count = 5, [FromQuery] bool light = false, [FromQuery] int offset = 0, [FromQuery] bool manifestOnly = false)
        {
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow()) ?? _personas.First();
            var analysis = new MessageAnalysis { Mood = "neutral", Intent = "listen" };
            var playlist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, userId, count, offset);
            // If SINGLE_HLS_ENDPOINT is set, force manifestOnly behavior so API returns only the single HLS URL (no inline list)
            var singleEndpointEnv = Environment.GetEnvironmentVariable("SINGLE_HLS_ENDPOINT");
            bool singleEndpoint = !string.IsNullOrEmpty(singleEndpointEnv) && (singleEndpointEnv == "1" || singleEndpointEnv.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (singleEndpoint)
            {
                manifestOnly = true;
            }
            // If client only wants the combined manifest URL (single streaming endpoint) and minimal metadata,
            // return that here to avoid sending large lists to the frontend.
            if (manifestOnly)
            {
                // Build a minimal response that clients can use to start playback from a single CDN/HLS endpoint.
                var continuousUrl = $"{Request.Scheme}://{Request.Host}/api/oap/playlist/{userId}?count={count}&offset={offset}&format=m3u8";

                // Provide an optional small preview: first track id/title to show in UI without sending entire list
                var first = playlist.FirstOrDefault();
                string? firstId = first?.Id;
                string? firstTitle = first?.Title;

                // Determine how many tracks to include in the continuous manifest (prefetch ahead)
                var continuousPrefetchTracks = 10;
                var cptEnv = Environment.GetEnvironmentVariable("CONTINUOUS_PREFETCH_TRACKS");
                if (!string.IsNullOrEmpty(cptEnv) && int.TryParse(cptEnv, out var cptv)) continuousPrefetchTracks = cptv;

                // Build combined playlist for manifest generation. If we need more tracks than provided, fetch more.
                var combinedPlaylist = playlist;
                if (continuousPrefetchTracks > playlist.Count)
                {
                    try
                    {
                        combinedPlaylist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, userId, continuousPrefetchTracks, offset);
                    }
                    catch
                    {
                        combinedPlaylist = playlist;
                    }
                }

                // Apply shuffle by default for a continuous listening experience
                var shuffleEnv = Environment.GetEnvironmentVariable("CONTINUOUS_SHUFFLE");
                bool shuffle = string.IsNullOrEmpty(shuffleEnv) || shuffleEnv.Equals("1") || shuffleEnv.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (shuffle)
                {
                    combinedPlaylist = combinedPlaylist.OrderBy(_ => Guid.NewGuid()).ToList();
                }

                // Filter explicit tracks if user is not allowed explicit content
                bool allowExplicitFlag = GetAllowExplicitFromClaims();
                if (!allowExplicitFlag)
                {
                    combinedPlaylist = combinedPlaylist.Where(t => !IsProbablyExplicit(t)).ToList();
                }

                // Generate combined manifest text (may throw)
                string combinedManifest;
                try
                {
                    combinedManifest = await BuildCombinedManifestAsync(combinedPlaylist);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to build combined manifest for user {UserId}", userId);
                    // Fallback to URL-only if manifest generation fails
                    return Ok(new
                    {
                        oap = activePersona.Name,
                        continuousHlsUrl = continuousUrl,
                        firstTrack = first == null ? null : new { id = firstId, title = firstTitle },
                        totalCount = playlist.Count,
                        hasMore = playlist.Count >= count,
                        nextUrl = $"{Request.Scheme}://{Request.Host}/api/oap/playlist/{userId}?count={count}&offset={offset + count}&light={light.ToString().ToLower()}",
                        bufferConfig = new
                        {
                            minBufferSeconds = 3,
                            maxBufferSeconds = 30,
                            prefetchTracks = 2,
                            resumeBufferSeconds = 5
                        }
                    });
                }

                // Control endpoints (these should be implemented / secured separately)
                var controlBase = $"{Request.Scheme}://{Request.Host}/api/oap/control/{userId}";
                var wsUrl = $"{(Request.IsHttps ? "wss" : "ws")}://{Request.Host}/api/oap/ws/{userId}";

                return Ok(new
                {
                    oap = activePersona.Name,
                    continuousHlsUrl = continuousUrl,
                    continuousManifest = combinedManifest, // single-call manifest payload (m3u8 text)
                    firstTrack = first == null ? null : new { id = firstId, title = firstTitle },
                    controls = new
                    {
                        play = $"{controlBase}/play",
                        pause = $"{controlBase}/pause",
                        skip = $"{controlBase}/skip",
                        previous = $"{controlBase}/previous",
                        volume = $"{controlBase}/volume", // PUT { volume: 0-1 }
                        shuffle = $"{controlBase}/shuffle",
                        websocket = wsUrl
                    },
                    totalCount = playlist.Count,
                    hasMore = playlist.Count >= count,
                    nextUrl = $"{Request.Scheme}://{Request.Host}/api/oap/playlist/{userId}?count={count}&offset={offset + count}&light={light.ToString().ToLower()}",
                    bufferConfig = new
                    {
                        minBufferSeconds = 3,
                        maxBufferSeconds = 30,
                        prefetchTracks = continuousPrefetchTracks,
                        resumeBufferSeconds = 5
                    }
                });
            }

            // Token TTL and returned fields differ slightly in light mode to minimize payload.
            var tracks = playlist.Select(track =>
            {
                var allowExplicit = GetAllowExplicitFromClaims();
                var token = _tokenService?.GenerateToken(track.S3Key ?? string.Empty, TimeSpan.FromHours(24), allowExplicit) ?? string.Empty;
                var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";

                    if (light)
                    {
                    // Minimal fields for fastest startup (smaller JSON)
                    // Include `artist` as empty string so anonymous types match between branches
                        var hlsUrl = GetCdnOrOriginUrl(track.S3Key, encoded);

                        return new
                        {
                            id = track.Id,
                            title = track.Title,
                            artist = string.Empty,
                            s3Key = track.S3Key,
                            genre = track.Genre,
                            duration = track.DurationSeconds,
                            hlsUrl = hlsUrl,
                            // If CloudFront not configured we avoid returning per-track signed URLs.
                            // Clients should call /api/oap/track-url to obtain a short-lived tokenized URL when needed.
                            tokenUrl = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CDN_DOMAIN")) ? string.Empty : $"{Request.Scheme}://{Request.Host}/api/oap/track-url?s3Key={Uri.EscapeDataString(track.S3Key ?? string.Empty)}&ttlMinutes=60"
                        };
                }

                // Default (non-light) includes artist + more metadata
                var nonLightHls = GetCdnOrOriginUrl(track.S3Key, encoded);

                return new
                {
                    id = track.Id,
                    title = track.Title,
                    artist = track.Artist,
                    s3Key = track.S3Key,
                    genre = track.Genre,
                    duration = track.DurationSeconds,
                    hlsUrl = nonLightHls,
                    tokenUrl = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CDN_DOMAIN")) ? string.Empty : $"{Request.Scheme}://{Request.Host}/api/oap/track-url?s3Key={Uri.EscapeDataString(track.S3Key ?? string.Empty)}&ttlMinutes=60"
                };
            }).ToList();

                // If client requests a single continuous M3U8 manifest, return it
            var format = Request.Query.ContainsKey("format") ? Request.Query["format"].ToString() : string.Empty;
            if (string.Equals(format, "m3u8", StringComparison.OrdinalIgnoreCase))
            {
                // Manifest caching: cache combined manifest for a short TTL to avoid heavy presigning bursts
                string? cacheKey = null;
                if (_cache != null)
                {
                    var idsConcat = string.Join('|', playlist.Select(p => p.S3Key ?? string.Empty));
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    var hash = Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(idsConcat)));
                    cacheKey = $"combined_manifest:{userId}:{count}:{offset}:{hash}";
                    try
                    {
                        var cachedBytes = await _cache.GetAsync(cacheKey);
                        if (cachedBytes != null && cachedBytes.Length > 0)
                        {
                            var cached = System.Text.Encoding.UTF8.GetString(cachedBytes);
                            Response.Headers["Content-Disposition"] = "inline";
                            return Content(cached, "application/vnd.apple.mpegurl");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Distributed cache lookup failed for {Key}", cacheKey);
                    }
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("#EXTM3U");
                sb.AppendLine("#EXT-X-VERSION:3");
                sb.AppendLine("#EXT-X-TARGETDURATION:10");
                sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
                sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");

                var presignExpiry = TimeSpan.FromMinutes(60);
                var expiryEnv = Environment.GetEnvironmentVariable("MUSIC_PRESIGN_EXPIRY_MINUTES");
                if (!string.IsNullOrEmpty(expiryEnv) && int.TryParse(expiryEnv, out var ev)) presignExpiry = TimeSpan.FromMinutes(ev);

                // Allow client to request that returned manifest use presigned URLs for direct objects
                var presignRequested = Request.Query.ContainsKey("presign") && string.Equals(Request.Query["presign"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

                var preSignSegments = 3;
                var segEnv = Environment.GetEnvironmentVariable("MUSIC_PREFETCH_SEGMENTS");
                if (!string.IsNullOrEmpty(segEnv) && int.TryParse(segEnv, out var segv)) preSignSegments = segv;

                // CDN domain used for manifest URI rewriting
                var cdnDomain = Environment.GetEnvironmentVariable("CDN_DOMAIN")?.TrimEnd('/');

                // (manifest-level prefetch and CDN handling declared below before manifest assembly)

                if (_breaker != null && _breaker.IsOpen)
                {
                    // Circuit is open - avoid heavy manifest assembly
                    return StatusCode(503, new { error = "Manifest generation temporarily unavailable", retryAfterSeconds = 30 });
                }

                // Determine how many tracks to include in the continuous manifest (prefetch ahead)
                var continuousPrefetchTracks = 10;
                var cptEnv = Environment.GetEnvironmentVariable("CONTINUOUS_PREFETCH_TRACKS");
                if (!string.IsNullOrEmpty(cptEnv) && int.TryParse(cptEnv, out var cptv)) continuousPrefetchTracks = cptv;

                // Build combined playlist for manifest generation. If we need more tracks than provided, fetch more.
                var combinedPlaylist = playlist;
                if (continuousPrefetchTracks > playlist.Count)
                {
                    try
                    {
                        combinedPlaylist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, userId, continuousPrefetchTracks, offset);
                    }
                    catch
                    {
                        combinedPlaylist = playlist;
                    }
                }

                // Apply shuffle by default for a continuous listening experience
                var shuffleEnv = Environment.GetEnvironmentVariable("CONTINUOUS_SHUFFLE");
                bool shuffle = string.IsNullOrEmpty(shuffleEnv) || shuffleEnv.Equals("1") || shuffleEnv.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (shuffle)
                {
                    combinedPlaylist = combinedPlaylist.OrderBy(_ => Guid.NewGuid()).ToList();
                }

                // Filter explicit tracks if user is not allowed explicit content (simple heuristic)
                bool allowExplicitFlag = GetAllowExplicitFromClaims();
                if (!allowExplicitFlag)
                {
                    combinedPlaylist = combinedPlaylist.Where(t => !IsProbablyExplicit(t)).ToList();
                }

                foreach (var track in combinedPlaylist)
                {
                    var baseKey = track.S3Key ?? string.Empty;
                    if (string.IsNullOrEmpty(baseKey)) continue;

                    var withoutExt = baseKey.Contains('.') ? baseKey.Substring(0, baseKey.LastIndexOf('.')) : baseKey;
                    var manifestKey = $"{withoutExt}-hls/playlist.m3u8";

                    // Prefer using the generated HLS manifest in S3 (if present). We'll rewrite referenced URIs to presigned S3/CloudFront URLs,
                    // but limit signing to the first N segments per track to reduce signing work.
                    if (_s3Service != null)
                    {
                        try
                        {
                            if (await _s3Service.ObjectExistsAsync(manifestKey))
                            {
                                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m3u8_{Guid.NewGuid():N}.m3u8");
                                var ok = await _s3Service.DownloadFileAsync(manifestKey, tmp);
                                if (ok && System.IO.File.Exists(tmp))
                                {
                                    var lines = await System.IO.File.ReadAllLinesAsync(tmp);
                                    int signedSegmentCount = 0;
                                    // Keep track: when we see #EXTINF, the next non-comment line is the segment URI
                                    for (int i = 0; i < lines.Length; i++)
                                    {
                                        var line = lines[i].TrimEnd('\r');
                                        if (string.IsNullOrWhiteSpace(line)) continue;
                                        if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                                        {
                                            // write the EXTINF line as-is
                                            sb.AppendLine(line);
                                            // find next non-comment, non-empty line for segment URI
                                            int j = i + 1;
                                            while (j < lines.Length && (string.IsNullOrWhiteSpace(lines[j]) || lines[j].TrimStart().StartsWith("#"))) j++;
                                            if (j < lines.Length)
                                            {
                                                var segLine = lines[j].Trim();
                                                // resolve referenced key relative to manifest
                                                var dir = manifestKey.Contains('/') ? manifestKey.Substring(0, manifestKey.LastIndexOf('/')) : string.Empty;
                                                var referencedKey = segLine.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? segLine : (string.IsNullOrEmpty(dir) ? segLine : $"{dir}/{segLine}");
                                                try
                                                {
                                                    string presigned = referencedKey;
                                                    if (!referencedKey.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        // If a generic CDN domain is configured, emit CDN URLs for all segments so the CDN can cache them.
                                                        if (!string.IsNullOrEmpty(cdnDomain))
                                                        {
                                                            presigned = $"https://{cdnDomain}/{referencedKey.TrimStart('/')}";
                                                            // do not increment signedSegmentCount because we're not signing
                                                        }
                                                        else
                                                        {
                                                            // Only presign the first N segments per manifest to allow prefetching
                                                            if (signedSegmentCount < preSignSegments)
                                                            {
                                                                // Try CloudFront signed URL first
                                                                var cf = MusicAI.Infrastructure.Services.CloudFrontSigner.GetSignedUrl(referencedKey, presignExpiry);
                                                                if (!string.IsNullOrEmpty(cf)) presigned = cf;
                                                                else
                                                                {
                                                                    presigned = await _s3Service.GetPresignedUrlAsync(referencedKey, presignExpiry);
                                                                }
                                                                signedSegmentCount++;
                                                            }
                                                            else
                                                            {
                                                                // For remaining segments, prefer unsigned CloudFront domain if configured, else S3 presigned URL
                                                                if (MusicAI.Infrastructure.Services.CloudFrontSigner.IsConfigured)
                                                                {
                                                                    var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                                                                    if (!string.IsNullOrEmpty(cfDomain))
                                                                    {
                                                                        presigned = $"https://{cfDomain}/{referencedKey.TrimStart('/')}";
                                                                    }
                                                                    else
                                                                    {
                                                                        presigned = await _s3Service.GetPresignedUrlAsync(referencedKey, presignExpiry);
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    presigned = await _s3Service.GetPresignedUrlAsync(referencedKey, presignExpiry);
                                                                }
                                                            }
                                                        }
                                                    }
                                                    sb.AppendLine(presigned);
                                                }
                                                catch (Exception ex)
                                                {
                                                    _logger.LogWarning(ex, "Failed to presign HLS segment {Seg} for manifest {Manifest}", referencedKey, manifestKey);
                                                    // fallback to controller HLS endpoint for this track
                                                    var allowExplicit = GetAllowExplicitFromClaims();
                                                    var token = _tokenService?.GenerateToken(baseKey ?? string.Empty, TimeSpan.FromHours(1), allowExplicit) ?? string.Empty;
                                                    var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";
                                                    sb.AppendLine(GetCdnOrOriginUrl(baseKey, encoded));
                                                }

                                                // advance i to j so loop continues after the segment
                                                i = j;
                                            }
                                        }
                                        else if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                                        {
                                            // Variant list in master manifest - include the EXT-X-STREAM-INF and presign next URI (first N variants)
                                            sb.AppendLine(line);
                                            int j = i + 1;
                                            while (j < lines.Length && (string.IsNullOrWhiteSpace(lines[j]) || lines[j].TrimStart().StartsWith("#"))) j++;
                                            if (j < lines.Length)
                                            {
                                                var variantLine = lines[j].Trim();
                                                var dir = manifestKey.Contains('/') ? manifestKey.Substring(0, manifestKey.LastIndexOf('/')) : string.Empty;
                                                var referencedKey = variantLine.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? variantLine : (string.IsNullOrEmpty(dir) ? variantLine : $"{dir}/{variantLine}");
                                                try
                                                {
                                                    string presignedVar;
                                                    if (referencedKey.StartsWith("http", StringComparison.OrdinalIgnoreCase)) presignedVar = referencedKey;
                                                    else
                                                    {
                                                        var cfVar = MusicAI.Infrastructure.Services.CloudFrontSigner.GetSignedUrl(referencedKey, presignExpiry);
                                                        presignedVar = !string.IsNullOrEmpty(cfVar) ? cfVar : await _s3Service.GetPresignedUrlAsync(referencedKey, presignExpiry);
                                                    }
                                                    sb.AppendLine(presignedVar);
                                                }
                                                catch (Exception ex)
                                                {
                                                    _logger.LogWarning(ex, "Failed to presign variant {Variant} for manifest {Manifest}", referencedKey, manifestKey);
                                                }
                                                i = j;
                                            }
                                        }
                                        else
                                        {
                                            // other tags: copy through
                                            if (line.StartsWith("#")) sb.AppendLine(line);
                                        }
                                    }

                                    try { System.IO.File.Delete(tmp); } catch { }
                                    // processed this track via its manifest, move to next track
                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _breaker?.MarkFailure();
                            _logger.LogWarning(ex, "Error processing manifest {Manifest} for track {Track}", manifestKey, baseKey);
                        }
                    }

                    // Fallback: no manifest available - add a single entry pointing to the object URL.
                    var fallbackToken = _tokenService?.GenerateToken(baseKey ?? string.Empty, TimeSpan.FromHours(1), GetAllowExplicitFromClaims()) ?? string.Empty;
                    var fallbackEncoded = string.IsNullOrEmpty(fallbackToken) ? string.Empty : $"?t={WebUtility.UrlEncode(fallbackToken)}";
                    var dur = track.DurationSeconds ?? 10;
                    sb.AppendLine($"#EXTINF:{dur:F1},{(track.Title ?? string.Empty).Replace('\n', ' ')}");

                    // If the client requested presigned URLs for the manifest, generate them here so the browser can fetch private objects.
                    if (presignRequested && _s3Service != null)
                    {
                        try
                        {
                            var pres = await _s3Service.GetPresignedUrlAsync(baseKey, presignExpiry);
                            if (!string.IsNullOrEmpty(pres))
                            {
                                sb.AppendLine(pres);
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Presign requested but failed for {Key}, falling back", baseKey);
                        }
                    }

                    sb.AppendLine(GetCdnOrOriginUrl(baseKey, fallbackEncoded));
                }

                sb.AppendLine("#EXT-X-ENDLIST");

                var manifestText = sb.ToString();
                // Mark success on breaker to allow recovery
                _breaker?.MarkSuccess();
                // store in distributed cache briefly
                if (_cache != null && cacheKey != null)
                {
                    var cacheSeconds = 15;
                    var cacheEnv = Environment.GetEnvironmentVariable("MUSIC_MANIFEST_CACHE_SECONDS");
                    if (!string.IsNullOrEmpty(cacheEnv) && int.TryParse(cacheEnv, out var cs)) cacheSeconds = cs;
                    try
                    {
                        var options = new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(cacheSeconds)
                        };
                        var bytes = System.Text.Encoding.UTF8.GetBytes(manifestText);
                        await _cache.SetAsync(cacheKey, bytes, options);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Distributed cache set failed for {Key}", cacheKey);
                    }
                }

                Response.Headers["Content-Disposition"] = "inline";
                return Content(manifestText, "application/vnd.apple.mpegurl");
            }

            // enrich tracks with CDN/HLS variant info when not in light mode
            object tracksToReturn = tracks;
            if (!light)
            {
                var enriched = new List<object>();
                var qualityProfiles = new[] {
                    new { name = "high", bitrate = 320 },
                    new { name = "medium", bitrate = 192 },
                    new { name = "low", bitrate = 128 }
                };

                foreach (var t in playlist)
                {
                    string baseKey = t.S3Key ?? string.Empty;
                    var withoutExt = baseKey.Contains('.') ? baseKey.Substring(0, baseKey.LastIndexOf('.')) : baseKey;
                    var ext = baseKey.Contains('.') ? baseKey.Substring(baseKey.LastIndexOf('.')) : string.Empty;

                    // Try to find an HLS manifest for this track in S3 (folder: <track>-hls/playlist.m3u8)
                    string? masterHlsUrl = null;
                    var manifestKey = $"{withoutExt}-hls/playlist.m3u8";
                    if (_s3Service != null)
                    {
                        try
                        {
                            if (await _s3Service.ObjectExistsAsync(manifestKey))
                            {
                                // Prefer CloudFront domain with signed cookies when available
                                if (MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured)
                                {
                                    try
                                    {
                                        var cf = MusicAI.Infrastructure.Services.CloudFrontCookieSigner.GetSignedCookies("*", TimeSpan.FromMinutes(60));
                                        if (cf.HasValue)
                                        {
                                            var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
                                            {
                                                HttpOnly = false,
                                                Secure = true,
                                                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.None,
                                                Expires = DateTimeOffset.UtcNow.AddMinutes(60),
                                                Path = "/"
                                            };
                                            Response.Cookies.Append("CloudFront-Policy", cf.Value.policy, cookieOptions);
                                            Response.Cookies.Append("CloudFront-Signature", cf.Value.signature, cookieOptions);
                                            Response.Cookies.Append("CloudFront-Key-Pair-Id", cf.Value.keyPairId, cookieOptions);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Failed to generate CloudFront cookies for playlist enrichment");
                                    }

                                    var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CDN_DOMAIN")))
                                    {
                                        masterHlsUrl = GetCdnOrOriginUrl(manifestKey);
                                    }
                                    else if (!string.IsNullOrEmpty(cfDomain))
                                    {
                                        masterHlsUrl = $"https://{cfDomain}/{manifestKey}";
                                    }
                                    else
                                    {
                                        masterHlsUrl = await _s3Service.GetPresignedUrlAsync(manifestKey, TimeSpan.FromMinutes(60));
                                    }
                                }
                                else
                                {
                                    masterHlsUrl = await _s3Service.GetPresignedUrlAsync(manifestKey, TimeSpan.FromMinutes(60));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error checking/presigning manifest {Manifest} for track {Track}", manifestKey, baseKey);
                        }
                    }

                    // Fallback to controller HLS endpoint (which will rewrite manifest or proxy)
                    if (string.IsNullOrEmpty(masterHlsUrl))
                    {
                        var token = _tokenService?.GenerateToken(t.S3Key ?? string.Empty, TimeSpan.FromHours(24), GetAllowExplicitFromClaims()) ?? string.Empty;
                        var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";
                        masterHlsUrl = GetCdnOrOriginUrl(t.S3Key, encoded);
                    }

                    // Build quality variants (if mp3 variants were generated and uploaded)
                    var qualities = new List<object>();
                    if (_s3Service != null)
                    {
                        foreach (var q in qualityProfiles)
                        {
                            var qKey = $"{withoutExt}_{q.name}{ext}";
                            try
                            {
                                if (await _s3Service.ObjectExistsAsync(qKey))
                                {
                                    string url;
                                    if (MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured)
                                    {
                                        var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                                        if (!string.IsNullOrEmpty(cfDomain))
                                        {
                                            url = $"https://{cfDomain}/{qKey}";
                                        }
                                        else
                                        {
                                            url = await _s3Service.GetPresignedUrlAsync(qKey, TimeSpan.FromMinutes(60));
                                        }
                                    }
                                    else
                                    {
                                        url = await _s3Service.GetPresignedUrlAsync(qKey, TimeSpan.FromMinutes(60));
                                    }
                                    qualities.Add(new { quality = q.name, bitrate = q.bitrate, url });
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error checking/presigning quality {QualityKey} for {Track}", qKey, baseKey);
                            }
                        }
                    }

                    enriched.Add(new
                    {
                        id = t.Id,
                        title = t.Title,
                        artist = t.Artist,
                        s3Key = t.S3Key,
                        genre = t.Genre,
                        duration = t.DurationSeconds,
                        masterHlsUrl,
                        // If CloudFront isn't configured we don't expose signed master URLs inline.
                        // Clients should request a short-lived signed URL via the track-url endpoint when needed.
                        tokenUrl = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CDN_DOMAIN")) ? string.Empty : $"{Request.Scheme}://{Request.Host}/api/oap/track-url?s3Key={Uri.EscapeDataString(t.S3Key ?? string.Empty)}&ttlMinutes=60",
                        qualities
                    });
                }

                tracksToReturn = enriched;
            }

            var response = new
            {
                oap = activePersona.Name,
                tracks = tracksToReturn,
                // A single combined/continuous HLS manifest URL the client can use for immediate playback
                continuousHlsUrl = $"{Request.Scheme}://{Request.Host}/api/oap/playlist/{userId}?count={count}&offset={offset}&format=m3u8",
                hasMore = true,
                nextUrl = $"{Request.Scheme}://{Request.Host}/api/oap/playlist/{userId}?count={count}&offset={offset + count}&light={light.ToString().ToLower()}",
                bufferConfig = new
                {
                    // Adaptive buffering for smooth playback on all network conditions
                    minBufferSeconds = 3,      // Start playing after 3 seconds buffered (fast start)
                    maxBufferSeconds = 30,     // Buffer up to 30 seconds ahead (handles network drops)
                    prefetchTracks = 2,        // Prefetch next 2 tracks in background
                    resumeBufferSeconds = 5    // Resume playback after 5 seconds when paused/reconnected
                }
            };

            // If CloudFront cookies are configured, issue them so the client can fetch unsigned CloudFront URLs referenced in the combined manifest
            try
            {
                if (MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured)
                {
                    var cf = MusicAI.Infrastructure.Services.CloudFrontCookieSigner.GetSignedCookies("*", TimeSpan.FromMinutes(60));
                    if (cf.HasValue)
                    {
                        var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
                        {
                            HttpOnly = false,
                            Secure = true,
                            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.None,
                            Expires = DateTimeOffset.UtcNow.AddMinutes(60),
                            Path = "/"
                        };
                        Response.Cookies.Append("CloudFront-Policy", cf.Value.policy, cookieOptions);
                        Response.Cookies.Append("CloudFront-Signature", cf.Value.signature, cookieOptions);
                        Response.Cookies.Append("CloudFront-Key-Pair-Id", cf.Value.keyPairId, cookieOptions);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate CloudFront cookies for playlist response");
            }

            return Ok(response);
        }

        /// <summary>
        /// Get more tracks for queue - lightweight metadata only, request URLs via /track-url when needed
        /// </summary>
        [HttpGet("playlist-metadata/{userId}")]
        public async Task<IActionResult> GetPlaylistMetadata(string userId, [FromQuery] int count = 20, [FromQuery] int offset = 0)
        {
            var activePersona = _personas.FirstOrDefault(p => p.IsActiveNow()) ?? _personas.First();
            var analysis = new MessageAnalysis { Mood = "neutral", Intent = "listen" };
            var playlist = await GetPersonalizedPlaylist(activePersona.Id, analysis.Mood, analysis.Intent, userId, count, offset);

            var metadata = playlist.Select(track => new
            {
                id = track.Id,
                title = track.Title,
                artist = track.Artist,
                s3Key = track.S3Key,
                genre = track.Genre,
                durationSeconds = track.DurationSeconds
            }).ToList();

            return Ok(new
            {
                oap = activePersona.Name,
                tracks = metadata,
                totalCount = metadata.Count
            });
        }

        /// <summary>
        /// [Spotify-like] Get tokenized URL for specific track on-demand
        /// Enables pre-fetching next tracks while playing current
        /// </summary>
        [AllowAnonymous]
        [HttpGet("track-url")]
        public async Task<IActionResult> GetTrackUrl([FromQuery] string s3Key, [FromQuery] int ttlMinutes = 5)
        {
            if (string.IsNullOrEmpty(s3Key))
                return BadRequest(new { error = "s3Key is required" });
            var token = _tokenService?.GenerateToken(s3Key ?? string.Empty, TimeSpan.FromMinutes(ttlMinutes), GetAllowExplicitFromClaims()) ?? string.Empty;
            var encoded = string.IsNullOrEmpty(token) ? string.Empty : $"?t={WebUtility.UrlEncode(token)}";

            var hlsUrl = GetCdnOrOriginUrl(s3Key, encoded);
            string streamUrl = hlsUrl;

            // If S3 service available, try to generate a presigned object URL for direct object playback
            if (_s3Service != null)
            {
                try
                {
                    var pres = await _s3Service.GetPresignedUrlAsync(s3Key, TimeSpan.FromMinutes(ttlMinutes));
                    if (!string.IsNullOrEmpty(pres)) streamUrl = pres;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate presigned URL for {Key}", s3Key);
                }
            }

            // If GetCdnOrOriginUrl returned a controller HLS endpoint, derive stream URL by swapping the path when presign not produced
            var controllerPrefix = $"{Request.Scheme}://{Request.Host}/api/music/hls/";
            if (string.IsNullOrEmpty(streamUrl) && hlsUrl.StartsWith(controllerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                streamUrl = hlsUrl.Replace("/api/music/hls/", "/api/music/stream/");
            }

            return Ok(new
            {
                hlsUrl,
                streamUrl,
                expiresIn = ttlMinutes * 60 // seconds
            });
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

        private async Task<List<MusicTrack>> GetPersonalizedPlaylist(string oapId, string mood, string intent, string userId, int count = 10, int offset = 0)
        {
            var oap = GetOapContext(oapId);
            
            List<MusicTrack> allMusic = new List<MusicTrack>();
            
            // Try database first (if available) - fetch 2x requested count for shuffle variety
            if (_musicRepo != null)
            {
                try
                {
                    // Only fetch what we need (2x for shuffle variety) instead of entire library
                    // Use offset for pagination when available
                    allMusic = await _musicRepo.GetAllAsync(offset, count * 2);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Database unavailable, falling back to S3 direct listing");
                }
            }

            // Fallback: List music from S3 bucket if database is empty (limit to avoid loading thousands)
            if (!allMusic.Any() && _s3Service != null)
            {
                try
                {
                    _logger.LogInformation("Fetching music files directly from S3 bucket (limited fetch)");
                    var s3Files = await _s3Service.ListObjectsAsync("music/");
                    
                    // Take only what we need (2x count) for fast response
                        var candidates = s3Files
                            .Where(key => key.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                         key.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                                         key.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                            .Skip(Math.Max(0, offset))
                            .ToList();

                        // If count is positive we limit to count*2 for speed, otherwise return full listing
                        var takeCount = count > 0 ? Math.Min(count * 2, candidates.Count) : candidates.Count;

                        allMusic = candidates
                            .Take(takeCount)
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
                var shuffled = allMusic.OrderBy(_ => Guid.NewGuid()).ToList();

                // If count <= 0 return entire shuffled library; otherwise take requested count (apply offset)
                var romanPlaylist = (count <= 0) ? shuffled : shuffled.Skip(Math.Max(0, offset)).Take(count).ToList();

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

            // Shuffle, apply offset and take requested count
            var playlist = filtered
                .OrderBy(_ => Guid.NewGuid())
                .Skip(Math.Max(0, offset))
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
        
        private async Task<string> BuildCombinedManifestAsync(List<MusicTrack> combinedPlaylist)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#EXTM3U");
            sb.AppendLine("#EXT-X-VERSION:3");
            sb.AppendLine("#EXT-X-TARGETDURATION:10");
            sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
            sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");

            var presignExpiry = TimeSpan.FromMinutes(60);
            var expiryEnv = Environment.GetEnvironmentVariable("MUSIC_PRESIGN_EXPIRY_MINUTES");
            if (!string.IsNullOrEmpty(expiryEnv) && int.TryParse(expiryEnv, out var ev)) presignExpiry = TimeSpan.FromMinutes(ev);

            var preSignSegments = 3;
            var segEnv = Environment.GetEnvironmentVariable("MUSIC_PREFETCH_SEGMENTS");
            if (!string.IsNullOrEmpty(segEnv) && int.TryParse(segEnv, out var segv)) preSignSegments = segv;

            var cdnDomain = Environment.GetEnvironmentVariable("CDN_DOMAIN")?.TrimEnd('/');
            foreach (var track in combinedPlaylist)
            {
                var baseKey = track.S3Key ?? string.Empty;
                if (string.IsNullOrEmpty(baseKey)) continue;
                var withoutExt = baseKey.Contains('.') ? baseKey.Substring(0, baseKey.LastIndexOf('.')) : baseKey;
                var manifestKey = $"{withoutExt}-hls/playlist.m3u8";

                if (_s3Service != null)
                {
                    try
                    {
                        if (await _s3Service.ObjectExistsAsync(manifestKey))
                        {
                            // Download manifest temporarily and rewrite segment URIs
                            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m3u8_{Guid.NewGuid():N}.m3u8");
                            var ok = await _s3Service.DownloadFileAsync(manifestKey, tmp);
                            if (ok && System.IO.File.Exists(tmp))
                            {
                                var lines = await System.IO.File.ReadAllLinesAsync(tmp);
                                int signedSegmentCount = 0;
                                for (int i = 0; i < lines.Length; i++)
                                {
                                    var line = lines[i].TrimEnd('\r');
                                    if (string.IsNullOrWhiteSpace(line)) continue;
                                    if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                                    {
                                        sb.AppendLine(line);
                                        int j = i + 1;
                                        while (j < lines.Length && (string.IsNullOrWhiteSpace(lines[j]) || lines[j].TrimStart().StartsWith("#"))) j++;
                                        if (j < lines.Length)
                                        {
                                            var segLine = lines[j].Trim();
                                            var dir = manifestKey.Contains('/') ? manifestKey.Substring(0, manifestKey.LastIndexOf('/')) : string.Empty;
                                            var referencedKey = segLine.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? segLine : (string.IsNullOrEmpty(dir) ? segLine : $"{dir}/{segLine}");
                                            string outUrl = referencedKey;
                                            if (!referencedKey.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                            {
                                                if (!string.IsNullOrEmpty(cdnDomain))
                                                {
                                                    outUrl = $"https://{cdnDomain}/{referencedKey.TrimStart('/')}";
                                                }
                                                else
                                                {
                                                    if (signedSegmentCount < preSignSegments)
                                                    {
                                                        var cf = MusicAI.Infrastructure.Services.CloudFrontSigner.GetSignedUrl(referencedKey, presignExpiry);
                                                        outUrl = !string.IsNullOrEmpty(cf) ? cf : await _s3Service.GetPresignedUrlAsync(referencedKey, presignExpiry);
                                                        signedSegmentCount++;
                                                    }
                                                    else
                                                    {
                                                        if (MusicAI.Infrastructure.Services.CloudFrontSigner.IsConfigured)
                                                        {
                                                            var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                                                            if (!string.IsNullOrEmpty(cfDomain)) outUrl = $"https://{cfDomain}/{referencedKey.TrimStart('/')}";
                                                            else outUrl = await _s3Service.GetPresignedUrlAsync(referencedKey, presignExpiry);
                                                        }
                                                        else outUrl = await _s3Service.GetPresignedUrlAsync(referencedKey, presignExpiry);
                                                    }
                                                }
                                            }
                                            sb.AppendLine(outUrl);
                                            i = j;
                                        }
                                    }
                                    else if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                                    {
                                        sb.AppendLine(line);
                                        int j = i + 1;
                                        while (j < lines.Length && (string.IsNullOrWhiteSpace(lines[j]) || lines[j].TrimStart().StartsWith("#"))) j++;
                                        if (j < lines.Length)
                                        {
                                            var variantLine = lines[j].Trim();
                                            var dir = manifestKey.Contains('/') ? manifestKey.Substring(0, manifestKey.LastIndexOf('/')) : string.Empty;
                                            var referencedKey = variantLine.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? variantLine : (string.IsNullOrEmpty(dir) ? variantLine : $"{dir}/{variantLine}");
                                            string outUrl = referencedKey;
                                            if (!referencedKey.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                            {
                                                if (!string.IsNullOrEmpty(cdnDomain)) outUrl = $"https://{cdnDomain}/{referencedKey.TrimStart('/')}";
                                                else
                                                {
                                                    var cf = MusicAI.Infrastructure.Services.CloudFrontSigner.GetSignedUrl(referencedKey, presignExpiry);
                                                    outUrl = !string.IsNullOrEmpty(cf) ? cf : await _s3Service.GetPresignedUrlAsync(referencedKey, presignExpiry);
                                                }
                                            }
                                            sb.AppendLine(outUrl);
                                            i = j;
                                        }
                                    }
                                    else
                                    {
                                        if (line.StartsWith("#")) sb.AppendLine(line);
                                    }
                                }
                                try { System.IO.File.Delete(tmp); } catch { }
                                continue;
                            }
                        }
                    }
                    catch { /* continue to fallback below */ }
                }

                // Fallback entry (controller HLS endpoint)
                var fallbackToken = _tokenService?.GenerateToken(baseKey ?? string.Empty, TimeSpan.FromHours(1), GetAllowExplicitFromClaims()) ?? string.Empty;
                var fallbackEncoded = string.IsNullOrEmpty(fallbackToken) ? string.Empty : $"?t={WebUtility.UrlEncode(fallbackToken)}";
                var dur = track.DurationSeconds ?? 10;
                sb.AppendLine($"#EXTINF:{dur:F1},{(track.Title ?? string.Empty).Replace('\n', ' ')}");
                sb.AppendLine(GetCdnOrOriginUrl(baseKey, fallbackEncoded));
            }

            sb.AppendLine("#EXT-X-ENDLIST");
            return sb.ToString();
        }
        private bool IsProbablyExplicit(MusicAI.Orchestrator.Data.MusicTrack t)
        {
            if (t == null) return false;
            var s = (t.Title ?? string.Empty) + " " + (t.S3Key ?? string.Empty);
            s = s.ToLowerInvariant();
            if (s.Contains("explicit") || s.Contains("nsfw") || s.Contains("(explicit)") ) return true;
            // crude profanity indicators
            var profanity = new[] { "fuck", "shit", "bitch", "asshole", "nigga" };
            foreach (var p in profanity)
            {
                if (s.Contains(p)) return true;
            }
            return false;
        }

        #region DTOs

        public class OapChatRequest
        {
            public string UserId { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string? Language { get; set; } = "en";
            // Optional controls for playlist behavior
            public int Count { get; set; } = 5;
            public bool Light { get; set; } = false; // minimal payload
            public bool All { get; set; } = false; // return entire library (use with caution)
            public string? PlayNowS3Key { get; set; } = null; // immediate track to play
            public bool EnableChat { get; set; } = true; // whether to include conversational response
            public int Offset { get; set; } = 0; // pagination offset
        }

        public class OapChatResponse
        {
            public string OapName { get; set; } = string.Empty;
            public string OapPersonality { get; set; } = string.Empty;
            public string ResponseText { get; set; } = string.Empty;
            public List<MusicTrackDto> MusicPlaylist { get; set; } = new();
            public string? CurrentAudioUrl { get; set; }
            // Single combined/continuous HLS manifest URL the client can use for immediate playback
            public string? ContinuousHlsUrl { get; set; }
            public BufferConfigDto? BufferConfig { get; set; }
            public string? NextUrl { get; set; }
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

        public class BufferConfigDto
        {
            public int MinBufferSeconds { get; set; }
            public int MaxBufferSeconds { get; set; }
            public int PrefetchTracks { get; set; }
            public int ResumeBufferSeconds { get; set; }
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
