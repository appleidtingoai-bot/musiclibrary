using System;
using System.Security.Cryptography;
using System.Text;
using Amazon.CloudFront.Model;

namespace MusicAI.Infrastructure.Services
{
    public static class CloudFrontSigner
    {
        // Environment variables used:
        // CLOUDFRONT_DOMAIN - e.g. d111111abcdef8.cloudfront.net
        // CLOUDFRONT_KEYPAIR_ID - key pair id
        // CLOUDFRONT_PRIVATE_KEY - PEM contents OR CLOUDFRONT_PRIVATE_KEY_PATH - path to PEM

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

        public static string? GetSignedUrl(string resourcePath, TimeSpan expiry)
        {
            if (!IsConfigured) return null;

            try
            {
                // Build full URL if a key/path was provided
                var resourceUrl = resourcePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? resourcePath
                    : ($"https://{Domain!.TrimEnd('/')}/{resourcePath.TrimStart('/')}");

                // Parse private key PEM into RSA
                using var rsa = RSA.Create();
                try
                {
                    // ImportFromPem exists on modern runtimes
                    rsa.ImportFromPem(PrivateKeyPem!.ToCharArray());
                }
                catch
                {
                    // Unable to import PEM into RSA on this runtime or key format; cannot sign.
                    return null;
                }

                var expires = DateTime.UtcNow.Add(expiry);

                try
                {
                    // Use AWS SDK AmazonCloudFrontUrlSigner to create a canned signed URL
                    using var keyReader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(PrivateKeyPem!)));
                    var signed = Amazon.CloudFront.AmazonCloudFrontUrlSigner.GetCannedSignedURL(
                        resourceUrl, 
                        keyReader, 
                        KeyPairId!, 
                        expires);
                    return signed;
                }
                catch
                {
                    // Signing failed
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
