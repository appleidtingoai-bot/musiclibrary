using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MusicAI.Orchestrator.Services
{
    /// <summary>
    /// Manages one-time streaming tokens for secure music access
    /// Prevents URL copying - tokens expire after first use or timeout
    /// </summary>
    public static class StreamingTokenStore
    {
        private static readonly ConcurrentDictionary<string, StreamingToken> _tokens = new();
        private const int TOKEN_VALIDITY_SECONDS = 30; // Token valid for 30 seconds only

        public class StreamingToken
        {
            public string MusicKey { get; set; } = string.Empty;
            public string? OapIdentifier { get; set; }
            public string? IpAddress { get; set; }
            public DateTime CreatedAt { get; set; }
            public bool Used { get; set; }
        }

        /// <summary>
        /// Generate a one-time streaming token for a music file
        /// </summary>
        public static string GenerateToken(string musicKey, string? oapIdentifier = null, string? ipAddress = null)
        {
            var token = GenerateSecureToken();
            var streamToken = new StreamingToken
            {
                MusicKey = musicKey,
                OapIdentifier = oapIdentifier,
                IpAddress = ipAddress,
                CreatedAt = DateTime.UtcNow,
                Used = false
            };

            _tokens[token] = streamToken;

            // Clean up old tokens
            CleanupExpiredTokens();

            return token;
        }

        /// <summary>
        /// Validate and consume a streaming token (one-time use)
        /// </summary>
        public static bool ValidateAndConsumeToken(string token, string? ipAddress, out string? musicKey)
        {
            musicKey = null;

            if (string.IsNullOrEmpty(token))
                return false;

            if (!_tokens.TryGetValue(token, out var streamToken))
                return false;

            // Check if token is expired
            var age = DateTime.UtcNow - streamToken.CreatedAt;
            if (age.TotalSeconds > TOKEN_VALIDITY_SECONDS)
            {
                _tokens.TryRemove(token, out _);
                return false;
            }

            // Check if already used
            if (streamToken.Used)
            {
                return false;
            }

            // Validate IP address if provided
            if (!string.IsNullOrEmpty(streamToken.IpAddress) &&
                !string.IsNullOrEmpty(ipAddress) &&
                streamToken.IpAddress != ipAddress)
            {
                return false;
            }

            // Mark as used and return music key
            streamToken.Used = true;
            musicKey = streamToken.MusicKey;

            // Remove token immediately after use
            _tokens.TryRemove(token, out _);

            return true;
        }

        /// <summary>
        /// Remove expired tokens from memory
        /// </summary>
        private static void CleanupExpiredTokens()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _tokens)
            {
                var age = now - kvp.Value.CreatedAt;
                if (age.TotalSeconds > TOKEN_VALIDITY_SECONDS || kvp.Value.Used)
                {
                    _tokens.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Generate a cryptographically secure random token
        /// </summary>
        private static string GenerateSecureToken()
        {
            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[32];
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .Replace("+", "")
                .Replace("/", "")
                .Replace("=", "")
                .Substring(0, 40);
        }
    }
}
