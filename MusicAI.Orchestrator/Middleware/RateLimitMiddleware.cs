using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace MusicAI.Orchestrator.Middleware;

public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly int _limit;
    private readonly TimeSpan _window;

    public RateLimitMiddleware(RequestDelegate next, IMemoryCache cache, int limit = 60, TimeSpan? window = null)
    {
        _next = next;
        _cache = cache;
        _limit = limit;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anon";
        var key = $"rl:{ip}";

        var counter = _cache.GetOrCreate<RequestCounter>(key, e => {
            e.AbsoluteExpirationRelativeToNow = _window;
            return new RequestCounter { Count = 0, ExpiresAt = DateTime.UtcNow.Add(_window) };
        });

        if (counter.Count >= _limit)
        {
            context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            context.Response.Headers["Retry-After"] = Math.Ceiling((counter.ExpiresAt - DateTime.UtcNow).TotalSeconds).ToString();
            await context.Response.WriteAsync("Too many requests");
            return;
        }

        counter.Count++;
        _cache.Set(key, counter, counter.ExpiresAt - DateTime.UtcNow);
        await _next(context);
    }

    class RequestCounter { public int Count; public DateTime ExpiresAt; }
}
