using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using MusicAI.Orchestrator.Services;

namespace MusicAI.Orchestrator.Controllers
{
    /// <summary>
    /// Offline download management (Spotify-like feature)
    /// Users can download tracks for offline playback
    /// </summary>
    [ApiController]
    [Route("api/downloads")]
    [Authorize(Policy = "UserOrAdmin")]
    public class DownloadsController : ControllerBase
    {
        private readonly IDistributedCache _cache;
        private readonly IStreamTokenService _tokenService;
        private readonly ILogger<DownloadsController> _logger;
     private readonly MusicAI.Orchestrator.Data.MusicRepository? _musicRepo;
     private readonly MusicAI.Orchestrator.Data.SubscriptionsRepository? _subsRepo;

        public DownloadsController(
            IDistributedCache cache,
            IStreamTokenService tokenService,
            ILogger<DownloadsController> logger,
            MusicAI.Orchestrator.Data.MusicRepository? musicRepo = null,
            MusicAI.Orchestrator.Data.SubscriptionsRepository? subsRepo = null)
        {
            _cache = cache;
            _tokenService = tokenService;
            _logger = logger;
            _musicRepo = musicRepo;
            _subsRepo = subsRepo;
        }

        /// <summary>
        /// Request offline download for a track
        /// Returns a long-lived token (24 hours) for downloading
        /// </summary>
        [HttpPost("request")]
        public async Task<IActionResult> RequestDownload([FromBody] DownloadRequest request)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            // Enforce subscription-only downloads
            try
            {
                if (_subsRepo == null)
                {
                    _logger.LogWarning("SubscriptionsRepository not available - denying download request for user {UserId}", userId);
                    return Forbid("Subscription required to download tracks");
                }

                var subscription = await _subsRepo.GetActiveSubscriptionAsync(userId);
                if (subscription == null)
                {
                    _logger.LogInformation("User {UserId} attempted download without active subscription", userId);
                    return Forbid("Subscription required to download tracks");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking subscription for user {UserId}", userId);
                return StatusCode(500, new { error = "Unable to verify subscription status" });
            }

            if (string.IsNullOrEmpty(request.S3Key))
            {
                return BadRequest(new { error = "s3Key is required" });
            }

            // Generate long-lived token (24 hours) for offline download
            var allowExplicitClaim = User?.FindFirst("allow_explicit")?.Value;
            var allowExplicit = allowExplicitClaim == "1" || string.Equals(allowExplicitClaim, "true", StringComparison.OrdinalIgnoreCase);
            var effectiveKey = request.S3Key; // DownloadRequest.S3Key is initialized to empty string by DTO
            try
            {
                if (!allowExplicit && _musicRepo != null && !string.IsNullOrEmpty(request.S3Key))
                {
                    var maybe = await _musicRepo.GetByS3KeyAsync(request.S3Key);
                    if (maybe != null && maybe.HasCleanVariant && !string.IsNullOrEmpty(maybe.CleanS3Key))
                    {
                        effectiveKey = maybe.CleanS3Key ?? effectiveKey;
                    }
                }
            }
            catch { }

            var token = _tokenService.GenerateToken(effectiveKey ?? string.Empty, TimeSpan.FromHours(24), allowExplicit);
            
            // Store download record
            var downloadId = Guid.NewGuid().ToString();
            var downloadRecord = new DownloadRecord
            {
                Id = downloadId,
                UserId = userId,
                    S3Key = effectiveKey,
                Title = request.Title ?? "Unknown",
                Artist = request.Artist ?? "Unknown",
                RequestedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(24),
                Token = token
            };

            // Save to cache (expires in 30 days)
            var cacheKey = $"download:{userId}:{downloadId}";
            var json = JsonSerializer.Serialize(downloadRecord);
            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30)
            };
            await _cache.SetStringAsync(cacheKey, json, cacheOptions);

            // Also add to user's download list
            await AddToUserDownloadsList(userId, downloadId);

            _logger.LogInformation("Download requested for user {UserId}: {S3Key}", userId, request.S3Key);

            return Ok(new
            {
                downloadId,
                downloadUrl = $"{Request.Scheme}://{Request.Host}/api/music/stream/{effectiveKey}?t={System.Net.WebUtility.UrlEncode(token)}",
                expiresAt = downloadRecord.ExpiresAt,
                validForHours = 24
            });
        }

        /// <summary>
        /// Get all downloads for the authenticated user
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDownloads()
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var downloadIds = await GetUserDownloadsList(userId);
            var downloads = new List<DownloadRecord>();

            foreach (var downloadId in downloadIds)
            {
                var cacheKey = $"download:{userId}:{downloadId}";
                var json = await _cache.GetStringAsync(cacheKey);
                
                if (!string.IsNullOrEmpty(json))
                {
                    var record = JsonSerializer.Deserialize<DownloadRecord>(json);
                    if (record != null && record.ExpiresAt > DateTime.UtcNow)
                    {
                        downloads.Add(record);
                    }
                }
            }

            return Ok(new
            {
                downloads = downloads.OrderByDescending(d => d.RequestedAt).Select(d => new
                {
                    id = d.Id,
                    title = d.Title,
                    artist = d.Artist,
                    requestedAt = d.RequestedAt,
                    expiresAt = d.ExpiresAt,
                    downloadUrl = $"{Request.Scheme}://{Request.Host}/api/music/stream/{d.S3Key}?t={System.Net.WebUtility.UrlEncode(d.Token)}"
                }),
                totalCount = downloads.Count
            });
        }

        /// <summary>
        /// Delete a download record
        /// </summary>
        [HttpDelete("{downloadId}")]
        public async Task<IActionResult> DeleteDownload(string downloadId)
        {
            var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("userId")?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var cacheKey = $"download:{userId}:{downloadId}";
            await _cache.RemoveAsync(cacheKey);

            // Remove from user's list
            await RemoveFromUserDownloadsList(userId, downloadId);

            _logger.LogInformation("Download deleted: {DownloadId} for user {UserId}", downloadId, userId);

            return Ok(new { success = true });
        }

        #region Helper Methods

        private async Task AddToUserDownloadsList(string userId, string downloadId)
        {
            var listKey = $"downloads_list:{userId}";
            var json = await _cache.GetStringAsync(listKey);
            
            var list = string.IsNullOrEmpty(json)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

            if (!list.Contains(downloadId))
            {
                list.Add(downloadId);
            }

            var updatedJson = JsonSerializer.Serialize(list);
            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30)
            };
            await _cache.SetStringAsync(listKey, updatedJson, cacheOptions);
        }

        private async Task<List<string>> GetUserDownloadsList(string userId)
        {
            var listKey = $"downloads_list:{userId}";
            var json = await _cache.GetStringAsync(listKey);
            
            return string.IsNullOrEmpty(json)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }

        private async Task RemoveFromUserDownloadsList(string userId, string downloadId)
        {
            var listKey = $"downloads_list:{userId}";
            var json = await _cache.GetStringAsync(listKey);
            
            if (string.IsNullOrEmpty(json))
                return;

            var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            list.Remove(downloadId);

            var updatedJson = JsonSerializer.Serialize(list);
            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30)
            };
            await _cache.SetStringAsync(listKey, updatedJson, cacheOptions);
        }

        #endregion

        #region DTOs

        public class DownloadRequest
        {
            public string S3Key { get; set; } = string.Empty;
            public string? Title { get; set; }
            public string? Artist { get; set; }
        }

        public class DownloadRecord
        {
            public string Id { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public string S3Key { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public DateTime RequestedAt { get; set; }
            public DateTime ExpiresAt { get; set; }
            public string Token { get; set; } = string.Empty;
        }

        #endregion
    }
}
