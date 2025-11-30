using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class CreditPackage
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal PriceNgn { get; set; }
        public int Credits { get; set; }
        public int DurationDays { get; set; } = 30;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
    }

    public class CreditPackagesRepository : IDisposable
    {
        private readonly string _connStr;

        public CreditPackagesRepository(string connectionString)
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
                CREATE TABLE IF NOT EXISTS credit_packages (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    price_ngn DECIMAL(10,2) NOT NULL,
                    credits INT NOT NULL,
                    duration_days INT DEFAULT 30,
                    is_active BOOLEAN DEFAULT true,
                    created_at TIMESTAMP DEFAULT NOW()
                );
            ");
            
            // Seed default packages if table is empty
            var count = db.ExecuteScalar<int>("SELECT COUNT(*) FROM credit_packages");
            if (count == 0)
            {
                db.Execute(@"
                    INSERT INTO credit_packages (id, name, price_ngn, credits, duration_days, is_active, created_at) VALUES
                    ('pkg_basic', 'Basic', 5.00, 350, 30, true, NOW()),
                    ('pkg_standard', 'Standard', 15.00, 550, 30, true, NOW()),
                    ('pkg_premium', 'Premium', 30.00, 1000, 30, true, NOW());
                ");
            }
        }

        public async Task<List<CreditPackage>> GetActivePackagesAsync()
        {
            using var db = Connection();
            db.Open();
            var results = await db.QueryAsync<CreditPackage>(
                "SELECT * FROM credit_packages WHERE is_active = true ORDER BY price_ngn ASC");
            return results.ToList();
        }

        public async Task<CreditPackage?> GetByIdAsync(string id)
        {
            using var db = Connection();
            db.Open();
            return await db.QuerySingleOrDefaultAsync<CreditPackage>(
                "SELECT * FROM credit_packages WHERE id = @Id",
                new { Id = id });
        }

        public void Dispose() { }
    }
}
