using Microsoft.AspNetCore.Mvc;
using MusicAI.Common.Models;
using System.Threading.Tasks;

[ApiController]
[Route("api/[controller]")]
public class TosinController : ControllerBase
{
    private readonly LlmService _llm;
    private readonly CustomTtsService _tts;
    private readonly MusicAI.Infrastructure.Services.PollyService? _polly;
    private readonly MusicAI.Infrastructure.Services.IS3Service? _s3;

    public TosinController(LlmService llm, CustomTtsService tts, IServiceProvider sp)
    {
        _llm = llm;
        _tts = tts;
        _polly = sp.GetService(typeof(MusicAI.Infrastructure.Services.PollyService)) as MusicAI.Infrastructure.Services.PollyService;
        _s3 = sp.GetService(typeof(MusicAI.Infrastructure.Services.IS3Service)) as MusicAI.Infrastructure.Services.IS3Service;
    }

    public record HeadlineRequest(string Country, int Count = 3);

    [HttpPost("generate-headlines-audio")]
    public async Task<IActionResult> GenerateHeadlinesAudio([FromBody] HeadlineRequest req)
    {
        var country = string.IsNullOrWhiteSpace(req.Country) ? "global" : req.Country;
        var prompt = $"Generate {req.Count} short news headlines for {country}. Keep each headline under 12 words. Separate by new line.";

        var llmReply = await _llm.GenerateReplyAsync(prompt, "news headlines");
        if (string.IsNullOrWhiteSpace(llmReply)) return StatusCode(500, new { error = "LLM returned empty reply" });

        var audioKey = $"tts/tosin/headlines_{Guid.NewGuid():N}.mp3";

        // Prefer Polly if available for real audio synthesis and upload to S3
        if (_polly != null && _s3 != null)
        {
            try
            {
                var presigned = await _polly.SynthesizeToS3Url(llmReply, audioKey);
                return Ok(new { text = llmReply, audioUrl = presigned });
            }
            catch (Exception ex)
            {
                // fallback to local tts
            }
        }

        // Fallback: use local CustomTtsService to create an asset and, if S3 available, upload it
        var localPath = await _tts.GenerateAudioUrl(llmReply);

        if (_s3 != null)
        {
            // try to upload the local asset into S3 as a text placeholder (or real audio if tts produced audio)
            try
            {
                // read file if path is a file
                if (System.IO.File.Exists(localPath))
                {
                    using var fs = System.IO.File.OpenRead(localPath);
                    await _s3.UploadFileAsync(audioKey, fs, "audio/mpeg");
                    var presigned = await _s3.GetPresignedUrlAsync(audioKey, TimeSpan.FromMinutes(60));
                    return Ok(new { text = llmReply, audioUrl = presigned });
                }
                else
                {
                    // localPath may be a virtual path; return it directly
                    return Ok(new { text = llmReply, audioUrl = localPath });
                }
            }
            catch { /* fall through to return local path */ }
        }

        return Ok(new { text = llmReply, audioUrl = localPath });
    }

    [HttpGet("now")]
    public IActionResult Now()
    {
        // Return latest published audio if available
        var store = HttpContext.RequestServices.GetService(typeof(NewsStore)) as NewsStore;
        if (store == null) return NotFound(new { error = "News store not available" });
        if (string.IsNullOrEmpty(store.AudioUrl)) return NotFound(new { error = "No news published yet" });
        return Ok(new { audioUrl = store.AudioUrl, publishedAtUtc = store.PublishedAtUtc });
    }
}
