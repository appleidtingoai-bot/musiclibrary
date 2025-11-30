using Amazon.S3;
using Amazon.S3.Transfer;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace MusicAI.Infrastructure.Services
{
    public interface IS3Service
    {
        Task<string> UploadFileAsync(string key, Stream stream, string contentType);
        Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry);
        Task DeleteObjectAsync(string key);
        Task<IEnumerable<string>> ListObjectsAsync(string prefix);
    }

    public class S3Service : IS3Service
    {
        private readonly IAmazonS3 _s3;
        private readonly string _bucket;

        public S3Service(IConfiguration cfg)
        {
                var accessKey = cfg["AWS:AccessKey"] ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                var secretKey = cfg["AWS:SecretKey"] ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
                var region = cfg["AWS:Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION");
                _bucket = cfg["AWS:S3Bucket"] ?? Environment.GetEnvironmentVariable("AWS_S3_BUCKET");

                // S3 Access Point alias (if provided, use as bucket name)
                var accessPointAlias = cfg["AWS:S3Endpoint"] ?? Environment.GetEnvironmentVariable("AWS_S3_ENDPOINT");
                
                // If access point alias is provided, use it as the bucket name
                if (!string.IsNullOrEmpty(accessPointAlias) && !accessPointAlias.StartsWith("http"))
                {
                    _bucket = accessPointAlias;
                }

                if (string.IsNullOrWhiteSpace(_bucket))
                {
                    throw new InvalidOperationException("S3 bucket not configured. Set 'AWS:S3Bucket' or environment variable 'AWS_S3_BUCKET'.");
                }

                if (string.IsNullOrWhiteSpace(region))
                {
                    throw new InvalidOperationException("AWS region not configured. Set 'AWS:Region' or environment variable 'AWS_REGION'.");
                }

                var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
                
                // Configure S3 client with UseArnRegion for access point support
                var s3Config = new AmazonS3Config
                {
                    RegionEndpoint = regionEndpoint,
                    UseArnRegion = true // Required for S3 Access Points
                };

                if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
                {
                    _s3 = new AmazonS3Client(accessKey, secretKey, s3Config);
                }
                else
                {
                    _s3 = new AmazonS3Client(s3Config);
                }
        }

        public async Task<string> UploadFileAsync(string key, Stream stream, string contentType)
        {
            try
            {
                var request = new TransferUtilityUploadRequest
                {
                    InputStream = stream,
                    Key = key,
                    BucketName = _bucket,
                    ContentType = contentType,
                    CannedACL = S3CannedACL.Private
                };
                var tu = new TransferUtility(_s3);
                await tu.UploadAsync(request);
                return $"s3://{_bucket}/{key}";
            }
            catch (Exception ex)
            {
                throw new Exception($"S3 upload failed for key '{key}': {ex.Message}", ex);
            }
        }

        public Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry)
        {
            try
            {
                var req = new GetPreSignedUrlRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    Expires = DateTime.UtcNow.Add(expiry)
                };
                var url = _s3.GetPreSignedURL(req);
                
                // Validate URL before returning
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    throw new Exception($"Generated presigned URL is invalid: {url}");
                }
                
                return Task.FromResult(url);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to generate presigned URL for key '{key}': {ex.Message}", ex);
            }
        }

        public async Task DeleteObjectAsync(string key)
        {
            var req = new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = key
            };
            await _s3.DeleteObjectAsync(req);
        }

        public async Task<IEnumerable<string>> ListObjectsAsync(string prefix)
        {
            var list = new List<string>();
            string continuationToken = null;
            do
            {
                var req = new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken,
                    MaxKeys = 1000
                };
                var resp = await _s3.ListObjectsV2Async(req);
                foreach (var o in resp.S3Objects)
                {
                    list.Add(o.Key);
                }
                continuationToken = resp.NextContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));

            return list;
        }
    }
}
