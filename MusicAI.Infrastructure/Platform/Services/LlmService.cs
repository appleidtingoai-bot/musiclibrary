using System.Text.Json;
using MusicAI.Infrastructure.Services;

namespace Platform.Services
{
    public class LlmService
    {
        private readonly SageMakerClientService _sm;

        public LlmService(SageMakerClientService sm)
        {
            _sm = sm;
        }

        public class LlmResponse { public string Text { get; set; } }

        public async Task<string> GenerateAsync(string prompt)
        {
            var payload = JsonSerializer.Serialize(new { prompt, max_tokens = 200 });
            var raw = await _sm.InvokeModelAsync(payload);
            var res = JsonSerializer.Deserialize<LlmResponse>(raw);
            return res?.Text ?? string.Empty;
        }
    }
}
