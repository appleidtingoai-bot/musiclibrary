using Microsoft.AspNetCore.Mvc;
using MusicAI.Orchestrator.Services;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Data;
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/payment")]
    public class PaymentController : ControllerBase
    {
        private readonly PaymentService _svc;
        private readonly PaymentsRepository _payments;
        private readonly PaymentMethodsRepository _methods;
        private readonly UsersRepository _users;
        private readonly ILogger<PaymentController> _logger;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;

        public PaymentController(PaymentService svc, PaymentsRepository payments, PaymentMethodsRepository methods, UsersRepository users, ILogger<PaymentController> logger, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _svc = svc;
            _payments = payments;
            _methods = methods;
            _users = users;
            _logger = logger;
            _configuration = configuration;
        }

        public record UserInitiateRequest(string UserId);

        [HttpPost("initiate")]
        public async Task<IActionResult> Initiate([FromBody] UserInitiateRequest? req)
        {
            // New flow: accept a userId in the request body and preload the user record server-side.
            if (req == null || string.IsNullOrEmpty(req.UserId)) return BadRequest(new { error = "userId required" });
            var userId = req.UserId;
            var user = _users.GetById(userId);
            if (user == null) return NotFound(new { error = "user not found" });

            // Disallow initiation for Nigeria IP users
            var country = (user.LastSeenCountry ?? user.Country ?? string.Empty).ToUpperInvariant();
            if (country.Contains("NIGERIA") || country == "NG" || country == "NGA")
            {
                return StatusCode(403, new { error = "you are yet due for payment" });
            }

            // Ensure user has credits expiry (non-null and in future) and credits > 0
            if (user.Credits <= 0 || !user.CreditsExpiry.HasValue || user.CreditsExpiry.Value < DateTime.UtcNow)
            {
                return StatusCode(402, new { error = "no valid credits or credits expired" });
            }

            // Derive name/email/phone/address from server-side user record (do not trust client payload)
            var firstName = user.FirstName;
            var lastName = user.LastName;
            var email = user.Email;
            var phone = string.IsNullOrWhiteSpace(user.Phone) ? user.IpAddress ?? string.Empty : user.Phone;
            var address = user.Country ?? string.Empty;
            // Determine currency by inferring from user's country
            string currency;
            if (country.Contains("NIGERIA") || country == "NG" || country == "NGA") currency = "NGN";
            else if (country.Contains("UNITED STATES") || country == "US" || country == "USA") currency = "USD";
            else if (country.Contains("UNITED KINGDOM") || country == "UK" || country == "GB") currency = "GBP";
            else if (country.Contains("GHANA") || country == "GHA") currency = "GHS";
            else currency = "USD";

            // If a tokenized payment method exists for this user, attempt a direct charge using the token.
            try
            {
                var pm = _methods.GetByUserId(userId);
                if (pm != null)
                {
                    var charge = await _svc.ChargeSavedMethodAsync(userId, pm, currency);
                    if (charge.Success)
                    {
                        // Return reference to the transaction; do NOT store raw card details anywhere.
                        return Ok(new { success = true, transactionReference = charge.TransactionReference });
                    }
                    // fall through to normal initiate if charge didn't succeed
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saved payment method charge attempt failed, falling back to checkout flow");
            }

            // Client-supplied amount and userId are ignored (server uses authenticated user and fixed $20)

            // Do NOT set or trust SaveToken at initiate time — SaveToken is applied only when payment is verified.

            // Ensure firstName and lastName are populated. Prefer explicit user properties; otherwise split email local-part.
            try
            {
                var fn = user.GetType().GetProperty("FirstName")?.GetValue(user) as string;
                var ln = user.GetType().GetProperty("LastName")?.GetValue(user) as string;
                if (!string.IsNullOrWhiteSpace(fn) || !string.IsNullOrWhiteSpace(ln))
                {
                    firstName = fn ?? string.Empty;
                    lastName = ln ?? string.Empty;
                }
                else
                {
                    var local = (email ?? string.Empty).Split('@')[0];
                    var tokens = local.Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length == 0)
                    {
                        firstName = local;
                        lastName = string.Empty;
                    }
                    else if (tokens.Length == 1)
                    {
                        firstName = tokens[0];
                        lastName = string.Empty;
                    }
                    else
                    {
                        firstName = tokens[0];
                        lastName = string.Join(" ", tokens.Skip(1));
                    }
                }

                // Initiate against the configured gateway and passthrough its raw JSON response
                var gatewayBody = await _svc.InitiatePurchaseRawAsync(userId, firstName, lastName, email, phone, address, currency);
                // Return the gateway response body verbatim with application/json
                return Content(gatewayBody, "application/json");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Payment:InitiateUrl"))
            {
                _logger.LogError(ex, "Payment initiate failed due to missing gateway config");
                return StatusCode(500, new { error = "payment gateway not configured" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment initiate error");
                return StatusCode(502, new { error = "initiate failed" });
            }
        }

        [HttpGet("verify/{merchantRef}")]
        public async Task<IActionResult> Verify(string merchantRef)
        {
            // Allow clients to pass either merchantRef (path) or referenceId (gateway transaction ref) as query
            var referenceId = Request.Query["referenceId"].FirstOrDefault();
            var saveTokenQuery = Request.Query["saveToken"].FirstOrDefault();
            var saveToken = string.Equals(saveTokenQuery, "true", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(referenceId))
            {
                var rec = _payments.GetByGatewayRef(referenceId);
                if (rec == null) return NotFound(new { success = false, error = "payment not found" });
                var ok = await _svc.VerifyAndCreditAsync(rec.MerchantReference, saveToken);
                return Ok(new { success = ok });
            }

            var ok2 = await _svc.VerifyAndCreditAsync(merchantRef, saveToken);
            return Ok(new { success = ok2 });
        }

        // Webhook endpoint for gateway notifications. Gateway will POST an encrypted payload.
        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook()
        {
            // Read raw body
            string body;
            using (var sr = new StreamReader(Request.Body, Encoding.UTF8))
            {
                body = await sr.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(body)) return BadRequest(new { error = "empty body" });

            // Try to extract the encrypted field from common names
            string? cipher = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var d)) cipher = d.GetString();
                if (string.IsNullOrEmpty(cipher) && root.TryGetProperty("payload", out var p)) cipher = p.GetString();
                if (string.IsNullOrEmpty(cipher) && root.TryGetProperty("cipherText", out var c)) cipher = c.GetString();
                if (string.IsNullOrEmpty(cipher) && root.TryGetProperty("encrypted", out var e)) cipher = e.GetString();
            }
            catch { }

            // If no wrapper found, assume the body itself is the base64 cipher
            if (string.IsNullOrEmpty(cipher)) cipher = body.Trim();

            // Decrypt using merchant public key configured in env
            var key = _configuration["Payment:WebhookKey"] ?? Environment.GetEnvironmentVariable("PAYMENT_MERCHANT_PUBLIC_KEY");
            if (string.IsNullOrEmpty(key)) return StatusCode(500, new { error = "webhook key not configured" });

            string decrypted;
            try
            {
                decrypted = DecryptString(cipher, key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook decrypt failed");
                return BadRequest(new { error = "decrypt failed" });
            }

            var processed = await _svc.ProcessWebhookNotificationAsync(decrypted);
            if (processed) return Ok(new { success = true });
            return Ok(new { success = false });
        }

        [HttpPost("consent")]
        public IActionResult SetConsent([FromBody] ConsentRequest req)
        {
            // Require authentication
            string? token = null;
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = authHeader.Substring("Bearer ".Length).Trim();
            else if (Request.Cookies.TryGetValue("MusicAI.Auth", out var c)) token = c;
            if (string.IsNullOrEmpty(token)) return Unauthorized();

            System.Security.Claims.ClaimsPrincipal principal;
            try
            {
                var jwtKey = _configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_SECRET") ?? string.Empty;
                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                var parameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = !string.IsNullOrEmpty(jwtKey),
                    IssuerSigningKey = !string.IsNullOrEmpty(jwtKey) ? new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtKey)) : null,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                principal = handler.ValidateToken(token, parameters, out var validatedToken);
            }
            catch { return Unauthorized(); }

            var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            _users.UpdateSavePaymentConsent(userId, req.Save);
            return Ok(new { success = true, save = req.Save });
        }

        [HttpGet("methods")]
        public IActionResult GetMethods()
        {
            string? token = null;
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = authHeader.Substring("Bearer ".Length).Trim();
            else if (Request.Cookies.TryGetValue("MusicAI.Auth", out var c)) token = c;
            if (string.IsNullOrEmpty(token)) return Unauthorized();

            System.Security.Claims.ClaimsPrincipal principal;
            try
            {
                var jwtKey = _configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_SECRET") ?? string.Empty;
                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                var parameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = !string.IsNullOrEmpty(jwtKey),
                    IssuerSigningKey = !string.IsNullOrEmpty(jwtKey) ? new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtKey)) : null,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                principal = handler.ValidateToken(token, parameters, out var validatedToken);
            }
            catch { return Unauthorized(); }

            var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var pm = _methods.GetByUserId(userId);
            if (pm == null) return Ok(new { methods = Array.Empty<object>() });
            return Ok(new { methods = new[] { new { provider = pm.Provider, last4 = pm.Last4, createdAt = pm.CreatedAt } } });
        }

        [HttpPost("methods/revoke")]
        public IActionResult RevokeMethods()
        {
            string? token = null;
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = authHeader.Substring("Bearer ".Length).Trim();
            else if (Request.Cookies.TryGetValue("MusicAI.Auth", out var c)) token = c;
            if (string.IsNullOrEmpty(token)) return Unauthorized();

            System.Security.Claims.ClaimsPrincipal principal;
            try
            {
                var jwtKey = _configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_SECRET") ?? string.Empty;
                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                var parameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = !string.IsNullOrEmpty(jwtKey),
                    IssuerSigningKey = !string.IsNullOrEmpty(jwtKey) ? new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtKey)) : null,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                principal = handler.ValidateToken(token, parameters, out var validatedToken);
            }
            catch { return Unauthorized(); }

            var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            _methods.DeleteByUserId(userId);
            _users.UpdateSavePaymentConsent(userId, false);
            return Ok(new { success = true });
        }

        public class ConsentRequest { public bool Save { get; set; } = false; }

        // Provided decryption helper (AES, merchant public key as keyString - must be 16 bytes)
        public static string DecryptString(string cipherText, string keyString)
        {
            byte[] fullCipher = Convert.FromBase64String(cipherText);
            try
            {
                using (Aes aesAlg = Aes.Create())
                {
                    aesAlg.Padding = PaddingMode.PKCS7;
                    var keyBytes = Encoding.UTF8.GetBytes(keyString);
                    if (keyBytes.Length != aesAlg.KeySize / 8)
                    {
                        // Ensure 16 bytes for AES-128 by truncating or padding with zeros
                        var kb = new byte[aesAlg.KeySize / 8];
                        Array.Copy(keyBytes, kb, Math.Min(kb.Length, keyBytes.Length));
                        keyBytes = kb;
                    }
                    aesAlg.Key = keyBytes;
                    byte[] iv = new byte[aesAlg.IV.Length];
                    Array.Copy(fullCipher, 0, iv, 0, iv.Length);
                    using (ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, iv))
                    {
                        string result;
                        using (MemoryStream msDecrypt = new MemoryStream())
                        {
                            using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Write))
                            {
                                csDecrypt.Write(fullCipher, iv.Length, fullCipher.Length - iv.Length);
                            }
                            result = Encoding.UTF8.GetString(msDecrypt.ToArray());
                        }
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                return string.IsNullOrWhiteSpace(ex.Message)?ex?.InnerException.Message:ex.Message;
            }
        }

        public class InitiateRequest
        {
            // Do NOT accept or trust UserId or Amount from clients; server derives user from JWT and amount is fixed ($20)
              // Only allow phone/address and the client-side consent flag. All other
              // fields are filled from the server-side user record.
              public string CustomerPhone { get; set; } = string.Empty;
              public string CustomerAddress { get; set; } = string.Empty;
              // Note: client should not send SaveToken here; saving consent is handled via /api/payment/consent
        }
    }
}
