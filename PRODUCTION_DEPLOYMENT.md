# MusicAI Platform - Production Deployment Guide

## 🚀 Full Stack Architecture

Your production deployment includes:

- ✅ **PostgreSQL 15** - Database for tracks, users, admins
- ✅ **Redis 7** - Caching and session management
- ✅ **Orchestrator** - Main API (all OAPs + admin endpoints)
- ✅ **Tosin** - TTS/News service
- ✅ **nginx** - Reverse proxy with HTTPS
- ✅ **Let's Encrypt** - Free SSL certificates (auto-renewal)
- ✅ **AWS S3** - Music storage and streaming

---

## 📋 Prerequisites

1. **VPS/Cloud Server** (Ubuntu 22.04 recommended)
   - Min: 2GB RAM, 2 vCPU, 50GB SSD
   - Recommended: 4GB RAM, 2 vCPU, 100GB SSD
   - Cost: $10-20/month (DigitalOcean, Hetzner, Linode, Vultr)

2. **Domain Name** 
   - Point A record to your server IP
   - Example: `api.yourdomain.com` → `123.456.789.012`

3. **AWS S3 Bucket** (existing)
   - Access Key ID
   - Secret Access Key
   - Bucket name: `tingoradiobucket`
   - Endpoint/Region

---

## ⚡ Quick Deploy (Automated)

### Step 1: Prepare Your Server

```bash
# SSH to your server
ssh root@your.server.ip

# Download setup script
curl -fsSL https://raw.githubusercontent.com/yourusername/mcp-agent/main/server-setup.sh -o setup.sh
chmod +x setup.sh

# Run setup (will prompt for domain and email)
sudo ./setup.sh
```

**The script installs:**
- Docker & Docker Compose
- nginx with SSL
- Certbot (Let's Encrypt)
- Firewall rules
- Auto-generated secure passwords

### Step 2: Configure Environment

```bash
cd /opt/musicai

# Edit .env file
nano .env
```

**Update these values:**
```env
# AWS S3 (REQUIRED)
AWS_ACCESS_KEY_ID=AKIA...
AWS_SECRET_ACCESS_KEY=your_secret_key
S3_ENDPOINT=tingoaccesspoint-xxxxx.s3-accesspoint.us-east-2.amazonaws.com

# OpenAI (OPTIONAL - for Tosin TTS)
OPENAI_API_KEY=sk-...
```

### Step 3: Deploy Application

```bash
# Clone repository
git clone https://github.com/yourusername/mcp-agent.git .

# Start all services
docker-compose -f docker-compose.production.yml up -d

# View logs
docker-compose -f docker-compose.production.yml logs -f
```

### Step 4: Test Deployment

```bash
# Check services
docker-compose -f docker-compose.production.yml ps

# Test endpoints
curl https://yourdomain.com/api/oap/health
curl https://yourdomain.com/api/oap/current
```

**🎉 Done! Your API is live at `https://yourdomain.com`**

---

## 📡 API Endpoints

### Public Endpoints (No Auth)
```
GET  https://yourdomain.com/api/oap/current           # Current OAP
POST https://yourdomain.com/api/oap/chat              # Chat with OAP
GET  https://yourdomain.com/api/oap/next-tracks/{id}  # Auto-play queue
GET  https://yourdomain.com/api/music/stream/{key}    # Stream music (MP3)
GET  https://yourdomain.com/api/music/hls/{key}       # Stream music (HLS)
GET  https://yourdomain.com/swagger                   # API docs
```

### Admin Endpoints (Auth Required)
```
POST https://yourdomain.com/api/admin/login           # Get JWT token
POST https://yourdomain.com/api/admin/bulk-upload     # Upload music
GET  https://yourdomain.com/api/admin/library         # List tracks
```

### Tosin TTS (Optional)
```
POST https://yourdomain.com/api/tosin/speak           # Generate TTS
GET  https://yourdomain.com/api/tosin/news            # Latest news
```

---

## 🔧 Manual Deployment Steps

### 1. Server Setup

```bash
# Update system
apt update && apt upgrade -y

# Install Docker
curl -fsSL https://get.docker.com | sh
systemctl enable docker
systemctl start docker

# Install Docker Compose
curl -L "https://github.com/docker/compose/releases/latest/download/docker-compose-$(uname -s)-$(uname -m)" -o /usr/local/bin/docker-compose
chmod +x /usr/local/bin/docker-compose

# Install nginx
apt install -y nginx

# Install Certbot
apt install -y certbot python3-certbot-nginx
```

### 2. Configure DNS

Point your domain to server:
```
Type: A
Name: @ (or api)
Value: YOUR_SERVER_IP
TTL: 300
```

Wait 5-10 minutes for propagation.

### 3. Get SSL Certificate

```bash
# Stop nginx
systemctl stop nginx

# Get certificate
certbot certonly --standalone \
  -d yourdomain.com \
  -d www.yourdomain.com \
  --agree-tos \
  --non-interactive \
  -m your@email.com

# Start nginx
systemctl start nginx
```

### 4. Deploy Application

```bash
# Create directory
mkdir -p /opt/musicai
cd /opt/musicai

# Clone code
git clone https://github.com/yourusername/mcp-agent.git .

# Create .env from example
cp .env.production.example .env
nano .env  # Edit with your values

# Start services
docker-compose -f docker-compose.production.yml up -d
```

### 5. Configure nginx

```bash
# Copy nginx config
cp nginx/musicai.conf /etc/nginx/sites-available/musicai

# Update domain in config
sed -i 's/yourdomain.com/your-actual-domain.com/g' /etc/nginx/sites-available/musicai

# Enable site
ln -s /etc/nginx/sites-available/musicai /etc/nginx/sites-enabled/
rm /etc/nginx/sites-enabled/default

# Test config
nginx -t

# Reload
systemctl reload nginx
```

---

## 🗄️ Database Management

### Connect to PostgreSQL

```bash
# Via Docker
docker exec -it musicai-postgres psql -U musicai -d musicai

# View tables
\dt

# Query tracks
SELECT id, title, artist, genre FROM music_tracks LIMIT 10;

# Exit
\q
```

### Backup Database

```bash
# Create backup
docker exec musicai-postgres pg_dump -U musicai musicai > backup/musicai-$(date +%Y%m%d).sql

# Restore backup
cat backup/musicai-20250130.sql | docker exec -i musicai-postgres psql -U musicai musicai
```

### Connect to Redis

```bash
# Via Docker
docker exec -it musicai-redis redis-cli -a YOUR_REDIS_PASSWORD

# Check keys
KEYS *

# Get value
GET key_name

# Exit
exit
```

---

## 📊 Monitoring & Logs

### View All Logs

```bash
docker-compose -f docker-compose.production.yml logs -f
```

### View Specific Service

```bash
# Orchestrator
docker logs -f musicai-orchestrator

# PostgreSQL
docker logs -f musicai-postgres

# Redis
docker logs -f musicai-redis

# nginx
docker logs -f musicai-nginx
```

### Check Service Status

```bash
docker-compose -f docker-compose.production.yml ps
```

### Resource Usage

```bash
docker stats
```

---

## 🔄 Updates & Maintenance

### Update Application

```bash
cd /opt/musicai

# Pull latest code
git pull origin main

# Rebuild and restart
docker-compose -f docker-compose.production.yml up -d --build

# View logs
docker-compose -f docker-compose.production.yml logs -f orchestrator
```

### Restart Services

```bash
# Restart all
docker-compose -f docker-compose.production.yml restart

# Restart specific service
docker-compose -f docker-compose.production.yml restart orchestrator

# Reload nginx
docker exec musicai-nginx nginx -s reload
```

### Scale Services

```bash
# Run 2 orchestrator instances
docker-compose -f docker-compose.production.yml up -d --scale orchestrator=2
```

---

## 🔒 Security Best Practices

### 1. Firewall

```bash
# Enable firewall
ufw enable

# Allow SSH, HTTP, HTTPS
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp

# Check status
ufw status
```

### 2. Fail2Ban (Prevent Brute Force)

```bash
apt install -y fail2ban
systemctl enable fail2ban
systemctl start fail2ban
```

### 3. Auto Updates

```bash
apt install -y unattended-upgrades
dpkg-reconfigure --priority=low unattended-upgrades
```

### 4. Secure Environment Variables

```bash
# Protect .env file
chmod 600 /opt/musicai/.env
chown root:root /opt/musicai/.env
```

### 5. Change Default Passwords

Edit `.env` and update:
- `POSTGRES_PASSWORD`
- `REDIS_PASSWORD`
- `JWT_SECRET`
- `ADMIN_API_KEY`

Then restart:
```bash
docker-compose -f docker-compose.production.yml restart
```

---

## 🐛 Troubleshooting

### Services Won't Start

```bash
# Check logs
docker-compose -f docker-compose.production.yml logs

# Check specific service
docker logs musicai-orchestrator

# Restart from scratch
docker-compose -f docker-compose.production.yml down
docker-compose -f docker-compose.production.yml up -d
```

### Can't Connect to Database

```bash
# Check PostgreSQL is running
docker exec musicai-postgres pg_isready -U musicai

# Check connection string in .env
cat .env | grep POSTGRES
```

### SSL Certificate Issues

```bash
# Check certificate status
certbot certificates

# Renew manually
certbot renew

# Test renewal
certbot renew --dry-run
```

### nginx Errors

```bash
# Test config
nginx -t

# View error log
tail -f /var/log/nginx/error.log

# Restart nginx
systemctl restart nginx
```

### High Memory Usage

```bash
# Check usage
docker stats

# Restart specific service
docker-compose -f docker-compose.production.yml restart orchestrator

# Prune unused data
docker system prune -a
```

---

## 💰 Cost Breakdown

### Hetzner (Cheapest)
- Server: €8/month (4GB RAM, 2 vCPU)
- Domain: €10/year
- SSL: FREE (Let's Encrypt)
- **Total: ~$10/month**

### DigitalOcean
- Droplet: $18/month (4GB RAM, 2 vCPU)
- Domain: $12/year
- SSL: FREE
- **Total: ~$19/month**

### AWS S3
- Storage: ~$0.023/GB/month
- Requests: ~$0.0004/1000 requests
- Data Transfer: ~$0.09/GB (first 10TB)
- **Estimate: $5-20/month depending on usage**

---

## 🚀 Next Steps

1. ✅ **Set up monitoring** - UptimeRobot, Pingdom
2. ✅ **Configure backups** - Automated DB dumps
3. ✅ **Add CDN** - Cloudflare (free tier)
4. ✅ **Enable auto-deploy** - GitHub Actions
5. ✅ **Set up staging** - Separate environment for testing

---

## 📞 Support

**Check logs first:**
```bash
docker-compose -f docker-compose.production.yml logs -f
```

**Common issues:**
- Database connection: Check `DATABASE_URL` in `.env`
- S3 errors: Verify AWS credentials
- SSL issues: Run `certbot renew`
- Port conflicts: Change ports in `docker-compose.production.yml`

**Your MusicAI Platform is now live! 🎉**
