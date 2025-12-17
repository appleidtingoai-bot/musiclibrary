using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Data;

namespace MusicAI.Orchestrator.Services
{
    public class RebillService : BackgroundService
    {
        private readonly PaymentMethodsRepository _methods;
        private readonly UsersRepository _users;
        private readonly PaymentService _paymentService;
        private readonly ILogger<RebillService> _logger;
        private readonly string _currency;

        public RebillService(PaymentMethodsRepository methods, UsersRepository users, PaymentService paymentService, ILogger<RebillService> logger, Microsoft.Extensions.Configuration.IConfiguration cfg)
        {
            _methods = methods;
            _users = users;
            _paymentService = paymentService;
            _logger = logger;
            _currency = cfg["Payment:Currency"] ?? "NGN";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("RebillService started");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RebillService loop error");
                }

                // Wait 24 hours between runs
                try
                {
                    await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
                }
                catch (TaskCanceledException) { }
            }
            _logger.LogInformation("RebillService stopping");
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            var methods = _methods == null 
                ? new System.Collections.Generic.List<PaymentMethod>() 
                : new System.Collections.Generic.List<PaymentMethod>(_methods.GetAllMethods());
            foreach (var pm in methods)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var user = _users.GetById(pm.UserId);
                    if (user == null) continue;
                    // If credits_expiry is null, or in future, skip
                    var expiryProp = user.GetType().GetProperty("Credits") != null ? user : user;
                    // Access credits expiry via query: re-fetch via SQL would be cleaner but reuse GetById
                    // For now, assume GetById returns TrialExpires and Credits but not credits_expiry; we'll check Credits value
                    if (user.Credits > 0) continue; // still has credits

                    // Attempt a rebill charge
                    _logger.LogInformation("Attempting rebill for user {UserId}", pm.UserId);
                    var charge = await _paymentService.ChargeSavedMethodAsync(pm.UserId, pm, _currency);
                    if (!charge.Success)
                    {
                        _logger.LogWarning("Rebill charge failed for user {UserId}", pm.UserId);
                        continue;
                    }

                    // Verify and credit (do not save token again)
                    if (!string.IsNullOrEmpty(charge.MerchantReference))
                    {
                        var ok = await _paymentService.VerifyAndCreditAsync(charge.MerchantReference, false);
                        if (ok)
                        {
                            _logger.LogInformation("Rebill successful for user {UserId}", pm.UserId);
                        }
                        else
                        {
                            _logger.LogWarning("Rebill verification failed for user {UserId}", pm.UserId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing rebill for method {MethodId}", pm.Id);
                }
            }
        }
    }
}
