using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MusicAI.Common.Models;
using MusicAI.Infrastructure.Services;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using MusicAI.Orchestrator.Middleware;

// Load local .env into environment variables so running in local dev picks up AWS/Postgres keys
// Check multiple locations: output directory, current directory, and parent directory (solution root)
string? dotenvPath = null;
var possiblePaths = new[]
{
    Path.Combine(AppContext.BaseDirectory, ".env"),
    Path.Combine(Directory.GetCurrentDirectory(), ".env"),
    Path.Combine(Directory.GetCurrentDirectory(), "..", ".env")
};

foreach (var path in possiblePaths)
{
    if (File.Exists(path))
    {
        dotenvPath = Path.GetFullPath(path);
        break;
    }
}

if (dotenvPath != null)
{
    try
    {
        foreach (var raw in File.ReadAllLines(dotenvPath))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line.Substring(0, idx).Trim();
            var value = line.Substring(idx + 1).Trim();
            // Remove surrounding quotes if present
            if ((value.StartsWith("\"") && value.EndsWith("\"")) || (value.StartsWith("'") && value.EndsWith("'")))
            {
                value = value.Substring(1, value.Length - 2);
            }
            // Only set if not already present in environment
            if (Environment.GetEnvironmentVariable(key) == null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
        Console.WriteLine($"✓ Loaded environment overrides from {dotenvPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ Could not load .env file at {dotenvPath}: {ex.Message}");
    }
}
else
{
    Console.WriteLine("⚠ Warning: No .env file found in any expected location. Using default/environment configuration.");
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// JWT Configuration
var jwtKey = Environment.GetEnvironmentVariable("JWT_SECRET") 
    ?? builder.Configuration["Jwt:Key"] 
    ?? "dev_jwt_secret_minimum_32_characters_required_for_hs256";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "musicai.local";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "musicai.clients";

// Add Authentication with both JWT Bearer and Cookie support
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromSeconds(30),
        // Map custom claims to standard claim types
        RoleClaimType = "role",
        NameClaimType = "sub"
    };
    
    // Allow JWT from Authorization header OR cookie
    options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            // Try Authorization header first
            if (context.Request.Headers.ContainsKey("Authorization"))
            {
                return Task.CompletedTask;
            }
            
            // Fallback to cookie
            if (context.Request.Cookies.TryGetValue("MusicAI.Auth", out var token))
            {
                context.Token = token;
            }
            
            return Task.CompletedTask;
        }
    };
})
.AddCookie(options =>
{
    options.LoginPath = "/api/admin/login";
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorization(options =>
{
    // Allow both User and Admin roles to access
    options.AddPolicy("UserOrAdmin", policy => 
        policy.RequireAuthenticatedUser()
              .RequireAssertion(context => 
                  context.User.IsInRole("User") || 
                  context.User.IsInRole("Admin") || 
                  context.User.IsInRole("SuperAdmin")));
});

// Enhanced Swagger configuration with X-Admin-Token authentication
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "MusicAI Orchestrator API",
        Version = "v1",
        Description = "Secure music streaming system for OAPs (On-Air Personalities)",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "MusicAI Team"
        }
    });

    // Add X-Admin-Token security definition
    options.AddSecurityDefinition("X-Admin-Token", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Name = "X-Admin-Token",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Admin Session Token obtained from /api/admin/login endpoint. Login first, then paste the token here.",
        Scheme = "ApiKeyScheme"
    });

    // Apply X-Admin-Token to all admin endpoints
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "X-Admin-Token"
                }
            },
            System.Array.Empty<string>()
        }
    });

    // Enable file upload support in Swagger UI
    options.MapType<Microsoft.AspNetCore.Http.IFormFile>(() => new Microsoft.OpenApi.Models.OpenApiSchema
    {
        Type = "string",
        Format = "binary"
    });

    options.MapType<Microsoft.AspNetCore.Http.IFormFileCollection>(() => new Microsoft.OpenApi.Models.OpenApiSchema
    {
        Type = "array",
        Items = new Microsoft.OpenApi.Models.OpenApiSchema
        {
            Type = "string",
            Format = "binary"
        }
    });

    // Custom operation filter to mark file upload endpoints
    options.OperationFilter<MusicAI.Orchestrator.FileUploadOperationFilter>();
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Postgres users repository registration
var pgConn = builder.Configuration.GetConnectionString("Default")
             ?? builder.Configuration["Postgres:ConnectionString"]
             ?? Environment.GetEnvironmentVariable("POSTGRES_URL")
             ?? Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__DEFAULT")
             ?? "Host=localhost;Username=postgres;Password=postgres;Database=musicai";
try
{
    builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.UsersRepository(pgConn));
}
catch (Exception ex)
{
    System.Console.WriteLine($"Warning: could not register UsersRepository: {ex.Message}");
}

// AWS services are optional for demo — register dummy implementations if config is missing
try
{
    // Detect S3 bucket and related keys from multiple possible configuration keys / env var names
    var s3Bucket = builder.Configuration["AWS:S3Bucket"]
                 ?? builder.Configuration["AWS__S3Bucket"]
                 ?? Environment.GetEnvironmentVariable("AWS__S3Bucket")
                 ?? Environment.GetEnvironmentVariable("AWS_S3_BUCKET")
                 ?? Environment.GetEnvironmentVariable("AWS__S3BUCKET");

    var s3Endpoint = builder.Configuration["AWS:S3Endpoint"]
                  ?? builder.Configuration["AWS__S3Endpoint"]
                  ?? Environment.GetEnvironmentVariable("AWS__S3Endpoint")
                  ?? Environment.GetEnvironmentVariable("AWS_S3_ENDPOINT");

    var accessKey = builder.Configuration["AWS:AccessKey"]
                 ?? builder.Configuration["AWS__AccessKey"]
                 ?? Environment.GetEnvironmentVariable("AWS__AccessKey")
                 ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");

    var region = builder.Configuration["AWS:Region"]
              ?? builder.Configuration["AWS__Region"]
              ?? Environment.GetEnvironmentVariable("AWS__Region")
              ?? Environment.GetEnvironmentVariable("AWS_REGION");

    // Log resolved configuration (mask secrets)
    string Mask(string s) => string.IsNullOrEmpty(s) ? "(none)" : (s.Length <= 6 ? "***" : s.Substring(0, 4) + "***" + s.Substring(s.Length - 2));
    System.Console.WriteLine($"Resolved S3 bucket: {(string.IsNullOrEmpty(s3Bucket) ? "(none)" : s3Bucket)}");
    System.Console.WriteLine($"Resolved S3 endpoint: {(string.IsNullOrEmpty(s3Endpoint) ? "(none)" : s3Endpoint)}");
    System.Console.WriteLine($"Resolved AWS access key: {Mask(accessKey)}");
    System.Console.WriteLine($"Resolved AWS region: {(string.IsNullOrEmpty(region) ? "(none)" : region)}");

    if (!string.IsNullOrEmpty(s3Bucket))
    {
        builder.Services.AddSingleton<MusicAI.Infrastructure.Services.IS3Service, MusicAI.Infrastructure.Services.S3Service>();
        System.Console.WriteLine("AWS S3 configured. S3Service registered.");
    }
    else
    {
        // If S3 is not configured, register a null fallback so the app still builds.
        // Note: this registers a null IS3Service; callers should handle a null service or you can replace this with a proper local implementation.
        builder.Services.AddSingleton<MusicAI.Infrastructure.Services.IS3Service>(sp => null);
        System.Console.WriteLine("AWS S3 not configured. Registered null IS3Service fallback (no uploads will be available).");
        System.Console.WriteLine("To enable real S3, set AWS__S3Bucket, AWS__AccessKey, AWS__SecretKey and either AWS__Region or AWS__S3Endpoint in your .env or environment.");
    }
    builder.Services.AddSingleton<MusicAI.Infrastructure.Services.SageMakerClientService>();
}
catch (Exception ex)
{
    // Log but don't crash if AWS config is missing
    System.Console.WriteLine($"Warning: Could not register AWS services: {ex.Message}. Swagger will still be available.");
}
builder.Services.AddSingleton<Func<string, MusicAI.Common.Models.User>>(sp => (id) => new MusicAI.Common.Models.User(id, "demo@example.com", true, 200, "NG", "127.0.0.1"));

// register AdminsRepository (using same Postgres connection string)
try
{
    builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.AdminsRepository(pgConn));
}
catch (Exception ex)
{
    System.Console.WriteLine($"Warning: could not register AdminsRepository: {ex.Message}");
}

// Music Library Repositories (optional - will fallback to S3 if unavailable)
try
{
    // Test database connection with timeout before creating repositories
    var connBuilder = new Npgsql.NpgsqlConnectionStringBuilder(pgConn);
    connBuilder.Timeout = 3; // 3 second timeout
    using (var testConn = new Npgsql.NpgsqlConnection(connBuilder.ToString()))
    {
        testConn.Open();
        testConn.Close();
    }
    
    builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.MusicRepository(pgConn));
    builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.OapAgentsRepository(pgConn));
    builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.SubscriptionsRepository(pgConn));
    builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.CreditPackagesRepository(pgConn));
    builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.PaymentTransactionsRepository(pgConn));
    
    // OAP Scheduling Service (requires OapAgentsRepository)
    builder.Services.AddSingleton<MusicAI.Orchestrator.Services.OapSchedulingService>();
    
    System.Console.WriteLine("Music library repositories registered successfully");
}
catch (Exception ex)
{
    System.Console.WriteLine($"Warning: Database unavailable - music will be served directly from S3: {ex.Message}");
    // Don't register database repositories - OAP will use S3 fallback
}

// Music Categorization Service (intelligent auto-organization)
try
{
    builder.Services.AddSingleton<MusicAI.Orchestrator.Services.MusicCategorizationService>();
    System.Console.WriteLine("Music categorization service registered successfully");
}
catch (Exception ex)
{
    System.Console.WriteLine($"Warning: could not register MusicCategorizationService: {ex.Message}");
}

// Agent Plugins  
try
{
    builder.Services.AddSingleton(sp => new MusicAI.Agents.Plugins.MusicPlugin(
        sp.GetRequiredService<MusicAI.Orchestrator.Data.MusicRepository>(),
        sp.GetService<MusicAI.Orchestrator.Services.OapSchedulingService>()));

    builder.Services.AddSingleton(sp => new MusicAI.Agents.Plugins.PaymentPlugin(
        sp.GetRequiredService<MusicAI.Orchestrator.Data.CreditPackagesRepository>(),
        sp.GetRequiredService<MusicAI.Orchestrator.Data.SubscriptionsRepository>()));

    builder.Services.AddSingleton(sp => new MusicAI.Agents.Plugins.UserPlugin(
        sp.GetRequiredService<MusicAI.Orchestrator.Data.UsersRepository>()));

    System.Console.WriteLine("Agent plugins registered successfully");
}
catch (Exception ex)
{
    System.Console.WriteLine($"Warning: could not register agent plugins: {ex.Message}");
}

// News Services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MusicAI.Orchestrator.Services.NewsService>();
builder.Services.AddSingleton<MusicAI.Orchestrator.Services.NewsReadingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MusicAI.Orchestrator.Services.NewsReadingService>());
System.Console.WriteLine("News reading service registered - Tosin will read news every hour");

// TosinAgent (Autonomous AI Agent)
try
{
    var openAiKey = builder.Configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrEmpty(openAiKey))
    {
        System.Console.WriteLine("WARNING: OPENAI_API_KEY not found. TosinAgent will not work without it.");
    }
    else
    {
        builder.Services.AddSingleton(sp => new MusicAI.Agents.OapAgents.TosinAgent(
            openAiKey,
            sp.GetRequiredService<MusicAI.Agents.Plugins.MusicPlugin>(),
            sp.GetRequiredService<MusicAI.Agents.Plugins.PaymentPlugin>(),
            sp.GetRequiredService<MusicAI.Agents.Plugins.UserPlugin>()));
        System.Console.WriteLine("TosinAgent registered successfully with OpenAI GPT-4");
    }
}
catch (Exception ex)
{
    System.Console.WriteLine($"Warning: could not register TosinAgent: {ex.Message}");
}

// PRODUCTION: Roman OAP running 24/7 - Cultural fusion curator
// Serves music from ALL folders (Afropop, Amapiano, Reggae, World Music, etc.)
// Auto-shuffle Spotify-style playlist with secure HLS + MP3 streaming
builder.Services.AddSingleton<List<MusicAI.Common.Models.PersonaConfig>>(sp => new List<MusicAI.Common.Models.PersonaConfig>
{
    new MusicAI.Common.Models.PersonaConfig("roman","Roman","localhost",8006, TimeSpan.Zero, TimeSpan.FromHours(24))
});

var app = builder.Build();

// Development: ensure the app listens on both http and https for local testing
if (app.Environment.IsDevelopment())
{
    try
    {
        app.Urls.Clear();
        app.Urls.Add("http://localhost:5000");
    }
    catch { }
}

// Add lightweight IP rate limiting middleware (in-memory). For production use an external store (Redis) or ALB WAF.
app.UseMiddleware<RateLimitMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Swagger (enabled in Development or via configuration)
var enableSwaggerUi = app.Configuration.GetValue<bool>("Swagger:Enabled", app.Environment.IsDevelopment());
if (enableSwaggerUi)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "MusicAI Orchestrator V1");
        c.RoutePrefix = "swagger";
        c.DisplayRequestDuration();
        c.EnableTryItOutByDefault();
        c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
        c.ConfigObject.AdditionalItems["persistAuthorization"] = true; // keep token across refreshes
    });

    // Redirect root to swagger for convenience
    app.MapGet("/", () => Results.Redirect("/swagger/index.html"));
}

// Ensure routing and controllers are mapped
app.MapControllers();

// Log the listening addresses so it's easy to see where to open Swagger
try
{
    var logger = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Startup");
    logger.LogInformation("Application URLs: {Urls}", string.Join(", ", app.Urls));
}
catch { }

try
{
    app.Run();
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Fatal");
    logger.LogCritical(ex, "FATAL: Application crashed during runtime");
    System.Console.WriteLine($"FATAL ERROR: {ex.GetType().Name}: {ex.Message}");
    System.Console.WriteLine($"Stack Trace: {ex.StackTrace}");
    if (ex.InnerException != null)
    {
        System.Console.WriteLine($"Inner Exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }
    throw;
}
