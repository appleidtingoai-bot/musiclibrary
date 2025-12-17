-- Migration: add first_name, last_name, phone to users table
ALTER TABLE users
ADD COLUMN IF NOT EXISTS first_name TEXT;
ALTER TABLE users
ADD COLUMN IF NOT EXISTS last_name TEXT;
ALTER TABLE users
ADD COLUMN IF NOT EXISTS phone TEXT;
