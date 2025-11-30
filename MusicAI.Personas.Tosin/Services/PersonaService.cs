using System.Threading.Tasks;
using MusicAI.Common.Models;
using Microsoft.Extensions.Caching.Memory;

public class PersonaService
{
    private readonly IMemoryCache _cache;
    private readonly LlmService _llm;
    private readonly CustomTtsService _tts;

    public PersonaService(IMemoryCache cache, LlmService llm, CustomTtsService tts)
    {
        _cache = cache;
        _llm = llm;
        _tts = tts;
    }

    public async Task<(string reply, string audioUrl)> ChatAsync(string userId, string message)
    {
        var contextKey = $"context:{userId}";
        _cache.TryGetValue(contextKey, out string context);

        var reply = await _llm.GenerateReplyAsync(message, context ?? string.Empty);

        var newContext = (context ?? string.Empty) + "\nUser: " + message + "\nTosin: " + reply;
        _cache.Set(contextKey, newContext, TimeSpan.FromHours(24));

        var audioUrl = await _tts.GenerateAudioUrl(reply);
        return (reply, audioUrl);
    }

    public void Feedback(string userId, string trackId, string feedback)
    {
        // push feedback into RL queue (not implemented in minimal)
    }
}
