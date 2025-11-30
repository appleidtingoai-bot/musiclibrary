using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using MusicAI.Infrastructure.Services;

public class NewsPublisher
{
    private readonly LlmService _llm;
    private readonly CustomTtsService _tts;
    private readonly IServiceProvider _sp;
    private readonly NewsStore _store;

    public NewsPublisher(LlmService llm, CustomTtsService tts, IServiceProvider sp, NewsStore store)
    {
        _llm = llm;
        _tts = tts;
        _sp = sp;
        _store = store;
    }

    public async Task PublishNowAsync(string country = "Nigeria")
    {
        // Prompt: generate roughly 5 minutes of spoken news. We'll ask for short segments.
        var prompt = $"Produce a 5-minute radio news script for {country}. Include local headlines, a short sports score, weather summary, and a closing jingle line. Keep it in short sentences suitable for TTS.";

        var script = await _llm.GenerateReplyAsync(prompt, "news_hourly");
        if (string.IsNullOrWhiteSpace(script)) script = "(Tosin) No news available right now.";

        // Prepare a key using UTC hour
        var key = $"tosin/news_{DateTime.UtcNow:yyyyMMddHH}.mp3";

        // Prefer Polly if available so we can synthesize high-quality audio and mix with a jingle.
        var pollyObj = _sp.GetService(typeof(PollyService));
        var s3 = _sp.GetService(typeof(MusicAI.Infrastructure.Services.IS3Service)) as MusicAI.Infrastructure.Services.IS3Service;

        // prepare temp paths early so reflection and download branches can reuse them
        var tmpDir = Path.Combine(Path.GetTempPath(), "musicai_tosin");
        Directory.CreateDirectory(tmpDir);
        var voiceFile = Path.Combine(tmpDir, $"tosin_voice_{Guid.NewGuid()}.mp3");
        var mixedFile = Path.Combine(tmpDir, $"tosin_mixed_{Guid.NewGuid()}.mp3");

        if (pollyObj != null)
        {
            try
            {
                // Attempt to call SynthesizeToStreamAsync via reflection so older builds without the method won't fail compilation.
                MemoryStream? voiceStream = null;
                var synthMethod = pollyObj.GetType().GetMethod("SynthesizeToStreamAsync");
                if (synthMethod != null)
                {
                    var taskObj = synthMethod.Invoke(pollyObj, new object[] { script });
                    if (taskObj is Task task)
                    {
                        await task.ConfigureAwait(false);
                        var resultProp = task.GetType().GetProperty("Result");
                        voiceStream = resultProp?.GetValue(task) as MemoryStream;
                    }
                }

                // If the stream method is not available, fall back to SynthesizeToS3Url (if present) and download the file.
                if (voiceStream == null)
                {
                    var s3Method = pollyObj.GetType().GetMethod("SynthesizeToS3Url");
                    if (s3Method != null && s3 != null)
                    {
                        // call SynthesizeToS3Url and then download the presigned URL into a temp file
                        var presignedTaskObj = s3Method.Invoke(pollyObj, new object[] { script, key });
                        if (presignedTaskObj is Task<string> presTask)
                        {
                            var presigned = await presTask.ConfigureAwait(false);
                            // try to download
                            try
                            {
                                using var http = new System.Net.Http.HttpClient();
                                var bytes = await http.GetByteArrayAsync(presigned);
                                    await File.WriteAllBytesAsync(voiceFile, bytes);
                                    voiceStream = new MemoryStream(bytes);
                            }
                            catch { /* ignore download failure */ }
                        }
                    }
                }

                if (voiceStream == null)
                {
                    // couldn't synthesize via Polly; fall back to local TTS below
                    throw new InvalidOperationException("Polly synthesis unavailable");
                }
                voiceStream.Position = 0;

                await using (var fs = File.Create(voiceFile))
                {
                    await voiceStream.CopyToAsync(fs);
                }

                // Look for a jingle asset shipped with the persona
                var jinglePath = Path.Combine(AppContext.BaseDirectory, "assets", "jingle.mp3");

                bool mixed = false;
                // If ffmpeg exists and jingle is present, mix and trim to 5 minutes
                if (File.Exists(jinglePath) && IsFfmpegAvailable())
                {
                    try
                    {
                        // Mix voice with looping jingle at lower volume and trim to 300s (5 minutes)
                        var args = $"-y -i \"{voiceFile}\" -stream_loop -1 -i \"{jinglePath}\" -filter_complex \"[1]volume=0.25[a1];[0][a1]amix=inputs=2:duration=shortest:dropout_transition=2,volume=1\" -t 300 -c:a libmp3lame -b:a 192k \"{mixedFile}\"";
                        var p = Process.Start(new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = args,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        if (p != null)
                        {
                            var stderr = await p.StandardError.ReadToEndAsync();
                            var stdout = await p.StandardOutput.ReadToEndAsync();
                            p.WaitForExit();
                            if (p.ExitCode == 0 && File.Exists(mixedFile))
                            {
                                mixed = true;
                            }
                            else
                            {
                                // for debugging, write to temp log
                                try { File.WriteAllText(Path.Combine(tmpDir, "ffmpeg_error.txt"), stderr + "\n" + stdout); } catch { }
                            }
                        }
                    }
                    catch { /* mixing failed, fall back to raw voice */ }
                }

                var fileToUse = mixed && File.Exists(mixedFile) ? mixedFile : voiceFile;

                if (s3 != null)
                {
                    try
                    {
                        await using var fsu = File.OpenRead(fileToUse);
                        await s3.UploadFileAsync(key, fsu, "audio/mpeg");
                        var presigned = await s3.GetPresignedUrlAsync(key, TimeSpan.FromHours(6));
                        _store.AudioUrl = presigned;
                        _store.Key = key;
                        _store.PublishedAtUtc = DateTime.UtcNow;
                        return;
                    }
                    catch { /* fall through to local file result */ }
                }

                // Final fallback: local file path
                _store.AudioUrl = fileToUse;
                _store.Key = key;
                _store.PublishedAtUtc = DateTime.UtcNow;
                return;
            }
            catch { /* fall back to TTS stub below */ }
        }

        // Fallback: create local TTS file and upload if S3 available
        var localPath = await _tts.GenerateAudioUrl(script);
        if (s3 != null)
        {
            try
            {
                if (File.Exists(localPath))
                {
                    await using var fs = File.OpenRead(localPath);
                    await s3.UploadFileAsync(key, fs, "audio/mpeg");
                    var presigned = await s3.GetPresignedUrlAsync(key, TimeSpan.FromHours(6));
                    _store.AudioUrl = presigned;
                    _store.Key = key;
                    _store.PublishedAtUtc = DateTime.UtcNow;
                    return;
                }
            }
            catch { /* fall through to local path result */ }
        }

        // Final fallback: return local path
        _store.AudioUrl = localPath;
        _store.Key = key;
        _store.PublishedAtUtc = DateTime.UtcNow;
    }

    private bool IsFfmpegAvailable()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null) return false;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
