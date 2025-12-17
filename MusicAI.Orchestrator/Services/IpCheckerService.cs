using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MusicAI.Orchestrator.Services
{
    public record IpCheckResult(string Ip, string? Country, bool IsVpn, bool IsProxy, string? Provider, string? RawJson);

    public class IpCheckerService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _cfg;
        private readonly ILogger<IpCheckerService> _logger;

        public IpCheckerService(IHttpClientFactory httpFactory, IConfiguration cfg, ILogger<IpCheckerService> logger)
        {
            _httpFactory = httpFactory;
            _cfg = cfg;
            _logger = logger;
        }

        // Best-effort: try ipqualityscore if key present for VPN/proxy detection; otherwise fall back to ipinfo/ipapi
        public async Task<IpCheckResult> CheckIpAsync(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return new IpCheckResult(ip, null, false, false, null, null);
            try
            {
                var ipqsKey = _cfg["IpQualityScore:Key"] ?? Environment.GetEnvironmentVariable("IPQS_KEY");
                if (!string.IsNullOrEmpty(ipqsKey))
                {
                    try
                    {
                        var client = _httpFactory.CreateClient();
                        // strictness=1, fast=true reduces latency; adjust if you need deeper checks
                        var url = $"https://ipqualityscore.com/api/json/ip/{ipqsKey}/{ip}?strictness=1&fast=true";
                        var resp = await client.GetAsync(url);
                        var body = await resp.Content.ReadAsStringAsync();
                        if (resp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(body);
                            var root = doc.RootElement;
                            var country = root.TryGetProperty("country_code", out var cc) ? cc.GetString() : (root.TryGetProperty("country", out var c2) ? c2.GetString() : null);
                            var proxy = root.TryGetProperty("proxy", out var p) && p.GetBoolean();
                            var vpn = root.TryGetProperty("vpn", out var v) && v.GetBoolean();
                            var provider = root.TryGetProperty("organization", out var org) ? org.GetString() : null;
                            return new IpCheckResult(ip, country, vpn, proxy, provider, body);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IpQualityScore failed, falling back to other providers");
                    }
                }

                // Fallback: ipinfo.io (limited free tier). Try ipapi.co as second fallback.
                var client2 = _httpFactory.CreateClient();
                var ipinfoUrl = _cfg["IpInfo:Url"] ?? $"https://ipinfo.io/{ip}/json";
                try
                {
                    var r = await client2.GetAsync(ipinfoUrl);
                    var b = await r.Content.ReadAsStringAsync();
                    if (r.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(b);
                        var root = doc.RootElement;
                        var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
                        var org = root.TryGetProperty("org", out var o) ? o.GetString() : null;
                        // ipinfo doesn't reliably report VPN; use org heuristic to detect cloud/VPN hosting
                        var orgLower = org?.ToLowerInvariant() ?? string.Empty;
                        var isProxyOrVpn = orgLower.Contains("amazon") || orgLower.Contains("digitalocean") || orgLower.Contains("linode") || orgLower.Contains("ovh") || orgLower.Contains("cloudflare") || orgLower.Contains("vultr") || orgLower.Contains("hetzner");
                        return new IpCheckResult(ip, country, isProxyOrVpn, isProxyOrVpn, org, b);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ipinfo lookup failed");
                }

                // Final fallback: ipapi.co
                try
                {
                    var r2 = await client2.GetAsync($"https://ipapi.co/{ip}/json/");
                    var b2 = await r2.Content.ReadAsStringAsync();
                    if (r2.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(b2);
                        var root = doc.RootElement;
                        var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
                        var org = root.TryGetProperty("org", out var o) ? o.GetString() : null;
                        var orgLower = org?.ToLowerInvariant() ?? string.Empty;
                        var isProxy = orgLower.Contains("amazon") || orgLower.Contains("digitalocean") || orgLower.Contains("linode");
                        return new IpCheckResult(ip, country, false, isProxy, org, b2);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ipapi lookup failed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ip check failed");
            }
            return new IpCheckResult(ip, null, false, false, null, null);
        }
    }
}
