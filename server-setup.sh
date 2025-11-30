#!/bin/bash

# MusicAI Platform - Full Stack Deployment Script
# Includes: Orchestrator, Tosin, PostgreSQL, Redis, nginx, SSL
# Supports: DigitalOcean, Linode, Vultr, Hetzner, AWS EC2, Azure VM, Google Compute

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

echo -e "${CYAN}========================================${NC}"
echo -e "${CYAN}MusicAI Platform - Full Stack Setup${NC}"
echo -e "${CYAN}========================================${NC}"
echo ""

# Check if running as root
if [ "$EUID" -ne 0 ]; then 
    echo -e "${RED}Please run as root or with sudo${NC}"
    exit 1
fi

# Get domain name
read -p "Enter your domain name (e.g., api.yourdomain.com): " DOMAIN_NAME
read -p "Enter your email for SSL certificate: " EMAIL

if [ -z "$DOMAIN_NAME" ] || [ -z "$EMAIL" ]; then
    echo -e "${RED}Domain name and email are required${NC}"
    exit 1
fi

echo ""
echo -e "${YELLOW}Domain: $DOMAIN_NAME${NC}"
echo -e "${YELLOW}Email: $EMAIL${NC}"
echo ""

# Update system
echo -e "${YELLOW}1. Updating system packages...${NC}"
apt-get update -qq
apt-get upgrade -y -qq

# Install Docker
echo -e "${YELLOW}2. Installing Docker...${NC}"
if ! command -v docker &> /dev/null; then
    curl -fsSL https://get.docker.com -o get-docker.sh
    sh get-docker.sh
    rm get-docker.sh
    systemctl enable docker
    systemctl start docker
    echo -e "${GREEN}Docker installed${NC}"
else
    echo -e "${GREEN}Docker already installed${NC}"
fi

# Install Docker Compose
echo -e "${YELLOW}3. Installing Docker Compose...${NC}"
if ! command -v docker-compose &> /dev/null; then
    curl -L "https://github.com/docker/compose/releases/latest/download/docker-compose-$(uname -s)-$(uname -m)" -o /usr/local/bin/docker-compose
    chmod +x /usr/local/bin/docker-compose
    echo -e "${GREEN}Docker Compose installed${NC}"
else
    echo -e "${GREEN}Docker Compose already installed${NC}"
fi

# Install nginx
echo -e "${YELLOW}4. Installing nginx...${NC}"
apt-get install -y nginx

# Create project directory
echo -e "${YELLOW}5. Setting up project directory...${NC}"
mkdir -p /opt/musicai
cd /opt/musicai
mkdir -p nginx certbot/conf certbot/www data logs backup

# Create nginx config
echo -e "${YELLOW}6. Configuring nginx...${NC}"
mkdir -p nginx

# Initial nginx config (before SSL)
cat > /etc/nginx/sites-available/musicai << EOF
server {
    listen 80;
    listen [::]:80;
    server_name $DOMAIN_NAME;
    
    location /.well-known/acme-challenge/ {
        root /var/www/certbot;
    }
    
    location / {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_buffering off;
        
        # CORS
        add_header Access-Control-Allow-Origin "*" always;
    }
}
EOF

# Enable site
ln -sf /etc/nginx/sites-available/musicai /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default

# Test nginx config
nginx -t

# Reload nginx
systemctl reload nginx

echo -e "${GREEN}Nginx configured${NC}"

# Install Certbot
echo -e "${YELLOW}7. Installing Certbot for SSL...${NC}"
apt-get install -y certbot python3-certbot-nginx

# Get SSL certificate
echo -e "${YELLOW}8. Obtaining SSL certificate...${NC}"
certbot --nginx -d $DOMAIN_NAME --non-interactive --agree-tos -m $EMAIL --redirect

if [ $? -eq 0 ]; then
    echo -e "${GREEN}SSL certificate obtained successfully!${NC}"
else
    echo -e "${RED}Failed to obtain SSL certificate. Check DNS configuration.${NC}"
    echo -e "${YELLOW}Make sure $DOMAIN_NAME points to this server's IP address.${NC}"
    exit 1
fi

# Update nginx config for production
cat > /etc/nginx/sites-available/musicai << EOF
upstream orchestrator {
    server localhost:5000;
    keepalive 32;
}

upstream tosin {
    server localhost:8001;
    keepalive 16;
}

server {
    listen 80;
    listen [::]:80;
    server_name $DOMAIN_NAME;
    
    location /.well-known/acme-challenge/ {
        root /var/www/certbot;
    }
    
    location / {
        return 301 https://\$host\$request_uri;
    }
}

server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name $DOMAIN_NAME;
    
    ssl_certificate /etc/letsencrypt/live/$DOMAIN_NAME/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN_NAME/privkey.pem;
    ssl_session_timeout 1d;
    ssl_session_cache shared:SSL:50m;
    ssl_session_tickets off;
    
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384;
    ssl_prefer_server_ciphers off;
    
    add_header Strict-Transport-Security "max-age=63072000" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-Content-Type-Options "nosniff" always;
    
    client_max_body_size 100M;
    proxy_read_timeout 300s;
    proxy_connect_timeout 300s;
    
    location / {
        proxy_pass http://orchestrator;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Connection "";
        proxy_buffering off;
    }
    
    location ~ ^/api/tosin/ {
        proxy_pass http://tosin;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_buffering off;
    }
    
    location ~ ^/api/music/(stream|hls)/ {
        proxy_pass http://orchestrator;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header Range \$http_range;
        proxy_set_header If-Range \$http_if_range;
        proxy_buffering off;
        proxy_cache off;
        
        add_header Access-Control-Allow-Origin "*" always;
        add_header Access-Control-Expose-Headers "Content-Length,Content-Range" always;
        add_header Cache-Control "public, max-age=3600" always;
    }
    
    location /api/oap/health {
        proxy_pass http://roman_oap;
        access_log off;
    }
}
EOF

systemctl reload nginx
echo -e "${GREEN}Nginx updated with SSL${NC}"

# Setup auto-renewal
echo -e "${YELLOW}9. Setting up SSL auto-renewal...${NC}"
systemctl enable certbot.timer
systemctl start certbot.timer

# Generate secure passwords
echo -e "${YELLOW}10. Generating secure passwords...${NC}"
POSTGRES_PASSWORD=$(openssl rand -base64 32)
REDIS_PASSWORD=$(openssl rand -base64 32)
JWT_SECRET=$(openssl rand -base64 48)
ADMIN_API_KEY=$(openssl rand -hex 16)

# Create environment file
echo -e "${YELLOW}11. Creating environment file...${NC}"
cat > .env << EOF
# PostgreSQL Database
POSTGRES_DB=musicai
POSTGRES_USER=musicai
POSTGRES_PASSWORD=$POSTGRES_PASSWORD

# Redis Cache
REDIS_PASSWORD=$REDIS_PASSWORD

# AWS S3 Configuration
AWS_ACCESS_KEY_ID=your_access_key_here
AWS_SECRET_ACCESS_KEY=your_secret_key_here
AWS_REGION=us-east-2
S3_BUCKET_NAME=tingoradiobucket
S3_ENDPOINT=your_s3_endpoint_here

# Security
JWT_SECRET=$JWT_SECRET
ADMIN_API_KEY=$ADMIN_API_KEY

# OpenAI (optional - for Tosin TTS)
OPENAI_API_KEY=

# Application
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:80
DOMAIN_NAME=$DOMAIN_NAME
EOF

chmod 600 .env

echo ""
echo -e "${CYAN}========================================${NC}"
echo -e "${GREEN}SERVER SETUP COMPLETE!${NC}"
echo -e "${CYAN}========================================${NC}"
echo ""
echo -e "${GREEN}Generated Credentials (SAVE THESE):${NC}"
echo -e "${CYAN}====================================${NC}"
echo -e "PostgreSQL Password: ${YELLOW}$POSTGRES_PASSWORD${NC}"
echo -e "Redis Password:      ${YELLOW}$REDIS_PASSWORD${NC}"
echo -e "JWT Secret:          ${YELLOW}$JWT_SECRET${NC}"
echo -e "Admin API Key:       ${YELLOW}$ADMIN_API_KEY${NC}"
echo -e "${CYAN}====================================${NC}"
echo ""
echo -e "${YELLOW}Next steps:${NC}"
echo ""
echo -e "1. Update S3 credentials in /opt/musicai/.env:"
echo -e "   ${CYAN}nano /opt/musicai/.env${NC}"
echo -e "   Update: AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, S3_ENDPOINT"
echo ""
echo -e "2. Clone your repository:"
echo -e "   ${CYAN}cd /opt/musicai${NC}"
echo -e "   ${CYAN}git clone https://github.com/yourusername/mcp-agent.git .${NC}"
echo ""
echo -e "3. Start all services with Docker Compose:"
echo -e "   ${CYAN}docker-compose -f docker-compose.production.yml up -d${NC}"
echo ""
echo -e "4. View logs:"
echo -e "   ${CYAN}docker-compose -f docker-compose.production.yml logs -f${NC}"
echo ""
echo -e "5. Test your deployment:"
echo -e "   ${CYAN}https://$DOMAIN_NAME/api/oap/health${NC}"
echo -e "   ${CYAN}https://$DOMAIN_NAME/api/oap/current${NC}"
echo -e "   ${CYAN}https://$DOMAIN_NAME/swagger${NC}"
echo ""
echo -e "${GREEN}Your MusicAI Platform will be available at: https://$DOMAIN_NAME${NC}"
echo ""
echo -e "${YELLOW}Services running:${NC}"
echo -e "  - PostgreSQL:    localhost:5432"
echo -e "  - Redis:         localhost:6379"
echo -e "  - Orchestrator:  localhost:5000"
echo -e "  - Tosin:         localhost:8001"
echo -e "  - nginx:         https://$DOMAIN_NAME"
echo ""
