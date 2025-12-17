-- Migration: Add payment_methods table and save_token_requested / save_payment_consent columns
BEGIN;

ALTER TABLE IF EXISTS payments
  ADD COLUMN IF NOT EXISTS save_token_requested BOOLEAN DEFAULT false;

ALTER TABLE IF EXISTS users
  ADD COLUMN IF NOT EXISTS save_payment_consent BOOLEAN NOT NULL DEFAULT false;

CREATE TABLE IF NOT EXISTS payment_methods (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL,
  provider TEXT NOT NULL,
  token TEXT NOT NULL,
  last4 TEXT,
  created_at TIMESTAMP NOT NULL,
  updated_at TIMESTAMP
);

COMMIT;
