using System;
using System.Data;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class UserRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public bool IsSubscribed { get; set; }
        public double Credits { get; set; }
        public string Country { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public DateTime TrialExpires { get; set; }
    }

    public class UsersRepository : IDisposable
    {
        private readonly string _connStr;

        public UsersRepository(string connectionString)
        {
            _connStr = connectionString;
            try { EnsureTable(); } catch { /* ignore for now to allow startup */ }
        }

        private IDbConnection Connection() => new NpgsqlConnection(_connStr);

        private void EnsureTable()
        {
            using var db = Connection();
            db.Open();
            db.Execute(@"
                CREATE TABLE IF NOT EXISTS users (
                    id TEXT PRIMARY KEY,
                    email TEXT UNIQUE NOT NULL,
                    password_hash TEXT NOT NULL,
                    is_subscribed BOOLEAN NOT NULL DEFAULT false,
                    credits DOUBLE PRECISION NOT NULL DEFAULT 0,
                    country TEXT,
                    ip_address TEXT,
                    trial_expires TIMESTAMP
                );
            ");
        }

        public void Create(UserRecord r)
        {
            using var db = Connection();
            db.Open();
            db.Execute(@"INSERT INTO users(id,email,password_hash,is_subscribed,credits,country,ip_address,trial_expires)
                        VALUES(@Id,@Email,@PasswordHash,@IsSubscribed,@Credits,@Country,@IpAddress,@TrialExpires)", r);
        }

        public UserRecord? GetByEmail(string email)
        {
            using var db = Connection();
            db.Open();
            // Explicit column mapping for PostgreSQL snake_case to C# PascalCase
            return db.QuerySingleOrDefault<UserRecord>(@"
                SELECT 
                    id as Id,
                    email as Email,
                    password_hash as PasswordHash,
                    is_subscribed as IsSubscribed,
                    credits as Credits,
                    country as Country,
                    ip_address as IpAddress,
                    trial_expires as TrialExpires
                FROM users 
                WHERE LOWER(email) = LOWER(@Email)", new { Email = email });
        }

        public UserRecord? GetById(string id)
        {
            using var db = Connection();
            db.Open();
            // Explicit column mapping for PostgreSQL snake_case to C# PascalCase
            return db.QuerySingleOrDefault<UserRecord>(@"
                SELECT 
                    id as Id,
                    email as Email,
                    password_hash as PasswordHash,
                    is_subscribed as IsSubscribed,
                    credits as Credits,
                    country as Country,
                    ip_address as IpAddress,
                    trial_expires as TrialExpires
                FROM users 
                WHERE id = @Id", new { Id = id });
        }

        public void UpdateSubscription(string id, bool subscribed)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE users SET is_subscribed = @Subscribed WHERE id = @Id", new { Subscribed = subscribed, Id = id });
        }

        public void UpdateCountry(string id, string country)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE users SET country = @Country WHERE id = @Id", new { Country = country, Id = id });
        }

        public void UpdatePasswordHash(string id, string passwordHash)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE users SET password_hash = @PasswordHash WHERE id = @Id", new { PasswordHash = passwordHash, Id = id });
        }

        public void UpdatePasswordHashByEmail(string email, string passwordHash)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE users SET password_hash = @PasswordHash WHERE LOWER(email) = LOWER(@Email)", new { PasswordHash = passwordHash, Email = email });
        }

        public void Dispose() { }
    }
}
