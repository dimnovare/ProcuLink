using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// <see cref="StorageHealthCheck"/> must report only what a real round trip established.
///
/// <para><b>The defect.</b> It reported <c>Healthy("Storage reachable.")</c> whenever
/// <see cref="IFileStorageService.GetSignedDownloadUrlAsync"/> returned a non-empty string.
/// Pre-signing is LOCAL — <c>AmazonS3Client.GetPreSignedURL</c> is synchronous and makes no network
/// call — so a wrong <c>ServiceURL</c> signs a perfectly-formed URL and the readiness endpoint
/// announced reachability nobody had observed, while every upload 403'd. This project has already
/// lost time to exactly that (an S3↔R2 <c>serviceUrl</c> mismatch).</para>
///
/// <para>The tests below pin the property that fixes it: the words "Storage reachable" may appear
/// ONLY when the probe came back <see cref="StorageProbeStatus.Reachable"/>. A signing-only success
/// is not a reachability observation and must not be rendered as one.</para>
/// </summary>
public class StorageHealthCheckObservesReachabilityTests
{
    private sealed class ProbeStorage : IFileStorageService
    {
        private readonly Func<CancellationToken, Task<StorageProbe>> _probe;
        public ProbeStorage(Func<CancellationToken, Task<StorageProbe>> probe) => _probe = probe;

        /// <summary>Signing always "succeeds" — the point is that this can no longer carry the verdict.</summary>
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult("https://signed.example.test/looks-perfectly-fine");

        public Task<StorageProbe> ProbeAsync(CancellationToken ct) => _probe(ct);

        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A double that implements NOTHING beyond the required members — no probe override.</summary>
    private sealed class NoProbeStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult("https://signed.example.test/looks-perfectly-fine");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Task<HealthCheckResult> RunAsync(IFileStorageService storage) =>
        new StorageHealthCheck(storage).CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task ObservedRoundTrip_ReportsHealthyAndReachable()
    {
        var result = await RunAsync(new ProbeStorage(_ =>
            Task.FromResult(StorageProbe.Reachable("Storage answered a HEAD request (404 for the probe key, which is expected)."))));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("Storage reachable", result.Description);
    }

    /// <summary>
    /// THE DEFECT, VERBATIM: credentials sign fine and the URL comes back non-empty, but the
    /// backend rejects the request. The old check called this "Storage reachable."
    /// </summary>
    [Fact]
    public async Task BackendRejectsCredentials_IsDegraded_AndNeverClaimsReachable()
    {
        var result = await RunAsync(new ProbeStorage(_ =>
            Task.FromResult(StorageProbe.Unreachable("Storage rejected our credentials (403 InvalidAccessKeyId)."))));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("Storage reachable", result.Description);
        Assert.Contains("unreachable", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A wrong <c>ServiceURL</c>: signing is flawless (the double proves it), the round trip is not.
    /// </summary>
    [Fact]
    public async Task WrongEndpoint_IsDegraded_EvenThoughSigningSucceeds()
    {
        var result = await RunAsync(new ProbeStorage(_ =>
            Task.FromResult(StorageProbe.Unreachable(
                "Storage did not answer a HEAD request: HttpRequestException: No such host is known."))));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("Storage reachable", result.Description);
    }

    /// <summary>
    /// Unconfigured storage stays READY (a dev box without R2 keys must not read as broken) but is
    /// not permitted to borrow the word "reachable" from a check that never ran.
    /// </summary>
    [Fact]
    public async Task NotProbed_IsHealthy_ButSaysReachabilityWasNotChecked()
    {
        var result = await RunAsync(new ProbeStorage(_ =>
            Task.FromResult(StorageProbe.NotProbed("Storage is not configured on this host."))));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.DoesNotContain("Storage reachable", result.Description);
        Assert.Contains("not checked", result.Description);
    }

    /// <summary>
    /// The interface default must not be a silent pass: a double that never implements a probe
    /// reports NotProbed, so it can never be mistaken for an observed reachable backend.
    /// </summary>
    [Fact]
    public async Task ProviderWithoutAProbe_DefaultsToNotProbed_NotToReachable()
    {
        var result = await RunAsync(new NoProbeStorage());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.DoesNotContain("Storage reachable", result.Description);
        Assert.Contains("not checked", result.Description);
    }

    /// <summary>A hung backend is bounded, and a timeout is not reachability.</summary>
    [Fact]
    public async Task ProbeThatNeverAnswers_IsBoundedAndDegraded()
    {
        var result = await RunAsync(new ProbeStorage(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StorageProbe.Reachable("unreachable code");
        }));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("Storage reachable", result.Description);
    }

    /// <summary>
    /// A probe that throws despite the contract is still not evidence of reachability.
    /// </summary>
    [Fact]
    public async Task ProbeThatThrows_IsDegraded_NotReachable()
    {
        var result = await RunAsync(new ProbeStorage(_ =>
            Task.FromException<StorageProbe>(new InvalidOperationException("client exploded"))));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("Storage reachable", result.Description);
    }
}
