using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Infrastructure.Services.Ai;

/// <summary>
/// OpenAI-backed implementation of <see cref="ISchemaInferencer"/>.
///
/// Mirrors <see cref="OpenAiMappingService"/>:
///   • No-op (empty schema / empty mapping) when <c>Ai:OpenAI:ApiKey</c> is missing.
///   • Uses OpenAI structured outputs (JSON Schema, strict mode) for inference + proposal.
///   • Enforces the per-org monthly token cap via <see cref="IAiUsageTracker"/>.
///   • 30 second hard timeout per OpenAI call.
///
/// Build #1 from docs/format-channel-roadmap.md §5. The inferencer is not
/// applied automatically — the controller returns the proposal and the
/// existing <c>/api/orders/{id}/resolve</c> endpoint persists the user's
/// confirmation.
/// </summary>
public sealed class OpenAiSchemaInferencer : ISchemaInferencer
{
    private const string DefaultModel = "gpt-5-mini";
    private const int    SampleRowLimit       = 5;
    private const int    SampleValueLimit     = 5;
    private const int    MaxFieldsForAi       = 200;
    private const int    InferenceMaxTokens   = 1500;
    private const int    ProposalMaxTokens    = 1200;
    private static readonly TimeSpan OpenAiCallTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    // Returned by /infer when the AI provider is not configured or refuses the call.
    // The caller surfaces this to the user as "AI unavailable; map manually".
    private static readonly InferredSchema EmptySchema = new(
        FileType: "unknown",
        Fields: Array.Empty<SchemaField>(),
        SampleRows: Array.Empty<IReadOnlyDictionary<string, string?>>());

    private static readonly ProposedMapping EmptyMapping = new(Array.Empty<FieldMapping>());

    // ── Structured-output JSON schemas ───────────────────────────────────────
    // OpenAI 2.10.0 strict mode requires every property to be required AND
    // additionalProperties: false at every level (no `default`, no optionals).
    private static readonly BinaryData InferenceJsonSchema = BinaryData.FromBytes("""
        {
          "type": "object",
          "properties": {
            "fields": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name":          { "type": "string" },
                  "path":          { "type": "string" },
                  "inferredType":  { "type": "string", "enum": ["string","number","integer","date","boolean","unknown"] },
                  "exampleValues": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["name","path","inferredType","exampleValues"],
                "additionalProperties": false
              }
            }
          },
          "required": ["fields"],
          "additionalProperties": false
        }
        """u8.ToArray());

    private static readonly BinaryData ProposalJsonSchema = BinaryData.FromBytes("""
        {
          "type": "object",
          "properties": {
            "mappings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "sourceField":          { "type": "string" },
                  "targetCanonicalField": { "type": "string" },
                  "confidence":           { "type": "number", "minimum": 0, "maximum": 1 },
                  "reason":               { "type": "string" }
                },
                "required": ["sourceField","targetCanonicalField","confidence","reason"],
                "additionalProperties": false
              }
            }
          },
          "required": ["mappings"],
          "additionalProperties": false
        }
        """u8.ToArray());

    private readonly ChatClient?                  _client;
    private readonly ILogger<OpenAiSchemaInferencer> _logger;
    private readonly string                       _model;
    private readonly IAiUsageTracker?             _tracker;
    private readonly ICurrentTenantService?       _tenant;
    private readonly Func<Guid>?                  _testOrgIdProvider;

    public OpenAiSchemaInferencer(
        IConfiguration             configuration,
        ILogger<OpenAiSchemaInferencer> logger,
        ICurrentTenantService      tenant,
        IAiUsageTracker            tracker)
    {
        _logger  = logger;
        _model   = configuration["Ai:OpenAI:InferenceModel"]
                   ?? configuration["Ai:OpenAI:MappingModel"]
                   ?? DefaultModel;
        _tenant  = tenant;
        _tracker = tracker;

        var provider = configuration["Ai:Provider"];
        var apiKey   = configuration["Ai:OpenAI:ApiKey"];

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }
    }

    /// <summary>
    /// Test-only ctor: lets tests inject a deterministic
    /// <see cref="IAiUsageTracker"/> and bypass the
    /// <see cref="ICurrentTenantService"/> chain entirely. Set
    /// <paramref name="overrideClient"/> to a non-null value to force the
    /// "client present" branch without a real API key — used by tests that
    /// want to verify the cap-check short-circuit without ever calling
    /// OpenAI for real.
    /// </summary>
    internal OpenAiSchemaInferencer(
        IConfiguration             configuration,
        ILogger<OpenAiSchemaInferencer> logger,
        IAiUsageTracker?           tracker,
        Func<Guid>                 orgIdProvider,
        ChatClient?                overrideClient = null)
    {
        _logger  = logger;
        _model   = configuration["Ai:OpenAI:InferenceModel"]
                   ?? configuration["Ai:OpenAI:MappingModel"]
                   ?? DefaultModel;
        _tracker = tracker;
        _tenant  = null;
        _testOrgIdProvider = orgIdProvider;

        var provider = configuration["Ai:Provider"];
        var apiKey   = configuration["Ai:OpenAI:ApiKey"];

        if (overrideClient is not null)
        {
            _client = overrideClient;
        }
        else if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }
    }

    // ─── Public API ─────────────────────────────────────────────────────────

    public async Task<InferredSchema> InferSchemaAsync(
        Stream      sample,
        string      fileName,
        string?     contentType,
        CancellationToken ct)
    {
        if (_client is null)
        {
            _logger.LogDebug("ISchemaInferencer.InferSchemaAsync: AI provider not configured — returning empty schema.");
            return EmptySchema;
        }

        var orgId = ResolveOrgIdOrEmpty();

        // Per-org monthly token cap — never bypass when the check throws.
        if (await IsAtOrOverCapAsync(orgId, ct))
            return EmptySchema;

        // ── Deterministic local extraction (no OpenAI yet) ───────────────────
        var (fileType, localSchema) = ExtractLocalSchema(sample, fileName, contentType);

        // For formats we cannot peek (XLSX without ClosedXML, binary unknowns)
        // we still call OpenAI with whatever we have — the model can sometimes
        // recognise from filename alone. But if we have zero fields AND
        // zero sample rows we just bail.
        if (localSchema.Fields.Count == 0 && localSchema.SampleRows.Count == 0)
        {
            _logger.LogDebug("ISchemaInferencer: nothing extracted from '{FileName}' (type {FileType}); returning empty schema.", fileName, fileType);
            return new InferredSchema(fileType, Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());
        }

        // ── OpenAI structured output: refine types + dedupe + cap field count ─
        try
        {
            var promptPayload = new
            {
                fileName,
                fileType,
                fields = localSchema.Fields.Take(MaxFieldsForAi).Select(f => new
                {
                    name          = f.Name,
                    path          = f.Path,
                    inferredType  = f.InferredType,
                    exampleValues = f.ExampleValues,
                }).ToArray(),
                sampleRows = localSchema.SampleRows.Take(SampleRowLimit).ToArray(),
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("""
                    You are a schema inferencer for B2B purchase-order files.
                    You receive a deterministic first-pass extraction of fields, sample values, and
                    naive types from a procurement file (CSV, XLSX, JSON, XML, or similar).
                    Your job: return the FINAL field list — refine the inferred type, normalize
                    naming, and drop obvious junk fields (blank columns, repeated headers,
                    metadata rows). Keep field PATHS exactly as given; only correct the
                    inferredType and trim/normalise the example values.
                    inferredType MUST be one of: string, number, integer, date, boolean, unknown.
                    Never invent fields. Never re-order. Return at most 200 fields.
                    """),
                new UserChatMessage(JsonSerializer.Serialize(promptPayload, JsonOptions)),
            };

            var completion = await CompleteWithTimeoutAsync(
                messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = InferenceMaxTokens,
                    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "inferred_schema",
                        jsonSchema: InferenceJsonSchema,
                        jsonSchemaIsStrict: true),
                },
                ct);

            await RecordUsageAsync(orgId, completion.Usage?.TotalTokenCount ?? 0, ct);

            var json = completion.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json))
                return new InferredSchema(fileType, localSchema.Fields, localSchema.SampleRows);

            var dto = JsonSerializer.Deserialize<InferenceDto>(json, JsonOptions);
            if (dto?.Fields is null || dto.Fields.Count == 0)
                return new InferredSchema(fileType, localSchema.Fields, localSchema.SampleRows);

            var refined = dto.Fields.Select(f => new SchemaField(
                    Name:          f.Name          ?? string.Empty,
                    Path:          f.Path          ?? string.Empty,
                    InferredType:  NormaliseType(f.InferredType),
                    ExampleValues: f.ExampleValues ?? Array.Empty<string>()))
                .Where(f => !string.IsNullOrEmpty(f.Path))
                .Take(MaxFieldsForAi)
                .ToList();

            return new InferredSchema(fileType, refined, localSchema.SampleRows);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("OpenAI schema inference timed out for '{FileName}' (org {OrgId}) — returning local extraction.", fileName, orgId);
            return new InferredSchema(fileType, localSchema.Fields, localSchema.SampleRows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI schema inference failed for '{FileName}' (org {OrgId}) — returning local extraction.", fileName, orgId);
            return new InferredSchema(fileType, localSchema.Fields, localSchema.SampleRows);
        }
    }

    public async Task<ProposedMapping> ProposeMappingAsync(
        InferredSchema schema,
        CancellationToken ct)
    {
        if (_client is null)
        {
            _logger.LogDebug("ISchemaInferencer.ProposeMappingAsync: AI provider not configured — returning empty mapping.");
            return EmptyMapping;
        }

        if (schema is null || schema.Fields.Count == 0)
            return EmptyMapping;

        var orgId = ResolveOrgIdOrEmpty();

        if (await IsAtOrOverCapAsync(orgId, ct))
            return EmptyMapping;

        try
        {
            var promptPayload = new
            {
                fileType   = schema.FileType,
                fields     = schema.Fields.Take(MaxFieldsForAi).ToArray(),
                sampleRows = schema.SampleRows.Take(SampleRowLimit).ToArray(),
                canonicalTargets = CanonicalTargets,
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("""
                    You map source fields onto the canonical ProcuLink purchase-order model.
                    For each source field in the input, return at most one canonical target from
                    canonicalTargets (or the empty string when no good target exists).
                    Header-level targets: PoNumber, OrderDate, Currency, BuyerName.
                    Line-level targets (the row repeats per order line):
                      lines[].LineNumber, lines[].BuyerItemCode, lines[].SupplierItemCode,
                      lines[].Description, lines[].Quantity, lines[].Unit, lines[].UnitPrice.
                    Confidence MUST be 0..1.  >=0.9 = obvious match (exact name or near-exact synonym);
                    0.7..0.89 = probable (the user should review);  <0.7 = weak (user must decide).
                    Never invent canonical targets. Never propose the same canonical target twice
                    unless line-level (e.g. multi-line aggregation is allowed).
                    Reason: one short sentence for a procurement user, no jargon.
                    """),
                new UserChatMessage(JsonSerializer.Serialize(promptPayload, JsonOptions)),
            };

            var completion = await CompleteWithTimeoutAsync(
                messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = ProposalMaxTokens,
                    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "proposed_mapping",
                        jsonSchema: ProposalJsonSchema,
                        jsonSchemaIsStrict: true),
                },
                ct);

            await RecordUsageAsync(orgId, completion.Usage?.TotalTokenCount ?? 0, ct);

            var json = completion.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json))
                return EmptyMapping;

            var dto = JsonSerializer.Deserialize<ProposalDto>(json, JsonOptions);
            if (dto?.Mappings is null || dto.Mappings.Count == 0)
                return EmptyMapping;

            var mappings = dto.Mappings.Select(m => new FieldMapping(
                    SourceField:           m.SourceField           ?? string.Empty,
                    TargetCanonicalField:  m.TargetCanonicalField  ?? string.Empty,
                    Confidence:            Math.Clamp(m.Confidence, 0f, 1f),
                    Reason:                m.Reason?.Trim()        ?? string.Empty))
                .Where(m => !string.IsNullOrEmpty(m.SourceField))
                .ToList();

            return new ProposedMapping(mappings);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("OpenAI mapping proposal timed out (org {OrgId}) — returning empty mapping.", orgId);
            return EmptyMapping;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI mapping proposal failed (org {OrgId}) — returning empty mapping.", orgId);
            return EmptyMapping;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private Guid ResolveOrgIdOrEmpty()
    {
        if (_testOrgIdProvider is not null) return _testOrgIdProvider();
        try { return _tenant?.OrganisationId ?? Guid.Empty; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ICurrentTenantService.OrganisationId unavailable — treating as no-tenant.");
            return Guid.Empty;
        }
    }

    private async Task<bool> IsAtOrOverCapAsync(Guid orgId, CancellationToken ct)
    {
        if (_tracker is null || orgId == Guid.Empty) return false;
        try
        {
            if (await _tracker.IsAtOrOverLimitAsync(orgId, ct))
            {
                _logger.LogWarning(
                    "OpenAI schema inference skipped — org {OrgId} reached monthly token limit {Limit}.",
                    orgId, _tracker.MonthlyLimit);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Cap check failure must not silently bypass the cap.
            _logger.LogWarning(ex, "OpenAI cap check failed for org {OrgId}; skipping inference to be safe.", orgId);
            return true;
        }
    }

    private async Task RecordUsageAsync(Guid orgId, int totalTokens, CancellationToken ct)
    {
        if (_tracker is null || orgId == Guid.Empty || totalTokens <= 0) return;
        try
        {
            await _tracker.IncrementAsync(orgId, totalTokens, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record AI token usage for org {OrgId}.", orgId);
        }
    }

    private async Task<ChatCompletion> CompleteWithTimeoutAsync(
        IList<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken    ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OpenAiCallTimeout);
        return await _client!.CompleteChatAsync(messages, options, cts.Token);
    }

    // ─── Local (deterministic) schema extraction ────────────────────────────

    /// <summary>
    /// Pulls header + first <see cref="SampleRowLimit"/> data rows from the
    /// sample stream without invoking the AI. The returned tuple's fileType
    /// drives the AI prompt; an empty fields list signals "we could not peek
    /// inside this file format" (e.g. XLSX without ClosedXML in this assembly).
    /// </summary>
    private static (string fileType, InferredSchema schema) ExtractLocalSchema(
        Stream sample,
        string fileName,
        string? contentType)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        var sniff = SniffFileType(ext, contentType, sample);

        // Buffer to MemoryStream — callers don't expect us to consume their stream
        // and several extractors need to seek.
        using var ms = new MemoryStream();
        sample.CopyTo(ms);
        ms.Position = 0;

        switch (sniff)
        {
            case "csv":  return ("csv",  ExtractCsv(ms));
            case "json": return ("json", ExtractJson(ms));
            case "xml":  return ("xml",  ExtractXml(ms));
            case "xlsx": return ("xlsx", ExtractXlsx(ms));
            default:     return ("unknown", new InferredSchema("unknown",
                                              Array.Empty<SchemaField>(),
                                              Array.Empty<IReadOnlyDictionary<string,string?>>()));
        }
    }

    private static string SniffFileType(string ext, string? contentType, Stream sample)
    {
        ext = ext?.TrimStart('.') ?? string.Empty;
        if (ext == "csv")  return "csv";
        if (ext == "tsv")  return "csv";
        if (ext == "json") return "json";
        if (ext == "xml")  return "xml";
        if (ext == "xlsx") return "xlsx";

        // Fall back to MIME type sniffing.
        var ct = contentType?.ToLowerInvariant() ?? string.Empty;
        if (ct.Contains("csv"))                  return "csv";
        if (ct.Contains("json"))                 return "json";
        if (ct.Contains("xml"))                  return "xml";
        if (ct.Contains("spreadsheetml"))        return "xlsx";

        // Last resort: peek at the first bytes.
        if (sample.CanSeek)
        {
            var pos = sample.Position;
            try
            {
                Span<byte> head = stackalloc byte[4];
                var n = sample.Read(head);
                sample.Position = pos;
                if (n >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
                    return "xlsx";   // ZIP magic — XLSX is a zip
                if (n >= 1 && (head[0] == (byte)'{' || head[0] == (byte)'['))
                    return "json";
                if (n >= 1 && head[0] == (byte)'<')
                    return "xml";
            }
            catch { /* peek failures fall through */ }
        }
        return "unknown";
    }

    // ─── CSV extraction ─────────────────────────────────────────────────────
    // We deliberately do NOT pull in CsvHelper here — Infrastructure cannot
    // reference ProcuLink.Transform and adding a NuGet is out of scope.
    // The hand-rolled splitter handles comma/semicolon and double-quoted fields
    // (escaped as ""), which is good enough for the schema-inference path.

    private static InferredSchema ExtractCsv(Stream ms)
    {
        ms.Position = 0;
        using var reader = new StreamReader(ms, leaveOpen: true);
        var firstLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(firstLine))
            return new InferredSchema("csv", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());

        var delimiter = firstLine.Contains(';') && !firstLine.Contains(',') ? ';' : ',';
        var headers   = SplitCsvLine(firstLine, delimiter);

        var rows = new List<IReadOnlyDictionary<string, string?>>();
        var examples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var h in headers) examples[h] = new List<string>();

        for (var i = 0; i < SampleRowLimit; i++)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) { i--; continue; }

            var cells = SplitCsvLine(line, delimiter);
            var row   = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var c = 0; c < headers.Count; c++)
            {
                var val = c < cells.Count ? cells[c] : null;
                row[headers[c]] = val;
                if (!string.IsNullOrWhiteSpace(val) && examples[headers[c]].Count < SampleValueLimit)
                    examples[headers[c]].Add(val.Trim());
            }
            rows.Add(row);
        }

        var fields = headers.Select(h => new SchemaField(
                Name:          h,
                Path:          h,
                InferredType:  InferType(examples[h]),
                ExampleValues: examples[h]))
            .ToList();

        return new InferredSchema("csv", fields, rows);
    }

    private static List<string> SplitCsvLine(string line, char delim)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == '"') inQuotes = true;
                else if (ch == delim) { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
        }
        result.Add(sb.ToString());
        return result.Select(s => s.Trim()).ToList();
    }

    // ─── JSON extraction ────────────────────────────────────────────────────

    private static InferredSchema ExtractJson(Stream ms)
    {
        ms.Position = 0;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(ms);
        }
        catch
        {
            return new InferredSchema("json", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());
        }

        using (doc)
        {
            // Flatten the document into dotted paths.
            // - Top-level object → each property is a field.
            // - Top-level array  → walk first N elements; their union of paths is the field set.
            var leafExamples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var leafTypes    = new Dictionary<string, string>(StringComparer.Ordinal);
            var sampleRows   = new List<IReadOnlyDictionary<string, string?>>();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var idx = 0;
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    if (idx >= SampleRowLimit) break;
                    var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                    WalkJson(elem, string.Empty, leafExamples, leafTypes, row);
                    sampleRows.Add(row);
                    idx++;
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                WalkJson(doc.RootElement, string.Empty, leafExamples, leafTypes, row);
                sampleRows.Add(row);
            }
            else
            {
                return new InferredSchema("json", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());
            }

            var fields = leafExamples
                .Select(kv => new SchemaField(
                    Name:          kv.Key.Split('.').LastOrDefault() ?? kv.Key,
                    Path:          kv.Key,
                    InferredType:  leafTypes.TryGetValue(kv.Key, out var t) ? t : InferType(kv.Value),
                    ExampleValues: kv.Value.Take(SampleValueLimit).ToList()))
                .ToList();

            return new InferredSchema("json", fields, sampleRows);
        }
    }

    private static void WalkJson(
        JsonElement elem,
        string prefix,
        Dictionary<string, List<string>> examples,
        Dictionary<string, string>       types,
        Dictionary<string, string?>      currentRow)
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in elem.EnumerateObject())
                {
                    var path = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    WalkJson(prop.Value, path, examples, types, currentRow);
                }
                break;

            case JsonValueKind.Array:
                // Use the first element as representative for type inference and
                // emit child paths with "[]" so the AI knows this is a repeating
                // line-level field.
                var first = true;
                foreach (var item in elem.EnumerateArray())
                {
                    if (!first) break;
                    first = false;
                    WalkJson(item, prefix + "[]", examples, types, currentRow);
                }
                break;

            default:
                var (typeName, str) = JsonScalarToString(elem);
                if (!examples.TryGetValue(prefix, out var list))
                    examples[prefix] = list = new List<string>();
                if (str is not null && list.Count < SampleValueLimit)
                    list.Add(str);
                types[prefix] = typeName;
                currentRow[prefix] = str;
                break;
        }
    }

    private static (string typeName, string? str) JsonScalarToString(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.String => ("string", e.GetString()),
            JsonValueKind.Number => (e.TryGetInt64(out _) ? "integer" : "number", e.GetRawText()),
            JsonValueKind.True   => ("boolean", "true"),
            JsonValueKind.False  => ("boolean", "false"),
            JsonValueKind.Null   => ("unknown", null),
            _                    => ("unknown", e.GetRawText()),
        };

    // ─── XML extraction ─────────────────────────────────────────────────────

    private static InferredSchema ExtractXml(Stream ms)
    {
        ms.Position = 0;
        XDocument doc;
        try
        {
            doc = XDocument.Load(ms);
        }
        catch
        {
            return new InferredSchema("xml", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());
        }

        if (doc.Root is null)
            return new InferredSchema("xml", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());

        var leafExamples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var firstRow     = new Dictionary<string, string?>(StringComparer.Ordinal);
        WalkXml(doc.Root, "/" + doc.Root.Name.LocalName, leafExamples, firstRow);

        var fields = leafExamples.Select(kv => new SchemaField(
                Name:          kv.Key.Split('/').LastOrDefault() ?? kv.Key,
                Path:          kv.Key,
                InferredType:  InferType(kv.Value),
                ExampleValues: kv.Value.Take(SampleValueLimit).ToList()))
            .ToList();

        return new InferredSchema("xml", fields, new[] { (IReadOnlyDictionary<string, string?>)firstRow });
    }

    private static void WalkXml(
        XElement element,
        string path,
        Dictionary<string, List<string>> examples,
        Dictionary<string, string?> currentRow)
    {
        // Capture text-bearing leaf elements and attributes.
        if (!element.HasElements)
        {
            var value = element.Value?.Trim();
            if (!examples.TryGetValue(path, out var list))
                examples[path] = list = new List<string>();
            if (!string.IsNullOrEmpty(value) && list.Count < SampleValueLimit)
                list.Add(value);
            currentRow[path] = value;
        }

        foreach (var attr in element.Attributes())
        {
            var apath = $"{path}/@{attr.Name.LocalName}";
            if (!examples.TryGetValue(apath, out var alist))
                examples[apath] = alist = new List<string>();
            var aval = attr.Value?.Trim();
            if (!string.IsNullOrEmpty(aval) && alist.Count < SampleValueLimit)
                alist.Add(aval);
            currentRow[apath] = aval;
        }

        foreach (var child in element.Elements())
        {
            WalkXml(child, $"{path}/{child.Name.LocalName}", examples, currentRow);
        }
    }

    // ─── XLSX extraction (BCL-only — no ClosedXML in this assembly) ─────────
    // Reads xl/sharedStrings.xml and xl/worksheets/sheet1.xml directly via
    // System.IO.Compression. Returns header + first SampleRowLimit data rows.
    // Falls back to an empty schema on any malformation — the caller still
    // gets a usable fileType="xlsx" signal.

    private static InferredSchema ExtractXlsx(Stream ms)
    {
        ms.Position = 0;
        try
        {
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);

            // Shared strings table (optional).
            var sst = new List<string>();
            var sstEntry = zip.GetEntry("xl/sharedStrings.xml");
            if (sstEntry is not null)
            {
                using var s = sstEntry.Open();
                var sstDoc = XDocument.Load(s);
                var ns = sstDoc.Root?.Name.Namespace ?? XNamespace.None;
                foreach (var si in sstDoc.Descendants(ns + "si"))
                {
                    // Take concatenation of all <t> descendants — covers both plain and rich text.
                    var text = string.Concat(si.Descendants(ns + "t").Select(t => t.Value));
                    sst.Add(text);
                }
            }

            // First worksheet — find via workbook.xml.rels for absolute robustness,
            // but the conventional path xl/worksheets/sheet1.xml works for >99 % of files
            // and avoids a second XML parse here.
            var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml");
            if (sheetEntry is null) sheetEntry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase));
            if (sheetEntry is null) return new InferredSchema("xlsx", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());

            using var sheetStream = sheetEntry.Open();
            var sheetDoc = XDocument.Load(sheetStream);
            var nss = sheetDoc.Root?.Name.Namespace ?? XNamespace.None;
            var rows = sheetDoc.Descendants(nss + "row").Take(SampleRowLimit + 1).ToList();

            if (rows.Count == 0)
                return new InferredSchema("xlsx", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());

            // Row 1 = headers.
            var headers = ReadXlsxRow(rows[0], nss, sst);
            // Headerless edge case: nothing to map.
            if (headers.Count == 0)
                return new InferredSchema("xlsx", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());

            var sampleRows = new List<IReadOnlyDictionary<string, string?>>();
            var examples   = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var h in headers) examples[h] = new List<string>();

            for (var i = 1; i < rows.Count && sampleRows.Count < SampleRowLimit; i++)
            {
                var cells = ReadXlsxRow(rows[i], nss, sst);
                var row   = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (var c = 0; c < headers.Count; c++)
                {
                    var val = c < cells.Count ? cells[c] : null;
                    row[headers[c]] = val;
                    if (!string.IsNullOrWhiteSpace(val) && examples[headers[c]].Count < SampleValueLimit)
                        examples[headers[c]].Add(val.Trim());
                }
                sampleRows.Add(row);
            }

            var fields = headers.Select(h => new SchemaField(
                    Name:          h,
                    Path:          h,
                    InferredType:  InferType(examples[h]),
                    ExampleValues: examples[h]))
                .ToList();

            return new InferredSchema("xlsx", fields, sampleRows);
        }
        catch
        {
            return new InferredSchema("xlsx", Array.Empty<SchemaField>(), Array.Empty<IReadOnlyDictionary<string,string?>>());
        }
    }

    private static List<string> ReadXlsxRow(XElement rowElem, XNamespace ns, List<string> sst)
    {
        var result = new List<string>();
        foreach (var c in rowElem.Elements(ns + "c"))
        {
            var t = (string?)c.Attribute("t");
            var v = c.Element(ns + "v")?.Value;
            string? value = null;
            if (t == "s" && int.TryParse(v, out var sIdx) && sIdx >= 0 && sIdx < sst.Count)
                value = sst[sIdx];
            else if (t == "inlineStr")
                value = string.Concat(c.Element(ns + "is")?.Descendants(ns + "t").Select(x => x.Value) ?? Array.Empty<string>());
            else
                value = v;
            result.Add(value?.Trim() ?? string.Empty);
        }
        return result;
    }

    // ─── Type inference for sample values ───────────────────────────────────

    private static readonly Regex IntRegex    = new(@"^-?\d+$",                             RegexOptions.Compiled);
    private static readonly Regex NumRegex    = new(@"^-?\d+([.,]\d+)?$",                   RegexOptions.Compiled);
    private static readonly Regex DateRegex   = new(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}.*)?$|^\d{1,2}[./]\d{1,2}[./]\d{2,4}$", RegexOptions.Compiled);
    private static readonly Regex BoolRegex   = new(@"^(true|false|yes|no|y|n|0|1)$",       RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string InferType(IReadOnlyList<string> values)
    {
        if (values is null || values.Count == 0) return "unknown";

        var allInt    = true;
        var allNum    = true;
        var allDate   = true;
        var allBool   = true;
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var s = v.Trim();
            if (!IntRegex.IsMatch(s))    allInt    = false;
            if (!NumRegex.IsMatch(s))    allNum    = false;
            if (!DateRegex.IsMatch(s))   allDate   = false;
            if (!BoolRegex.IsMatch(s))   allBool   = false;
        }
        if (allInt)  return "integer";
        if (allNum)  return "number";
        if (allDate) return "date";
        if (allBool) return "boolean";
        return "string";
    }

    private static string NormaliseType(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return t switch
        {
            "string" or "number" or "integer" or "date" or "boolean" or "unknown" => t,
            "int" or "long" or "int32" or "int64" => "integer",
            "float" or "double" or "decimal" => "number",
            "datetime" or "timestamp" => "date",
            "bool" => "boolean",
            _ => "string",
        };
    }

    // ─── Canonical PO target list (for the proposal prompt) ─────────────────

    private static readonly string[] CanonicalTargets =
    {
        "PoNumber",
        "OrderDate",
        "Currency",
        "BuyerName",
        "lines[].LineNumber",
        "lines[].BuyerItemCode",
        "lines[].SupplierItemCode",
        "lines[].Description",
        "lines[].Quantity",
        "lines[].Unit",
        "lines[].UnitPrice",
    };

    // ─── DTOs for OpenAI structured outputs ─────────────────────────────────

    private sealed record InferenceDto(IReadOnlyList<InferenceFieldDto>? Fields);

    private sealed record InferenceFieldDto(
        string? Name,
        string? Path,
        string? InferredType,
        IReadOnlyList<string>? ExampleValues);

    private sealed record ProposalDto(IReadOnlyList<ProposalRowDto>? Mappings);

    private sealed record ProposalRowDto(
        string? SourceField,
        string? TargetCanonicalField,
        float   Confidence,
        string? Reason);
}
