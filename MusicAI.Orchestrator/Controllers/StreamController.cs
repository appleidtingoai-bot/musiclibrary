using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicAI.Common.Models;
using MusicAI.Orchestrator.Services;
using System.Linq;
using System.Collections.Generic;

[ApiController]
[Route("api/[controller]")]
public class StreamController : ControllerBase
{
    private readonly List<PersonaConfig> _personas;
    private readonly Func<string, User> _getUser;
    private readonly ILogger<StreamController> _logger;
    private readonly NewsReadingService _newsService;

    public StreamController(List<PersonaConfig> personas, Func<string, User> getUser, ILogger<StreamController> logger, NewsReadingService newsService)
    {
        _personas = personas;
        _getUser = getUser;
        _logger = logger;
        _newsService = newsService;
    }

    // Route: /api/stream/{userId}/{personaId?}
    // If personaId is provided and active it will be used; otherwise the currently active persona is used.
    [HttpGet("stream/{userId}/{personaId?}")]
    public IActionResult GetStream(string userId, string? personaId = null)
    {
        var user = _getUser(userId);
        _logger.LogDebug("Stream requested for user {UserId}. Subscribed={Subscribed}, Credits={Credits}", user?.Id, user?.IsSubscribed, user?.Credits);

        // Check if news is currently playing (overrides all persona routing)
        if (_newsService.IsNewsCurrentlyPlaying())
        {
            var newsAudioUrl = _newsService.CurrentNewsAudioUrl;
            if (!string.IsNullOrEmpty(newsAudioUrl))
            {
                _logger.LogInformation("News is playing. Routing user {UserId} to news audio", userId);
                return Redirect(newsAudioUrl);
            }
        }

        // After news ends, check which OAP should play next (e.g., Ife Mi after Tosin's news)
        var nextOapId = _newsService.GetNextOapPersonaId();
        if (!string.IsNullOrEmpty(nextOapId))
        {
            _logger.LogDebug("News just ended. Next OAP persona: {Persona}", nextOapId);
            personaId = nextOapId; // Override requested persona with scheduled next OAP
        }

        // Find requested persona if present
        PersonaConfig? requested = null;
        if (!string.IsNullOrEmpty(personaId))
        {
            requested = _personas.FirstOrDefault(p => string.Equals(p.Id, personaId, StringComparison.OrdinalIgnoreCase));
            if (requested != null) _logger.LogDebug("Requested persona {Persona} found", personaId);
        }

        // Find the currently active persona
        var active = _personas.FirstOrDefault(p => p.IsActiveNow());
        if (active != null) _logger.LogDebug("Currently active persona: {Persona}", active.Id);

        // Resolve target persona: requested if active, otherwise active
        PersonaConfig? target = null;
        if (requested != null && requested.IsActiveNow()) target = requested;
        if (target == null) target = active;

        if (target == null)
        {
            _logger.LogInformation("No persona active; redirecting to fallback mix");
            return Redirect(GetFallbackMixUrl());
        }

        // Language selection: pass language hints to persona endpoint (comma-separated). Nigeria gets three languages.
        var lang = GetLanguageHint(user?.Country);

        // Access control: allow if subscribed OR has credits
        if (user == null || (!user.IsSubscribed && user.Credits <= 0))
        {
            _logger.LogInformation("User {UserId} not allowed to access persona {Persona}. Redirecting to fallback.", userId, target.Id);
            return Redirect($"{GetFallbackMixUrl()}?lang={Uri.EscapeDataString(lang)}");
        }

        // TODO: integrate billing service to perform micro-deduction on each connection/listen

        var url = $"http://{target.Host}:{target.Port}/api/chat/live/{Uri.EscapeDataString(userId)}?lang={Uri.EscapeDataString(lang)}";
        _logger.LogInformation("Routing user {UserId} to persona {Persona} at {Url}", userId, target.Id, url);
        return Redirect(url);
    }

    // A dedicated endpoint for 24/7 mix/jingles when no persona is active
    [HttpGet("playmix/{userId}")]
    public IActionResult PlayMix(string userId)
    {
        _logger.LogInformation("PlayMix requested for {UserId}", userId);
        return Redirect(GetFallbackMixUrl());
    }

    private static string GetFallbackMixUrl() => "http://localhost:6001/api/fallback/stream";

    private static string GetLanguageHint(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return "en";
        if (country.Equals("NG", StringComparison.OrdinalIgnoreCase) || country.Contains("Nigeria", StringComparison.OrdinalIgnoreCase))
        {
            // Nigeria: provide English + two major languages for richer responses
            return "en,yo,ig"; // English, Yoruba, Igbo
        }
        // Default single language responses for other countries
        return "en";
    }
}
