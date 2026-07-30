using System.Text;
using Scriban;
using Scriban.Parsing;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Output;

/// <summary>
/// WHOLE-DOCUMENT Scriban template output mode (heart-piece-flex flexible mapping — template mode).
/// Renders ONE supplier-authored Scriban template string against the canonical order to produce the
/// entire output document. This is the most flexible mapping surface: a supplier's exact required
/// structure (e.g. an REDACTED-NAME-style nested JSON with a <c>lines</c> array and <c>for.last</c>
/// comma handling) is written directly as a template, instead of being assembled field-by-field from
/// an <see cref="OutputMappingConfig"/>.
///
/// <para>The template sees the stable model namespace built by <see cref="ScribanOrderModel"/>
/// (documented in <c>docs/qa/2026-06-09-scriban-template-namespace.md</c>): top-level globals
/// <c>OrderNr</c>/<c>PoNumber</c>, <c>OrderDate</c>, <c>Currency</c>, <c>BuyerName</c>,
/// <c>SupplierName</c>, <c>ShippingAddress</c>, <c>CustomFields</c>, and <c>Lines</c> (with intuitive
/// aliases like <c>Line.DistributorPid</c> / <c>Line.OrderedPrice</c> / <c>Line.Qty</c>).</para>
///
/// <para><b>Opt-in &amp; safe.</b> Only invoked when an order carries a non-blank
/// <see cref="OrderMappingOverride.OutputTemplate"/>; with no template the existing fixed/override
/// paths are byte-for-byte unchanged. The same review guard the fixed transforms enforce
/// (<c>NeedsReview</c> / null <c>SupplierItemCode</c>) is applied first, so a template can never
/// deliver an unresolved order.</para>
///
/// <para><b>Never crashes the transform.</b> A compile or render error is returned as a clear
/// <see cref="RenderOutcome"/> failure (it never throws); the caller surfaces it as a validation
/// failure and the order stays un-transformed. The sandbox is the shared one from
/// <see cref="ScribanFieldEvaluator.BuildSafeTemplateContext"/> (identity renamer, relaxed member
/// access so missing fields render empty, bounded loops, the curated pure builtins, no IO/loader).</para>
/// </summary>
public sealed class ScribanTemplateTransformService
{
    /// <summary>Default content type stamped on a template artifact when the override does not set one.</summary>
    public const string DefaultContentType = "application/json";

    /// <summary>
    /// Defence-in-depth cap on rendered document length. Generous for any realistic order; guards
    /// against a pathological template producing an enormous string.
    /// </summary>
    private const int MaxOutputLength = 20_000_000;

    /// <summary>Outcome of rendering a template: either rendered <see cref="Text"/>, or an <see cref="Error"/>.</summary>
    public readonly record struct RenderOutcome(bool Ok, string? Text, string? Error)
    {
        public static RenderOutcome Success(string text) => new(true, text, null);
        public static RenderOutcome Failure(string error) => new(false, null, error);
    }

    /// <summary>
    /// Build the override-driven document from the whole-document template. Throws
    /// <see cref="TransformValidationException"/> if any line is unresolved (same guard as the fixed
    /// transforms), <see cref="ArgumentException"/> if the override carries no template, and
    /// <see cref="TransformTemplateException"/> (with a clear message) if the template fails to
    /// compile or render — the order is never delivered from a broken template.
    ///
    /// <para><paramref name="catalogLookup"/> (Phase 2): optional pre-loaded supplier catalog so the
    /// template's <c>{{ line.catalog.* }}</c> accessor resolves. Null/absent = byte-identical to
    /// today (empty catalog object per line).</para>
    /// </summary>
    public TransformResult Build(
        PurchaseOrderEntity order,
        OrderMappingOverride @override,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup = null)
    {
        ValidateOrder(order);

        if (string.IsNullOrWhiteSpace(@override.OutputTemplate))
            throw new ArgumentException("Override has no output template.", nameof(@override));

        var outcome = Render(@override.OutputTemplate!, order, @override, catalogLookup);
        if (!outcome.Ok)
            throw new TransformTemplateException(outcome.Error!);

        var contentType = string.IsNullOrWhiteSpace(@override.OutputTemplateContentType)
            ? DefaultContentType
            : @override.OutputTemplateContentType!.Trim();

        var bytes = Encoding.UTF8.GetBytes(outcome.Text!);
        return new TransformResult(new MemoryStream(bytes), contentType, ExtensionFor(contentType));
    }

    /// <summary>
    /// Pure render: compile + render <paramref name="template"/> against the model built from
    /// <paramref name="order"/> / <paramref name="override"/>. NEVER throws — a compile or runtime
    /// error returns <see cref="RenderOutcome.Failure"/> with a clear message. Does NOT enforce the
    /// review guard (callers that persist use <see cref="Build"/>); this overload is for previews/tests.
    /// </summary>
    public RenderOutcome Render(
        string template,
        PurchaseOrderEntity order,
        OrderMappingOverride? @override,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup = null)
    {
        if (template is null)
            return RenderOutcome.Failure("Template is null.");

        Template parsed;
        try
        {
            parsed = Template.Parse(template);
        }
        catch (Exception ex)
        {
            return RenderOutcome.Failure($"Template failed to compile: {ex.Message}");
        }

        if (parsed.HasErrors)
        {
            var first = parsed.Messages.FirstOrDefault(m => m.Type == ParserMessageType.Error);
            return RenderOutcome.Failure($"Template failed to compile: {first?.Message ?? "syntax error"}");
        }

        try
        {
            var model   = ScribanOrderModel.Build(order, @override, catalogLookup);
            var context = ScribanFieldEvaluator.BuildSafeTemplateContext(model, enableSafeBuiltins: true);

            var rendered = parsed.Render(context) ?? string.Empty;

            if (rendered.Length > MaxOutputLength)
                return RenderOutcome.Failure($"Template output exceeded {MaxOutputLength} characters.");

            return RenderOutcome.Success(rendered);
        }
        catch (Exception ex)
        {
            return RenderOutcome.Failure($"Template failed to render: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Same review guard as the fixed transforms / <see cref="MappedTransformService"/>.</summary>
    private static void ValidateOrder(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();

        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);
    }

    /// <summary>
    /// File extension for an operator-supplied template content type, so the delivered artifact is
    /// named sensibly. Consults <see cref="ProcuLink.Core.Services.Delivery.DeliveryMediaTypes"/>
    /// FIRST: a template declaring <c>application/EDI-X12</c> must be named the same way the
    /// built-in X12 transform names its output (<c>.x12</c>), not differently. The rows below cover
    /// only the content types the table does not own — alternate spellings and free-text types an
    /// operator may legitimately choose.
    /// </summary>
    private static string ExtensionFor(string contentType)
    {
        if (ProcuLink.Core.Services.Delivery.DeliveryMediaTypes
                .TryExtensionForContentType(contentType, out var known))
            return known;

        var ct = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return ct switch
        {
            "text/json"                 => ".json",
            "text/xml"                  => ".xml",
            "text/plain"                => ".txt",
            "application/edifact"       => ".edi",
            "application/octet-stream"  => ".dat",
            _                           => ".txt",
        };
    }
}

/// <summary>
/// Raised by <see cref="ScribanTemplateTransformService.Build"/> when the whole-document template
/// fails to compile or render. Carries a user-facing message; the transform caller reverts status
/// and returns the message (the order is never delivered from a broken template).
/// </summary>
public sealed class TransformTemplateException : Exception
{
    public TransformTemplateException(string message) : base(message) { }
}
