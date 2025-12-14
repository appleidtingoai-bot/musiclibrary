using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MusicAI.Orchestrator.Services
{
    /// <summary>
    /// A minimal OpenAI Whisper HTTP transcription implementation.
    /// Requires configuration key: OpenAI:ApiKey
    /// Uses the /v1/audio/transcriptions endpoint and attempts to extract word timestamps when available.
    /// </summary>
    public class OpenAITranscriptionService : ITranscriptionService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<OpenAITranscriptionService> _logger;

        public OpenAITranscriptionService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<OpenAITranscriptionService> logger)
        {
            _httpFactory = httpFactory;
            _config = config;
            _logger = logger;
        }

        public async Task<List<TranscribedWord>> TranscribeWithTimestamps(string localFilePath)
        {
            var apiKey = _config["OpenAI:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("OpenAI API key not configured (OpenAI:ApiKey)");

            using var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var content = new MultipartFormDataContent();
            // model parameter may vary; whisper-1 is a commonly used model name
            content.Add(new StringContent("whisper-1"), "model");

            var fileStream = File.OpenRead(localFilePath);
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            content.Add(streamContent, "file", Path.GetFileName(localFilePath));

            try
            {
                var resp = await client.PostAsync("https://api.openai.com/v1/audio/transcriptions", content);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenAI transcription failed ({Status}): {Body}", resp.StatusCode, body);
                    return new List<TranscribedWord>();
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var words = new List<TranscribedWord>();

                // Try to find segments -> words with start/end
                if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var seg in segments.EnumerateArray())
                    {
                        if (seg.TryGetProperty("words", out var wlist) && wlist.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var w in wlist.EnumerateArray())
                            {
                                try
                                {
                                    var text = w.GetProperty("word").GetString() ?? string.Empty;
                                    var start = w.GetProperty("start").GetDouble();
                                    var end = w.GetProperty("end").GetDouble();
                                    words.Add(new TranscribedWord { Text = text, Start = start, End = end });
                                }
                                catch { }
                            }
                        }
                        else
                        {
                            // Fallback: segment-level timestamps with text — try to split into words evenly
                            try
                            {
                                var segText = seg.GetProperty("text").GetString() ?? string.Empty;
                                var segStart = seg.GetProperty("start").GetDouble();
                                var segEnd = seg.GetProperty("end").GetDouble();
                                var tokens = segText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (tokens.Length > 0)
                                {
                                    var segLen = segEnd - segStart;
                                    var per = segLen / tokens.Length;
                                    for (int i = 0; i < tokens.Length; i++)
                                    {
                                        words.Add(new TranscribedWord { Text = tokens[i], Start = segStart + i * per, End = Math.Min(segEnd, segStart + (i + 1) * per) });
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                return words;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI transcription error");
                return new List<TranscribedWord>();
            }
            finally
            {
                try { fileStream.Dispose(); } catch { }
            }
        }
    }
}
