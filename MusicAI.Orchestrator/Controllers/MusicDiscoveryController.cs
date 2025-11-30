using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MusicAI.Orchestrator.Models;
using MusicAI.Orchestrator.Services;
using MusicAI.Infrastructure.Services;

namespace MusicAI.Orchestrator.Controllers
{
    /// <summary>
    /// Secure music streaming controller for OAPs
    /// Admin POSTs music to S3, OAPs GET content via secure streaming endpoint
    /// </summary>
    [ApiController]
    [Route("api/oap")]
    public class MusicDiscoveryController : ControllerBase
    {
        private readonly IS3Service? _s3;
        private static readonly HttpClient _httpClient = new();

        public MusicDiscoveryController(IS3Service? s3 = null)
        {
            _s3 = s3;
        }

        /// <summary>
        /// Get a streaming token for music playback
        /// This token is one-time use and expires in 30 seconds
        /// OAPs call this to get a secure, non-copyable streaming URL
        /// </summary>
        [HttpGet("get-stream-token")]
        public IActionResult GetStreamToken(
            [FromQuery] string? folder = null,
            [FromQuery] string? mood = null,
            [FromQuery] string? vibe = null,
            [FromQuery] bool random = true)
        {
            // Determine which folder(s) to select from
            IEnumerable<MusicFolder> folders = MusicFolder.All;

            if (!string.IsNullOrWhiteSpace(folder))
            {
                // Specific folder requested
                if (!MusicFolder.IsValidFolder(folder))
                    return BadRequest(new { error = "Invalid folder name" });

                var specificFolder = MusicFolder.GetByName(folder);
                folders = new[] { specificFolder! };
            }
            else if (!string.IsNullOrWhiteSpace(mood))
            {
                // Filter by mood
                folders = MusicFolder.GetByMood(mood);
            }
            else if (!string.IsNullOrWhiteSpace(vibe))
            {
                // Filter by vibe
                folders = MusicFolder.GetByVibe(vibe);
            }

            var folderList = folders.ToList();
            if (folderList.Count == 0)
                return NotFound(new { error = "No folders match the criteria" });

            // Pick folder (random or first)
            var rng = new Random();
            var selectedFolder = random
                ? folderList[rng.Next(folderList.Count)]
                : folderList[0];

            // Get music files from folder
            string? musicKey = null;
            string? fileName = null;

            if (_s3 == null)
            {
                // Local filesystem
                var uploads = Path.Combine(AppContext.BaseDirectory, "uploads", selectedFolder.Name);
                if (Directory.Exists(uploads))
                {
                    var files = Directory.GetFiles(uploads, "*.mp3", SearchOption.AllDirectories)
                        .Concat(Directory.GetFiles(uploads, "*.wav", SearchOption.AllDirectories))
                        .Concat(Directory.GetFiles(uploads, "*.m4a", SearchOption.AllDirectories))
                        .Concat(Directory.GetFiles(uploads, "*.flac", SearchOption.AllDirectories))
                        .ToList();

                    if (files.Count > 0)
                    {
                        var selectedFile = random
                            ? files[rng.Next(files.Count)]
                            : files[0];

                        fileName = Path.GetFileName(selectedFile);
                        musicKey = $"music/{selectedFolder.Name}/{fileName}";
                    }
                }
            }
            else
            {
                // S3 - would need ListObjects implementation
                // For now, return error
                return StatusCode(501, new
                {
                    error = "S3 listing not implemented. Upload music via admin endpoints first."
                });
            }

            if (string.IsNullOrEmpty(musicKey))
                return NotFound(new { error = "No music available in selected folder" });

            // Generate one-time streaming token
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString()
                ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()
                ?? "unknown";

            var oapId = User?.Identity?.Name ?? "anonymous";
            var token = StreamingTokenStore.GenerateToken(musicKey, oapId, clientIp);

            // Return streaming URL with one-time token
            var streamUrl = $"{Request.Scheme}://{Request.Host}/api/oap/stream?token={token}";

            return Ok(new
            {
                folder = selectedFolder.DisplayName,
                folderKey = selectedFolder.Name,
                fileName = fileName,
                streamUrl = streamUrl,
                expiresIn = 30, // seconds
                warning = "This URL is one-time use only and expires in 30 seconds. Cannot be copied or reused.",
                metadata = new
                {
                    moods = selectedFolder.Moods,
                    vibes = selectedFolder.Vibes,
                    description = selectedFolder.Description
                }
            });
        }

        /// <summary>
        /// Secure streaming endpoint - proxies S3 content to browser
        /// Token is one-time use and IP-validated
        /// S3 presigned URLs are never exposed to the client
        /// </summary>
        [HttpGet("stream")]
        public async Task<IActionResult> SecureStream([FromQuery] string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(new { error = "Token required" });

            // Validate IP
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString()
                ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()
                ?? "unknown";

            // Validate and consume token (one-time use)
            if (!StreamingTokenStore.ValidateAndConsumeToken(token, clientIp, out var musicKey))
            {
                return Unauthorized(new
                {
                    error = "Invalid, expired, or already used token",
                    message = "Please request a new streaming token via /api/oap/get-stream-token"
                });
            }

            // Additional security: Check referer to prevent direct linking
            var referer = Request.Headers["Referer"].ToString();
            var host = Request.Host.ToString();
            if (!string.IsNullOrEmpty(referer) && !referer.Contains(host))
            {
                return Forbid();
            }

            // Stream the music file
            if (_s3 == null)
            {
                // Local filesystem
                var parts = musicKey!.Replace("music/", "").Split('/');
                if (parts.Length < 2)
                    return NotFound();

                var folderName = parts[0];
                var fileName = parts[^1];

                var filePath = Path.Combine(AppContext.BaseDirectory, "uploads", folderName, fileName);
                if (!System.IO.File.Exists(filePath))
                    return NotFound(new { error = "Music file not found" });

                var contentType = GetContentType(fileName);

                // Add security headers
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
                Response.Headers["Pragma"] = "no-cache";
                Response.Headers["Expires"] = "0";

                var fs = System.IO.File.OpenRead(filePath);
                return File(fs, contentType, enableRangeProcessing: true);
            }
            else
            {
                // S3 - proxy the presigned URL so client never sees it
                var presigned = await _s3.GetPresignedUrlAsync(musicKey!, TimeSpan.FromMinutes(5));

                // Forward Range header if present
                var req = new HttpRequestMessage(HttpMethod.Get, presigned);
                if (Request.Headers.TryGetValue("Range", out var range) && !string.IsNullOrEmpty(range))
                {
                    req.Headers.TryAddWithoutValidation("Range", range.ToString());
                }

                var upstream = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!upstream.IsSuccessStatusCode && upstream.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    return StatusCode((int)upstream.StatusCode);

                var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";

                // Add security headers
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
                Response.Headers["Pragma"] = "no-cache";
                Response.Headers["Expires"] = "0";
                Response.Headers["Accept-Ranges"] = "bytes";

                if (upstream.Content.Headers.ContentLength.HasValue)
                    Response.ContentLength = upstream.Content.Headers.ContentLength.Value;

                if (upstream.Content.Headers.ContentRange != null)
                    Response.Headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();

                Response.ContentType = contentType;
                Response.StatusCode = (int)upstream.StatusCode;

                var upstreamStream = await upstream.Content.ReadAsStreamAsync();
                await upstreamStream.CopyToAsync(Response.Body);
                return new EmptyResult();
            }
        }

        /// <summary>
        /// List available folders for OAP selection
        /// Returns folder metadata with moods and vibes
        /// </summary>
        [HttpGet("folders")]
        public IActionResult GetFolders([FromQuery] string? mood = null, [FromQuery] string? vibe = null)
        {
            IEnumerable<MusicFolder> folders = MusicFolder.All;

            if (!string.IsNullOrWhiteSpace(mood))
            {
                folders = MusicFolder.GetByMood(mood);
            }
            else if (!string.IsNullOrWhiteSpace(vibe))
            {
                folders = MusicFolder.GetByVibe(vibe);
            }

            return Ok(new
            {
                totalFolders = folders.Count(),
                folders = folders.Select(f => new
                {
                    name = f.Name,
                    displayName = f.DisplayName,
                    description = f.Description,
                    moods = f.Moods,
                    vibes = f.Vibes
                }).ToArray()
            });
        }

        /// <summary>
        /// Get available moods for OAP filtering
        /// </summary>
        [HttpGet("moods")]
        public IActionResult GetMoods()
        {
            var moods = MusicFolder.All
                .SelectMany(f => f.Moods)
                .Distinct()
                .OrderBy(m => m)
                .ToArray();

            return Ok(new { totalMoods = moods.Length, moods });
        }

        /// <summary>
        /// Get available vibes for OAP filtering
        /// </summary>
        [HttpGet("vibes")]
        public IActionResult GetVibes()
        {
            var vibes = MusicFolder.All
                .SelectMany(f => f.Vibes)
                .Distinct()
                .OrderBy(v => v)
                .ToArray();

            return Ok(new { totalVibes = vibes.Length, vibes });
        }

        private static string GetContentType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".m4a" => "audio/mp4",
                ".flac" => "audio/flac",
                _ => "application/octet-stream"
            };
        }
    }
}
