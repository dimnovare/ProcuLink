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
        .WithImage("phelger/phorm:2.2.13")
        .WithEnvironment("PHORM_API_REQUIREDTOKEN", Token)
        .WithEnvironment("PHORM_API_RESPONSE_ONFAILURE_HTTP400", "false")
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/ping")))
        .Build();

    public async Task InitializeAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try { await _phorm.StartAsync(budget.Token); }
        catch (Exception ex)
        {
            var (_, stderr) = await _phorm.GetLogsAsync();
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
             Field("Seller VAT", "LV00000000000", FieldMeanings.SenderVatId), Field("Buyer reference", "BUYER-REF-1", FieldMeanings.BuyerReference),
             Field("Net", "100.00", FieldMeanings.TotalNet), Field("Tax", "21.00", FieldMeanings.TotalTax), Field("Gross", "121.00", FieldMeanings.TotalGross)],
            [new ReadTable("lines", "Lines", Columns.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning)).ToList(),
                [Row("Bolt", "3", "C62", "20.00", "60.00"), Row("Nut", "2", "C62", "20.00", "40.00")], true)]);
    }

    [DockerRequiredFact]
    public async Task Both_documents_pass_the_official_rules()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{_phorm.Hostname}:{_phorm.GetMappedPublicPort(8080)}") };
        var validator = new PhormValidator(new Factory(http),
            Options.Create(new ValidatorOptions { BaseUrl = http.BaseAddress.ToString(), Token = Token }), NullLogger<PhormValidator>.Instance);

        foreach (var id in new[] { "peppol-bis-billing-3", "peppol-bis-billing-3-credit-note" })
        {
            var render = await StandardOutputs.RenderAsync(id, Read(), FlowFieldList.Empty, "lines", Guid.Empty, default);
            Assert.True(render.Ok, render.Detail);
            using var bytes = new MemoryStream();
            await render.Result!.Content.CopyToAsync(bytes);

            var outcome = await validator.ValidateAsync(StandardOutputs.Find(id)!.Vesid!, bytes.ToArray(), default);
            Assert.True(outcome.Status == ValidationStatus.Passed,
                $"{id}: {outcome.Status} {string.Join("; ", outcome.Issues.Where(i => i.Severity == "error").Select(i => $"{i.RuleId} {i.Message}"))}");
        }
    }
}
