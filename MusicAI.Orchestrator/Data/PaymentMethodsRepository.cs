using System;
using System.Data;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class PaymentMethod
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty; // tokenized payment method (no PAN)
        public string? Last4 { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }

    public class PaymentMethodsRepository : IDisposable
    {
        private readonly string _connStr;

        public PaymentMethodsRepository(string connectionString)
        {
            _connStr = connectionString;
            try { EnsureTable(); } catch { }
        }

        private IDbConnection Connection() => new NpgsqlConnection(_connStr);

        private void EnsureTable()
        {
            using var db = Connection();
            db.Open();
            db.Execute(@"
                CREATE TABLE IF NOT EXISTS payment_methods (
                    id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    token TEXT NOT NULL,
                    last4 TEXT,
                    created_at TIMESTAMP NOT NULL,
                    updated_at TIMESTAMP
                );
            ");
        }

        public void Insert(PaymentMethod m)
        {
            using var db = Connection();
            db.Open();
            db.Execute(@"INSERT INTO payment_methods(id,user_id,provider,token,last4,created_at,updated_at)
                         VALUES(@Id,@UserId,@Provider,@Token,@Last4,@CreatedAt,@UpdatedAt)", m);
        }

        public PaymentMethod? GetByUserId(string userId)
        {
            using var db = Connection();
            db.Open();
            return db.QuerySingleOrDefault<PaymentMethod>("SELECT * FROM payment_methods WHERE user_id = @U LIMIT 1", new { U = userId });
        }

        public System.Collections.Generic.IEnumerable<PaymentMethod> GetAllMethods()
        {
            using var db = Connection();
            db.Open();
            return db.Query<PaymentMethod>("SELECT * FROM payment_methods");
        }

        public void DeleteByUserId(string userId)
        {
            using var db = Connection();
            db.Open();
            db.Execute("DELETE FROM payment_methods WHERE user_id = @U", new { U = userId });
        }

        public void Dispose() { }
    }
}
