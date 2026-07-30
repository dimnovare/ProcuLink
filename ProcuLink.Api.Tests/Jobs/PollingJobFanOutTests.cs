using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Worker.Jobs;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Verifies the fan-out dispatcher→child architecture for the three polling jobs:
/// EmailPollingJob, SftpPollingJob, S3PollingJob.
///
/// Strategy: use an InMemory DbContext seeded with org(s); mock IBackgroundJobClient
/// to capture Enqueue calls; assert exactly one child job is enqueued per org.
/// No real IMAP/SFTP/S3 connections are ever opened in these tests.
/// </summary>
public class PollingJobFanOutTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static Mock<IBackgroundJobClient> CapturingJobClient(out List<Job> captured)
    {
        var jobs = new List<Job>();
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((j, _) => jobs.Add(j))
            .Returns(Guid.NewGuid().ToString());
        captured = jobs;
        return mock;
    }

    // ── EmailPollingJob (dispatcher) ──────────────────────────────────────────

    [Fact]
    public async Task EmailPollingJob_TwoOrgsWithEmailConfig_EnqueuesTwoEmailPollOrgJobs()
    {
        await using var db = NewDb();
        db.Organisations.AddRange(
            // EmailPollingEnabled is the indexed poller-candidate flag, set alongside a
            // non-empty email config by EmailSettingsService / the migration backfill.
            new Organisation { Id = Guid.NewGuid(), ClerkOrgId = "a", Name = "OrgA", Slug = "org-a", EmailConfigJson = """{"enabled":true}""", EmailPollingEnabled = true },
            new Organisation { Id = Guid.NewGuid(), ClerkOrgId = "b", Name = "OrgB", Slug = "org-b", EmailConfigJson = """{"enabled":true}""", EmailPollingEnabled = true },
            // This org has no email config — flag stays false, excluded from the query
            new Organisation { Id = Guid.NewGuid(), ClerkOrgId = "c", Name = "OrgC", Slug = "org-c", EmailConfigJson = "{}" });
        await db.SaveChangesAsync();

        var mockClient = CapturingJobClient(out var enqueuedJobs);
        var dispatcher = new EmailPollingJob(
            db,
            mockClient.Object,
            NullLogger<EmailPollingJob>.Instance);

        await dispatcher.ExecuteAsync(CancellationToken.None);

        enqueuedJobs.Should().HaveCount(2, "two orgs have non-empty email config");
        enqueuedJobs.Should().AllSatisfy(j =>
            j.Type.Should().Be(typeof(EmailPollOrgJob),
                "dispatcher must enqueue EmailPollOrgJob child jobs"));
        enqueuedJobs.Should().AllSatisfy(j =>
            j.Method.Name.Should().Be(nameof(EmailPollOrgJob.ExecuteAsync)));
    }

    [Fact]
    public async Task EmailPollingJob_NoOrgsWithEmailConfig_EnqueuesNothing()
    {
        await using var db = NewDb();
        db.Organisations.Add(
            new Organisation { Id = Guid.NewGuid(), ClerkOrgId = "x", Name = "NoEmail", Slug = "no-email", EmailConfigJson = "{}" });
        await db.SaveChangesAsync();

        var mockClient = CapturingJobClient(out var enqueuedJobs);
        var dispatcher = new EmailPollingJob(
            db,
            mockClient.Object,
            NullLogger<EmailPollingJob>.Instance);

        await dispatcher.ExecuteAsync(CancellationToken.None);

        enqueuedJobs.Should().BeEmpty("no orgs have email config");
    }

    // ── SftpPollingJob (dispatcher) ───────────────────────────────────────────

    [Fact]
    public async Task SftpPollingJob_TwoEnabledOrgs_EnqueuesTwoSftpPollOrgJobs()
    {
        await using var db = NewDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var orgC = Guid.NewGuid();

        db.Set<SftpIngressConfig>().AddRange(
            new SftpIngressConfig { Id = Guid.NewGuid(), OrgId = orgA, IsEnabled = true, Host = "sftp.a.test" },
            new SftpIngressConfig { Id = Guid.NewGuid(), OrgId = orgB, IsEnabled = true, Host = "sftp.b.test" },
            // Disabled config — should not be enqueued
            new SftpIngressConfig { Id = Guid.NewGuid(), OrgId = orgC, IsEnabled = false, Host = "sftp.c.test" });
        await db.SaveChangesAsync();

        var mockClient = CapturingJobClient(out var enqueuedJobs);
        var dispatcher = new SftpPollingJob(
            db,
            mockClient.Object,
            NullLogger<SftpPollingJob>.Instance);

        await dispatcher.ExecuteAsync(CancellationToken.None);

        enqueuedJobs.Should().HaveCount(2, "two orgs have enabled SFTP configs");
        enqueuedJobs.Should().AllSatisfy(j =>
            j.Type.Should().Be(typeof(SftpPollOrgJob)));
        enqueuedJobs.Should().AllSatisfy(j =>
            j.Method.Name.Should().Be(nameof(SftpPollOrgJob.ExecuteAsync)));
    }

    [Fact]
    public async Task SftpPollingJob_NoEnabledOrgs_EnqueuesNothing()
    {
        await using var db = NewDb();
        db.Set<SftpIngressConfig>().Add(
            new SftpIngressConfig { Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), IsEnabled = false, Host = "sftp.x.test" });
        await db.SaveChangesAsync();

        var mockClient = CapturingJobClient(out var enqueuedJobs);
        var dispatcher = new SftpPollingJob(
            db,
            mockClient.Object,
            NullLogger<SftpPollingJob>.Instance);

        await dispatcher.ExecuteAsync(CancellationToken.None);

        enqueuedJobs.Should().BeEmpty();
    }

    // ── S3PollingJob (dispatcher) ─────────────────────────────────────────────

    [Fact]
    public async Task S3PollingJob_TwoEnabledOrgs_EnqueuesTwoS3PollOrgJobs()
    {
        await using var db = NewDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var orgC = Guid.NewGuid();

        db.Set<S3IngressConfig>().AddRange(
            new S3IngressConfig { Id = Guid.NewGuid(), OrgId = orgA, IsEnabled = true, BucketName = "bucket-a" },
            new S3IngressConfig { Id = Guid.NewGuid(), OrgId = orgB, IsEnabled = true, BucketName = "bucket-b" },
            // Disabled config
            new S3IngressConfig { Id = Guid.NewGuid(), OrgId = orgC, IsEnabled = false, BucketName = "bucket-c" });
        await db.SaveChangesAsync();

        var mockClient = CapturingJobClient(out var enqueuedJobs);
        var dispatcher = new S3PollingJob(
            db,
            mockClient.Object,
            NullLogger<S3PollingJob>.Instance);

        await dispatcher.ExecuteAsync(CancellationToken.None);

        enqueuedJobs.Should().HaveCount(2, "two orgs have enabled S3 configs");
        enqueuedJobs.Should().AllSatisfy(j =>
            j.Type.Should().Be(typeof(S3PollOrgJob)));
        enqueuedJobs.Should().AllSatisfy(j =>
            j.Method.Name.Should().Be(nameof(S3PollOrgJob.ExecuteAsync)));
    }

    [Fact]
    public async Task S3PollingJob_NoEnabledOrgs_EnqueuesNothing()
    {
        await using var db = NewDb();
        await db.SaveChangesAsync();

        var mockClient = CapturingJobClient(out var enqueuedJobs);
        var dispatcher = new S3PollingJob(
            db,
            mockClient.Object,
            NullLogger<S3PollingJob>.Instance);

        await dispatcher.ExecuteAsync(CancellationToken.None);

        enqueuedJobs.Should().BeEmpty();
    }

    // ── Child job isolation: SftpPollOrgJob delegates to ISftpIngressService ──

    [Fact]
    public async Task SftpPollOrgJob_CallsPollAsync_WithCorrectOrgId()
    {
        var orgId = Guid.NewGuid();
        var sftpMock = new Mock<ISftpIngressService>();
        sftpMock.Setup(s => s.PollAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var child = new SftpPollOrgJob(sftpMock.Object, TestDoubles.PermissiveBilling.Service(), NullLogger<SftpPollOrgJob>.Instance);

        await child.ExecuteAsync(orgId, CancellationToken.None);

        sftpMock.Verify(s => s.PollAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Child job isolation: S3PollOrgJob delegates to IS3IngressService ──────

    [Fact]
    public async Task S3PollOrgJob_CallsPollAsync_WithCorrectOrgId()
    {
        var orgId = Guid.NewGuid();
        var s3Mock = new Mock<IS3IngressService>();
        s3Mock.Setup(s => s.PollAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var child = new S3PollOrgJob(s3Mock.Object, TestDoubles.PermissiveBilling.Service(), NullLogger<S3PollOrgJob>.Instance);

        await child.ExecuteAsync(orgId, CancellationToken.None);

        s3Mock.Verify(s => s.PollAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
