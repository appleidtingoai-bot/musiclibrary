using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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

        // Simple in-memory queue for tasks (for larger scale use a persistent queue)
        private readonly ConcurrentQueue<Func<Task>> _queue = new ConcurrentQueue<Func<Task>>();

        public BackgroundSignerService(ILogger<BackgroundSignerService> logger, IS3Service? s3, IDistributedCache cache)
        {
            _logger = logger;
            _s3 = s3;
            _cache = cache;
        }

        public void Enqueue(Func<Task> workItem)
        {
            _queue.Enqueue(workItem);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BackgroundSignerService started");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_queue.TryDequeue(out var work))
                    {
                        await work();
                    }
                    else
                    {
                        await Task.Delay(200, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BackgroundSignerService work item failed");
                }
            }
            _logger.LogInformation("BackgroundSignerService stopping");
        }
    }
}
