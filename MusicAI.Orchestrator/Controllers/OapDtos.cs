using System;
using System.Collections.Generic;

namespace MusicAI.Orchestrator.Controllers
{
    // Small DTOs extracted from OapChatController to keep the controller file focused
    public class OapChatRequest
    {
        public string UserId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Language { get; set; } = "en";
        public int Count { get; set; } = 5;
        public bool Light { get; set; } = false;
        public bool All { get; set; } = false;
        public string? PlayNowS3Key { get; set; } = null;
        public bool EnableChat { get; set; } = true;
        public int Offset { get; set; } = 0;
    }

    public class OapChatResponse
    {
        public string OapName { get; set; } = string.Empty;
        public string OapPersonality { get; set; } = string.Empty;
        public string ResponseText { get; set; } = string.Empty;
        public List<MusicTrackDto> MusicPlaylist { get; set; } = new();
        public string? CurrentAudioUrl { get; set; }
        public string? ContinuousHlsUrl { get; set; }
        public BufferConfigDto? BufferConfig { get; set; }
        public string? NextUrl { get; set; }
        public string? DetectedMood { get; set; }
        public string? DetectedIntent { get; set; }
        public bool IsNewsSegment { get; set; }
    }

    public class MusicTrackDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string S3Key { get; set; } = string.Empty;
        public string HlsUrl { get; set; } = string.Empty;
        public string StreamUrl { get; set; } = string.Empty;
        public string? Duration { get; set; }
        public string? Genre { get; set; }
    }

    public class BufferConfigDto
    {
        public int MinBufferSeconds { get; set; }
        public int MaxBufferSeconds { get; set; }
        public int PrefetchTracks { get; set; }
        public int ResumeBufferSeconds { get; set; }
    }

    internal class MessageAnalysis
    {
        public string Mood { get; set; } = "neutral";
        public string Intent { get; set; } = "listen";
    }

    internal class OapContext
    {
        public string Name { get; set; } = string.Empty;
        public string Personality { get; set; } = string.Empty;
        public string ShowName { get; set; } = string.Empty;
    }
}
