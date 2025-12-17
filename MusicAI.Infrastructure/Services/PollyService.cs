using Amazon.Polly;
using Amazon.Polly.Model;
using Microsoft.Extensions.Configuration;

namespace MusicAI.Infrastructure.Services
{
    public class PollyService
    {
        private readonly AmazonPollyClient _client;
        private readonly IS3Service _s3;
        private readonly string _voice;

        public PollyService(IConfiguration cfg, IS3Service s3)
        {
            _client = new AmazonPollyClient(cfg["AWS:AccessKey"], cfg["AWS:SecretKey"], Amazon.RegionEndpoint.GetBySystemName(cfg["AWS:Region"]));
            _s3 = s3;
            _voice = cfg["AWS:PollyVoice"] ?? "Joanna";
        }

        public async Task<string> SynthesizeToS3Url(string text, string key)
        {
            var req = new SynthesizeSpeechRequest
            {
                OutputFormat = OutputFormat.Mp3,
                Text = text,
                VoiceId = _voice
            };
            var res = await _client.SynthesizeSpeechAsync(req);
            using var ms = new MemoryStream();
            await res.AudioStream.CopyToAsync(ms);
            ms.Position = 0;
            await using var uploadStream = new MemoryStream(ms.ToArray());
            uploadStream.Position = 0;
            await _s3.UploadFileAsync(key, uploadStream, "audio/mpeg");
            return await _s3.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(30));
        }

        // New API: synthesize speech and return the audio bytes as a MemoryStream
        public async Task<MemoryStream> SynthesizeToStreamAsync(string text)
        {
            var req = new SynthesizeSpeechRequest
            {
                OutputFormat = OutputFormat.Mp3,
                Text = text,
                VoiceId = _voice
            };
            var res = await _client.SynthesizeSpeechAsync(req);
            var ms = new MemoryStream();
            await res.AudioStream.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }
    }
}
