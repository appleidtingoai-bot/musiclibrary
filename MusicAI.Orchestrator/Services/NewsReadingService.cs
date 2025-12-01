using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MusicAI.Orchestrator.Services
{
    /// <summary>
    /// Background service that reads news every hour at the top of the hour
    /// </summary>
    public class NewsReadingService : BackgroundService
    {
        private readonly NewsService _newsService;
        private readonly ILogger<NewsReadingService> _logger;
        private string? _currentNewsAudioUrl;
        private DateTime? _newsReadingStartTime;
        private const int NEWS_DURATION_MINUTES = 5;

        public NewsReadingService(
            NewsService newsService,
            ILogger<NewsReadingService> logger)
        {
            _newsService = newsService;
            _logger = logger;
        }

        public bool IsNewsCurrentlyPlaying()
        {
            if (_newsReadingStartTime == null) return false;
            
            var elapsed = DateTime.UtcNow - _newsReadingStartTime.Value;
            return elapsed.TotalMinutes < NEWS_DURATION_MINUTES;
        }

        public string? CurrentNewsAudioUrl => IsNewsCurrentlyPlaying() ? _currentNewsAudioUrl : null;
        
        public string? GetCurrentNewsAudioUrl()
        {
            return CurrentNewsAudioUrl;
        }

        public DateTime? GetNewsStartTime()
        {
            return _newsReadingStartTime;
        }

        public string GetNextOapPersonaId()
        {
            // After Tosin's news (12AM-5AM), switch to Ife Mi (Love Lounge, 10PM-2AM)
            var currentHour = DateTime.UtcNow.Hour;
            
            // Schedule: Tosin reads news on the hour, then switches to next OAP
            // For now, return "ifemi" (Ife Mi) as the next persona
            return "ifemi";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("News Reading Service started (automatic news DISABLED - TTS not configured).");
            _logger.LogInformation("News can be triggered manually via POST /api/news/trigger once TTS is set up.");

            // Automatic news disabled until TTS integration with ElevenLabs/Azure is complete
            // This allows other OAPs to work normally without being interrupted by placeholder news
            
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                    // Service kept alive for manual triggers only
                }
            }
            catch (TaskCanceledException)
            {
                // Expected during shutdown
            }
        }

        private async Task FetchAndReadNewsAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Fetching news headlines...");
                
                // Get top headlines
                var headlines = await _newsService.GetTopHeadlinesAsync("ng", 10);
                
                if (headlines == null || !headlines.Any())
                {
                    _logger.LogWarning("No headlines fetched");
                    return;
                }

                _logger.LogInformation($"Fetched {headlines.Count} headlines");

                // Format news script
                var script = _newsService.FormatNewsScript(headlines);
                
                _logger.LogInformation($"News script: {script.Substring(0, Math.Min(100, script.Length))}...");

                // Generate TTS audio (placeholder - implement actual TTS)
                var audioUrl = await GenerateNewsAudioAsync(script, cancellationToken);
                
                if (!string.IsNullOrEmpty(audioUrl))
                {
                    _currentNewsAudioUrl = audioUrl;
                    _newsReadingStartTime = DateTime.UtcNow;
                    
                    _logger.LogInformation($"News audio ready. Will play for {NEWS_DURATION_MINUTES} minutes.");
                    _logger.LogInformation($"After news, switching to OAP: {GetNextOapPersonaId()}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching and reading news");
            }
        }

        private async Task<string?> GenerateNewsAudioAsync(string script, CancellationToken cancellationToken)
        {
            try
            {
                // TODO: Implement actual TTS using:
                // - Azure Text-to-Speech
                // - Google Cloud TTS
                // - AWS Polly
                // - ElevenLabs
                
                // For now, return a placeholder URL
                // The actual implementation should:
                // 1. Call TTS service with script
                // 2. Get audio file
                // 3. Upload to S3 or save locally
                // 4. Return HLS streaming URL

                _logger.LogInformation("TTS generation placeholder - integrate Azure/Google/AWS TTS here");
                
                // Placeholder: Return a test audio URL using public music endpoint
                // In production, this would be the HLS URL of the generated news audio
                return "http://localhost:5000/api/music/hls/news/tosin-news-" + DateTime.UtcNow.ToString("yyyyMMddHH") + ".mp3";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating news audio");
                return null;
            }
        }

        public async Task<string?> GenerateNewsNowAsync()
        {
            // Manual trigger for testing
            _logger.LogInformation("Manual news generation triggered");
            
            var headlines = await _newsService.GetTopHeadlinesAsync("ng", 10);
            var script = _newsService.FormatNewsScript(headlines);
            var audioUrl = await GenerateNewsAudioAsync(script, CancellationToken.None);
            
            if (!string.IsNullOrEmpty(audioUrl))
            {
                _currentNewsAudioUrl = audioUrl;
                _newsReadingStartTime = DateTime.UtcNow;
            }
            
            return audioUrl;
        }
    }
}
