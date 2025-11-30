using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class OapAgent
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Personality { get; set; } // friendly, energetic, calm
        public string Genre { get; set; } = string.Empty;
        public string Host { get; set; } = "localhost";
        public int Port { get; set; }
        public TimeSpan StartTimeUtc { get; set; }
        public TimeSpan EndTimeUtc { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
    }

    public class OapAgentsRepository : IDisposable
    {
        private readonly string _connStr;

        public OapAgentsRepository(string connectionString)
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
                CREATE TABLE IF NOT EXISTS oap_agents (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    display_name TEXT NOT NULL,
                    personality TEXT,
                    genre TEXT NOT NULL,
                    host TEXT NOT NULL,
                    port INT NOT NULL,
                    start_time_utc TIME NOT NULL,
                    end_time_utc TIME NOT NULL,
                    is_active BOOLEAN DEFAULT true,
                    created_at TIMESTAMP DEFAULT NOW()
                );
            ");
            
            // Seed all OAP agents with correct schedules
            var count = db.ExecuteScalar<int>("SELECT COUNT(*) FROM oap_agents");
            if (count == 0)
            {
                db.Execute(@"
                    INSERT INTO oap_agents 
                    (id, name, display_name, personality, genre, host, port, start_time_utc, end_time_utc, is_active, created_at)
                    VALUES 
                    -- Tosin: News & Information (Every Hour)
                    ('tosin', 'tosin', 'Tosin', 'professional', 'news', 'localhost', 8001, '00:00:00', '23:59:59', true, NOW()),
                    
                    -- Dozy: Business & Tech (6AM - 10AM)
                    ('dozy', 'dozy', 'Dozy', 'professional', 'business', 'localhost', 8007, '06:00:00', '10:00:00', true, NOW()),
                    
                    -- Rotimi: Street Vibes (12PM - 4PM)
                    ('rotimi', 'rotimi', 'Rotimi', 'energetic', 'afrobeats', 'localhost', 8004, '12:00:00', '16:00:00', true, NOW()),
                    
                    -- Mimi: Youth & Pop Culture (4PM - 8PM)
                    ('mimi', 'mimi', 'Mimi', 'trendy', 'pop', 'localhost', 8002, '16:00:00', '20:00:00', true, NOW()),
                    
                    -- Maka: Real Talk (8PM - 10PM)
                    ('maka', 'maka', 'Maka',  'bold', 'talk', 'localhost', 8005, '20:00:00', '22:00:00', true, NOW()),
                    
                    -- Roman: World Music (6PM - 10PM)
                    ('roman', 'roman', 'Roman', 'cultured', 'worldmusic', 'localhost', 8006, '18:00:00', '22:00:00', true, NOW()),
                    
                    -- Ife Mi: Love & Relationships (10PM - 2AM)
                    ('ifemi', 'ifemi', 'Ife Mi', 'soulful', 'rnb', 'localhost', 8003, '22:00:00', '02:00:00', true, NOW());
                ");
            }
        }

        public async Task<string> CreateAsync(OapAgent agent)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync(@"
                INSERT INTO oap_agents 
                (id, name, display_name, personality, genre, host, port, start_time_utc, end_time_utc, is_active, created_at)
                VALUES 
                (@Id, @Name, @DisplayName, @Personality, @Genre, @Host, @Port, @StartTimeUtc, @EndTimeUtc, @IsActive, @CreatedAt)",
                agent);
            return agent.Id;
        }

        public async Task<OapAgent?> GetByIdAsync(string id)
        {
            using var db = Connection();
            db.Open();
            return await db.QuerySingleOrDefaultAsync<OapAgent>(
                "SELECT * FROM oap_agents WHERE id = @Id",
                new { Id = id });
        }

        public async Task<List<OapAgent>> GetAllActiveAsync()
        {
            using var db = Connection();
            db.Open();
            var results = await db.QueryAsync<OapAgent>(
                "SELECT * FROM oap_agents WHERE is_active = true ORDER BY name ASC");
            return results.ToList();
        }

        public async Task<OapAgent?> GetByTimeSlotAsync(TimeSpan currentTime)
        {
            using var db = Connection();
            db.Open();
            
            // Handle wrap-around schedules (e.g., 22:00-02:00)
            var results = await db.QueryAsync<OapAgent>(@"
                SELECT * FROM oap_agents 
                WHERE is_active = true 
                  AND (
                    (start_time_utc <= end_time_utc AND @CurrentTime >= start_time_utc AND @CurrentTime <= end_time_utc)
                    OR
                    (start_time_utc > end_time_utc AND (@CurrentTime >= start_time_utc OR @CurrentTime <= end_time_utc))
                  )
                ORDER BY name ASC
                LIMIT 1",
                new { CurrentTime = currentTime });
            
            return results.FirstOrDefault();
        }

        public async Task UpdateAsync(OapAgent agent)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync(@"
                UPDATE oap_agents 
                SET display_name = @DisplayName, 
                    personality = @Personality,
                    genre = @Genre,
                    host = @Host,
                    port = @Port,
                    start_time_utc = @StartTimeUtc,
                    end_time_utc = @EndTimeUtc,
                    is_active = @IsActive
                WHERE id = @Id",
                agent);
        }

        public async Task DeleteAsync(string id)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync("UPDATE oap_agents SET is_active = false WHERE id = @Id", new { Id = id });
        }

        public void Dispose() { }
    }
}
