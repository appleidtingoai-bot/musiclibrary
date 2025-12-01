using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace MusicAI.Orchestrator.Services
{
    /// <summary>
    /// Manages persistent playback queues for cross-device sync (Spotify-like feature)
    /// </summary>
    public interface IQueueService
    {
        Task<QueueState?> GetQueueAsync(string userId);
        Task SetQueueAsync(string userId, QueueState state);
        Task UpdatePositionAsync(string userId, int trackIndex, double positionSeconds);
        Task ClearQueueAsync(string userId);
    }

    public class QueueService : IQueueService
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<QueueService> _logger;
        private const int QUEUE_TTL_HOURS = 168; // 7 days

        public QueueService(IDistributedCache cache, ILogger<QueueService> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public async Task<QueueState?> GetQueueAsync(string userId)
        {
            try
            {
                var key = $"queue:{userId}";
                var json = await _cache.GetStringAsync(key);
                
                if (string.IsNullOrEmpty(json))
                    return null;

                return JsonSerializer.Deserialize<QueueState>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get queue for user {UserId}", userId);
                return null;
            }
        }

        public async Task SetQueueAsync(string userId, QueueState state)
        {
            try
            {
                var key = $"queue:{userId}";
                state.UpdatedAt = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(state);
                
                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(QUEUE_TTL_HOURS)
                };

                await _cache.SetStringAsync(key, json, options);
                _logger.LogInformation("Queue saved for user {UserId}: {TrackCount} tracks", 
                    userId, state.Tracks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set queue for user {UserId}", userId);
            }
        }

        public async Task UpdatePositionAsync(string userId, int trackIndex, double positionSeconds)
        {
            try
            {
                var queue = await GetQueueAsync(userId);
                if (queue == null)
                {
                    _logger.LogWarning("Cannot update position: no queue found for user {UserId}", userId);
                    return;
                }

                queue.CurrentTrackIndex = trackIndex;
                queue.CurrentPositionSeconds = positionSeconds;
                await SetQueueAsync(userId, queue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update position for user {UserId}", userId);
            }
        }

        public async Task ClearQueueAsync(string userId)
        {
            try
            {
                var key = $"queue:{userId}";
                await _cache.RemoveAsync(key);
                _logger.LogInformation("Queue cleared for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear queue for user {UserId}", userId);
            }
        }
    }

    public class QueueState
    {
        public List<QueueTrack> Tracks { get; set; } = new();
        public int CurrentTrackIndex { get; set; } = 0;
        public double CurrentPositionSeconds { get; set; } = 0;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string OapId { get; set; } = string.Empty;
        public string PlaylistId { get; set; } = string.Empty;
    }

    public class QueueTrack
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string S3Key { get; set; } = string.Empty;
        public string? Genre { get; set; }
        public int? DurationSeconds { get; set; }
    }
}
