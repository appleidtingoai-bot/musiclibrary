Extending the skeleton
----------------------
- Add additional persona projects under MusicAI.Personas.<PersonaName>
- Implement real LLM service in LlmService (Azure OpenAI, local model)
- Implement custom TTS in CustomTtsService (ONNX runtime or cloud TTS)
- Wire the orchestrator to load persona configs from DB and to decide active persona windows
