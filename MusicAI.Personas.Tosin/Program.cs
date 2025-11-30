using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

// register HttpClient and LLM service which will use OPENAI_API_KEY if provided
builder.Services.AddHttpClient();
builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<PersonaService>();
builder.Services.AddSingleton<CustomTtsService>();

// Try to register optional infra services (S3 / Polly) if Infrastructure project is available
try
{
	builder.Services.AddSingleton<MusicAI.Infrastructure.Services.IS3Service, MusicAI.Infrastructure.Services.S3Service>();
	builder.Services.AddSingleton<MusicAI.Infrastructure.Services.PollyService>();
}
catch
{
	// ignore if infra assembly is missing or cannot be constructed
}

// News publishing: store + publisher + timed background worker
builder.Services.AddSingleton<NewsStore>();
builder.Services.AddSingleton<NewsPublisher>();
builder.Services.AddHostedService<TimedNewsService>();

var app = builder.Build();
app.MapGet("/", () => "Tosin Persona running");
app.MapControllers();
app.Run();
