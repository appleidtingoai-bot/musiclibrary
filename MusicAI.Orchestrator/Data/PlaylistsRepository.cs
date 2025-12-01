using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using Microsoft.Extensions.Configuration;

namespace MusicAI.Orchestrator.Data
{
    public interface IPlaylistsRepository
    {
        Task<string> CreatePlaylistAsync(string ownerId, string name, string? description, bool isCollaborative);
        Task<PlaylistDto?> GetPlaylistAsync(string playlistId);
        Task<List<PlaylistDto>> GetUserPlaylistsAsync(string userId);
        Task<bool> AddCollaboratorAsync(string playlistId, string userId);
        Task<bool> RemoveCollaboratorAsync(string playlistId, string userId);
        Task<List<CollaboratorDto>> GetCollaboratorsAsync(string playlistId);
        Task<bool> AddTrackAsync(string playlistId, string s3Key, string title, string artist, string addedBy);
        Task<bool> RemoveTrackAsync(string playlistId, string trackId);
        Task<List<PlaylistTrackDto>> GetPlaylistTracksAsync(string playlistId);
        Task<bool> IsCollaboratorAsync(string playlistId, string userId);
    }

    public class PlaylistsRepository : IPlaylistsRepository, IDisposable
    {
        private readonly string _connectionString;

        public PlaylistsRepository(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("Default")
                ?? Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__DEFAULT")
                ?? throw new InvalidOperationException("Database connection string not configured");
        }

        private NpgsqlConnection GetConnection() => new NpgsqlConnection(_connectionString);

        public async Task<string> CreatePlaylistAsync(string ownerId, string name, string? description, bool isCollaborative)
        {
            using var db = GetConnection();
            
            var playlistId = Guid.NewGuid().ToString();
            
            await db.ExecuteAsync(@"
                INSERT INTO playlists (id, owner_id, name, description, is_collaborative, created_at, updated_at)
                VALUES (@Id, @OwnerId, @Name, @Description, @IsCollaborative, @CreatedAt, @UpdatedAt)",
                new
                {
                    Id = playlistId,
                    OwnerId = ownerId,
                    Name = name,
                    Description = description ?? "",
                    IsCollaborative = isCollaborative,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });

            return playlistId;
        }

        public async Task<PlaylistDto?> GetPlaylistAsync(string playlistId)
        {
            using var db = GetConnection();
            
            return await db.QueryFirstOrDefaultAsync<PlaylistDto>(@"
                SELECT id, owner_id AS ownerId, name, description, is_collaborative AS isCollaborative, 
                       created_at AS createdAt, updated_at AS updatedAt
                FROM playlists
                WHERE id = @PlaylistId",
                new { PlaylistId = playlistId });
        }

        public async Task<List<PlaylistDto>> GetUserPlaylistsAsync(string userId)
        {
            using var db = GetConnection();
            
            var playlists = await db.QueryAsync<PlaylistDto>(@"
                SELECT DISTINCT p.id, p.owner_id AS ownerId, p.name, p.description, 
                       p.is_collaborative AS isCollaborative, p.created_at AS createdAt, p.updated_at AS updatedAt
                FROM playlists p
                LEFT JOIN playlist_collaborators pc ON p.id = pc.playlist_id
                WHERE p.owner_id = @UserId OR pc.user_id = @UserId
                ORDER BY p.updated_at DESC",
                new { UserId = userId });

            return playlists.AsList();
        }

        public async Task<bool> AddCollaboratorAsync(string playlistId, string userId)
        {
            using var db = GetConnection();
            
            try
            {
                await db.ExecuteAsync(@"
                    INSERT INTO playlist_collaborators (playlist_id, user_id, added_at)
                    VALUES (@PlaylistId, @UserId, @AddedAt)
                    ON CONFLICT (playlist_id, user_id) DO NOTHING",
                    new
                    {
                        PlaylistId = playlistId,
                        UserId = userId,
                        AddedAt = DateTime.UtcNow
                    });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> RemoveCollaboratorAsync(string playlistId, string userId)
        {
            using var db = GetConnection();
            
            var rows = await db.ExecuteAsync(@"
                DELETE FROM playlist_collaborators
                WHERE playlist_id = @PlaylistId AND user_id = @UserId",
                new { PlaylistId = playlistId, UserId = userId });

            return rows > 0;
        }

        public async Task<List<CollaboratorDto>> GetCollaboratorsAsync(string playlistId)
        {
            using var db = GetConnection();
            
            var collaborators = await db.QueryAsync<CollaboratorDto>(@"
                SELECT u.id, u.email, u.full_name AS fullName, pc.added_at AS addedAt
                FROM playlist_collaborators pc
                JOIN users u ON pc.user_id = u.id
                WHERE pc.playlist_id = @PlaylistId
                ORDER BY pc.added_at",
                new { PlaylistId = playlistId });

            return collaborators.AsList();
        }

        public async Task<bool> AddTrackAsync(string playlistId, string s3Key, string title, string artist, string addedBy)
        {
            using var db = GetConnection();
            
            try
            {
                var trackId = Guid.NewGuid().ToString();
                
                await db.ExecuteAsync(@"
                    INSERT INTO playlist_tracks (id, playlist_id, s3_key, title, artist, added_by, added_at, position)
                    VALUES (@Id, @PlaylistId, @S3Key, @Title, @Artist, @AddedBy, @AddedAt, 
                            (SELECT COALESCE(MAX(position), 0) + 1 FROM playlist_tracks WHERE playlist_id = @PlaylistId))",
                    new
                    {
                        Id = trackId,
                        PlaylistId = playlistId,
                        S3Key = s3Key,
                        Title = title,
                        Artist = artist,
                        AddedBy = addedBy,
                        AddedAt = DateTime.UtcNow
                    });

                // Update playlist's updated_at
                await db.ExecuteAsync(@"
                    UPDATE playlists SET updated_at = @UpdatedAt WHERE id = @PlaylistId",
                    new { PlaylistId = playlistId, UpdatedAt = DateTime.UtcNow });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> RemoveTrackAsync(string playlistId, string trackId)
        {
            using var db = GetConnection();
            
            var rows = await db.ExecuteAsync(@"
                DELETE FROM playlist_tracks
                WHERE id = @TrackId AND playlist_id = @PlaylistId",
                new { TrackId = trackId, PlaylistId = playlistId });

            if (rows > 0)
            {
                await db.ExecuteAsync(@"
                    UPDATE playlists SET updated_at = @UpdatedAt WHERE id = @PlaylistId",
                    new { PlaylistId = playlistId, UpdatedAt = DateTime.UtcNow });
            }

            return rows > 0;
        }

        public async Task<List<PlaylistTrackDto>> GetPlaylistTracksAsync(string playlistId)
        {
            using var db = GetConnection();
            
            var tracks = await db.QueryAsync<PlaylistTrackDto>(@"
                SELECT pt.id, pt.s3_key AS s3Key, pt.title, pt.artist, pt.added_by AS addedBy, 
                       pt.added_at AS addedAt, pt.position, u.full_name AS addedByName
                FROM playlist_tracks pt
                LEFT JOIN users u ON pt.added_by = u.id
                WHERE pt.playlist_id = @PlaylistId
                ORDER BY pt.position",
                new { PlaylistId = playlistId });

            return tracks.AsList();
        }

        public async Task<bool> IsCollaboratorAsync(string playlistId, string userId)
        {
            using var db = GetConnection();
            
            // Check if user is owner or collaborator
            var isAuthorized = await db.QueryFirstOrDefaultAsync<bool>(@"
                SELECT EXISTS(
                    SELECT 1 FROM playlists WHERE id = @PlaylistId AND owner_id = @UserId
                    UNION
                    SELECT 1 FROM playlist_collaborators WHERE playlist_id = @PlaylistId AND user_id = @UserId
                )",
                new { PlaylistId = playlistId, UserId = userId });

            return isAuthorized;
        }

        public void Dispose()
        {
            // Connection disposal handled by using statements
        }
    }

    public class PlaylistDto
    {
        public string Id { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsCollaborative { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CollaboratorDto
    {
        public string Id { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public DateTime AddedAt { get; set; }
    }

    public class PlaylistTrackDto
    {
        public string Id { get; set; } = string.Empty;
        public string S3Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string AddedBy { get; set; } = string.Empty;
        public string? AddedByName { get; set; }
        public DateTime AddedAt { get; set; }
        public int Position { get; set; }
    }
}
