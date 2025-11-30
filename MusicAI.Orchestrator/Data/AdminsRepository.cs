using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace MusicAI.Orchestrator.Data
{
    public class AdminRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? PasswordHash { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool Approved { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class AdminsRepository : IDisposable
    {
        private readonly string _connStr;
        private readonly bool _useFallback;
        // expose fallback mode for callers that need to behave differently in dev
        public bool IsFallback => _useFallback;
        private readonly string _fallbackPath = string.Empty;
        private readonly Dictionary<string, AdminRecord> _fallbackStore = new();

        public AdminsRepository(string connectionString)
        {
            _connStr = connectionString;
            try
            {
                EnsureTable();
                _useFallback = false;
            }
            catch (Exception ex)
            {
                // DB unavailable — fall back to file-backed store for local/dev
                Console.WriteLine($"Warning: AdminsRepository could not connect to Postgres (falling back to local file store): {ex.Message}");
                _useFallback = true;
                // DB unavailable — fall back to file-backed store for local/dev
                var baseDirCandidates = new List<string>
                {
                    Path.Combine(AppContext.BaseDirectory, "data"),
                    Path.Combine(Directory.GetCurrentDirectory(), "data"),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "data")
                };
                string? existing = null;
                foreach (var cand in baseDirCandidates)
                {
                    try
                    {
                        Directory.CreateDirectory(cand);
                        var p = Path.Combine(cand, "admins.json");
                        if (File.Exists(p)) { existing = p; break; }
                    }
                    catch { }
                }

                if (existing != null)
                {
                    _fallbackPath = existing;
                }
                else
                {
                    var primary = Path.Combine(baseDirCandidates[0], "admins.json");
                    _fallbackPath = primary;
                }

                // load existing fallback store if present
                if (File.Exists(_fallbackPath))
                {
                    try
                    {
                        var txt = File.ReadAllText(_fallbackPath);
                        var items = JsonSerializer.Deserialize<List<AdminRecord>>(txt);
                        if (items != null)
                        {
                            foreach (var it in items)
                            {
                                if (it.PasswordHash == null) it.PasswordHash = string.Empty;
                                _fallbackStore[it.Id] = it;
                            }
                        }
                    }
                    catch { }
                }
                Console.WriteLine($"AdminsRepository using fallback file: {_fallbackPath}");
            }
        }

        private IDbConnection Connection() => new NpgsqlConnection(_connStr);

        private void EnsureTable()
        {
            using var db = Connection();
            db.Open();
            db.Execute(@"
                CREATE TABLE IF NOT EXISTS admins (
                    id TEXT PRIMARY KEY,
                    email TEXT UNIQUE NOT NULL,
                    password_hash TEXT NOT NULL,
                    is_superadmin BOOLEAN NOT NULL DEFAULT false,
                    approved BOOLEAN NOT NULL DEFAULT false,
                    created_at TIMESTAMP NOT NULL
                );
            ");
        }

        public void Create(AdminRecord r)
        {
            if (_useFallback)
            {
                r.PasswordHash = r.PasswordHash ?? string.Empty;
                _fallbackStore[r.Id] = r;
                PersistFallback();
                return;
            }
            using var db = Connection();
            db.Open();
            db.Execute(@"INSERT INTO admins(id,email,password_hash,is_superadmin,approved,created_at)
                        VALUES(@Id,@Email,@PasswordHash,@IsSuperAdmin,@Approved,@CreatedAt)", r);
        }

        public AdminRecord? GetByEmail(string email)
        {
            if (_useFallback)
            {
                return _fallbackStore.Values.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
            }
            using var db = Connection();
            db.Open();
            // Explicit column mapping to handle PostgreSQL snake_case
            var rec = db.QuerySingleOrDefault<AdminRecord>(
                @"SELECT id as Id, email as Email, password_hash as PasswordHash, 
                  is_superadmin as IsSuperAdmin, approved as Approved, created_at as CreatedAt 
                  FROM admins WHERE LOWER(email) = LOWER(@Email)", 
                new { Email = email });
            if (rec != null && rec.PasswordHash == null) rec.PasswordHash = string.Empty;
            return rec;
        }

        public AdminRecord? GetById(string id)
        {
            if (_useFallback)
            {
                return _fallbackStore.TryGetValue(id, out var r) ? r : null;
            }
            using var db = Connection();
            db.Open();
            // Explicit column mapping to handle PostgreSQL snake_case
            var rec = db.QuerySingleOrDefault<AdminRecord>(
                @"SELECT id as Id, email as Email, password_hash as PasswordHash, 
                  is_superadmin as IsSuperAdmin, approved as Approved, created_at as CreatedAt 
                  FROM admins WHERE id = @Id", 
                new { Id = id });
            if (rec != null && rec.PasswordHash == null) rec.PasswordHash = string.Empty;
            return rec;
        }

        public void Approve(string id)
        {
            if (_useFallback)
            {
                if (_fallbackStore.TryGetValue(id, out var r))
                {
                    r.Approved = true; PersistFallback();
                }
                return;
            }
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE admins SET approved = TRUE WHERE id = @Id", new { Id = id });
        }

        public void UpgradeToSuperAdmin(string id)
        {
            if (_useFallback)
            {
                if (_fallbackStore.TryGetValue(id, out var r))
                {
                    r.IsSuperAdmin = true;
                    r.Approved = true;
                    PersistFallback();
                }
                return;
            }
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE admins SET is_superadmin = TRUE, approved = TRUE WHERE id = @Id", new { Id = id });
        }

        public void UpdatePassword(string id, string passwordHash)
        {
            if (_useFallback)
            {
                if (_fallbackStore.TryGetValue(id, out var r))
                {
                    r.PasswordHash = passwordHash;
                    PersistFallback();
                }
                return;
            }
            using var db = Connection();
            db.Open();
            db.Execute("UPDATE admins SET password_hash = @PasswordHash WHERE id = @Id", new { PasswordHash = passwordHash, Id = id });
        }

        public void Delete(string id)
        {
            if (_useFallback)
            {
                if (_fallbackStore.Remove(id)) PersistFallback();
                return;
            }
            using var db = Connection();
            db.Open();
            db.Execute("DELETE FROM admins WHERE id = @Id", new { Id = id });
        }

        public IEnumerable<AdminRecord> GetPending()
        {
            if (_useFallback)
            {
                return _fallbackStore.Values.Where(a => !a.Approved).ToList();
            }
            using var db = Connection();
            db.Open();
            return db.Query<AdminRecord>(
                @"SELECT id as Id, email as Email, password_hash as PasswordHash, 
                  is_superadmin as IsSuperAdmin, approved as Approved, created_at as CreatedAt 
                  FROM admins WHERE approved = FALSE ORDER BY created_at DESC").ToList();
        }

        public IEnumerable<AdminRecord> GetAll()
        {
            if (_useFallback)
            {
                return _fallbackStore.Values.ToList();
            }
            using var db = Connection();
            db.Open();
            var records = db.Query<AdminRecord>(
                @"SELECT id as Id, email as Email, password_hash as PasswordHash, 
                  is_superadmin as IsSuperAdmin, approved as Approved, created_at as CreatedAt 
                  FROM admins ORDER BY created_at DESC").ToList();
            foreach (var rec in records)
            {
                if (rec.PasswordHash == null) rec.PasswordHash = string.Empty;
            }
            return records;
        }

        private void PersistFallback()
        {
            try
            {
                var list = _fallbackStore.Values.ToList();
                var txt = JsonSerializer.Serialize(list);
                File.WriteAllText(_fallbackPath, txt);
            }
            catch { }
        }

        public void Dispose() { }
    }
}
