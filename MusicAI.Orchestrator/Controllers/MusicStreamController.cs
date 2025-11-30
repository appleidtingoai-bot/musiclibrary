using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicAI.Infrastructure.Services;
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

        public MusicStreamController(IS3Service? s3Service, ILogger<MusicStreamController> logger)
        {
            _s3Service = s3Service;
            _logger = logger;
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

                // Enable CORS for browser playback
                Response.Headers["Access-Control-Allow-Origin"] = "*";
                Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                Response.Headers["Access-Control-Allow-Headers"] = "Range, Content-Type";
                Response.Headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
                
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
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            Response.Headers["Access-Control-Allow-Headers"] = "Range, Content-Type";
            return Ok();
        }
    }
}
