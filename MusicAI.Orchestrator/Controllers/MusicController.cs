using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MusicAI.Infrastructure.Services;
using MusicAI.Orchestrator.Data;
using MusicAI.Orchestrator.Services;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MusicController : ControllerBase
    {
        private readonly MusicRepository? _musicRepo;
        private readonly IS3Service? _s3Service;
        private readonly AdminsRepository _adminsRepo;
        private static readonly string[] AllowedExtensions = { ".mp3", ".wav", ".flac", ".m4a", ".aac" };
        private const long MaxFileSizeBytes = 52428800; // 50MB
        
        public MusicController(
            IS3Service? s3Service,
            AdminsRepository adminsRepo,
            MusicRepository? musicRepo = null)
        {
            _musicRepo = musicRepo;
            _s3Service = s3Service;
            _adminsRepo = adminsRepo;
        }
        
        private bool IsAdminAuthorized(out string? adminEmail)
        {
            adminEmail = null;
            
            // Check JWT role
            if (User?.Identity?.IsAuthenticated == true && User.IsInRole("Admin"))
            {
                adminEmail = User.Identity.Name ?? User.FindFirst("sub")?.Value;
                return !string.IsNullOrEmpty(adminEmail);
            }
            
            // Check X-Admin-Token header
            if (Request.Headers.TryGetValue("X-Admin-Token", out var token) && !string.IsNullOrEmpty(token))
            {
                if (AdminSessionStore.ValidateSession(token.ToString(), out var email))
                {
                    adminEmail = email;
                    return true;
                }
            }
            
            return false;
        }
        
        [HttpPost("upload")]
        public async Task<IActionResult> UploadMusic([FromForm] IFormFile file, 
            [FromForm] string title,
            [FromForm] string artist,
            [FromForm] string genre,
            [FromForm] string? album = null,
            [FromForm] string? mood = null)
        {
            if (!IsAdminAuthorized(out var adminEmail))
                return Unauthorized(new { error = "Admin access required" });
            
            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file provided" });
            
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(genre))
                return BadRequest(new { error = "Title, artist, and genre are required" });
            
            // Validate file extension
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(extension))
                return BadRequest(new { error = $"Invalid file type. Allowed: {string.Join(", ", AllowedExtensions)}" });
            
            // Validate file size
            if (file.Length > MaxFileSizeBytes)
                return BadRequest(new { error = $"File too large. Max size: {MaxFileSizeBytes / 1024 / 1024}MB" });
            
            try
            {
                var trackId = Guid.NewGuid().ToString("N");
                var s3Key = $"music/{genre.ToLower()}/{trackId}_{file.FileName}";
                var s3Bucket = _s3Service != null
                    ? (Environment.GetEnvironmentVariable("AWS__S3Bucket") ?? "tingomusiclibrary")
                    : "local";

                // Upload to S3/R2 or save locally
                if (_s3Service != null)
                {
                    // Copy into memory to avoid callers disposing the original stream.
                    await using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    ms.Position = 0;
                    await using var uploadStream = new MemoryStream(ms.ToArray());
                    uploadStream.Position = 0;
                    await _s3Service.UploadFileAsync(s3Key, uploadStream, file.ContentType ?? "audio/mpeg");
                }
                else
                {
                    // Local fallback
                    var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads", genre.ToLower());
                    Directory.CreateDirectory(uploadsDir);
                    var localPath = Path.Combine(uploadsDir, $"{trackId}_{file.FileName}");
                    
                    await using var fileStream = new FileStream(localPath, FileMode.Create);
                    await file.CopyToAsync(fileStream);
                }
                
                // Save metadata to database
                var track = new MusicTrack
                {
                     Id = trackId,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    Genre = genre.ToLower(),
                    Mood = mood?.ToLower(),
                    S3Key = s3Key,
                    S3Bucket = s3Bucket,
                    FileSizeBytes = file.Length,
                    ContentType = file.ContentType ?? "audio/mpeg",
                    UploadedBy = adminEmail,
                    UploadedAt = DateTime.UtcNow,
                    IsActive = true
                };
                
                await _musicRepo.CreateAsync(track);
                
                return Ok(new
                {
                    success = true,
                    trackId = track.Id,
                    title = track.Title,
                    artist = track.Artist,
                    genre = track.Genre,
                    s3Key = track.S3Key,
                    message = "Music uploaded successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Upload failed: {ex.Message}" });
            }
        }
        
        [HttpGet("tracks")]
        public async Task<IActionResult> GetTracks(
            [FromQuery] string? genre = null,
            [FromQuery] string? mood = null,
            [FromQuery] string? artist = null,
            [FromQuery] int page = 1,
            [FromQuery] int limit = 50)
        {
            if (!IsAdminAuthorized(out _))
                return Unauthorized(new { error = "Admin access required" });
            
            try
            {
                var offset = (page - 1) * limit;
                
                List<MusicTrack> tracks;
                if (!string.IsNullOrWhiteSpace(mood) || !string.IsNullOrWhiteSpace(genre) || !string.IsNullOrWhiteSpace(artist))
                {
                    tracks = await _musicRepo.SearchAsync(mood, genre, artist, limit);
                }
                else
                {
                    tracks = await _musicRepo.GetAllAsync(offset, limit);
                }
                
                var results = tracks.Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    artist = t.Artist,
                    album = t.Album,
                    genre = t.Genre,
                    mood = t.Mood,
                    durationSeconds = t.DurationSeconds,
                    uploadedAt = t.UploadedAt,
                    playCount = t.PlayCount
                });
                
                return Ok(new { count = results.Count(), tracks = results });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
        
        [HttpGet("tracks/{id}")]
        public async Task<IActionResult> GetTrack(string id)
        {
            if (!IsAdminAuthorized(out _))
                return Unauthorized(new { error = "Admin access required" });
            
            var track = await _musicRepo.GetByIdAsync(id);
            if (track == null)
                return NotFound(new { error = "Track not found" });
            
            return Ok(track);
        }
        
        [HttpPut("tracks/{id}")]
        public async Task<IActionResult> UpdateTrack(string id,
            [FromBody] UpdateTrackRequest request)
        {
            if (!IsAdminAuthorized(out _))
                return Unauthorized(new { error = "Admin access required" });
            
            var track = await _musicRepo.GetByIdAsync(id);
            if (track == null)
                return NotFound(new { error = "Track not found" });
            
            await _musicRepo.UpdateMetadataAsync(id, request.Title, request.Artist, 
                request.Album, request.Genre, request.Mood);
            
            return Ok(new { success = true, message = "Track updated successfully" });
        }
        
        [HttpDelete("tracks/{id}")]
        public async Task<IActionResult> DeleteTrack(string id)
        {
            if (!IsAdminAuthorized(out var adminEmail))
                return Unauthorized(new { error = "Admin access required" });
            
            // Check if super admin (for actual deletion)
            var admin = _adminsRepo.GetByEmail(adminEmail!);
            if (admin == null || !admin.IsSuperAdmin)
                return Forbid();
            
            var track = await _musicRepo.GetByIdAsync(id);
            if (track == null)
                return NotFound(new { error = "Track not found" });
            
            await _musicRepo.DeleteAsync(id);
            
            // Optionally delete from S3
            if (_s3Service != null && track.S3Bucket != "local")
            {
                try
                {
                    await _s3Service.DeleteObjectAsync(track.S3Key);
                }
                catch
                {
                    // Continue even if S3 deletion fails
                }
            }
            
            return Ok(new { success = true, message = "Track deleted successfully" });
        }
        
        public record UpdateTrackRequest(
            string? Title = null,
            string? Artist = null,
            string? Album = null,
            string? Genre = null,
            string? Mood = null);
    }
}
