-- Migration: create refresh_tokens table
-- Run by startup migration runner when RUN_DB_MIGRATIONS_ON_STARTUP=true
CREATE TABLE IF NOT EXISTS refresh_tokens (
    token TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    expires_at TIMESTAMP NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    revoked BOOLEAN NOT NULL DEFAULT false
);
