using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Loads the vendored OASIS UBL 2.1 Order schema and its import closure into a compiled
/// <see cref="XmlSchemaSet"/>, resolving every <c>xsd:import</c> from the embedded copies and from
/// nowhere else.
///
/// <para><b>Why a catalog and not just a file path.</b> The 14 vendored schemas are embedded
/// resources (see the <c>EmbeddedResource</c> block in <c>ProcuLink.Transform.csproj</c> and
/// <c>Conformance/Schemas/ubl-2.1/PROVENANCE.md</c>), so they resolve identically in a unit test, in
/// the API container and in the Worker container with no working-directory dependency. But
/// <see cref="XmlSchemaSet"/> resolves imports by URI, so the resources need a URI space. This class
/// is that mapping: <c>https://schemas.proculink.invalid/ubl-2.1/&lt;relative path&gt;</c> ⇄ embedded
/// resource. The host is under <c>.invalid</c>, which RFC 2606 reserves and guarantees can never
/// resolve in DNS — so even a resolver bug cannot become a network call.</para>
///
/// <para><b>Only the root schema is added; the rest is walked.</b> Adding all 14 up front would also
/// work, and would hide the thing worth knowing: whether the closure is actually complete. Starting
/// from <c>UBL-Order-2.1.xsd</c> alone forces every import to travel through
/// <see cref="VendoredOnlyResolver"/>, so a file that is missing from the vendored set is a file that
/// cannot be served.</para>
///
/// <para><b>Fail closed, loudly.</b> This is the subtle part, and it is the reason for
/// <see cref="Problems"/>. When an <c>xsd:import</c> cannot be resolved, .NET does NOT throw — not
/// even when the resolver itself throws. It swallows the exception, raises a
/// <see cref="XmlSeverityType.Warning"/> ("Cannot resolve the 'schemaLocation' attribute") and
/// carries on compiling. The result is a schema set that loads happily with components missing, and
/// every document validated against it then fails with "type not declared" — a missing FILE
/// presenting itself as a malformed DOCUMENT. So every warning and error raised during load is
/// collected and any single one aborts the build with a message naming the schema. A schema set that
/// exists here is a schema set that resolved completely.</para>
/// </summary>
internal static class UblSchemaCatalog
{
    /// <summary>
    /// Synthetic URI space for the embedded schemas. <c>.invalid</c> is reserved by RFC 2606 and is
    /// guaranteed never to resolve — belt and braces behind <see cref="VendoredOnlyResolver"/>,
    /// which refuses every host including this one for anything it has not vendored.
    /// </summary>
    internal const string CatalogPrefix = "https://schemas.proculink.invalid/ubl-2.1/";

    /// <summary>Resource path of the document schema the closure is walked from.</summary>
    internal const string RootSchemaPath = "maindoc/UBL-Order-2.1.xsd";

    /// <summary>The UBL 2.1 Order-2 target namespace, i.e. the expected root element's namespace.</summary>
    internal const string OrderNamespace = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";

    /// <summary>The vendored package version, as published by OASIS.</summary>
    internal const string SchemaVersion = "UBL 2.1 OS (04 November 2013)";

    /// <summary>
    /// Reader settings for the vendored schemas.
    ///
    /// <para><c>DtdProcessing.Parse</c> is required: <c>UBL-xmldsig-core-schema-2.1.xsd</c> carries a
    /// DOCTYPE, and without this the closure cannot load at all. It is safe because OASIS
    /// republished that file with the external PUBLIC/SYSTEM identifiers already stripped, leaving an
    /// internal subset resolvable from the file's own bytes — and because <c>XmlResolver = null</c>
    /// here means an external identifier could not be fetched even if a future re-vendor
    /// reintroduced one; the load would throw instead. <c>UblSchemaClosureTests</c> asserts over the
    /// vendored bytes that none is present.</para>
    /// </summary>
    private static XmlReaderSettings SchemaReaderSettings => new()
    {
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
    };

    private static readonly Lazy<XmlSchemaSet> LazySet =
        new(() => Build(OpenEmbedded), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The compiled schema set, built once per process.
    ///
    /// <para>Shared across threads deliberately. A compiled <see cref="XmlSchemaSet"/> is only unsafe
    /// to share while it is being MUTATED, and this one never is after
    /// <see cref="XmlSchemaSet.Compile"/>: <see cref="UblSchemaValidator"/> validates with
    /// <c>ProcessSchemaLocation</c> and <c>ProcessInlineSchema</c> both off, so a document's own
    /// <c>xsi:schemaLocation</c> or inline schema cannot add anything to it.</para>
    /// </summary>
    internal static XmlSchemaSet Compiled => LazySet.Value;

    /// <summary>
    /// Builds and compiles the set, walking the import closure from <see cref="RootSchemaPath"/>
    /// through <paramref name="open"/>.
    /// </summary>
    /// <param name="open">
    /// Opens a vendored schema by catalog-relative path (e.g. <c>common/UBL-CommonBasicComponents-2.1.xsd</c>),
    /// or returns null when it is not vendored. Injected so a test can build a deliberately
    /// INCOMPLETE catalog and prove the load fails loudly — the property this class exists to hold,
    /// and one that cannot be tested through the real, complete resource set.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The closure did not resolve completely, or the root schema is not embedded.
    /// </exception>
    internal static XmlSchemaSet Build(Func<string, Stream?> open)
    {
        var problems = new List<string>();
        var set = new XmlSchemaSet { XmlResolver = new VendoredOnlyResolver(open) };
        set.ValidationEventHandler += (_, e) => problems.Add(Describe(e));

        using (var rootStream = open(RootSchemaPath)
                   ?? throw new InvalidOperationException(
                       $"The vendored UBL root schema '{RootSchemaPath}' is not embedded in " +
                       $"{typeof(UblSchemaCatalog).Assembly.GetName().Name}. See " +
                       "ProcuLink.Transform/Conformance/Schemas/ubl-2.1/PROVENANCE.md."))
        using (var reader = XmlReader.Create(rootStream, SchemaReaderSettings, CatalogPrefix + RootSchemaPath))
        {
            set.Add(null, reader);
        }

        set.Compile();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The vendored UBL 2.1 schema closure did not resolve completely, so schema validation " +
                "would report missing FILES as malformed DOCUMENTS. .NET reports this as a warning and " +
                "keeps going, which is why it is escalated here. Re-vendor per " +
                "ProcuLink.Transform/Conformance/Schemas/ubl-2.1/PROVENANCE.md. Unresolved: " +
                string.Join(" | ", problems));
        }

        return set;
    }

    private static string Describe(ValidationEventArgs e) =>
        e.Exception is { } x && !string.IsNullOrEmpty(x.SourceUri)
            ? $"{e.Severity} at {x.SourceUri} line {x.LineNumber}: {e.Message}"
            : $"{e.Severity}: {e.Message}";

    /// <summary>Opens a vendored schema from this assembly's embedded resources, or null.</summary>
    internal static Stream? OpenEmbedded(string catalogRelativePath) =>
        typeof(UblSchemaCatalog).Assembly
            .GetManifestResourceStream("ubl-2.1/" + catalogRelativePath);

    /// <summary>Every embedded vendored schema, by catalog-relative path.</summary>
    internal static IReadOnlyList<string> EmbeddedSchemaPaths() =>
        typeof(UblSchemaCatalog).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith("ubl-2.1/", StringComparison.Ordinal)
                        && n.EndsWith(".xsd", StringComparison.Ordinal))
            .Select(n => n["ubl-2.1/".Length..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// An <see cref="XmlResolver"/> that serves the vendored schemas and refuses everything else.
    ///
    /// <para>It has no network client, no <see cref="XmlUrlResolver"/> base and no filesystem access:
    /// the only bytes it can return come from <see cref="_open"/>. That is the point — an
    /// <see cref="XmlResolver"/> reachable from document content is an SSRF surface as well as a
    /// build-fragility, so this one cannot fetch a URL even when handed a perfectly valid one.
    /// <c>UblSchemaResolverIsClosedTests</c> proves it by asking for the real OASIS URL the schemas
    /// were vendored from and asserting the refusal.</para>
    /// </summary>
    internal sealed class VendoredOnlyResolver : XmlResolver
    {
        private readonly Func<string, Stream?> _open;

        internal VendoredOnlyResolver(Func<string, Stream?> open) => _open = open;

        /// <summary>Every URI this resolver was asked for, in order. Read by the closed-resolver tests.</summary>
        internal List<string> Requested { get; } = new();

        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            ArgumentNullException.ThrowIfNull(absoluteUri);
            Requested.Add(absoluteUri.ToString());

            var uri = absoluteUri.ToString();
            if (!uri.StartsWith(CatalogPrefix, StringComparison.Ordinal))
            {
                throw new XmlException(
                    $"Refused to resolve '{absoluteUri}': the UBL schema validator resolves only the " +
                    "vendored copies under " + CatalogPrefix + ", never the network.");
            }

            var relative = uri[CatalogPrefix.Length..];
            return _open(relative)
                ?? throw new XmlException(
                    $"'{relative}' is imported by the UBL 2.1 closure but is not vendored. " +
                    "The vendored set must be the COMPLETE import closure — see " +
                    "ProcuLink.Transform/Conformance/Schemas/ubl-2.1/PROVENANCE.md.");
        }
    }
}
