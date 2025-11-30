using Microsoft.AspNetCore.Mvc;
using MusicAI.Common.Models;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly LlmService _llm;
    private readonly CustomTtsService _tts;

    public ChatController(LlmService llm, CustomTtsService tts)
    {
        _llm = llm;
        _tts = tts;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatRequest req)
    {
        var reply = await _llm.GenerateReplyAsync(req.Message, string.Empty);
        var audioUrl = await _tts.GenerateAudioUrl(reply);
        return Ok(new ChatResponse(reply, audioUrl, "Tosin"));
    }

    [HttpGet("live/{userId}")]
    public IActionResult Live(string userId)
    {
        return Ok(new { message = "Live persona Tosin endpoint for user " + userId });
    }

    [HttpPost("feedback")]
    public IActionResult Feedback([FromBody] FeedbackRequest req)
    {
        // push feedback to RL pipeline
        return Ok();
    }
}
