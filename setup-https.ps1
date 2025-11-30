#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Quick HTTPS setup for Roman OAP using existing ALB

.DESCRIPTION
    This script adds HTTPS support to your deployed Roman OAP:
    1. Requests ACM certificate for your domain
    2. Adds HTTPS listener to existing ALB
    3. Optionally redirects HTTP to HTTPS

.PARAMETER DomainName
    Your domain name (e.g., api.yourdomain.com)
    
.PARAMETER Region
    AWS Region (default: us-east-2)
    
.PARAMETER AlbName
    ALB name (default: roman-oap-alb)
    
.PARAMETER RedirectHttp
    Redirect HTTP to HTTPS (default: $true)
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$DomainName,
    [string]$Region = "us-east-2",
    [string]$AlbName = "roman-oap-alb",
    [bool]$RedirectHttp = $true
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Roman OAP HTTPS Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Get ALB ARN
Write-Host "1. Finding Application Load Balancer..." -ForegroundColor Yellow
$albArn = aws elbv2 describe-load-balancers --names $AlbName --region $Region --query "LoadBalancers[0].LoadBalancerArn" --output text
$albDns = aws elbv2 describe-load-balancers --names $AlbName --region $Region --query "LoadBalancers[0].DNSName" --output text

if ([string]::IsNullOrEmpty($albArn) -or $albArn -eq "None") {
    Write-Error "ALB not found. Run deploy-to-aws.ps1 first."
    exit 1
}
Write-Host "Found ALB: $albDns" -ForegroundColor Green

# 2. Get Target Group ARN
Write-Host "`n2. Finding Target Group..." -ForegroundColor Yellow
$tgArn = aws elbv2 describe-target-groups --names roman-oap-tg --region $Region --query "TargetGroups[0].TargetGroupArn" --output text

if ([string]::IsNullOrEmpty($tgArn) -or $tgArn -eq "None") {
    Write-Error "Target group not found."
    exit 1
}
Write-Host "Found Target Group" -ForegroundColor Green

# 3. Request ACM Certificate
Write-Host "`n3. Requesting SSL Certificate..." -ForegroundColor Yellow
$certJson = aws acm request-certificate `
    --domain-name $DomainName `
    --validation-method DNS `
    --region $Region

$certArn = ($certJson | ConvertFrom-Json).CertificateArn
Write-Host "Certificate requested: $certArn" -ForegroundColor Green

# 4. Get DNS validation records
Write-Host "`n4. Getting DNS validation records..." -ForegroundColor Yellow
Start-Sleep -Seconds 5  # Wait for certificate to be created

$certDetails = aws acm describe-certificate --certificate-arn $certArn --region $Region | ConvertFrom-Json
$validationRecord = $certDetails.Certificate.DomainValidationOptions[0].ResourceRecord

Write-Host ""
Write-Host "========================================" -ForegroundColor Yellow
Write-Host "DNS VALIDATION REQUIRED" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "Add this DNS record to validate your domain:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Type:  CNAME" -ForegroundColor White
Write-Host "  Name:  $($validationRecord.Name)" -ForegroundColor Green
Write-Host "  Value: $($validationRecord.Value)" -ForegroundColor Green
Write-Host ""
Write-Host "After adding the DNS record, press Enter to continue..." -ForegroundColor Yellow
Read-Host

# 5. Wait for certificate validation
Write-Host "`n5. Waiting for certificate validation (this may take a few minutes)..." -ForegroundColor Yellow
$maxWaitMinutes = 15
$waitStart = Get-Date

while ($true) {
    $certStatus = aws acm describe-certificate --certificate-arn $certArn --region $Region --query "Certificate.Status" --output text
    
    if ($certStatus -eq "ISSUED") {
        Write-Host "Certificate validated successfully!" -ForegroundColor Green
        break
    }
    
    $elapsed = ((Get-Date) - $waitStart).TotalMinutes
    if ($elapsed -gt $maxWaitMinutes) {
        Write-Error "Certificate validation timed out. Check your DNS settings."
        exit 1
    }
    
    Write-Host "Status: $certStatus ... waiting" -ForegroundColor Yellow
    Start-Sleep -Seconds 30
}

# 6. Create HTTPS listener
Write-Host "`n6. Adding HTTPS listener to ALB..." -ForegroundColor Yellow

$httpsListenerExists = aws elbv2 describe-listeners --load-balancer-arn $albArn --region $Region --query "Listeners[?Port==\`443\`].ListenerArn" --output text

if ([string]::IsNullOrEmpty($httpsListenerExists)) {
    aws elbv2 create-listener `
        --load-balancer-arn $albArn `
        --protocol HTTPS `
        --port 443 `
        --certificates CertificateArn=$certArn `
        --default-actions "Type=forward,TargetGroupArn=$tgArn" `
        --region $Region | Out-Null
    Write-Host "HTTPS listener created" -ForegroundColor Green
} else {
    Write-Host "HTTPS listener already exists" -ForegroundColor Yellow
}

# 7. Redirect HTTP to HTTPS (optional)
if ($RedirectHttp) {
    Write-Host "`n7. Configuring HTTP to HTTPS redirect..." -ForegroundColor Yellow
    
    $httpListenerArn = aws elbv2 describe-listeners --load-balancer-arn $albArn --region $Region --query "Listeners[?Port==\`80\`].ListenerArn" --output text
    
    if (-not [string]::IsNullOrEmpty($httpListenerArn)) {
        # Modify existing HTTP listener to redirect
        aws elbv2 modify-listener `
            --listener-arn $httpListenerArn `
            --default-actions "Type=redirect,RedirectConfig={Protocol=HTTPS,Port=443,StatusCode=HTTP_301}" `
            --region $Region | Out-Null
        Write-Host "HTTP traffic will redirect to HTTPS" -ForegroundColor Green
    }
}

# 8. Add DNS CNAME for domain
Write-Host "`n8. Final DNS Configuration..." -ForegroundColor Yellow
Write-Host ""
Write-Host "Add this DNS record to point your domain to the load balancer:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Type:  CNAME" -ForegroundColor White
Write-Host "  Name:  $DomainName" -ForegroundColor Green
Write-Host "  Value: $albDns" -ForegroundColor Green
Write-Host ""

# Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "HTTPS SETUP COMPLETE!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Your Roman OAP will be available at:" -ForegroundColor Green
Write-Host "  https://$DomainName" -ForegroundColor Yellow
Write-Host ""
Write-Host "Test endpoints:" -ForegroundColor Cyan
Write-Host "  - https://$DomainName/api/oap/health" -ForegroundColor White
Write-Host "  - https://$DomainName/api/oap/current" -ForegroundColor White
Write-Host "  - https://$DomainName/swagger" -ForegroundColor White
Write-Host ""
Write-Host "Note: DNS propagation may take 5-60 minutes" -ForegroundColor Yellow
Write-Host ""
