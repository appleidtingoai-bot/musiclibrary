using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicAI.Infrastructure.Services;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MediaController : ControllerBase
    {
        private readonly IS3Service _s3Service;

        public MediaController(IS3Service s3Service)
        {
            _s3Service = s3Service ?? throw new ArgumentNullException(nameof(s3Service));
        }

        /// <summary>
        /// Returns a short-lived presigned URL for the requested S3 object key.
        /// Example: GET /api/media/presign?key=music/rnb/file.mp3&expiryMinutes=2
        /// Requires authentication.
        /// </summary>
        [HttpGet("presign")]
        [Authorize]
        public async Task<IActionResult> GetPresigned([FromQuery] string key, [FromQuery] int expiryMinutes = 2)
        {
            if (string.IsNullOrWhiteSpace(key))
                return BadRequest(new { error = "key is required" });

            if (expiryMinutes <= 0 || expiryMinutes > 60)
                expiryMinutes = 2;

            try
            {
                var url = await _s3Service.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(expiryMinutes));
                return Ok(new { url, expiresIn = expiryMinutes * 60 });
            }
            catch (Exception ex)
            {
                // Keep error message friendly but include details in server logs
                // so operator can inspect the exception without leaking internals to clients.
                Console.Error.WriteLine($"Presign error for key '{key}': {ex}");
                return StatusCode(500, new { error = "failed to generate presigned url" });
            }
        }
    }
}
