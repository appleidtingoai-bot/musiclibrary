using System;
using System.Collections.Generic;
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
    public class RadioController : ControllerBase
    {
        private readonly MusicRepository? _musicRepo;
        private readonly OapSchedulingService? _oapService;
        private readonly IS3Service? _s3Service;
        private readonly SubscriptionsRepository _subsRepo;
        private readonly UsersRepository _usersRepo;

        public RadioController(
            MusicRepository? musicRepo,
            OapSchedulingService? oapService,
            IS3Service? s3Service,
            SubscriptionsRepository subscriptionsRepo,
            UsersRepository usersRepo)
        {
            _musicRepo = musicRepo;
            _oapService = oapService;
            _s3Service = s3Service;
            _subsRepo = subscriptionsRepo;
            _usersRepo = usersRepo;
        }

        /// <summary>
        /// Get current on-air OAP and their playlist
        /// </summary>
        [HttpGet("now-playing")]
        [AllowAnonymous]
        public async Task<IActionResult> GetNowPlaying()
        {
            var currentOap = await _oapService.GetCurrentOapAgentAsync();

            if (currentOap == null)
            {
                return Ok(new
                {
                    message = "No OAP currently on air",
                    station = "102.5 FM"
                });
            }

            // Get playlist for current OAP's genre
            var playlist = await _musicRepo.GetByGenreAsync(currentOap.Genre, 20);

            return Ok(new
            {
                oap = new
                {
                    id = currentOap.Id,
                    name = currentOap.DisplayName,
                    personality = currentOap.Personality,
                    genre = currentOap.Genre,
                    showTime = $"{currentOap.StartTimeUtc:hh\\:mm} - {currentOap.EndTimeUtc:hh\\:mm} UTC"
                },
                playlist = playlist.Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    artist = t.Artist,
                    album = t.Album,
                    genre = t.Genre,
                    mood = t.Mood,
                    duration = t.DurationSeconds
                }).ToList(),
                playlistCount = playlist.Count,
                station = "102.5 FM"
            });
        }

        /// <summary>
        /// Stream a specific track (authenticated users only)
        /// </summary>
        [HttpGet("stream/{trackId}")]
        [Authorize]
        public async Task<IActionResult> StreamTrack(string trackId)
        {
            var userId = User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            // Check user has credits
            var user = _usersRepo.GetById(userId);
            if (user == null) return NotFound(new { error = "User not found" });

            var hasCredits = user.Credits > 0;
            var trialActive = user.TrialExpires > DateTime.UtcNow;
            var subscription = await _subsRepo.GetActiveSubscriptionAsync(userId);
            var hasActiveSubscription = subscription != null && subscription.CreditsRemaining > 0;

            if (!hasCredits && !trialActive && !hasActiveSubscription)
            {
                return Forbid();
            }

            // Get track
            var track = await _musicRepo.GetByIdAsync(trackId);
            if (track == null)
                return NotFound(new { error = "Track not found" });

            // Increment play count
            await _musicRepo.IncrementPlayCountAsync(trackId);

            // Stream from S3 or local
            if (_s3Service != null && track.S3Bucket != "local")
            {
                try
                {
                    // Get presigned URL (valid for 1 hour) - FIXED TO USE ASYNC
                    var presignedUrl = await _s3Service.GetPresignedUrlAsync(track.S3Key, TimeSpan.FromHours(1));

                    // Return redirect to S3 URL
                    return Redirect(presignedUrl);
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { error = $"Streaming error: {ex.Message}" });
                }
            }
            else
            {
                // Local file streaming
                var localPath = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "uploads",
                    track.Genre,
                    System.IO.Path.GetFileName(track.S3Key));

                if (!System.IO.File.Exists(localPath))
                    return NotFound(new { error = "File not found on server" });

                var stream = System.IO.File.OpenRead(localPath);
                return File(stream, track.ContentType, enableRangeProcessing: true);
            }
        }

        /// <summary>
        /// Get OAP schedule for the day
        /// </summary>
        [HttpGet("schedule")]
        [AllowAnonymous]
        public async Task<IActionResult> GetSchedule()
        {
            var allOaps = await _oapService.GetAllOapsAsync();

            var schedule = allOaps.OrderBy(o => o.StartTimeUtc).Select(o => new
            {
                id = o.Id,
                name = o.DisplayName,
                genre = o.Genre,
                personality = o.Personality,
                startTime = o.StartTimeUtc.ToString(@"hh\:mm"),
                endTime = o.EndTimeUtc.ToString(@"hh\:mm"),
                isCurrentlyOn = IsOapCurrentlyActive(o)
            }).ToList();

            return Ok(new
            {
                station = "102.5 FM",
                schedule,
                currentTime = DateTime.UtcNow.TimeOfDay.ToString(@"hh\:mm")
            });
        }

        /// <summary>
        /// Auto-play: Get next track in current OAP's playlist
        /// </summary>
        [HttpGet("auto-play/next")]
        [Authorize]
        public async Task<IActionResult> GetNextTrack([FromQuery] string? lastTrackId = null)
        {
            var userId = User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var currentOap = await _oapService.GetCurrentOapAgentAsync();
            if (currentOap == null)
                return NotFound(new { error = "No OAP currently on air" });

            // Get playlist for current OAP
            var playlist = await _musicRepo.GetByGenreAsync(currentOap.Genre, 50);

            if (playlist.Count == 0)
                return NotFound(new { error = "No music available for current show" });

            // If lastTrackId is provided, get the next track
            MusicTrack nextTrack;
            if (!string.IsNullOrEmpty(lastTrackId))
            {
                var lastIndex = playlist.FindIndex(t => t.Id == lastTrackId);
                if (lastIndex >= 0 && lastIndex < playlist.Count - 1)
                {
                    nextTrack = playlist[lastIndex + 1];
                }
                else
                {
                    // Loop back to first track or shuffle
                    nextTrack = playlist[0];
                }
            }
            else
            {
                // First track
                nextTrack = playlist[0];
            }

            return Ok(new
            {
                track = new
                {
                    id = nextTrack.Id,
                    title = nextTrack.Title,
                    artist = nextTrack.Artist,
                    album = nextTrack.Album,
                    genre = nextTrack.Genre,
                    mood = nextTrack.Mood,
                    duration = nextTrack.DurationSeconds,
                    streamUrl = $"/api/radio/stream/{nextTrack.Id}"
                },
                oap = new
                {
                    id = currentOap.Id,
                    name = currentOap.DisplayName,
                    genre = currentOap.Genre
                },
                playlistPosition = playlist.FindIndex(t => t.Id == nextTrack.Id) + 1,
                playlistTotal = playlist.Count
            });
        }

        private bool IsOapCurrentlyActive(OapAgent oap)
        {
            var now = DateTime.UtcNow.TimeOfDay;

            if (oap.StartTimeUtc <= oap.EndTimeUtc)
            {
                return now >= oap.StartTimeUtc && now <= oap.EndTimeUtc;
            }
            else
            {
                // Wrap-around (e.g., 22:00 - 02:00)
                return now >= oap.StartTimeUtc || now <= oap.EndTimeUtc;
            }
        }
    }
}
