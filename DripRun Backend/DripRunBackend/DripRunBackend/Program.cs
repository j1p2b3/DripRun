using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using AspNetCoreRateLimit;
using Microsoft.AspNetCore.HttpOverrides; // ✅ needed for forwarded headers (Azure IPs)
using Azure.Storage.Blobs;

var builder = WebApplication.CreateBuilder(args);

// Limit max request body size (adjust if you support larger uploads)
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB
    o.AddServerHeader = false;
});

// Rate limiting services
builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
builder.Services.AddMemoryCache();
builder.Services.AddInMemoryRateLimiting();
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

// ✅ Use built-in configuration (no manual rebuild)
var configuration = builder.Configuration;

// ----------------------------------------------------------------------------
// Blob Storage (container-level client for modelsimages-coupimages)
// ----------------------------------------------------------------------------
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();

    var connString = cfg["BlobStorage:ConnectionString"];
    var containerName = cfg["BlobStorage:ContainerName"];

    if (string.IsNullOrWhiteSpace(connString))
        throw new InvalidOperationException("BlobStorage:ConnectionString is not configured.");
    if (string.IsNullOrWhiteSpace(containerName))
        throw new InvalidOperationException("BlobStorage:ContainerName is not configured.");

    // You always work in a single container: modelsimages-coupimages
    return new BlobContainerClient(connString, containerName);
});

// If your AdminController currently injects BlobServiceClient instead of BlobContainerClient,
// this also registers a BlobServiceClient so DI can resolve it.
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();

    var connString = cfg["BlobStorage:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connString))
        throw new InvalidOperationException("BlobStorage:ConnectionString is not configured.");

    return new BlobServiceClient(connString);
});

// ----------------------------------------------------------------------------
// Authentication
// ----------------------------------------------------------------------------
builder.Services
    .AddAuthentication(options =>
    {
        // Keep the default scheme for "regular user" tokens
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    // Default (regular user) JWT scheme
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = true; // enforce HTTPS
        options.SaveToken = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuration["JWT_ISSUER"],

            ValidateAudience = true,
            ValidAudience = configuration["JWT_AUDIENCE"], // e.g. DripRunUsers

            ValidateLifetime = true,                 // Ensure token hasn't expired
            RequireExpirationTime = true,            // require exp
            ClockSkew = TimeSpan.FromSeconds(60),    // keep skew tight

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["JWT_SECRET"])),

            RequireSignedTokens = true               // no unsigned JWTs
        };
    })
    // ✅ Admin JWT scheme (separate audience, no fallback)
    .AddJwtBearer("AdminJwt", options =>
    {
        options.RequireHttpsMetadata = true;
        options.SaveToken = false;

        var cfg = builder.Configuration;
        var adminAudience = cfg["JWT_ADMIN_AUDIENCE"];
        if (string.IsNullOrWhiteSpace(adminAudience))
            throw new InvalidOperationException("Missing JWT_ADMIN_AUDIENCE in configuration");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = cfg["JWT_ISSUER"],

            ValidateAudience = true,
            ValidAudience = adminAudience, // ✅ only admin audience accepted

            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(60),

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["JWT_SECRET"])),

            RequireSignedTokens = true
        };
    });

// ----------------------------------------------------------------------------
// Authorization
// ----------------------------------------------------------------------------
builder.Services.AddAuthorization(options =>
{
    // ✅ Policy that forces the AdminJwt scheme and requires is_company_admin=true
    options.AddPolicy("AdminJwt", policy =>
    {
        policy.AddAuthenticationSchemes("AdminJwt");
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("is_company_admin", "true");
    });
});

// Controllers
builder.Services.AddControllers();

// HttpClient for DI (used by Google/Facebook/Apple controllers)
builder.Services.AddHttpClient();

// Swagger (keep as you had it)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "DripRun API", Version = "v1" });
    c.CustomSchemaIds(t => t.FullName!.Replace('+', '.'));
});

// CORS (edit domains later)
const string CorsPolicyName = "DripRunCors";
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy.WithOrigins(
                "https://www-driprun-com-au.filesusr.com",
                "https://www.driprun.com.au",
                "https://driprunwebapp.z8.web.core.windows.net"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials(); // only matters if you use cookies on the web
    });
});

var app = builder.Build();

// Exception handler
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"internal_server_error\"}");
    });
});

// Forwarded headers (Azure)
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    RequireHeaderSymmetry = false,
    ForwardLimit = null
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// Pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors(CorsPolicyName);

// Basic security headers (browser-only)
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    await next();
});

// Rate limiting early
app.UseIpRateLimiting();

app.UseAuthentication();
app.UseAuthorization();

// Strong CSP for browsers (Production only)
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; " +
            "frame-ancestors 'none'; " +
            "img-src 'self' data: https://*.mapbox.com; " +
            "script-src 'self' 'unsafe-inline' https://accounts.google.com https://apis.google.com; " +
            "style-src 'self' 'unsafe-inline' https://api.mapbox.com; " +
            "connect-src 'self' https://api.YOURDOMAIN.com https://api.mapbox.com;";
        await next();
    });
}

app.MapControllers();
app.Run();
