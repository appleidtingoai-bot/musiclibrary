using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicAI.Agents.OapAgents;
using MusicAI.Agents.Models;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AgentController : ControllerBase
    {
        private readonly TosinAgent _tosinAgent;
        private readonly Data.SubscriptionsRepository _subsRepo;
        private readonly Data.UsersRepository _usersRepo;
        
        public AgentController(
            TosinAgent tosinAgent,
            Data.SubscriptionsRepository subscriptionsRepo,
            Data.UsersRepository usersRepo)
        {
            _tosinAgent = tosinAgent;
            _subsRepo = subscriptionsRepo;
            _usersRepo = usersRepo;
        }
        
        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] ChatRequest request)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized(new { error = "Invalid token" });
            
            if (string.IsNullOrWhiteSpace(request.Message))
                return BadRequest(new { error = "Message is required" });
            
            // Check if user has active subscription or trial credits
            var user = _usersRepo.GetById(userId);
            if (user == null)
                return NotFound(new { error = "User not found" });
            
            try
            {
                var hasCredits = user.Credits > 0;
                var trialActive = user.TrialExpires > DateTime.UtcNow;
                var subscription = await _subsRepo.GetActiveSubscriptionAsync(userId);
                var hasActiveSubscription = subscription != null && subscription.CreditsRemaining > 0;
                
                if (!hasCredits && !trialActive && !hasActiveSubscription)
                {
                    return Ok(new
                    {
                        requiresPayment = true,
                        message = "Your credits have expired. Please purchase a package to continue enjoying music!",
                        creditsRemaining = 0,
                        trialExpired = true
                    });
                }
                
                // Chat with Tosin agent
                AgentResponse response;
                // Only use history if it has valid entries with both Role and Content
                if (request.ConversationHistory != null && 
                    request.ConversationHistory.Any() && 
                    request.ConversationHistory.All(h => !string.IsNullOrEmpty(h.Role) && !string.IsNullOrEmpty(h.Content)))
                {
                    var history = request.ConversationHistory.Select(h => (h.Role, h.Content)).ToList();
                    response = await _tosinAgent.ChatWithHistoryAsync(userId, request.Message, history);
                }
                else
                {
                    response = await _tosinAgent.ChatAsync(userId, request.Message);
                }
                
                // Return response with metadata
                return Ok(new
                {
                    message = response.Message,
                    detectedMood = response.DetectedMood,
                    recommendedTracks = response.RecommendedTracks?.Select(t => new
                    {
                        id = t.GetType().GetProperty("Id")?.GetValue(t),
                        title = t.GetType().GetProperty("Title")?.GetValue(t),
                        artist = t.GetType().GetProperty("Artist")?.GetValue(t),
                        genre = t.GetType().GetProperty("Genre")?.GetValue(t),
                        mood = t.GetType().GetProperty("Mood")?.GetValue(t)
                    }).ToList(),
                    requiresPayment = response.RequiresPayment,
                    paymentIntent = response.PaymentIntent,
                    creditsRemaining = subscription?.CreditsRemaining ?? (int)user.Credits
                });
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    message = $"Oops! Something went wrong: {ex.Message}",
                    detectedMood = (string?)null,
                    recommendedTracks = (object?)null,
                    requiresPayment = false,
                    paymentIntent = (string?)null,
                    creditsRemaining = user?.Credits ?? 0
                });
            }
        }
        
        [HttpGet("agents")]
        public IActionResult GetAvailableAgents()
        {
            return Ok(new[]
            {
                new
                {
                    id = "tosin",
                    name = "Tosin",
                    displayName = "Tosin",
                    role = "Afrobeats Music Host",
                    genre = "afrobeats",
                    personality = "friendly",
                    description = "Your friendly Afrobeats radio host who helps you discover amazing Nigerian and African music based on your mood"
                }
            });
        }
        
        [HttpGet("agent/tosin")]
        public IActionResult GetTosinInfo()
        {
            return Ok(new
            {
                id = "tosin",
                name = _tosinAgent.Name,
                personality = _tosinAgent.Personality,
                genre = "afrobeats",
                capabilities = new[]
                {
                    "Mood-based music recommendations",
                    "Artist and genre search",
                    "Subscription management help",
                    "Conversational music discovery"
                },
                sampleQuestions = new[]
                {
                    "I'm feeling happy, play me something upbeat!",
                    "What Afrobeats songs are popular right now?",
                    "I need something calm and relaxing",
                    "Play me some Burna Boy",
                    "I'm out of credits, what are my options?"
                }
            });
        }
        
        public class ChatRequest
        {
            public string Message { get; set; } = string.Empty;
            public List<ConversationMessage>? ConversationHistory { get; set; }
        }
        
        public class ConversationMessage
        {
            public string Role { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }
    }
}
