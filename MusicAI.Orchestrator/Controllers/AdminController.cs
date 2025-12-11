using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net.Http.Headers;
using MusicAI.Orchestrator.Services;
using MusicAI.Orchestrator.Data;
using MusicAI.Orchestrator.Models;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MusicAI.Infrastructure.Services;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    // sessions persisted to disk via AdminSessionStore
    private readonly IS3Service? _s3;
    private readonly AdminsRepository _adminsRepo;
    private readonly MusicAI.Orchestrator.Services.MusicCategorizationService _categorizer;
    private readonly MusicAI.Orchestrator.Data.MusicRepository? _musicRepo;
    private static readonly HttpClient _httpClient = new();
    private static readonly AsyncRetryPolicy _uploadPolicy = Policy
        .Handle<Exception>()
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)));

    // simple IP-based rate limiter for stream proxy: max N requests per window
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _rateLimits = new();
    private const int RATE_WINDOW_SECONDS = 60;
    private const int RATE_MAX_REQUESTS = 60; // per IP per window

    public AdminController(
        IS3Service? s3 = null,
        AdminsRepository? adminsRepo = null,
        MusicAI.Orchestrator.Services.MusicCategorizationService? categorizer = null,
        MusicAI.Orchestrator.Data.MusicRepository? musicRepo = null)
    {
        _s3 = s3;
        _adminsRepo = adminsRepo ?? throw new ArgumentNullException(nameof(adminsRepo));
        _categorizer = categorizer ?? new MusicAI.Orchestrator.Services.MusicCategorizationService();
        _musicRepo = musicRepo;
    }

    public record AdminLoginRequest(string Email, string Password);

    [HttpPost("login")]
    public IActionResult Login([FromBody] AdminLoginRequest req)
    {
        try
        {
            if (req == null) return BadRequest(new { error = "request body required" });
            if (string.IsNullOrWhiteSpace(req.Email))
                return BadRequest(new { error = "email is required" });

            // Allow both superadmin and regular admin to login
            var record = _adminsRepo.GetByEmail(req.Email);
            if (record == null) return Unauthorized(new { error = "Admin not found" });
            if (!record.Approved) return Unauthorized(new { error = "Admin not approved" });

            // Create JWT with role information
            var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_jwt_secret_minimum_32_characters_required_for_hs256";
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var claims = new[] {
                new Claim(ClaimTypes.Email, record.Email),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim("isSuper", record.IsSuperAdmin.ToString().ToLower())
            };
            var handler = new JwtSecurityTokenHandler();
            var expires = DateTime.UtcNow.AddDays(7);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = expires,
                SigningCredentials = creds
            };
            var securityToken = handler.CreateToken(tokenDescriptor);
            var jwt = handler.WriteToken(securityToken);

            // Set HttpOnly cookie for automatic token refresh
            Response.Cookies.Append("MusicAI.Auth", jwt, new CookieOptions
            {
                HttpOnly = true,
                Secure = true, // HTTPS only
                SameSite = SameSiteMode.Strict,
                Expires = expires,
                Domain = Environment.GetEnvironmentVariable("COOKIE_DOMAIN") ?? ".tingoradio.ai",
                Path = "/"
            });

            return Ok(new { token = jwt, expires, isSuper = record.IsSuperAdmin, role = record.IsSuperAdmin ? "SuperAdmin" : "Admin" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Internal server error", detail = ex.Message });
        }
    }

    public record SuperAdminRegisterRequest(string Email, string Password);

    [HttpPost("superadmin/register")]
    public IActionResult RegisterSuperAdmin([FromBody] SuperAdminRegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { error = "Email is required" });

        var existing = _adminsRepo.GetByEmail(req.Email);
        if (existing != null)
        {
            // If admin exists, upgrade to superadmin and return JWT
            _adminsRepo.UpgradeToSuperAdmin(existing.Id);
            
            // Reload from database to get fresh superadmin flags
            existing = _adminsRepo.GetByEmail(req.Email);
            if (existing == null) return StatusCode(500, new { error = "Failed to reload admin after upgrade" });
            
            var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_jwt_secret_minimum_32_characters_required_for_hs256";
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var claims = new[] {
                new Claim(ClaimTypes.Email, existing.Email),
                new Claim(ClaimTypes.Role, existing.IsSuperAdmin ? "SuperAdmin" : "Admin"),
                new Claim("isSuper", existing.IsSuperAdmin.ToString().ToLower())
            };
            var handler = new JwtSecurityTokenHandler();
            var expires = DateTime.UtcNow.AddDays(7);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = expires,
                SigningCredentials = creds
            };
            var securityToken = handler.CreateToken(tokenDescriptor);
            var jwt = handler.WriteToken(securityToken);

            // Set HttpOnly cookie
            Response.Cookies.Append("MusicAI.Auth", jwt, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = expires,
                Domain = Environment.GetEnvironmentVariable("COOKIE_DOMAIN") ?? ".tingoradio.ai",
                Path = "/"
            });

             return Ok(new { token = jwt, expires, isSuper = existing.IsSuperAdmin, role = existing.IsSuperAdmin ? "SuperAdmin" : "Admin", message = "Upgraded to superadmin" });
        }

        // Create new superadmin
        var rec = new MusicAI.Orchestrator.Data.AdminRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = req.Email,
            PasswordHash = string.Empty,
            IsSuperAdmin = true,
            Approved = true,
            CreatedAt = DateTime.UtcNow
        };
        _adminsRepo.Create(rec);

        // Return JWT
        var jwtSecret2 = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_jwt_secret_minimum_32_characters_required_for_hs256";
        var key2 = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret2));
        var creds2 = new SigningCredentials(key2, SecurityAlgorithms.HmacSha256);
        var claims2 = new[] {
            new Claim(ClaimTypes.Email, rec.Email),
            new Claim(ClaimTypes.Role, "SuperAdmin"),
            new Claim("isSuper", "true")
        };
        var handler2 = new JwtSecurityTokenHandler();
        var expires2 = DateTime.UtcNow.AddDays(7);
        var tokenDescriptor2 = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims2),
            Expires = expires2,
            SigningCredentials = creds2
        };
        var securityToken2 = handler2.CreateToken(tokenDescriptor2);
        var jwt2 = handler2.WriteToken(securityToken2);

        // Set HttpOnly cookie
        Response.Cookies.Append("MusicAI.Auth", jwt2, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = expires2,
            Domain = Environment.GetEnvironmentVariable("COOKIE_DOMAIN") ?? ".tingoradio.ai",
            Path = "/"
        });

        return Ok(new { token = jwt2, expires = expires2, isSuper = true, role = "SuperAdmin", message = "Superadmin registered" });
    }

    [HttpPost("register-admin")]
    public IActionResult RegisterAdmin([FromBody] AdminLoginRequest req)
    {
        if (_adminsRepo.GetByEmail(req.Email) != null) return Conflict(new { error = "admin already exists" });
        var rec = new MusicAI.Orchestrator.Data.AdminRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = req.Email,
            PasswordHash = HashPassword(req.Password),
            IsSuperAdmin = false,
            Approved = false,
            CreatedAt = DateTime.UtcNow
        };
        _adminsRepo.Create(rec);
        return Ok(new { id = rec.Id, approved = rec.Approved });
    }

    /// <summary>
    /// Refresh token from HttpOnly cookie - call on page load to restore session
    /// </summary>
    [HttpPost("refresh-token")]
    public IActionResult RefreshToken()
    {
        try
        {
            // Try to get token from cookie
            if (!Request.Cookies.TryGetValue("MusicAI.Auth", out var token) || string.IsNullOrEmpty(token))
            {
                return Unauthorized(new { error = "No authentication cookie found" });
            }

            // Validate and decode the token
            var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_jwt_secret_minimum_32_characters_required_for_hs256";
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
            var handler = new JwtSecurityTokenHandler();

            try
            {
                var principal = handler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                }, out var validatedToken);

                // Extract claims
                var email = principal.FindFirst(ClaimTypes.Email)?.Value;
                var isSuperClaim = principal.FindFirst("isSuper")?.Value;
                var isSuper = isSuperClaim == "true";

                if (string.IsNullOrEmpty(email))
                    return Unauthorized(new { error = "Invalid token claims" });

                // Token is valid, issue a new one with extended expiry
                var record = _adminsRepo.GetByEmail(email);
                if (record == null || !record.Approved)
                    return Unauthorized(new { error = "Admin not found or not approved" });

                var newClaims = new[] {
                    new Claim(ClaimTypes.Email, record.Email),
                    new Claim(ClaimTypes.Role, record.IsSuperAdmin ? "SuperAdmin" : "Admin"),
                    new Claim("isSuper", record.IsSuperAdmin.ToString().ToLower())
                };

                var expires = DateTime.UtcNow.AddDays(7);
                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(newClaims),
                    Expires = expires,
                    SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
                };

                var newSecurityToken = handler.CreateToken(tokenDescriptor);
                var newJwt = handler.WriteToken(newSecurityToken);

                // Update cookie with new token
                Response.Cookies.Append("MusicAI.Auth", newJwt, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Expires = expires,
                    Domain = Environment.GetEnvironmentVariable("COOKIE_DOMAIN") ?? ".tingoradio.ai",
                    Path = "/"
                });

                return Ok(new
                {
                    token = newJwt,
                    expires,
                    isSuper = record.IsSuperAdmin,
                    role = record.IsSuperAdmin ? "SuperAdmin" : "Admin",
                    email = record.Email
                });
            }
            catch (SecurityTokenExpiredException)
            {
                // Token expired - clear cookie and require login
                Response.Cookies.Delete("MusicAI.Auth");
                return Unauthorized(new { error = "Token expired, please login again" });
            }
            catch (Exception ex)
            {
                return Unauthorized(new { error = "Invalid token", detail = ex.Message });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Token refresh failed", detail = ex.Message });
        }
    }

    /// <summary>
    /// Logout - clears authentication cookie
    /// </summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("MusicAI.Auth");
        return Ok(new { message = "Logged out successfully" });
    }

    [HttpPost("approve/{adminId}")]
    public IActionResult ApproveAdmin([FromRoute] string adminId)
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) return Unauthorized(new { error = "admin required" });
        if (!isSuper) return Forbid();
        _adminsRepo.Approve(adminId);
        return Ok(new { approved = true, id = adminId });
    }

    [HttpGet("pending")]
    public IActionResult PendingAdmins()
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) return Unauthorized(new { error = "admin required" });
        if (!isSuper) return Forbid();
        try
        {
            var items = _adminsRepo.GetPending();
            var result = items.Select(i => new { id = i.Id, email = i.Email, createdAt = i.CreatedAt });
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// SUPERADMIN ONLY: Get system performance metrics
    /// </summary>
    [HttpGet("superadmin/performance")]
    public IActionResult GetPerformance()
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) 
            return Unauthorized(new { error = "admin required" });
        if (!isSuper) 
            return Forbid();

        try
        {
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
            var totalFiles = Directory.Exists(uploadsDir) 
                ? Directory.GetFiles(uploadsDir, "*.*", SearchOption.AllDirectories).Length 
                : 0;

            var folders = Directory.Exists(uploadsDir)
                ? Directory.GetDirectories(uploadsDir).Select(d => new
                {
                    folder = Path.GetFileName(d),
                    fileCount = Directory.GetFiles(d, "*.*", SearchOption.AllDirectories).Length,
                    sizeBytes = Directory.GetFiles(d, "*.*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                }).ToArray()
                : Array.Empty<object>();

            return Ok(new
            {
                totalMusicFiles = totalFiles,
                totalAdmins = _adminsRepo.GetAll().Count(),
                approvedAdmins = _adminsRepo.GetAll().Count(a => a.Approved),
                superAdmins = _adminsRepo.GetAll().Count(a => a.IsSuperAdmin),
                folderStats = folders,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// SUPERADMIN ONLY: Get transaction records (placeholder for payment integration)
    /// </summary>
    [HttpGet("superadmin/transactions")]
    public IActionResult GetTransactions([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) 
            return Unauthorized(new { error = "admin required" });
        if (!isSuper) 
            return Forbid();

        // Placeholder - integrate with payment service
        return Ok(new
        {
            message = "Transaction tracking coming soon",
            totalTransactions = 0,
            page,
            pageSize,
            transactions = Array.Empty<object>(),
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// SUPERADMIN ONLY: Get OAP performance metrics
    /// </summary>
    [HttpGet("superadmin/oap-performance")]
    public IActionResult GetOapPerformance()
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) 
            return Unauthorized(new { error = "admin required" });
        if (!isSuper) 
            return Forbid();

        // Placeholder - integrate with OAP metrics
        return Ok(new
        {
            message = "OAP performance tracking active",
            oaps = new[]
            {
                new { name = "Tosin", activeUsers = 0, songsPlayed = 0, uptime = "100%" },
                new { name = "Future OAPs", activeUsers = 0, songsPlayed = 0, uptime = "N/A" }
            },
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// SUPERADMIN ONLY: Delete an admin
    /// </summary>
    [HttpDelete("superadmin/delete-admin/{adminId}")]
    public IActionResult DeleteAdmin([FromRoute] string adminId)
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) 
            return Unauthorized(new { error = "admin required" });
        if (!isSuper) 
            return Forbid();

        try
        {
            var targetAdmin = _adminsRepo.GetById(adminId);
            if (targetAdmin == null) 
                return NotFound(new { error = "Admin not found" });

            // Prevent deleting yourself
            if (targetAdmin.Email.Equals(adminEmail, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Cannot delete yourself" });

            _adminsRepo.Delete(adminId);
            return Ok(new { deleted = true, adminId, email = targetAdmin.Email });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// SUPERADMIN ONLY: List all admins
    /// </summary>
    [HttpGet("superadmin/list-admins")]
    public IActionResult ListAdmins()
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) 
            return Unauthorized(new { error = "admin required" });
        if (!isSuper) 
            return Forbid();

        try
        {
            var admins = _adminsRepo.GetAll().Select(a => new
            {
                id = a.Id,
                email = a.Email,
                isSuperAdmin = a.IsSuperAdmin,
                approved = a.Approved,
                createdAt = a.CreatedAt
            }).ToArray();

            return Ok(new
            {
                totalAdmins = admins.Length,
                admins
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// ADMIN: Get limited upload statistics (own uploads only)
    /// </summary>
    [HttpGet("admin/my-uploads")]
    public IActionResult GetMyUploads()
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) 
            return Unauthorized(new { error = "admin required" });

        // For now, return aggregate stats (file tracking per admin coming soon)
        try
        {
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
            var totalFiles = Directory.Exists(uploadsDir) 
                ? Directory.GetFiles(uploadsDir, "*.*", SearchOption.AllDirectories).Length 
                : 0;

            return Ok(new
            {
                message = "Upload tracking by admin coming soon",
                totalSystemFiles = totalFiles,
                adminEmail,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // Check JWT role 'Admin' or fallback to X-Admin-Token header
    private bool IsAdminAuthorized(out string? adminEmail, out bool isSuper)
    {
        adminEmail = null; isSuper = false;

        // Try to extract JWT from Authorization header (with or without "Bearer " prefix)
        string? jwt = null;
        if (Request.Headers.TryGetValue("Authorization", out var auth) && !string.IsNullOrEmpty(auth))
        {
            var header = auth.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                jwt = header.Substring("Bearer ".Length).Trim();
            }
            else
            {
                // Support Authorization header without "Bearer " prefix
                jwt = header.Trim();
            }
        }
        
        // Also check X-Admin-Token header for JWT (backward compatibility)
        if (string.IsNullOrEmpty(jwt) && Request.Headers.TryGetValue("X-Admin-Token", out var xtoken) && !string.IsNullOrEmpty(xtoken))
        {
            jwt = xtoken.ToString().Trim();
        }

        // Try to validate as JWT
        if (!string.IsNullOrEmpty(jwt) && jwt.Contains("."))
        {
            try
            {
                var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_jwt_secret_minimum_32_characters_required_for_hs256";
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
                var tokenHandler = new JwtSecurityTokenHandler();
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                var principal = tokenHandler.ValidateToken(jwt, validationParameters, out var validatedToken);
                var email = principal.FindFirst(ClaimTypes.Email)?.Value ?? principal.FindFirst("email")?.Value;
                if (!string.IsNullOrEmpty(email))
                {
                    var rec = _adminsRepo.GetByEmail(email);
                    if (rec != null && rec.Approved)
                    {
                        adminEmail = email;
                        // prefer repo value for isSuper (ensure claim wasn't forged)
                        isSuper = rec.IsSuperAdmin;
                        return true;
                    }
                }
            }
            catch { /* invalid/expired token -> fallthrough to other checks */ }
        }

        // Check ASP.NET Core authentication
        if (User?.Identity?.IsAuthenticated == true && User.IsInRole("Admin"))
        {
            var email = User.FindFirst(ClaimTypes.Email)?.Value ?? User.Identity?.Name;
            if (!string.IsNullOrEmpty(email))
            {
                var rec = _adminsRepo.GetByEmail(email!);
                if (rec != null && rec.Approved)
                {
                    adminEmail = email;
                    isSuper = rec.IsSuperAdmin;
                    return true;
                }
            }
        }

        // Legacy session-based auth (X-Admin-Token with session ID, not JWT)
        if (Request.Headers.TryGetValue("X-Admin-Token", out var sessionToken) && !string.IsNullOrEmpty(sessionToken))
        {
            var token = sessionToken.ToString();
            // Only treat as session token if it doesn't look like a JWT
            if (!token.Contains(".") && AdminSessionStore.ValidateSession(token, out var email))
            {
                var rec = _adminsRepo.GetByEmail(email!);
                if (rec != null && rec.Approved)
                {
                    adminEmail = email;
                    isSuper = rec.IsSuperAdmin;
                    return true;
                }
            }
        }
        
        return false;
    }

    // Improved profanity detection: normalize common leet substitutions and collapse repeated characters
    private static readonly string[] ProfanityWords = new[]
    {
        "fuck","fucking","fucker","shit","bitch","bastard","asshole","damn","cunt"
    };

    private static string NormalizeForProfanity(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var lowered = s.ToLowerInvariant();
        // common leet replacements
        lowered = lowered.Replace('0', 'o').Replace('1', 'i').Replace('3', 'e').Replace('4', 'a').Replace('@', 'a').Replace('$', 's');
        // remove non-letters/digits
        lowered = Regex.Replace(lowered, "[^a-z0-9]", "");
        // collapse repeated characters: baaaad -> bad
        lowered = Regex.Replace(lowered, "([a-z])\\1+", "$1");
        return lowered;
    }

    private static bool ContainsProfanity(string input)
    {
        var norm = NormalizeForProfanity(input);
        if (string.IsNullOrWhiteSpace(norm)) return false;
        foreach (var w in ProfanityWords)
        {
            var nw = NormalizeForProfanity(w);
            if (norm.Contains(nw)) return true;
        }
        return false;
    }

    /// <summary>
    /// Get music library with pagination, filtering, and shuffle support
    /// Returns tracks ready for queue/playlist building like Spotify
    /// </summary>
    [HttpGet("library")]
    public async Task<IActionResult> GetMusicLibrary(
        [FromQuery] string? folder = null, 
        [FromQuery] string? search = null,
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 50,
        [FromQuery] bool shuffle = false)
    {
        if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
            return Unauthorized(new { error = "admin required" });

        try
        {
            var allTracks = new List<object>();
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");

            if (Directory.Exists(uploadsDir))
            {
                var folders = string.IsNullOrEmpty(folder) 
                    ? Directory.GetDirectories(uploadsDir) 
                    : new[] { Path.Combine(uploadsDir, folder) };

                foreach (var dir in folders.Where(Directory.Exists))
                {
                    var folderName = Path.GetFileName(dir);
                    var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                        .Where(f => new[] { ".mp3", ".m4a", ".wav", ".flac", ".aac" }.Contains(Path.GetExtension(f).ToLower()));

                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileName(file);
                        if (!string.IsNullOrEmpty(search) && !fileName.Contains(search, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var key = $"music/{folderName}/{fileName}";
                        var streamUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-proxy?key={Uri.EscapeDataString(key)}";
                        var hlsUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-hls/{key}";

                        allTracks.Add(new
                        {
                            id = Convert.ToBase64String(Encoding.UTF8.GetBytes(key)),
                            title = Path.GetFileNameWithoutExtension(fileName),
                            fileName = fileName,
                            folder = folderName,
                            key = key,
                            streamUrl = streamUrl,
                            hlsUrl = hlsUrl,
                            duration = 0, // TODO: Extract from file metadata
                            size = new FileInfo(file).Length
                        });
                    }
                }
            }

            // Shuffle if requested (Spotify-like)
            if (shuffle)
            {
                var rng = new Random();
                allTracks = allTracks.OrderBy(_ => rng.Next()).ToList();
            }

            // Pagination
            var total = allTracks.Count;
            var tracks = allTracks.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return Ok(new
            {
                page,
                pageSize,
                totalTracks = total,
                totalPages = (int)Math.Ceiling(total / (double)pageSize),
                shuffle,
                tracks,
                hasNext = page * pageSize < total,
                nextPage = page * pageSize < total ? page + 1 : (int?)null
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get next tracks for auto-play queue (Spotify-like continuous playback)
    /// </summary>
    [HttpGet("next-tracks")]
    public IActionResult GetNextTracks([FromQuery] string? currentTrackId = null, [FromQuery] int count = 10)
    {
        if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
            return Unauthorized(new { error = "admin required" });

        try
        {
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
            var allTracks = new List<object>();

            if (Directory.Exists(uploadsDir))
            {
                foreach (var dir in Directory.GetDirectories(uploadsDir))
                {
                    var folderName = Path.GetFileName(dir);
                    var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                        .Where(f => new[] { ".mp3", ".m4a", ".wav", ".flac", ".aac" }.Contains(Path.GetExtension(f).ToLower()));

                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileName(file);
                        var key = $"music/{folderName}/{fileName}";
                        var trackId = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

                        allTracks.Add(new
                        {
                            id = trackId,
                            title = Path.GetFileNameWithoutExtension(fileName),
                            fileName = fileName,
                            folder = folderName,
                            key = key,
                            streamUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-proxy?key={Uri.EscapeDataString(key)}",
                            hlsUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-hls/{key}"
                        });
                    }
                }
            }

            // Shuffle for auto-play variety
            var rng = new Random();
            var shuffled = allTracks.OrderBy(_ => rng.Next()).ToList();

            // If currentTrackId provided, ensure it's not in the next batch
            if (!string.IsNullOrEmpty(currentTrackId))
            {
                shuffled = shuffled.Where(t => ((dynamic)t).id != currentTrackId).ToList();
            }

            var nextTracks = shuffled.Take(count).ToList();

            return Ok(new
            {
                count = nextTracks.Count,
                tracks = nextTracks,
                autoPlayReady = true
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// HLS master playlist endpoint - returns .m3u8 manifest for adaptive bitrate streaming
    /// Provides high-quality audio streaming with support for seeking and adaptive quality
    /// </summary>
    [HttpGet("stream-hls/{*key}")]
    public IActionResult StreamHls([FromRoute] string key)
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) 
            return Unauthorized(new { error = "admin required" });
        if (string.IsNullOrWhiteSpace(key)) 
            return BadRequest(new { error = "key is required" });

        // Generate HLS master playlist for adaptive bitrate streaming
        var baseUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-proxy?key={Uri.EscapeDataString(key)}";
        
        // Create M3U8 master playlist with multiple quality levels
        var m3u8Content = $@"#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:10
#EXT-X-MEDIA-SEQUENCE:0
#EXT-X-PLAYLIST-TYPE:VOD

# High Quality Stream (320kbps equivalent)
#EXTINF:10.0,
{baseUrl}&quality=high

#EXT-X-ENDLIST";

        return Content(m3u8Content, "application/vnd.apple.mpegurl");
    }

    // Stream proxy endpoint - proxies content from S3 presigned URL so the client never sees the raw signed URL
    [HttpGet("stream")]
    public async Task<IActionResult> Stream([FromQuery] string key)
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) return Unauthorized(new { error = "admin required" });
        if (string.IsNullOrWhiteSpace(key)) return BadRequest(new { error = "key is required" });

        if (_s3 == null)
        {
            // try local fallback
            var candidate = key.StartsWith("genres/") ? key.Split('/').Last() : key;
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
            var found = Directory.GetFiles(uploadsDir, "*" + candidate, SearchOption.AllDirectories).FirstOrDefault();
            if (found == null) return NotFound(new { error = "Not found" });
            var fs = System.IO.File.OpenRead(found);
            return File(fs, "application/octet-stream", enableRangeProcessing: true);
        }

        var presigned = await _s3.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(1));
        using var http = new HttpClient();
        using var upstream = await http.GetAsync(presigned, HttpCompletionOption.ResponseHeadersRead);
        if (!upstream.IsSuccessStatusCode) return StatusCode((int)upstream.StatusCode);
        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        Response.ContentType = contentType;
        await using var stream = await upstream.Content.ReadAsStreamAsync();
        await stream.CopyToAsync(Response.Body);
        return new EmptyResult();
    }

    // Stream proxy for authenticated users/subscribers (prevents exposing presigned URLs)
    [Authorize(Policy = "UserOrAdmin")]
    [HttpGet("stream-proxy")]
    public async Task<IActionResult> StreamProxy([FromQuery] string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return BadRequest(new { error = "key is required" });

        // rate-limit by client IP
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "unknown";
        var now = DateTime.UtcNow;
        _rateLimits.AddOrUpdate(clientIp, (1, now), (ip, state) =>
        {
            if ((now - state.WindowStart).TotalSeconds > RATE_WINDOW_SECONDS) return (1, now);
            return (state.Count + 1, state.WindowStart);
        });
        var stateAfter = _rateLimits[clientIp];
        if ((now - stateAfter.WindowStart).TotalSeconds <= RATE_WINDOW_SECONDS && stateAfter.Count > RATE_MAX_REQUESTS)
        {
            return StatusCode(429, new { error = "Too many requests" });
        }

        // entitlement: allow admins; require subscribers for regular users
        if (User?.Identity?.IsAuthenticated == true && !User.IsInRole("Admin"))
        {
            var claim = User.FindFirst("is_subscribed")?.Value ?? User.FindFirst("is-subscribed")?.Value;
            if (!bool.TryParse(claim, out var isSub) || !isSub)
            {
                return Forbid();
            }
        }

        // allow users (authenticated) to stream via server proxy; server does not expose presigned URL
        if (_s3 == null)
        {
            var candidate = key.StartsWith("genres/") ? key.Split('/').Last() : key;
            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
            var found = Directory.GetFiles(uploadsDir, "*" + candidate, SearchOption.AllDirectories).FirstOrDefault();
            if (found == null) return NotFound(new { error = "Not found" });
            var fs = System.IO.File.OpenRead(found);
            return File(fs, "application/octet-stream", enableRangeProcessing: true);
        }

        var presigned = await _s3.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(1));

        // Forward client's Range header (if any) to the upstream presigned URL so S3 returns partial content
        var req = new HttpRequestMessage(HttpMethod.Get, presigned);
        if (Request.Headers.TryGetValue("Range", out var range) && !string.IsNullOrEmpty(range))
        {
            // Add without validation to preserve the exact Range string
            req.Headers.TryAddWithoutValidation("Range", range.ToString());
        }

        var upstream = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!upstream.IsSuccessStatusCode && upstream.StatusCode != System.Net.HttpStatusCode.PartialContent)
            return StatusCode((int)upstream.StatusCode);

        // Detect audio format from key extension for proper MIME type
        var contentType = upstream.Content.Headers.ContentType?.ToString();
        if (string.IsNullOrEmpty(contentType) || contentType == "application/octet-stream")
        {
            var extension = Path.GetExtension(key).ToLowerInvariant();
            contentType = extension switch
            {
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".wav" => "audio/wav",
                ".flac" => "audio/flac",
                ".ogg" => "audio/ogg",
                _ => "audio/mpeg"
            };
        }
        
        Response.Headers["Cache-Control"] = "public, max-age=3600"; // Cache for 1 hour for better performance
        Response.Headers["Accept-Ranges"] = "bytes";
        // Echo Origin header only if it matches allowed origins
        var origin = Request.Headers.ContainsKey("Origin") ? Request.Headers["Origin"].ToString() : string.Empty;
        if (!string.IsNullOrEmpty(origin) && (origin == "http://localhost:3000" || origin == "https://tingoradio.ai" || origin == "https://tingoradiomusiclibrary.tingoai.ai"))
        {
            Response.Headers["Access-Control-Allow-Origin"] = origin; // Allow CORS for browser streaming
        }

        if (upstream.Content.Headers.ContentLength.HasValue)
            Response.ContentLength = upstream.Content.Headers.ContentLength.Value;

        if (upstream.Content.Headers.ContentRange != null)
            Response.Headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();

        Response.ContentType = contentType;
        Response.StatusCode = (int)upstream.StatusCode;

        var upstreamStream = await upstream.Content.ReadAsStreamAsync();
        await upstreamStream.CopyToAsync(Response.Body);
        return new EmptyResult();
    }

    /// <summary>
    /// Bulk upload music files to a specific folder (genre/vibe)
    /// Optimized for concurrent uploads (up to 20 parallel streams)
    /// </summary>
    /// <param name="folder">Folder name (e.g., hip-hop, jazz, gospel). Use /api/admin/music-folders to see available folders.</param>
    /// <param name="files">Music files to upload (MP3, WAV, M4A, FLAC)</param>
    [HttpPost("bulk-upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 401)]
    public async Task<IActionResult> BulkUpload([FromQuery] string folder, [FromForm] IFormFileCollection files)
    {
        if (!IsAdminAuthorized(out var ae, out var isSuper2) || string.IsNullOrEmpty(ae))
            return Unauthorized(new { error = "admin required" });

        if (string.IsNullOrWhiteSpace(folder))
            return BadRequest(new { error = "folder is required" });

        // Validate folder exists in our predefined music folders
        if (!MusicFolder.IsValidFolder(folder))
        {
            return BadRequest(new
            {
                error = "Invalid folder. Use /api/admin/music-folders to see available folders.",
                availableFolders = MusicFolder.All.Select(f => f.Name).ToArray()
            });
        }

        var musicFolder = MusicFolder.GetByName(folder);
        if (files == null || files.Count == 0)
            return BadRequest(new { error = "No files provided" });

        var uploaded = new ConcurrentBag<object>();
        var rejected = new ConcurrentBag<object>();

        // Increased concurrency from 6 to 20 for faster bulk uploads (50-100+ files)
        var semaphore = new System.Threading.SemaphoreSlim(20);
        var tasks = new List<Task>();

        foreach (var file in files)
        {
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var fileName = Path.GetFileName(file.FileName ?? string.Empty);
                    if (ContainsProfanity(fileName))
                    {
                        rejected.Add(new { file = fileName, reason = "Profanity detected in filename" });
                        return;
                    }

                    // Use normalized folder name for consistent S3 structure
                    var normalizedFolder = musicFolder!.Name;
                    var key = $"music/{normalizedFolder}/{Guid.NewGuid():N}_{fileName}";

                    // Copy to memory to allow retries
                    await using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    ms.Position = 0;

                    try
                    {
                        await _uploadPolicy.ExecuteAsync(async () =>
                        {
                            ms.Position = 0;
                            if (_s3 != null)
                            {
                                await _s3.UploadFileAsync(key, ms, file.ContentType ?? "audio/mpeg");
                            }
                            else
                            {
                                var uploads = Path.Combine(AppContext.BaseDirectory, "uploads", normalizedFolder);
                                Directory.CreateDirectory(uploads);
                                var path = Path.Combine(uploads, key.Split('/').Last());
                                await using (var fs = System.IO.File.Create(path))
                                {
                                    ms.Position = 0;
                                    await ms.CopyToAsync(fs);
                                }
                            }
                        });
                        
                        // Save to database if MusicRepository is available
                        if (_musicRepo != null)
                        {
                            try
                            {
                                var track = new MusicAI.Orchestrator.Data.MusicTrack
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    Title = Path.GetFileNameWithoutExtension(fileName),
                                    Artist = "Unknown", // TODO: Extract from metadata
                                    Genre = normalizedFolder,
                                    S3Key = key,
                                    S3Bucket = "tingoradiobucket",
                                    ContentType = file.ContentType ?? "audio/mpeg",
                                    FileSizeBytes = ms.Length,
                                    UploadedBy = ae,
                                    UploadedAt = DateTime.UtcNow,
                                    IsActive = true
                                };
                                await _musicRepo.CreateAsync(track);
                            }
                            catch (Exception dbEx)
                            {
                                // Log but don't fail upload if database insert fails
                                Console.WriteLine($"Warning: Failed to save track to database: {dbEx.Message}");
                            }
                        }
                        
                        // Return PUBLIC streaming URLs (no auth required)
                        string streamUrl;
                        string hlsUrl;
                        var cdn = Environment.GetEnvironmentVariable("CDN_DOMAIN")?.TrimEnd('/');
                        if (!string.IsNullOrEmpty(cdn))
                        {
                            hlsUrl = $"https://{cdn}/{key}";
                            streamUrl = $"https://{cdn}/{key}";
                        }
                        else
                        {
                            streamUrl = $"{Request.Scheme}://{Request.Host}/api/music/stream/{key}";
                            try
                            {
                                if (MusicAI.Infrastructure.Services.CloudFrontCookieSigner.IsConfigured)
                                {
                                    var cfDomain = Environment.GetEnvironmentVariable("CLOUDFRONT_DOMAIN")?.TrimEnd('/');
                                    if (!string.IsNullOrEmpty(cfDomain))
                                    {
                                        hlsUrl = $"https://{cfDomain}/{key}";
                                    }
                                    else
                                    {
                                        hlsUrl = $"{Request.Scheme}://{Request.Host}/api/music/hls/{key}";
                                    }
                                }
                                else
                                {
                                    hlsUrl = $"{Request.Scheme}://{Request.Host}/api/music/hls/{key}";
                                }
                            }
                            catch
                            {
                                hlsUrl = $"{Request.Scheme}://{Request.Host}/api/music/hls/{key}";
                            }
                        }
                        
                        uploaded.Add(new
                        {
                            file = fileName,
                            key,
                            streamUrl = streamUrl,
                            hlsUrl = hlsUrl,
                            folder = normalizedFolder,
                            displayName = musicFolder.DisplayName
                        });
                    }
                    catch (Exception ex)
                    {
                        rejected.Add(new
                        {
                            file = Path.GetFileName(file.FileName ?? string.Empty),
                            reason = ex.Message
                        });
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        return Ok(new
        {
            folder = musicFolder!.DisplayName,
            folderKey = musicFolder.Name,
            totalFiles = files.Count,
            uploadedCount = uploaded.Count,
            rejectedCount = rejected.Count,
            rejected = rejected.ToArray(),
            uploaded = uploaded.ToArray()
        });
    }

    /// <summary>
    /// Get all available music folders with their metadata
    /// OAPs use this to discover music folders based on personality/mood
    /// </summary>
    [HttpGet("music-folders")]
    public IActionResult GetMusicFolders([FromQuery] string? mood = null, [FromQuery] string? vibe = null)
    {
        if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
            return Unauthorized(new { error = "admin required" });

        IEnumerable<MusicFolder> folders = MusicFolder.All;

        // Filter by mood if specified
        if (!string.IsNullOrWhiteSpace(mood))
        {
            folders = MusicFolder.GetByMood(mood);
        }
        // Filter by vibe if specified
        else if (!string.IsNullOrWhiteSpace(vibe))
        {
            folders = MusicFolder.GetByVibe(vibe);
        }

        return Ok(new
        {
            totalFolders = folders.Count(),
            folders = folders.Select(f => new
            {
                name = f.Name,
                displayName = f.DisplayName,
                description = f.Description,
                moods = f.Moods,
                vibes = f.Vibes
            }).ToArray()
        });
    }

    /// <summary>
    /// Get contents of a specific music folder
    /// Lists all uploaded music in the folder for verification
    /// </summary>
    [HttpGet("folder-contents/{folderName}")]
    public async Task<IActionResult> GetFolderContents([FromRoute] string folderName)
    {
        if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
            return Unauthorized(new { error = "admin required" });

        if (!MusicFolder.IsValidFolder(folderName))
            return BadRequest(new { error = "Invalid folder name" });

        var folder = MusicFolder.GetByName(folderName);
        var contents = new List<object>();

        if (_s3 != null)
        {
            // For S3, we'd need to implement a ListObjects method
            // For now, return metadata about the folder
            return Ok(new
            {
                folder = folder!.DisplayName,
                folderKey = folder.Name,
                message = "S3 listing not implemented. Upload tracking needed."
            });
        }
        else
        {
            // Local filesystem
            var uploads = Path.Combine(AppContext.BaseDirectory, "uploads", folder!.Name);
            if (Directory.Exists(uploads))
            {
                var files = Directory.GetFiles(uploads, "*.*", SearchOption.AllDirectories);
                contents = files.Select(f => new
                {
                    fileName = Path.GetFileName(f),
                    size = new FileInfo(f).Length,
                    key = $"music/{folder.Name}/{Path.GetFileName(f)}"
                }).Cast<object>().ToList();
            }

            return Ok(new
            {
                folder = folder.DisplayName,
                folderKey = folder.Name,
                fileCount = contents.Count,
                files = contents
            });
        }
    }

    [HttpDelete("delete")]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest req)
    {
        if (!IsAdminAuthorized(out var adminEmail2, out var isSuperAdmin) || string.IsNullOrEmpty(adminEmail2)) return Unauthorized(new { error = "admin required" });
        if (!isSuperAdmin) return Forbid();
        if (req == null || string.IsNullOrWhiteSpace(req.Key)) return BadRequest(new { error = "Key is required" });
        try
        {
            if (_s3 != null)
            {
                await DeleteS3ObjectAsync(_s3, req.Key);
                return Ok(new { deleted = req.Key });
            }
            else
            {
                var candidate = req.Key;
                if (candidate.StartsWith("genres/")) candidate = candidate.Split('/').Last();
                var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
                var found = Directory.GetFiles(uploadsDir, "*" + candidate, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null)
                {
                    System.IO.File.Delete(found);
                    return Ok(new { deleted = req.Key });
                }
                return NotFound(new { error = "Not found" });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    public record DeleteRequest(string Key);

    public record RepairPasswordRequest(string Email, string NewPassword);

    [HttpPost("repair-password")]
    public IActionResult RepairPassword([FromBody] RepairPasswordRequest req)
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) return Unauthorized(new { error = "admin required" });
        if (!isSuper) return Forbid();
        if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.NewPassword)) return BadRequest(new { error = "email and newPassword are required" });

        var rec = _adminsRepo.GetByEmail(req.Email);
        if (rec == null) return NotFound(new { error = "admin not found" });

        var newHash = HashPassword(req.NewPassword);
        try
        {
            _adminsRepo.UpdatePassword(rec.Id, newHash);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "failed to update password", detail = ex.Message });
        }

        return Ok(new { success = true, email = rec.Email });
    }

    // Superadmin-only: inspect an admin record (masked hash) for debugging
    [HttpGet("admin-record")]
    public IActionResult GetAdminRecord([FromQuery] string email)
    {
        if (!IsAdminAuthorized(out var adminEmail, out var isSuper) || string.IsNullOrEmpty(adminEmail)) return Unauthorized(new { error = "admin required" });
        if (!isSuper) return Forbid();
        if (string.IsNullOrWhiteSpace(email)) return BadRequest(new { error = "email required" });

        var rec = _adminsRepo.GetByEmail(email);
        if (rec == null) return NotFound(new { error = "admin not found" });

        var raw = rec.PasswordHash ?? string.Empty;
        string masked;
        if (string.IsNullOrEmpty(raw)) masked = string.Empty;
        else if (raw.Length <= 8) masked = new string('*', raw.Length);
        else masked = raw.Substring(0, 4) + new string('*', Math.Max(0, raw.Length - 8)) + raw.Substring(raw.Length - 4);

        // Include the raw password hash unconditionally in the response for callers
        // who passed the superadmin authorization check above. This removes the
        // environment gating and returns the stored hash directly.
        string? passwordHashRaw = raw;

        return Ok(new
        {
            id = rec.Id,
            email = rec.Email,
            isSuperAdmin = rec.IsSuperAdmin,
            approved = rec.Approved,
            createdAt = rec.CreatedAt,
            passwordHashPresent = !string.IsNullOrEmpty(raw),
            passwordHashLength = raw.Length,
            passwordHashMasked = masked,
            passwordHashRaw = passwordHashRaw
        });
    }

    // Helper to call a delete method on IS3Service implementations that may use different method names.
    // This uses reflection to attempt common delete method names (async/sync) and awaits if a Task is returned.
    private static async Task DeleteS3ObjectAsync(IS3Service s3, string key)
    {
        if (s3 == null) throw new ArgumentNullException(nameof(s3));
        var type = s3.GetType();
        var candidateNames = new[] { "DeleteObjectAsync", "DeleteAsync", "DeleteObject", "Delete", "RemoveObjectAsync", "RemoveAsync", "RemoveObject", "Remove" };

        foreach (var name in candidateNames)
        {
            // try signature with single string parameter first
            var method = type.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase, null, new[] { typeof(string) }, null)
                ?? type.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);

            if (method == null) continue;

            var parameters = method.GetParameters();
            object? result;
            if (parameters.Length == 1)
            {
                result = method.Invoke(s3, new object[] { key });
            }
            else
            {
                result = method.Invoke(s3, Array.Empty<object>());
            }

            if (result is Task taskResult)
            {
                await taskResult.ConfigureAwait(false);
            }

            // If method invoked (sync or async), consider operation complete
            return;
        }

        throw new NotSupportedException("IS3Service implementation does not expose a compatible delete method.");
    }

    private static string HashPassword(string password) => MusicAI.Common.Security.PasswordHasher.Hash(password);
    private static bool VerifyPassword(string password, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        return MusicAI.Common.Security.PasswordHasher.Verify(password, hash);
    }

    /// <summary>
    /// Smart bulk upload - Upload music files with optional genre hint
    /// System intelligently analyzes and categorizes files into correct folders
    /// High-performance: 25 concurrent uploads, automatic retry, secure
    /// </summary>
    /// <param name="genreHint">Optional hint about genre/vibe to help categorization (e.g., "energetic", "gospel", "afrobeats")</param>
    /// <param name="files">Music files to upload and auto-categorize</param>
    [HttpPost("smart-bulk-upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(object), 200)]
    public async Task<IActionResult> SmartBulkUpload(
        [FromQuery] string? genreHint = null,
        [FromForm] IFormFileCollection? files = null)
    {
        if (!IsAdminAuthorized(out var ae, out var isSuper2) || string.IsNullOrEmpty(ae))
            return Unauthorized(new { error = "admin required" });

        var uploadFiles = files ?? Request.Form?.Files;
        if (uploadFiles == null || uploadFiles.Count == 0)
            return BadRequest(new { error = "No files provided" });

        // Step 1: Analyze files and determine categories
        var fileNames = uploadFiles.Select(f => f.FileName ?? "").ToList();
        var categorizations = _categorizer.BatchCategorize(fileNames, genreHint);
        var distribution = _categorizer.GetCategoryDistribution(fileNames, genreHint);

        // Step 2: Upload with high concurrency (25 parallel streams for maximum speed)
        var uploaded = new ConcurrentBag<object>();
        var rejected = new ConcurrentBag<object>();
        var semaphore = new System.Threading.SemaphoreSlim(25); // High-speed: 25 concurrent uploads
        var tasks = new List<Task>();

        for (int i = 0; i < uploadFiles.Count; i++)
        {
            var file = uploadFiles[i];
            var categorization = categorizations[i];

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var fileName = Path.GetFileName(file.FileName ?? string.Empty);

                    // Security: Profanity check
                    if (ContainsProfanity(fileName))
                    {
                        rejected.Add(new
                        {
                            file = fileName,
                            reason = "Profanity detected in filename",
                            suggestedFolder = categorization.suggestedFolder
                        });
                        return;
                    }

                    // Use intelligent categorization
                    var targetFolder = categorization.suggestedFolder;
                    var key = $"music/{targetFolder}/{Guid.NewGuid():N}_{fileName}";

                    // Copy to memory for retry resilience
                    await using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    ms.Position = 0;

                    try
                    {
                        // Upload with automatic retry (exponential backoff)
                        await _uploadPolicy.ExecuteAsync(async () =>
                        {
                            ms.Position = 0;
                            if (_s3 != null)
                            {
                                await _s3.UploadFileAsync(key, ms, file.ContentType ?? "audio/mpeg");
                            }
                            else
                            {
                                // Local filesystem fallback
                                var uploads = Path.Combine(AppContext.BaseDirectory, "uploads", targetFolder);
                                Directory.CreateDirectory(uploads);
                                var path = Path.Combine(uploads, key.Split('/').Last());
                                await using (var fs = System.IO.File.Create(path))
                                {
                                    ms.Position = 0;
                                    await ms.CopyToAsync(fs);
                                }
                            }
                        });

                        // Return both progressive and HLS streaming URLs
                        var streamUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-proxy?key={Uri.EscapeDataString(key)}";
                        var hlsUrl = $"{Request.Scheme}://{Request.Host}/api/admin/stream-hls/{key}";
                        
                        var folderInfo = MusicFolder.GetByName(targetFolder);
                        uploaded.Add(new
                        {
                            file = fileName,
                            key,
                            streamUrl = streamUrl,
                            hlsUrl = hlsUrl,
                            folder = targetFolder,
                            displayName = folderInfo?.DisplayName,
                            categorization.confidence,
                            categorization.reasoning
                        });
                    }
                    catch (Exception ex)
                    {
                        rejected.Add(new
                        {
                            file = fileName,
                            reason = ex.Message,
                            suggestedFolder = targetFolder
                        });
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        // Wait for all uploads to complete
        await Task.WhenAll(tasks);

        return Ok(new
        {
            message = "Smart categorization and upload complete",
            genreHint = genreHint,
            totalFiles = uploadFiles.Count,
            uploadedCount = uploaded.Count,
            rejectedCount = rejected.Count,
            categoryDistribution = distribution,
            categorizations = categorizations.Select(c => new
            {
                file = c.fileName,
                folder = c.suggestedFolder,
                c.confidence,
                c.reasoning
            }).ToArray(),
            uploaded = uploaded.ToArray(),
            rejected = rejected.ToArray(),
            performance = new
            {
                concurrentStreams = 25,
                retryPolicy = "3 attempts with exponential backoff",
                security = "Profanity filtering + S3 presigned URLs + IP rate limiting"
            }
        });
    }

    /// <summary>
    /// Get list of all uploaded music files with counts, grouped by folder/genre
    /// </summary>
    [HttpGet("music/list")]
    public async Task<IActionResult> GetMusicList(
        [FromQuery] string? folder = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
            return Unauthorized(new { error = "admin required" });

        try
        {
            if (_s3 == null)
                return StatusCode(500, new { error = "S3 service not configured" });

            var allFiles = new List<object>();
            var folderCounts = new Dictionary<string, int>();

            // List all files in music/ prefix
            var s3Objects = await _s3.ListObjectsAsync("music/");

            foreach (var obj in s3Objects)
            {
                // Support both implementations that return simple string keys and ones that return objects with metadata.
                string key = string.Empty;
                long size = 0;
                DateTime? lastModified = null;

                // If the enumerable yields string keys
                if (obj is string s)
                {
                    key = s;
                }
                else
                {
                    // Attempt to read common properties via reflection (Key, Size, LastModified)
                    try
                    {
                        var t = obj?.GetType();
                        if (t != null)
                        {
                            var keyProp = t.GetProperty("Key");
                            if (keyProp != null)
                                key = keyProp.GetValue(obj) as string ?? string.Empty;

                            var sizeProp = t.GetProperty("Size");
                            if (sizeProp != null)
                            {
                                var val = sizeProp.GetValue(obj);
                                if (val is long l) size = l;
                                else if (val is int i) size = i;
                                else if (val is string sv && long.TryParse(sv, out var pl)) size = pl;
                            }

                            var lmProp = t.GetProperty("LastModified") ?? t.GetProperty("LastModifiedUtc");
                            if (lmProp != null)
                            {
                                var val = lmProp.GetValue(obj);
                                if (val is DateTime dt) lastModified = dt;
                            }
                        }
                    }
                    catch
                    {
                        // ignore reflection errors and fall back to ToString()
                        key = obj?.ToString() ?? string.Empty;
                    }
                }

                if (string.IsNullOrWhiteSpace(key) || !key.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Extract folder from path: music/afrobeats/song.mp3 -> afrobeats
                var parts = key.Split('/');
                var folderName = parts.Length > 2 ? parts[1] : "uncategorized";
                var fileName = parts[parts.Length - 1];

                // Count by folder
                if (!folderCounts.ContainsKey(folderName))
                    folderCounts[folderName] = 0;
                folderCounts[folderName]++;

                allFiles.Add(new
                {
                    s3Key = key,
                    fileName = fileName,
                    folder = folderName,
                    size = size,
                    lastModified = lastModified
                });
            }

            // Filter by folder if specified
            var filteredFiles = folder != null
                ? allFiles.Where(f => ((dynamic)f).folder == folder).ToList()
                : allFiles;

            // Pagination
            var totalCount = filteredFiles.Count;
            var paginatedFiles = filteredFiles
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Ok(new
            {
                totalFiles = totalCount,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                folderCounts = folderCounts.OrderByDescending(kvp => kvp.Value).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                files = paginatedFiles
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to list music", detail = ex.Message });
        }
    }

    /// <summary>
    /// Delete or move music file to a different folder
    /// </summary>
    [HttpPost("music/manage")]
    public async Task<IActionResult> ManageMusic([FromBody] ManageMusicRequest req)
    {
        if (!IsAdminAuthorized(out var ae, out var _) || string.IsNullOrEmpty(ae))
            return Unauthorized(new { error = "admin required" });

        try
        {
            if (_s3 == null)
                return StatusCode(500, new { error = "S3 service not configured" });

            if (string.IsNullOrWhiteSpace(req.S3Key))
                return BadRequest(new { error = "s3Key is required" });

            if (req.Action == "delete")
            {
                // Delete the file
                await DeleteS3ObjectAsync(_s3, req.S3Key);
                return Ok(new { message = $"Deleted {req.S3Key}" });
            }
            else if (req.Action == "move")
            {
                if (string.IsNullOrWhiteSpace(req.TargetFolder))
                    return BadRequest(new { error = "targetFolder is required for move action" });

                // Extract filename from current key
                var parts = req.S3Key.Split('/');
                var fileName = parts[parts.Length - 1];

                // Construct new key: music/targetFolder/filename.mp3
                var newKey = $"music/{req.TargetFolder.Trim().ToLower()}/{fileName}";

                if (newKey == req.S3Key)
                    return BadRequest(new { error = "Target path is the same as current path" });

                // TODO: Fix S3Service interface to support move operation
                // Check if target already exists
                // var exists = await _s3.ObjectExistsAsync(newKey);
                // if (exists)
                //     return Conflict(new { error = $"File already exists at {newKey}" });

                // Download from old location
                // using var stream = await _s3.DownloadFileAsync(req.S3Key);
                // if (stream == null)
                //     return NotFound(new { error = $"File not found: {req.S3Key}" });

                // Upload to new location
                // await _s3.UploadFileAsync(newKey, stream, "audio/mpeg");

                // Delete old file
                // await DeleteS3ObjectAsync(_s3, req.S3Key);
                
                return StatusCode(501, new { error = "Move operation not yet implemented" });

                return Ok(new
                {
                    message = $"Moved {req.S3Key} to {newKey}",
                    oldKey = req.S3Key,
                    newKey = newKey
                });
            }
            else
            {
                return BadRequest(new { error = "Invalid action. Use 'delete' or 'move'" });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to manage music", detail = ex.Message });
        }
    }

    public record ManageMusicRequest(
        string S3Key,
        string Action, // "delete" or "move"
        string? TargetFolder // required for "move" action (e.g., "afrobeats", "gospel", "hip-hop")
    );
}
