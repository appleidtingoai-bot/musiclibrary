using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using MusicAI.Infrastructure.Services;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/oap")]
    public class StreamProxyController : ControllerBase
    {
        private readonly IS3Service _s3Service;
        private readonly ILogger<StreamProxyController> _logger;
        private readonly System.Net.Http.IHttpClientFactory _httpFactory;

        public StreamProxyController(IS3Service s3Service, ILogger<StreamProxyController> logger, System.Net.Http.IHttpClientFactory httpFactory)
        {
            _s3Service = s3Service;
            _logger = logger;
            _httpFactory = httpFactory;
        }

        [HttpGet("stream-proxy")]
        [AllowAnonymous]
        public async Task<IActionResult> StreamProxy([FromQuery] string s3Key)
        {
            if (string.IsNullOrEmpty(s3Key)) return BadRequest(new { error = "s3Key is required" });

            try
            {
                // Generate a short-lived presigned URL and stream it through the server to avoid browser CORS
                var presigned = await _s3Service.GetPresignedUrlAsync(s3Key, TimeSpan.FromMinutes(10));
                if (string.IsNullOrEmpty(presigned)) return NotFound(new { error = "could not generate presigned url" });

                var http = _httpFactory != null ? _httpFactory.CreateClient("stream-proxy") : new System.Net.Http.HttpClient();
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, presigned);

                // Forward Range header from the client if present to enable seeking / partial responses
                var rangeHeader = Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    req.Headers.TryAddWithoutValidation("Range", rangeHeader);
                }

                var upstream = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
                if (!upstream.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Upstream returned {Status} for {Key}", upstream.StatusCode, s3Key);
                    return StatusCode((int)upstream.StatusCode, new { error = "upstream fetch failed" });
                }
                // Copy relevant response headers BEFORE writing body
                if (upstream.Content.Headers.ContentType != null)
                {
                    Response.ContentType = upstream.Content.Headers.ContentType.ToString();
                }

                // Forward partial content headers for range requests
                if (upstream.Content.Headers.ContentRange != null)
                {
                    Response.Headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();
                }
                if (upstream.Headers.Contains("Accept-Ranges"))
                {
                    Response.Headers["Accept-Ranges"] = string.Join(",", upstream.Headers.GetValues("Accept-Ranges"));
                }

                // Copy other useful headers if present
                if (upstream.Content.Headers.ContentDisposition != null)
                {
                    Response.Headers["Content-Disposition"] = upstream.Content.Headers.ContentDisposition.ToString();
                }
                if (upstream.Headers.ETag != null)
                {
                    Response.Headers["ETag"] = upstream.Headers.ETag.ToString();
                }
                if (upstream.Content.Headers.LastModified.HasValue)
                {
                    Response.Headers["Last-Modified"] = upstream.Content.Headers.LastModified.Value.ToString("R");
                }

                // Set response status code (may be 200 or 206)
                Response.StatusCode = (int)upstream.StatusCode;

                // Respect content-length when provided (otherwise Transfer-Encoding chunked will be used)
                if (upstream.Content.Headers.ContentLength.HasValue)
                {
                    Response.ContentLength = upstream.Content.Headers.ContentLength.Value;
                }

                // Stream the response body directly to client honoring cancellation
                using var stream = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                await stream.CopyToAsync(Response.Body, 81920, HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stream proxy failed for {Key}", s3Key);
                return StatusCode(500, new { error = "stream proxy failed" });
            }
        }

        private static string GetContentType(string ext)
        {
            ext = ext?.ToLowerInvariant() ?? string.Empty;
            return ext switch
            {
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                _ => "application/octet-stream",
            };
        }
    }
}
