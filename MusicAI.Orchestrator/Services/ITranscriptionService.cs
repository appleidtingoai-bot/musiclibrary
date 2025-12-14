using System.Collections.Generic;
using System.Threading.Tasks;

namespace MusicAI.Orchestrator.Services
{
    public interface ITranscriptionService
    {
        Task<List<TranscribedWord>> TranscribeWithTimestamps(string localFilePath);
    }
}
