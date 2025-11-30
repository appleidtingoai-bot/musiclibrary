using System.Collections.Generic;
using System.Linq;

namespace MusicAI.Orchestrator.Models
{
    /// <summary>
    /// Defines music folders/genres for organizing uploaded music.
    /// Each folder represents a genre/vibe that OAPs can tap into based on mood and personality.
    /// </summary>
    public class MusicFolder
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Moods { get; set; } = new List<string>();
        public List<string> Vibes { get; set; } = new List<string>();

        /// <summary>
        /// All available music folders for the system.
        /// OAPs can select from these based on their personality and current mood.
        /// </summary>
        public static readonly List<MusicFolder> All = new List<MusicFolder>
        {
            new MusicFolder
            {
                Name = "hip-hop",
                DisplayName = "Hip Hop",
                Description = "Urban beats, rap, and hip hop culture",
                Moods = new List<string> { "energetic", "confident", "bold", "rebellious" },
                Vibes = new List<string> { "urban", "street", "powerful", "rhythmic" }
            },
            new MusicFolder
            {
                Name = "rnb",
                DisplayName = "R&B",
                Description = "Rhythm and blues, soul, and contemporary R&B",
                Moods = new List<string> { "romantic", "smooth", "sensual", "emotional" },
                Vibes = new List<string> { "smooth", "sultry", "groovy", "intimate" }
            },
            new MusicFolder
            {
                Name = "gospel",
                DisplayName = "Gospel",
                Description = "Inspirational and spiritual gospel music",
                Moods = new List<string> { "uplifting", "spiritual", "joyful", "hopeful" },
                Vibes = new List<string> { "inspirational", "powerful", "soulful", "blessed" }
            },
            new MusicFolder
            {
                Name = "afrobeats",
                DisplayName = "Afrobeats",
                Description = "Modern African rhythms and Afrobeats",
                Moods = new List<string> { "energetic", "joyful", "dancing", "celebratory" },
                Vibes = new List<string> { "vibrant", "rhythmic", "cultural", "infectious" }
            },
            new MusicFolder
            {
                Name = "jazz",
                DisplayName = "Jazz",
                Description = "Classic and contemporary jazz",
                Moods = new List<string> { "relaxed", "sophisticated", "contemplative", "smooth" },
                Vibes = new List<string> { "elegant", "improvisational", "classy", "timeless" }
            },
            new MusicFolder
            {
                Name = "electronic",
                DisplayName = "Electronic",
                Description = "EDM, techno, and electronic music",
                Moods = new List<string> { "energetic", "futuristic", "intense", "euphoric" },
                Vibes = new List<string> { "digital", "synthetic", "pulsing", "hypnotic" }
            },
            new MusicFolder
            {
                Name = "pop",
                DisplayName = "Pop",
                Description = "Popular mainstream music",
                Moods = new List<string> { "happy", "upbeat", "catchy", "fun" },
                Vibes = new List<string> { "mainstream", "accessible", "melodic", "commercial" }
            },
            new MusicFolder
            {
                Name = "rock",
                DisplayName = "Rock",
                Description = "Rock, alternative, and indie rock",
                Moods = new List<string> { "energetic", "rebellious", "passionate", "intense" },
                Vibes = new List<string> { "raw", "powerful", "guitar-driven", "authentic" }
            },
            new MusicFolder
            {
                Name = "country",
                DisplayName = "Country",
                Description = "Country and Americana music",
                Moods = new List<string> { "nostalgic", "storytelling", "heartfelt", "honest" },
                Vibes = new List<string> { "rustic", "traditional", "down-to-earth", "wholesome" }
            },
            new MusicFolder
            {
                Name = "reggae",
                DisplayName = "Reggae",
                Description = "Reggae, dancehall, and Caribbean vibes",
                Moods = new List<string> { "relaxed", "positive", "carefree", "peaceful" },
                Vibes = new List<string> { "island", "laid-back", "rhythmic", "chill" }
            },
            new MusicFolder
            {
                Name = "classical",
                DisplayName = "Classical",
                Description = "Classical and orchestral compositions",
                Moods = new List<string> { "contemplative", "peaceful", "sophisticated", "emotional" },
                Vibes = new List<string> { "elegant", "timeless", "refined", "majestic" }
            },
            new MusicFolder
            {
                Name = "blues",
                DisplayName = "Blues",
                Description = "Traditional and modern blues",
                Moods = new List<string> { "melancholic", "soulful", "emotional", "reflective" },
                Vibes = new List<string> { "raw", "authentic", "heartfelt", "gritty" }
            },
            new MusicFolder
            {
                Name = "latin",
                DisplayName = "Latin",
                Description = "Latin, salsa, reggaeton, and Spanish music",
                Moods = new List<string> { "passionate", "energetic", "romantic", "festive" },
                Vibes = new List<string> { "spicy", "rhythmic", "cultural", "dancing" }
            },
            new MusicFolder
            {
                Name = "soul",
                DisplayName = "Soul",
                Description = "Deep soul and Motown classics",
                Moods = new List<string> { "emotional", "passionate", "heartfelt", "nostalgic" },
                Vibes = new List<string> { "deep", "authentic", "groovy", "timeless" }
            },
            new MusicFolder
            {
                Name = "funk",
                DisplayName = "Funk",
                Description = "Funky grooves and dance music",
                Moods = new List<string> { "energetic", "fun", "groovy", "upbeat" },
                Vibes = new List<string> { "funky", "rhythmic", "danceable", "groovy" }
            },
            new MusicFolder
            {
                Name = "indie",
                DisplayName = "Indie",
                Description = "Independent and alternative music",
                Moods = new List<string> { "creative", "introspective", "unique", "artistic" },
                Vibes = new List<string> { "alternative", "experimental", "authentic", "hipster" }
            },
            new MusicFolder
            {
                Name = "folk",
                DisplayName = "Folk",
                Description = "Traditional and contemporary folk",
                Moods = new List<string> { "peaceful", "storytelling", "nostalgic", "warm" },
                Vibes = new List<string> { "acoustic", "authentic", "earthy", "traditional" }
            },
            new MusicFolder
            {
                Name = "house",
                DisplayName = "House",
                Description = "House, deep house, and dance music",
                Moods = new List<string> { "energetic", "euphoric", "dancing", "uplifting" },
                Vibes = new List<string> { "clubby", "groovy", "electronic", "pulsing" }
            },
            new MusicFolder
            {
                Name = "ambient",
                DisplayName = "Ambient",
                Description = "Ambient, atmospheric, and chill music",
                Moods = new List<string> { "relaxed", "meditative", "peaceful", "contemplative" },
                Vibes = new List<string> { "atmospheric", "spacey", "ethereal", "calm" }
            },
            new MusicFolder
            {
                Name = "lo-fi",
                DisplayName = "Lo-Fi",
                Description = "Lo-fi beats, study music, and chill hop",
                Moods = new List<string> { "relaxed", "focused", "nostalgic", "cozy" },
                Vibes = new List<string> { "chill", "mellow", "nostalgic", "study" }
            }
        };

        /// <summary>
        /// Get folder by name (case-insensitive)
        /// </summary>
        public static MusicFolder? GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return All.FirstOrDefault(f => f.Name.Equals(name.Trim(), System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validate if a folder name exists
        /// </summary>
        public static bool IsValidFolder(string name)
        {
            return GetByName(name) != null;
        }

        /// <summary>
        /// Get folders matching specific moods
        /// </summary>
        public static List<MusicFolder> GetByMood(string mood)
        {
            if (string.IsNullOrWhiteSpace(mood)) return new List<MusicFolder>();
            return All.Where(f => f.Moods.Any(m => m.Contains(mood.Trim(), System.StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>
        /// Get folders matching specific vibes
        /// </summary>
        public static List<MusicFolder> GetByVibe(string vibe)
        {
            if (string.IsNullOrWhiteSpace(vibe)) return new List<MusicFolder>();
            return All.Where(f => f.Vibes.Any(v => v.Contains(vibe.Trim(), System.StringComparison.OrdinalIgnoreCase))).ToList();
        }
    }
}
