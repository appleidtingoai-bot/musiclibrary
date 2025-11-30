using System;

namespace MusicAI.Common.Models
{
    public record ChatRequest(string UserId, string Message, string Language = "en");
    public record ChatResponse(string ReplyText, string AudioUrl, string Persona);
    public record FeedbackRequest(string UserId, string TrackId, string Feedback);
    public record User(string Id, string Email, bool IsSubscribed, double Credits, string Country, string IpAddress);
    public record PersonaConfig(string Id, string Name, string Host, int Port, TimeSpan Start, TimeSpan End)
    {
        public bool IsActiveNow()
        {
            var now = DateTime.UtcNow.TimeOfDay;
            if (Start <= End) return now >= Start && now <= End;
            return now >= Start || now <= End;
        }
    }
}
