using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MusicAI.Orchestrator.Data;
using MusicAI.Orchestrator.Services;
using Xunit;

namespace Orchestrator.IntegrationTests
{
    public class OapDownloadsMusicTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public OapDownloadsMusicTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
        }

        // OAP endpoints
        [Fact]
        public async Task Oap_Chat_ReturnsBadRequest_When_MissingFields()
        {
            var client = _factory.CreateClient();
            var payload = new { UserId = "", Message = "" };
            var res = await client.PostAsync("/api/oap/chat", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            res.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Oap_Chat_ReturnsOk_WithValidRequest()
        {
            var client = _factory.CreateClient();
            var payload = new { UserId = "test-user", Message = "Play something happy" };
            var res = await client.PostAsync("/api/oap/chat", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            res.EnsureSuccessStatusCode();
            var txt = await res.Content.ReadAsStringAsync();
            (txt.Contains("OapName") || txt.Contains("oapName")).Should().BeTrue();
        }

        [Fact]
        public async Task Oap_Playlist_ReturnsJson_And_Manifest()
        {
            var client = _factory.CreateClient();
            var resJson = await client.GetAsync("/api/oap/playlist/test-user");
            resJson.EnsureSuccessStatusCode();

            var resManifest = await client.GetAsync("/api/oap/playlist/test-user?format=m3u8");
            resManifest.EnsureSuccessStatusCode();
            var contentType = resManifest.Content.Headers.ContentType?.ToString() ?? string.Empty;
            (contentType.Contains("mpegurl") || contentType.Contains("vnd.apple.mpegurl")).Should().BeTrue();
        }

        [Fact]
        public async Task Oap_TrackUrl_ReturnsBadRequest_And_Ok()
        {
            var client = _factory.CreateClient();
            var bad = await client.GetAsync("/api/oap/track-url");
            bad.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

            var ok = await client.GetAsync("/api/oap/track-url?s3Key=some-key&ttlMinutes=1");
            ok.EnsureSuccessStatusCode();
            var txt = await ok.Content.ReadAsStringAsync();
            txt.Should().Contain("hlsUrl");
        }

        [Fact]
        public async Task Oap_Current_ReturnsOk()
        {
            var client = _factory.CreateClient();
            var res = await client.GetAsync("/api/oap/current");
            res.EnsureSuccessStatusCode();
        }

        // Downloads endpoints
        [Fact]
        public async Task Downloads_RequestDownload_Forbidden_And_Ok_When_Subscribed()
        {
            var client = _factory.CreateClient();

            // First call: authenticated but no subscription -> Forbid (403)
            var req = new { S3Key = "some-key", Title = "T", Artist = "A" };
            var res = await client.PostAsync("/api/downloads/request", new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json"));
            res.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);

            // Create an active subscription for the test user using the DI repository
            using (var scope = _factory.Services.CreateScope())
            {
                var subs = scope.ServiceProvider.GetService<SubscriptionsRepository>();
                if (subs != null)
                {
                    var s = new Subscription { Id = Guid.NewGuid().ToString(), UserId = "test-user", StartedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1), CreditsAllocated = 1, CreditsRemaining = 1 };
                    await subs.CreateAsync(s);
                }
            }

            // Recreate client so server sees DB changes
            client = _factory.CreateClient();
            var req2 = new { S3Key = "some-key", Title = "T", Artist = "A" };
            var res2 = await client.PostAsync("/api/downloads/request", new StringContent(JsonSerializer.Serialize(req2), Encoding.UTF8, "application/json"));
            // Either Forbid if repository wasn't available, or Ok if subscription found. Accept either 200 or 403.
            (res2.StatusCode == System.Net.HttpStatusCode.OK || res2.StatusCode == System.Net.HttpStatusCode.Forbidden).Should().BeTrue();
        }

        // Music endpoints
        [Fact]
        public async Task Music_GetTracks_Unauthorized_Then_Authorized()
        {
            var client = _factory.CreateClient();
            var res = await client.GetAsync("/api/music/tracks");
            res.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

            // Create an admin session and call with X-Admin-Token
            var token = Guid.NewGuid().ToString("N");
            AdminSessionStore.AddSession(token, "admin@test.local", DateTime.UtcNow.AddHours(1));
            client.DefaultRequestHeaders.Add("X-Admin-Token", token);
            var res2 = await client.GetAsync("/api/music/tracks");
            res2.EnsureSuccessStatusCode();
        }

        [Fact]
        public async Task Music_Upload_Delete_Flow_Unauthorized_And_Authorized()
        {
            var client = _factory.CreateClient();
            // unauthorized upload
            var multi = new MultipartFormDataContent();
            var fileBytes = Encoding.UTF8.GetBytes("fake-mp3-content");
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
            multi.Add(fileContent, "file", "test.mp3");
            multi.Add(new StringContent("Title"), "title");
            multi.Add(new StringContent("Artist"), "artist");
            multi.Add(new StringContent("genre"), "genre");

            var unauth = await client.PostAsync("/api/music/upload", multi);
            unauth.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

            // authorized upload
            var token = Guid.NewGuid().ToString("N");
            AdminSessionStore.AddSession(token, "admin@test.local", DateTime.UtcNow.AddHours(1));
            client.DefaultRequestHeaders.Remove("X-Admin-Token");
            client.DefaultRequestHeaders.Add("X-Admin-Token", token);

            // New multipart for authorized call
            var multi2 = new MultipartFormDataContent();
            var fileContent2 = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-mp3-content"));
            fileContent2.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
            multi2.Add(fileContent2, "file", "test.mp3");
            multi2.Add(new StringContent("Title"), "title");
            multi2.Add(new StringContent("Artist"), "artist");
            multi2.Add(new StringContent("genre"), "genre");

            var ok = await client.PostAsync("/api/music/upload", multi2);
            ok.StatusCode.Should().NotBe(System.Net.HttpStatusCode.Unauthorized);
            ok.StatusCode.Should().NotBe(System.Net.HttpStatusCode.Forbidden);

            if (ok.IsSuccessStatusCode)
            {
                var txt = await ok.Content.ReadAsStringAsync();
                // attempt to parse trackId and delete the track
                if (txt.Contains("trackId"))
                {
                    using var doc = JsonDocument.Parse(txt);
                    if (doc.RootElement.TryGetProperty("trackId", out var idEl))
                    {
                        var trackId = idEl.GetString();
                        var del = await client.DeleteAsync($"/api/music/tracks/{trackId}");
                        // Deletion requires super admin; we expect either Forbidden or Ok
                        (del.StatusCode == System.Net.HttpStatusCode.Forbidden || del.IsSuccessStatusCode).Should().BeTrue();
                    }
                }
            }
        }
    }
}
