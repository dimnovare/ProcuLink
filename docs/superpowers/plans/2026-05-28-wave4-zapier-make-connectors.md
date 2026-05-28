# Wave 4 — Zapier + Make.com Connectors Implementation Plan
**Date:** 2026-05-28  
**Scope:** Per-tenant API keys, inbound REST API with tenant slug, IntegrationSubscription layer, Hangfire outbound trigger delivery, connector definition files, frontend API Keys tab.

---

## Overview

Wave 4 adds a full integration platform layer to ProcuLink:
- **TenantApiKey** — HMAC-SHA256 hashed keys, shown plaintext once, per-org
- **Organisation.Slug** — stable kebab-case identifier for tenant addressing
- **ApiKeyAuthHandler** — second auth scheme (alongside Clerk JWT)
- **Inbound REST API** — receive events/webhooks from external platforms via `POST /api/ingress/{slug}/...`
- **IntegrationSubscription** — per-org subscriptions (Zapier, Make.com, custom) to ProcuLink events
- **FireIntegrationTriggerJob** — Hangfire async delivery with HMAC-SHA256 signature header, exponential backoff
- **Trigger hooks** — `order.created`, `order.delivered`, `order.failed` wired into existing services
- **Connector files** — `docs/integrations/` with Zapier app JSON, Make.com connector JSON, SUBMISSION.md
- **Frontend** — Settings → API Keys tab; Settings → Connectors section showing Zapier/Make.com

---

## Task Map

```
T1  TenantApiKey entity + Org.Slug + migrations     ← parallel block 1
T2  ApiKeyAuthHandler + auth scheme wiring           ← parallel block 1
T3  IApiKeyService + ApiKeyService + ApiKeyController
T4  IngressController (ApiKey auth, slug guard)
T5  IntegrationSubscription entity + migration + IntegrationController
T6  IIntegrationTriggerService + FireIntegrationTriggerJob
T7  Hook into OrderService + DeliveryService
T8  docs/integrations/ connector files              ← parallel block 2
T9  Frontend — API Keys tab + connectors section    ← parallel block 2
T10 Tests
```

---

## Task 1 — TenantApiKey entity + Organisation.Slug + migrations

### `ProcuLink.Core/Entities/TenantApiKey.cs`
```csharp
namespace ProcuLink.Core.Entities;

/// <summary>
/// A hashed API key for machine-to-machine access (Zapier, Make.com, custom integrations).
/// The raw key is shown to the user ONCE at creation and never stored.
/// Only the HMAC-SHA256 hash is persisted.
/// </summary>
public class TenantApiKey
{
    public Guid   Id             { get; set; }
    public Guid   OrganisationId { get; set; }

    /// <summary>User-assigned label, e.g. "Zapier production".</summary>
    public string Label          { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256(key, secret) hex string.
    /// Secret is the first 32 chars of the raw key itself (self-HMAC).
    /// </summary>
    public string KeyHash        { get; set; } = string.Empty;

    /// <summary>First 8 chars of the raw key — shown in list so user can identify key.</summary>
    public string KeyPrefix      { get; set; } = string.Empty;

    public bool      IsActive    { get; set; } = true;
    public DateTime  CreatedAt   { get; set; }
    public DateTime? LastUsedAt  { get; set; }
    public DateTime? ExpiresAt   { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
```

### Add `Slug` to `Organisation.cs`
In `ProcuLink.Core/Entities/Organisation.cs`, add after `Name`:
```csharp
/// <summary>
/// Unique kebab-case slug used for machine-to-machine inbound addressing.
/// Auto-generated at org creation. Never changes after set.
/// </summary>
public string Slug { get; set; } = string.Empty;
```
Also add navigation:
```csharp
public List<TenantApiKey>         ApiKeys                 { get; set; } = new();
public List<IntegrationSubscription> IntegrationSubscriptions { get; set; } = new();
```

### EF migration: `AddTenantApiKeysAndOrgSlug`

In `ProcuLink.Infrastructure/Migrations/` — run:
```
dotnet ef migrations add AddTenantApiKeysAndOrgSlug --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
```

The migration must add:
- `organisations.slug` — `text NOT NULL DEFAULT ''`, unique index
- `tenant_api_keys` table

DbContext additions (see Task 3 for DbSet).

---

## Task 2 — ApiKeyAuthHandler + auth scheme wiring

### `ProcuLink.Api/Auth/ApiKeyAuthHandler.cs`
```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ProcuLink.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ProcuLink.Api.Auth;

public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Authenticates requests that carry an X-ProcuLink-Key header.
/// Validates the key by hashing against stored HMAC-SHA256 hashes.
/// On success, sets claims: org_id, org_slug, auth_method=api_key.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    private readonly ProcuLinkDbContext _db;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ProcuLinkDbContext db)
        : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-ProcuLink-Key", out var keyValues))
            return AuthenticateResult.NoResult(); // let next scheme try

        var rawKey = keyValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKey) || rawKey.Length < 32)
            return AuthenticateResult.Fail("Invalid API key format.");

        var hash = ComputeHash(rawKey);

        var apiKey = await _db.TenantApiKeys
            .Include(k => k.Organisation)
            .Where(k => k.KeyHash == hash && k.IsActive)
            .FirstOrDefaultAsync();

        if (apiKey is null)
            return AuthenticateResult.Fail("API key not found or inactive.");

        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow)
            return AuthenticateResult.Fail("API key expired.");

        // Update LastUsedAt — fire-and-forget, non-critical
        apiKey.LastUsedAt = DateTime.UtcNow;
        _ = _db.SaveChangesAsync();

        var claims = new[]
        {
            new Claim("org_id",      apiKey.OrganisationId.ToString()),
            new Claim("org_slug",    apiKey.Organisation.Slug),
            new Claim("auth_method", "api_key"),
            new Claim("key_id",      apiKey.Id.ToString()),
        };
        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    /// <summary>HMAC-SHA256 using the first 32 chars of the key as the secret (self-HMAC).</summary>
    public static string ComputeHash(string rawKey)
    {
        var secret = Encoding.UTF8.GetBytes(rawKey[..Math.Min(32, rawKey.Length)]);
        var data   = Encoding.UTF8.GetBytes(rawKey);
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }
}
```

### Register in `Program.cs`

Replace the single `AddAuthentication(JwtBearerDefaults.AuthenticationScheme)` block with a dual-scheme setup:

```csharp
// ── Authentication — Clerk JWT Bearer + API Key ───────────────────────────
builder.Services.AddAuthentication(options =>
{
    // Clerk JWT Bearer remains the default for browser/frontend calls.
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.Authority           = builder.Configuration["Clerk:Authority"];
    options.MapInboundClaims    = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateAudience = false,
        NameClaimType    = "sub",
    };
})
.AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(
    "ApiKey", _ => { });
```

For ingress endpoints, a custom `[Authorize(AuthenticationSchemes = "ApiKey")]` attribute restricts to machine keys.

---

## Task 3 — IApiKeyService + ApiKeyService + ApiKeyController

### `ProcuLink.Core/Services/IApiKeyService.cs`
```csharp
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IApiKeyService
{
    /// <summary>
    /// Generate a new API key for the org.
    /// Returns (entity, rawKey). Raw key is shown once — never stored.
    /// </summary>
    Task<(TenantApiKey Key, string RawKey)> CreateAsync(
        Guid organisationId, string label, DateTime? expiresAt, CancellationToken ct);

    Task<IReadOnlyList<TenantApiKey>> ListAsync(Guid organisationId, CancellationToken ct);

    Task<bool> RevokeAsync(Guid organisationId, Guid keyId, CancellationToken ct);
}
```

### `ProcuLink.Infrastructure/Services/ApiKeyService.cs`
```csharp
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;           // ApiKeyAuthHandler.ComputeHash
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class ApiKeyService : IApiKeyService
{
    private readonly ProcuLinkDbContext _db;

    public ApiKeyService(ProcuLinkDbContext db) => _db = db;

    public async Task<(TenantApiKey Key, string RawKey)> CreateAsync(
        Guid organisationId, string label, DateTime? expiresAt, CancellationToken ct)
    {
        // plk_ prefix + 48 random chars — total 52 chars, URL-safe base64
        var rawBytes = new byte[36];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rawBytes);
        var rawKey = "plk_" + Convert.ToBase64String(rawBytes)
                                     .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var hash   = ApiKeyAuthHandler.ComputeHash(rawKey);
        var prefix = rawKey[..8];  // "plk_XXXX"

        var entity = new TenantApiKey
        {
            Id             = Guid.NewGuid(),
            OrganisationId = organisationId,
            Label          = label.Trim(),
            KeyHash        = hash,
            KeyPrefix      = prefix,
            IsActive       = true,
            CreatedAt      = DateTime.UtcNow,
            ExpiresAt      = expiresAt,
        };

        _db.TenantApiKeys.Add(entity);
        await _db.SaveChangesAsync(ct);

        return (entity, rawKey);
    }

    public async Task<IReadOnlyList<TenantApiKey>> ListAsync(Guid organisationId, CancellationToken ct)
        => await _db.TenantApiKeys
                    .Where(k => k.OrganisationId == organisationId)
                    .OrderByDescending(k => k.CreatedAt)
                    .ToListAsync(ct);

    public async Task<bool> RevokeAsync(Guid organisationId, Guid keyId, CancellationToken ct)
    {
        var key = await _db.TenantApiKeys
                           .Where(k => k.OrganisationId == organisationId && k.Id == keyId)
                           .FirstOrDefaultAsync(ct);
        if (key is null) return false;
        key.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
```

### `ProcuLink.Api/Controllers/ApiKeyController.cs`
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]          // Clerk JWT — org members manage their own keys
[ApiController]
[Route("api/api-keys")]
public sealed class ApiKeyController : ControllerBase
{
    private readonly IApiKeyService      _keys;
    private readonly ICurrentTenantService _tenant;

    public ApiKeyController(IApiKeyService keys, ICurrentTenantService tenant)
    {
        _keys   = keys;
        _tenant = tenant;
    }

    // GET /api/api-keys
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var list  = await _keys.ListAsync(orgId, ct);
        var dto   = list.Select(k => new
        {
            k.Id, k.Label, k.KeyPrefix, k.IsActive,
            k.CreatedAt, k.LastUsedAt, k.ExpiresAt
        });
        return Ok(dto);
    }

    // POST /api/api-keys
    public sealed record CreateKeyRequest(string Label, DateTime? ExpiresAt);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateKeyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Label))
            return BadRequest(new { error = "Label is required." });

        var orgId = _tenant.OrganisationId;
        var (entity, rawKey) = await _keys.CreateAsync(orgId, req.Label, req.ExpiresAt, ct);

        return Ok(new
        {
            entity.Id,
            entity.Label,
            entity.KeyPrefix,
            entity.IsActive,
            entity.CreatedAt,
            entity.ExpiresAt,
            // Raw key shown ONCE. Store it securely — cannot be retrieved again.
            RawKey = rawKey,
        });
    }

    // DELETE /api/api-keys/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var orgId  = _tenant.OrganisationId;
        var result = await _keys.RevokeAsync(orgId, id, ct);
        return result ? NoContent() : NotFound();
    }
}
```

---

## Task 4 — IngressController (ApiKey auth, slug guard)

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Inbound REST API for machine-to-machine callers (Zapier, Make.com, custom).
/// Auth: X-ProcuLink-Key header (ApiKey scheme).
/// Slug guard: the path slug must match the authenticated org.
/// </summary>
[ApiController]
[Route("api/ingress/{slug}")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public sealed class IngressController : ControllerBase
{
    private readonly ProcuLinkDbContext _db;

    public IngressController(ProcuLinkDbContext db) => _db = db;

    /// <summary>Verify the slug in the path matches the authenticated org.</summary>
    private async Task<bool> SlugMatchesCallerAsync(string slug, CancellationToken ct)
    {
        var orgIdClaim = User.FindFirst("org_id")?.Value;
        if (!Guid.TryParse(orgIdClaim, out var orgId)) return false;

        return await _db.Organisations
                        .AnyAsync(o => o.Id == orgId && o.Slug == slug, ct);
    }

    // ── POST /api/ingress/{slug}/orders ──────────────────────────────────────
    /// <summary>
    /// Zapier/Make.com pushes a structured order payload.
    /// Validates auth + slug then routes to IOrderService.CreateStubFromParsedOrderAsync.
    /// </summary>
    [HttpPost("orders")]
    public async Task<IActionResult> ReceiveOrder(
        string slug,
        [FromBody] IngressOrderRequest req,
        [FromServices] ProcuLink.Core.Services.IOrderService orders,
        CancellationToken ct)
    {
        if (!await SlugMatchesCallerAsync(slug, ct))
            return Forbid();

        if (req is null || req.Lines is null || req.Lines.Count == 0)
            return BadRequest(new { error = "Order must have at least one line." });

        var orgIdClaim = User.FindFirst("org_id")!.Value;
        var orgId      = Guid.Parse(orgIdClaim);

        // Resolve supplier by reference (external_id or id)
        Guid supplierId;
        if (Guid.TryParse(req.SupplierId, out var sid))
        {
            supplierId = sid;
        }
        else
        {
            var supplier = await _db.Suppliers
                .Where(s => s.OrganisationId == orgId && s.ExternalId == req.SupplierId)
                .FirstOrDefaultAsync(ct);
            if (supplier is null)
                return BadRequest(new { error = $"Supplier '{req.SupplierId}' not found." });
            supplierId = supplier.Id;
        }

        // Map to ExtractedOrder (the Core DTO)
        var extracted = new ProcuLink.Core.Services.Ai.ExtractedOrder
        {
            OrderNumber  = req.OrderNumber ?? Guid.NewGuid().ToString("N")[..8],
            OrderDate    = req.OrderDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Currency     = req.Currency ?? "EUR",
            Notes        = req.Notes,
            Lines        = req.Lines.Select((l, i) => new ProcuLink.Core.Services.Ai.ExtractedLine
            {
                LineNumber      = i + 1,
                BuyerItemCode   = l.BuyerItemCode,
                Description     = l.Description ?? string.Empty,
                Quantity        = l.Quantity,
                Unit            = l.Unit ?? "EA",
                UnitPrice       = l.UnitPrice,
            }).ToList(),
        };

        var result = await orders.CreateStubFromParsedOrderAsync(
            orgId, supplierId, extracted, "ingress_api", ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error });

        return Ok(new
        {
            result.Value!.Id,
            result.Value.Status,
            LinesCount = result.Value.Lines.Count,
        });
    }

    // ── GET /api/ingress/{slug}/ping ─────────────────────────────────────────
    [HttpGet("ping")]
    public async Task<IActionResult> Ping(string slug, CancellationToken ct)
    {
        if (!await SlugMatchesCallerAsync(slug, ct))
            return Forbid();

        return Ok(new
        {
            message = "ProcuLink inbound API OK",
            slug,
            timestamp = DateTime.UtcNow,
        });
    }
}

/// <summary>Inbound order payload from external integration platforms.</summary>
public sealed record IngressOrderRequest(
    string?                       OrderNumber,
    DateOnly?                     OrderDate,
    string?                       Currency,
    string?                       Notes,
    string                        SupplierId,
    IReadOnlyList<IngressOrderLine> Lines
);

public sealed record IngressOrderLine(
    string? BuyerItemCode,
    string? Description,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice
);
```

---

## Task 5 — IntegrationSubscription entity + migration + IntegrationController

### `ProcuLink.Core/Entities/IntegrationSubscription.cs`
```csharp
namespace ProcuLink.Core.Entities;

/// <summary>
/// An outbound webhook subscription for a specific ProcuLink event type.
/// When the event fires, <c>FireIntegrationTriggerJob</c> posts to TargetUrl
/// with HMAC-SHA256 signature in X-ProcuLink-Signature.
/// </summary>
public class IntegrationSubscription
{
    public Guid   Id             { get; set; }
    public Guid   OrganisationId { get; set; }

    /// <summary>Integration platform label: "zapier", "make", "custom".</summary>
    public string Platform       { get; set; } = "custom";

    /// <summary>
    /// Event type this subscription fires on:
    /// "order.created" | "order.delivered" | "order.failed"
    /// </summary>
    public string EventType      { get; set; } = string.Empty;

    /// <summary>URL to POST the event payload to.</summary>
    public string TargetUrl      { get; set; } = string.Empty;

    /// <summary>AES-GCM encrypted HMAC secret — use DeliveryEncryptionService.</summary>
    public string? EncryptedSecret { get; set; }

    public bool      IsActive    { get; set; } = true;
    public int       FailureCount { get; set; } = 0;
    public DateTime  CreatedAt   { get; set; }
    public DateTime  UpdatedAt   { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
```

### Migration: `AddIntegrationSubscriptions`
```
dotnet ef migrations add AddIntegrationSubscriptions --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
```

### `ProcuLink.Api/Controllers/IntegrationController.cs`
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// CRUD for integration subscriptions (outbound webhooks to Zapier, Make.com, etc.)
/// Auth: Clerk JWT — org members manage their own subscriptions.
/// </summary>
[Authorize]
[ApiController]
[Route("api/integrations")]
public sealed class IntegrationController : ControllerBase
{
    private readonly ProcuLinkDbContext     _db;
    private readonly ICurrentTenantService  _tenant;
    private readonly DeliveryEncryptionService _enc;

    public IntegrationController(
        ProcuLinkDbContext db,
        ICurrentTenantService tenant,
        DeliveryEncryptionService enc)
    {
        _db     = db;
        _tenant = tenant;
        _enc    = enc;
    }

    // GET /api/integrations
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var subs  = await _db.IntegrationSubscriptions
                             .Where(s => s.OrganisationId == orgId)
                             .OrderByDescending(s => s.CreatedAt)
                             .ToListAsync(ct);

        var dto = subs.Select(s => new
        {
            s.Id, s.Platform, s.EventType, s.TargetUrl,
            s.IsActive, s.FailureCount, s.CreatedAt, s.UpdatedAt,
        });

        return Ok(dto);
    }

    public sealed record CreateSubRequest(
        string Platform, string EventType, string TargetUrl, string? Secret);

    // POST /api/integrations
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSubRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TargetUrl))
            return BadRequest(new { error = "TargetUrl is required." });

        var validEvents = new[] { "order.created", "order.delivered", "order.failed" };
        if (!validEvents.Contains(req.EventType))
            return BadRequest(new { error = $"EventType must be one of: {string.Join(", ", validEvents)}" });

        if (!Uri.TryCreate(req.TargetUrl, UriKind.Absolute, out _))
            return BadRequest(new { error = "TargetUrl must be a valid absolute URL." });

        var orgId = _tenant.OrganisationId;

        string? encSecret = null;
        if (!string.IsNullOrWhiteSpace(req.Secret))
            encSecret = _enc.Encrypt(req.Secret);

        var sub = new IntegrationSubscription
        {
            Id             = Guid.NewGuid(),
            OrganisationId = orgId,
            Platform       = req.Platform ?? "custom",
            EventType      = req.EventType,
            TargetUrl      = req.TargetUrl,
            EncryptedSecret = encSecret,
            IsActive       = true,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };

        _db.IntegrationSubscriptions.Add(sub);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            sub.Id, sub.Platform, sub.EventType, sub.TargetUrl,
            sub.IsActive, sub.CreatedAt,
        });
    }

    // DELETE /api/integrations/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var sub   = await _db.IntegrationSubscriptions
                             .Where(s => s.OrganisationId == orgId && s.Id == id)
                             .FirstOrDefaultAsync(ct);
        if (sub is null) return NotFound();
        _db.IntegrationSubscriptions.Remove(sub);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // PATCH /api/integrations/{id}/toggle
    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var sub   = await _db.IntegrationSubscriptions
                             .Where(s => s.OrganisationId == orgId && s.Id == id)
                             .FirstOrDefaultAsync(ct);
        if (sub is null) return NotFound();
        sub.IsActive  = !sub.IsActive;
        sub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { sub.Id, sub.IsActive });
    }
}
```

---

## Task 6 — IIntegrationTriggerService + FireIntegrationTriggerJob

### `ProcuLink.Core/Services/IIntegrationTriggerService.cs`
```csharp
namespace ProcuLink.Core.Services;

/// <summary>
/// Enqueues outbound trigger deliveries for all active subscriptions matching
/// the given org + event type. Each subscription fires as a separate Hangfire job.
/// </summary>
public interface IIntegrationTriggerService
{
    Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct);
}
```

### `ProcuLink.Infrastructure/Services/IntegrationTriggerService.cs`
```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class IntegrationTriggerService : IIntegrationTriggerService
{
    private readonly ProcuLinkDbContext   _db;
    private readonly IBackgroundJobClient _jobs;

    public IntegrationTriggerService(ProcuLinkDbContext db, IBackgroundJobClient jobs)
    {
        _db   = db;
        _jobs = jobs;
    }

    public async Task EnqueueAsync(
        Guid organisationId, string eventType, object payload, CancellationToken ct)
    {
        var subs = await _db.IntegrationSubscriptions
                            .Where(s => s.OrganisationId == organisationId
                                     && s.EventType      == eventType
                                     && s.IsActive)
                            .ToListAsync(ct);

        // Serialize payload once; all jobs share the same JSON string
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);

        foreach (var sub in subs)
        {
            FireIntegrationTriggerJob.Enqueue(_jobs, sub.Id, payloadJson);
        }
    }
}
```

### `ProcuLink.Api/Jobs/FireIntegrationTriggerJob.cs`
```csharp
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire job: delivers one integration event to one subscriber's TargetUrl.
/// Signs the payload with HMAC-SHA256 (X-ProcuLink-Signature: sha256=hex).
/// Deactivates the subscription after 3 consecutive failures.
/// Idempotent: if subscription is no longer active, exits silently.
/// </summary>
public class FireIntegrationTriggerJob
{
    private readonly ProcuLinkDbContext        _db;
    private readonly IHttpClientFactory        _http;
    private readonly DeliveryEncryptionService _enc;
    private readonly ILogger<FireIntegrationTriggerJob> _logger;

    public FireIntegrationTriggerJob(
        ProcuLinkDbContext        db,
        IHttpClientFactory        http,
        DeliveryEncryptionService enc,
        ILogger<FireIntegrationTriggerJob> logger)
    {
        _db     = db;
        _http   = http;
        _enc    = enc;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task ExecuteAsync(Guid subscriptionId, string payloadJson, CancellationToken ct)
    {
        var sub = await _db.IntegrationSubscriptions
                           .Where(s => s.Id == subscriptionId)
                           .FirstOrDefaultAsync(ct);

        if (sub is null || !sub.IsActive)
        {
            _logger.LogInformation(
                "FireIntegrationTriggerJob: sub {SubId} not found or inactive — skipping.",
                subscriptionId);
            return;
        }

        // Build HMAC-SHA256 signature
        string? signatureHeader = null;
        if (!string.IsNullOrEmpty(sub.EncryptedSecret))
        {
            var secret      = _enc.Decrypt(sub.EncryptedSecret);
            var secretBytes = Encoding.UTF8.GetBytes(secret);
            var dataBytes   = Encoding.UTF8.GetBytes(payloadJson);
            using var hmac  = new HMACSHA256(secretBytes);
            var sigHex      = Convert.ToHexString(hmac.ComputeHash(dataBytes)).ToLowerInvariant();
            signatureHeader = $"sha256={sigHex}";
        }

        try
        {
            var client = _http.CreateClient("delivery");

            using var request = new HttpRequestMessage(HttpMethod.Post, sub.TargetUrl)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (signatureHeader is not null)
                request.Headers.TryAddWithoutValidation("X-ProcuLink-Signature", signatureHeader);
            request.Headers.TryAddWithoutValidation("X-ProcuLink-Event", sub.EventType);

            var response = await client.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                // Reset failure counter on success
                sub.FailureCount = 0;
                sub.UpdatedAt    = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "FireIntegrationTriggerJob delivered event to {TargetUrl}, status={Status}",
                    sub.TargetUrl, response.StatusCode);
            }
            else
            {
                await IncrementFailureAsync(sub, ct);
                throw new InvalidOperationException(
                    $"Delivery failed: HTTP {(int)response.StatusCode} from {sub.TargetUrl}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await IncrementFailureAsync(sub, ct);
            throw;
        }
    }

    private async Task IncrementFailureAsync(
        Core.Entities.IntegrationSubscription sub, CancellationToken ct)
    {
        sub.FailureCount++;
        sub.UpdatedAt = DateTime.UtcNow;

        if (sub.FailureCount >= 3)
        {
            sub.IsActive = false;
            _logger.LogWarning(
                "FireIntegrationTriggerJob: deactivated sub {SubId} after {Count} failures.",
                sub.Id, sub.FailureCount);
        }

        await _db.SaveChangesAsync(ct);
    }

    public static void Enqueue(IBackgroundJobClient jobs, Guid subscriptionId, string payloadJson)
    {
        jobs.Enqueue<FireIntegrationTriggerJob>(
            j => j.ExecuteAsync(subscriptionId, payloadJson, CancellationToken.None));
    }
}
```

---

## Task 7 — Hook into OrderService + DeliveryService

### In `OrderService.CreateStubAsync` (after saving the order entity)
Find `ProcuLink.Infrastructure/Services/OrderService.cs` (or wherever it lives).

After `await _db.SaveChangesAsync(ct)` in `CreateStubAsync`, add:
```csharp
// ── Wave 4: fire order.created integration triggers ─────────────────────
await _integrationTrigger.EnqueueAsync(
    organisationId,
    "order.created",
    new
    {
        order_id       = order.Id,
        status         = order.Status,
        source_filename = order.SourceFileName,
        created_at     = order.CreatedAt,
    },
    ct);
```

Constructor injection: `IIntegrationTriggerService _integrationTrigger`.

### In `DeliveryService` status transitions

In `ProcuLink.Infrastructure/Services/DeliveryService.cs`, after marking an order `delivered`:
```csharp
await _integrationTrigger.EnqueueAsync(
    organisationId,
    "order.delivered",
    new { order_id = orderId, delivered_at = DateTime.UtcNow },
    ct);
```

After marking `delivery_failed` (final failure, not transient retry):
```csharp
await _integrationTrigger.EnqueueAsync(
    organisationId,
    "order.failed",
    new { order_id = orderId, error = errorMessage, failed_at = DateTime.UtcNow },
    ct);
```

---

## Task 8 — docs/integrations/ connector files

### `docs/integrations/zapier-app.json`
Complete Zapier CLI app definition:
```json
{
  "version": "1.0.0",
  "platformVersion": "15.0.0",
  "title": "ProcuLink",
  "description": "Connect ProcuLink to your Zapier workflows. Trigger Zaps when purchase orders are created or delivered, or send structured order data into ProcuLink.",
  "homepage_url": "https://proculink.io",
  "logo_url": "https://proculink.io/logo.png",
  "authentication": {
    "type": "custom",
    "test": {
      "url": "https://api.proculink.io/api/ingress/{{bundle.authData.slug}}/ping",
      "method": "GET",
      "headers": {
        "X-ProcuLink-Key": "{{bundle.authData.apiKey}}"
      }
    },
    "fields": [
      {
        "key": "apiKey",
        "label": "API Key",
        "required": true,
        "type": "password",
        "helpText": "Found in ProcuLink → Settings → API Keys. Starts with plk_"
      },
      {
        "key": "slug",
        "label": "Organisation Slug",
        "required": true,
        "type": "string",
        "helpText": "Your unique organisation slug shown in ProcuLink → Settings → API Keys."
      }
    ]
  },
  "triggers": {
    "order_created": {
      "key": "order_created",
      "noun": "Order",
      "display": {
        "label": "New Purchase Order Created",
        "description": "Triggers when a new purchase order is uploaded or received by ProcuLink."
      },
      "operation": {
        "type": "hook",
        "performSubscribe": {
          "url": "https://api.proculink.io/api/integrations",
          "method": "POST",
          "headers": { "Content-Type": "application/json" },
          "body": {
            "platform": "zapier",
            "eventType": "order.created",
            "targetUrl": "{{bundle.targetUrl}}",
            "secret": "{{bundle.subscribeData.secret}}"
          }
        },
        "performUnsubscribe": {
          "url": "https://api.proculink.io/api/integrations/{{bundle.subscribeData.id}}",
          "method": "DELETE"
        },
        "perform": {
          "source": "return [bundle.cleanedRequest.data || bundle.cleanedRequest];"
        },
        "sample": {
          "order_id": "00000000-0000-0000-0000-000000000001",
          "status": "parsing",
          "source_filename": "po-12345.csv",
          "created_at": "2026-05-28T10:00:00Z"
        }
      }
    },
    "order_delivered": {
      "key": "order_delivered",
      "noun": "Delivery",
      "display": {
        "label": "Purchase Order Delivered",
        "description": "Triggers when ProcuLink successfully delivers a purchase order to the supplier."
      },
      "operation": {
        "type": "hook",
        "performSubscribe": {
          "url": "https://api.proculink.io/api/integrations",
          "method": "POST",
          "headers": { "Content-Type": "application/json" },
          "body": {
            "platform": "zapier",
            "eventType": "order.delivered",
            "targetUrl": "{{bundle.targetUrl}}",
            "secret": "{{bundle.subscribeData.secret}}"
          }
        },
        "performUnsubscribe": {
          "url": "https://api.proculink.io/api/integrations/{{bundle.subscribeData.id}}",
          "method": "DELETE"
        },
        "perform": {
          "source": "return [bundle.cleanedRequest.data || bundle.cleanedRequest];"
        },
        "sample": {
          "order_id": "00000000-0000-0000-0000-000000000001",
          "delivered_at": "2026-05-28T10:05:00Z"
        }
      }
    }
  },
  "creates": {
    "create_order": {
      "key": "create_order",
      "noun": "Order",
      "display": {
        "label": "Create Purchase Order",
        "description": "Send a structured purchase order into ProcuLink from another app."
      },
      "operation": {
        "perform": {
          "url": "https://api.proculink.io/api/ingress/{{bundle.authData.slug}}/orders",
          "method": "POST",
          "headers": {
            "Content-Type": "application/json",
            "X-ProcuLink-Key": "{{bundle.authData.apiKey}}"
          },
          "body": {
            "orderNumber":  "{{bundle.inputData.orderNumber}}",
            "supplierId":   "{{bundle.inputData.supplierId}}",
            "currency":     "{{bundle.inputData.currency}}",
            "lines":        "{{bundle.inputData.lines}}"
          }
        },
        "inputFields": [
          { "key": "supplierId", "label": "Supplier ID", "required": true },
          { "key": "orderNumber", "label": "Order Number", "required": false },
          { "key": "currency", "label": "Currency", "default": "EUR" },
          {
            "key": "lines",
            "label": "Order Lines",
            "required": true,
            "type": "string",
            "helpText": "JSON array: [{buyerItemCode, description, quantity, unit, unitPrice}]"
          }
        ],
        "sample": {
          "id": "00000000-0000-0000-0000-000000000001",
          "status": "parsing",
          "linesCount": 3
        }
      }
    }
  }
}
```

### `docs/integrations/make-connector.json`
```json
{
  "name": "ProcuLink",
  "label": "ProcuLink",
  "version": "1.0.0",
  "description": "Automate purchase order workflows with ProcuLink — receive triggers when orders are created or delivered, or push new orders directly.",
  "theme": "#1a56db",
  "baseUrl": "https://api.proculink.io",
  "connection": {
    "label": "{{data.slug}} API Key",
    "type": "custom",
    "account": {
      "fields": [
        {
          "name": "apiKey",
          "label": "API Key",
          "type": "password",
          "required": true,
          "help": "From ProcuLink → Settings → API Keys. Starts with plk_"
        },
        {
          "name": "slug",
          "label": "Organisation Slug",
          "type": "text",
          "required": true,
          "help": "Your unique slug from ProcuLink → Settings → API Keys."
        }
      ],
      "validation": {
        "condition": "{{connection.verify}}",
        "url": "/api/ingress/{{connection.slug}}/ping",
        "method": "GET",
        "headers": { "X-ProcuLink-Key": "{{connection.apiKey}}" }
      }
    }
  },
  "triggers": [
    {
      "name": "watchOrderCreated",
      "label": "Watch for New Purchase Orders",
      "description": "Fires when a new purchase order is created in ProcuLink.",
      "type": "instant",
      "webhook": {
        "subscribe": {
          "url": "/api/integrations",
          "method": "POST",
          "headers": { "Authorization": "Bearer {{connection.clerkToken}}" },
          "body": {
            "platform": "make",
            "eventType": "order.created",
            "targetUrl": "{{webhook.url}}"
          }
        },
        "unsubscribe": {
          "url": "/api/integrations/{{subscribe.id}}",
          "method": "DELETE",
          "headers": { "Authorization": "Bearer {{connection.clerkToken}}" }
        }
      },
      "output": {
        "order_id": { "label": "Order ID", "type": "text" },
        "status": { "label": "Status", "type": "text" },
        "source_filename": { "label": "Source Filename", "type": "text" },
        "created_at": { "label": "Created At", "type": "date" }
      }
    },
    {
      "name": "watchOrderDelivered",
      "label": "Watch for Delivered Orders",
      "description": "Fires when ProcuLink successfully delivers a purchase order.",
      "type": "instant",
      "webhook": {
        "subscribe": {
          "url": "/api/integrations",
          "method": "POST",
          "headers": { "Authorization": "Bearer {{connection.clerkToken}}" },
          "body": {
            "platform": "make",
            "eventType": "order.delivered",
            "targetUrl": "{{webhook.url}}"
          }
        },
        "unsubscribe": {
          "url": "/api/integrations/{{subscribe.id}}",
          "method": "DELETE",
          "headers": { "Authorization": "Bearer {{connection.clerkToken}}" }
        }
      },
      "output": {
        "order_id": { "label": "Order ID", "type": "text" },
        "delivered_at": { "label": "Delivered At", "type": "date" }
      }
    }
  ],
  "actions": [
    {
      "name": "createOrder",
      "label": "Create Purchase Order",
      "description": "Push a structured purchase order into ProcuLink.",
      "url": "/api/ingress/{{connection.slug}}/orders",
      "method": "POST",
      "headers": { "X-ProcuLink-Key": "{{connection.apiKey}}" },
      "parameters": [
        { "name": "supplierId", "label": "Supplier ID", "type": "text", "required": true },
        { "name": "orderNumber", "label": "Order Number", "type": "text" },
        { "name": "currency", "label": "Currency", "type": "text", "default": "EUR" },
        {
          "name": "lines",
          "label": "Order Lines (JSON)",
          "type": "text",
          "required": true,
          "help": "[{buyerItemCode, description, quantity, unit, unitPrice}]"
        }
      ],
      "output": {
        "id": { "label": "Order ID", "type": "text" },
        "status": { "label": "Status", "type": "text" }
      }
    }
  ]
}
```

### `docs/integrations/SUBMISSION.md`
```markdown
# Integration Platform Submission Guide

## Zapier

**Status:** Ready for Zapier Developer Platform review.

### Pre-submission checklist
- [ ] Zapier developer account created at https://developer.zapier.com
- [ ] App definition: `zapier-app.json` (this directory)
- [ ] Test API key created in ProcuLink → Settings → API Keys
- [ ] Test org slug noted from same screen
- [ ] Zapier CLI installed: `npm install -g zapier-platform-cli`

### Submission steps
1. `zapier register "ProcuLink"` in the zapier/ SDK project directory
2. `zapier push` to upload the app
3. Test each trigger + action in the Zapier editor
4. Submit for Zapier review via Developer Platform dashboard

### Notes
- Webhook triggers use Zapier's REST hook pattern (subscribe/unsubscribe)
- Auth uses a custom `X-ProcuLink-Key` header, not OAuth2
- The org slug is stable — users find it in Settings → API Keys

---

## Make.com (formerly Integromat)

**Status:** Ready for Make.com partner review.

### Pre-submission checklist
- [ ] Make.com partner account at https://partners.make.com
- [ ] Connector JSON: `make-connector.json` (this directory)
- [ ] Test connection verified via /api/ingress/{slug}/ping

### Submission steps
1. Log in to Make.com Partner Portal
2. Create new connector, paste `make-connector.json`
3. Verify all triggers fire correctly with a test scenario
4. Submit for Make.com review

---

## Webhook Security

All outbound events from ProcuLink carry:
- `X-ProcuLink-Signature: sha256=<hex>` — HMAC-SHA256 of the payload using your subscription secret
- `X-ProcuLink-Event: <event-type>` — e.g. `order.created`

To verify:
```python
import hmac, hashlib
expected = hmac.new(secret.encode(), payload_bytes, hashlib.sha256).hexdigest()
assert f"sha256={expected}" == request.headers["X-ProcuLink-Signature"]
```

---

## Supported Events

| Event | When it fires |
|---|---|
| `order.created` | A new PO is uploaded or received via inbound API |
| `order.delivered` | PO successfully delivered to the supplier |
| `order.failed` | PO delivery failed after all retry attempts |
```

---

## Task 9 — Frontend: API Keys tab + Connectors section

### `project-proculink/src/app/(app)/settings/api-keys/page.tsx`

```tsx
'use client';

import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle
} from '@/components/ui/card';
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel,
  AlertDialogContent, AlertDialogDescription, AlertDialogFooter,
  AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger,
} from '@/components/ui/alert-dialog';
import { Copy, Eye, EyeOff, Key, Plus, Trash2 } from 'lucide-react';

interface ApiKey {
  id: string;
  label: string;
  keyPrefix: string;
  isActive: boolean;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
}

export default function ApiKeysPage() {
  const qc = useQueryClient();
  const [newLabel, setNewLabel] = useState('');
  const [createdKey, setCreatedKey] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const { data: keys = [], isLoading } = useQuery<ApiKey[]>({
    queryKey: ['api-keys'],
    queryFn: () => apiClient.get('/api/api-keys').then(r => r.data),
  });

  const create = useMutation({
    mutationFn: (label: string) =>
      apiClient.post('/api/api-keys', { label }).then(r => r.data),
    onSuccess: (data) => {
      setCreatedKey(data.rawKey);
      setNewLabel('');
      qc.invalidateQueries({ queryKey: ['api-keys'] });
    },
  });

  const revoke = useMutation({
    mutationFn: (id: string) => apiClient.delete(`/api/api-keys/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['api-keys'] }),
  });

  const copyKey = async (key: string) => {
    await navigator.clipboard.writeText(key);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold text-[#0f172a]">API Keys</h2>
        <p className="text-sm text-[#64748b] mt-1">
          Machine-to-machine access for Zapier, Make.com, and custom integrations.
          Each key is shown once at creation.
        </p>
      </div>

      {/* New key shown once */}
      {createdKey && (
        <Card className="border-[#22c55e]/30 bg-[#f0fdf4]">
          <CardHeader className="pb-2">
            <CardTitle className="text-sm font-semibold text-[#15803d]">
              API key created — copy it now
            </CardTitle>
            <CardDescription className="text-xs text-[#166534]">
              This key will not be shown again. Store it securely.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div className="flex items-center gap-2">
              <code className="flex-1 text-sm font-mono bg-white border border-[#dcfce7] rounded px-3 py-2 text-[#15803d] break-all">
                {createdKey}
              </code>
              <Button
                size="sm"
                variant="outline"
                onClick={() => copyKey(createdKey)}
                className="shrink-0 border-[#22c55e] text-[#15803d]"
              >
                <Copy className="h-3.5 w-3.5 mr-1" />
                {copied ? 'Copied!' : 'Copy'}
              </Button>
            </div>
            <Button
              size="sm"
              variant="ghost"
              className="mt-2 text-xs text-[#64748b]"
              onClick={() => setCreatedKey(null)}
            >
              I've saved it, dismiss
            </Button>
          </CardContent>
        </Card>
      )}

      {/* Create new key */}
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm font-medium">Create new API key</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="flex gap-2">
            <Input
              placeholder='e.g. "Zapier production"'
              value={newLabel}
              onChange={e => setNewLabel(e.target.value)}
              onKeyDown={e => {
                if (e.key === 'Enter' && newLabel.trim()) create.mutate(newLabel.trim());
              }}
              className="flex-1"
            />
            <Button
              onClick={() => create.mutate(newLabel.trim())}
              disabled={!newLabel.trim() || create.isPending}
              size="sm"
            >
              <Plus className="h-3.5 w-3.5 mr-1" />
              {create.isPending ? 'Creating…' : 'Create'}
            </Button>
          </div>
          {create.isError && (
            <p className="text-xs text-[#ef4444] mt-2">
              {(create.error as any)?.response?.data?.error ?? 'Failed to create key.'}
            </p>
          )}
        </CardContent>
      </Card>

      {/* Key list */}
      <div className="space-y-2">
        {isLoading && (
          <p className="text-sm text-[#64748b]">Loading…</p>
        )}
        {!isLoading && keys.length === 0 && (
          <Card className="border-dashed">
            <CardContent className="py-8 text-center">
              <Key className="h-6 w-6 text-[#cbd5e1] mx-auto mb-2" />
              <p className="text-sm text-[#94a3b8]">No API keys yet.</p>
            </CardContent>
          </Card>
        )}
        {keys.map(key => (
          <Card key={key.id} className={key.isActive ? '' : 'opacity-50'}>
            <CardContent className="py-3 flex items-center gap-3">
              <Key className="h-4 w-4 text-[#94a3b8] shrink-0" />
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-sm font-medium text-[#0f172a]">{key.label}</span>
                  <Badge variant={key.isActive ? 'default' : 'secondary'} className="text-xs">
                    {key.isActive ? 'Active' : 'Revoked'}
                  </Badge>
                </div>
                <div className="text-xs text-[#94a3b8] mt-0.5 flex flex-wrap gap-2">
                  <span>Prefix: <code className="font-mono">{key.keyPrefix}…</code></span>
                  <span>·</span>
                  <span>Created {new Date(key.createdAt).toLocaleDateString()}</span>
                  {key.lastUsedAt && (
                    <>
                      <span>·</span>
                      <span>Last used {new Date(key.lastUsedAt).toLocaleDateString()}</span>
                    </>
                  )}
                </div>
              </div>
              {key.isActive && (
                <AlertDialog>
                  <AlertDialogTrigger asChild>
                    <Button size="sm" variant="ghost" className="text-[#ef4444] hover:text-[#ef4444]">
                      <Trash2 className="h-3.5 w-3.5" />
                    </Button>
                  </AlertDialogTrigger>
                  <AlertDialogContent>
                    <AlertDialogHeader>
                      <AlertDialogTitle>Revoke API key?</AlertDialogTitle>
                      <AlertDialogDescription>
                        Revoking "{key.label}" will immediately break any integration using it.
                        This cannot be undone.
                      </AlertDialogDescription>
                    </AlertDialogHeader>
                    <AlertDialogFooter>
                      <AlertDialogCancel>Cancel</AlertDialogCancel>
                      <AlertDialogAction
                        onClick={() => revoke.mutate(key.id)}
                        className="bg-[#ef4444] hover:bg-[#dc2626] text-white"
                      >
                        Revoke key
                      </AlertDialogAction>
                    </AlertDialogFooter>
                  </AlertDialogContent>
                </AlertDialog>
              )}
            </CardContent>
          </Card>
        ))}
      </div>
    </div>
  );
}
```

### Add to settings navigation in `project-proculink/src/app/(app)/settings/layout.tsx` (or wherever settings nav lives)

Add an "API Keys" tab/link pointing to `/settings/api-keys`.

### `project-proculink/src/app/(app)/settings/connectors/page.tsx`

```tsx
'use client';

import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle
} from '@/components/ui/card';
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue
} from '@/components/ui/select';
import { ExternalLink, Plus, ToggleLeft, ToggleRight, Trash2, Zap } from 'lucide-react';

interface IntegrationSub {
  id: string;
  platform: string;
  eventType: string;
  targetUrl: string;
  isActive: boolean;
  failureCount: number;
  createdAt: string;
}

const EVENT_LABELS: Record<string, string> = {
  'order.created':   'New order created',
  'order.delivered': 'Order delivered',
  'order.failed':    'Order delivery failed',
};

const PLATFORM_LABELS: Record<string, string> = {
  zapier: 'Zapier',
  make:   'Make.com',
  custom: 'Custom',
};

export default function ConnectorsPage() {
  const qc = useQueryClient();
  const [platform, setPlatform]   = useState('custom');
  const [eventType, setEventType] = useState('order.created');
  const [targetUrl, setTargetUrl] = useState('');
  const [secret, setSecret]       = useState('');
  const [creating, setCreating]   = useState(false);

  const { data: subs = [], isLoading } = useQuery<IntegrationSub[]>({
    queryKey: ['integrations'],
    queryFn: () => apiClient.get('/api/integrations').then(r => r.data),
  });

  const create = useMutation({
    mutationFn: () =>
      apiClient.post('/api/integrations', {
        platform, eventType, targetUrl, secret: secret || undefined,
      }).then(r => r.data),
    onSuccess: () => {
      setTargetUrl('');
      setSecret('');
      setCreating(false);
      qc.invalidateQueries({ queryKey: ['integrations'] });
    },
  });

  const toggle = useMutation({
    mutationFn: (id: string) =>
      apiClient.patch(`/api/integrations/${id}/toggle`).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['integrations'] }),
  });

  const remove = useMutation({
    mutationFn: (id: string) => apiClient.delete(`/api/integrations/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['integrations'] }),
  });

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold text-[#0f172a]">Connectors &amp; Webhooks</h2>
        <p className="text-sm text-[#64748b] mt-1">
          Send real-time events to Zapier, Make.com, or any webhook endpoint when orders are created or delivered.
        </p>
      </div>

      {/* Zapier + Make.com call-to-action */}
      <div className="grid gap-3 sm:grid-cols-2">
        <Card className="border-[#f4a815]/30 bg-[#fffbeb]">
          <CardHeader className="pb-2">
            <CardTitle className="text-sm font-semibold flex items-center gap-2">
              <Zap className="h-4 w-4 text-[#f4a815]" /> Zapier
            </CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-xs text-[#78350f] mb-3">
              Connect ProcuLink to 6,000+ apps via Zapier.
              Use the "New Purchase Order Created" or "Order Delivered" triggers.
            </p>
            <Button
              size="sm"
              variant="outline"
              className="border-[#f4a815] text-[#92400e] text-xs"
              asChild
            >
              <a href="https://zapier.com/apps/proculink" target="_blank" rel="noopener noreferrer">
                Connect on Zapier <ExternalLink className="h-3 w-3 ml-1" />
              </a>
            </Button>
          </CardContent>
        </Card>

        <Card className="border-[#6366f1]/30 bg-[#eef2ff]">
          <CardHeader className="pb-2">
            <CardTitle className="text-sm font-semibold flex items-center gap-2">
              <div className="h-4 w-4 rounded bg-[#6366f1] flex items-center justify-center text-[10px] text-white font-bold">M</div>
              Make.com
            </CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-xs text-[#3730a3] mb-3">
              Build visual automation flows with ProcuLink as a trigger or action module.
            </p>
            <Button
              size="sm"
              variant="outline"
              className="border-[#6366f1] text-[#3730a3] text-xs"
              asChild
            >
              <a href="https://make.com/en/integrations/proculink" target="_blank" rel="noopener noreferrer">
                Connect on Make.com <ExternalLink className="h-3 w-3 ml-1" />
              </a>
            </Button>
          </CardContent>
        </Card>
      </div>

      {/* Custom webhook subscriptions */}
      <div>
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-sm font-medium text-[#0f172a]">Webhook subscriptions</h3>
          <Button
            size="sm"
            variant="outline"
            onClick={() => setCreating(c => !c)}
          >
            <Plus className="h-3.5 w-3.5 mr-1" />
            Add webhook
          </Button>
        </div>

        {creating && (
          <Card className="mb-4">
            <CardHeader className="pb-3">
              <CardTitle className="text-sm">New webhook subscription</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              <div className="grid gap-2 sm:grid-cols-2">
                <div>
                  <label className="text-xs text-[#64748b] font-medium mb-1 block">Platform</label>
                  <Select value={platform} onValueChange={setPlatform}>
                    <SelectTrigger className="h-8 text-sm">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="zapier">Zapier</SelectItem>
                      <SelectItem value="make">Make.com</SelectItem>
                      <SelectItem value="custom">Custom</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
                <div>
                  <label className="text-xs text-[#64748b] font-medium mb-1 block">Event</label>
                  <Select value={eventType} onValueChange={setEventType}>
                    <SelectTrigger className="h-8 text-sm">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="order.created">order.created</SelectItem>
                      <SelectItem value="order.delivered">order.delivered</SelectItem>
                      <SelectItem value="order.failed">order.failed</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
              </div>
              <div>
                <label className="text-xs text-[#64748b] font-medium mb-1 block">Target URL</label>
                <Input
                  placeholder="https://hooks.zapier.com/…"
                  value={targetUrl}
                  onChange={e => setTargetUrl(e.target.value)}
                  className="h-8 text-sm"
                />
              </div>
              <div>
                <label className="text-xs text-[#64748b] font-medium mb-1 block">
                  Signing secret <span className="text-[#94a3b8]">(optional)</span>
                </label>
                <Input
                  type="password"
                  placeholder="Used to compute X-ProcuLink-Signature"
                  value={secret}
                  onChange={e => setSecret(e.target.value)}
                  className="h-8 text-sm"
                />
              </div>
              <div className="flex gap-2 pt-1">
                <Button
                  size="sm"
                  onClick={() => create.mutate()}
                  disabled={!targetUrl.startsWith('http') || create.isPending}
                >
                  {create.isPending ? 'Saving…' : 'Save webhook'}
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setCreating(false)}>
                  Cancel
                </Button>
              </div>
              {create.isError && (
                <p className="text-xs text-[#ef4444]">
                  {(create.error as any)?.response?.data?.error ?? 'Failed to save.'}
                </p>
              )}
            </CardContent>
          </Card>
        )}

        {isLoading && <p className="text-sm text-[#64748b]">Loading…</p>}

        {!isLoading && subs.length === 0 && !creating && (
          <Card className="border-dashed">
            <CardContent className="py-8 text-center">
              <p className="text-sm text-[#94a3b8]">
                No webhook subscriptions yet. Add one above or connect via Zapier/Make.com.
              </p>
            </CardContent>
          </Card>
        )}

        <div className="space-y-2">
          {subs.map(sub => (
            <Card key={sub.id} className={sub.isActive ? '' : 'opacity-60'}>
              <CardContent className="py-3 flex items-start gap-3">
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 flex-wrap">
                    <Badge variant="outline" className="text-xs">
                      {PLATFORM_LABELS[sub.platform] ?? sub.platform}
                    </Badge>
                    <span className="text-xs font-mono text-[#64748b]">{sub.eventType}</span>
                    {!sub.isActive && (
                      <Badge variant="secondary" className="text-xs">Paused</Badge>
                    )}
                    {sub.failureCount > 0 && (
                      <Badge variant="destructive" className="text-xs">
                        {sub.failureCount} failure{sub.failureCount !== 1 ? 's' : ''}
                      </Badge>
                    )}
                  </div>
                  <p className="text-xs text-[#64748b] mt-1 truncate" title={sub.targetUrl}>
                    {sub.targetUrl}
                  </p>
                </div>
                <div className="flex items-center gap-1 shrink-0">
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => toggle.mutate(sub.id)}
                    title={sub.isActive ? 'Pause' : 'Resume'}
                  >
                    {sub.isActive
                      ? <ToggleRight className="h-4 w-4 text-[#22c55e]" />
                      : <ToggleLeft className="h-4 w-4 text-[#94a3b8]" />}
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="text-[#ef4444]"
                    onClick={() => remove.mutate(sub.id)}
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      </div>
    </div>
  );
}
```

---

## Task 10 — DbContext additions and DI registration

### DbContext additions (in `ProcuLinkDbContext.OnModelCreating`)

```csharp
// ── TenantApiKey ──────────────────────────────────────────────────────────
modelBuilder.Entity<TenantApiKey>(e =>
{
    e.ToTable("tenant_api_keys");
    e.HasKey(k => k.Id);
    e.Property(k => k.Id).HasColumnName("id");
    e.Property(k => k.OrganisationId).HasColumnName("organisation_id");
    e.Property(k => k.Label).HasColumnName("label");
    e.Property(k => k.KeyHash).HasColumnName("key_hash");
    e.Property(k => k.KeyPrefix).HasColumnName("key_prefix");
    e.Property(k => k.IsActive).HasColumnName("is_active").HasDefaultValue(true);
    e.Property(k => k.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
    e.Property(k => k.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
    e.Property(k => k.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");

    e.HasIndex(k => k.KeyHash).IsUnique();
    e.HasIndex(k => k.OrganisationId);

    e.HasOne(k => k.Organisation)
     .WithMany(o => o.ApiKeys)
     .HasForeignKey(k => k.OrganisationId);
});

// ── Organisation.Slug ─────────────────────────────────────────────────────
// Add to Organisation entity config:
modelBuilder.Entity<Organisation>(e =>
{
    // ... existing config ...
    e.Property(o => o.Slug).HasColumnName("slug").HasDefaultValue("");
    e.HasIndex(o => o.Slug).IsUnique();
});

// ── IntegrationSubscription ───────────────────────────────────────────────
modelBuilder.Entity<IntegrationSubscription>(e =>
{
    e.ToTable("integration_subscriptions");
    e.HasKey(s => s.Id);
    e.Property(s => s.Id).HasColumnName("id");
    e.Property(s => s.OrganisationId).HasColumnName("organisation_id");
    e.Property(s => s.Platform).HasColumnName("platform");
    e.Property(s => s.EventType).HasColumnName("event_type");
    e.Property(s => s.TargetUrl).HasColumnName("target_url");
    e.Property(s => s.EncryptedSecret).HasColumnName("encrypted_secret");
    e.Property(s => s.IsActive).HasColumnName("is_active").HasDefaultValue(true);
    e.Property(s => s.FailureCount).HasColumnName("failure_count").HasDefaultValue(0);
    e.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
    e.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

    e.HasIndex(s => new { s.OrganisationId, s.EventType, s.IsActive });

    e.HasOne(s => s.Organisation)
     .WithMany(o => o.IntegrationSubscriptions)
     .HasForeignKey(s => s.OrganisationId);
});
```

### DbSet additions
```csharp
public DbSet<TenantApiKey>             TenantApiKeys             => Set<TenantApiKey>();
public DbSet<IntegrationSubscription>  IntegrationSubscriptions  => Set<IntegrationSubscription>();
```

### DI registration in `Program.cs`
```csharp
// ── Wave 4: API keys + integration triggers ───────────────────────────────
using ProcuLink.Api.Auth;
// ... (add at top of file)

// In builder.Services section:
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<IIntegrationTriggerService, IntegrationTriggerService>();
```

### Slug generation at org creation
Find where new `Organisation` entities are created. In the org creation code, add:
```csharp
org.Slug = GenerateSlug(org.Name);

// Helper:
private static string GenerateSlug(string name)
{
    var slug = name.ToLowerInvariant()
        .Replace(" ", "-")
        .Replace("'", "")
        .Replace("\"", "")
        .Where(c => char.IsLetterOrDigit(c) || c == '-')
        .Aggregate(string.Empty, (s, c) => s + c);
    // Collapse multiple dashes
    while (slug.Contains("--"))
        slug = slug.Replace("--", "-");
    slug = slug.Trim('-');
    // Append 4 random hex chars for uniqueness
    slug += "-" + Guid.NewGuid().ToString("N")[..4];
    return slug;
}
```

---

## Tests (Task 10)

### `ProcuLink.Infrastructure.Tests/Services/ApiKeyServiceTests.cs`
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

public class ApiKeyServiceTests
{
    private static ProcuLinkDbContext MakeDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProcuLinkDbContext(options);
    }

    [Fact]
    public async Task CreateAsync_ReturnsRawKeyAndEntityWithPrefix()
    {
        var db  = MakeDb();
        var svc = new ApiKeyService(db);
        var orgId = Guid.NewGuid();

        // Seed org
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = "c_test", Name = "Test", Slug = "test-0001"
        });
        await db.SaveChangesAsync();

        var (entity, rawKey) = await svc.CreateAsync(orgId, "Zapier prod", null, default);

        rawKey.Should().StartWith("plk_");
        rawKey.Length.Should().BeGreaterThan(20);
        entity.KeyPrefix.Should().Be(rawKey[..8]);
        entity.IsActive.Should().BeTrue();
        entity.OrganisationId.Should().Be(orgId);
    }

    [Fact]
    public async Task RevokeAsync_SetsIsActiveFalse()
    {
        var db  = MakeDb();
        var svc = new ApiKeyService(db);
        var orgId = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = "c_test2", Name = "Test2", Slug = "test-0002"
        });
        await db.SaveChangesAsync();

        var (entity, _) = await svc.CreateAsync(orgId, "temp", null, default);
        var result      = await svc.RevokeAsync(orgId, entity.Id, default);

        result.Should().BeTrue();
        var loaded = await db.TenantApiKeys.FindAsync(entity.Id);
        loaded!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_WrongOrg_ReturnsFalse()
    {
        var db     = MakeDb();
        var svc    = new ApiKeyService(db);
        var orgId1 = Guid.NewGuid();
        var orgId2 = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id = orgId1, ClerkOrgId = "c1", Name = "Org1", Slug = "org1-aaaa"
        });
        db.Organisations.Add(new Organisation
        {
            Id = orgId2, ClerkOrgId = "c2", Name = "Org2", Slug = "org2-bbbb"
        });
        await db.SaveChangesAsync();

        var (entity, _) = await svc.CreateAsync(orgId1, "my key", null, default);
        var result      = await svc.RevokeAsync(orgId2, entity.Id, default);

        result.Should().BeFalse();
        var loaded = await db.TenantApiKeys.FindAsync(entity.Id);
        loaded!.IsActive.Should().BeTrue(); // untouched
    }
}
```

### `ProcuLink.Api.Tests/Auth/ApiKeyAuthHandlerTests.cs`
```csharp
using FluentAssertions;
using ProcuLink.Api.Auth;
using Xunit;

public class ApiKeyAuthHandlerTests
{
    [Fact]
    public void ComputeHash_SameKeyProducesSameHash()
    {
        var key  = "plk_abcdefghijklmnopqrstuvwxyz012345678";
        var h1   = ApiKeyAuthHandler.ComputeHash(key);
        var h2   = ApiKeyAuthHandler.ComputeHash(key);
        h1.Should().Be(h2);
    }

    [Fact]
    public void ComputeHash_DifferentKeysProduceDifferentHashes()
    {
        var h1 = ApiKeyAuthHandler.ComputeHash("plk_aaa" + new string('x', 30));
        var h2 = ApiKeyAuthHandler.ComputeHash("plk_bbb" + new string('x', 30));
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeHash_ProducesLowercaseHex()
    {
        var hash = ApiKeyAuthHandler.ComputeHash("plk_" + new string('a', 40));
        hash.Should().MatchRegex("^[0-9a-f]+$");
    }
}
```

---

## Execution order for parallel agents

**Block 1 (parallel):** T1 entities + migrations, T2 auth handler  
**Block 2 (sequential):** T3 ApiKeyService + Controller, T4 IngressController, T5 IntegrationSubscription + Controller, T6 TriggerService + Job  
**Block 3 (parallel):** T7 hook into services, T8 docs/integrations/ files, T9 Frontend  
**Block 4:** T10 Tests + DI wiring + build check

---

## Critical notes for agents

1. `ApiKeyService` is in `ProcuLink.Infrastructure.Services` but imports `ApiKeyAuthHandler.ComputeHash` from `ProcuLink.Api.Auth`. To avoid circular project references, move `ComputeHash` to a shared static class in `ProcuLink.Core` (e.g. `ProcuLink.Core.Security.ApiKeyHasher`), then reference it from both `ProcuLink.Api.Auth.ApiKeyAuthHandler` and `ProcuLink.Infrastructure.Services.ApiKeyService`.

2. `FireIntegrationTriggerJob` uses `IHttpClientFactory` — ensure the `"delivery"` named client is already registered in Program.cs (it is, from the existing delivery dispatcher).

3. The `Organisation` entity's `Slug` property must be added as a migration column, not just in the model. The migration must also set the default value for existing rows (empty string is fine for dev; prod would need a data migration).

4. `IngressController` references `IOrderService` from a `[FromServices]` parameter to avoid circular base-class dependencies. This is valid in ASP.NET Core 8.

5. `IntegrationTriggerService` uses `IBackgroundJobClient` from Hangfire — this is already registered via `builder.Services.AddHangfire(...)`.

6. All EF queries in Wave 4 services MUST scope by `OrganisationId` — no cross-tenant reads.
