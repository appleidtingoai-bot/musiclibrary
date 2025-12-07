using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using System.Text.Json;
using MusicAI.Common.Models;
using MusicAI.Orchestrator.Data;

[ApiController]
[Route("api/[controller]")]
public class OnboardingController : ControllerBase
{
    private readonly IHttpContextAccessor _httpAccessor;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _sp;
    private readonly UsersRepository _usersRepo;

    public OnboardingController(IHttpContextAccessor httpContextAccessor, IConfiguration configuration, IServiceProvider sp, UsersRepository usersRepo)
    {
        _httpAccessor = httpContextAccessor;
        _configuration = configuration;
        _sp = sp;
        _usersRepo = usersRepo;
        HttpContextAccessorHolder.HttpContextAccessor = httpContextAccessor;
    }

    private const int TrialCredits = 150; // trial credits for new users (7-day trial implied)

    public record RegisterRequest(string Email, string Password, string? Country = null);
    public record LoginRequest(string Email, string Password);
    public record LocationRequest(string Country);

    class UserInfo
    {
        public string Id { get; init; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public bool IsSubscribed { get; set; }
        public double Credits { get; set; }
        public string Country { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public DateTime TrialExpires { get; set; }
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public IActionResult Register([FromBody] RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Email and Password are required" });

        if (_usersRepo.GetByEmail(req.Email) != null)
            return Conflict(new { error = "User already exists" });

        var id = Guid.NewGuid().ToString();
        var hash = HashPassword(req.Password);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var user = new UserRecord
        {
            Id = id,
            Email = req.Email,
            PasswordHash = hash,
            IsSubscribed = false,
            Credits = TrialCredits,
            Country = req.Country ?? "",
            IpAddress = ip,
            TrialExpires = DateTime.UtcNow.AddDays(1) // Changed to 1 day trial
        };
        _usersRepo.Create(user);

        var role = "User";
        var (token, expires) = CreateJwtToken(id, role, isSubscribed: user.IsSubscribed);

        // Set auth cookie (contains JWT) for browser-based flows
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.None,
            Expires = expires
        };
        Response.Cookies.Append("MusicAI.Auth", token, cookieOptions);

        return CreatedAtAction(nameof(Me), new { id }, new { userId = id, token, role, credits = user.Credits, trialExpires = user.TrialExpires });
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        var u = _usersRepo.GetByEmail(req.Email);
        if (u == null) return Unauthorized(new { error = "Invalid credentials" });
        if (!VerifyPassword(req.Password, u.PasswordHash)) return Unauthorized(new { error = "Invalid credentials" });
        var role = "User";
        var (token, expires) = CreateJwtToken(u.Id, role, isSubscribed: u.IsSubscribed);

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.None,
            Expires = expires
        };
        Response.Cookies.Append("MusicAI.Auth", token, cookieOptions);

        return Ok(new { userId = u.Id, token, role, credits = u.Credits, isSubscribed = u.IsSubscribed });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        var user = GetUserFromToken(out var uid, out var role);
        if (user == null) return Unauthorized(new { error = "Invalid or missing token" });

        var fresh = _usersRepo.GetById(uid);
        if (fresh == null) return NotFound(new { error = "user not found" });

        var ip = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? fresh.IpAddress ?? "unknown";

        return Ok(new
        {
            userId = uid,
            email = fresh.Email,
            role,
            credits = fresh.Credits,
            country = fresh.Country,
            ipAddress = ip,
            isSubscribed = fresh.IsSubscribed
        });
    }

    /// <summary>
    /// Refresh token from HttpOnly cookie - extends user session by 7 days
    /// </summary>
    [AllowAnonymous]
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

            // Parse JWT manually to extract userId and validate
            var parts = token.Split('.');
            if (parts.Length != 3)
                return Unauthorized(new { error = "Invalid token format" });

            try
            {
                // Decode payload (base64url)
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                var payloadBytes = Convert.FromBase64String(payload);
                var payloadJson = Encoding.UTF8.GetString(payloadBytes);
                var payloadDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);

                if (payloadDict == null)
                    return Unauthorized(new { error = "Invalid token payload" });

                // Check expiration
                if (payloadDict.TryGetValue("exp", out var expElement))
                {
                    var exp = expElement.GetInt64();
                    var expDateTime = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
                    if (expDateTime < DateTime.UtcNow)
                    {
                        Response.Cookies.Delete("MusicAI.Auth");
                        return Unauthorized(new { error = "Token expired, please login again" });
                    }
                }

                // Extract userId
                var userId = payloadDict.TryGetValue("sub", out var subElement) ? subElement.GetString() : null;
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized(new { error = "Invalid token claims" });

                // Get user from database
                var user = _usersRepo.GetById(userId);
                if (user == null)
                    return Unauthorized(new { error = "User not found" });

                // Issue new token with extended expiry
                var role = "User";
                var (newToken, expires) = CreateJwtToken(userId, role, isSubscribed: user.IsSubscribed);

                // Update cookie with new token
                var cookieOptions = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.None,
                    Expires = expires
                };
                Response.Cookies.Append("MusicAI.Auth", newToken, cookieOptions);

                return Ok(new
                {
                    userId = user.Id,
                    token = newToken,
                    expires,
                    role,
                    credits = user.Credits,
                    isSubscribed = user.IsSubscribed,
                    email = user.Email
                });
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
    /// Logout - clears user authentication cookie
    /// </summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("MusicAI.Auth");
        return Ok(new { message = "Logged out successfully" });
    }

    [Authorize]
    [HttpPost("select-location")]
    public IActionResult SelectLocation([FromBody] LocationRequest req)
    {
        var user = GetUserFromToken(out var uid, out _);
        if (user == null) return Unauthorized(new { error = "Invalid or missing token" });
        var fresh = _usersRepo.GetById(uid);
        if (fresh == null) return NotFound(new { error = "user not found" });
        var country = req.Country ?? fresh.Country;
        _usersRepo.UpdateCountry(fresh.Id, country);
        return Ok(new { success = true, country });
    }

    [HttpGet("detect-ip")]
    public IActionResult DetectIp()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return Ok(new { ip });
    }

    // Admin token generator (simple dev helper). Provide `Admin:Secret` in configuration or env var `ADMIN_SECRET`.
    public record AdminTokenRequest(string Secret);

    [AllowAnonymous]
    [HttpPost("admin-token")]
    public IActionResult AdminToken([FromBody] AdminTokenRequest req)
    {
        var configured = _configuration["Admin:Secret"] ?? Environment.GetEnvironmentVariable("ADMIN_SECRET");
        if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(req.Secret) || req.Secret != configured)
            return Unauthorized(new { error = "Invalid admin secret" });

        var adminId = "admin";
        var (token, expires) = CreateJwtToken(adminId, "Admin", isSubscribed: true);

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.None,
            Expires = expires
        };
        Response.Cookies.Append("MusicAI.Auth", token, cookieOptions);

        return Ok(new { token, role = "Admin" });
    }

    // Helper methods
    private (string token, DateTime expires) CreateJwtToken(string userId, string role, bool isSubscribed = false)
    {
        // Manual JWT creation (HS256) to avoid compile-time dependency on IdentityModel types
        var key = _configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_secret_change_me";
        var issuer = _configuration["Jwt:Issuer"] ?? Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "MusicAI";
        var audience = _configuration["Jwt:Audience"] ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "MusicAIUsers";
        // Extended to 7 days for persistent sessions
        var expires = DateTime.UtcNow.AddDays(7);

        var header = new { alg = "HS256", typ = "JWT" };
        // Add extra entropy to the JWT payload so the resulting token is longer and harder to guess.
        // This also satisfies the developer request to produce a "longer" JWT for testing.
        var rndBytes = new byte[32];
        RandomNumberGenerator.Fill(rndBytes);
        var rndB64 = Convert.ToBase64String(rndBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var payload = new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["role"] = role,
            ["is_subscribed"] = isSubscribed,
            // use a longer jti with extra randomness
            ["jti"] = Guid.NewGuid().ToString() + "." + rndB64,
            // include raw randomness claim to lengthen the token
            ["rnd"] = rndB64,
            ["iss"] = issuer,
            ["aud"] = audience,
            ["exp"] = new DateTimeOffset(expires).ToUnixTimeSeconds()
        };

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);

        static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        var headerB = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var unsignedToken = headerB + "." + payloadB;

        var keyBytes = Encoding.UTF8.GetBytes(key);
        using var hmac = new HMACSHA256(keyBytes);
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken));
        var sigB = Base64UrlEncode(sig);

        var jwt = unsignedToken + "." + sigB;
        return (jwt, expires);
    }

    private UserInfo? GetUserFromToken(out string userId, out string? role)
    {
        userId = string.Empty;
        role = null;
        var ctx = HttpContextAccessorHolder.HttpContextAccessor?.HttpContext;
        if (ctx == null) return null;
        var principal = ctx.User;
        if (principal?.Identity == null || !principal.Identity.IsAuthenticated) return null;
        var uid = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
        role = principal.FindFirst(ClaimTypes.Role)?.Value;
        if (string.IsNullOrEmpty(uid)) return null;
        userId = uid;
        var record = _usersRepo.GetById(uid);
        if (record == null) return null;
        return new UserInfo
        {
            Id = record.Id,
            Email = record.Email,
            PasswordHash = record.PasswordHash,
            IsSubscribed = record.IsSubscribed,
            Credits = record.Credits,
            Country = record.Country,
            IpAddress = record.IpAddress,
            TrialExpires = record.TrialExpires
        };
    }

    private static string HashPassword(string password) => MusicAI.Common.Security.PasswordHasher.Hash(password);
    private static bool VerifyPassword(string password, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        return MusicAI.Common.Security.PasswordHasher.Verify(password, hash);
    }

    // Since controllers are created per-request, access HttpContext via a static accessor service
    private static class HttpContextAccessorHolder
    {
        public static IHttpContextAccessor? HttpContextAccessor { get; set; }
    }

    // Static constructor to ensure HttpContextAccessor is available when controller is used
    static OnboardingController()
    {
        // Try to get IHttpContextAccessor from DI at runtime; will be initialized by startup registration below
        // noop here; registration happens in Program.cs
    }

    // Return a playlist (proxied URLs) for all uploads in a genre. Requires user to be subscribed.
    [Authorize]
    [HttpGet("playlist")]
    public async Task<IActionResult> Playlist([FromQuery] string genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return BadRequest(new { error = "genre required" });
        var user = GetUserFromToken(out var uid, out _);
        if (user == null) return Unauthorized(new { error = "invalid token" });
        if (!user.IsSubscribed) return Forbid();

        // Resolve the S3 service dynamically so we don't depend on a specific method signature on the interface.
        dynamic s3 = _sp.GetService(typeof(MusicAI.Infrastructure.Services.IS3Service));
        if (s3 == null) return StatusCode(503, new { error = "storage not configured" });

        var prefix = $"genres/{genre}/";
        // Call the dynamically resolved service at runtime to avoid compile-time dependency on a specific method name.
        // The dynamic call may return various enumerable shapes, normalize to IEnumerable<string>.
        object resultObj = await s3.ListObjectsAsync(prefix);

        IEnumerable<string> keys;
        if (resultObj is IEnumerable<string> ks)
        {
            keys = ks;
        }
        else if (resultObj is IEnumerable<object> ko)
        {
            keys = ko.Select(o => o?.ToString() ?? string.Empty);
        }
        else
        {
            return StatusCode(503, new { error = "unexpected storage response" });
        }

        var urls = keys.Select(k => $"/api/admin/stream-proxy?key={Uri.EscapeDataString(k)}").ToArray();
        return Ok(new { count = urls.Length, urls });
    }

    // Forgot password endpoint: accepts email and a new password (in production, you'd send a reset link/token via email)
    // For now, this is a simple reset that requires the email to exist.
    public record ForgotPasswordRequest(string Email, string NewPassword);

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public IActionResult ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { error = "Email and NewPassword are required" });

        var user = _usersRepo.GetByEmail(req.Email);
        if (user == null)
            return NotFound(new { error = "User not found" });

        // Hash the new password and update
        var newHash = HashPassword(req.NewPassword);
        _usersRepo.UpdatePasswordHashByEmail(req.Email, newHash);

        return Ok(new { success = true, message = "Password updated successfully. You can now log in with your new password." });
    }

    // Change password endpoint: requires authentication and current password verification
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    [Authorize]
    [HttpPost("change-password")]
    public IActionResult ChangePassword([FromBody] ChangePasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CurrentPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { error = "CurrentPassword and NewPassword are required" });

        var user = GetUserFromToken(out var uid, out _);
        if (user == null)
            return Unauthorized(new { error = "Invalid or missing token" });

        var fresh = _usersRepo.GetById(uid);
        if (fresh == null)
            return NotFound(new { error = "User not found" });

        // Verify current password
        if (!VerifyPassword(req.CurrentPassword, fresh.PasswordHash))
            return Unauthorized(new { error = "Current password is incorrect" });

        // Hash and update new password
        var newHash = HashPassword(req.NewPassword);
        _usersRepo.UpdatePasswordHash(uid, newHash);

        return Ok(new { success = true, message = "Password changed successfully" });
    }
}
