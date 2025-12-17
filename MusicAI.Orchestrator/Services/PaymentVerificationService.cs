using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using MusicAI.Orchestrator.Data;
using Microsoft.Extensions.Configuration;
using System;

namespace MusicAI.Orchestrator.Services
{
    // Background service that periodically checks pending payments and credits users when completed.
    public class PaymentVerificationService : BackgroundService
    {
        private readonly PaymentsRepository _payments;
        private readonly PaymentService _paymentService;
        private readonly ILogger<PaymentVerificationService> _logger;
        private readonly IConfiguration _cfg;

        public PaymentVerificationService(PaymentsRepository payments, PaymentService paymentService, ILogger<PaymentVerificationService> logger, IConfiguration cfg)
        {
            _payments = payments;
            _paymentService = paymentService;
            _logger = logger;
            _cfg = cfg;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromSeconds(int.TryParse(_cfg["Payment:VerifyIntervalSeconds"], out var s) ? s : 30);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var pending = _payments.GetPendingOlderThan(TimeSpan.FromSeconds(10));
                    foreach (var p in pending)
                    {
                        try
                        {
                            _logger.LogInformation("Verifying payment {M}", p.MerchantReference);
                            await _paymentService.VerifyAndCreditAsync(p.MerchantReference);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error verifying payment {M}", p.MerchantReference);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Payment verification loop error");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
    }
}
