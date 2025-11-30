using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicAI.Orchestrator.Services;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class NewsController : ControllerBase
    {
        private readonly NewsService _newsService;
        private readonly NewsReadingService _newsReadingService;

        public NewsController(
            NewsService newsService,
            NewsReadingService newsReadingService)
        {
            _newsService = newsService;
            _newsReadingService = newsReadingService;
        }

        /// <summary>
        /// Get current news headlines (for display, not audio)
        /// </summary>
        [HttpGet("headlines")]
        [AllowAnonymous]
        public async Task<IActionResult> GetHeadlines([FromQuery] string country = "ng", [FromQuery] int count = 10)
        {
            try
            {
                var headlines = await _newsService.GetTopHeadlinesAsync(country, count);
                return Ok(new
                {
                    count = headlines.Count,
                    headlines
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Check if news is currently being read
        /// </summary>
        [HttpGet("status")]
        [AllowAnonymous]
        public IActionResult GetNewsStatus()
        {
            var isPlaying = _newsReadingService.IsNewsCurrentlyPlaying();
            var audioUrl = _newsReadingService.GetCurrentNewsAudioUrl();
            var nextOap = _newsReadingService.GetNextOapPersonaId();

            return Ok(new
            {
                isNewsPlaying = isPlaying,
                currentNewsAudioUrl = audioUrl,
                nextOapPersona = nextOap,
                newsReader = "tosin",
                newsDurationMinutes = 5
            });
        }

        /// <summary>
        /// Manually trigger news reading (for testing)
        /// Admin only
        /// </summary>
        [HttpPost("trigger")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        public async Task<IActionResult> TriggerNewsReading()
        {
            try
            {
                var audioUrl = await _newsReadingService.GenerateNewsNowAsync();
                
                if (string.IsNullOrEmpty(audioUrl))
                {
                    return StatusCode(500, new { error = "Failed to generate news audio" });
                }

                return Ok(new
                {
                    message = "News reading triggered",
                    audioUrl,
                    durationMinutes = 5,
                    nextOap = _newsReadingService.GetNextOapPersonaId()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get formatted news script (for testing)
        /// </summary>
        [HttpGet("script")]
        [AllowAnonymous]
        public async Task<IActionResult> GetNewsScript([FromQuery] string country = "ng")
        {
            try
            {
                var headlines = await _newsService.GetTopHeadlinesAsync(country, 10);
                var script = _newsService.FormatNewsScript(headlines);

                return Ok(new
                {
                    script,
                    headlineCount = headlines.Count,
                    estimatedDuration = "5 minutes"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
