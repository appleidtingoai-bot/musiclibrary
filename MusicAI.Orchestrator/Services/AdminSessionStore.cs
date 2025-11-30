using System;
using Microsoft.Data.Sqlite;
using System.IO;

namespace MusicAI.Orchestrator.Services
{
    public static class AdminSessionStore
    {
        private static readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        private static readonly string _dbFile = Path.Combine(_dataDir, "admin_sessions.db");
        private static readonly string _connString;

        static AdminSessionStore()
        {
            if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
            _connString = new SqliteConnectionStringBuilder { DataSource = _dbFile }.ToString();
            using var conn = new SqliteConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS sessions (
                token TEXT PRIMARY KEY,
                admin_email TEXT NOT NULL,
                expires INTEGER NOT NULL
            );";
            cmd.ExecuteNonQuery();
        }

        public static void AddSession(string token, string adminEmail, DateTime expires)
        {
            using var conn = new SqliteConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO sessions(token, admin_email, expires) VALUES($t,$e,$x);";
            cmd.Parameters.AddWithValue("$t", token);
            cmd.Parameters.AddWithValue("$e", adminEmail);
            cmd.Parameters.AddWithValue("$x", expires.ToBinary());
            cmd.ExecuteNonQuery();
        }

        public static bool ValidateSession(string token, out string? adminEmail)
        {
            adminEmail = null;
            if (string.IsNullOrEmpty(token)) return false;
            using var conn = new SqliteConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT admin_email, expires FROM sessions WHERE token = $t LIMIT 1;";
            cmd.Parameters.AddWithValue("$t", token);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) return false;
            var email = rdr.GetString(0);
            var bin = rdr.GetInt64(1);
            var expires = DateTime.FromBinary(bin);
            if (expires > DateTime.UtcNow)
            {
                adminEmail = email;
                return true;
            }
            // expired - remove
            RemoveSession(token);
            return false;
        }

        public static void RemoveSession(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            using var conn = new SqliteConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE token = $t;";
            cmd.Parameters.AddWithValue("$t", token);
            cmd.ExecuteNonQuery();
        }
    }
}
