using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;
using MusicAI.Orchestrator.Controllers;
using MusicAI.Orchestrator.Data;
using MusicAI.Orchestrator.Services;
using Microsoft.AspNetCore.Mvc;

namespace Orchestrator.UnitTests
{
    public class DownloadsControllerTests
    {
        [Fact]
        public async Task RequestDownload_ReturnsForbid_When_NoActiveSubscription()
        {
            var cacheMock = new Mock<IDistributedCache>();
            var tokenSvcMock = new Mock<IStreamTokenService>();
            var loggerMock = new Mock<ILogger<DownloadsController>>();

            // Pass null for SubscriptionsRepository to simulate unavailable subscription backend
            var controller = new DownloadsController(cacheMock.Object, tokenSvcMock.Object, loggerMock.Object, null, null);

            // Arrange request with minimal values
            var req = new DownloadsController.DownloadRequest { S3Key = "some-key", Title = "T", Artist = "A" };

            // Need to set ControllerBase.User to a ClaimsPrincipal — but RequestDownload reads claims for userId
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext();
            var claims = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim("sub", "user-1") }, "test"));
            controller.ControllerContext.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = claims };

            var result = await controller.RequestDownload(req);

            result.Should().BeOfType<ForbidResult>();
        }

        [Fact]
        public async Task RequestDownload_ReturnsUnauthorized_When_Unauthenticated()
        {
            var cacheMock = new Mock<IDistributedCache>();
            var tokenSvcMock = new Mock<IStreamTokenService>();
            var loggerMock = new Mock<ILogger<DownloadsController>>();

            var controller = new DownloadsController(cacheMock.Object, tokenSvcMock.Object, loggerMock.Object, null, null);

            // Set an empty principal to simulate unauthenticated request (avoids null HttpContext.User)
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext();
            controller.ControllerContext.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()) };

            var req = new DownloadsController.DownloadRequest { S3Key = "some-key", Title = "T", Artist = "A" };

            var result = await controller.RequestDownload(req);

            result.Should().BeOfType<UnauthorizedResult>();
        }
    }
}
