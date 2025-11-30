using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MusicAI.Agents.Models
{
    public record AgentResponse(
        string Message,
        string? AudioUrl = null,
        List<object>? RecommendedTracks = null,
        string? DetectedMood = null,
        PaymentIntent? PaymentIntent = null,
        bool RequiresPayment = false);

    public record PaymentIntent(string PackageId, decimal Amount, string PaymentUrl);

    public record SubscriptionStatus(
        bool HasActiveSubscription,
        int CreditsRemaining,
        DateTime? ExpiresAt,
        int DaysRemaining);
        
    public record UserProfile(
        string Email,
        string Country,
        bool IsSubscribed,
        double Credits);
        
    public record PlaybackSession(
        string Id,
        string UserId,
        string TrackId,
        DateTime StartedAt,
        string StreamUrl);
        
    public record ConversationLog(
        string Id,
        string UserId,
        string OapAgentId,
        string UserMessage,
        string OapResponse,
        string? DetectedMood,
        DateTime CreatedAt);
}
