using System.Text.Json;
using MusicAI.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace Platform.Services
{
    public class CustomTtsService
    {
        private readonly SageMakerClientService _sm;
        private readonly MusicAI.Infrastructure.Services.IS3Service _s3;
        private readonly string _cloudfront;

        public CustomTtsService(SageMakerClientService sm, MusicAI.Infrastructure.Services.IS3Service s3, IConfiguration cfg)
        {
            _sm = sm;
            _s3 = s3;
            _cloudfront = cfg["AWS:CloudFrontDomain"] ?? "dummydomain.cloudfront.net";
        }

        public class TtsResponse { public string Base64Audio { get; set; } }

        public async Task<string> GenerateAudioUrl(string text, string persona)
        {
            var payload = JsonSerializer.Serialize(new { text, voice = persona });
            var result = await _sm.InvokeModelAsync(payload);

            var obj = JsonSerializer.Deserialize<TtsResponse>(result);
            var audio = Convert.FromBase64String(obj.Base64Audio);

            var key = $"tts/{persona}/{Guid.NewGuid()}.wav";
            using var ms = new MemoryStream(audio);
            await _s3.UploadFileAsync(key, ms, "audio/wav");

            return $"https://{_cloudfront}/{key}";
        }
    }
}
