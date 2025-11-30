using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicAI.Infrastructure.Services
{
    // Minimal local filesystem implementation of IS3Service used when AWS is not configured.
    public class NullS3Service : IS3Service
    {
        private readonly string _baseDir;

        public NullS3Service()
        {
            var baseDir = Path.Combine(AppContext.BaseDirectory, "data", "uploads");
            Directory.CreateDirectory(baseDir);
            _baseDir = baseDir;
        }

        private string LocalPathForKey(string key)
        {
            // sanitize key to avoid directory traversal
            var safe = key.Replace('\u0000', '_').Replace("..", "_");
            return Path.Combine(_baseDir, safe.Replace('/', Path.DirectorySeparatorChar));
        }

        public async Task<string> UploadFileAsync(string key, Stream stream, string contentType)
        {
            var path = LocalPathForKey(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _baseDir);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Position = 0;
                await stream.CopyToAsync(fs);
            }
            return $"file://{path}";
        }

        public Task<string> GetPresignedUrlAsync(string key, TimeSpan expiry)
        {
            var path = LocalPathForKey(key);
            return Task.FromResult($"file://{path}");
        }

        public Task DeleteObjectAsync(string key)
        {
            var path = LocalPathForKey(key);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return Task.CompletedTask;
        }

        public Task<IEnumerable<string>> ListObjectsAsync(string prefix)
        {
            var dir = _baseDir;
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_baseDir, f).Replace(Path.DirectorySeparatorChar, '/'))
                : Array.Empty<string>();
            if (!string.IsNullOrEmpty(prefix)) files = files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(files);
        }
    }
}
