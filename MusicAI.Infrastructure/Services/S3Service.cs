using Amazon.S3;
using Amazon.S3.Transfer;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace MusicAI.Infrastructure.Services
{
    public interface IS3Service
    {
        Task<string> UploadFileAsync(string key, Stream stream, string contentType);
        Task<bool> UploadFileAsync(string localFilePath, string s3Key, string contentType);
        Task<bool> DownloadFileAsync(string s3Key, string localFilePath);
        Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry);
        Task DeleteObjectAsync(string key);
        Task<IEnumerable<string>> ListObjectsAsync(string prefix);
        Task<bool> ObjectExistsAsync(string key);
    }

    public class S3Service : IS3Service
    {
        private readonly IAmazonS3 _s3;
        private readonly string _bucket;
        private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache? _cache;

        public S3Service(IConfiguration cfg, Microsoft.Extensions.Caching.Distributed.IDistributedCache? cache = null)
        {
            _cache = cache;
                var accessKey = (cfg["AWS:AccessKey"] ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"))?.Trim();
                var secretKey = (cfg["AWS:SecretKey"] ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"))?.Trim();
                var region = (cfg["AWS:Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION"))?.Trim();
                _bucket = (cfg["AWS:S3Bucket"] ?? Environment.GetEnvironmentVariable("AWS_S3_BUCKET"))?.Trim();

                // S3 Access Point alias or custom endpoint
                var accessPointAlias = (cfg["AWS:S3Endpoint"] ?? Environment.GetEnvironmentVariable("AWS_S3_ENDPOINT"))?.Trim();

                // Basic validation and trimming
                if (string.IsNullOrWhiteSpace(_bucket))
                {
                    // Do not throw here yet if accessPointAlias is an access point ARN/alias - we'll validate below
                    _bucket = null;
                }

                // Configure S3 client with defaults; we'll adjust if a custom endpoint/access point is provided
                AmazonS3Config s3Config = new AmazonS3Config();
                s3Config.UseArnRegion = true; // Required for S3 Access Points when region is provided

                Amazon.RegionEndpoint? regionEndpoint = null;
                if (!string.IsNullOrWhiteSpace(region))
                {
                    try
                    {
                        regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
                        s3Config.RegionEndpoint = regionEndpoint;
                    }
                    catch
                    {
                        // If region string is invalid, leave RegionEndpoint null and continue; custom ServiceURL may be used instead
                        regionEndpoint = null;
                    }
                }

                // Decide how to treat accessPointAlias:
                // - If it looks like an ARN (starts with "arn:") or contains "s3alias", treat as access-point/bucket name
                // - If it looks like a URL or host (contains '.' or starts with http), treat as custom service endpoint (set ServiceURL and ForcePathStyle)
                if (!string.IsNullOrEmpty(accessPointAlias))
                {
                    if (accessPointAlias.StartsWith("arn:", StringComparison.OrdinalIgnoreCase) || accessPointAlias.Contains("s3alias", StringComparison.OrdinalIgnoreCase))
                    {
                        // Use the alias/ARN as the bucket name for access-point usage
                        _bucket = accessPointAlias;
                    }
                    else if (accessPointAlias.StartsWith("http", StringComparison.OrdinalIgnoreCase) || accessPointAlias.Contains('.'))
                    {
                        // Custom S3-compatible endpoint (e.g., MinIO or Cloudflare R2)
                        s3Config.ServiceURL = accessPointAlias;
                        s3Config.ForcePathStyle = true;
                        // When using a custom ServiceURL (like R2) a region may not be required.
                        // Ensure we don't require a RegionEndpoint in that case.
                        s3Config.RegionEndpoint = regionEndpoint;
                    }
                    else
                    {
                        // Unknown form: if _bucket is empty, assume this is an access-point style alias and use it as bucket
                        if (string.IsNullOrEmpty(_bucket)) _bucket = accessPointAlias;
                    }
                }

                if (string.IsNullOrWhiteSpace(_bucket))
                {
                    throw new InvalidOperationException("S3 bucket not configured. Set 'AWS:S3Bucket' or environment variable 'AWS_S3_BUCKET', or provide a valid access point in 'AWS:S3Endpoint'.");
                }

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

        public async Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry)
        {
            try
            {
                // Try cache first when a distributed cache is available
                if (_cache != null)
                {
                    var cacheKey = $"presign:{key}:{(int)expiry.TotalSeconds}";
                    try
                    {
                        var cached = await _cache.GetAsync(cacheKey);
                        if (cached != null && cached.Length > 0)
                        {
                            var cachedUrl = System.Text.Encoding.UTF8.GetString(cached);
                            return cachedUrl;
                        }
                    }
                    catch
                    {
                        // Cache read errors should not block presign generation
                    }
                }

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

                // Store in cache if available - set TTL slightly shorter than expiry to avoid edge cases
                if (_cache != null)
                {
                    var cacheKey = $"presign:{key}:{(int)expiry.TotalSeconds}";
                    try
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(url);
                        var options = new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = expiry > TimeSpan.FromSeconds(10) ? expiry - TimeSpan.FromSeconds(10) : expiry
                        };
                        await _cache.SetAsync(cacheKey, bytes, options);
                    }
                    catch
                    {
                        // Ignore cache write failures
                    }
                }

                return url;
            }
            catch (Exception ex)
            {
                if (ex is AmazonS3Exception s3ex)
                {
                    throw new Exception($"Failed to generate presigned URL for key '{key}': {s3ex.StatusCode} {s3ex.ErrorCode} {s3ex.Message} (RequestId={s3ex.RequestId})", s3ex);
                }
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

        public async Task<bool> UploadFileAsync(string localFilePath, string s3Key, string contentType)
        {
            try
            {
                using var fileStream = File.OpenRead(localFilePath);
                var request = new TransferUtilityUploadRequest
                {
                    InputStream = fileStream,
                    Key = s3Key,
                    BucketName = _bucket,
                    ContentType = contentType,
                    CannedACL = S3CannedACL.Private
                };
                var tu = new TransferUtility(_s3);
                await tu.UploadAsync(request);
                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"S3 upload failed for {localFilePath} -> {s3Key}: {ex.Message}", ex);
            }
        }

        public async Task<bool> DownloadFileAsync(string s3Key, string localFilePath)
        {
            try
            {
                var request = new GetObjectRequest
                {
                    BucketName = _bucket,
                    Key = s3Key
                };

                using var response = await _s3.GetObjectAsync(request);
                using var fileStream = File.Create(localFilePath);
                await response.ResponseStream.CopyToAsync(fileStream);
                return true;
            }
            catch (Exception ex)
            {
                if (ex is AmazonS3Exception s3ex)
                {
                    throw new Exception($"S3 download failed for {s3Key} -> {localFilePath}: {s3ex.StatusCode} {s3ex.ErrorCode} {s3ex.Message} (RequestId={s3ex.RequestId})", s3ex);
                }
                throw new Exception($"S3 download failed for {s3Key} -> {localFilePath}: {ex.Message}", ex);
            }
        }

        public async Task<bool> ObjectExistsAsync(string key)
        {
            try
            {
                var request = new GetObjectMetadataRequest
                {
                    BucketName = _bucket,
                    Key = key
                };
                await _s3.GetObjectMetadataAsync(request);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
            catch (AmazonS3Exception ex)
            {
                // Log more details and rethrow so callers can decide how to handle transient errors
                // For NotFound we already handled above; other statuses may indicate permission/region issues
                throw new Exception($"S3 metadata check failed for {key}: {ex.StatusCode} {ex.ErrorCode} {ex.Message} (RequestId={ex.RequestId})", ex);
            }
            catch (Exception ex)
            {
                // Other errors mean we can't determine; surface them to caller with context
                throw new Exception($"S3 metadata check failed for {key}: {ex.Message}", ex);
            }
        }
    }
}
