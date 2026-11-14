using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>
/// The proof the Peppol standards need before they are listed: the invoice and the credit note the engine writes are
/// run through the REAL rule checker (phorm, the same pinned image as validator/Dockerfile) and pass the official rules.
/// An image that cannot start fails the test with its log; it never skips silently. Rule-checked, not access-point accepted.
/// </summary>
public sealed class PeppolRulesProofTests : IAsyncLifetime
{
    private const string Token = "proof-test-token";

    private readonly IContainer _phorm = new ContainerBuilder()
        .WithImage(PinnedImage())
        .WithEnvironment("PHORM_API_REQUIREDTOKEN", Token)
        .WithEnvironment("PHORM_API_RESPONSE_ONFAILURE_HTTP400", "false")
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/ping")))
        .Build();

    // The tag lives in validator/Dockerfile only: a bump there is the bump this test proves.
    private static string PinnedImage() =>
        File.ReadLines(Path.Combine(ProcuLink.Api.Tests.Architecture.OrphanDetector.FindRepoRoot(), "validator", "Dockerfile"))
            .First(l => l.StartsWith("FROM ", StringComparison.Ordinal))["FROM ".Length..].Trim();

    public async Task InitializeAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try { await _phorm.StartAsync(budget.Token); }
        catch (Exception ex)
        {
            var stderr = "";
            try { (_, stderr) = await _phorm.GetLogsAsync(); } catch { /* never created: keep the original failure */ }
            throw new InvalidOperationException($"The rule checker image did not start in 180 s: {ex.Message}\n{stderr}", ex);
        }
    }

    public Task DisposeAsync() => _phorm.DisposeAsync().AsTask();

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static ReadField Field(string label, string value, string meaning) =>
        new(FieldKey.Normalise(label), label, value, "text", meaning, 1, null, false, true, false);

    private static readonly (string Label, string Meaning)[] Columns =
    [
        ("Description", FieldMeanings.LineDescription), ("Qty", FieldMeanings.LineQuantity), ("Unit", FieldMeanings.LineUnit),
        ("Price", FieldMeanings.LineUnitPrice), ("Amount", FieldMeanings.LineAmount),
    ];

    // Latvia has no national rules; the GLN ids are checksum-valid test ids; a taxed invoice needs the seller VAT id.
    internal static ReadResult Read()
    {
        ReadRow Row(params string[] v) => new(Columns.Select((c, i) => (c, i)).ToDictionary(x => FieldKey.Normalise(x.c.Label), x => new ReadCell(v[x.i], 1, null, false, false)));
        return new ReadResult("Invoice",
            [Field("Invoice number", "INV-1", FieldMeanings.DocumentNumber), Field("Issue date", "01.11.2026", FieldMeanings.IssueDate),
             Field("Due date", "01.12.2026", FieldMeanings.DueDate), Field("Currency", "EUR", FieldMeanings.Currency),
             Field("Seller", "Seller Co", FieldMeanings.SenderName), Field("Buyer", "Buyer Co", FieldMeanings.ReceiverName),
             Field("Sender Peppol ID", "0088:0000000000000", FieldMeanings.SenderEndpointId), Field("Receiver Peppol ID", "0088:0000000000017", FieldMeanings.ReceiverEndpointId),
             Field("Seller country", "LV", FieldMeanings.SenderCountry), Field("Buyer country", "LV", FieldMeanings.ReceiverCountry),
             Field("Delivery date", "05.11.2026", FieldMeanings.DeliveryDate), Field("Seller VAT", "LV00000000000", FieldMeanings.SenderVatId), Field("Buyer reference", "BUYER-REF-1", FieldMeanings.BuyerReference),
             Field("Net", "100.00", FieldMeanings.TotalNet), Field("Tax", "21.00", FieldMeanings.TotalTax), Field("Gross", "121.00", FieldMeanings.TotalGross)],
            [new ReadTable("lines", "Lines", Columns.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning)).ToList(),
                [Row("Bolt", "3", "C62", "20.00", "60.00"), Row("Nut", "2", "C62", "20.00", "40.00")], true)]);
    }

    // The CII needs no electronic addresses: its read carries none.
    private static ReadResult ReadWithoutEndpoints()
    {
        var read = Read();
        return new ReadResult(read.KindLabel, [.. read.Fields.Where(f => f.Meaning is not (FieldMeanings.SenderEndpointId or FieldMeanings.ReceiverEndpointId))], read.Tables);
    }

    // Every address, contact and payment meaning present: what the XRechnung rules (BR-DE-1..9) need.
    private static ReadResult RichRead()
    {
        var read = Read();
        return new ReadResult(read.KindLabel,
            [.. read.Fields,
             Field("Seller street", "Main Street 1", FieldMeanings.SenderStreet), Field("Seller city", "Riga", FieldMeanings.SenderCity), Field("Seller postcode", "LV-1000", FieldMeanings.SenderPostcode),
             Field("Buyer street", "Side Street 2", FieldMeanings.ReceiverStreet), Field("Buyer city", "Riga", FieldMeanings.ReceiverCity), Field("Buyer postcode", "LV-1050", FieldMeanings.ReceiverPostcode),
             Field("Contact name", "Alex Example", FieldMeanings.SenderContactName), Field("Contact phone", "+49 30 0000000", FieldMeanings.SenderContactPhone),
             Field("Contact email", "billing@example.com", FieldMeanings.SenderContactEmail),
             Field("IBAN", "DE02120300000000202051", FieldMeanings.Iban), Field("Payment reference", "INV-1", FieldMeanings.PaymentReference)],
            read.Tables);
    }

    // The XRechnung rules accept an email electronic address (EAS EM), so the seller is also read with one.
    private static ReadResult RichReadWithEmailSeller()
    {
        var read = RichRead();
        return new ReadResult(read.KindLabel,
            [.. read.Fields.Select(f => f.Meaning == FieldMeanings.SenderEndpointId ? Field("Sender Peppol ID", "EM:billing@example.com", FieldMeanings.SenderEndpointId) : f)],
            read.Tables);
    }

    private static readonly string[] Amounts = [FieldMeanings.TotalNet, FieldMeanings.TotalTax, FieldMeanings.TotalGross, FieldMeanings.LineAmount];

    // A credit note printed negative: each listed meaning's value carries a minus sign.
    private static ReadResult Negated(ReadResult read, params string[] meanings)
    {
        string? Neg(string? meaning, string? value) => meanings.Contains(meaning) ? "-" + value : value;
        var t = read.Tables[0];
        var columns = t.Columns.ToDictionary(c => c.Key, c => c.Meaning);
        return new ReadResult(read.KindLabel, [.. read.Fields.Select(f => f with { Value = Neg(f.Meaning, f.Value) })],
            [t with { Rows = [.. t.Rows.Select(r => new ReadRow(r.Cells.ToDictionary(c => c.Key, c => c.Value with { Value = Neg(columns[c.Key], c.Value.Value) })))] }]);
    }

    [DockerRequiredFact]
    public async Task Both_documents_pass_the_official_rules()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{_phorm.Hostname}:{_phorm.GetMappedPublicPort(8080)}") };
        var validator = new PhormValidator(new Factory(http),
            Options.Create(new ValidatorOptions { BaseUrl = http.BaseAddress.ToString(), Token = Token }), NullLogger<PhormValidator>.Instance);

        var checkedRenders = 0;
        foreach (var id in new[] { "peppol-bis-billing-3", "peppol-bis-billing-3-credit-note", "en16931-cii", "en16931-cii-credit-note",
            "xrechnung-ubl", "xrechnung-ubl-credit-note", "xrechnung-cii", "xrechnung-cii-credit-note" })
        {
            // The CII reads with and without electronic addresses; the Peppol ids need them.
            var reads = id.StartsWith("en16931", StringComparison.Ordinal) ? new[] { Read(), ReadWithoutEndpoints() }
                : id is "xrechnung-ubl" or "xrechnung-cii" ? new[] { RichRead(), RichReadWithEmailSeller() }
                : id.StartsWith("xrechnung", StringComparison.Ordinal) ? new[] { RichRead() } : id == "peppol-bis-billing-3" ? new[] { Read(), RichRead() } : new[] { Read() };
            // Every credit note is also read printed negative (XRechnung with the rich fields it needs). The two whose rules check
            // line amount = quantity x price per syntax (Peppol UBL, XRechnung CII: R120) read only the amounts negative; the rest everything.
            string[] signed = id is "peppol-bis-billing-3-credit-note" or "xrechnung-cii-credit-note" ? Amounts : [.. Amounts, FieldMeanings.LineQuantity, FieldMeanings.LineUnitPrice];
            if (StandardOutputs.Find(id)!.CreditNote) reads = [.. reads, Negated(id.StartsWith("xrechnung", StringComparison.Ordinal) ? RichRead() : Read(), signed)];
            foreach (var read in reads)
            {
                var render = await StandardOutputs.RenderAsync(id, read, FlowFieldList.Empty, "lines", Guid.Empty, default);
                Assert.True(render.Ok, render.Detail);
                using var bytes = new MemoryStream();
                await render.Result!.Content.CopyToAsync(bytes);

                var outcome = await validator.ValidateAsync(StandardOutputs.Find(id)!.Vesid!, bytes.ToArray(), default);
                Assert.True(outcome.Status == ValidationStatus.Passed,
                    $"{id}: {outcome.Status} {string.Join("; ", outcome.Issues.Where(i => i.Severity == "error").Select(i => $"{i.RuleId} {i.Message}"))}");
                // phorm only warns on a wrong XRechnung CustomizationID (BR-DE-21) and only notes a UBL XRechnung without a delivery date
                // (BR-DE-TMP-32, info), so a clean run must have no warnings or info either.
                Assert.True(outcome.Issues.Count == 0,
                    $"{id}: {string.Join("; ", outcome.Issues.Select(i => $"{i.Severity} {i.RuleId} {i.Message}"))}");
                checkedRenders++;
            }
        }

        // Two Peppol renders + two CII ids read with and without electronic addresses + four XRechnung ids (the UBL and CII invoices also read with an email seller address) + the Peppol invoice read with addresses too
        // + the four credit notes read printed negative (Peppol UBL and XRechnung CII with only the amounts negative, the other two with everything negative).
        Assert.Equal(17, checkedRenders);
    }
}
