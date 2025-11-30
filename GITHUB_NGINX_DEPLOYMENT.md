# Roman OAP - GitHub + nginx Deployment Guide

## Overview

Deploy Roman OAP using **GitHub Actions** + **nginx** on any VPS/cloud server. No AWS CLI needed!

## Supported Platforms

- ✅ **DigitalOcean** ($6-12/month droplet)
- ✅ **Linode** ($5-10/month)
- ✅ **Vultr** ($6-12/month)
- ✅ **Hetzner** (€4-8/month)
- ✅ **AWS EC2** (t2.small or t3.small)
- ✅ **Azure VM**
- ✅ **Google Compute Engine**
- ✅ **Any Ubuntu/Debian VPS**

---

## Quick Start (3 Steps)

### Step 1: Get a Server

**Option A: DigitalOcean (Recommended)**
```bash
# Create Ubuntu 22.04 droplet
# Min specs: 2GB RAM, 1 vCPU, 50GB SSD
# Cost: ~$12/month
```

**Option B: Hetzner (Cheapest)**
```bash
# Create Ubuntu 22.04 server
# Min specs: 2GB RAM, 1 vCPU
# Cost: ~€4.5/month (~$5)
```

### Step 2: Run Setup Script

SSH into your server and run:

```bash
# Download and run setup script
curl -fsSL https://raw.githubusercontent.com/yourusername/yourrepo/main/server-setup.sh -o setup.sh
chmod +x setup.sh
sudo ./setup.sh
```

**The script will:**
- ✅ Install Docker & Docker Compose
- ✅ Install nginx
- ✅ Install Certbot (Let's Encrypt)
- ✅ Get FREE SSL certificate
- ✅ Configure HTTPS with auto-renewal
- ✅ Set up firewall rules

### Step 3: Deploy Application

```bash
cd /opt/roman-oap

# Edit environment variables
nano .env

# Clone your repository (or upload files)
git clone https://github.com/yourusername/mcp-agent.git .

# Build Docker image
docker build -f Dockerfile.orchestrator -t roman-oap:latest .

# Start container
docker run -d \
  --name roman-oap \
  --restart unless-stopped \
  -p 5000:80 \
  --env-file .env \
  roman-oap:latest

# Check logs
docker logs -f roman-oap
```

**Done!** Your API is live at `https://yourdomain.com`

---

## GitHub Actions Auto-Deploy (Optional)

### Setup GitHub Secrets

Go to your GitHub repository → Settings → Secrets and Variables → Actions

Add these secrets:
```
SERVER_HOST = your.server.ip.address
SERVER_USER = root
SSH_PRIVATE_KEY = <your private SSH key>
```

### Generate SSH Key

On your **local machine**:
```bash
ssh-keygen -t ed25519 -C "github-actions"
# Save as: github_deploy_key
# Don't set a passphrase
```

Copy to your server:
```bash
ssh-copy-id -i github_deploy_key.pub root@your.server.ip
```

Copy private key to GitHub:
```bash
cat github_deploy_key
# Copy entire output to GitHub SECRET: SSH_PRIVATE_KEY
```

### Enable Auto-Deploy

Now, every time you push to `main` branch:
1. GitHub builds Docker image
2. Pushes to GitHub Container Registry
3. SSH to your server
4. Pulls latest image
5. Restarts container
6. Reloads nginx

**Zero-downtime deployments!**

---

## Manual Deployment Steps

### 1. Initial Server Setup

```bash
# SSH to your server
ssh root@your.server.ip

# Update system
apt update && apt upgrade -y

# Install Docker
curl -fsSL https://get.docker.com | sh

# Install Docker Compose
curl -L "https://github.com/docker/compose/releases/latest/download/docker-compose-$(uname -s)-$(uname -m)" -o /usr/local/bin/docker-compose
chmod +x /usr/local/bin/docker-compose

# Install nginx
apt install -y nginx

# Install Certbot
apt install -y certbot python3-certbot-nginx
```

### 2. Configure DNS

Point your domain to your server IP:
```
Type: A
Name: @ (or api, or subdomain)
Value: YOUR_SERVER_IP
TTL: 300
```

Wait 5-10 minutes for DNS propagation.

### 3. Get SSL Certificate

```bash
# Stop nginx temporarily
systemctl stop nginx

# Get certificate
certbot certonly --standalone -d yourdomain.com -d www.yourdomain.com

# Start nginx
systemctl start nginx
```

### 4. Configure nginx

```bash
# Copy nginx config
nano /etc/nginx/sites-available/roman-oap

# Paste the content from nginx/roman-oap.conf
# Update server_name and ssl_certificate paths

# Enable site
ln -s /etc/nginx/sites-available/roman-oap /etc/nginx/sites-enabled/
rm /etc/nginx/sites-enabled/default

# Test config
nginx -t

# Reload nginx
systemctl reload nginx
```

### 5. Deploy Application

```bash
# Create app directory
mkdir -p /opt/roman-oap
cd /opt/roman-oap

# Upload your code (via git or scp)
git clone https://github.com/yourusername/mcp-agent.git .

# Create .env file
nano .env
# Add your S3 credentials, JWT secret, etc.

# Build image
docker build -f Dockerfile.orchestrator -t roman-oap:latest .

# Run container
docker run -d \
  --name roman-oap \
  --restart unless-stopped \
  -p 5000:80 \
  --env-file .env \
  roman-oap:latest
```

### 6. Verify Deployment

```bash
# Check container
docker ps

# Check logs
docker logs -f roman-oap

# Test endpoints
curl https://yourdomain.com/api/oap/health
curl https://yourdomain.com/api/oap/current
```

---

## Using Docker Compose (Easier)

### 1. Create docker-compose.yml

Use the provided `docker-compose.production.yml`:

```bash
cd /opt/roman-oap
cp docker-compose.production.yml docker-compose.yml
```

### 2. Start Everything

```bash
# Start all services
docker-compose up -d

# View logs
docker-compose logs -f

# Restart
docker-compose restart

# Stop
docker-compose down
```

---

## SSL Auto-Renewal

Certbot automatically renews certificates. Verify:

```bash
# Check renewal timer
systemctl status certbot.timer

# Test renewal (dry run)
certbot renew --dry-run

# Manual renewal
certbot renew
nginx -s reload
```

---

## Monitoring & Logs

### Check Application Logs
```bash
docker logs -f roman-oap
```

### Check nginx Logs
```bash
tail -f /var/log/nginx/access.log
tail -f /var/log/nginx/error.log
```

### Check nginx Status
```bash
systemctl status nginx
```

### Restart Services
```bash
# Restart Roman OAP
docker restart roman-oap

# Restart nginx
systemctl restart nginx

# Reload nginx config
nginx -s reload
```

---

## Updating Your Application

### Manual Update
```bash
cd /opt/roman-oap

# Pull latest code
git pull

# Rebuild image
docker build -f Dockerfile.orchestrator -t roman-oap:latest .

# Stop old container
docker stop roman-oap
docker rm roman-oap

# Start new container
docker run -d \
  --name roman-oap \
  --restart unless-stopped \
  -p 5000:80 \
  --env-file .env \
  roman-oap:latest
```

### With GitHub Actions
```bash
# Just push to main branch
git push origin main

# GitHub Actions will automatically deploy
```

---

## Backup & Restore

### Backup
```bash
# Backup environment
cp /opt/roman-oap/.env ~/roman-oap-env-backup

# Backup nginx config
cp /etc/nginx/sites-available/roman-oap ~/roman-oap-nginx-backup

# Backup SSL certificates
tar -czf ~/ssl-backup.tar.gz /etc/letsencrypt/
```

### Restore
```bash
# Restore environment
cp ~/roman-oap-env-backup /opt/roman-oap/.env

# Restore nginx
cp ~/roman-oap-nginx-backup /etc/nginx/sites-available/roman-oap
nginx -s reload
```

---

## Troubleshooting

### Container won't start
```bash
docker logs roman-oap
# Check for missing environment variables
```

### nginx errors
```bash
nginx -t  # Test config
tail -f /var/log/nginx/error.log
```

### SSL certificate issues
```bash
certbot certificates  # Check status
certbot renew --force-renewal  # Force renewal
```

### Can't connect to server
```bash
# Check firewall
ufw status
ufw allow 80/tcp
ufw allow 443/tcp

# Check ports
netstat -tulpn | grep LISTEN
```

---

## Cost Breakdown

### DigitalOcean
- Droplet: $12/month (2GB RAM, 1 vCPU)
- Domain: $12/year
- SSL: FREE (Let's Encrypt)
- **Total**: ~$13/month

### Hetzner (Cheapest)
- Server: €4.5/month
- Domain: €10/year
- SSL: FREE
- **Total**: ~$6/month

### Free Tier Options
- **Oracle Cloud**: Always Free tier (1-2 ARM VMs)
- **Google Cloud**: $300 credit for 90 days
- **AWS**: 12 months free (t2.micro)

---

## Security Best Practices

1. **Firewall**
```bash
ufw enable
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
```

2. **Fail2Ban** (prevent brute force)
```bash
apt install -y fail2ban
systemctl enable fail2ban
```

3. **Auto Updates**
```bash
apt install -y unattended-upgrades
dpkg-reconfigure --priority=low unattended-upgrades
```

4. **Disable Root Login**
```bash
nano /etc/ssh/sshd_config
# Set: PermitRootLogin no
systemctl restart sshd
```

---

## Next Steps

1. ✅ Set up monitoring (Uptime Robot, Pingdom)
2. ✅ Configure backups (automated scripts)
3. ✅ Add CDN (Cloudflare - free tier)
4. ✅ Set up staging environment
5. ✅ Implement CI/CD with GitHub Actions

**Your Roman OAP is now running with HTTPS! 🎉**
