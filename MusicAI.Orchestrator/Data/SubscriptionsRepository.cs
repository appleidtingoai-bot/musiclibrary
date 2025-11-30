using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class Subscription
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string? PackageId { get; set; }
        public int CreditsAllocated { get; set; }
        public int CreditsRemaining { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsTrial { get; set; }
        public string Status { get; set; } = "active"; // active, expired, cancelled
    }

    public class SubscriptionsRepository : IDisposable
    {
        private readonly string _connStr;

        public SubscriptionsRepository(string connectionString)
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
                CREATE TABLE IF NOT EXISTS subscriptions (
                    id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    package_id TEXT,
                    credits_allocated INT NOT NULL,
                    credits_remaining INT NOT NULL,
                    started_at TIMESTAMP NOT NULL,
                    expires_at TIMESTAMP NOT NULL,
                    is_trial BOOLEAN DEFAULT false,
                    status TEXT DEFAULT 'active'
                );
                
                CREATE INDEX IF NOT EXISTS idx_subscriptions_user ON subscriptions(user_id);
                CREATE INDEX IF NOT EXISTS idx_subscriptions_expires ON subscriptions(expires_at);
                CREATE INDEX IF NOT EXISTS idx_subscriptions_status ON subscriptions(status);
            ");
        }

        public async Task<string> CreateAsync(Subscription subscription)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync(@"
                INSERT INTO subscriptions 
                (id, user_id, package_id, credits_allocated, credits_remaining, started_at, expires_at, is_trial, status)
                VALUES 
                (@Id, @UserId, @PackageId, @CreditsAllocated, @CreditsRemaining, @StartedAt, @ExpiresAt, @IsTrial, @Status)",
                subscription);
            return subscription.Id;
        }

        public async Task<Subscription?> GetActiveSubscriptionAsync(string userId)
        {
            using var db = Connection();
            db.Open();
            return await db.QuerySingleOrDefaultAsync<Subscription>(@"
                SELECT * FROM subscriptions 
                WHERE user_id = @UserId 
                  AND status = 'active' 
                  AND expires_at > NOW()
                ORDER BY expires_at DESC 
                LIMIT 1",
                new { UserId = userId });
        }

        public async Task<List<Subscription>> GetAllByUserAsync(string userId)
        {
            using var db = Connection();
            db.Open();
            var results = await db.QueryAsync<Subscription>(@"
                SELECT * FROM subscriptions 
                WHERE user_id = @UserId 
                ORDER BY started_at DESC",
                new { UserId = userId });
            return results.ToList();
        }

        public async Task DeductCreditsAsync(string userId, int amount)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync(@"
                UPDATE subscriptions 
                SET credits_remaining = credits_remaining - @Amount 
                WHERE user_id = @UserId 
                  AND status = 'active' 
                  AND expires_at > NOW()
                  AND credits_remaining >= @Amount",
                new { UserId = userId, Amount = amount });
        }

        public async Task<int> ExpireSubscriptionsAsync()
        {
            using var db = Connection();
            db.Open();
            return await db.ExecuteAsync(@"
                UPDATE subscriptions 
                SET status = 'expired' 
                WHERE status = 'active' 
                  AND expires_at <= NOW()");
        }

        public async Task UpdateStatusAsync(string id, string status)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync(
                "UPDATE subscriptions SET status = @Status WHERE id = @Id",
                new { Id = id, Status = status });
        }

        public void Dispose() { }
    }
}
