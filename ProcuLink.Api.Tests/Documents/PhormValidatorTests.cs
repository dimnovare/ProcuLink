using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class PhormValidatorTests
{
    private const string Vesid = "eu.peppol.bis3:invoice:2026.5";

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(answer(request));
        }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string file) =>
        new(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Documents", "Fixtures", file))) };

    private static PhormValidator Validator(Stub stub, string? baseUrl = "http://validator.test:8080", string? token = "t0ken") =>
        new(stub, Options.Create(new ValidatorOptions { BaseUrl = baseUrl, Token = token }), NullLogger<PhormValidator>.Instance);

    [Fact]
    public async Task Pass_and_fail_bodies_map_to_outcomes()
    {
        var passed = await Validator(new Stub(_ => Json("phorm-pass.json"))).ValidateAsync(Vesid, [1], default);
        Assert.Equal(ValidationStatus.Passed, passed.Status);
        Assert.DoesNotContain(passed.Issues, i => i.Severity == "error");

        var failed = await Validator(new Stub(_ => Json("phorm-fail.json"))).ValidateAsync(Vesid, [1], default);
        Assert.Equal(ValidationStatus.Failed, failed.Status);
        Assert.False(string.IsNullOrEmpty(failed.Issues[0].RuleId));
        Assert.Equal("error", failed.Issues[0].Severity);
        Assert.DoesNotContain(failed.Issues, i => i.Message.StartsWith('['));
    }

    [Fact]
    public async Task A_failure_with_only_warnings_is_unavailable()
    {
        const string body = """{"success":false,"results":[{"items":[{"errorLevel":"WARN","errorID":"W-1","errorText":"[W-1]-careful"}]}]}""";
        var warned = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        Assert.Equal(ValidationStatus.Unavailable, (await Validator(warned).ValidateAsync(Vesid, [1], default)).Status);
    }

    [Fact]
    public async Task Request_carries_the_token_and_the_vesid()
    {
        var stub = new Stub(_ => Json("phorm-pass.json"));
        await Validator(stub).ValidateAsync(Vesid, [1], default);

        Assert.Equal($"/api/validate/{Vesid}", stub.Seen!.RequestUri!.AbsolutePath);
        Assert.Equal("t0ken", Assert.Single(stub.Seen.Headers.GetValues("X-Token")));
        Assert.Equal("application/xml", stub.Seen.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task No_url_timeout_or_500_is_unavailable()
    {
        var ok = new Stub(_ => Json("phorm-pass.json"));
        Assert.Equal(ValidationStatus.Unavailable, (await Validator(ok, baseUrl: "").ValidateAsync(Vesid, [1], default)).Status);
        Assert.Null(ok.Seen);

        var timeout = new Stub(_ => throw new TaskCanceledException("timed out"));
        Assert.Equal(ValidationStatus.Unavailable, (await Validator(timeout).ValidateAsync(Vesid, [1], default)).Status);

        var broken = new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Equal(ValidationStatus.Unavailable, (await Validator(broken).ValidateAsync(Vesid, [1], default)).Status);
    }
}
