MusicAI Platform - Minimal C# Skeleton
======================================
This repository contains a minimal runnable skeleton for:
  - MusicAI.Common (shared models)
  - MusicAI.Orchestrator (Web API)
  - MusicAI.Personas.Tosin (persona microservice)

Purpose
-------
Starter code for the agentic AI platform fully in C#. It is intentionally minimal:
- Provides endpoints for streaming routing and persona chat.
- Uses in-memory stores for simplicity.
- Replace or extend LLM/TTS integrations as needed.

How to run (requires .NET 8 SDK)
--------------------------------
1. Build solution:
   dotnet build MusicAI.sln

2. Run orchestrator:
   cd MusicAI.Orchestrator
   dotnet run

3. Run persona (Tosin):
   cd ../MusicAI.Personas.Tosin
   dotnet run

Or use docker-compose:
   docker-compose up --build

