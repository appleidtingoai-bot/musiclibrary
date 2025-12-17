using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using MusicAI.Infrastructure.Services;

namespace MusicAI.Orchestrator.Services
{
    // Lightweight background worker for batch presigning and manifest assembly
    public class BackgroundSignerService : BackgroundService
    {
        private readonly ILogger<BackgroundSignerService> _logger;
        private readonly IS3Service? _s3;
        private readonly IDistributedCache _cache;
        // Bounded channel-backed queue for presign work
        private readonly Channel<WorkItem> _channel;
        private readonly IHttpClientFactory _httpFactory;

        public BackgroundSignerService(ILogger<BackgroundSignerService> logger, IS3Service? s3, IDistributedCache cache, IHttpClientFactory httpFactory)
        {
            _logger = logger;
            _s3 = s3;
            _cache = cache;
            _httpFactory = httpFactory;
            // small bounded buffer to provide backpressure (configurable size)
            _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        }

        // Enqueue a presign request and await the result
        public Task<PresignResult> EnqueueAsync(PresignRequest req, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<PresignResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new WorkItem { Req = req, Tcs = tcs };
            // will wait if channel is full
            var write = _channel.Writer.WriteAsync(item, ct);
            if (!write.IsCompletedSuccessfully)
            {
                // await to observe exceptions/cancellation
                return AwaitWriteAsync(write, tcs, ct);
            }
            return tcs.Task;
        }

        private async Task<PresignResult> AwaitWriteAsync(ValueTask writeTask, TaskCompletionSource<PresignResult> tcs, CancellationToken ct)
        {
            await writeTask;
            return await tcs.Task;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BackgroundSignerService started");
            var reader = _channel.Reader;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var has = await reader.WaitToReadAsync(stoppingToken);
                    if (!has) break;
                    while (reader.TryRead(out var work))
                    {
                        try
                        {
                            var res = await DoPresignAsync(work.Req, stoppingToken);
                            work.Tcs.TrySetResult(res);
                        }
                        catch (Exception ex)
                        {
                            work.Tcs.TrySetException(ex);
                            _logger.LogError(ex, "BackgroundSignerService work item failed");
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BackgroundSignerService loop failure");
                    await Task.Delay(500, stoppingToken);
                }
            }
            _logger.LogInformation("BackgroundSignerService stopping");
        }

        private async Task<PresignResult> DoPresignAsync(PresignRequest req, CancellationToken ct)
        {
            if (_s3 == null) throw new InvalidOperationException("S3 service not configured");
            var cacheKey = $"presign:{req.Key}:{req.ExpiryMinutes}";
            try
            {
                // check distributed cache first
                var cached = await _cache.GetStringAsync(cacheKey, ct);
                if (!string.IsNullOrEmpty(cached))
                {
                    return new PresignResult { Url = cached, ExpiresAt = DateTime.UtcNow.AddMinutes(req.ExpiryMinutes) };
                }
            }
            catch { }

            var url = await _s3.GetPresignedUrlAsync(req.Key, TimeSpan.FromMinutes(req.ExpiryMinutes));
            if (!string.IsNullOrEmpty(url))
            {
                try { await _cache.SetStringAsync(cacheKey, url, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(req.ExpiryMinutes - 1) }, ct); } catch { }
            }
            return new PresignResult { Url = url ?? string.Empty, ExpiresAt = DateTime.UtcNow.AddMinutes(req.ExpiryMinutes) };
        }

        private record WorkItem { public PresignRequest Req; public TaskCompletionSource<PresignResult> Tcs; }

        public record PresignRequest { public string Key { get; init; } = string.Empty; public int ExpiryMinutes { get; init; } = 60; }

        public record PresignResult { public string Url { get; init; } = string.Empty; public DateTime ExpiresAt { get; init; } }
    }
}
