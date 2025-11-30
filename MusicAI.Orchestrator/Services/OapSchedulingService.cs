using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MusicAI.Orchestrator.Data;

namespace MusicAI.Orchestrator.Services
{
    public class OapSchedulingService
    {
        private readonly OapAgentsRepository _oapRepo;

        public OapSchedulingService(OapAgentsRepository oapRepo)
        {
            _oapRepo = oapRepo;
        }

        public OapAgent? GetCurrentOapAgent()
        {
            var currentTime = DateTime.UtcNow.TimeOfDay;
            return _oapRepo.GetByTimeSlotAsync(currentTime).Result;
        }

        public async Task<OapAgent?> GetCurrentOapAgentAsync()
        {
            var currentTime = DateTime.UtcNow.TimeOfDay;
            return await _oapRepo.GetByTimeSlotAsync(currentTime);
        }

        public async Task<string> GetCurrentGenreAsync()
        {
            var agent = await GetCurrentOapAgentAsync();
            return agent?.Genre ?? "afrobeats";
        }

        public async Task<List<OapAgent>> GetAllOapsAsync()
        {
            return await _oapRepo.GetAllActiveAsync();
        }

        public async Task<OapAgent?> GetOapByIdAsync(string oapId)
        {
            return await _oapRepo.GetByIdAsync(oapId);
        }

        /// <summary>
        /// Get the next OAP that will be on air
        /// </summary>
        public async Task<(OapAgent? nextOap, TimeSpan timeUntilStart)?> GetNextOapAsync()
        {
            var allOaps = await GetAllOapsAsync();
            var currentTime = DateTime.UtcNow.TimeOfDay;

            // Find next OAP
            var nextOap = allOaps
                .Where(o => o.StartTimeUtc > currentTime)
                .OrderBy(o => o.StartTimeUtc)
                .FirstOrDefault();

            if (nextOap != null)
            {
                var timeUntil = nextOap.StartTimeUtc - currentTime;
                return (nextOap, timeUntil);
            }

            // If no OAP after current time, get first one tomorrow
            var firstOap = allOaps.OrderBy(o => o.StartTimeUtc).FirstOrDefault();
            if (firstOap != null)
            {
                var timeUntil = TimeSpan.FromHours(24) - currentTime + firstOap.StartTimeUtc;
                return (firstOap, timeUntil);
            }

            return null;
        }
    }
}
