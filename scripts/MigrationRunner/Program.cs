using System;
using System.IO;
using System.Linq;
using Npgsql;

class Program
{
    static int Main(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("POSTGRES_URL")
                   ?? "Host=localhost;Username=postgres;Password=postgres;Database=musicai";

        Console.WriteLine($"Using Postgres connection: {conn.Split(';').FirstOrDefault()}");

            // If called with 'status', print the number of refresh_tokens and exit
            if (args != null && args.Length > 0 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var connCheck = new NpgsqlConnection(conn);
                    connCheck.Open();
                    using var cmd = new NpgsqlCommand("SELECT count(*) FROM refresh_tokens", connCheck);
                    var cnt = cmd.ExecuteScalar();
                    Console.WriteLine($"refresh_tokens count: {cnt}");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Status check failed: {ex.GetType().Name}: {ex.Message}");
                    return 4;
                }
            }

        // Find migrations folder by searching current and parent directories
        string? FindMigrations()
        {
            var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (var s in starts)
            {
                try
                {
                    var dir = new DirectoryInfo(s);
                    while (dir != null)
                    {
                        var candidate = Path.Combine(dir.FullName, "scripts", "db_migrations");
                        if (Directory.Exists(candidate)) return candidate;
                        dir = dir.Parent;
                    }
                }
                catch { }
            }
            return null;
        }

        var mig = FindMigrations();
        if (string.IsNullOrEmpty(mig))
        {
            Console.Error.WriteLine("Migrations folder not found. Ensure scripts/db_migrations exists in repo.");
            return 2;
        }

        Console.WriteLine($"Found migrations folder: {mig}");
        var files = Directory.GetFiles(mig, "*.sql").OrderBy(f => f).ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine("No .sql files found in migrations folder.");
            return 0;
        }

        try
        {
            using var connPg = new NpgsqlConnection(conn);
            connPg.Open();
            foreach (var f in files)
            {
                Console.WriteLine($"Applying {Path.GetFileName(f)}...");
                var sql = File.ReadAllText(f);
                using var cmd = new NpgsqlCommand(sql, connPg);
                cmd.ExecuteNonQuery();
                Console.WriteLine($"✓ Applied {Path.GetFileName(f)}");
            }
            Console.WriteLine("All migrations applied.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Migration failed: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }
}
