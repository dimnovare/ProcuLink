using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Ksef;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Output.Ksef;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>Against a fake KSeF (no network): synthetic NIP 1111111111, a fake token, and a self-signed certificate made here.</summary>
public class KsefDeliveryDispatcherTests
{
    private const string Token = "fake-ksef-token-1";
    private const string AccessToken = "fake-access-token-SECRET";

    /// <summary>Stands in for KSeF behind the "ksef" client; serves one self-signed RSA certificate for both usages and records every request.</summary>
    private sealed class FakeKsef(int invoiceStatus = 200, int authStatus = 200, string invoiceAnswer = """{"referenceNumber":"INV-1"}""") : HttpMessageHandler, IHttpClientFactory
    {
        public readonly RSA Key = RSA.Create(2048);
        public readonly List<(HttpMethod Method, string Path, string Body)> Requests = [];

        public HttpClient CreateClient(string name) =>
            name == KsefOptions.HttpClientName ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return path switch
            {
                _ when path.EndsWith("/auth/challenge") => Json("""{"challenge":"20261125-CR-0000000000-0000000000-00","timestamp":"2026-11-25T10:00:00Z","timestampMs":1764064800000,"clientIp":"127.0.0.1"}"""),
                _ when path.EndsWith("/security/public-key-certificates") => Json(new JsonArray(new JsonObject
                {
                    ["certificate"] = Convert.ToBase64String(Certificate().RawData), ["certificateId"] = "c1", ["publicKeyId"] = "k1",
                    ["usage"] = new JsonArray("KsefTokenEncryption", "SymmetricKeyEncryption"),
                    ["validFrom"] = DateTime.UtcNow.AddDays(-1).ToString("O"), ["validTo"] = DateTime.UtcNow.AddDays(1).ToString("O"),
                }).ToJsonString()),
                _ when path.EndsWith("/auth/ksef-token") => Json("""{"referenceNumber":"AUTH-1","authenticationToken":{"token":"auth-tok","validUntil":"2026-11-25T11:00:00Z"}}""", HttpStatusCode.Accepted),
                _ when path.EndsWith("/auth/AUTH-1") => Json("{\"status\":{\"code\":" + authStatus + ",\"description\":\"status\"}}"),
                _ when path.EndsWith("/auth/token/redeem") => Json("{\"accessToken\":{\"token\":\"" + AccessToken + "\"},\"refreshToken\":{\"token\":\"r\"}}"),
                _ when path.EndsWith("/sessions/online") => Json("""{"referenceNumber":"SES-1","validUntil":"2026-11-25T11:00:00Z"}""", HttpStatusCode.Created),
                _ when path.EndsWith("/sessions/online/SES-1/invoices") => Json(invoiceAnswer, HttpStatusCode.Accepted),
                _ when path.EndsWith("/invoices/INV-1/upo") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<UPO/>", Encoding.UTF8, "application/xml") },
                _ when path.EndsWith("/invoices/INV-1") => Json(invoiceStatus switch
                {
                    200 => """{"ksefNumber":"1111111111-20261125-ABCDEF123456-7A","status":{"code":200,"description":"Sukces"}}""",
                    440 => """{"status":{"code":440,"description":"Duplikat faktury","extensions":{"originalKsefNumber":"1111111111-20261120-000000000000-01"}}}""",
                    var c => "{\"status\":{\"code\":" + c + ",\"description\":\"Przetwarzanie\"}}",
                }),
                _ when path.EndsWith("/close") => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private X509Certificate2 Certificate() =>
            new CertificateRequest("CN=Fake KSeF", Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    // A real FA(3) file: seller 1111111111, buyer 9999999999, one line 1 × 100.00 at 23 %, issued 2026-11-20.
    private static readonly byte[] File = Fa3();

    private static byte[] Fa3()
    {
        var (doc, refusal) = Fa3InvoiceWriter.Build(
            new InvoiceEntity { InvoiceNumber = "FV/2026/11/0001", IssueDate = new DateOnly(2026, 11, 20), Currency = "PLN", SubTotal = 100.00m, TaxTotal = 23.00m, GrandTotal = 123.00m },
            [new InvoiceLineEntity { Description = "Usługa A", Quantity = 1, UnitPrice = 100.00m, LineTotal = 100.00m, TaxRate = 23 }],
            new PeppolPartyOptions
            {
                SellerName = "Przykładowa Spółka Testowa sp. z o.o.", SellerVatId = "1111111111", SellerCountry = "PL",
                SellerStreet = "ul. Testowa 1", SellerPostcode = "00-001", SellerCity = "Warszawa", BuyerName = "Fikcyjny Odbiorca S.A.", BuyerVatId = "9999999999",
            }, DateTime.UtcNow);
        refusal.Should().BeNull();
        return Encoding.UTF8.GetBytes(doc!.Declaration + "\n" + doc);
    }

    private static Task<DeliveryResult> Send(FakeKsef ksef, string configJson) =>
        new KsefDeliveryDispatcher(new KsefClient(ksef, Options.Create(new KsefOptions
            {
                AuthPollInterval = TimeSpan.FromMilliseconds(5), StatusPollInterval = TimeSpan.FromMilliseconds(5),
                StatusPollBudget = TimeSpan.FromMilliseconds(150), UpoBudget = TimeSpan.FromMilliseconds(50),
            })), NullLogger<KsefDeliveryDispatcher>.Instance)
            .DispatchAsync(File, "FV.xml", "application/xml",
                new SupplierDeliveryConfig { OrgId = Guid.NewGuid(), Protocol = DeliveryProtocolConstants.Ksef, ConfigJson = configJson },
                $$"""{"token":"{{Token}}"}""", default);

    private static string Field(string json, string name) => JsonNode.Parse(json)![name]!.GetValue<string>();

    [Fact]
    public async Task Test_send_signs_in_and_sends_nothing()
    {
        var ksef = new FakeKsef();
        var result = await Send(ksef, """{"environment":"production","nip":"1111111111","checkOnly":true}""");

        (result.Success, result.ErrorMessage).Should().Be((true, "Signed in to KSeF Production for NIP 1111111111. No invoice was sent."));
        ksef.Requests.Should().NotContain(r => r.Path.Contains("/sessions"));
        var sent = ksef.Requests.Single(r => r.Path.EndsWith("/auth/ksef-token")).Body;
        Encoding.UTF8.GetString(ksef.Key.Decrypt(Convert.FromBase64String(Field(sent, "encryptedToken")), RSAEncryptionPadding.OaepSHA256))
            .Should().Be($"{Token}|1764064800000");
    }

    [Theory]
    [InlineData("""{"referenceNumber":"INV-1"}""", "INV-1")]   // still working when the poll budget ends
    [InlineData("{}", "SES-1")]                                // accepted with no usable reference
    public async Task Accepted_invoice_is_never_resubmitted(string answer, string reference)
    {
        var ksef = new FakeKsef(invoiceStatus: 150, invoiceAnswer: answer);
        var result = await Send(ksef, """{"environment":"test","nip":"1111111111"}""");

        (result.Outcome, result.RemoteReference, result.FailureKind).Should().Be((DeliveryOutcome.NotRetryable, reference, (string?)null)); // ours: it pages
        result.ErrorMessage.Should().Contain(reference).And.Contain("sending again may issue it twice")
            .And.NotContain(Token).And.NotContain(AccessToken);
        ksef.Requests.Count(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/invoices")).Should().Be(1);
    }

    [Theory]
    [InlineData("no environment")]
    [InlineData("440")]
    [InlineData("200")]
    public async Task Refusals_are_plain_and_final(string @case)
    {
        var ksef = new FakeKsef(invoiceStatus: @case == "440" ? 440 : 200);
        var result = await Send(ksef, @case == "no environment" ? """{"nip":"1111111111"}""" : """{"environment":"test","nip":"1111111111"}""");

        switch (@case)
        {
            case "no environment":
                (result.Outcome, result.ErrorMessage).Should().Be((DeliveryOutcome.NotRetryable,
                    "This KSeF destination isn't set up correctly (environment or NIP). Edit it and save again. Nothing was sent."));
                ksef.Requests.Should().BeEmpty();
                result.FailureKind.Should().Be(FlowDeliveryFailureKind.Setup);
                break;
            case "440":
                result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
                result.ErrorMessage.Should().Contain("KSeF number 1111111111-20261120-000000000000-01");
                result.RemoteReference.Should().Be("1111111111-20261120-000000000000-01");
                result.FailureKind.Should().Be(FlowDeliveryFailureKind.Refused);
                break;
            default:
                (result.Success, result.RemoteReference, result.RemoteEnvironment).Should().Be((true, "1111111111-20261125-ABCDEF123456-7A", "test"));
                Encoding.UTF8.GetString(result.Receipt!).Should().Be("<UPO/>");
                var session = JsonNode.Parse(ksef.Requests.Single(r => r.Path.EndsWith("/sessions/online")).Body)!["encryption"]!;
                var key = ksef.Key.Decrypt(Convert.FromBase64String(session["encryptedSymmetricKey"]!.GetValue<string>()), RSAEncryptionPadding.OaepSHA256);
                var invoice = ksef.Requests.Single(r => r.Path.EndsWith("/invoices")).Body;
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.DecryptCbc(Convert.FromBase64String(Field(invoice, "encryptedInvoiceContent")), Convert.FromBase64String(session["initializationVector"]!.GetValue<string>()), PaddingMode.PKCS7)
                        .Should().Equal(File);
                }
                Field(invoice, "invoiceHash").Should().Be(Convert.ToBase64String(SHA256.HashData(File)));
                break;
        }
    }
}
