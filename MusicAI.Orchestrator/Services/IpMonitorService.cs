using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MusicAI.Orchestrator.Data;

namespace MusicAI.Orchestrator.Services
{
    public class IpMonitorService : BackgroundService
    {
        private readonly ILogger<IpMonitorService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _interval;

        public IpMonitorService(ILogger<IpMonitorService> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _interval = TimeSpan.FromHours(6); // default every 6 hours
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("IpMonitorService starting");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var usersRepo = scope.ServiceProvider.GetRequiredService<UsersRepository>();
                    var ipChecker = scope.ServiceProvider.GetRequiredService<IpCheckerService>();

                    var users = usersRepo.GetUsersWithCredits();
                    _logger.LogInformation("IpMonitor: checking {count} users", users.Count());

                    foreach (var u in users)
                    {
                        try
                        {
                            var ipToCheck = string.IsNullOrWhiteSpace(u.IpAddress) ? u.LastSeenIp : u.IpAddress;
                            if (string.IsNullOrWhiteSpace(ipToCheck)) continue;
                            var result = await ipChecker.CheckIpAsync(ipToCheck);
                            usersRepo.UpdateLastSeenIp(u.Id, result.Ip, result.Country, result.IsVpn, result.IsProxy, DateTime.UtcNow);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "IpMonitor: failed for user {userId}", u.Id);
                        }

                        if (stoppingToken.IsCancellationRequested) break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IpMonitorService loop failure");
                }

                await Task.Delay(_interval, stoppingToken);
            }
            _logger.LogInformation("IpMonitorService stopping");
        }
    }
}
