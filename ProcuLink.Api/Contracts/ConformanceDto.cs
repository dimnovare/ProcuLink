namespace ProcuLink.Api.Contracts;

/// <summary>
/// Group V8 — a standards conformance report for one order's would-be OUTBOUND
/// document, validated against the NAMED standard profile for its format
/// (cXML 1.2 OrderRequest / UBL 2.1 Order / X12 850 / EDIFACT ORDERS D.96A).
/// Returned by <c>GET /api/orders/{id}/conformance</c>.
/// </summary>
public sealed record ConformanceReportDto(
    Guid OrderId,
    /// <summary>The output format the report was produced for (e.g. "cxml", "ubl", "x12").</summary>
    string Format,
    /// <summary>The named profile enum value, e.g. "Ubl21Order".</summary>
    string Profile,
    /// <summary>Human-readable profile name, e.g. "UBL 2.1 Order (Peppol BIS Order-only 3.0)".</summary>
    string ProfileName,
    /// <summary>Profile version, e.g. "2.1" / "004010" / "D.96A".</summary>
    string ProfileVersion,
    /// <summary>True when no Error-severity check failed.</summary>
    bool OverallPass,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<ConformanceCheckDto> Checks);

/// <summary>One named, profile-referenced conformance check row.</summary>
public sealed record ConformanceCheckDto(
    /// <summary>Stable machine code, e.g. "ubl.id".</summary>
    string Code,
    /// <summary>"Error", "Warning", or "Info".</summary>
    string Severity,
    bool Passed,
    string Message,
    /// <summary>The exact element / segment / cardinality reference in the named profile.</summary>
    string ProfileRef);
