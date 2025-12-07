using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MusicAI.Orchestrator.Services
{
    public class InMemoryPlayerService : IPlayerService
    {
        private readonly ConcurrentDictionary<string, PlayerStateDto> _states = new();

        public event EventHandler<(string userId, PlayerStateDto state)>? StateChanged;

        private PlayerStateDto EnsureState(string userId)
        {
            return _states.GetOrAdd(userId, id => new PlayerStateDto { IsPlaying = false, CurrentIndex = 0, Volume = 1.0 });
        }

        public PlayerStateDto GetState(string userId)
        {
            return EnsureState(userId);
        }

        public Task<PlayerStateDto> Enqueue(string userId, QueueItemDto item, bool toFront = false)
        {
            var s = EnsureState(userId);
            lock (s)
            {
                if (toFront) s.Queue.Insert(0, item);
                else s.Queue.Add(item);
            }
            StateChanged?.Invoke(this, (userId, s));
            return Task.FromResult(s);
        }

        public Task<PlayerStateDto> Play(string userId)
        {
            var s = EnsureState(userId);
            s.IsPlaying = true;
            StateChanged?.Invoke(this, (userId, s));
            return Task.FromResult(s);
        }

        public Task<PlayerStateDto> Pause(string userId)
        {
            var s = EnsureState(userId);
            s.IsPlaying = false;
            StateChanged?.Invoke(this, (userId, s));
            return Task.FromResult(s);
        }

        public Task<PlayerStateDto> Skip(string userId)
        {
            var s = EnsureState(userId);
            lock (s)
            {
                if (s.CurrentIndex < s.Queue.Count - 1) s.CurrentIndex++;
                else s.CurrentIndex = Math.Min(s.Queue.Count - 1, s.CurrentIndex);
            }
            StateChanged?.Invoke(this, (userId, s));
            return Task.FromResult(s);
        }

        public Task<PlayerStateDto> Previous(string userId)
        {
            var s = EnsureState(userId);
            lock (s)
            {
                if (s.CurrentIndex > 0) s.CurrentIndex--;
            }
            StateChanged?.Invoke(this, (userId, s));
            return Task.FromResult(s);
        }

        public Task<PlayerStateDto> SetVolume(string userId, double volume)
        {
            var s = EnsureState(userId);
            s.Volume = Math.Clamp(volume, 0.0, 1.0);
            StateChanged?.Invoke(this, (userId, s));
            return Task.FromResult(s);
        }

        public Task<PlayerStateDto> Shuffle(string userId, bool enable)
        {
            var s = EnsureState(userId);
            lock (s)
            {
                if (enable)
                {
                    var rnd = new Random();
                    s.Queue = s.Queue.OrderBy(_ => rnd.Next()).ToList();
                    s.CurrentIndex = 0;
                }
            }
            StateChanged?.Invoke(this, (userId, s));
            return Task.FromResult(s);
        }
    }
}
