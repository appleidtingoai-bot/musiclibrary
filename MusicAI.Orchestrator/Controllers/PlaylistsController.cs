using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Data;

namespace MusicAI.Orchestrator.Controllers
{
    /// <summary>
    /// Collaborative playlists management (Spotify-like feature)
    /// Users can create shared playlists and add collaborators
    /// </summary>
    [ApiController]
    [Route("api/playlists")]
    [Authorize(Policy = "UserOrAdmin")]
    public class PlaylistsController : ControllerBase
    {
        private readonly IPlaylistsRepository _playlistsRepo;
        private readonly ILogger<PlaylistsController> _logger;

        public PlaylistsController(IPlaylistsRepository playlistsRepo, ILogger<PlaylistsController> logger)
        {
            _playlistsRepo = playlistsRepo;
            _logger = logger;
        }

        /// <summary>
        /// Create a new playlist
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreatePlaylist([FromBody] CollaborativePlaylistRequest request)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            if (string.IsNullOrEmpty(request.Name))
            {
                return BadRequest(new { error = "Playlist name is required" });
            }

            var playlistId = await _playlistsRepo.CreatePlaylistAsync(
                userId,
                request.Name,
                request.Description,
                request.IsCollaborative ?? false
            );

            _logger.LogInformation("Playlist created: {PlaylistId} by user {UserId}", playlistId, userId);

            return Ok(new
            {
                playlistId,
                name = request.Name,
                isCollaborative = request.IsCollaborative ?? false
            });
        }

        /// <summary>
        /// Get user's playlists (owned + collaborative)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPlaylists()
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var playlists = await _playlistsRepo.GetUserPlaylistsAsync(userId);

            return Ok(new
            {
                playlists = playlists.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    description = p.Description,
                    isCollaborative = p.IsCollaborative,
                    isOwner = p.OwnerId == userId,
                    createdAt = p.CreatedAt,
                    updatedAt = p.UpdatedAt
                }),
                totalCount = playlists.Count
            });
        }

        /// <summary>
        /// Get specific playlist details
        /// </summary>
        [HttpGet("{playlistId}")]
        public async Task<IActionResult> GetPlaylist(string playlistId)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var playlist = await _playlistsRepo.GetPlaylistAsync(playlistId);
            
            if (playlist == null)
            {
                return NotFound(new { error = "Playlist not found" });
            }

            // Check if user has access
            var isCollaborator = await _playlistsRepo.IsCollaboratorAsync(playlistId, userId);
            
            if (!isCollaborator && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            var tracks = await _playlistsRepo.GetPlaylistTracksAsync(playlistId);
            var collaborators = await _playlistsRepo.GetCollaboratorsAsync(playlistId);

            return Ok(new
            {
                id = playlist.Id,
                name = playlist.Name,
                description = playlist.Description,
                isCollaborative = playlist.IsCollaborative,
                isOwner = playlist.OwnerId == userId,
                tracks = tracks.Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    artist = t.Artist,
                    s3Key = t.S3Key,
                    addedBy = t.AddedByName ?? "Unknown",
                    addedAt = t.AddedAt,
                    position = t.Position
                }),
                collaborators = collaborators.Select(c => new
                {
                    id = c.Id,
                    email = c.Email,
                    name = c.FullName ?? c.Email,
                    addedAt = c.AddedAt
                }),
                createdAt = playlist.CreatedAt,
                updatedAt = playlist.UpdatedAt
            });
        }

        /// <summary>
        /// Add a track to playlist
        /// Only owner and collaborators can add tracks
        /// </summary>
        [HttpPost("{playlistId}/tracks")]
        public async Task<IActionResult> AddTrack(string playlistId, [FromBody] AddTrackRequest request)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            // Verify user is owner or collaborator
            var isCollaborator = await _playlistsRepo.IsCollaboratorAsync(playlistId, userId);
            
            if (!isCollaborator && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            if (string.IsNullOrEmpty(request.S3Key) || string.IsNullOrEmpty(request.Title))
            {
                return BadRequest(new { error = "s3Key and title are required" });
            }

            var success = await _playlistsRepo.AddTrackAsync(
                playlistId,
                request.S3Key,
                request.Title,
                request.Artist ?? "Unknown",
                userId
            );

            if (!success)
            {
                return StatusCode(500, new { error = "Failed to add track" });
            }

            _logger.LogInformation("Track added to playlist {PlaylistId} by user {UserId}", playlistId, userId);

            return Ok(new { success = true });
        }

        /// <summary>
        /// Remove a track from playlist
        /// Only owner and track adder can remove
        /// </summary>
        [HttpDelete("{playlistId}/tracks/{trackId}")]
        public async Task<IActionResult> RemoveTrack(string playlistId, string trackId)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var playlist = await _playlistsRepo.GetPlaylistAsync(playlistId);
            
            if (playlist == null)
            {
                return NotFound(new { error = "Playlist not found" });
            }

            // Only owner can remove tracks (or admin)
            if (playlist.OwnerId != userId && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            var success = await _playlistsRepo.RemoveTrackAsync(playlistId, trackId);

            if (!success)
            {
                return NotFound(new { error = "Track not found" });
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// Add collaborator to playlist
        /// Only owner can add collaborators
        /// </summary>
        [HttpPost("{playlistId}/collaborators")]
        public async Task<IActionResult> AddCollaborator(string playlistId, [FromBody] AddCollaboratorRequest request)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var playlist = await _playlistsRepo.GetPlaylistAsync(playlistId);
            
            if (playlist == null)
            {
                return NotFound(new { error = "Playlist not found" });
            }

            // Only owner can add collaborators
            if (playlist.OwnerId != userId && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            if (!playlist.IsCollaborative)
            {
                return BadRequest(new { error = "Playlist is not collaborative" });
            }

            if (string.IsNullOrEmpty(request.UserId))
            {
                return BadRequest(new { error = "userId is required" });
            }

            var success = await _playlistsRepo.AddCollaboratorAsync(playlistId, request.UserId);

            if (!success)
            {
                return StatusCode(500, new { error = "Failed to add collaborator" });
            }

            _logger.LogInformation("Collaborator {CollaboratorId} added to playlist {PlaylistId}", request.UserId, playlistId);

            return Ok(new { success = true });
        }

        /// <summary>
        /// Remove collaborator from playlist
        /// Only owner can remove collaborators
        /// </summary>
        [HttpDelete("{playlistId}/collaborators/{collaboratorId}")]
        public async Task<IActionResult> RemoveCollaborator(string playlistId, string collaboratorId)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var playlist = await _playlistsRepo.GetPlaylistAsync(playlistId);
            
            if (playlist == null)
            {
                return NotFound(new { error = "Playlist not found" });
            }

            // Only owner can remove collaborators
            if (playlist.OwnerId != userId && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            var success = await _playlistsRepo.RemoveCollaboratorAsync(playlistId, collaboratorId);

            if (!success)
            {
                return NotFound(new { error = "Collaborator not found" });
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// Get playlist collaborators
        /// </summary>
        [HttpGet("{playlistId}/collaborators")]
        public async Task<IActionResult> GetCollaborators(string playlistId)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            // Verify user has access to playlist
            var isCollaborator = await _playlistsRepo.IsCollaboratorAsync(playlistId, userId);
            
            if (!isCollaborator && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            var collaborators = await _playlistsRepo.GetCollaboratorsAsync(playlistId);

            return Ok(new
            {
                collaborators = collaborators.Select(c => new
                {
                    id = c.Id,
                    email = c.Email,
                    name = c.FullName ?? c.Email,
                    addedAt = c.AddedAt
                }),
                totalCount = collaborators.Count
            });
        }

        #region DTOs

        public class CollaborativePlaylistRequest
        {
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public bool? IsCollaborative { get; set; }
        }

        public class AddTrackRequest
        {
            public string S3Key { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string? Artist { get; set; }
        }

        public class AddCollaboratorRequest
        {
            public string UserId { get; set; } = string.Empty;
        }

        #endregion
    }
}
