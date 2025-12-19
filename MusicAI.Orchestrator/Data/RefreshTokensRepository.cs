using System;
using System.Data;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class RefreshTokenRecord
    {
        public string Token { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool Revoked { get; set; }
    }

    public class RefreshTokensRepository : IDisposable
    {
        private readonly string _connStr;
        public RefreshTokensRepository(string connectionString)
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
                CREATE TABLE IF NOT EXISTS refresh_tokens (
                    token TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    expires_at TIMESTAMP NOT NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT now(),
                    revoked BOOLEAN NOT NULL DEFAULT false
                );
            ");
        }

        public void Create(string token, string userId, DateTime expiresAt)
        {
            using var db = Connection();
            db.Open();
            db.Execute("INSERT INTO refresh_tokens(token,user_id,expires_at,created_at,revoked) VALUES(@Token,@UserId,@ExpiresAt,@CreatedAt,false)", new { Token = token, UserId = userId, ExpiresAt = expiresAt, CreatedAt = DateTime.UtcNow });
        }

        public RefreshTokenRecord? GetByToken(string token)
        {
            try
            {
                using var db = Connection();
                db.Open();
                return db.QuerySingleOrDefault<RefreshTokenRecord>("SELECT token as Token, user_id as UserId, expires_at as ExpiresAt, created_at as CreatedAt, revoked as Revoked FROM refresh_tokens WHERE token = @Token", new { Token = token });
            }
            catch
            {
                return null;
            }
        }

        public void Revoke(string token)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE refresh_tokens SET revoked = true WHERE token = @Token", new { Token = token });
        }

        public void RevokeByUser(string userId)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE refresh_tokens SET revoked = true WHERE user_id = @UserId", new { UserId = userId });
        }

        public void DeleteExpired()
        {
            using var db = Connection();
            db.Open();
            db.Execute("DELETE FROM refresh_tokens WHERE expires_at < now() OR revoked = true");
        }

        public void Dispose() { }
    }
}
