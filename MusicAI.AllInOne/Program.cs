using System.Reflection;
using System.Text;
using System.IO;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MusicAI.Common.Models;

// Load .env into process environment so configuration/builder picks it up automatically.
// Searches common locations and stops at the first .env found.
try
{
    var envCandidates = new[] {
        Path.Combine(AppContext.BaseDirectory, ".env"),
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env")
    };
    foreach (var p in envCandidates)
    {
        if (File.Exists(p))
        {
            foreach (var raw in File.ReadAllLines(p))
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line.Substring(0, idx).Trim();
                var val = line.Substring(idx + 1).Trim();
                Environment.SetEnvironmentVariable(key, val);
            }
            Console.WriteLine($"✓ Loaded .env overrides from {p}");
            break;
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Warning loading .env: {ex.Message}");
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);

// Add configuration and services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Allow providing the opaque admin token via `X-Admin-Token` header in Swagger UI
    c.AddSecurityDefinition("X-Admin-Token", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Admin-Token",
        Description = "Opaque admin token returned by superadmin register / admin login. Paste the token here (no prefix)."
    });

    // Also support Bearer JWTs in the Authorization header
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    // Make both schemes available globally (operations will show the Authorize button)
    var xAdminScheme = new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "X-Admin-Token" } };
    var bearerScheme = new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } };
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [xAdminScheme] = new string[] { },
        [bearerScheme] = new string[] { }
    });

    // Add operation filter to explicitly show X-Admin-Token header on admin endpoints
    try
    {
        c.OperationFilter<MusicAI.AllInOne.Swagger.RequireAdminHeaderOperationFilter>();
    }
    catch
    {
        // ignore if type cannot be found in some build contexts
    }
});
// Expose IHttpContextAccessor so controllers resolved from loaded ApplicationParts can use it
builder.Services.AddHttpContextAccessor();
// Ensure explicit registration in case application parts reference a different load context
builder.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor, Microsoft.AspNetCore.Http.HttpContextAccessor>();

// JWT settings (can be provided via appsettings or environment variables)
var jwtKey = builder.Configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_SECRET") ?? "dev_secret_change_me";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "MusicAI";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "MusicAIUsers";
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);
var signingKey = new SymmetricSecurityKey(keyBytes);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false; // for local dev
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = signingKey,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("UserOrAdmin", policy => policy.RequireRole("User", "Admin"));
});

// Add controllers and try to discover controllers from referenced assemblies (Orchestrator & Personas)
string[] assemblyNames = new[] { "MusicAI.Orchestrator", "MusicAI.Personas.Tosin" };
builder.Services.AddControllers().ConfigureApplicationPartManager(apm =>
{
    foreach (var name in assemblyNames)
    {
        try
        {
            var asmName = new AssemblyName(name);
            var asm = Assembly.Load(asmName);
            if (asm != null)
            {
                apm.ApplicationParts.Add(new AssemblyPart(asm));
            }
        }
        catch
        {
            // ignore missing assemblies (they may not be built yet)
        }
    }
});

// Register common/infrastructure services by checking types at runtime so missing infra doesn't crash startup
void TryRegisterInfraService(string assemblyQualifiedTypeName, Action<Type> register)
{
    try
    {
        var t = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);
        if (t != null)
        {
            register(t);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning registering infra service {assemblyQualifiedTypeName}: {ex.Message}");
    }
}

// Only register S3Service when S3 configuration or endpoint is present to avoid startup exceptions
var s3BucketOrEndpoint = builder.Configuration["AWS:S3Bucket"] ?? Environment.GetEnvironmentVariable("AWS_S3_BUCKET")
    ?? builder.Configuration["AWS:S3Endpoint"] ?? Environment.GetEnvironmentVariable("AWS_S3_ENDPOINT");
if (!string.IsNullOrWhiteSpace(s3BucketOrEndpoint))
{
    TryRegisterInfraService("MusicAI.Infrastructure.Services.S3Service, MusicAI.Infrastructure", t =>
    {
        var iface = Type.GetType("MusicAI.Infrastructure.Services.IS3Service, MusicAI.Infrastructure", throwOnError: false);
        if (iface != null)
        {
            builder.Services.AddSingleton(iface, t);
        }
        else
        {
            builder.Services.AddSingleton(t);
        }
    });
}
else
{
    Console.WriteLine("S3 not configured; skipping S3Service registration (uploads will use local filesystem). If you want S3, set AWS:S3Bucket or AWS:S3Endpoint.");
}

TryRegisterInfraService("MusicAI.Infrastructure.Services.SageMakerClientService, MusicAI.Infrastructure", t => builder.Services.AddSingleton(t));

// Register Postgres-backed repositories
var pgConn = builder.Configuration.GetConnectionString("Default")
             ?? builder.Configuration["Postgres:ConnectionString"]
             ?? Environment.GetEnvironmentVariable("POSTGRES_URL")
             ?? Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__DEFAULT")
             ?? "Host=localhost;Username=postgres;Password=postgres;Database=musicai";

if (!string.IsNullOrWhiteSpace(pgConn))
{
    try
    {
        // Core Repositories
        builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.AdminsRepository(pgConn));
        builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.UsersRepository(pgConn));

        // Music & OAP Repositories
        builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.MusicRepository(pgConn));
        builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.OapAgentsRepository(pgConn));
        builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.SubscriptionsRepository(pgConn));
        builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.CreditPackagesRepository(pgConn));
        builder.Services.AddSingleton(new MusicAI.Orchestrator.Data.PaymentTransactionsRepository(pgConn));

        // Services
        builder.Services.AddSingleton<MusicAI.Orchestrator.Services.OapSchedulingService>();

        Console.WriteLine("Registered all repositories and services for RadioController");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: could not register repositories: {ex.Message}");
    }
}

// Register Agent Plugins (needed for TosinAgent if loaded)
try
{
    // read OpenAI key from config or env
    var openAiKey = builder.Configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

    builder.Services.AddSingleton(sp => new MusicAI.Agents.Plugins.MusicPlugin(
        sp.GetRequiredService<MusicAI.Orchestrator.Data.MusicRepository>(),
        sp.GetService<MusicAI.Orchestrator.Services.OapSchedulingService>()));

    builder.Services.AddSingleton(sp => new MusicAI.Agents.Plugins.PaymentPlugin(
        sp.GetRequiredService<MusicAI.Orchestrator.Data.CreditPackagesRepository>(),
        sp.GetRequiredService<MusicAI.Orchestrator.Data.SubscriptionsRepository>()));

    builder.Services.AddSingleton(sp => new MusicAI.Agents.Plugins.UserPlugin(
        sp.GetRequiredService<MusicAI.Orchestrator.Data.UsersRepository>()));

    try
    {
        if (!string.IsNullOrEmpty(openAiKey))
        {
            builder.Services.AddSingleton(sp => new MusicAI.Agents.OapAgents.TosinAgent(
                openAiKey,
                sp.GetRequiredService<MusicAI.Agents.Plugins.MusicPlugin>(),
                sp.GetRequiredService<MusicAI.Agents.Plugins.PaymentPlugin>(),
                sp.GetRequiredService<MusicAI.Agents.Plugins.UserPlugin>()
            ));
            Console.WriteLine("Registered TosinAgent successfully");
        }
        else
        {
            Console.WriteLine("OpenAI API key not found; TosinAgent will not be registered.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: could not register TosinAgent: {ex.Message}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Warning: could not register agent plugins: {ex.Message}");
}

var app = builder.Build();

// Swagger enabled in Development, via config, or when the environment variable `ENABLE_SWAGGER=true` is set.
// This lets you enable Swagger for local testing when the app is running in Production mode.
var enableSwagger = app.Environment.IsDevelopment()
                    || app.Configuration.GetValue<bool>("Swagger:Enabled", true)
                    || string.Equals(Environment.GetEnvironmentVariable("ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase);
if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Show developer exception page in Development to surface full error details during debugging
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Redirect HTTP to HTTPS in local dev when HTTPS is available
app.UseHttpsRedirection();

// Enable authentication/authorization middleware so JWT tokens are validated and roles applied
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

Console.WriteLine("MusicAI.AllInOne running on http://localhost:5000");
// Diagnostic lifetime and exception hooks to capture unexpected shutdowns
var lifetime = app.Lifetime;
lifetime.ApplicationStarted.Register(() => Console.WriteLine("LIFETIME: ApplicationStarted"));
lifetime.ApplicationStopping.Register(() => Console.WriteLine("LIFETIME: ApplicationStopping"));
lifetime.ApplicationStopped.Register(() => Console.WriteLine("LIFETIME: ApplicationStopped"));

AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    Console.WriteLine("UNHANDLED EXCEPTION: " + (e.ExceptionObject?.ToString() ?? "<null>"));
};
TaskScheduler.UnobservedTaskException += (s, e) =>
{
    Console.WriteLine("UNOBSERVED TASK EXCEPTION: " + e.Exception.ToString());
};
try
{
    // Bind to deterministic ports for local development (HTTP + HTTPS)
    app.Urls.Clear();
    app.Urls.Add("http://localhost:5000");
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine("Unhandled exception running AllInOne host:");
    Console.WriteLine(ex.ToString());
    // Ensure process doesn't exit silently in CI/debug scenarios
    throw;
}
