namespace MusicAI.Orchestrator.Services
{
    public class TranscribedWord
    {
        public string Text { get; set; } = string.Empty;
        public double Start { get; set; }
        public double End { get; set; }
    }
}
