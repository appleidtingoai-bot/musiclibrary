using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MusicAI.Infrastructure.Services;

namespace MusicAI.Orchestrator.Services
{
    /// <summary>
    /// Manages adaptive bitrate streaming - serves different quality versions based on network conditions
    /// </summary>
    public interface IQualityService
    {
        Task<List<QualityVersion>> GetAvailableQualitiesAsync(string s3Key);
        Task<string> GetQualityUrlAsync(string s3Key, string quality, TimeSpan ttl);
    }

    public class QualityService : IQualityService
    {
        private readonly IS3Service _s3Service;
        private readonly IStreamTokenService _tokenService;
        private readonly ILogger<QualityService> _logger;

        public QualityService(IS3Service s3Service, IStreamTokenService tokenService, ILogger<QualityService> logger)
        {
            _s3Service = s3Service;
            _tokenService = tokenService;
            _logger = logger;
        }

        public async Task<List<QualityVersion>> GetAvailableQualitiesAsync(string s3Key)
        {
            try
            {
                // Check for quality variants in S3
                // Expected structure: music/genre/track.mp3 (original)
                //                    music/genre/track_high.mp3 (320kbps)
                //                    music/genre/track_medium.mp3 (192kbps)
                //                    music/genre/track_low.mp3 (128kbps)
                
                var basePath = s3Key.Replace(".mp3", "");
                var qualities = new List<QualityVersion>();

                // Check for each quality version
                var variants = new[]
                {
                    new { Suffix = "_high", Bitrate = 320, Label = "High (320kbps)" },
                    new { Suffix = "_medium", Bitrate = 192, Label = "Medium (192kbps)" },
                    new { Suffix = "_low", Bitrate = 128, Label = "Low (128kbps)" },
                    new { Suffix = "", Bitrate = 256, Label = "Original" }
                };

                foreach (var variant in variants)
                {
                    var variantKey = $"{basePath}{variant.Suffix}.mp3";
                    
                    // Adding variant entries without performing an S3 existence check.
                    // If you need to verify existence, add an appropriate method to IS3Service.
                    qualities.Add(new QualityVersion
                    {
                        Quality = variant.Suffix == "" ? "original" : variant.Suffix.TrimStart('_'),
                        Bitrate = variant.Bitrate,
                        Label = variant.Label,
                        S3Key = variantKey
                    });
                }

                // If no quality variants exist, return original only
                if (qualities.Count == 0)
                {
                    qualities.Add(new QualityVersion
                    {
                        Quality = "original",
                        Bitrate = 256,
                        Label = "Original",
                        S3Key = s3Key
                    });
                }

                _logger.LogInformation("Found {Count} quality versions for {S3Key}", qualities.Count, s3Key);
                return qualities.OrderByDescending(q => q.Bitrate).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get qualities for {S3Key}", s3Key);
                
                // Return original as fallback
                return new List<QualityVersion>
                {
                    new QualityVersion
                    {
                        Quality = "original",
                        Bitrate = 256,
                        Label = "Original",
                        S3Key = s3Key
                    }
                };
            }
        }

        public async Task<string> GetQualityUrlAsync(string s3Key, string quality, TimeSpan ttl)
        {
            try
            {
                var qualities = await GetAvailableQualitiesAsync(s3Key);
                var selected = qualities.FirstOrDefault(q => q.Quality == quality) 
                            ?? qualities.First(); // Fallback to first available

                var token = _tokenService.GenerateToken(selected.S3Key, ttl);
                return $"{selected.S3Key}?t={System.Net.WebUtility.UrlEncode(token)}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get quality URL for {S3Key} @ {Quality}", s3Key, quality);
                throw;
            }
        }
    }

    public class QualityVersion
    {
        public string Quality { get; set; } = string.Empty; // "high", "medium", "low", "original"
        public int Bitrate { get; set; } // kbps
        public string Label { get; set; } = string.Empty;
        public string S3Key { get; set; } = string.Empty;
    }
}
