using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using MusicAI.Agents.Models;

namespace MusicAI.Agents.Plugins
{
    public class MusicPlugin
    {
        private readonly object _musicRepo;
        private readonly object? _oapService;
        private string? _lastDetectedMood;
        private List<object>? _lastRecommendations;
        
        public MusicPlugin(object musicRepository, object? oapSchedulingService = null)
        {
            _musicRepo = musicRepository ?? throw new ArgumentNullException(nameof(musicRepository));
            _oapService = oapSchedulingService;
        }
        
        [KernelFunction("search_music")]
        [Description("Search for music tracks by mood, genre, or artist. Returns a list of matching songs.")]
        public async Task<string> SearchMusicAsync(
            [Description("The emotional mood like 'happy', 'energetic', 'calm', 'romantic', 'sad'")] string? mood = null,
            [Description("The music genre like 'afrobeats', 'gospel', 'hiphop'")] string? genre = null,
            [Description("The artist name")] string? artist = null)
        {
            try
            {
                // Use reflection to call SearchAsync dynamically
                var method = _musicRepo.GetType().GetMethod("SearchAsync");
                if (method == null) return "Error: Music search not available";
                
                var task = (Task<dynamic>)method.Invoke(_musicRepo, new object[] { mood, genre, artist, 10 });
                var tracks = await task;
                
                _lastRecommendations = new List<object>(tracks as IEnumerable<object> ?? new List<object>());
                
                if (_lastRecommendations.Count == 0)
                {
                    return $"No music found matching mood='{mood}', genre='{genre}', artist='{artist}'";
                }
                
                var trackList = string.Join("\n", _lastRecommendations.Take(5).Select((t, i) => 
                {
                    var title = t.GetType().GetProperty("Title")?.GetValue(t);
                    var artistName = t.GetType().GetProperty("Artist")?.GetValue(t);
                    var id = t.GetType().GetProperty("Id")?.GetValue(t);
                    return $"{i+1}. \"{title}\" by {artistName} (ID: {id})";
                }));
                
                return $"Found {_lastRecommendations.Count} tracks:\n{trackList}";
            }
            catch (Exception ex)
            {
                return $"Error searching music: {ex.Message}";
            }
        }
        
        [KernelFunction("detect_mood")]
        [Description("Analyze the user's message to detect their emotional mood. Returns a single mood word.")]
        public string DetectMood(
            [Description("The user's message to analyze")] string userMessage)
        {
            // Simple keyword-based mood detection (can be enhanced with OpenAI later)
            var message = userMessage.ToLower();
            
            if (message.Contains("happy") || message.Contains("joyful") || message.Contains("excited") || message.Contains("great"))
                _lastDetectedMood = "happy";
            else if (message.Contains("sad") || message.Contains("down") || message.Contains("depressed") || message.Contains("crying"))
                _lastDetectedMood = "sad";
            else if (message.Contains("energetic") || message.Contains("pump") || message.Contains("workout") || message.Contains("dance"))
                _lastDetectedMood = "energetic";
            else if (message.Contains("calm") || message.Contains("relax") || message.Contains("chill") || message.Contains("peaceful"))
                _lastDetectedMood = "calm";
            else if (message.Contains("romantic") || message.Contains("love") || message.Contains("romance"))
                _lastDetectedMood = "romantic";
            else if (message.Contains("stressed") || message.Contains("anxious") || message.Contains("worried"))
                _lastDetectedMood = "calm"; // Recommend calming music for stress
            else
                _lastDetectedMood = "neutral";
                
            return _lastDetectedMood;
        }
        
        [KernelFunction("get_current_genre")]
        [Description("Get the music genre for the currently active OAP agent")]
        public string GetCurrentGenre()
        {
            try
            {
                if (_oapService == null) return "afrobeats"; // Default
                
                var method = _oapService.GetType().GetMethod("GetCurrentOapAgent");
                if (method == null) return "afrobeats";
                
                var agent = method.Invoke(_oapService, null);
                if (agent == null) return "afrobeats";
                    
                var genreProp = agent.GetType().GetProperty("Genre");
                return genreProp?.GetValue(agent)?.ToString() ?? "afrobeats";
            }
            catch
            {
                return "afrobeats";
            }
        }
        
        // Helper methods (not exposed to AI)
        public string? GetLastDetectedMood() => _lastDetectedMood;
        public List<object>? GetLastRecommendations() => _lastRecommendations;
    }
}
