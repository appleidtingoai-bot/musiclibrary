# Roman OAP - AWS Deployment Guide

## Prerequisites

1. **AWS Account** with appropriate permissions
2. **AWS CLI** installed and configured (`aws configure`)
3. **Docker Desktop** running
4. **PowerShell** 5.1 or later

## Quick Deploy (HTTP)

Run the automated deployment script:

```powershell
.\deploy-to-aws.ps1
```

This will:
- Build and push Docker image to ECR
- Create ECS Fargate cluster and service
- Set up Application Load Balancer
- Configure CloudWatch logging
- Deploy Roman OAP with 1 task (auto-scaling ready)

**Result**: HTTP endpoint available in ~5 minutes

## HTTPS Setup (with Domain)

### Option 1: Using AWS Certificate Manager (ACM)

1. **Request Certificate in ACM**:
   ```powershell
   aws acm request-certificate `
     --domain-name yourdomain.com `
     --validation-method DNS `
     --region us-east-2
   ```

2. **Validate Domain** (add DNS records provided by ACM)

3. **Get Certificate ARN**:
   ```powershell
   aws acm list-certificates --region us-east-2
   ```

4. **Add HTTPS Listener to ALB**:
   ```powershell
   $albArn = aws elbv2 describe-load-balancers --names roman-oap-alb --query "LoadBalancers[0].LoadBalancerArn" --output text --region us-east-2
   $tgArn = aws elbv2 describe-target-groups --names roman-oap-tg --query "TargetGroups[0].TargetGroupArn" --output text --region us-east-2
   $certArn = "arn:aws:acm:us-east-2:YOUR_ACCOUNT:certificate/CERT_ID"
   
   aws elbv2 create-listener `
     --load-balancer-arn $albArn `
     --protocol HTTPS `
     --port 443 `
     --certificates CertificateArn=$certArn `
     --default-actions "Type=forward,TargetGroupArn=$tgArn" `
     --region us-east-2
   ```

5. **Update DNS** (point your domain to ALB DNS)

### Option 2: Using Route 53 + ACM (Automated)

```powershell
# Create hosted zone (if you manage DNS with Route 53)
aws route53 create-hosted-zone --name yourdomain.com --caller-reference $(Get-Date -Format "yyyyMMddHHmmss")

# Request certificate
$certArn = (aws acm request-certificate --domain-name yourdomain.com --validation-method DNS --region us-east-2 --query CertificateArn --output text)

# Get validation records
aws acm describe-certificate --certificate-arn $certArn --region us-east-2

# Add DNS records (use Route 53 console or CLI)
# Wait for validation (can take 5-30 minutes)

# Add HTTPS listener (same as Option 1 step 4)
```

## Environment Variables

The deployment script reads from `.env` file. Required variables:

```env
# AWS S3
AWS_ACCESS_KEY_ID=your_access_key
AWS_SECRET_ACCESS_KEY=your_secret_key
AWS_REGION=us-east-2
S3_BUCKET_NAME=tingoradiobucket
S3_ENDPOINT=tingoaccesspoint-XXXXX.s3-accesspoint.us-east-2.amazonaws.com

# Database (optional - will fallback to S3)
DATABASE_URL=your_postgres_connection_string

# Security
JWT_SECRET=your_jwt_secret_key
ADMIN_API_KEY=your_admin_api_key

# OpenAI (for Tosin TTS - optional)
OPENAI_API_KEY=your_openai_key
```

## Post-Deployment

### Test Endpoints

```powershell
$baseUrl = "http://YOUR_ALB_DNS"

# Health check
Invoke-RestMethod "$baseUrl/api/oap/health"

# Current OAP
Invoke-RestMethod "$baseUrl/api/oap/current"

# Chat with Roman
$body = '{"userId":"test123","message":"play music"}'
Invoke-RestMethod "$baseUrl/api/oap/chat" -Method Post -Body $body -ContentType "application/json"
```

### View Logs

```powershell
aws logs tail /ecs/roman-oap --follow --region us-east-2
```

### Scale Service

```powershell
# Increase to 2 tasks
aws ecs update-service --cluster roman-oap-cluster --service roman-oap-service --desired-count 2 --region us-east-2

# Enable auto-scaling
aws application-autoscaling register-scalable-target `
  --service-namespace ecs `
  --resource-id service/roman-oap-cluster/roman-oap-service `
  --scalable-dimension ecs:service:DesiredCount `
  --min-capacity 1 `
  --max-capacity 4 `
  --region us-east-2
```

## Cost Estimate

**Fargate (1 task, 1 vCPU, 2GB RAM)**:
- ~$30-40/month

**Application Load Balancer**:
- ~$20-25/month

**Data Transfer**:
- Variable based on streaming usage

**Total**: ~$50-70/month for basic setup

## Troubleshooting

### Service won't start
```powershell
# Check service events
aws ecs describe-services --cluster roman-oap-cluster --services roman-oap-service --region us-east-2 --query "services[0].events[0:5]"

# Check task status
aws ecs list-tasks --cluster roman-oap-cluster --service-name roman-oap-service --region us-east-2
```

### Container crashes
```powershell
# Get container logs
aws logs tail /ecs/roman-oap --follow --region us-east-2
```

### Can't connect to ALB
```powershell
# Check security group rules
aws ec2 describe-security-groups --filters "Name=group-name,Values=roman-oap-sg" --region us-east-2

# Check target health
aws elbv2 describe-target-health --target-group-arn YOUR_TG_ARN --region us-east-2
```

## Cleanup

Remove all resources:

```powershell
# Delete service
aws ecs update-service --cluster roman-oap-cluster --service roman-oap-service --desired-count 0 --region us-east-2
aws ecs delete-service --cluster roman-oap-cluster --service roman-oap-service --region us-east-2

# Delete cluster
aws ecs delete-cluster --cluster roman-oap-cluster --region us-east-2

# Delete ALB and target group
$albArn = aws elbv2 describe-load-balancers --names roman-oap-alb --query "LoadBalancers[0].LoadBalancerArn" --output text --region us-east-2
aws elbv2 delete-load-balancer --load-balancer-arn $albArn --region us-east-2

Start-Sleep -Seconds 30

$tgArn = aws elbv2 describe-target-groups --names roman-oap-tg --query "TargetGroups[0].TargetGroupArn" --output text --region us-east-2
aws elbv2 delete-target-group --target-group-arn $tgArn --region us-east-2

# Delete security group
$sgId = aws ec2 describe-security-groups --filters "Name=group-name,Values=roman-oap-sg" --query "SecurityGroups[0].GroupId" --output text --region us-east-2
aws ec2 delete-security-group --group-id $sgId --region us-east-2

# Delete ECR repository
aws ecr delete-repository --repository-name roman-oap-repo --force --region us-east-2

# Delete IAM role
aws iam detach-role-policy --role-name roman-oap-task-execution-role --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy
aws iam detach-role-policy --role-name roman-oap-task-execution-role --policy-arn arn:aws:iam::aws:policy/CloudWatchLogsFullAccess
aws iam delete-role --role-name roman-oap-task-execution-role

# Delete CloudWatch logs
aws logs delete-log-group --log-group-name /ecs/roman-oap --region us-east-2
```

## Security Best Practices

1. **Use HTTPS** - Always use SSL/TLS in production
2. **Restrict Admin Access** - Use IP whitelisting for admin endpoints
3. **Rotate Secrets** - Store credentials in AWS Secrets Manager
4. **Enable CloudTrail** - Audit all AWS API calls
5. **Use VPC Endpoints** - For S3 access without internet gateway
6. **Enable WAF** - Protect ALB from common web attacks

## Next Steps

1. **Custom Domain** - Set up Route 53 and ACM certificate
2. **CI/CD Pipeline** - Automate deployments with GitHub Actions or CodePipeline
3. **Monitoring** - Set up CloudWatch dashboards and alarms
4. **Backup** - Configure automated database backups
5. **CDN** - Add CloudFront for faster global content delivery
