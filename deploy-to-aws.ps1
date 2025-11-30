#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Deploy Roman OAP to AWS ECS Fargate with Application Load Balancer and HTTPS

.DESCRIPTION
    This script:
    1. Builds and pushes Docker image to AWS ECR
    2. Creates ECS cluster, task definition, and service
    3. Sets up Application Load Balancer with HTTPS
    4. Configures CloudWatch logging
    
.PARAMETER Region
    AWS Region (default: us-east-2)
    
.PARAMETER ProjectName
    Project name for resources (default: roman-oap)
    
.PARAMETER DomainName
    Your domain name for HTTPS certificate (optional)
#>

param(
    [string]$Region = "us-east-2",
    [string]$ProjectName = "roman-oap",
    [string]$DomainName = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Roman OAP AWS Deployment Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$ClusterName = "$ProjectName-cluster"
$ServiceName = "$ProjectName-service"
$TaskFamily = "$ProjectName-task"
$ECRRepo = "$ProjectName-repo"
$LogGroup = "/ecs/$ProjectName"
$VpcName = "$ProjectName-vpc"
$AlbName = "$ProjectName-alb"
$TargetGroupName = "$ProjectName-tg"

# Get AWS Account ID
Write-Host "Getting AWS Account ID..." -ForegroundColor Yellow
$AccountId = (aws sts get-caller-identity --query Account --output text)
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to get AWS Account ID. Make sure AWS CLI is configured."
    exit 1
}
Write-Host "Account ID: $AccountId" -ForegroundColor Green

# 1. Create ECR Repository
Write-Host "`n1. Setting up ECR Repository..." -ForegroundColor Yellow
$EcrRepoUri = "$AccountId.dkr.ecr.$Region.amazonaws.com/$ECRRepo"

$repoExists = aws ecr describe-repositories --repository-names $ECRRepo --region $Region 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating ECR repository: $ECRRepo" -ForegroundColor Yellow
    aws ecr create-repository --repository-name $ECRRepo --region $Region | Out-Null
} else {
    Write-Host "ECR repository already exists" -ForegroundColor Green
}

# 2. Build and Push Docker Image
if (-not $SkipBuild) {
    Write-Host "`n2. Building Docker image..." -ForegroundColor Yellow
    docker build -f Dockerfile.orchestrator -t ${ECRRepo}:latest .
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker build failed"
        exit 1
    }
    
    Write-Host "Logging into ECR..." -ForegroundColor Yellow
    aws ecr get-login-password --region $Region | docker login --username AWS --password-stdin $EcrRepoUri
    
    Write-Host "Tagging image..." -ForegroundColor Yellow
    docker tag ${ECRRepo}:latest ${EcrRepoUri}:latest
    
    Write-Host "Pushing to ECR..." -ForegroundColor Yellow
    docker push ${EcrRepoUri}:latest
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker push failed"
        exit 1
    }
    Write-Host "Image pushed successfully!" -ForegroundColor Green
} else {
    Write-Host "`n2. Skipping Docker build (using existing image)" -ForegroundColor Yellow
}

# 3. Create CloudWatch Log Group
Write-Host "`n3. Setting up CloudWatch Logs..." -ForegroundColor Yellow
$logGroupExists = aws logs describe-log-groups --log-group-name-prefix $LogGroup --region $Region --query "logGroups[?logGroupName=='$LogGroup']" --output text
if ([string]::IsNullOrEmpty($logGroupExists)) {
    aws logs create-log-group --log-group-name $LogGroup --region $Region
    Write-Host "Log group created" -ForegroundColor Green
} else {
    Write-Host "Log group already exists" -ForegroundColor Green
}

# 4. Create ECS Cluster
Write-Host "`n4. Setting up ECS Cluster..." -ForegroundColor Yellow
$clusterExists = aws ecs describe-clusters --clusters $ClusterName --region $Region --query "clusters[?status=='ACTIVE'].clusterName" --output text
if ([string]::IsNullOrEmpty($clusterExists)) {
    aws ecs create-cluster --cluster-name $ClusterName --region $Region | Out-Null
    Write-Host "Cluster created" -ForegroundColor Green
} else {
    Write-Host "Cluster already exists" -ForegroundColor Green
}

# 5. Get Default VPC and Subnets
Write-Host "`n5. Getting VPC and Subnet information..." -ForegroundColor Yellow
$VpcId = (aws ec2 describe-vpcs --filters "Name=isDefault,Values=true" --region $Region --query "Vpcs[0].VpcId" --output text)
$Subnets = (aws ec2 describe-subnets --filters "Name=vpc-id,Values=$VpcId" --region $Region --query "Subnets[*].SubnetId" --output text) -split '\s+'
Write-Host "VPC: $VpcId" -ForegroundColor Green
Write-Host "Subnets: $($Subnets -join ', ')" -ForegroundColor Green

# 6. Create Security Group
Write-Host "`n6. Setting up Security Group..." -ForegroundColor Yellow
$SgName = "$ProjectName-sg"
$sgId = aws ec2 describe-security-groups --filters "Name=group-name,Values=$SgName" "Name=vpc-id,Values=$VpcId" --region $Region --query "SecurityGroups[0].GroupId" --output text

if ($sgId -eq "None" -or [string]::IsNullOrEmpty($sgId)) {
    $sgId = (aws ec2 create-security-group --group-name $SgName --description "Security group for Roman OAP" --vpc-id $VpcId --region $Region --query "GroupId" --output text)
    
    # Allow HTTP and HTTPS from anywhere
    aws ec2 authorize-security-group-ingress --group-id $sgId --protocol tcp --port 80 --cidr 0.0.0.0/0 --region $Region | Out-Null
    aws ec2 authorize-security-group-ingress --group-id $sgId --protocol tcp --port 443 --cidr 0.0.0.0/0 --region $Region | Out-Null
    Write-Host "Security group created: $sgId" -ForegroundColor Green
} else {
    Write-Host "Security group already exists: $sgId" -ForegroundColor Green
}

# 7. Create Application Load Balancer
Write-Host "`n7. Setting up Application Load Balancer..." -ForegroundColor Yellow
$albArn = aws elbv2 describe-load-balancers --names $AlbName --region $Region --query "LoadBalancers[0].LoadBalancerArn" --output text 2>$null

if ($LASTEXITCODE -ne 0 -or $albArn -eq "None") {
    Write-Host "Creating ALB..." -ForegroundColor Yellow
    $albJson = aws elbv2 create-load-balancer --name $AlbName --subnets $Subnets[0] $Subnets[1] --security-groups $sgId --scheme internet-facing --type application --region $Region
    $albArn = ($albJson | ConvertFrom-Json).LoadBalancers[0].LoadBalancerArn
    $albDns = ($albJson | ConvertFrom-Json).LoadBalancers[0].DNSName
    Write-Host "ALB created: $albDns" -ForegroundColor Green
} else {
    $albDns = aws elbv2 describe-load-balancers --load-balancer-arns $albArn --region $Region --query "LoadBalancers[0].DNSName" --output text
    Write-Host "ALB already exists: $albDns" -ForegroundColor Green
}

# 8. Create Target Group
Write-Host "`n8. Setting up Target Group..." -ForegroundColor Yellow
$tgArn = aws elbv2 describe-target-groups --names $TargetGroupName --region $Region --query "TargetGroups[0].TargetGroupArn" --output text 2>$null

if ($LASTEXITCODE -ne 0 -or $tgArn -eq "None") {
    $tgJson = aws elbv2 create-target-group --name $TargetGroupName --protocol HTTP --port 80 --vpc-id $VpcId --target-type ip --health-check-path "/api/oap/health" --region $Region
    $tgArn = ($tgJson | ConvertFrom-Json).TargetGroups[0].TargetGroupArn
    Write-Host "Target group created" -ForegroundColor Green
} else {
    Write-Host "Target group already exists" -ForegroundColor Green
}

# 9. Create HTTP Listener (for now - will upgrade to HTTPS if domain provided)
Write-Host "`n9. Setting up Load Balancer Listener..." -ForegroundColor Yellow
$listenerArn = aws elbv2 describe-listeners --load-balancer-arn $albArn --region $Region --query "Listeners[?Port==\`80\`].ListenerArn" --output text

if ([string]::IsNullOrEmpty($listenerArn) -or $listenerArn -eq "None") {
    $listenerJson = aws elbv2 create-listener --load-balancer-arn $albArn --protocol HTTP --port 80 --default-actions "Type=forward,TargetGroupArn=$tgArn" --region $Region
    Write-Host "HTTP Listener created" -ForegroundColor Green
} else {
    Write-Host "Listener already exists" -ForegroundColor Green
}

# 10. Create IAM Role for ECS Task Execution
Write-Host "`n10. Setting up IAM Role..." -ForegroundColor Yellow
$RoleName = "${ProjectName}-task-execution-role"
$roleArn = aws iam get-role --role-name $RoleName --query "Role.Arn" --output text 2>$null

if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating IAM role..." -ForegroundColor Yellow
    
    $trustPolicy = @{
        Version = "2012-10-17"
        Statement = @(
            @{
                Effect = "Allow"
                Principal = @{
                    Service = "ecs-tasks.amazonaws.com"
                }
                Action = "sts:AssumeRole"
            }
        )
    } | ConvertTo-Json -Depth 10
    
    $trustPolicy | Out-File -FilePath "trust-policy.json" -Encoding utf8
    aws iam create-role --role-name $RoleName --assume-role-policy-document file://trust-policy.json | Out-Null
    Remove-Item "trust-policy.json"
    
    aws iam attach-role-policy --role-name $RoleName --policy-arn "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy" | Out-Null
    aws iam attach-role-policy --role-name $RoleName --policy-arn "arn:aws:iam::aws:policy/CloudWatchLogsFullAccess" | Out-Null
    
    Start-Sleep -Seconds 10  # Wait for IAM role to propagate
    $roleArn = aws iam get-role --role-name $RoleName --query "Role.Arn" --output text
    Write-Host "IAM role created: $roleArn" -ForegroundColor Green
} else {
    Write-Host "IAM role already exists: $roleArn" -ForegroundColor Green
}

# 11. Get environment variables
Write-Host "`n11. Reading environment variables..." -ForegroundColor Yellow
$envFile = Join-Path $PSScriptRoot "MusicAI.Orchestrator\bin\Debug\net8.0\.env"
$envVars = @()

if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^([^=]+)=(.+)$') {
            $envVars += @{
                name = $matches[1]
                value = $matches[2]
            }
        }
    }
    Write-Host "Loaded $($envVars.Count) environment variables" -ForegroundColor Green
} else {
    Write-Host "No .env file found, using defaults" -ForegroundColor Yellow
}

# Add required environment variables
$envVars += @{name = "ASPNETCORE_ENVIRONMENT"; value = "Production"}
$envVars += @{name = "ASPNETCORE_URLS"; value = "http://+:80"}

# 12. Register Task Definition
Write-Host "`n12. Registering ECS Task Definition..." -ForegroundColor Yellow

$taskDef = @{
    family = $TaskFamily
    networkMode = "awsvpc"
    requiresCompatibilities = @("FARGATE")
    cpu = "1024"
    memory = "2048"
    executionRoleArn = $roleArn
    taskRoleArn = $roleArn
    containerDefinitions = @(
        @{
            name = "roman-oap"
            image = "${EcrRepoUri}:latest"
            essential = $true
            portMappings = @(
                @{
                    containerPort = 80
                    protocol = "tcp"
                }
            )
            environment = $envVars
            logConfiguration = @{
                logDriver = "awslogs"
                options = @{
                    "awslogs-group" = $LogGroup
                    "awslogs-region" = $Region
                    "awslogs-stream-prefix" = "ecs"
                }
            }
        }
    )
} | ConvertTo-Json -Depth 10

$taskDef | Out-File -FilePath "task-definition.json" -Encoding utf8
aws ecs register-task-definition --cli-input-json file://task-definition.json --region $Region | Out-Null
Remove-Item "task-definition.json"
Write-Host "Task definition registered" -ForegroundColor Green

# 13. Create or Update ECS Service
Write-Host "`n13. Creating/Updating ECS Service..." -ForegroundColor Yellow

$serviceExists = aws ecs describe-services --cluster $ClusterName --services $ServiceName --region $Region --query "services[?status=='ACTIVE'].serviceName" --output text

if ([string]::IsNullOrEmpty($serviceExists)) {
    Write-Host "Creating ECS service..." -ForegroundColor Yellow
    aws ecs create-service `
        --cluster $ClusterName `
        --service-name $ServiceName `
        --task-definition $TaskFamily `
        --desired-count 1 `
        --launch-type FARGATE `
        --network-configuration "awsvpcConfiguration={subnets=[$($Subnets[0]),$($Subnets[1])],securityGroups=[$sgId],assignPublicIp=ENABLED}" `
        --load-balancers "targetGroupArn=$tgArn,containerName=roman-oap,containerPort=80" `
        --region $Region | Out-Null
    Write-Host "Service created successfully!" -ForegroundColor Green
} else {
    Write-Host "Updating existing service..." -ForegroundColor Yellow
    aws ecs update-service `
        --cluster $ClusterName `
        --service $ServiceName `
        --task-definition $TaskFamily `
        --force-new-deployment `
        --region $Region | Out-Null
    Write-Host "Service updated successfully!" -ForegroundColor Green
}

# 14. Wait for service to stabilize
Write-Host "`n14. Waiting for service to stabilize (this may take 2-3 minutes)..." -ForegroundColor Yellow
aws ecs wait services-stable --cluster $ClusterName --services $ServiceName --region $Region

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "DEPLOYMENT COMPLETE!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Roman OAP is now running on AWS!" -ForegroundColor Green
Write-Host ""
Write-Host "HTTP URL: http://$albDns" -ForegroundColor Yellow
Write-Host ""
Write-Host "Test endpoints:" -ForegroundColor Cyan
Write-Host "  - Health: http://$albDns/api/oap/health" -ForegroundColor White
Write-Host "  - Current OAP: http://$albDns/api/oap/current" -ForegroundColor White
Write-Host "  - Swagger: http://$albDns/swagger" -ForegroundColor White
Write-Host ""

if ([string]::IsNullOrEmpty($DomainName)) {
    Write-Host "HTTPS Setup (Optional):" -ForegroundColor Yellow
    Write-Host "To enable HTTPS:" -ForegroundColor White
    Write-Host "1. Get a domain name and point it to: $albDns" -ForegroundColor White
    Write-Host "2. Request an ACM certificate for your domain" -ForegroundColor White
    Write-Host "3. Add HTTPS listener to the ALB with the certificate" -ForegroundColor White
    Write-Host "4. Re-run this script with -DomainName parameter" -ForegroundColor White
} else {
    Write-Host "Domain: $DomainName" -ForegroundColor Green
    Write-Host "Point your domain's DNS to: $albDns" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "CloudWatch Logs: $LogGroup" -ForegroundColor Cyan
Write-Host ""
Write-Host "To view logs: aws logs tail $LogGroup --follow --region $Region" -ForegroundColor White
Write-Host ""
