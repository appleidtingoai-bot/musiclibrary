using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Services;

namespace MusicAI.Orchestrator.Controllers
{
    /// <summary>
    /// Adaptive bitrate streaming controller (Spotify-like feature)
    /// Returns different quality versions based on network conditions
    /// </summary>
    [ApiController]
    [Route("api/music")]
    public class MusicQualityController : ControllerBase
    {
        private readonly IQualityService _qualityService;
        private readonly ILogger<MusicQualityController> _logger;

        public MusicQualityController(IQualityService qualityService, ILogger<MusicQualityController> logger)
        {
            _qualityService = qualityService;
            _logger = logger;
        }

        private bool IsCdnAvailable()
        {
            var cdn = Environment.GetEnvironmentVariable("CDN_DOMAIN");
            return !string.IsNullOrEmpty(cdn) || MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured;
        }

        private string GetCdnOrOriginUrl(string s3Key)
        {
            if (string.IsNullOrEmpty(s3Key)) return string.Empty;
            var cdn = Environment.GetEnvironmentVariable("CDN_DOMAIN")?.TrimEnd('/');
            if (!string.IsNullOrEmpty(cdn)) return $"https://{cdn}/{s3Key.TrimStart('/')}";
            if (MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured)
            {
                var cf = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                if (!string.IsNullOrEmpty(cf)) return $"https://{cf}/{s3Key.TrimStart('/')}";
            }
            return $"{Request.Scheme}://{Request.Host}/api/music/hls/{Uri.EscapeDataString(s3Key)}";
        }

        /// <summary>
        /// Get available quality versions for a track
        /// Returns list of bitrates: high (320kbps), medium (192kbps), low (128kbps), original
        /// </summary>
        [HttpGet("qualities/{*s3Key}")]
        public async Task<IActionResult> GetQualities(string s3Key)
        {
            if (string.IsNullOrEmpty(s3Key))
            {
                return BadRequest(new { error = "s3Key is required" });
            }

            // Decode URL-encoded path
            s3Key = Uri.UnescapeDataString(s3Key);

            var qualities = await _qualityService.GetAvailableQualitiesAsync(s3Key);

            return Ok(new
            {
                s3Key,
                qualities = qualities.Select(q => new
                {
                    quality = q.Quality,
                    bitrate = q.Bitrate,
                    label = q.Label,
                    s3Key = q.S3Key
                }),
                recommended = GetRecommendedQuality(Request)
            });
        }

        /// <summary>
        /// Get streaming URL for specific quality
        /// Frontend can request different qualities based on bandwidth detection
        /// </summary>
        [HttpGet("quality-url")]
        public async Task<IActionResult> GetQualityUrl(
            [FromQuery] string s3Key,
            [FromQuery] string quality = "original",
            [FromQuery] int ttlMinutes = 5)
        {
            if (string.IsNullOrEmpty(s3Key))
            {
                return BadRequest(new { error = "s3Key is required" });
            }

            try
            {
                var qualityUrl = await _qualityService.GetQualityUrlAsync(s3Key, quality, TimeSpan.FromMinutes(ttlMinutes));

                string hlsUrl;
                string streamUrl;
                try
                {
                    hlsUrl = GetCdnOrOriginUrl(qualityUrl);
                    streamUrl = GetCdnOrOriginUrl(qualityUrl).Replace("/api/music/hls/", "/api/music/stream/");
                }
                catch
                {
                    hlsUrl = $"{Request.Scheme}://{Request.Host}/api/music/hls/{qualityUrl}";
                    streamUrl = $"{Request.Scheme}://{Request.Host}/api/music/stream/{qualityUrl}";
                }

                return Ok(new
                {
                    hlsUrl,
                    streamUrl,
                    quality,
                    expiresIn = ttlMinutes * 60
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get quality URL for {S3Key} @ {Quality}", s3Key, quality);
                return StatusCode(500, new { error = "Failed to generate quality URL" });
            }
        }

        /// <summary>
        /// Get adaptive playlist manifest that includes multiple quality levels
        /// HLS player will automatically switch between qualities based on bandwidth
        /// </summary>
        [HttpGet("adaptive-playlist/{*s3Key}")]
        public async Task<IActionResult> GetAdaptivePlaylist(string s3Key)
        {
            if (string.IsNullOrEmpty(s3Key))
            {
                return BadRequest(new { error = "s3Key is required" });
            }

            s3Key = Uri.UnescapeDataString(s3Key);

            var qualities = await _qualityService.GetAvailableQualitiesAsync(s3Key);

            if (!qualities.Any())
            {
                return NotFound(new { error = "No quality versions found" });
            }

            // Generate HLS master playlist with multiple quality streams
            var m3u8 = "#EXTM3U\n";
            m3u8 += "#EXT-X-VERSION:3\n";

                foreach (var quality in qualities.OrderByDescending(q => q.Bitrate))
            {
                var token = await _qualityService.GetQualityUrlAsync(s3Key, quality.Quality, TimeSpan.FromMinutes(10));
                var streamUrl = GetCdnOrOriginUrl(token);

                // Add quality variant stream
                m3u8 += $"#EXT-X-STREAM-INF:BANDWIDTH={quality.Bitrate * 1000},RESOLUTION=1x1\n";
                m3u8 += $"{streamUrl}\n";
            }

            _logger.LogInformation("Generated adaptive playlist for {S3Key} with {Count} quality levels", 
                s3Key, qualities.Count);

            return Content(m3u8, "application/vnd.apple.mpegurl");
        }

        #region Helper Methods

        /// <summary>
        /// Recommend quality based on client headers and connection type
        /// </summary>
        private string GetRecommendedQuality(Microsoft.AspNetCore.Http.HttpRequest request)
        {
            // Check for connection hints (Chrome, Edge send these)
            if (request.Headers.TryGetValue("downlink", out var downlink))
            {
                if (double.TryParse(downlink, out var mbps))
                {
                    if (mbps >= 5.0) return "high";
                    if (mbps >= 2.0) return "medium";
                    return "low";
                }
            }

            // Check for Save-Data header (data saver mode)
            if (request.Headers.TryGetValue("Save-Data", out var saveData) && saveData == "on")
            {
                return "low";
            }

            // Default to medium quality
            return "medium";
        }

        #endregion
    }
}
