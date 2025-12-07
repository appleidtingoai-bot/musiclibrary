using System.Collections.Generic;
using System.Threading.Tasks;

namespace MusicAI.Orchestrator.Services
{
    public class QueueItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Artist { get; set; }
        public string? S3Key { get; set; }
        public int? DurationSeconds { get; set; }
    }

    public class PlayerStateDto
    {
        public bool IsPlaying { get; set; }
        public int CurrentIndex { get; set; }
        public double Volume { get; set; }
        public List<QueueItemDto> Queue { get; set; } = new List<QueueItemDto>();
    }

    public interface IPlayerService
    {
        PlayerStateDto GetState(string userId);
        Task<PlayerStateDto> Enqueue(string userId, QueueItemDto item, bool toFront = false);
        Task<PlayerStateDto> Play(string userId);
        Task<PlayerStateDto> Pause(string userId);
        Task<PlayerStateDto> Skip(string userId);
        Task<PlayerStateDto> Previous(string userId);
        Task<PlayerStateDto> SetVolume(string userId, double volume);
        Task<PlayerStateDto> Shuffle(string userId, bool enable);
        event System.EventHandler<(string userId, PlayerStateDto state)>? StateChanged;
    }
}
