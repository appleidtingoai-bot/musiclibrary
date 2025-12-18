using Microsoft.AspNetCore.Mvc;
using MusicAI.Infrastructure.Services;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using System;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Data;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/media")]
    public class MediaController : ControllerBase
    {
        private readonly IS3Service _s3;
        private readonly IHttpClientFactory _httpFactory;
        private readonly UsersRepository _usersRepo;
        private readonly SubscriptionsRepository? _subsRepo;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<MediaController> _logger;
        private readonly IConfiguration _configuration;

        public MediaController(IS3Service s3,
            IHttpClientFactory httpFactory,
            UsersRepository usersRepo,
            SubscriptionsRepository? subsRepo,
            IMemoryCache memoryCache,
            ILogger<MediaController> logger,
            IConfiguration configuration)
        {
            _s3 = s3;
            _httpFactory = httpFactory;
            _usersRepo = usersRepo;
            _subsRepo = subsRepo;
            _memoryCache = memoryCache;
            _logger = logger;
            _configuration = configuration;
        }

        // GET /api/media/presign?key=path/to/object.mp3
        [HttpGet("presign")]
        public async Task<IActionResult> Presign([FromQuery] string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return BadRequest(new { error = "missing key" });
            // Accept URL-encoded keys from the worker; decode so S3 receives the raw object key
            try
            {
                key = WebUtility.UrlDecode(key ?? string.Empty);
            }
            catch
            {
                // if decode fails, keep original key
            }
            if (_s3 == null) return StatusCode(500, new { error = "S3 service not configured" });

            // Authenticate request: either a JWT in Authorization: Bearer or MusicAI.Auth cookie,
            // or a worker API key (ORCHESTRATOR_API_KEY) supplied as Authorization: Bearer <key>.
            string? token = null;
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            var orchestratorApiKey = _configuration["Orchestrator:ApiKey"] ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_API_KEY");

            var isWorkerAuth = false;
            // Check for worker key in a custom header first (worker will send X-Orchestrator-Key)
            var orchestratorHeader = Request.Headers["X-Orchestrator-Key"].FirstOrDefault();
            if (!string.IsNullOrEmpty(orchestratorHeader) && !string.IsNullOrEmpty(orchestratorApiKey) && orchestratorHeader == orchestratorApiKey)
            {
                isWorkerAuth = true;
            }

            if (!isWorkerAuth && !string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = authHeader.Substring("Bearer ".Length).Trim();
                if (!string.IsNullOrEmpty(orchestratorApiKey) && candidate == orchestratorApiKey)
                {
                    // authenticated as trusted worker (fallback)
                    isWorkerAuth = true;
                }
                else
                {
                    token = candidate;
                }
            }
            else if (Request.Cookies.TryGetValue("MusicAI.Auth", out var cookieToken))
            {
                token = cookieToken;
            }

            ClaimsPrincipal? principal = null;
            string? userId = null;

            if (!isWorkerAuth)
            {
                if (string.IsNullOrEmpty(token)) return Unauthorized(new { error = "missing token" });

                try
                {
                    var jwtKey = _configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_SECRET") ?? string.Empty;
                    var handler = new JwtSecurityTokenHandler();
                    var parameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = !string.IsNullOrEmpty(jwtKey),
                        IssuerSigningKey = !string.IsNullOrEmpty(jwtKey) ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)) : null,
                        ClockSkew = TimeSpan.FromSeconds(30)
                    };
                    principal = handler.ValidateToken(token, parameters, out var validatedToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Presign token validation failed: {Msg}", ex.Message);
                    return Unauthorized(new { error = "invalid token" });
                }

                userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
                if (string.IsNullOrEmpty(userId)) return Unauthorized(new { error = "invalid token payload" });
            }
            else
            {
                // Worker-authenticated requests are trusted; set a synthetic user id for logging
                userId = "edge-worker";
            }

            // Rate limiting: simple per-user sliding counter (MemoryCache)
            var rlKey = $"presign_rl:{userId}";
            var rlLimit = 30; // requests per minute
            var rlWindow = TimeSpan.FromMinutes(1);
            var count = _memoryCache.GetOrCreate<int>(rlKey, entry =>
            {
                entry.SetSlidingExpiration(rlWindow);
                return 0;
            });
            if (count >= rlLimit)
            {
                _logger.LogWarning("Rate limit exceeded for user {UserId}", userId);
                return StatusCode(429, new { error = "rate limit exceeded" });
            }
            _memoryCache.Set(rlKey, count + 1, new MemoryCacheEntryOptions { SlidingExpiration = rlWindow });

            // Allow jingles even without subscription/credits (key contains 'jingle')
            var isJingle = key.IndexOf("jingle", StringComparison.OrdinalIgnoreCase) >= 0;

            // Authorization / policy enforcement: check subscription or credits
            try
            {
                if (!isWorkerAuth)
                {
                    var user = _usersRepo.GetById(userId);
                    if (user == null)
                    {
                        _logger.LogWarning("Presign denied: user not found {UserId}", userId);
                        return Unauthorized(new { error = "user not found" });
                    }
                    if (!isJingle)
                    {
                        var hasAccess = false;
                        if (user.IsSubscribed) hasAccess = true;
                        else if (user.Credits > 0 && (user.CreditsExpiry == null || user.CreditsExpiry > DateTime.UtcNow)) hasAccess = true;
                        else if (_subsRepo != null)
                        {
                            var sub = await _subsRepo.GetActiveSubscriptionAsync(userId);
                            if (sub != null && sub.Status == "active") hasAccess = true;
                        }

                        if (!hasAccess)
                        {
                            // If credits expired, allow access when the user's country is Nigeria; otherwise require purchase
                            var country = user.Country ?? string.Empty;
                            var isNigeria = country.Equals("NG", StringComparison.OrdinalIgnoreCase) || country.Contains("Nigeria", StringComparison.OrdinalIgnoreCase);
                            if (isNigeria)
                            {
                                _logger.LogInformation("Presign allowed: expired credits but user country is Nigeria for {UserId}", userId);
                                hasAccess = true;
                            }
                        }

                        if (!hasAccess)
                        {
                            _logger.LogInformation("Presign denied: no valid subscription/credits for {UserId}", userId);
                            return StatusCode(402, new { error = "insufficient subscription/credits" });
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("Presign requested by trusted worker for key {Key}", key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking user/subscription for presign");
                return StatusCode(500, new { error = "internal" });
            }

            try
            {
                // Generate a short-lived presigned URL
                var url = await _s3.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(5));

                // Attempt a HEAD request to get metadata (content-length, content-type, etag)
                var client = _httpFactory.CreateClient();
                var head = new HttpRequestMessage(HttpMethod.Head, url);
                var headResp = await client.SendAsync(head);

                long? contentLength = null;
                string? contentType = null;
                string? etag = null;

                if (headResp.IsSuccessStatusCode)
                {
                    contentLength = headResp.Content.Headers.ContentLength;
                    contentType = headResp.Content.Headers.ContentType?.MediaType ?? (headResp.Headers.TryGetValues("Content-Type", out var v) ? v.FirstOrDefault() : null);
                    etag = headResp.Headers.ETag?.Tag;
                }

                _logger.LogInformation("Presign issued for user {UserId} key {Key}", userId, key);
                return Ok(new { url, contentLength, contentType, etag });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Presign generation failed for key {Key}", key);
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
