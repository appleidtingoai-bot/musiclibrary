-- Migration: add credits_expiry and IP/VPN tracking columns to users
BEGIN;

ALTER TABLE IF EXISTS users
  ADD COLUMN IF NOT EXISTS credits_expiry TIMESTAMP;

ALTER TABLE IF EXISTS users
  ADD COLUMN IF NOT EXISTS last_seen_ip TEXT;

ALTER TABLE IF EXISTS users
  ADD COLUMN IF NOT EXISTS last_seen_country TEXT;

ALTER TABLE IF EXISTS users
  ADD COLUMN IF NOT EXISTS last_ip_checked_at TIMESTAMP;

ALTER TABLE IF EXISTS users
  ADD COLUMN IF NOT EXISTS last_ip_is_vpn BOOLEAN DEFAULT false;

ALTER TABLE IF EXISTS users
  ADD COLUMN IF NOT EXISTS last_ip_is_proxy BOOLEAN DEFAULT false;

COMMIT;
