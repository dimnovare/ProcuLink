using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ProcuLink.Api.Middleware;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ───────────────────────────────────────────────────────────────
builder.Services.AddDbContext<ProcuLinkDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── Authentication — Clerk JWT Bearer ─────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Clerk:Authority"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            NameClaimType = "sub",
        };
    });

builder.Services.AddAuthorization();

// ── Rate limiting — 20 uploads/min per authenticated user ──────────────────
builder.Services.AddRateLimiter(options =>
{
    // Per-user fixed-window policy for the upload endpoint.
    // Key: Clerk sub claim; falls back to IP for unauthenticated callers.
    options.AddPolicy("upload", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0   // reject immediately — no queuing
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Upload rate limit exceeded. Maximum 20 uploads per minute." }, ct);
    };
});

// ── Tenant service ─────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenantService, CurrentTenantService>();

// ── MVC / Controllers ──────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── CORS — React frontend ──────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins("http://localhost:8080", "http://localhost:8081", "http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ── Repositories ──────────────────────────────────────────────────────────
builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<ISupplierProfileRepository, EfSupplierProfileRepository>();
builder.Services.AddScoped<IItemMappingRepository, EfItemMappingRepository>();

// Outbound/delivery: file-backed until R2 is wired in Phase 2
var dataRoot = Path.Combine(builder.Environment.ContentRootPath, "data");
builder.Services.AddSingleton<IOutboundRepository>(
    new FileOutboundRepository(Path.Combine(dataRoot, "outbound")));

// ── HTTP client (webhook delivery) ────────────────────────────────────────
builder.Services.AddHttpClient();

// ── OpenAPI — Swashbuckle for spec, Scalar for UI ──────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ProcuLink API",
        Version = "v1",
        Description = "Purchase Order processing API"
    });
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste a Clerk session JWT (without 'Bearer ' prefix)."
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ──────────────────────────────────────────────────────────────────────────
var app = builder.Build();
// ──────────────────────────────────────────────────────────────────────────

// ── OpenAPI / Scalar UI — dev only ────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    // Swashbuckle generates the spec at /swagger/v1/swagger.json
    app.UseSwagger();

    // Scalar UI at /scalar — replaces Swagger UI
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("ProcuLink API");
        options.WithOpenApiRoutePattern("/swagger/v1/swagger.json");
        options.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");

// Pipeline order: Authenticate → resolve tenant → rate-limit → Authorize → controllers
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

app.Run();
