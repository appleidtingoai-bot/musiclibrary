using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Orchestrator.IntegrationTests
{
    public class OapEndpointsTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public OapEndpointsTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Health_ReturnsOk()
        {
            var client = _factory.CreateClient();
            var res = await client.GetAsync("/api/oap/health");
            res.EnsureSuccessStatusCode();
            var txt = await res.Content.ReadAsStringAsync();
            txt.Should().Contain("ok");
        }

        [Fact]
        public async Task Current_ReturnsOk()
        {
            var client = _factory.CreateClient();
            var res = await client.GetAsync("/api/oap/current");
            res.EnsureSuccessStatusCode();
            var txt = await res.Content.ReadAsStringAsync();
            txt.Should().Contain("oap");
        }

        [Fact]
        public async Task TrackUrl_ReturnsBadRequest_When_MissingKey()
        {
            var client = _factory.CreateClient();
            var res = await client.GetAsync("/api/oap/track-url");
            res.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }
    }
}
