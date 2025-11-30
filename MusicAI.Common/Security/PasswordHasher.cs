using System;
using System.Security.Cryptography;
using System.Text;

namespace MusicAI.Common.Security
{
    public static class PasswordHasher
    {
        // Read salt from env var or use default
        private static readonly string Salt = Environment.GetEnvironmentVariable("APP_PASSWORD_SALT") ?? ":adminsalt:v1";

        public static string Hash(string password)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(password + Salt);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }

        public static bool Verify(string password, string hash)
        {
            var h2 = Hash(password);
            return string.Equals(h2, hash, StringComparison.Ordinal);
        }
    }
}
