using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3.Transfer;
using Amazon.S3;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Polly;

public class UploadService : BackgroundService
{
    private readonly ILogger<UploadService> _logger;
    private readonly IHostEnvironment _env;

    public UploadService(ILogger<UploadService> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UploadService starting (production-ready template)");

        // Read config from environment
        var conn = Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__DEFAULT");
        var accessKey = Environment.GetEnvironmentVariable("AWS__AccessKey");
        var secretKey = Environment.GetEnvironmentVariable("AWS__SecretKey");
        var region = Environment.GetEnvironmentVariable("AWS__Region") ?? "us-east-1";
        var bucket = Environment.GetEnvironmentVariable("AWS__S3Bucket");
        var endpoint = Environment.GetEnvironmentVariable("AWS__S3Endpoint");

        if (string.IsNullOrEmpty(conn))
        {
            _logger.LogError("CONNECTIONSTRINGS__DEFAULT not set. Exiting.");
            return;
        }

        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
        {
            _logger.LogWarning("S3 config missing. Uploads will be skipped.");
        }

        // ensure DB table exists (simple migration step)
        try
        {
            await EnsureTracksTableAsync(conn, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure tracks table");
        }

        // prepare S3 client
        AmazonS3Config config = new AmazonS3Config();
        if (!string.IsNullOrEmpty(endpoint))
        {
            config.ServiceURL = endpoint;
            config.ForcePathStyle = true;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        AmazonS3Client? s3Client = null;
        TransferUtility? transferUtility = null;
        if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey) && !string.IsNullOrEmpty(bucket))
        {
            s3Client = new AmazonS3Client(accessKey, secretKey, config);
            transferUtility = new TransferUtility(s3Client);
            _logger.LogInformation("S3 client ready for bucket {bucket}", bucket);
        }

        // For production we accept a directory path via env UPLOAD_DIR or default to tmp generated files
        var uploadDir = Environment.GetEnvironmentVariable("UPLOAD_DIR");
        int filesPerGenre = int.TryParse(Environment.GetEnvironmentVariable("UPLOAD_FILES_PER_GEN"), out var n) ? n : 5;
        long sizeBytes = long.TryParse(Environment.GetEnvironmentVariable("UPLOAD_SIZE_BYTES"), out var s) ? s : 5 * 1024 * 1024;

        var genres = new[] { "afrobeat", "rnb", "reggae", "hiphop" };

        List<(string path, string genre)> files = new();
        if (!string.IsNullOrEmpty(uploadDir) && Directory.Exists(uploadDir))
        {
            _logger.LogInformation("Using files from {dir}", uploadDir);
            foreach (var g in genres)
            {
                var genreDir = Path.Combine(uploadDir, g);
                if (!Directory.Exists(genreDir)) continue;
                foreach (var f in Directory.EnumerateFiles(genreDir)) files.Add((f, g));
            }
        }
        else
        {
            _logger.LogInformation("No upload dir provided; generating {count} dummy files per genre", filesPerGenre);
            var tmp = Path.Combine(Path.GetTempPath(), "musicai_upload_test");
            Directory.CreateDirectory(tmp);
            var rnd = new Random();
            for (int g = 0; g < genres.Length; g++)
            {
                var genre = genres[g];
                for (int i = 0; i < filesPerGenre; i++)
                {
                    var path = Path.Combine(tmp, $"{genre}_{i}_{Guid.NewGuid():N}.bin");
                    CreateDummyFile(path, sizeBytes, rnd);
                    files.Add((path, genre));
                }
            }
        }

        if (files.Count == 0)
        {
            _logger.LogWarning("No files to upload. Exiting.");
            return;
        }

        var concurrency = int.TryParse(Environment.GetEnvironmentVariable("UPLOAD_CONCURRENCY"), out var c) ? c : 8;
        var semaphore = new SemaphoreSlim(concurrency);

        var sw = Stopwatch.StartNew();

        var uploadPolicy = Policy.Handle<Exception>().WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)), (ex, ts) =>
        {
            _logger.LogWarning(ex, "Upload attempt failed; retrying in {delay}", ts);
        });

        var dbPolicy = Policy.Handle<Exception>().WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(1));

        var tasks = files.Select(async f =>
        {
            await semaphore.WaitAsync(stoppingToken);
            try
            {
                var key = $"music/{f.genre}/{Path.GetFileName(f.path)}";
                if (transferUtility != null)
                {
                    await uploadPolicy.ExecuteAsync(async () =>
                    {
                        var request = new TransferUtilityUploadRequest
                        {
                            BucketName = bucket,
                            FilePath = f.path,
                            Key = key
                        };
                        await transferUtility.UploadAsync(request, stoppingToken);
                        _logger.LogInformation("Uploaded {file} -> {key}", f.path, key);
                    });
                }
                else
                {
                    _logger.LogInformation("S3 not configured; skipping upload for {file}", f.path);
                }

                // record to DB
                if (!string.IsNullOrEmpty(conn))
                {
                    await dbPolicy.ExecuteAsync(async () =>
                    {
                        await using var pg = new NpgsqlConnection(conn);
                        await pg.OpenAsync(stoppingToken);
                        await using var ccmd = new NpgsqlCommand("INSERT INTO tracks(genre, filename, s3key, filesize) VALUES(@g,@f,@k,@s)", pg);
                        ccmd.Parameters.AddWithValue("@g", f.genre);
                        ccmd.Parameters.AddWithValue("@f", Path.GetFileName(f.path));
                        ccmd.Parameters.AddWithValue("@k", transferUtility != null ? (object)key : (object)DBNull.Value);
                        ccmd.Parameters.AddWithValue("@s", new FileInfo(f.path).Length);
                        await ccmd.ExecuteNonQueryAsync(stoppingToken);
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload or DB write failed for {file}", f.path);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        var totalBytes = files.Sum(f => new FileInfo(f.path).Length);
        _logger.LogInformation("Completed uploading {count} files ({size}) in {time}. Throughput: {tp}/s", files.Count, FormatBytes(totalBytes), sw.Elapsed, FormatBytes((long)(totalBytes / Math.Max(1, sw.Elapsed.TotalSeconds))));

        _logger.LogInformation("UploadService run complete; stopping host.");
    }

    private static void CreateDummyFile(string path, long sizeBytes, Random rnd)
    {
        using var fs = File.Create(path);
        var buffer = new byte[8192];
        long remaining = sizeBytes;
        while (remaining > 0)
        {
            rnd.NextBytes(buffer);
            var toWrite = (int)Math.Min(buffer.Length, remaining);
            fs.Write(buffer, 0, toWrite);
            remaining -= toWrite;
        }
    }

    private static string FormatBytes(long b)
    {
        string[] suf = { "B", "KB", "MB", "GB", "TB" };
        if (b == 0) return "0B";
        var e = (int)Math.Floor(Math.Log(b) / Math.Log(1024));
        return Math.Round(b / Math.Pow(1024, e), 2) + suf[e];
    }

    private static async Task EnsureTracksTableAsync(string conn, CancellationToken ct)
    {
        await using var pg = new NpgsqlConnection(conn);
        await pg.OpenAsync(ct);
        var createSql = @"
CREATE TABLE IF NOT EXISTS tracks (
  id SERIAL PRIMARY KEY,
  genre TEXT NOT NULL,
  filename TEXT NOT NULL,
  s3key TEXT,
  filesize bigint,
  uploaded_at timestamptz default now()
);
";
        await using var cmd = new NpgsqlCommand(createSql, pg);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
