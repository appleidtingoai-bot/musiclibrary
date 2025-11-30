using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

public class LlmService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string? _apiKey;
    private readonly string _model;

    public LlmService(IConfiguration config, IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
            _apiKey = config["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"); // plain OpenAI key (recommended)
        _model = config["OPENAI_MODEL"] ?? "gpt-3.5-turbo";
    }

    public async Task<string> GenerateReplyAsync(string prompt, string context)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            // fallback stub when no key provided
            await Task.CompletedTask;
            return $"(Tosin) [stub] I heard '{prompt}'. Here's a quick reply.";
        }

        try
        {
            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var systemMsg = "You are Tosin, a friendly radio DJ and jingle creator. Keep replies short and upbeat.";
            var messages = new[] {
                new { role = "system", content = systemMsg },
                new { role = "user", content = prompt + "\nContext:\n" + (context ?? string.Empty) }
            };

            var payload = new
            {
                model = _model,
                messages = messages,
                max_tokens = 256,
                temperature = 0.8
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
            resp.EnsureSuccessStatusCode();
            var respText = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message").GetProperty("content").GetString();
                return message ?? string.Empty;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            // keep app resilient and return a fallback
            return $"(Tosin) [error] LLM call failed: {ex.Message}";
        }
    }
}
