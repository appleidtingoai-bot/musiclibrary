Place a background jingle audio file named `jingle.mp3` in this folder.

- Recommended: ~30s MP3 loopable jingle, bitrate 128-192 kbps.
- The NewsPublisher will attempt to find `assets/jingle.mp3` under the persona folder and use `ffmpeg` (if available in PATH) to loop the jingle, mix it under the TTS audio, and trim the final output to 5 minutes (300 seconds).

If `ffmpeg` or `jingle.mp3` is missing, the publisher will upload the raw synthesized voice audio without background music.