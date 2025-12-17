using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicAI.Infrastructure.Services;
using MusicAI.Orchestrator.Services;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace MusicAI.Orchestrator.Controllers
{
    /// <summary>
    /// Public music streaming endpoints - no authentication required for playback
    /// </summary>
    [ApiController]
    [Route("api/music")]
    public class MusicStreamController : ControllerBase
    {
        private readonly IS3Service? _s3Service;
        private readonly ILogger<MusicStreamController> _logger;
        private readonly IStreamTokenService _tokenService;
        private readonly dynamic _breaker;

        public MusicStreamController(IS3Service? s3Service, ILogger<MusicStreamController> logger, IStreamTokenService tokenService, dynamic breaker)
        {
            _s3Service = s3Service;
            _logger = logger;
            _tokenService = tokenService;
            _breaker = breaker;
        }

        /// <summary>
        /// Public HLS endpoint - returns M3U8 manifest for audio streaming
        /// No authentication required - used by OAP chat responses
        /// </summary>
        [HttpGet("hls/{*key}")]
        public async Task<IActionResult> GetHlsPlaylist([FromRoute] string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return BadRequest(new { error = "key is required" });
            }

            _logger.LogInformation("HLS playlist requested for key: {Key}", key);

            if (_breaker != null && _breaker.IsOpen)
            {
                _logger.LogWarning("Manifest generation circuit open - rejecting HLS request for {Key}", key);
                return StatusCode(503, new { error = "Temporarily unavailable" });
            }

            // Enforce Origin whitelist for HLS requests to prevent direct downloads
            var origin = Request.Headers.ContainsKey("Origin") ? Request.Headers["Origin"].ToString() : string.Empty;
            if (string.IsNullOrEmpty(origin) || !IsOriginAllowed(origin))
            {
                _logger.LogWarning("Blocked HLS request without allowed Origin: {Origin}", origin);
                return StatusCode(403, new { error = "Origin not allowed" });
            }

            // Validate stream token (short-lived) - query param 't'
            var token = Request.Query.ContainsKey("t") ? Request.Query["t"].ToString() : string.Empty;
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Blocked HLS request missing token for key: {Key}", key);
                return StatusCode(403, new { error = "Missing token" });
            }
            if (!_tokenService.ValidateToken(token, out var tokenS3Key, out var tokenAllowExplicit) || string.IsNullOrEmpty(tokenS3Key) || tokenS3Key != key)
            {
                _logger.LogWarning("Blocked HLS request with invalid token or key mismatch. Key:{Key} TokenKey:{TokenKey}", key, tokenS3Key);
                return StatusCode(403, new { error = "Invalid or expired token" });
            }

            // If S3 is configured and the key points to an HLS manifest in S3, download the manifest,
            // rewrite any relative segment/variant URIs to presigned S3 URLs, and return the rewritten manifest.
            if (_s3Service != null && key.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Check object existence
                    var exists = await _s3Service.ObjectExistsAsync(key);
                    if (exists)
                    {
                        var tmpFile = Path.Combine(Path.GetTempPath(), $"hls_manifest_{Guid.NewGuid():N}.m3u8");
                        var downloaded = await _s3Service.DownloadFileAsync(key, tmpFile);
                        if (!downloaded || !System.IO.File.Exists(tmpFile))
                        {
                            _logger.LogError("Failed to download manifest {Key} from S3", key);
                            return NotFound(new { error = "Manifest not found" });
                        }

                        var content = await System.IO.File.ReadAllTextAsync(tmpFile);
                        var lines = content.Split(new[] { '\n' }).Select(l => l.TrimEnd('\r')).ToList();

                        // Presign expiry (minutes)
                        var expiryMinutes = 60;
                        var expiryEnv = Environment.GetEnvironmentVariable("MUSIC_PRESIGN_EXPIRY_MINUTES");
                        if (!string.IsNullOrEmpty(expiryEnv) && int.TryParse(expiryEnv, out var ev)) expiryMinutes = ev;

                        // If CloudFront is configured, issue signed cookies for the client so it can fetch multiple objects without per-URL signing.
                        if (CloudFrontCookieHelper.IsConfigured())
                        {
                            try
                            {
                                var cfCookies = CloudFrontCookieHelper.GetSignedCookies("*", TimeSpan.FromMinutes(expiryMinutes));
                                if (cfCookies.HasValue)
                                {
                                    var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
                                    {
                                        HttpOnly = false,
                                        Secure = true,
                                        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.None,
                                        Expires = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes),
                                        Path = "/"
                                    };
                                    Response.Cookies.Append("CloudFront-Policy", cfCookies.Value.policy, cookieOptions);
                                    Response.Cookies.Append("CloudFront-Signature", cfCookies.Value.signature, cookieOptions);
                                    Response.Cookies.Append("CloudFront-Key-Pair-Id", cfCookies.Value.keyPairId, cookieOptions);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to generate CloudFront signed cookies");
                            }
                        }

                        // Directory prefix for segments/variants
                        var dir = key.Contains('/') ? key.Substring(0, key.LastIndexOf('/')) : string.Empty;

                        for (int i = 0; i < lines.Count; i++)
                        {
                            var line = lines[i];
                            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                            if (Uri.IsWellFormedUriString(line, UriKind.Absolute))
                            {
                                // Already absolute URL; skip
                                continue;
                            }

                            // Build S3 key for the referenced resource
                            var referencedKey = string.IsNullOrEmpty(dir) ? line : $"{dir}/{line}";

                            try
                            {
                                // Prefer CDN_DOMAIN when set, then CloudFront domain (cookies), else presigned S3 URL
                                var cdn = Environment.GetEnvironmentVariable("CDN_DOMAIN")?.TrimEnd('/');
                                if (!string.IsNullOrEmpty(cdn))
                                {
                                    // Worker expects /media/<key> requests; emit CDN URL with /media/ prefix.
                                    lines[i] = $"https://{cdn}/media/{referencedKey.TrimStart('/')}";
                                }
                                else if (CloudFrontCookieHelper.IsConfigured())
                                {
                                    var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                                    if (!string.IsNullOrEmpty(cfDomain))
                                    {
                                        lines[i] = $"https://{cfDomain}/{referencedKey.TrimStart('/')}";
                                    }
                                    else
                                    {
                                        var presigned = await _s3Service.GetPresignedUrlAsync(referencedKey, TimeSpan.FromMinutes(expiryMinutes));
                                        lines[i] = presigned;
                                    }
                                }
                                else
                                {
                                    var presigned = await _s3Service.GetPresignedUrlAsync(referencedKey, TimeSpan.FromMinutes(expiryMinutes));
                                    lines[i] = presigned;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to presign referenced HLS resource {Resource} referenced in {Key}", referencedKey, key);
                                // Leave original line if presign fails
                            }
                        }

                        var rewritten = string.Join("\n", lines);
                        try { System.IO.File.Delete(tmpFile); } catch { }

                        Response.Headers["Content-Disposition"] = "inline";
                        return Content(rewritten, "application/vnd.apple.mpegurl");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while generating presigned HLS manifest for {Key}", key);
                    // fallthrough to fallback manifest generation below
                }
            }

            // Fallback: generate a simple HLS manifest that proxies the stream endpoint
            var baseUrl = $"{Request.Scheme}://{Request.Host}/api/music/stream/{Uri.EscapeDataString(key)}";

            var m3u8Content = $@"#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:10
#EXT-X-MEDIA-SEQUENCE:0
#EXT-X-PLAYLIST-TYPE:VOD

# High Quality Audio Stream
#EXTINF:10.0,
{baseUrl}

#EXT-X-ENDLIST";

            Response.Headers["Content-Disposition"] = "inline";
            return Content(m3u8Content, "application/vnd.apple.mpegurl");
        }

        /// <summary>
        /// Public streaming endpoint - proxies audio from S3
        /// No authentication required - used for public playback
        /// Supports Range requests for seeking
        /// </summary>
        [HttpGet("stream/{*key}")]
        public async Task<IActionResult> StreamAudio([FromRoute] string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return BadRequest(new { error = "key is required" });
            }

            _logger.LogInformation("Stream requested for key: {Key}", key);

            // Require an Origin header from allowed origins to reduce direct download abuse
            var origin = Request.Headers.ContainsKey("Origin") ? Request.Headers["Origin"].ToString() : string.Empty;
            if (string.IsNullOrEmpty(origin) || !IsOriginAllowed(origin))
            {
                _logger.LogWarning("Blocked stream request without allowed Origin: {Origin}", origin);
                return StatusCode(403, new { error = "Origin not allowed" });
            }

            // Validate stream token (short-lived) - query param 't'
            var token = Request.Query.ContainsKey("t") ? Request.Query["t"].ToString() : string.Empty;
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Blocked stream request missing token for key: {Key}", key);
                return StatusCode(403, new { error = "Missing token" });
            }
            if (!_tokenService.ValidateToken(token, out var tokenS3Key, out var tokenAllowExplicit) || string.IsNullOrEmpty(tokenS3Key) || tokenS3Key != key)
            {
                _logger.LogWarning("Blocked stream request with invalid token or key mismatch. Key:{Key} TokenKey:{TokenKey}", key, tokenS3Key);
                return StatusCode(403, new { error = "Invalid or expired token" });
            }

            if (_s3Service == null)
            {
                _logger.LogError("S3 service not configured");
                return StatusCode(500, new { error = "Streaming service not configured" });
            }

            try
            {
                // If CloudFront signed cookies are available, issue them and redirect client to CloudFront URL for direct fetch
                if (CloudFrontCookieHelper.IsConfigured())
                {
                    try
                    {
                        var cf = CloudFrontCookieHelper.GetSignedCookies("*", TimeSpan.FromMinutes(5));
                        if (cf.HasValue)
                        {
                            var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
                            {
                                HttpOnly = false,
                                Secure = true,
                                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.None,
                                Expires = DateTimeOffset.UtcNow.AddMinutes(5),
                                Path = "/"
                            };
                            Response.Cookies.Append("CloudFront-Policy", cf.Value.policy, cookieOptions);
                            Response.Cookies.Append("CloudFront-Signature", cf.Value.signature, cookieOptions);
                            Response.Cookies.Append("CloudFront-Key-Pair-Id", cf.Value.keyPairId, cookieOptions);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate CloudFront cookies for stream redirect");
                    }

                    var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                    if (!string.IsNullOrEmpty(cfDomain))
                    {
                        var redirectUrl = $"https://{cfDomain}/{key}";
                        return Redirect(redirectUrl);
                    }
                }

                // Fallback to proxying via S3 presigned URL
                // Get presigned URL from S3 (short-lived, 5 minutes)
                var presignedUrl = await _s3Service.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(5));

                if (string.IsNullOrEmpty(presignedUrl))
                {
                    _logger.LogError("Failed to generate presigned URL for key: {Key}", key);
                    return NotFound(new { error = "Track not found" });
                }

                // Proxy the stream from S3
                using var httpClient = new HttpClient();
                
                // Forward Range header if present (for seeking)
                if (Request.Headers.ContainsKey("Range"))
                {
                    var rangeHeader = Request.Headers["Range"].ToString();
                    httpClient.DefaultRequestHeaders.Add("Range", rangeHeader);
                    _logger.LogDebug("Range request: {Range}", rangeHeader);
                }

                var response = await httpClient.GetAsync(presignedUrl, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("S3 request failed with status: {Status}", response.StatusCode);
                    return StatusCode((int)response.StatusCode, new { error = "Failed to fetch audio" });
                }

                // Set appropriate headers for audio streaming
                var contentType = response.Content.Headers.ContentType?.ToString();
                // If S3 returned no content type or generic octet-stream, try to infer from file extension
                if (string.IsNullOrEmpty(contentType) || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    var ext = System.IO.Path.GetExtension(key ?? string.Empty)?.ToLowerInvariant();
                    contentType = ext switch
                    {
                        ".mp3" => "audio/mpeg",
                        ".m4a" => "audio/mp4",
                        ".mp4" => "audio/mp4",
                        ".aac" => "audio/aac",
                        ".wav" => "audio/wav",
                        ".ogg" => "audio/ogg",
                        ".m3u8" => "application/vnd.apple.mpegurl",
                        _ => "audio/mpeg"
                    };
                    _logger.LogDebug("Overriding missing/opaque content-type for key {Key} -> {ContentType}", key, contentType);
                }
                else
                {
                    // use the returned content type
                }
                var contentLength = response.Content.Headers.ContentLength;
                Response.ContentType = contentType;
                
                if (contentLength.HasValue)
                {
                    Response.ContentLength = contentLength.Value;
                }

                // Enable CORS for browser playback: echo Origin only when it's an allowed origin
                if (!string.IsNullOrEmpty(origin) && IsOriginAllowed(origin))
                {
                    Response.Headers["Access-Control-Allow-Origin"] = origin;
                    Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                    Response.Headers["Access-Control-Allow-Headers"] = "Range, Content-Type";
                    Response.Headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
                }
                
                // Cache for 1 hour
                Response.Headers["Cache-Control"] = "public, max-age=3600";
                Response.Headers["Accept-Ranges"] = "bytes";

                // Ensure we do NOT suggest download via Content-Disposition
                if (Response.Headers.ContainsKey("Content-Disposition"))
                {
                    Response.Headers.Remove("Content-Disposition");
                }

                // Handle partial content (206) for Range requests
                if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    Response.StatusCode = 206;
                    if (response.Content.Headers.ContentRange != null)
                    {
                        Response.Headers["Content-Range"] = response.Content.Headers.ContentRange.ToString();
                    }
                }

                // Stream the content
                await using var stream = await response.Content.ReadAsStreamAsync();
                await stream.CopyToAsync(Response.Body);

                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming audio for key: {Key}", key);
                return StatusCode(500, new { error = "Streaming failed", details = ex.Message });
            }
        }

        /// <summary>
        /// OPTIONS endpoint for CORS preflight
        /// </summary>
        [HttpOptions("stream/{*key}")]
        [HttpOptions("hls/{*key}")]
        public IActionResult Options()
        {
            var origin = Request.Headers.ContainsKey("Origin") ? Request.Headers["Origin"].ToString() : string.Empty;
            if (!string.IsNullOrEmpty(origin) && IsOriginAllowed(origin))
            {
                Response.Headers["Access-Control-Allow-Origin"] = origin;
                Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                Response.Headers["Access-Control-Allow-Headers"] = "Range, Content-Type";
            }
            return Ok();
        }

        private static bool IsOriginAllowed(string origin)
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS");
                if (!string.IsNullOrEmpty(env))
                {
                    var parts = env.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
                    return parts.Any(p => string.Equals(p, origin, StringComparison.OrdinalIgnoreCase));
                }

                // Fallback defaults
                var defaults = new[] { "http://localhost:3000", "https://tingoradio.ai", "https://tingoradiomusiclibrary.tingoai.ai" };
                return defaults.Any(d => string.Equals(d, origin, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        // Reflection-based helper to safely interact with an optional CloudFrontCookieSigner type
        // This avoids a hard compile dependency on MusicAI.Infrastructure.Services.CloudFrontCookieSigner
        private static class CloudFrontCookieHelper
        {
            public static bool IsConfigured()
            {
                try
                {
                    var type = FindType();
                    if (type == null) return false;
                    var prop = type.GetProperty("IsConfigured", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop == null) return false;
                    var val = prop.GetValue(null);
                    return val is bool b && b;
                }
                catch
                {
                    return false;
                }
            }

            public static (string policy, string signature, string keyPairId)? GetSignedCookies(string resource, TimeSpan expiry)
            {
                try
                {
                    var type = FindType();
                    if (type == null) return null;
                    var method = type.GetMethod("GetSignedCookies", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (method == null) return null;
                    var result = method.Invoke(null, new object[] { resource, expiry });
                    if (result == null) return null;

                    // Handle Nullable<T> results that have HasValue / Value
                    var resultType = result.GetType();
                    var hasValueProp = resultType.GetProperty("HasValue");
                    object valueObj = result;
                    if (hasValueProp != null)
                    {
                        var hasValue = (bool)hasValueProp.GetValue(result);
                        if (!hasValue) return null;
                        var valueProp = resultType.GetProperty("Value");
                        if (valueProp == null) return null;
                        valueObj = valueProp.GetValue(result);
                    }

                    if (valueObj == null) return null;

                    var policy = valueObj.GetType().GetProperty("policy")?.GetValue(valueObj)?.ToString();
                    var signature = valueObj.GetType().GetProperty("signature")?.GetValue(valueObj)?.ToString();
                    var keyPairId = valueObj.GetType().GetProperty("keyPairId")?.GetValue(valueObj)?.ToString();

                    if (policy == null || signature == null || keyPairId == null) return null;
                    return (policy, signature, keyPairId);
                }
                catch
                {
                    return null;
                }
            }

            private static Type FindType()
            {
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = a.GetType("MusicAI.Infrastructure.Services.CloudFrontCookieSigner");
                        if (t != null) return t;
                    }
                }
                catch
                {
                }
                return null;
            }
        }
    }
}
