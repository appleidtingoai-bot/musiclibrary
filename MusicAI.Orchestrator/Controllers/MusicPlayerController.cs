using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using MusicAI.Orchestrator.Data;

namespace MusicAI.Orchestrator.Controllers
{
    /// <summary>
    /// Spotify-level music player API with queue management, shuffle, auto-play
    /// </summary>
    [ApiController]
    [Route("api/player")]
    public class MusicPlayerController : ControllerBase
    {
        private readonly AdminsRepository _adminsRepo;

        public MusicPlayerController(AdminsRepository adminsRepo)
        {
            _adminsRepo = adminsRepo;
        }

        private bool IsAdminAuthorized(out string? adminEmail, out bool isSuper)
        {
            adminEmail = null;
            isSuper = false;

            // JWT validation (same as AdminController)
            if (Request.Headers.TryGetValue("Authorization", out var auth) && !string.IsNullOrEmpty(auth))
            {
                var header = auth.ToString();
                var jwt = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? header.Substring("Bearer ".Length).Trim()
                    : header.Trim();

                if (!string.IsNullOrEmpty(jwt) && jwt.Contains("."))
                {
                    try
                    {
                        var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_jwt_secret_minimum_32_characters_required_for_hs256";
                        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
                        var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                        var validationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                        {
                            ValidateIssuer = false,
                            ValidateAudience = false,
                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = key,
                            ClockSkew = TimeSpan.FromSeconds(30)
                        };

                        var principal = tokenHandler.ValidateToken(jwt, validationParameters, out _);
                        var email = principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
                        if (!string.IsNullOrEmpty(email))
                        {
                            var rec = _adminsRepo.GetByEmail(email);
                            if (rec != null && rec.Approved)
                            {
                                adminEmail = email;
                                isSuper = rec.IsSuperAdmin;
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }

            return false;
        }

        /// <summary>
        /// Create a dynamic playlist with shuffle and auto-play queue
        /// </summary>
        [HttpPost("playlist/create")]
        public IActionResult CreatePlaylist([FromBody] CreatePlaylistRequest request)
        {
            if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
                return Unauthorized(new { error = "admin required" });

            try
            {
                var tracks = GetAllTracks(request.Folder, request.Search);

                if (request.Shuffle)
                {
                    var rng = new Random();
                    tracks = tracks.OrderBy(_ => rng.Next()).ToList();
                }

                var playlistId = Guid.NewGuid().ToString("N");
                var queue = tracks.Take(request.InitialQueueSize ?? 50).ToList();

                return Ok(new
                {
                    playlistId,
                    totalTracks = tracks.Count,
                    queueSize = queue.Count,
                    shuffle = request.Shuffle,
                    autoPlay = true,
                    queue,
                    currentIndex = 0
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get next batch of tracks for seamless auto-play (Spotify-like)
        /// </summary>
        [HttpGet("queue/next")]
        public IActionResult GetNextInQueue(
            [FromQuery] string? currentTrackId = null,
            [FromQuery] string? folder = null,
            [FromQuery] int count = 20,
            [FromQuery] bool shuffle = true)
        {
            if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
                return Unauthorized(new { error = "admin required" });

            try
            {
                var tracks = GetAllTracks(folder);

                // Remove current track
                if (!string.IsNullOrEmpty(currentTrackId))
                {
                    tracks = tracks.Where(t => t.id != currentTrackId).ToList();
                }

                // Shuffle for variety
                if (shuffle)
                {
                    var rng = new Random();
                    tracks = tracks.OrderBy(_ => rng.Next()).ToList();
                }

                var nextQueue = tracks.Take(count).ToList();

                return Ok(new
                {
                    queueSize = nextQueue.Count,
                    shuffle,
                    autoPlayEnabled = true,
                    tracks = nextQueue,
                    hasMore = tracks.Count > count
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get music by mood/vibe for contextual playback
        /// </summary>
        [HttpGet("discover")]
        public IActionResult Discover(
            [FromQuery] string? mood = null,
            [FromQuery] int limit = 30,
            [FromQuery] bool shuffle = true)
        {
            if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
                return Unauthorized(new { error = "admin required" });

            try
            {
                var tracks = GetAllTracks(mood);

                if (shuffle)
                {
                    var rng = new Random();
                    tracks = tracks.OrderBy(_ => rng.Next()).ToList();
                }

                var discovered = tracks.Take(limit).ToList();

                return Ok(new
                {
                    mood,
                    totalFound = tracks.Count,
                    returned = discovered.Count,
                    shuffle,
                    tracks = discovered
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Shuffle all tracks (Spotify-like shuffle play)
        /// </summary>
        [HttpGet("shuffle-all")]
        public IActionResult ShuffleAll([FromQuery] int limit = 100)
        {
            if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
                return Unauthorized(new { error = "admin required" });

            try
            {
                var tracks = GetAllTracks();
                var rng = new Random();
                var shuffled = tracks.OrderBy(_ => rng.Next()).Take(limit).ToList();

                return Ok(new
                {
                    totalTracks = tracks.Count,
                    queueSize = shuffled.Count,
                    shuffle = true,
                    autoPlay = true,
                    queue = shuffled
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private List<dynamic> GetAllTracks(string? folder = null, string? search = null)
        {
            var tracks = new List<dynamic>();
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");

            if (!Directory.Exists(uploadsDir))
                return tracks;

            var folders = string.IsNullOrEmpty(folder)
                ? Directory.GetDirectories(uploadsDir)
                : new[] { Path.Combine(uploadsDir, folder) };

            foreach (var dir in folders.Where(Directory.Exists))
            {
                var folderName = Path.GetFileName(dir);
                var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => new[] { ".mp3", ".m4a", ".wav", ".flac", ".aac", ".ogg" }
                        .Contains(Path.GetExtension(f).ToLower()));

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    
                    // Search filter
                    if (!string.IsNullOrEmpty(search) && 
                        !fileName.Contains(search, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var key = $"music/{folderName}/{fileName}";
                    var trackId = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
                    var streamUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-proxy?key={Uri.EscapeDataString(key)}";
                    var hlsUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-hls/{key}";

                    tracks.Add(new
                    {
                        id = trackId,
                        title = Path.GetFileNameWithoutExtension(fileName),
                        artist = folderName, // Use folder as artist/genre
                        album = folderName,
                        fileName = fileName,
                        folder = folderName,
                        key = key,
                        streamUrl = streamUrl,
                        hlsUrl = hlsUrl,
                        duration = 0, // TODO: Extract from metadata
                        size = new FileInfo(file).Length,
                        format = Path.GetExtension(file).TrimStart('.')
                    });
                }
            }

            return tracks;
        }

        public class CreatePlaylistRequest
        {
            public string? Folder { get; set; }
            public string? Search { get; set; }
            public bool Shuffle { get; set; } = true;
            public int? InitialQueueSize { get; set; } = 50;
        }
    }
}
