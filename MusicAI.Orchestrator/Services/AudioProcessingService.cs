using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MusicAI.Infrastructure.Services;

namespace MusicAI.Orchestrator.Services
{
    /// <summary>
    /// Converts uploaded MP3s to HLS segments (.ts chunks) for Spotify-like chunk-based streaming
    /// Requires FFmpeg installed on the server
    /// </summary>
    public interface IAudioProcessingService
    {
        Task<HlsProcessingResult> ConvertToHlsAsync(string sourceS3Key, string targetFolder);
        Task<List<QualityVersion>> GenerateMultipleQualitiesAsync(string sourceS3Key);
    }

    public class AudioProcessingService : IAudioProcessingService
    {
        private readonly IS3Service _s3Service;
        private readonly ILogger<AudioProcessingService> _logger;
        private readonly string _tempPath;

        public AudioProcessingService(IS3Service s3Service, ILogger<AudioProcessingService> logger)
        {
            _s3Service = s3Service;
            _logger = logger;
            _tempPath = Path.Combine(Path.GetTempPath(), "musicai-processing");
            
            if (!Directory.Exists(_tempPath))
                Directory.CreateDirectory(_tempPath);
        }

        public async Task<HlsProcessingResult> ConvertToHlsAsync(string sourceS3Key, string targetFolder)
        {
            var processId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var localInput = Path.Combine(_tempPath, $"{processId}_input.mp3");
            var localOutput = Path.Combine(_tempPath, $"{processId}_output.m3u8");
            var segmentPattern = Path.Combine(_tempPath, $"{processId}_segment_%03d.ts");

            try
            {
                _logger.LogInformation("Starting HLS conversion for {S3Key}", sourceS3Key);

                // 1. Download from S3
                _logger.LogInformation("Downloading {S3Key} to {LocalPath}", sourceS3Key, localInput);
                var downloadSuccess = await _s3Service.DownloadFileAsync(sourceS3Key, localInput);
                
                if (!downloadSuccess || !File.Exists(localInput))
                {
                    throw new Exception($"Failed to download {sourceS3Key} from S3");
                }

                // 2. Convert to HLS using FFmpeg
                // Segment length: 10 seconds (Spotify uses 5-10s)
                // Codec: AAC (web-compatible)
                var ffmpegArgs = $"-i \"{localInput}\" -codec:a aac -b:a 192k -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{segmentPattern}\" \"{localOutput}\"";
                
                _logger.LogInformation("Running FFmpeg: ffmpeg {Args}", ffmpegArgs);
                
                var exitCode = await RunFfmpegAsync(ffmpegArgs);
                
                if (exitCode != 0 || !File.Exists(localOutput))
                {
                    throw new Exception($"FFmpeg conversion failed with exit code {exitCode}");
                }

                // 3. Upload manifest and segments to S3
                var manifestS3Key = $"{targetFolder}/playlist.m3u8";
                using (var manifestStream = File.OpenRead(localOutput))
                {
                    await _s3Service.UploadFileAsync(manifestS3Key, manifestStream, "application/vnd.apple.mpegurl");
                }
                
                _logger.LogInformation("Uploaded HLS manifest to {ManifestKey}", manifestS3Key);

                // Upload all .ts segments
                var segmentFiles = Directory.GetFiles(_tempPath, $"{processId}_segment_*.ts");
                var uploadedSegments = new List<string>();

                foreach (var segmentFile in segmentFiles)
                {
                    var segmentName = Path.GetFileName(segmentFile);
                    var segmentS3Key = $"{targetFolder}/{segmentName}";
                    
                    using (var segmentStream = File.OpenRead(segmentFile))
                    {
                        await _s3Service.UploadFileAsync(segmentS3Key, segmentStream, "video/MP2T");
                    }
                    uploadedSegments.Add(segmentS3Key);
                }

                _logger.LogInformation("Uploaded {Count} HLS segments to S3", uploadedSegments.Count);

                return new HlsProcessingResult
                {
                    Success = true,
                    ManifestS3Key = manifestS3Key,
                    SegmentKeys = uploadedSegments,
                    SegmentCount = uploadedSegments.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HLS conversion failed for {S3Key}", sourceS3Key);
                
                return new HlsProcessingResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
            finally
            {
                // Cleanup temp files
                CleanupTempFiles(processId);
            }
        }

        public async Task<List<QualityVersion>> GenerateMultipleQualitiesAsync(string sourceS3Key)
        {
            var processId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var localInput = Path.Combine(_tempPath, $"{processId}_input.mp3");
            var results = new List<QualityVersion>();

            try
            {
                _logger.LogInformation("Generating multiple quality versions for {S3Key}", sourceS3Key);

                // Download original
                await _s3Service.DownloadFileAsync(sourceS3Key, localInput);

                // Quality profiles (Spotify-like)
                var profiles = new[]
                {
                    new { Name = "high", Bitrate = "320k", Label = "High (320kbps)" },
                    new { Name = "medium", Bitrate = "192k", Label = "Medium (192kbps)" },
                    new { Name = "low", Bitrate = "128k", Label = "Low (128kbps)" }
                };

                foreach (var profile in profiles)
                {
                    var outputFile = Path.Combine(_tempPath, $"{processId}_{profile.Name}.mp3");
                    
                    // Convert using FFmpeg
                    var ffmpegArgs = $"-i \"{localInput}\" -codec:a libmp3lame -b:a {profile.Bitrate} \"{outputFile}\"";
                    var exitCode = await RunFfmpegAsync(ffmpegArgs);
                    
                    if (exitCode == 0 && File.Exists(outputFile))
                    {
                        // Upload to S3 with quality suffix
                        var targetS3Key = sourceS3Key.Replace(".mp3", $"_{profile.Name}.mp3");
                        using (var fileStream = File.OpenRead(outputFile))
                        {
                            await _s3Service.UploadFileAsync(targetS3Key, fileStream, "audio/mpeg");
                        }
                        
                        results.Add(new QualityVersion
                        {
                            Quality = profile.Name,
                            Bitrate = int.Parse(profile.Bitrate.TrimEnd('k')),
                            Label = profile.Label,
                            S3Key = targetS3Key
                        });

                        _logger.LogInformation("Generated {Quality} quality: {S3Key}", profile.Name, targetS3Key);
                        
                        File.Delete(outputFile);
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate quality versions for {S3Key}", sourceS3Key);
                return results;
            }
            finally
            {
                CleanupTempFiles(processId);
            }
        }

        private async Task<int> RunFfmpegAsync(string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogError("FFmpeg error: {Error}", error);
            }

            return process.ExitCode;
        }

        private void CleanupTempFiles(string processId)
        {
            try
            {
                var files = Directory.GetFiles(_tempPath, $"{processId}*");
                foreach (var file in files)
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup temp files for process {ProcessId}", processId);
            }
        }
    }

    public class HlsProcessingResult
    {
        public bool Success { get; set; }
        public string ManifestS3Key { get; set; } = string.Empty;
        public List<string> SegmentKeys { get; set; } = new();
        public int SegmentCount { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Extension methods for IS3Service to provide a DownloadFileAsync helper when concrete implementations expose stream-returning download APIs.
    /// This implementation uses reflection to find a suitable method that returns Stream or Task&lt;Stream&gt; and accepts a single string key argument.
    /// </summary>
    public static class S3ServiceExtensions
    {
        public static async Task<bool> DownloadFileAsync(this IS3Service s3Service, string key, string localPath)
        {
            if (s3Service == null) throw new ArgumentNullException(nameof(s3Service));
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (localPath == null) throw new ArgumentNullException(nameof(localPath));

            var type = s3Service.GetType();
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public);

            // Prefer methods that return Stream or Task<Stream> and take a single string parameter
            MethodInfo? candidate = methods.FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                if (ps.Length != 1) return false;
                if (ps[0].ParameterType != typeof(string)) return false;
                var rt = m.ReturnType;
                if (rt == typeof(Stream)) return true;
                if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>) && rt.GetGenericArguments()[0] == typeof(Stream)) return true;
                return false;
            });

            // Fallback: look for commonly named methods
            if (candidate == null)
            {
                candidate = methods.FirstOrDefault(m =>
                    (m.Name.Equals("GetObjectAsync", StringComparison.OrdinalIgnoreCase)
                     || m.Name.IndexOf("Download", StringComparison.OrdinalIgnoreCase) >= 0
                     || m.Name.IndexOf("GetFile", StringComparison.OrdinalIgnoreCase) >= 0)
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(string));
            }

            if (candidate == null)
                throw new NotSupportedException("IS3Service does not expose a compatible download method (no method returning Stream or Task<Stream> with a single string parameter was found).");

            object? invokeResult = candidate.Invoke(s3Service, new object[] { key });
            Stream? stream = null;

            if (invokeResult is Task task)
            {
                await task.ConfigureAwait(false);
                // retrieve Result property for Task<T>
                var resultProp = invokeResult.GetType().GetProperty("Result");
                if (resultProp != null)
                {
                    stream = resultProp.GetValue(invokeResult) as Stream;
                }
            }
            else
            {
                stream = invokeResult as Stream;
            }

            if (stream == null)
                throw new NotSupportedException("Download method did not return a Stream.");

            try
            {
                using (var fs = File.Create(localPath))
                {
                    await stream.CopyToAsync(fs).ConfigureAwait(false);
                }
            }
            finally
            {
                try { stream.Dispose(); } catch { }
            }

            return File.Exists(localPath);
        }
    }
}
