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
        public async Task<IActionResult> StreamProxy([FromQuery] string s3Key, [FromQuery] string? t)
        {
            if (string.IsNullOrEmpty(s3Key)) return BadRequest(new { error = "s3Key is required" });

            try
            {
                _logger.LogInformation("StreamProxy requested for key={Key}", s3Key);

                var presigned = await _s3Service.GetPresignedUrlAsync(s3Key, TimeSpan.FromMinutes(10));
                if (string.IsNullOrEmpty(presigned))
                {
                    _logger.LogWarning("Presigned url missing for {Key}", s3Key);
                    return NotFound(new { error = "could not generate presigned url" });
                }
                _logger.LogDebug("Presigned host={Host} for key={Key}", new Uri(presigned).Host, s3Key);

                var http = _httpFactory.CreateClient("stream-proxy");
                http.Timeout = TimeSpan.FromSeconds(30);

                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, presigned);
                var rangeHeader = Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    req.Headers.TryAddWithoutValidation("Range", rangeHeader);
                }

                var upstream = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

                if (!upstream.IsSuccessStatusCode)
                {
                    string bodyPreview = string.Empty;
                    try
                    {
                        var s = await upstream.Content.ReadAsStringAsync();
                        bodyPreview = s.Length > 200 ? s.Substring(0, 200) : s;
                    }
                    catch { /* ignore body read errors */ }

                    _logger.LogWarning("Upstream returned {Status} for {Key} bodyPreview={Preview}", upstream.StatusCode, s3Key, bodyPreview);
                    if (!Response.HasStarted) return StatusCode((int)upstream.StatusCode, new { error = "upstream fetch failed" });
                    try { HttpContext.Abort(); } catch { }
                    return new EmptyResult();
                }

                // copy headers BEFORE writing body
                if (upstream.Content.Headers.ContentType != null)
                {
                    Response.ContentType = upstream.Content.Headers.ContentType.ToString();
                }
                if (upstream.Content.Headers.ContentRange != null)
                {
                    Response.Headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();
                }
                if (upstream.Headers.Contains("Accept-Ranges"))
                {
                    Response.Headers["Accept-Ranges"] = string.Join(",", upstream.Headers.GetValues("Accept-Ranges"));
                }
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

                Response.StatusCode = (int)upstream.StatusCode;
                if (upstream.Content.Headers.ContentLength.HasValue)
                {
                    Response.ContentLength = upstream.Content.Headers.ContentLength.Value;
                }

                using var stream = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                try
                {
                    _logger.LogInformation("Begin streaming to client for {Key}", s3Key);
                    var buffer = new byte[81920];
                    int read;
                    long total = 0;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, HttpContext.RequestAborted)) > 0)
                    {
                        await Response.Body.WriteAsync(buffer, 0, read, HttpContext.RequestAborted);
                        total += read;
                    }
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                    _logger.LogInformation("Finished streaming {Bytes} bytes for {Key}", total, s3Key);
                }
                catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation("Client cancelled streaming for {Key}", s3Key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while streaming body for {Key}", s3Key);
                    if (!Response.HasStarted)
                    {
                        return StatusCode(500, new { error = "stream proxy failed" });
                    }
                    try { HttpContext.Abort(); } catch { }
                }

                return new EmptyResult();
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogInformation("Request aborted for {Key}", s3Key);
                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stream proxy failed for {Key}", s3Key);
                if (!Response.HasStarted)
                {
                    return StatusCode(500, new { error = "stream proxy failed" });
                }
                try { HttpContext.Abort(); } catch { }
                return new EmptyResult();
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
