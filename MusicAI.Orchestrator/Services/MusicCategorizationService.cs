using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MusicAI.Orchestrator.Models;

namespace MusicAI.Orchestrator.Services
{
    /// <summary>
    /// Intelligent music categorization service
    /// Analyzes music files and automatically determines the best folder/genre
    /// Uses filename analysis, metadata extraction, and pattern matching
    /// </summary>
    public class MusicCategorizationService
    {
        // Genre/vibe keywords for intelligent categorization
        private static readonly Dictionary<string, List<string>> CategoryKeywords = new()
        {
            ["hip-hop"] = new List<string> { "hiphop", "hip hop", "rap", "trap", "beats", "freestyle", "mc ", "rapper", "urban", "street" },
            ["rnb"] = new List<string> { "r&b", "rnb", "soul", "rhythm and blues", "smooth", "neo soul", "contemporary r&b" },
            ["gospel"] = new List<string> { "gospel", "praise", "worship", "spiritual", "christian", "hymn", "church", "blessed", "holy" },
            ["afrobeats"] = new List<string> { "afrobeat", "afro beat", "afro", "nigerian", "ghana", "african", "amapiano", "afropop" },
            ["jazz"] = new List<string> { "jazz", "bebop", "swing", "blues jazz", "smooth jazz", "saxophone", "improvisation" },
            ["electronic"] = new List<string> { "edm", "electronic", "techno", "trance", "dubstep", "electro", "synth", "digital" },
            ["pop"] = new List<string> { "pop", "mainstream", "chart", "top 40", "commercial", "radio" },
            ["rock"] = new List<string> { "rock", "metal", "punk", "alternative rock", "indie rock", "hard rock", "guitar" },
            ["country"] = new List<string> { "country", "western", "nashville", "bluegrass", "americana", "cowboy" },
            ["reggae"] = new List<string> { "reggae", "dancehall", "ska", "dub", "jamaican", "rasta", "caribbean" },
            ["classical"] = new List<string> { "classical", "orchestra", "symphony", "piano", "violin", "opera", "baroque", "mozart", "beethoven" },
            ["blues"] = new List<string> { "blues", "delta blues", "chicago Blues", "acoustic blues", "electric blues" },
            ["latin"] = new List<string> { "latin", "salsa", "reggaeton", "bachata", "merengue", "spanish", "latino", "hispanic" },
            ["soul"] = new List<string> { "soul", "motown", "funk soul", "deep soul", "northern soul" },
            ["funk"] = new List<string> { "funk", "funky", "groove", "p-funk", "disco", "boogie" },
            ["indie"] = new List<string> { "indie", "independent", "alternative", "underground", "hipster", "art rock" },
            ["folk"] = new List<string> { "folk", "acoustic", "traditional", "singer songwriter", "ballad", "country folk" },
            ["house"] = new List<string> { "house", "deep house", "tech house", "progressive house", "club", "dance" },
            ["ambient"] = new List<string> { "ambient", "atmospheric", "soundscape", "chill out", "downtempo", "meditation" },
            ["lo-fi"] = new List<string> { "lofi", "lo-fi", "chillhop", "study", "beats to relax", "chill beats", "lazy" }
        };

        // Mood keywords that can help determine category
        private static readonly Dictionary<string, List<string>> MoodToCategoryMap = new()
        {
            ["energetic"] = new List<string> { "hip-hop", "electronic", "rock", "afrobeats", "house", "funk" },
            ["relaxed"] = new List<string> { "jazz", "ambient", "lo-fi", "reggae", "classical" },
            ["romantic"] = new List<string> { "rnb", "soul", "jazz", "latin" },
            ["spiritual"] = new List<string> { "gospel", "classical", "ambient" },
            ["dancing"] = new List<string> { "afrobeats", "house", "funk", "latin", "pop" },
            ["melancholic"] = new List<string> { "blues", "indie", "folk" }
        };

        /// <summary>
        /// Analyze music file and determine the best category
        /// Returns folder name and confidence score (0-100)
        /// </summary>
        public (string folder, int confidence, string reasoning) CategorizeMusicFile(
            string fileName,
            Stream? audioStream = null,
            string? userHint = null)
        {
            var scores = new Dictionary<string, int>();
            var reasoningParts = new List<string>();

            // Initialize all folders with base score of 0
            foreach (var folder in MusicFolder.All)
            {
                scores[folder.Name] = 0;
            }

            // 1. Analyze filename
            var filenameLower = fileName.ToLowerInvariant();
            var filenameScore = AnalyzeFilename(filenameLower, scores, reasoningParts);

            // 2. User hint (if provided, give it high weight)
            if (!string.IsNullOrWhiteSpace(userHint))
            {
                var hintScore = AnalyzeUserHint(userHint.ToLowerInvariant(), scores, reasoningParts);
            }

            // 3. Extract metadata from filename patterns (e.g., "[Genre] - Artist - Title.mp3")
            AnalyzeFilenamePattern(filenameLower, scores, reasoningParts);

            // 4. If we have audio stream, analyze audio characteristics (placeholder for future)
            if (audioStream != null)
            {
                // Future: Analyze tempo, key, energy level
                // For now, just note that we could do this
                reasoningParts.Add("Audio analysis not yet implemented");
            }

            // Find the folder with highest score
            var bestMatch = scores.OrderByDescending(kv => kv.Value).First();
            var confidence = Math.Min(100, bestMatch.Value);

            // If confidence is too low, default to 'pop' as a safe catch-all
            if (confidence < 20)
            {
                reasoningParts.Add("Low confidence, defaulting to 'pop' as general category");
                return ("pop", 20, string.Join("; ", reasoningParts));
            }

            return (bestMatch.Key, confidence, string.Join("; ", reasoningParts));
        }

        private int AnalyzeFilename(string filenameLower, Dictionary<string, int> scores, List<string> reasoning)
        {
            var maxScore = 0;
            foreach (var category in CategoryKeywords)
            {
                var folder = category.Key;
                var keywords = category.Value;

                foreach (var keyword in keywords)
                {
                    if (filenameLower.Contains(keyword))
                    {
                        var points = keyword.Length * 10; // Longer, more specific keywords get more points
                        scores[folder] += points;
                        maxScore = Math.Max(maxScore, points);
                        reasoning.Add($"Found '{keyword}' in filename → +{points} for {folder}");
                    }
                }
            }
            return maxScore;
        }

        private int AnalyzeUserHint(string hintLower, Dictionary<string, int> scores, List<string> reasoning)
        {
            // User hint gets 50 points if it matches a category
            foreach (var folder in MusicFolder.All)
            {
                if (hintLower.Contains(folder.Name) ||
                    hintLower.Contains(folder.DisplayName.ToLowerInvariant()))
                {
                    scores[folder.Name] += 50;
                    reasoning.Add($"User hint '{hintLower}' matches {folder.DisplayName} → +50");
                    return 50;
                }

                // Check if hint matches any keyword for this category
                if (CategoryKeywords.TryGetValue(folder.Name, out var keywords))
                {
                    foreach (var keyword in keywords)
                    {
                        if (hintLower.Contains(keyword))
                        {
                            scores[folder.Name] += 30;
                            reasoning.Add($"User hint contains '{keyword}' → +30 for {folder.DisplayName}");
                            return 30;
                        }
                    }
                }
            }
            return 0;
        }

        private void AnalyzeFilenamePattern(string filenameLower, Dictionary<string, int> scores, List<string> reasoning)
        {
            // Common patterns:
            // [Genre] - Artist - Title.mp3
            // Artist - Title (Genre).mp3
            // Genre - Title - Artist.mp3

            var bracketMatch = Regex.Match(filenameLower, @"\[([^\]]+)\]");
            if (bracketMatch.Success)
            {
                var bracketContent = bracketMatch.Groups[1].Value;
                AnalyzeUserHint(bracketContent, scores, reasoning);
            }

            var parenMatch = Regex.Match(filenameLower, @"\(([^\)]+)\)");
            if (parenMatch.Success)
            {
                var parenContent = parenMatch.Groups[1].Value;
                AnalyzeUserHint(parenContent, scores, reasoning);
            }
        }

        /// <summary>
        /// Batch categorize multiple files
        /// Returns suggestions for each file
        /// </summary>
        public List<(string fileName, string suggestedFolder, int confidence, string reasoning)>
            BatchCategorize(IEnumerable<string> fileNames, string? userHint = null)
        {
            var results = new List<(string, string, int, string)>();

            foreach (var fileName in fileNames)
            {
                var (folder, confidence, reasoning) = CategorizeMusicFile(fileName, null, userHint);
                results.Add((fileName, folder, confidence, reasoning));
            }

            return results;
        }

        /// <summary>
        /// Get category distribution for a batch of files
        /// Useful for showing admin what will be categorized where
        /// </summary>
        public Dictionary<string, int> GetCategoryDistribution(IEnumerable<string> fileNames, string? userHint = null)
        {
            var distribution = new Dictionary<string, int>();

            foreach (var fileName in fileNames)
            {
                var (folder, _, _) = CategorizeMusicFile(fileName, null, userHint);

                if (!distribution.ContainsKey(folder))
                    distribution[folder] = 0;

                distribution[folder]++;
            }

            return distribution;
        }
    }
}
