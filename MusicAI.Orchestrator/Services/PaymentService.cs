using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Data;

namespace MusicAI.Orchestrator.Services
{
    public class PaymentInitResult
    {
        public bool Success { get; set; }
        public string? CheckoutUrl { get; set; }
        public string? TransactionReference { get; set; }
        public string? AccessCode { get; set; }
        public string? Message { get; set; }
        public string? TransactionId { get; set; }
        public string? MerchantReference { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
    }

    public class PaymentService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _cfg;
        private readonly PaymentsRepository _payments;
        private readonly UsersRepository _users;
        private readonly MusicAI.Orchestrator.Data.PaymentMethodsRepository _methods;
        private readonly ILogger<PaymentService> _logger;

        public PaymentService(IHttpClientFactory httpFactory, IConfiguration cfg, PaymentsRepository payments, UsersRepository users, MusicAI.Orchestrator.Data.PaymentMethodsRepository methods, ILogger<PaymentService> logger)
        {
            _httpFactory = httpFactory;
            _cfg = cfg;
            _payments = payments;
            _users = users;
            _methods = methods;
            _logger = logger;
        }

        // Convert USD 20 to target currency using exchangerate.host; fallback to given amount
        private async Task<decimal> AmountForCurrencyAsync(string currency)
        {
            if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)) return 20m;
            try
            {
                var client = _httpFactory.CreateClient();
                var url = $"https://api.exchangerate.host/convert?from=USD&to={currency}&amount=20";
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return 20m;
                var j = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (j.RootElement.TryGetProperty("result", out var r)) return r.GetDecimal();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exchange rate lookup failed, falling back to 20");
            }
            return 20m;
        }

        public async Task<PaymentInitResult> InitiatePurchaseAsync(string userId, string firstName, string lastName, string email, string phone, string address, string currency, bool saveToken = false)
        {
            var gatewayUrl = _cfg["Payment:InitiateUrl"];
            var amount = await AmountForCurrencyAsync(currency);

            // Require a configured gateway in all environments for initiating purchases
            if (string.IsNullOrEmpty(gatewayUrl))
            {
                throw new InvalidOperationException("Payment:InitiateUrl not configured");
            }
            var merchantTransactionReference = "TINGO-" + Guid.NewGuid().ToString("N");
            var amountInt = (int)Math.Ceiling(amount);
            var payload = new
            {
                amount = amountInt, // integer minor units expected by gateway sample
                currency = currency.ToUpper(),
                customerFirstName = firstName,
                customerLastName = lastName,
                customerEmail = email,
                customerPhone = phone,
                customerAddress = address,
                merchantTransactionReference = merchantTransactionReference,
                customFields = new[] { new { name = "userId", value = userId } }
            };

            var client = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Post, gatewayUrl);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            // Attach gateway auth headers if configured
            ApplyGatewayAuth(req);
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Payment initiate failed: {Status}", resp.StatusCode);
                return new PaymentInitResult { Success = false };
            }

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("success", out var s) ? s.GetBoolean() : false;
            if (!ok) return new PaymentInitResult { Success = false };

            root.TryGetProperty("checkoutUrl", out var checkoutUrlEl);
            root.TryGetProperty("transactionReference", out var txnRefEl);
            root.TryGetProperty("accessCode", out var accessCodeEl);
            root.TryGetProperty("message", out var messageEl);
            root.TryGetProperty("transactionId", out var transactionIdEl);

            var checkoutUrl = checkoutUrlEl.GetString();
            var txnRef = txnRefEl.GetString();
            var accessCode = accessCodeEl.GetString();
            var message = messageEl.GetString();
            var transactionId = transactionIdEl.GetString();

            var record = new PaymentRecord
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                MerchantReference = merchantTransactionReference,
                GatewayReference = txnRef ?? string.Empty,
                Amount = (decimal)amountInt,
                Currency = currency.ToUpper(),
                Status = "pending",
                CheckoutUrl = checkoutUrl,
                AccessCode = accessCode,
                SaveTokenRequested = saveToken,
                CreatedAt = DateTime.UtcNow
            };
            _payments.Insert(record);

            return new PaymentInitResult
            {
                Success = true,
                CheckoutUrl = checkoutUrl,
                TransactionReference = txnRef,
                AccessCode = accessCode,
                MerchantReference = merchantTransactionReference,
                Message = message,
                TransactionId = transactionId
                ,Amount = amountInt
                ,Currency = currency.ToUpper()
            };
        }

        // Send the actual gateway payload and return raw JSON response body (for passthrough to clients)
        public async Task<string> InitiatePurchaseRawAsync(string userId, string firstName, string lastName, string email, string phone, string address, string currency)
        {
            var gatewayUrl = _cfg["Payment:InitiateUrl"] ?? throw new InvalidOperationException("Payment:InitiateUrl not configured");
            var amount = await AmountForCurrencyAsync(currency);
            var merchantTransactionReference = "TINGO-" + Guid.NewGuid().ToString("N");
            var amountInt = (int)Math.Ceiling(amount);

            var payload = new
            {
                amount = amountInt,
                currency = currency.ToUpper(),
                customerFirstName = firstName,
                customerLastName = lastName,
                customerEmail = email,
                customerPhone = phone,
                customerAddress = address,
                merchantTransactionReference = merchantTransactionReference,
                customFields = new[] { new { name = "userId", value = userId } }
            };

            var client = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Post, gatewayUrl);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            // Attach gateway auth headers if configured
            ApplyGatewayAuth(req);
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            // Store a payment record locally (pending) for later verification
            var record = new PaymentRecord
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                MerchantReference = merchantTransactionReference,
                GatewayReference = string.Empty,
                Amount = (decimal)amountInt,
                Currency = currency.ToUpper(),
                Status = resp.IsSuccessStatusCode ? "pending" : "failed",
                CheckoutUrl = null,
                AccessCode = null,
                // Do NOT record client-provided save token at initiate time; saving consent occurs at verification/webhook
                SaveTokenRequested = false,
                CreatedAt = DateTime.UtcNow
            };
            try { _payments.Insert(record); } catch { /* non-fatal */ }

            return body;
        }

        public async Task<bool> VerifyAndCreditAsync(string merchantReference, bool saveToken = false)
        {
            var gatewayVerifyBase = _cfg["Payment:VerifyBaseUrl"] ?? throw new InvalidOperationException("Payment:VerifyBaseUrl not configured");
            var rec = _payments.GetByMerchantRef(merchantReference);
            if (rec == null) return false;
            var client = _httpFactory.CreateClient();
            var vurl = gatewayVerifyBase.TrimEnd('/') + "/" + (rec.GatewayReference ?? merchantReference);
            var resp = await client.GetAsync(vurl);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("paymentStatus").GetString();
            // Try to extract token information if present
            string? token = null;
            string? last4 = null;
            if (doc.RootElement.TryGetProperty("paymentToken", out var pt)) token = pt.GetString();
            if (doc.RootElement.TryGetProperty("token", out var t2) && string.IsNullOrEmpty(token)) token = t2.GetString();
            if (doc.RootElement.TryGetProperty("cardLast4", out var l4)) last4 = l4.GetString();
            if (doc.RootElement.TryGetProperty("last4", out var l42) && string.IsNullOrEmpty(last4)) last4 = l42.GetString();
            if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                // credit user $20 equivalent in their currency: use USD base for credit amount
                var creditAmountUsd = 20.0;
                var expiry = DateTime.UtcNow.AddDays(29);
                // store credits as USD-equivalent value in user's Credits (for simplicity)
                _users.AddCredits(rec.UserId, creditAmountUsd, expiry);
                _payments.UpdateStatus(rec.Id, "success", rec.GatewayReference);
                // Persist token if available and if either caller requested saving or payment record requested saving
                try
                {
                    var shouldSave = saveToken || rec.SaveTokenRequested;
                    if (shouldSave && !string.IsNullOrEmpty(token))
                    {
                        var pm = new MusicAI.Orchestrator.Data.PaymentMethod
                        {
                            UserId = rec.UserId,
                            Provider = _cfg["Payment:Provider"] ?? "gateway",
                            Token = token,
                            Last4 = last4
                        };
                        // Insert but ignore errors (non-blocking)
                        try { _methods.Insert(pm); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Could not persist payment method token"); }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while attempting to persist token (non-fatal)");
                }
                return true;
            }
            else if (string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            else
            {
                _payments.UpdateStatus(rec.Id, "failed", rec.GatewayReference);
                return false;
            }
        }

        // Process webhook JSON (already decrypted) from gateway: update payment status and credit user immediately on success
        public async Task<bool> ProcessWebhookNotificationAsync(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? merchantRef = null;
                string? gatewayRef = null;
                string? status = null;

                if (root.TryGetProperty("merchantTransactionReference", out var mtr)) merchantRef = mtr.GetString();
                if (root.TryGetProperty("merchantReference", out var mtr2) && string.IsNullOrEmpty(merchantRef)) merchantRef = mtr2.GetString();
                if (root.TryGetProperty("transactionReference", out var tr)) gatewayRef = tr.GetString();
                if (root.TryGetProperty("paymentReference", out var pr) && string.IsNullOrEmpty(gatewayRef)) gatewayRef = pr.GetString();
                if (root.TryGetProperty("paymentStatus", out var ps)) status = ps.GetString();
                if (root.TryGetProperty("status", out var s2) && string.IsNullOrEmpty(status)) status = s2.GetString();

                PaymentRecord? rec = null;
                if (!string.IsNullOrEmpty(merchantRef)) rec = _payments.GetByMerchantRef(merchantRef);
                if (rec == null && !string.IsNullOrEmpty(gatewayRef)) rec = _payments.GetByGatewayRef(gatewayRef);
                if (rec == null)
                {
                    _logger.LogWarning("Webhook: payment record not found (merchantRef={MerchantRef} gatewayRef={Gw})", merchantRef, gatewayRef);
                    return false;
                }

                if (!string.IsNullOrEmpty(status) && (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)))
                {
                    // credit user and mark success
                    var creditAmountUsd = 20.0;
                    var expiry = DateTime.UtcNow.AddDays(29);
                    _users.AddCredits(rec.UserId, creditAmountUsd, expiry);
                    _payments.UpdateStatus(rec.Id, "success", gatewayRef);
                        // Persist token if available and if either caller requested saving or payment record requested saving
                        try
                        {
                            string? token = null;
                            string? last4 = null;
                            if (root.TryGetProperty("paymentToken", out var pt)) token = pt.GetString();
                            if (root.TryGetProperty("token", out var t2) && string.IsNullOrEmpty(token)) token = t2.GetString();
                            if (root.TryGetProperty("cardLast4", out var l4)) last4 = l4.GetString();
                            if (root.TryGetProperty("last4", out var l42) && string.IsNullOrEmpty(last4)) last4 = l42.GetString();

                            var shouldSave = rec.SaveTokenRequested;
                            var user = _users.GetById(rec.UserId);
                            if (user != null && user.GetType().GetProperty("SavePaymentConsent") != null)
                            {
                                var consentVal = user.GetType().GetProperty("SavePaymentConsent")?.GetValue(user);
                                if (consentVal is bool b) shouldSave = shouldSave || b;
                            }

                            if (shouldSave && !string.IsNullOrEmpty(token))
                            {
                                var pm = new MusicAI.Orchestrator.Data.PaymentMethod
                                {
                                    UserId = rec.UserId,
                                    Provider = _cfg["Payment:Provider"] ?? "gateway",
                                    Token = token,
                                    Last4 = last4
                                };
                                try { _methods.Insert(pm); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Could not persist payment method token from webhook"); }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error persisting token from webhook (non-fatal)");
                        }
                        return true;
                }
                else if (!string.IsNullOrEmpty(status) && string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    _payments.UpdateStatus(rec.Id, "failed", gatewayRef);
                    return false;
                }

                // Unknown/Pending status - leave as-is
                _logger.LogInformation("Webhook: received non-final status for payment {Id}: {Status}", rec.Id, status);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process webhook payload");
                return false;
            }
        }

        // Charge a previously saved/tokenized payment method (no raw card data stored)
        public async Task<PaymentInitResult> ChargeSavedMethodAsync(string userId, Data.PaymentMethod pm, string currency)
        {
            var gatewayChargeUrl = _cfg["Payment:ChargeUrl"] ?? _cfg["Payment:InitiateUrl"] ?? throw new InvalidOperationException("Payment:ChargeUrl not configured");
            var amount = await AmountForCurrencyAsync(currency);

            var merchantRef = "TINGO-" + Guid.NewGuid().ToString("N");
            var payload = new
            {
                amount = (int)Math.Ceiling(amount),
                currency = currency.ToUpper(),
                paymentToken = pm.Token,
                merchantTransactionReference = merchantRef,
                customFields = new[] { new { name = "userId", value = userId } }
            };

            var client = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Post, gatewayChargeUrl);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            // Attach gateway auth headers if configured
            ApplyGatewayAuth(req);
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Payment charge (saved method) failed: {Status}", resp.StatusCode);
                return new PaymentInitResult { Success = false };
            }

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("success", out var s) ? s.GetBoolean() : true;
            var txnRef = root.TryGetProperty("transactionReference", out var t) ? t.GetString() : null;

            var record = new PaymentRecord
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                MerchantReference = merchantRef,
                GatewayReference = txnRef ?? string.Empty,
                Amount = (decimal)Math.Ceiling(amount),
                Currency = currency.ToUpper(),
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };
            _payments.Insert(record);

            return new PaymentInitResult { Success = ok, TransactionReference = txnRef, MerchantReference = merchantRef };
        }

        private void ApplyGatewayAuth(HttpRequestMessage req)
        {
            try
            {
                // Common options: Payment:ApiKey -> X-API-Key header; Payment:BearerToken -> Authorization: Bearer <token>
                var apiKey = _cfg["Payment:ApiKey"] ?? Environment.GetEnvironmentVariable("PAYMENT__APIKEY") ?? Environment.GetEnvironmentVariable("PAYMENT_APIKEY");
                if (!string.IsNullOrEmpty(apiKey))
                {
                    if (!req.Headers.Contains("X-API-Key")) req.Headers.Add("X-API-Key", apiKey);
                }

                var bearer = _cfg["Payment:BearerToken"] ?? Environment.GetEnvironmentVariable("PAYMENT__BEARERTOKEN") ?? Environment.GetEnvironmentVariable("PAYMENT_BEARERTOKEN");
                if (!string.IsNullOrEmpty(bearer))
                {
                    if (!req.Headers.Contains("Authorization")) req.Headers.Add("Authorization", "Bearer " + bearer);
                }

                // Custom header pair
                var hdrName = _cfg["Payment:AuthHeaderName"] ?? Environment.GetEnvironmentVariable("PAYMENT__AUTHHEADERNAME");
                var hdrValue = _cfg["Payment:AuthHeaderValue"] ?? Environment.GetEnvironmentVariable("PAYMENT__AUTHHEADERVALUE");
                if (!string.IsNullOrEmpty(hdrName) && !string.IsNullOrEmpty(hdrValue))
                {
                    if (!req.Headers.Contains(hdrName)) req.Headers.Add(hdrName, hdrValue);
                }

                // Basic auth support: Payment:BasicUser & Payment:BasicPassword (or env PAYMENT__BASICUSER / PAYMENT__BASICPASSWORD)
                var basicUser = _cfg["Payment:BasicUser"] ?? Environment.GetEnvironmentVariable("PAYMENT__BASICUSER") ?? Environment.GetEnvironmentVariable("PAYMENT_BASICUSER");
                var basicPass = _cfg["Payment:BasicPassword"] ?? Environment.GetEnvironmentVariable("PAYMENT__BASICPASSWORD") ?? Environment.GetEnvironmentVariable("PAYMENT_BASICPASSWORD");
                if (!string.IsNullOrEmpty(basicUser) && !string.IsNullOrEmpty(basicPass))
                {
                    if (!req.Headers.Contains("Authorization") && string.IsNullOrEmpty(req.Headers.Authorization?.ToString()))
                    {
                        try
                        {
                            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(basicUser + ":" + basicPass));
                            req.Headers.Add("Authorization", "Basic " + token);
                        }
                        catch { /* non-fatal */ }
                    }
                }
            }
            catch { }
        }
    }
}
