using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace MusicAI.Infrastructure.Services
{
    // Very small in-process circuit breaker to protect manifest assembly/signing
    public class SimpleCircuitBreaker
    {
        private readonly int _failureThreshold;
        private readonly TimeSpan _samplingWindow;
        private readonly TimeSpan _openDuration;

        private readonly ConcurrentQueue<DateTime> _failures = new ConcurrentQueue<DateTime>();
        private DateTime? _openUntil;

        public SimpleCircuitBreaker(int failureThreshold = 10, TimeSpan? samplingWindow = null, TimeSpan? openDuration = null)
        {
            _failureThreshold = failureThreshold;
            _samplingWindow = samplingWindow ?? TimeSpan.FromSeconds(30);
            _openDuration = openDuration ?? TimeSpan.FromSeconds(60);
        }

        public bool IsOpen
        {
            get
            {
                if (_openUntil == null) return false;
                if (DateTime.UtcNow > _openUntil.Value)
                {
                    _openUntil = null;
                    return false;
                }
                return true;
            }
        }

        public void MarkFailure()
        {
            var now = DateTime.UtcNow;
            _failures.Enqueue(now);

            // Purge old failures
            while (_failures.TryPeek(out var ts) && ts < now - _samplingWindow)
            {
                _failures.TryDequeue(out _);
            }

            if (_failures.Count >= _failureThreshold)
            {
                _openUntil = DateTime.UtcNow.Add(_openDuration);
            }
        }

        public void MarkSuccess()
        {
            // On success, clear some failures to help recovery
            while (_failures.Count > 0)
            {
                _failures.TryDequeue(out _);
            }
            _openUntil = null;
        }

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            if (IsOpen) throw new InvalidOperationException("Circuit is open");
            try
            {
                var result = await action();
                MarkSuccess();
                return result;
            }
            catch
            {
                MarkFailure();
                throw;
            }
        }
    }
}
