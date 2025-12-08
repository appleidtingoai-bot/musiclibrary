using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Services;

namespace MusicAI.Orchestrator.Controllers
{
    [ApiController]
    [Route("api/oap")]
    [Authorize]
    public class OapControlController : ControllerBase
    {
        private readonly IPlayerService _player;
        private readonly ILogger<OapControlController> _logger;

        public OapControlController(IPlayerService player, ILogger<OapControlController> logger)
        {
            _player = player;
            _logger = logger;
        }

        [AllowAnonymous]
        [HttpPost("control/{userId}/play")]
        public async Task<IActionResult> Play(string userId)
        {
            var s = await _player.Play(userId);
            return Ok(s);
        }

        [AllowAnonymous]
        [HttpPost("control/{userId}/pause")]
        public async Task<IActionResult> Pause(string userId)
        {
            var s = await _player.Pause(userId);
            return Ok(s);
        }

        [AllowAnonymous]
        [HttpPost("control/{userId}/skip")]
        public async Task<IActionResult> Skip(string userId)
        {
            var s = await _player.Skip(userId);
            return Ok(s);
        }

        [AllowAnonymous]
        [HttpPost("control/{userId}/previous")]
        public async Task<IActionResult> Previous(string userId)
        {
            var s = await _player.Previous(userId);
            return Ok(s);
        }

        [AllowAnonymous]
        [HttpPut("control/{userId}/volume")]
        public async Task<IActionResult> Volume(string userId, [FromBody] JsonElement body)
        {
            try
            {
                var vol = body.GetProperty("volume").GetDouble();
                var s = await _player.SetVolume(userId, vol);
                return Ok(s);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "volume missing or invalid", detail = ex.Message });
            }
        }

        [AllowAnonymous]
        [HttpPost("control/{userId}/shuffle")]
        public async Task<IActionResult> Shuffle(string userId, [FromBody] JsonElement body)
        {
            var enable = true;
            try { if (body.TryGetProperty("enable", out var p)) enable = p.GetBoolean(); } catch { }
            var s = await _player.Shuffle(userId, enable);
            return Ok(s);
        }

        // Lightweight WebSocket endpoint for real-time state sync
        // Supports JWT via Authorization header or token query param for browser compatibility.
        [AllowAnonymous]
        [HttpGet("ws/{userId}")]
        public async Task GetWs(string userId)
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                HttpContext.Response.StatusCode = 400;
                return;
            }

            // Optional JWT validation: accept Authorization: Bearer or ?token= for browsers
            try
            {
                string? token = null;
                if (Request.Headers.TryGetValue("Authorization", out var authHeader) && authHeader.Count > 0)
                {
                    var raw = authHeader.ToString();
                    if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        token = raw.Substring("Bearer ".Length).Trim();
                    }
                }
                if (string.IsNullOrEmpty(token))
                {
                    token = Request.Query.ContainsKey("token") ? Request.Query["token"].ToString() : null;
                }

                if (!string.IsNullOrEmpty(token))
                {
                    var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_jwt_secret_minimum_32_characters_required_for_hs256";
                    var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret));
                    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                    var parms = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = key,
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(30)
                    };
                    try
                    {
                        var principal = handler.ValidateToken(token, parms, out var validated);
                        var sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                        if (!string.IsNullOrEmpty(sub) && sub != userId && !principal.IsInRole("Admin") && !principal.IsInRole("SuperAdmin"))
                        {
                            HttpContext.Response.StatusCode = 403;
                            return;
                        }
                        // Attach principal to HttpContext so downstream can inspect User
                        HttpContext.User = principal;
                    }
                    catch
                    {
                        // Invalid token: reject
                        HttpContext.Response.StatusCode = 401;
                        return;
                    }
                }
            }
            catch { }

            using var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
            var cts = new CancellationTokenSource();

            void OnState(object? sender, (string userId, PlayerStateDto state) ev)
            {
                if (ev.userId != userId) return;
                try
                {
                    var txt = JsonSerializer.Serialize(ev.state);
                    var bytes = Encoding.UTF8.GetBytes(txt);
                    ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                }
                catch { }
            }

            _player.StateChanged += OnState;

            // Send initial state
            var init = _player.GetState(userId);
            var initBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(init));
            await ws.SendAsync(new ArraySegment<byte>(initBytes), WebSocketMessageType.Text, true, CancellationToken.None);

            var buffer = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    // Expect simple JSON commands: { "cmd": "play" } or { "cmd":"skip" }
                    try
                    {
                        using var doc = JsonDocument.Parse(msg);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("cmd", out var cmd))
                        {
                            var c = cmd.GetString()?.ToLowerInvariant();
                            switch (c)
                            {
                                case "play": await _player.Play(userId); break;
                                case "pause": await _player.Pause(userId); break;
                                case "skip": await _player.Skip(userId); break;
                                case "previous": await _player.Previous(userId); break;
                                case "shuffle": {
                                    var enable = true; if (root.TryGetProperty("enable", out var p)) enable = p.GetBoolean();
                                    await _player.Shuffle(userId, enable);
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                _player.StateChanged -= OnState;
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
            }
        }
    }
}
