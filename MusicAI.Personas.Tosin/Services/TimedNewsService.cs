using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

public class TimedNewsService : BackgroundService
{
    private readonly NewsPublisher _publisher;
    private readonly NewsStore _store;

    public TimedNewsService(NewsPublisher publisher, NewsStore store)
    {
        _publisher = publisher;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until next top of the hour, then run every hour
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
            var delay = next - now;
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException) { break; }

            try
            {
                await _publisher.PublishNowAsync("Nigeria");
            }
            catch { /* swallow exceptions to avoid stopping the service */ }

            // then loop to schedule next hour
        }
    }
}
