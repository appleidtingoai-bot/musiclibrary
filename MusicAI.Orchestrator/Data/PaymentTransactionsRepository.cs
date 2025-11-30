using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class PaymentTransaction
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string? PackageId { get; set; }
        public decimal AmountNgn { get; set; }
        public string? ZenithReference { get; set; }
        public string? ZenithStatus { get; set; }
        public string? PaymentMethod { get; set; } // card, ussd, bank_transfer
        public DateTime InitiatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string Status { get; set; } = "pending"; // pending, completed, failed, cancelled
        public string? Metadata { get; set; } // JSON
    }

    public class PaymentTransactionsRepository : IDisposable
    {
        private readonly string _connStr;

        public PaymentTransactionsRepository(string connectionString)
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
                CREATE TABLE IF NOT EXISTS payment_transactions (
                    id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    package_id TEXT,
                    amount_ngn DECIMAL(10,2) NOT NULL,
                    zenith_reference TEXT UNIQUE,
                    zenith_status TEXT,
                    payment_method TEXT,
                    initiated_at TIMESTAMP DEFAULT NOW(),
                    completed_at TIMESTAMP,
                    status TEXT DEFAULT 'pending',
                    metadata TEXT
                );
                
                CREATE INDEX IF NOT EXISTS idx_payment_user ON payment_transactions(user_id);
                CREATE INDEX IF NOT EXISTS idx_payment_reference ON payment_transactions(zenith_reference);
                CREATE INDEX IF NOT EXISTS idx_payment_status ON payment_transactions(status);
            ");
        }

        public async Task<string> CreateAsync(PaymentTransaction transaction)
        {
            using var db = Connection();
            db.Open();
            await db.ExecuteAsync(@"
                INSERT INTO payment_transactions 
                (id, user_id, package_id, amount_ngn, zenith_reference, zenith_status, payment_method, 
                 initiated_at, completed_at, status, metadata)
                VALUES 
                (@Id, @UserId, @PackageId, @AmountNgn, @ZenithReference, @ZenithStatus, @PaymentMethod,
                 @InitiatedAt, @CompletedAt, @Status, @Metadata)",
                transaction);
            return transaction.Id;
        }

        public async Task<PaymentTransaction?> GetByIdAsync(string id)
        {
            using var db = Connection();
            db.Open();
            return await db.QuerySingleOrDefaultAsync<PaymentTransaction>(
                "SELECT * FROM payment_transactions WHERE id = @Id",
                new { Id = id });
        }

        public async Task<PaymentTransaction?> GetByReferenceAsync(string reference)
        {
            using var db = Connection();
            db.Open();
            return await db.QuerySingleOrDefaultAsync<PaymentTransaction>(
                "SELECT * FROM payment_transactions WHERE zenith_reference = @Reference",
                new { Reference = reference });
        }

        public async Task<List<PaymentTransaction>> GetByUserAsync(string userId)
        {
            using var db = Connection();
            db.Open();
            var results = await db.QueryAsync<PaymentTransaction>(@"
                SELECT * FROM payment_transactions 
                WHERE user_id = @UserId 
                ORDER BY initiated_at DESC",
                new { UserId = userId });
            return results.ToList();
        }

        public async Task UpdateStatusAsync(string id, string status, string? zenithStatus = null, DateTime? completedAt = null)
        {
            using var db = Connection();
            db.Open();
            
            var updates = new List<string> { "status = @Status" };
            var parameters = new DynamicParameters();
            parameters.Add("Id", id);
            parameters.Add("Status", status);
            
            if (zenithStatus != null)
            {
                updates.Add("zenith_status = @ZenithStatus");
                parameters.Add("ZenithStatus", zenithStatus);
            }
            
            if (completedAt.HasValue)
            {
                updates.Add("completed_at = @CompletedAt");
                parameters.Add("CompletedAt", completedAt.Value);
            }
            
            var query = $"UPDATE payment_transactions SET {string.Join(", ", updates)} WHERE id = @Id";
            await db.ExecuteAsync(query, parameters);
        }

        public void Dispose() { }
    }
}
