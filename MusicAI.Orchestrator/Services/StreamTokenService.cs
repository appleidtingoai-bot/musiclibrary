using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace MusicAI.Orchestrator.Services
{
    public interface IStreamTokenService
    {
        string GenerateToken(string s3Key, TimeSpan ttl, bool allowExplicit = false);
        bool ValidateToken(string token, out string s3Key, out bool allowExplicit);
    }

    public class StreamTokenService : IStreamTokenService
    {
        private readonly byte[] _signingKey;
        private readonly string _issuer = "musicai";

        public StreamTokenService(IConfiguration config)
        {
            var key = config["STREAM_TOKEN_KEY"] ?? config["JWT_SECRET"] ?? Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_stream_key_please_change_in_production";
            _signingKey = Encoding.UTF8.GetBytes(key);
        }

        public string GenerateToken(string s3Key, TimeSpan ttl, bool allowExplicit = false)
        {
            var now = DateTime.UtcNow;
            var claims = new[] {
                new Claim("s3", s3Key),
                new Claim("allow_explicit", allowExplicit ? "1" : "0")
            };
            var creds = new SigningCredentials(new SymmetricSecurityKey(_signingKey), SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(_issuer, _issuer, claims, notBefore: now, expires: now.Add(ttl), signingCredentials: creds);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public bool ValidateToken(string token, out string s3Key, out bool allowExplicit)
        {
            s3Key = string.Empty;
            allowExplicit = false;
            if (string.IsNullOrEmpty(token)) return false;

            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _issuer,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(_signingKey),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            try
            {
                var principal = tokenHandler.ValidateToken(token, validationParams, out var validatedToken);
                var s3Claim = principal.FindFirst("s3");
                if (s3Claim == null) return false;
                s3Key = s3Claim.Value;

                var explicitClaim = principal.FindFirst("allow_explicit");
                if (explicitClaim != null && (explicitClaim.Value == "1" || explicitClaim.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                {
                    allowExplicit = true;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
