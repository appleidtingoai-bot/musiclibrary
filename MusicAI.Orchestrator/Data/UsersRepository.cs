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
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public bool IsSubscribed { get; set; }

        public double Credits { get; set; }
        public DateTime? CreditsExpiry { get; set; }
        public bool SavePaymentConsent { get; set; }
        public string Country { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string LastSeenIp { get; set; } = string.Empty;
        public string LastSeenCountry { get; set; } = string.Empty;
        public DateTime? LastIpCheckedAt { get; set; }
        public bool LastIpIsVpn { get; set; }
        public bool LastIpIsProxy { get; set; }
        
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
                    first_name TEXT,
                    last_name TEXT,
                    phone TEXT,
                    password_hash TEXT NOT NULL,
                    is_subscribed BOOLEAN NOT NULL DEFAULT false,
                    credits DOUBLE PRECISION NOT NULL DEFAULT 0,
                    credits_expiry TIMESTAMP,
                    save_payment_consent BOOLEAN NOT NULL DEFAULT false,
                    last_seen_ip TEXT,
                    last_seen_country TEXT,
                    last_ip_checked_at TIMESTAMP,
                    last_ip_is_vpn BOOLEAN DEFAULT false,
                    last_ip_is_proxy BOOLEAN DEFAULT false,
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
            db.Execute(@"INSERT INTO users(id,email,first_name,last_name,phone,password_hash,is_subscribed,credits,credits_expiry,save_payment_consent,country,ip_address,trial_expires)
                        VALUES(@Id,@Email,@FirstName,@LastName,@Phone,@PasswordHash,@IsSubscribed,@Credits,@CreditsExpiry,@SavePaymentConsent,@Country,@IpAddress,@TrialExpires)", r);
        }

        public UserRecord? GetByEmail(string email)
        {
            try
            {
                using var db = Connection();
                db.Open();
                // Explicit column mapping for PostgreSQL snake_case to C# PascalCase
                return db.QuerySingleOrDefault<UserRecord>(@"
                    SELECT 
                        id as Id,
                        first_name as FirstName,
                        last_name as LastName,
                        phone as Phone,
                        email as Email,
                        password_hash as PasswordHash,
                        is_subscribed as IsSubscribed,
                        credits as Credits,
                        credits_expiry as CreditsExpiry,
                        save_payment_consent as SavePaymentConsent,
                        country as Country,
                        ip_address as IpAddress,
                        trial_expires as TrialExpires
                    FROM users 
                    WHERE LOWER(email) = LOWER(@Email)", new { Email = email });
            }
            catch (Exception ex)
            {
                // Log and return null so callers receive Unauthorized instead of a 500
                Console.WriteLine($"⚠ UsersRepository.GetByEmail failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public UserRecord? GetById(string id)
        {
            try
            {
                using var db = Connection();
                db.Open();
                // Explicit column mapping for PostgreSQL snake_case to C# PascalCase
                return db.QuerySingleOrDefault<UserRecord>(@"
                    SELECT 
                        id as Id,
                        first_name as FirstName,
                        last_name as LastName,
                        phone as Phone,
                        email as Email,
                        password_hash as PasswordHash,
                        is_subscribed as IsSubscribed,
                        credits as Credits,
                        credits_expiry as CreditsExpiry,
                        save_payment_consent as SavePaymentConsent,
                        country as Country,
                        ip_address as IpAddress,
                        trial_expires as TrialExpires
                    FROM users 
                    WHERE id = @Id", new { Id = id });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ UsersRepository.GetById failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
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

        public void UpdateSavePaymentConsent(string id, bool consent)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE users SET save_payment_consent = @Consent WHERE id = @Id", new { Consent = consent, Id = id });
        }

        public void UpdateLastSeenIp(string id, string ip, string? country, bool isVpn, bool isProxy, DateTime checkedAt)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE users SET last_seen_ip = @Ip, last_seen_country = @Country, last_ip_is_vpn = @IsVpn, last_ip_is_proxy = @IsProxy, last_ip_checked_at = @CheckedAt WHERE id = @Id",
                new { Ip = ip, Country = country, IsVpn = isVpn, IsProxy = isProxy, CheckedAt = checkedAt, Id = id });
        }

        public System.Collections.Generic.IEnumerable<UserRecord> GetUsersWithCredits()
        {
            using var db = Connection();
            db.Open();
            return db.Query<UserRecord>("SELECT id as Id, email as Email, password_hash as PasswordHash, is_subscribed as IsSubscribed, credits as Credits, credits_expiry as CreditsExpiry, save_payment_consent as SavePaymentConsent, country as Country, ip_address as IpAddress, trial_expires as TrialExpires, last_seen_ip as LastSeenIp, last_seen_country as LastSeenCountry, last_ip_checked_at as LastIpCheckedAt, last_ip_is_vpn as LastIpIsVpn, last_ip_is_proxy as LastIpIsProxy FROM users WHERE credits > 0");
        }

        public void AddCredits(string id, double amount, DateTime expiry)
        {
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE users SET credits = credits + @Amount, credits_expiry = @Expiry WHERE id = @Id", new { Amount = amount, Expiry = expiry, Id = id });
        }

        public void Dispose() { }
    }
}
