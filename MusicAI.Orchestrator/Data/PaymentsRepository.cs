using System;
using System.Data;
using Dapper;
using Npgsql;
using System.Collections.Generic;

namespace MusicAI.Orchestrator.Data
{
    public class PaymentRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; } = string.Empty;
        public string MerchantReference { get; set; } = string.Empty;
        public string GatewayReference { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string Status { get; set; } = "pending"; // pending, success, failed
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public string? CheckoutUrl { get; set; }
        public string? AccessCode { get; set; }
        public bool SaveTokenRequested { get; set; } = false;
    }

    public class PaymentsRepository : IDisposable
    {
        private readonly string _connStr;

        public PaymentsRepository(string connectionString)
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
                CREATE TABLE IF NOT EXISTS payments (
                    id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    merchant_reference TEXT,
                    gateway_reference TEXT,
                    amount NUMERIC NOT NULL,
                    currency TEXT NOT NULL,
                    status TEXT NOT NULL,
                    checkout_url TEXT,
                    access_code TEXT,
                    save_token_requested BOOLEAN DEFAULT false,
                    created_at TIMESTAMP NOT NULL,
                    updated_at TIMESTAMP
                );
            ");
        }

        public void Insert(PaymentRecord r)
        {
            using var db = Connection();
            db.Open();
            db.Execute(@"INSERT INTO payments(id,user_id,merchant_reference,gateway_reference,amount,currency,status,checkout_url,access_code,save_token_requested,created_at,updated_at)
                         VALUES(@Id,@UserId,@MerchantReference,@GatewayReference,@Amount,@Currency,@Status,@CheckoutUrl,@AccessCode,@SaveTokenRequested,@CreatedAt,@UpdatedAt)", r);
        }

        public void UpdateStatus(string id, string status, string? gatewayRef = null)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE payments SET status=@Status, gateway_reference = COALESCE(@Gw, gateway_reference), updated_at = NOW() WHERE id = @Id", new { Status = status, Gw = gatewayRef, Id = id });
        }

        public PaymentRecord? GetByMerchantRef(string merchantRef)
        {
            using var db = Connection();
            db.Open();
            return db.QuerySingleOrDefault<PaymentRecord>("SELECT * FROM payments WHERE merchant_reference = @M", new { M = merchantRef });
        }

        public PaymentRecord? GetByGatewayRef(string gatewayRef)
        {
            using var db = Connection();
            db.Open();
            return db.QuerySingleOrDefault<PaymentRecord>("SELECT * FROM payments WHERE gateway_reference = @G", new { G = gatewayRef });
        }

        public IEnumerable<PaymentRecord> GetPendingOlderThan(TimeSpan olderThan)
        {
            using var db = Connection();
            db.Open();
            return db.Query<PaymentRecord>("SELECT * FROM payments WHERE status = 'pending' AND created_at <= (NOW() - (@s || ' seconds')::interval)", new { s = (int)olderThan.TotalSeconds });
        }

        public void Dispose() { }
    }
}
