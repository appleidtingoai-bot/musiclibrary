using System;
using System;
using System;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;

namespace MusicAI.Infrastructure.Services
{
    public static class CloudFrontCookieSigner
    {
        private static readonly string? Domain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN");
        private static readonly string? KeyPairId = Environment.GetEnvironmentVariable("CLOUDFRONT_KEYPAIR_ID");
        private static readonly string? PrivateKeyPem = LoadPrivateKeyPem();

        private static string? LoadPrivateKeyPem()
        {
            var pem = Environment.GetEnvironmentVariable("CLOUDFRONT_PRIVATE_KEY");
            if (!string.IsNullOrEmpty(pem)) return pem;
            var path = Environment.GetEnvironmentVariable("CLOUDFRONT_PRIVATE_KEY_PATH");
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                return System.IO.File.ReadAllText(path);
            }
            return null;
        }

        public static bool IsConfigured => !string.IsNullOrEmpty(Domain) && !string.IsNullOrEmpty(KeyPairId) && !string.IsNullOrEmpty(PrivateKeyPem);

        // Returns cookies values: CloudFront-Policy, CloudFront-Signature, CloudFront-Key-Pair-Id
        public static (string policy, string signature, string keyPairId)? GetSignedCookies(string resourcePattern, TimeSpan expiry)
        {
            if (!IsConfigured) return null;

            try
            {
                var expires = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeSeconds();

                // Build policy structure using dictionaries so we can include the AWS:EpochTime key name
                var policy = new Dictionary<string, object>
                {
                    ["Statement"] = new[] {
                        new Dictionary<string, object>
                        {
                            ["Resource"] = resourcePattern.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? resourcePattern : $"https://{Domain!.TrimEnd('/')}/{resourcePattern.TrimStart('/')}",
                            ["Condition"] = new Dictionary<string, object>
                            {
                                ["DateLessThan"] = new Dictionary<string, object>
                                {
                                    ["AWS:EpochTime"] = expires
                                }
                            }
                        }
                    }
                };

                var policyJson = JsonSerializer.Serialize(policy);

                using var rsa = RSA.Create();
                try
                {
                    rsa.ImportFromPem(PrivateKeyPem!);
                }
                catch
                {
                    return null;
                }

                var policyBytes = Encoding.UTF8.GetBytes(policyJson);
                var signatureBytes = rsa.SignData(policyBytes, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

                static string ToBase64Url(byte[] input)
                {
                    var s = Convert.ToBase64String(input);
                    s = s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
                    return s;
                }

                var policyB64 = ToBase64Url(policyBytes);
                var sigB64 = ToBase64Url(signatureBytes);

                return (policyB64, sigB64, KeyPairId!);
            }
            catch
            {
                return null;
            }
        }
    }
}
