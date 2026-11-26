using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Dispatchers;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

public class B2BrouterDeliveryDispatcherTests
{
    /// <summary>Stands in for B2Brouter behind the "delivery" client: answers in order, records every request. Synthetic bodies only.</summary>
    private sealed class FakeB2Brouter(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public HttpClient CreateClient(string name) => name == "delivery" ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answers[Math.Min(Requests.Count, answers.Length) - 1]());
        }
    }

    private static Func<HttpResponseMessage> Answer(int code, string body = "", int? retryAfter = null) => () =>
    {
        var r = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is { } s) r.Headers.TryAddWithoutValidation("Retry-After", s.ToString());
        return r;
    };

    private static Task<DeliveryResult> Send(FakeB2Brouter fake, string config = """{"account":"acc-1","sandbox":true}""", string key = "test_k", string type = "application/xml") =>
        new B2BrouterDeliveryDispatcher(fake, NullLogger<B2BrouterDeliveryDispatcher>.Instance) { SettleDelay = TimeSpan.Zero }.DispatchAsync(
            "<Invoice/>"u8.ToArray(), "inv.xml", type,
            new SupplierDeliveryConfig { OrgId = Guid.NewGuid(), Protocol = DeliveryProtocolConstants.B2Brouter, ConfigJson = config },
            $$"""{"apiKey":"{{key}}"}""", default);

    private const string Imported = """{"invoice":{"id":7,"number":"INV-1","state":"new"}}""";

    [Theory]
    [InlineData("sent")]
    [InlineData("error")]
    [InlineData("refused")]
    [InlineData("no_reason")]
    [InlineData("422")]
    [InlineData("429")]
    [InlineData("503")]
    [InlineData("csv")]
    public async Task Send_outcomes(string answer)
    {
        var fake = answer switch
        {
            "sent" => new FakeB2Brouter(Answer(201, Imported), Answer(200, """{"invoice":{"id":7,"state":"sent"}}""")),
            "error" => new FakeB2Brouter(Answer(201, Imported), Answer(200, """{"invoice":{"id":7,"state":"error","error_code":"PEPPOL_NO_RECEIVER"}}""")),
            "refused" => new FakeB2Brouter(Answer(201, Imported), Answer(200, """{"invoice":{"id":7,"state":"sent"}}"""), Answer(200, """{"invoice":{"id":7,"state":"registered"}}"""),
                Answer(200, """{"invoice":{"id":7,"state":"refused","errors":[{"error":"Recipient is not registered."},{"error":"Second reason"}]}}""")),
            "no_reason" => new FakeB2Brouter(Answer(201, Imported), Answer(200, """{"invoice":{"id":7,"state":"refused"}}""")),
            "422" => new FakeB2Brouter(Answer(422, """{"error":{"code":"parameter_invalid","message":"Buyer endpoint is missing"}}""")),
            "429" => new FakeB2Brouter(Answer(429, "", retryAfter: 5)),
            _ => new FakeB2Brouter(Answer(503)),
        };

        var result = await Send(fake, type: answer == "csv" ? "text/csv" : "application/xml; charset=utf-8");

        switch (answer)
        {
            case "sent":
                (result.Success, result.RemoteReference).Should().Be((true, "7"));
                var post = fake.Requests[0];
                (post.Method, post.RequestUri!.AbsoluteUri).Should().Be((HttpMethod.Post, "https://api.b2brouter.net/accounts/acc-1/invoices/import?send_after_import=true"));
                post.Headers.GetValues("X-B2B-API-Key").Should().Equal("test_k");
                post.Headers.GetValues("X-B2B-API-Version").Should().Equal("2026-06-26");
                fake.Requests[1].RequestUri!.AbsoluteUri.Should().Be("https://api.b2brouter.net/invoices/7");
                break;
            case "error":
                (result.Success, result.Outcome, result.RemoteReference).Should().Be((false, DeliveryOutcome.NotRetryable, "7"));
                result.ErrorMessage.Should().Be("B2Brouter couldn't deliver invoice INV-1: PEPPOL_NO_RECEIVER.");
                break;
            case "refused": // sent and registered are not final: the window is read to its end
                (result.Success, result.Outcome, result.RemoteReference, fake.Requests.Count).Should().Be((false, DeliveryOutcome.NotRetryable, "7", 4));
                result.ErrorMessage.Should().Be("B2Brouter couldn't deliver invoice INV-1: Recipient is not registered; Second reason.");
                break;
            case "no_reason":
                result.ErrorMessage.Should().Be("B2Brouter couldn't deliver invoice INV-1: it was refused without a reason.");
                break;
            case "422":
                (result.Outcome, result.ResponseCode).Should().Be((DeliveryOutcome.NotRetryable, 422));
                result.ErrorMessage.Should().Be("B2Brouter refused the file: Buyer endpoint is missing");
                break;
            case "429":
                (result.Success, result.Outcome, result.RetryAfter).Should().Be((false, DeliveryOutcome.Dispatched, TimeSpan.FromSeconds(5)));
                break;
            case "503": // the import may exist: never retried, never "sent"
                (result.Success, result.Outcome).Should().Be((false, DeliveryOutcome.NotRetryable));
                result.ErrorMessage.Should().Contain("didn't confirm");
                break;
            default:
                result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
                fake.Requests.Should().BeEmpty();
                break;
        }
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(401, false)]
    public async Task Test_send_is_check_only_whatever_the_key(int code, bool success)
    {
        var fake = new FakeB2Brouter(Answer(code, """{"account":{"id":"acc-1"}}"""));

        var result = await Send(fake, """{"account":"acc-1","checkOnly":true}""", key: "live_k");

        var request = fake.Requests.Should().ContainSingle().Subject;
        (request.Method, request.RequestUri!.AbsoluteUri).Should().Be((HttpMethod.Get, "https://api.b2brouter.net/accounts/acc-1"));
        result.Success.Should().Be(success);
        if (!success) result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
    }
}
