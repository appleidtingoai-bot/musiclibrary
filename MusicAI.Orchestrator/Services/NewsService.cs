using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MusicAI.Orchestrator.Services
{
    public class NewsService
    {
        private readonly HttpClient _httpClient;
        private readonly string? _newsApiKey;

        public NewsService(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient();
            _newsApiKey = Environment.GetEnvironmentVariable("NEWS_API_KEY");
        }

        public async Task<List<NewsHeadline>> GetTopHeadlinesAsync(string country = "ng", int count = 10)
        {
            try
            {
                if (string.IsNullOrEmpty(_newsApiKey))
                {
                    // Return mock headlines if no API key
                    return GetMockHeadlines();
                }

                var url = $"https://newsapi.org/v2/top-headlines?country={country}&pageSize={count}&apiKey={_newsApiKey}";
                var response = await _httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    return GetMockHeadlines();
                }

                var json = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<NewsApiResponse>(json);
                
                return data?.Articles?.Select(a => new NewsHeadline
                {
                    Title = a.Title ?? "No title",
                    Description = a.Description ?? "",
                    Source = a.Source?.Name ?? "Unknown",
                    PublishedAt = a.PublishedAt,
                    Url = a.Url ?? ""
                }).Take(count).ToList() ?? GetMockHeadlines();
            }
            catch
            {
                return GetMockHeadlines();
            }
        }

        public string FormatNewsScript(List<NewsHeadline> headlines)
        {
            var script = "Good morning, I'm Tosin with News on the Hour. Here are today's top stories.\n\n";
            
            for (int i = 0; i < headlines.Count; i++)
            {
                var headline = headlines[i];
                script += $"Story {i + 1}: {headline.Title}. ";
                
                if (!string.IsNullOrEmpty(headline.Description))
                {
                    // Clean and simplify description
                    var cleanDesc = CleanText(headline.Description);
                    script += $"{cleanDesc}. ";
                }
                
                script += $"Source: {headline.Source}.\n\n";
            }
            
            script += "That's all for now. Stay tuned for more updates. I'm Tosin, and you've been listening to News on the Hour.";
            
            return script;
        }

        private string CleanText(string text)
        {
            // Remove HTML tags, extra spaces, and special characters
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            text = text.Replace("&nbsp;", " ")
                       .Replace("&amp;", "and")
                       .Replace("&quot;", "\"")
                       .Replace("&#39;", "'");
            return text.Trim();
        }

        private List<NewsHeadline> GetMockHeadlines()
        {
            return new List<NewsHeadline>
            {
                new NewsHeadline
                {
                    Title = "Nigerian Economy Shows Strong Growth",
                    Description = "Latest economic indicators show positive trends in Nigeria's GDP growth",
                    Source = "Business Daily",
                    PublishedAt = DateTime.UtcNow
                },
                new NewsHeadline
                {
                    Title = "Afrobeats Artists Win International Awards",
                    Description = "Nigerian artists continue to dominate global music charts",
                    Source = "Entertainment News",
                    PublishedAt = DateTime.UtcNow
                },
                new NewsHeadline
                {
                    Title = "Technology Innovation Drives Youth Employment",
                    Description = "Tech startups create thousands of jobs across major cities",
                    Source = "Tech Today",
                    PublishedAt = DateTime.UtcNow
                }
            };
        }

        private class NewsApiResponse
        {
            public List<Article>? Articles { get; set; }
        }

        private class Article
        {
            public ArticleSource? Source { get; set; }
            public string? Title { get; set; }
            public string? Description { get; set; }
            public string? Url { get; set; }
            public DateTime PublishedAt { get; set; }
        }

        private class ArticleSource
        {
            public string? Name { get; set; }
        }
    }

    public class NewsHeadline
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime PublishedAt { get; set; }
        public string Url { get; set; } = string.Empty;
    }
}
