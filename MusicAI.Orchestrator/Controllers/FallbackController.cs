using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class FallbackController : ControllerBase
{
    [HttpGet("stream")]
    public IActionResult Stream()
    {
        // In production return HLS manifest URL or CDN location
        return Ok(new { message = "Fallback stream (music + jingles). Subscribe to access OAPs." });
    }
}
