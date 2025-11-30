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

        public MusicStreamController(IS3Service? s3Service, ILogger<MusicStreamController> logger, IStreamTokenService tokenService)
        {
            _s3Service = s3Service;
            _logger = logger;
            _tokenService = tokenService;
        }

        /// <summary>
        /// Public HLS endpoint - returns M3U8 manifest for audio streaming
        /// No authentication required - used by OAP chat responses
        /// </summary>
        [HttpGet("hls/{*key}")]
        public IActionResult GetHlsPlaylist([FromRoute] string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return BadRequest(new { error = "key is required" });
            }

            _logger.LogInformation("HLS playlist requested for key: {Key}", key);

            // Enforce Origin whitelist for HLS requests to prevent direct downloads
            var origin = Request.Headers.ContainsKey("Origin") ? Request.Headers["Origin"].ToString() : string.Empty;
            if (string.IsNullOrEmpty(origin) || !(origin == "http://localhost:3000" || origin == "https://tingoradio.ai" || origin == "https://tingoradiomusiclibrary.tingoai.ai"))
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
            if (!_tokenService.ValidateToken(token, out var tokenS3Key) || string.IsNullOrEmpty(tokenS3Key) || tokenS3Key != key)
            {
                _logger.LogWarning("Blocked HLS request with invalid token or key mismatch. Key:{Key} TokenKey:{TokenKey}", key, tokenS3Key);
                return StatusCode(403, new { error = "Invalid or expired token" });
            }

            // Generate HLS master playlist pointing to public stream proxy
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

            var result = Content(m3u8Content, "application/vnd.apple.mpegurl");
            // Encourage browsers to play inline (not download)
            Response.Headers["Content-Disposition"] = "inline";
            return result;
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
            if (string.IsNullOrEmpty(origin) || !(origin == "http://localhost:3000" || origin == "https://tingoradio.ai" || origin == "https://tingoradiomusiclibrary.tingoai.ai"))
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
            if (!_tokenService.ValidateToken(token, out var tokenS3Key) || string.IsNullOrEmpty(tokenS3Key) || tokenS3Key != key)
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
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
                var contentLength = response.Content.Headers.ContentLength;

                Response.ContentType = contentType;
                
                if (contentLength.HasValue)
                {
                    Response.ContentLength = contentLength.Value;
                }

                // Enable CORS for browser playback: echo Origin only when it's an allowed origin
                if (!string.IsNullOrEmpty(origin) && (origin == "http://localhost:3000" || origin == "https://tingoradio.ai" || origin == "https://tingoradiomusiclibrary.tingoai.ai"))
                {
                    Response.Headers["Access-Control-Allow-Origin"] = origin;
                    Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                    Response.Headers["Access-Control-Allow-Headers"] = "Range, Content-Type";
                    Response.Headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
                }
                
                // Cache for 1 hour
                Response.Headers["Cache-Control"] = "public, max-age=3600";
                Response.Headers["Accept-Ranges"] = "bytes";

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
            if (!string.IsNullOrEmpty(origin) && (origin == "http://localhost:3000" || origin == "https://tingoradio.ai" || origin == "https://tingoradiomusiclibrary.tingoai.ai"))
            {
                Response.Headers["Access-Control-Allow-Origin"] = origin;
                Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                Response.Headers["Access-Control-Allow-Headers"] = "Range, Content-Type";
            }
            return Ok();
        }
    }
}
