using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class MusicTrack
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string? Album { get; set; }
        public string Genre { get; set; } = string.Empty;
        public string? Mood { get; set; }
        public string S3Key { get; set; } = string.Empty;
        public string S3Bucket { get; set; } = string.Empty;
        public int? DurationSeconds { get; set; }
        public long? FileSizeBytes { get; set; }
        public string ContentType { get; set; } = "audio/mpeg";
        public string? UploadedBy { get; set; }
        public DateTime UploadedAt { get; set; }
        public int PlayCount { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class MusicRepository : IDisposable
    {
        private readonly string _connStr;

        public MusicRepository(string connectionString)
        {
            _connStr = connectionString;
            try { EnsureTable(); } catch { /* ignore */ }
        }

        private IDbConnection Connection() => new NpgsqlConnection(_connStr);

        private void EnsureTable()
        {
            using var db = Connection();
            db.Open();
            db.Execute(@"
                CREATE TABLE IF NOT EXISTS music_tracks (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    artist TEXT NOT NULL,
                    album TEXT,
                    genre TEXT NOT NULL,
                    mood TEXT,
                    s3_key TEXT NOT NULL UNIQUE,
                    s3_bucket TEXT NOT NULL,
                    duration_seconds INT,
                    file_size_bytes BIGINT,
                    content_type TEXT DEFAULT 'audio/mpeg',
                    uploaded_by TEXT,
                    uploaded_at TIMESTAMP DEFAULT NOW(),
                    play_count INT DEFAULT 0,
                    is_active BOOLEAN DEFAULT true
                );
                
                CREATE INDEX IF NOT EXISTS idx_music_genre ON music_tracks(genre);
                CREATE INDEX IF NOT EXISTS idx_music_mood ON music_tracks(mood);
                CREATE INDEX IF NOT EXISTS idx_music_active ON music_tracks(is_active);
            ");
        }

        public async Task<string> CreateAsync(MusicTrack track)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync(@"
                INSERT INTO music_tracks 
                (id, title, artist, album, genre, mood, s3_key, s3_bucket, duration_seconds, 
                 file_size_bytes, content_type, uploaded_by, uploaded_at, play_count, is_active)
                VALUES 
                (@Id, @Title, @Artist, @Album, @Genre, @Mood, @S3Key, @S3Bucket, @DurationSeconds,
                 @FileSizeBytes, @ContentType, @UploadedBy, @UploadedAt, @PlayCount, @IsActive)",
                track);
            return track.Id;
        }

        public async Task<MusicTrack?> GetByIdAsync(string id)
        {
            using var db = Connection();
            db.Open();
            return await db.QuerySingleOrDefaultAsync<MusicTrack>(
                "SELECT * FROM music_tracks WHERE id = @Id AND is_active = true",
                new { Id = id });
        }

        public async Task<List<MusicTrack>> GetByGenreAsync(string genre, int limit = 50)
        {
            using var db = Connection();
            db.Open();
            var results = await db.QueryAsync<MusicTrack>(@"
                SELECT * FROM music_tracks 
                WHERE genre = @Genre AND is_active = true 
                ORDER BY uploaded_at DESC 
                LIMIT @Limit",
                new { Genre = genre, Limit = limit });
            return results.ToList();
        }

        public async Task<List<MusicTrack>> SearchAsync(string? mood = null, string? genre = null, string? artist = null, int limit = 20)
        {
            using var db = Connection();
            db.Open();
            
            var conditions = new List<string> { "is_active = true" };
            var parameters = new DynamicParameters();
            
            if (!string.IsNullOrWhiteSpace(mood))
            {
                conditions.Add("mood = @Mood");
                parameters.Add("Mood", mood);
            }
            
            if (!string.IsNullOrWhiteSpace(genre))
            {
                conditions.Add("genre = @Genre");
                parameters.Add("Genre", genre);
            }
            
            if (!string.IsNullOrWhiteSpace(artist))
            {
                conditions.Add("artist ILIKE @Artist");
                parameters.Add("Artist", $"%{artist}%");
            }
            
            parameters.Add("Limit", limit);
            
            var query = $@"
                SELECT * FROM music_tracks 
                WHERE {string.Join(" AND ", conditions)}
                ORDER BY play_count DESC, uploaded_at DESC
                LIMIT @Limit";
            
            var results = await db.QueryAsync<MusicTrack>(query, parameters);
            return results.ToList();
        }

        public async Task<List<MusicTrack>> GetAllAsync(int offset = 0, int limit = 50)
        {
            using var db = Connection();
            db.Open();
            var results = await db.QueryAsync<MusicTrack>(@"
                SELECT * FROM music_tracks 
                WHERE is_active = true 
                ORDER BY uploaded_at DESC 
                OFFSET @Offset LIMIT @Limit",
                new { Offset = offset, Limit = limit });
            return results.ToList();
        }

        public async Task UpdateMetadataAsync(string id, string? title = null, string? artist = null, 
            string? album = null, string? genre = null, string? mood = null)
        {
            using var db = Connection();
            db.Open();
            
            var updates = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("Id", id);
            
            if (title != null) { updates.Add("title = @Title"); parameters.Add("Title", title); }
            if (artist != null) { updates.Add("artist = @Artist"); parameters.Add("Artist", artist); }
            if (album != null) { updates.Add("album = @Album"); parameters.Add("Album", album); }
            if (genre != null) { updates.Add("genre = @Genre"); parameters.Add("Genre", genre); }
            if (mood != null) { updates.Add("mood = @Mood"); parameters.Add("Mood", mood); }
            
            if (updates.Count == 0) return;
            
            var query = $"UPDATE music_tracks SET {string.Join(", ", updates)} WHERE id = @Id";
            await db.ExecuteAsync(query, parameters);
        }

        public async Task DeleteAsync(string id)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync("UPDATE music_tracks SET is_active = false WHERE id = @Id", new { Id = id });
        }

        public async Task IncrementPlayCountAsync(string id)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync("UPDATE music_tracks SET play_count = play_count + 1 WHERE id = @Id", new { Id = id });
        }

        public void Dispose() { }
    }
}
