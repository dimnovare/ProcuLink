using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class PeppolReceiverTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    // The 9915:test ServiceGroup as its SMP answers it (public test participant), trimmed to the two references.
    private const string ServiceGroup = """
        <ServiceGroup xmlns="http://busdox.org/serviceMetadata/publishing/1.0/" xmlns:id="http://busdox.org/transport/identifiers/1.0/">
          <id:ParticipantIdentifier scheme="iso6523-actorid-upis">9915:test</id:ParticipantIdentifier>
          <ServiceMetadataReferenceCollection>
            <ServiceMetadataReference href="https://smp.example.test/smp/iso6523-actorid-upis%3A%3A9915%3Atest/services/busdox-docid-qns%3A%3Aurn%3Aoasis%3Anames%3Aspecification%3Aubl%3Aschema%3Axsd%3AInvoice-2%3A%3AInvoice%23%23urn%3Acen.eu%3Aen16931%3A2017%23compliant%23urn%3Afdc%3Apeppol.eu%3A2017%3Apoacc%3Abilling%3A3.0%3A%3A2.1"/>
            <ServiceMetadataReference href="https://smp.example.test/smp/iso6523-actorid-upis%3A%3A9915%3Atest/services/busdox-docid-qns%3A%3Aurn%3Aoasis%3Anames%3Aspecification%3Aubl%3Aschema%3Axsd%3ACreditNote-2%3A%3ACreditNote%23%23urn%3Acen.eu%3Aen16931%3A2017%23compliant%23urn%3Afdc%3Apeppol.eu%3A2017%3Apoacc%3Abilling%3A3.0%3A%3A2.1"/>
          </ServiceMetadataReferenceCollection>
        </ServiceGroup>
        """;

    private sealed class FakeLocator(Func<string, SmpLocation> answer) : IPeppolSmpLocator
    {
        public FakeLocator(SmpLocation answer) : this(_ => answer) { }
        public int Calls;
        public Task<SmpLocation> LocateAsync(string host, CancellationToken ct) { Calls++; return Task.FromResult(answer(host)); }
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : DelegatingHandler, IHttpClientFactory
    {
        public List<string> Urls { get; } = [];
        public HttpClient CreateClient(string name) => name == "delivery" ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(answer(request));
        }
    }

    private static PeppolReceiverLookup Lookup(IPeppolSmpLocator locator, IHttpClientFactory http) =>
        new(locator, http, Options.Create(new PeppolLookupOptions()), NullLogger<PeppolReceiverLookup>.Instance);

    private static readonly SmpLocation Smp = new(SmpLocationKind.Found, "https://smp.example.test/smp");

    [Fact]
    public async Task Found_reads_the_service_group_and_caches_it()
    {
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ServiceGroup) });
        using var lookup = Lookup(new FakeLocator(Smp), http);

        await lookup.LookupAsync(PeppolParticipant.Parse("9915:test")!, false, default);
        var result = await lookup.LookupAsync(PeppolParticipant.Parse("9915:test")!, false, default);

        result.Should().Be(new PeppolReceiver("found", "9915:test", false, true, true));
        http.Urls.Should().Equal("https://smp.example.test/smp/iso6523-actorid-upis%3A%3A9915%3Atest");
    }

    [Theory]
    [InlineData("locator-unknown", "unknown")]
    [InlineData("smp-500", "unknown")]
    [InlineData("not-xml", "unknown")]
    [InlineData("timeout", "unknown")]
    [InlineData("redirect", "unknown")]
    [InlineData("not-a-service-group", "unknown")] // well-formed, but an error document: never "doesn't receive invoices"
    [InlineData("over-1-mb", "unknown")]
    [InlineData("not-registered", "not_found")]
    public async Task Unknown_is_never_cached_and_never_not_found(string answer, string status)
    {
        var locator = new FakeLocator(answer switch
        {
            "locator-unknown" => new(SmpLocationKind.Unknown, null),
            "not-registered" => new(SmpLocationKind.NotRegistered, null),
            _ => Smp,
        });
        var http = new FakeHttp(_ => answer switch
        {
            "smp-500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "not-xml" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<not-xml") },
            "not-a-service-group" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<Error><Message>Internal</Message></Error>") },
            "over-1-mb" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ServiceGroup + new string(' ', 1024 * 1024)) },
            "redirect" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example.test/") } },
            _ => throw new TaskCanceledException(),
        });
        using var lookup = Lookup(locator, http);
        var participant = PeppolParticipant.Parse("0208:0000000097")!;

        (await lookup.LookupAsync(participant, false, default)).Status.Should().Be(status);
        (await lookup.LookupAsync(participant, false, default)).Status.Should().Be(status);

        locator.Calls.Should().Be(status == "unknown" ? 2 : 1); // a definite "not found" is cached; an unknown never is
    }

    [Fact]
    public async Task Endpoint_is_gated_org_scoped_and_reads_the_buyer_id()
    {
        var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            // Nobody is registered, and the 9925 (VAT) registry cannot be reached.
            s.AddSingleton<IPeppolSmpLocator>(new FakeLocator(host => new(
                host == new PeppolParticipant("9925", "BE0000000097").SmlHost(new PeppolLookupOptions().SmlZone) ? SmpLocationKind.Unknown : SmpLocationKind.NotRegistered, null)))));
        HttpClient ClientFor(Guid org)
        {
            var client = host.CreateClient();
            foreach (var h in factory.ClientFor(org).DefaultRequestHeaders) client.DefaultRequestHeaders.Add(h.Key, h.Value);
            return client;
        }

        Guid Seed(Guid org, params (string Key, string Value, string Meaning)[] fields)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var flow = new Flow
            {
                Id = Guid.NewGuid(), OrgId = org, Name = "Synthetic flow", FieldsJson = DocumentJson.Serialize(new FlowFieldList([])),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var doc = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf", Status = DocumentStatus.Ready,
                ReadJson = DocumentJson.Serialize(new ReadResult("Invoice",
                    [.. fields.Select(f => new ReadField(f.Key, f.Key, f.Value, FieldTypes.Text, f.Meaning, 1, null, false, true, false))], [])),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Flows.Add(flow);
            db.FlowDocuments.Add(doc);
            db.SaveChanges();
            return doc.Id;
        }

        async Task<(HttpStatusCode Code, JsonElement Body)> Get(Guid org, Guid doc)
        {
            var response = await ClientFor(org).GetAsync($"/api/documents/{doc}/peppol-receiver");
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
        }

        var a = DocumentEngineTestFactory.OrgA;
        var byEndpoint = Seed(a, ("buyer_peppol", "0208:0000000097", FieldMeanings.ReceiverEndpointId));
        var (code, body) = await Get(a, byEndpoint);
        code.Should().Be(HttpStatusCode.OK);
        (body.GetProperty("status").GetString(), body.GetProperty("participant").GetString(), body.GetProperty("derived").GetBoolean())
            .Should().Be(("not_found", "0208:0000000097", false));

        // Derived from the VAT number: 0208 is not registered, the 9925 lookup could not finish, so the answer is unknown, never "not found".
        (code, body) = await Get(a, Seed(a, ("buyer_vat", "BE0000000097", FieldMeanings.ReceiverVatId)));
        (body.GetProperty("status").GetString(), body.GetProperty("participant").GetString(), body.GetProperty("derived").GetBoolean())
            .Should().Be(("unknown", "9925:BE0000000097", true));

        (code, body) = await Get(a, Seed(a, ("buyer_vat", "BE0000000098", FieldMeanings.ReceiverVatId))); // check digits wrong
        body.GetProperty("status").GetString().Should().Be("no_id");

        (code, body) = await Get(a, Seed(a, ("buyer_peppol", "BE 0000.000.097", FieldMeanings.ReceiverEndpointId)));
        (body.GetProperty("status").GetString(), body.GetProperty("participant").GetString()).Should().Be(("invalid_id", "BE 0000.000.097"));

        var b = DocumentEngineTestFactory.OrgB; // engine on, Peppol not listed
        (code, body) = await Get(b, Seed(b, ("buyer_peppol", "0208:0000000097", FieldMeanings.ReceiverEndpointId)));
        (code, body.GetProperty("error").GetString()).Should().Be((HttpStatusCode.Forbidden, "peppol_off"));

        (code, _) = await Get(a, Seed(b, ("buyer_peppol", "0208:0000000097", FieldMeanings.ReceiverEndpointId)));
        code.Should().Be(HttpStatusCode.NotFound);
    }
}
