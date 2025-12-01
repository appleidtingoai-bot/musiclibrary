# CloudFront CDN Setup Guide

This guide explains how to set up AWS CloudFront CDN in front of your S3 bucket for improved streaming performance (Spotify-like feature).

## Benefits

- **Lower Latency**: Content served from edge locations closer to users globally
- **Reduced S3 Costs**: CloudFront caches content, reducing S3 bandwidth charges
- **Better Scalability**: Handle traffic spikes without overloading S3
- **Improved Streaming**: Faster audio delivery with HTTP/2 support

## Prerequisites

- AWS account with S3 bucket (`tingoradiobucket`) already configured
- AWS CLI installed (optional)
- Access to AWS Console

---

## Step 1: Create CloudFront Distribution

### Via AWS Console

1. **Go to CloudFront Console**
   - Navigate to: https://console.aws.amazon.com/cloudfront/
   - Click **Create Distribution**

2. **Origin Settings**
   - **Origin Domain**: `tingoradiobucket.s3.us-east-2.amazonaws.com`
   - **Origin Path**: Leave blank (or `/music` if you only want to serve music folder)
   - **Name**: `MusicAI-S3-Origin`
   - **Origin Access**: Choose **Origin Access Control (OAC)** (recommended)
     - Click **Create Control Setting**
     - Name: `MusicAI-OAC`
     - Signing behavior: Sign requests (recommended)
     - Origin type: S3

3. **Default Cache Behavior**
   - **Viewer Protocol Policy**: `Redirect HTTP to HTTPS`
   - **Allowed HTTP Methods**: `GET, HEAD, OPTIONS`
   - **Cache Key and Origin Requests**:
     - **Cache Policy**: Create custom policy or use `CachingOptimized`
     - **Include Query Strings**: Yes (for tokenized URLs with `?t=...`)
   - **Compress Objects Automatically**: Yes

4. **Distribution Settings**
   - **Price Class**: Choose based on your audience (e.g., `Use All Edge Locations` for global)
   - **Alternate Domain Names (CNAMEs)**: Add your custom domain if you have one (e.g., `cdn.tingoradio.ai`)
   - **SSL Certificate**: 
     - Default CloudFront certificate (`*.cloudfront.net`)
     - OR request custom ACM certificate for your domain
   - **Default Root Object**: Leave blank

5. **Create Distribution**
   - Click **Create Distribution**
   - Wait 5-15 minutes for deployment (Status: "Deployed")
   - Note your CloudFront domain: `d1234abcd.cloudfront.net`

---

## Step 2: Configure S3 Bucket Policy for CloudFront OAC

After creating the distribution, AWS will provide a bucket policy statement. Copy it and add to your S3 bucket policy.

1. Go to **S3 Console** → `tingoradiobucket` → **Permissions** → **Bucket Policy**

2. Add the CloudFront OAC policy (example):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "AllowCloudFrontServicePrincipal",
      "Effect": "Allow",
      "Principal": {
        "Service": "cloudfront.amazonaws.com"
      },
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::tingoradiobucket/*",
      "Condition": {
        "StringEquals": {
          "AWS:SourceArn": "arn:aws:cloudfront::YOUR_ACCOUNT_ID:distribution/YOUR_DISTRIBUTION_ID"
        }
      }
    }
  ]
}
```

3. Save the bucket policy

---

## Step 3: Create Custom Cache Policy (Recommended)

CloudFront's default policies may not work well with tokenized streaming URLs. Create a custom policy:

1. **Go to CloudFront** → **Policies** → **Cache Policies** → **Create Policy**

2. **Settings**:
   - **Name**: `MusicAI-Streaming-Cache`
   - **Minimum TTL**: `3600` (1 hour)
   - **Maximum TTL**: `86400` (24 hours)
   - **Default TTL**: `7200` (2 hours)
   
3. **Cache Key Settings**:
   - **Query Strings**: Include specific query strings
     - Add: `t` (for token parameter)
   - **Headers**: Include `Origin` (for CORS)
   - **Cookies**: None

4. **Create** and attach to your distribution's default behavior

---

## Step 4: Update MusicAI Application Configuration

Update your application to use CloudFront instead of direct S3 URLs.

### Option A: Environment Variable (Recommended)

Add to your `.env` file:

```bash
# CloudFront CDN Configuration
CLOUDFRONT_DOMAIN=d1234abcd.cloudfront.net
# OR custom domain if configured
CLOUDFRONT_DOMAIN=cdn.tingoradio.ai

# Keep S3 config for uploads (CloudFront is read-only)
AWS__S3BUCKET=tingoradiobucket
AWS__REGION=us-east-2
AWS__AccessKey=YOUR_ACCESS_KEY
AWS__SecretKey=YOUR_SECRET_KEY
```

### Option B: Code Changes

Update `S3Service.cs` to use CloudFront for presigned URLs:

```csharp
public Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry)
{
    // Check if CloudFront is configured
    var cloudFrontDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN");
    
    if (!string.IsNullOrEmpty(cloudFrontDomain))
    {
        // Use CloudFront URL instead of direct S3
        var cloudFrontUrl = $"https://{cloudFrontDomain}/{key}";
        return Task.FromResult(cloudFrontUrl);
    }
    
    // Fallback to S3 presigned URL
    var req = new GetPreSignedUrlRequest
    {
        BucketName = _bucket,
        Key = key,
        Expires = DateTime.UtcNow.Add(expiry)
    };
    return Task.FromResult(_s3.GetPreSignedURL(req));
}
```

### Option C: Controller-Level Override

Update `OapChatController.cs` to inject CloudFront domain:

```csharp
private string GetStreamingBaseUrl()
{
    var cloudFrontDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN");
    
    if (!string.IsNullOrEmpty(cloudFrontDomain))
    {
        return $"https://{cloudFrontDomain}";
    }
    
    // Fallback to local streaming proxy
    return $"{Request.Scheme}://{Request.Host}";
}

// Usage in endpoints:
var baseUrl = GetStreamingBaseUrl();
HlsUrl = $"{baseUrl}/api/music/hls/{track.S3Key}{encoded}"
```

---

## Step 5: Test CloudFront Streaming

### Test Direct Access

```bash
# Replace with your CloudFront domain
curl -I https://d1234abcd.cloudfront.net/music/afropop/track.mp3
```

Expected response headers:
- `HTTP/2 200`
- `x-cache: Hit from cloudfront` (after first request)
- `x-amz-cf-pop: IAD89-C1` (edge location)

### Test Tokenized Streaming

```bash
# Get a track URL from API
curl https://tingoradiomusiclibrary.tingoai.ai/api/oap/playlist-metadata/user123?count=1

# Extract s3Key and request tokenized URL
curl https://tingoradiomusiclibrary.tingoai.ai/api/oap/track-url?s3Key=music/afropop/track.mp3

# Test returned URL in browser or audio player
```

### Verify Cache Behavior

Make the same request twice and check `x-cache` header:
- First request: `Miss from cloudfront`
- Second request: `Hit from cloudfront` ✅

---

## Step 6: Monitor and Optimize

### CloudWatch Metrics

Monitor CloudFront performance:
- **Cache Hit Rate**: Should be > 80% after warm-up
- **Origin Latency**: Should decrease as cache warms
- **Data Transfer**: Compare S3 vs CloudFront costs

### Cost Optimization

1. **Enable Compression**: Reduces bandwidth (already enabled)
2. **Tune TTLs**: Increase for popular tracks, decrease for new uploads
3. **Price Class**: Use regional edge locations if audience is local
4. **Invalidations**: Avoid frequent invalidations (costly)

### Cache Invalidation (When Needed)

If you update a file in S3 and need immediate propagation:

```bash
aws cloudfront create-invalidation \
  --distribution-id E1ABCDEF123456 \
  --paths "/music/*"
```

**Note**: First 1000 invalidations/month are free, then $0.005 per path

---

## Step 7: Custom Domain Setup (Optional)

### Prerequisites
- Domain name (e.g., `cdn.tingoradio.ai`)
- Route53 or external DNS provider

### Steps

1. **Request ACM Certificate**
   - Go to AWS Certificate Manager (ACM) in **us-east-1** (required for CloudFront)
   - Request public certificate for `cdn.tingoradio.ai`
   - Validate via DNS (add CNAME records)

2. **Add CNAME to CloudFront Distribution**
   - Go to CloudFront → Distribution → Edit
   - **Alternate Domain Names**: Add `cdn.tingoradio.ai`
   - **SSL Certificate**: Select your ACM certificate
   - Save changes

3. **Update DNS**
   - Add CNAME record in Route53 or your DNS provider:
     ```
     cdn.tingoradio.ai  CNAME  d1234abcd.cloudfront.net
     ```

4. **Update Application**
   - Set `CLOUDFRONT_DOMAIN=cdn.tingoradio.ai` in `.env`

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                         User's Device                           │
│                     (Browser / Mobile App)                      │
└─────────────────────────┬───────────────────────────────────────┘
                          │
                          │ HTTPS Request
                          │
┌─────────────────────────▼───────────────────────────────────────┐
│                   CloudFront Edge Location                      │
│                  (Nearest to User Globally)                     │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  Cache Storage (SSD)                                     │  │
│  │  - Popular tracks cached here                            │  │
│  │  - Cache Hit: Serve directly (< 50ms)                    │  │
│  │  - Cache Miss: Fetch from origin                         │  │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────┬───────────────────────────────────────┘
                          │
                          │ Cache Miss? Forward to Origin
                          │
┌─────────────────────────▼───────────────────────────────────────┐
│                     S3 Bucket (Origin)                          │
│                    tingoradiobucket                             │
│                      us-east-2                                  │
│                                                                 │
│  /music/afropop/*.mp3                                           │
│  /music/rnb/*.mp3                                               │
│  /music/highlife/*.mp3                                          │
└─────────────────────────────────────────────────────────────────┘
```

---

## Performance Comparison

| Metric | Direct S3 | With CloudFront |
|--------|-----------|-----------------|
| Average Latency (Global) | 200-500ms | 50-150ms |
| Cache Hit Rate | N/A | 85-95% |
| S3 Data Transfer Costs | $0.09/GB | $0.02/GB (after cache) |
| Concurrent Streams | Limited by S3 rate limits | Unlimited (scaled by AWS) |

---

## Troubleshooting

### Issue: 403 Forbidden

**Cause**: S3 bucket policy not configured for CloudFront OAC

**Solution**: Verify bucket policy includes CloudFront service principal (see Step 2)

### Issue: CORS Errors

**Cause**: CloudFront not forwarding `Origin` header

**Solution**: Update cache policy to include `Origin` header in cache key

### Issue: Tokenized URLs Not Working

**Cause**: Query string `?t=...` not included in cache key

**Solution**: Update cache policy to include `t` query parameter (see Step 3)

### Issue: Cache Not Working

**Cause**: Default TTLs too low or cache policy misconfigured

**Solution**: Check cache policy and increase TTLs for audio files

---

## Next Steps

1. ✅ Deploy CloudFront distribution
2. ✅ Update S3 bucket policy
3. ✅ Configure custom cache policy
4. ✅ Update application to use CloudFront URLs
5. ✅ Test streaming with cache hit/miss verification
6. ⏳ Monitor CloudWatch metrics
7. ⏳ Optimize cache policies based on usage patterns
8. ⏳ Consider adding custom domain with SSL

---

## Additional Resources

- [CloudFront Developer Guide](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/)
- [CloudFront Pricing](https://aws.amazon.com/cloudfront/pricing/)
- [Cache Policy Best Practices](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/controlling-the-cache-key.html)
- [Origin Access Control (OAC)](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-restricting-access-to-s3.html)

---

## Support

For issues or questions:
- Check CloudWatch Logs: CloudFront → Distribution → Monitoring
- Enable CloudFront logging for detailed access logs
- Contact AWS Support for distribution-specific issues
