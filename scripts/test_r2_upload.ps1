<#
Test script to publish the Cloudflare Worker (wrangler) and attempt a test upload to R2.

Usage (on the server):
  # Ensure env vars are set (CF_API_TOKEN, AWS__S3Bucket, AWS__S3Endpoint, AWS__AccessKey, AWS__SecretKey, AWS__Region)
  .\scripts\test_r2_upload.ps1

This script will try these upload paths in order:
  1. If `wrangler` is installed, publish the worker in `infra/cloudflare`.
  2. If `rclone` is available and a remote named `r2remote` is configured, use it to copy the test file to R2.
  3. Else, if `aws` CLI is available, use `aws s3 cp` with `--endpoint-url` to upload the test file.

Note: set `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` from `AWS__AccessKey` and `AWS__SecretKey` if needed.
#>

Set-StrictMode -Version Latest

Write-Host "Starting R2 test script..."

$bucket = $env:AWS__S3Bucket
$endpoint = $env:AWS__S3Endpoint
$access = $env:AWS__AccessKey
$secret = $env:AWS__SecretKey
$region = $env:AWS__Region

if ([string]::IsNullOrWhiteSpace($bucket)) {
    Write-Error "AWS__S3Bucket is not set. Set AWS__S3Bucket to your R2 bucket name (or AWS S3 bucket)."
    exit 2
}

if ([string]::IsNullOrWhiteSpace($endpoint)) {
    Write-Warning "AWS__S3Endpoint is not set. If you're targeting Cloudflare R2 you should set AWS__S3Endpoint to the R2 endpoint."
}

# 1) Publish worker with wrangler if available
$wrangler = Get-Command wrangler -ErrorAction SilentlyContinue
if ($wrangler) {
    Write-Host "Found wrangler at $($wrangler.Path). Publishing worker from infra/cloudflare..."
    Push-Location "infra\cloudflare"
    try {
        & wrangler publish
    }
    catch {
        Write-Warning "wrangler publish failed: $_"
    }
    finally { Pop-Location }
}
else { Write-Host "wrangler not found in PATH; skipping worker publish." }

# Ensure test file exists
$testFile = Join-Path $PSScriptRoot "test-upload.txt"
if (-not (Test-Path $testFile)) {
    "This is a test upload from test_r2_upload.ps1" | Out-File -FilePath $testFile -Encoding UTF8
}

# 2) Try rclone if available
$rclone = Get-Command rclone -ErrorAction SilentlyContinue
if ($rclone) {
    Write-Host "rclone found at $($rclone.Path). Attempting rclone copy to r2remote:$bucket/"
    try {
        & rclone copy $testFile "r2remote:$bucket/" --progress
        if ($LASTEXITCODE -eq 0) { Write-Host "rclone upload succeeded."; exit 0 }
    }
    catch {
        Write-Warning "rclone upload failed: $_"
    }
}
else { Write-Host "rclone not found; skipping rclone upload." }

# 3) Fall back to AWS CLI (S3-compatible) if available
$aws = Get-Command aws -ErrorAction SilentlyContinue
if ($aws) {
    Write-Host "AWS CLI found at $($aws.Path). Using aws s3 cp with endpoint $endpoint"
    if (-not [string]::IsNullOrWhiteSpace($access)) { $env:AWS_ACCESS_KEY_ID = $access }
    if (-not [string]::IsNullOrWhiteSpace($secret)) { $env:AWS_SECRET_ACCESS_KEY = $secret }
    if (-not [string]::IsNullOrWhiteSpace($region)) { $env:AWS_REGION = $region }

    $dest = "s3://$bucket/test-upload.txt"
    $cmd = @('s3','cp',$testFile,$dest)
    if (-not [string]::IsNullOrWhiteSpace($endpoint)) { $cmd += @('--endpoint-url',$endpoint) }
    try {
        & aws @cmd
        if ($LASTEXITCODE -eq 0) { Write-Host "aws s3 cp succeeded."; exit 0 }
    }
    catch {
        Write-Warning "aws s3 cp failed: $_"
    }
}
else { Write-Host "AWS CLI not found; skipping aws upload." }

# If we reach here, the script did not automatically exit after upload. Attempt to verify the file exists via HTTP.
$keyPath = "test-upload.txt"

function Try-HttpGet($url) {
    try {
        Write-Host "Trying GET: $url"
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 15 -ErrorAction Stop
        Write-Host "GET succeeded: Status $($resp.StatusCode) Length: $($resp.RawContentLength)"
        return $true
    }
    catch {
        Write-Warning "GET failed for $url : $_"
        return $false
    }
}

$succeeded = $false

# 1) Try direct R2 endpoint URL if endpoint was provided
if (-not [string]::IsNullOrWhiteSpace($endpoint)) {
    # Assume endpoint is like https://<accountid>.r2.cloudflarestorage.com
    $r2Url = $endpoint.TrimEnd('/') + "/" + $bucket.Trim('/') + "/" + $keyPath
    if (Try-HttpGet $r2Url) { $succeeded = $true }
}

# 2) Try workers.dev public worker URL if infra/cloudflare/wrangler.toml exists
$wranglerToml = Join-Path $PSScriptRoot "..\infra\cloudflare\wrangler.toml"
if (-not $succeeded -and (Test-Path $wranglerToml)) {
    try {
        $toml = Get-Content $wranglerToml -Raw
        $name = ($toml -match 'name\s*=\s*"([^"]+)"') | Out-Null; $name = $Matches[1]
        $acct = ($toml -match 'account_id\s*=\s*"([^"]+)"') | Out-Null; $acct = $Matches[1]
        if ($name -and $acct) {
            $workerUrl = "https://$name.$acct.workers.dev/media/$keyPath"
            if (Try-HttpGet $workerUrl) { $succeeded = $true }
        }
    }
    catch {
        Write-Warning "Could not parse wrangler.toml or try worker URL: $_"
    }
}

if ($succeeded) {
    Write-Host "Verification succeeded: file appears reachable via HTTP."; exit 0
}

Write-Error "Upload did not complete via rclone/aws or file not reachable via HTTP. Confirm credentials and bucket/policy."; exit 3
