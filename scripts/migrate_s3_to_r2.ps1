# migrate_s3_to_r2.ps1
# Usage: .\migrate_s3_to_r2.ps1 -s3Bucket tingoradiobucket -s3Prefix music -r2Bucket musicai-media -previewOnly:$false
param(
  [Parameter(Mandatory=$true)] [string] $s3Bucket,
  [Parameter(Mandatory=$true)] [string] $s3Prefix,
  [Parameter(Mandatory=$true)] [string] $r2Bucket,
  [switch] $previewOnly
)

Write-Host "This script uses rclone to sync objects from S3 to R2. Make sure rclone is installed and remotes are configured as 's3remote' and 'r2remote' (see scripts/rclone.conf.example)."

if ($previewOnly) {
  Write-Host "Preview mode: listing objects to be copied..."
  rclone lsjson "s3remote:$s3Bucket/$s3Prefix" | ConvertFrom-Json | Select-Object Name, Size | Format-Table -AutoSize
  exit 0
}

Write-Host "Starting sync from s3remote:$s3Bucket/$s3Prefix to r2remote:$r2Bucket/$s3Prefix"
rclone sync "s3remote:$s3Bucket/$s3Prefix" "r2remote:$r2Bucket/$s3Prefix" --progress

Write-Host "Sync complete. Verify counts:"
rclone lsjson "s3remote:$s3Bucket/$s3Prefix" | ConvertFrom-Json | Measure-Object | Select-Object Count
rclone lsjson "r2remote:$r2Bucket/$s3Prefix" | ConvertFrom-Json | Measure-Object | Select-Object Count

Write-Host "Migration finished. Validate files in Cloudflare dashboard or via rclone lsjson."