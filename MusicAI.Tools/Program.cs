using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging((ctx, lb) =>
    {
        lb.ClearProviders();
        lb.AddConsole();
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddHostedService<UploadService>();
    })
    .Build();

await host.RunAsync();

