using Microsoft.AspNetCore.Mvc;
using MusicAI.Common.Models;
using System.Linq;
using Microsoft.AspNetCore.WebUtilities;

[ApiController]
[Route("api/[controller]")]
public class PlaybackController : ControllerBase
{
    private readonly List<PersonaConfig> _personas;
    private readonly Func<string, User> _getUser;

    public PlaybackController(List<PersonaConfig> personas, Func<string, User> getUser)
    {
        _personas = personas;
        _getUser = getUser;
    }

    // 24/7 mix endpoint (public)
    [HttpGet("mix")]
    public IActionResult Mix()
    {
        // Redirect to a configurable mix stream if present; otherwise to a local fallback
        var mixUrl = "http://localhost:6001/api/fallback/stream"; // placeholder
        return Redirect(mixUrl);
    }

    // Main playback entry that selects active OAP persona or falls back to mix stream.
    [HttpGet("oap/{userId}")]
    public IActionResult PlayOap(string userId, [FromQuery] string? persona = null)
    {
        var user = _getUser(userId);

        // find requested persona if provided
        PersonaConfig? requested = null;
        if (!string.IsNullOrEmpty(persona)) requested = _personas.FirstOrDefault(p => string.Equals(p.Id, persona, StringComparison.OrdinalIgnoreCase));

        // find active persona
        var active = _personas.FirstOrDefault(p => p.IsActiveNow());

        // If a persona was requested, prefer it only if it's active
        if (requested != null && requested.IsActiveNow()) active = requested;

        // subscription/credit check
        if (active != null && user.IsSubscribed && user.Credits > 0)
        {
            var url = $"http://{active.Host}:{active.Port}/api/chat/live/{userId}";
            return Redirect(url);
        }

        // If not subscribed or no credits, redirect to mix and include a reminder flag
        var fallback = "http://localhost:6001/api/fallback/stream";
        var redirect = QueryHelpers.AddQueryString(fallback, "reminder", "subscribe");
        return Redirect(redirect);
    }
}
