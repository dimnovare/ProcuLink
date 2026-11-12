using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

public class S3DeliveryDispatcherTests
{
    private const string Creds = "{\"secretKey\":\"s\"}";

    private static (S3DeliveryDispatcher Dispatcher, Mock<IAmazonS3> S3) Build()
    {
        var s3 = new Mock<IAmazonS3>();
        var factory = new Mock<IAmazonS3ClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>())).Returns(s3.Object);
        var guard = new OutboundRequestGuard(new ConfigurationBuilder().Build(), NullLogger<OutboundRequestGuard>.Instance);
        return (new S3DeliveryDispatcher(factory.Object, guard, NullLogger<S3DeliveryDispatcher>.Instance), s3);
    }

    private static SupplierDeliveryConfig Config(string prefix) => new()
    {
        Protocol = "s3",
        ConfigJson = $"{{\"bucket\":\"b\",\"prefix\":\"{prefix}\",\"region\":\"auto\",\"accessKeyId\":\"k\"}}",
    };

    [Theory]
    [InlineData("in/", "in/a.csv")]
    [InlineData("/in//x", "in/x/a.csv")]
    [InlineData("", "a.csv")]
    public async Task Puts_prefix_and_file_name_with_the_content_type(string prefix, string key)
    {
        var (dispatcher, s3) = Build();
        PutObjectRequest? sent = null;
        s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        var result = await dispatcher.DispatchAsync([1], "a.csv", "text/csv", Config(prefix), Creds, default);

        result.Success.Should().BeTrue();
        sent!.BucketName.Should().Be("b");
        sent.Key.Should().Be(key);
        sent.ContentType.Should().Be("text/csv");
        sent.UseChunkEncoding.Should().BeFalse(); // R2 rejects aws-chunked trailer uploads
    }

    [Fact]
    public async Task Refuses_a_non_https_endpoint_without_calling_the_bucket()
    {
        var (dispatcher, s3) = Build();
        var config = new SupplierDeliveryConfig
        {
            Protocol = "s3",
            ConfigJson = "{\"bucket\":\"b\",\"region\":\"auto\",\"accessKeyId\":\"k\",\"serviceUrl\":\"http://169.254.169.254\"}",
        };

        var result = await dispatcher.DispatchAsync([1], "a.csv", "text/csv", config, Creds, default);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        s3.Verify(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_resolver_failure_is_retryable_but_a_policy_refusal_is_not()
    {
        var (dispatcher, s3) = Build();
        var config = new SupplierDeliveryConfig
        {
            Protocol = "s3",
            ConfigJson = "{\"bucket\":\"b\",\"region\":\"auto\",\"accessKeyId\":\"k\",\"serviceUrl\":\"https://no-such-host.invalid\"}",
        };

        var result = await dispatcher.DispatchAsync([1], "a.csv", "text/csv", config, Creds, default);

        result.Success.Should().BeFalse();
        result.Outcome.Should().NotBe(DeliveryOutcome.NotRetryable);
        s3.Verify(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", DeliveryOutcome.NotRetryable)]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket", DeliveryOutcome.NotRetryable)]
    [InlineData(HttpStatusCode.MovedPermanently, "PermanentRedirect", DeliveryOutcome.NotRetryable)]
    [InlineData(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed", DeliveryOutcome.NotRetryable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", DeliveryOutcome.Dispatched)]
    public async Task Maps_a_refusal_to_a_result_without_throwing(HttpStatusCode status, string errorCode, DeliveryOutcome outcome)
    {
        var (dispatcher, s3) = Build();
        s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("x") { StatusCode = status, ErrorCode = errorCode });

        var result = await dispatcher.DispatchAsync([1], "a.csv", "text/csv", Config("in/"), Creds, default);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(outcome);
        result.ResponseCode.Should().Be((int)status);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
