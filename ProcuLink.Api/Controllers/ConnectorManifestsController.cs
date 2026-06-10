using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Connectors;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Group V7 — Connector SDK: read-only API that exposes the static connector manifest catalog.
/// Manifests describe the configuration shape of each wired delivery dispatcher / ERP connector.
///
/// <para>
/// No org-scoping is needed — the manifests are global static definitions, not per-tenant data.
/// Clerk auth is required so unauthenticated callers cannot enumerate the connector surface area.
/// </para>
///
/// <para>Routes:<br/>
///   <c>GET  /api/connector-manifests</c>              — list all manifests<br/>
///   <c>GET  /api/connector-manifests/{key}</c>         — single manifest or 404<br/>
///   <c>POST /api/connector-manifests/{key}/validate-config</c> — pure config validation (no persistence)
/// </para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/connector-manifests")]
public sealed class ConnectorManifestsController : ControllerBase
{
    /// <summary>List all available connector manifests.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ConnectorManifestDto>), StatusCodes.Status200OK)]
    public IActionResult GetAll()
    {
        return Ok(ConnectorManifestCatalog.All.Select(ToDto));
    }

    /// <summary>Get a single connector manifest by key (e.g. "http", "sftp", "erp_erply").</summary>
    [HttpGet("{key}")]
    [ProducesResponseType(typeof(ConnectorManifestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetByKey(string key)
    {
        if (!ConnectorManifestCatalog.ByKey.TryGetValue(key, out var manifest))
            return NotFound();

        return Ok(ToDto(manifest));
    }

    /// <summary>
    /// Validate a config object against the connector's manifest field descriptors.
    /// Pure, stateless: nothing is persisted and no secrets are logged.
    ///
    /// <para>
    /// The request body is the JSON object that would be stored in
    /// <c>SupplierDeliveryConfig.ConfigJson</c> (unencrypted fields only).
    /// Secret fields (stored encrypted in credentials) are accepted but not required —
    /// this endpoint validates config-blob shape, not credential completeness.
    /// </para>
    /// </summary>
    [HttpPost("{key}/validate-config")]
    [ProducesResponseType(typeof(ValidateConfigResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult ValidateConfig(string key, [FromBody] Dictionary<string, object?> config)
    {
        if (!ConnectorManifestCatalog.ByKey.TryGetValue(key, out var manifest))
            return NotFound();

        var postedKeys = new HashSet<string>(
            config is null ? Enumerable.Empty<string>() : config.Keys,
            StringComparer.OrdinalIgnoreCase);

        var manifestKeys = new HashSet<string>(
            manifest.Fields.Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);

        // Required fields that are absent (null/missing) from the posted object.
        var missing = manifest.Fields
            .Where(f => f.Required && !postedKeys.Contains(f.Name))
            .Select(f => f.Name)
            .ToList();

        // Posted keys that are not declared in the manifest.
        var unknown = postedKeys
            .Where(k => !manifestKeys.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new ValidateConfigResultDto(
            Valid: missing.Count == 0 && unknown.Count == 0,
            Missing: missing,
            Unknown: unknown));
    }

    // ── mapper ──────────────────────────────────────────────────────────────
    private static ConnectorManifestDto ToDto(ConnectorManifest m) => new(
        m.Key,
        m.DisplayName,
        m.Transport,
        m.AuthType,
        m.Fields.Select(f => new ConnectorConfigFieldDto(
            f.Name, f.Label, f.Type, f.Required, f.Secret, f.HelpText)).ToList(),
        m.Capabilities,
        m.DocsRef);
}
