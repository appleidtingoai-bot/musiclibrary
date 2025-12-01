using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Services;

namespace MusicAI.Orchestrator.Controllers
{
    /// <summary>
    /// Queue management for cross-device playback sync (Spotify-like feature)
    /// </summary>
    [ApiController]
    [Route("api/queue")]
    [Authorize(Policy = "UserOrAdmin")]
    public class QueueController : ControllerBase
    {
        private readonly IQueueService _queueService;
        private readonly ILogger<QueueController> _logger;

        public QueueController(IQueueService queueService, ILogger<QueueController> logger)
        {
            _queueService = queueService;
            _logger = logger;
        }

        /// <summary>
        /// Get user's current queue state (tracks, position, current track)
        /// Enables resuming playback across devices
        /// </summary>
        [HttpGet("{userId}")]
        public async Task<IActionResult> GetQueue(string userId)
        {
            // Verify user can only access their own queue
            var authenticatedUserId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            if (authenticatedUserId != userId && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            var queue = await _queueService.GetQueueAsync(userId);
            
            if (queue == null)
            {
                return Ok(new
                {
                    exists = false,
                    message = "No active queue"
                });
            }

            return Ok(new
            {
                exists = true,
                tracks = queue.Tracks,
                currentTrackIndex = queue.CurrentTrackIndex,
                currentPositionSeconds = queue.CurrentPositionSeconds,
                updatedAt = queue.UpdatedAt,
                oapId = queue.OapId,
                playlistId = queue.PlaylistId
            });
        }

        /// <summary>
        /// Set or replace user's queue with new playlist
        /// Called when user starts playing a new playlist
        /// </summary>
        [HttpPost("{userId}")]
        public async Task<IActionResult> SetQueue(string userId, [FromBody] SetQueueRequest request)
        {
            var authenticatedUserId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            if (authenticatedUserId != userId && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            if (request.Tracks == null || !request.Tracks.Any())
            {
                return BadRequest(new { error = "Tracks list cannot be empty" });
            }

            var queueState = new QueueState
            {
                Tracks = request.Tracks.Select(t => new QueueTrack
                {
                    Id = t.Id,
                    Title = t.Title,
                    Artist = t.Artist,
                    S3Key = t.S3Key,
                    Genre = t.Genre,
                    DurationSeconds = t.DurationSeconds
                }).ToList(),
                CurrentTrackIndex = request.StartIndex ?? 0,
                CurrentPositionSeconds = 0,
                OapId = request.OapId ?? "unknown",
                PlaylistId = request.PlaylistId ?? Guid.NewGuid().ToString()
            };

            await _queueService.SetQueueAsync(userId, queueState);

            _logger.LogInformation("Queue set for user {UserId}: {Count} tracks", userId, queueState.Tracks.Count);

            return Ok(new
            {
                success = true,
                trackCount = queueState.Tracks.Count,
                playlistId = queueState.PlaylistId
            });
        }

        /// <summary>
        /// Update current playback position
        /// Called periodically by frontend to sync position across devices
        /// </summary>
        [HttpPost("{userId}/position")]
        public async Task<IActionResult> UpdatePosition(string userId, [FromBody] UpdatePositionRequest request)
        {
            var authenticatedUserId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            if (authenticatedUserId != userId && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            await _queueService.UpdatePositionAsync(userId, request.TrackIndex, request.PositionSeconds);

            return Ok(new { success = true });
        }

        /// <summary>
        /// Clear user's queue
        /// </summary>
        [HttpDelete("{userId}")]
        public async Task<IActionResult> ClearQueue(string userId)
        {
            var authenticatedUserId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            if (authenticatedUserId != userId && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            await _queueService.ClearQueueAsync(userId);

            return Ok(new { success = true });
        }

        #region DTOs

        public class SetQueueRequest
        {
            public List<QueueTrackDto> Tracks { get; set; } = new();
            public int? StartIndex { get; set; }
            public string? OapId { get; set; }
            public string? PlaylistId { get; set; }
        }

        public class QueueTrackDto
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public string S3Key { get; set; } = string.Empty;
            public string? Genre { get; set; }
            public int? DurationSeconds { get; set; }
        }

        public class UpdatePositionRequest
        {
            public int TrackIndex { get; set; }
            public double PositionSeconds { get; set; }
        }

        #endregion
    }
}
