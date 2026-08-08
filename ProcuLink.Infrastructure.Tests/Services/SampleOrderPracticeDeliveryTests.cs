using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Infrastructure;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Tests.Support;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-39 §4.5 — what the practice run says will happen, versus what pressing send does.
///
/// <para><b>The reported finding does not reproduce, and the code says why.</b> §4.5 reads the
/// sample supplier's live HTTP delivery config — <c>autoDeliver: true</c>, pointed at an expired
/// <c>webhook.site</c> bin — as SEEDED, and concludes the demo target is dead. No code path has
/// ever written it: <c>git log -S "DeliveryProtocolConstants.Http" -- SampleOrderService.cs</c>
/// returns nothing, <c>webhook.site</c> appears nowhere outside QA documents, and the seeder only
/// ever writes <c>email</c> with <c>AutoDeliver = false</c>. That row is hand-made QA residue on
/// one organisation, and the order that 404'd was an uploaded CSV routed to the sample supplier,
/// not a practice run.</para>
///
/// <para><b>What IS wrong is worse.</b> Both of the seeder's guards return early without ever
/// looking at whether a delivery config already exists, so <c>DeliveryConfigured</c> answers
/// "I wrote one just now", while its own documentation claims "the sample supplier now has a
/// delivery setup". When a foreign config is sitting there, those are opposite answers — and the
/// review screen states the false one as fact:</para>
///
/// <code>
/// Email sending isn't configured on this ProcuLink deployment yet, so this run will stop at
/// "no delivery is set up".
/// </code>
///
/// <para>The run does not stop. Pressing send dispatches through whatever config is there. On the
/// organisation in the QA pass that was a dead bin, which is the 404 §4.5 recorded. On an
/// organisation where someone pointed the sample supplier at a working endpoint it is a practice
/// order arriving at a real supplier, moments after the screen said nothing would.</para>
///
/// <para>So the honest fix is neither "find a better echo endpoint" nor "stop before delivery" —
/// WP-27 already chose the echo endpoint, and it is the operator's own mailbox, which needs no
/// third party. It is that the practice run must know the difference between "no delivery target"
/// and "a delivery target I did not set up for practice", and say which.</para>
/// </summary>
public class SampleOrderPracticeDeliveryTests
{
    private static async Task<(Guid orgId, Guid supplierId)> WithForeignHttpConfigAsync(
        ProcuLinkDbContext db, Guid orgId)
    {
        // The shape found on production: a hand-made HTTP target on the sample supplier, with
        // autoDeliver ON — which the seeder never sets.
        var supplier = new Supplier
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = "ProcuLink Sample Supplier",
            Code      = "__sample__",
            IsSample  = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Suppliers.Add(supplier);
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id                   = Guid.NewGuid(),
            OrgId                = orgId,
            SupplierId           = supplier.Id,
            Protocol             = DeliveryProtocolConstants.Http,
            AutoDeliver          = true,
            ConfigJson           = """{"url":"https://webhook.site/9a1f85b7-0000-0000-0000-000000000000/e2e-sample-xml"}""",
            EncryptedCredentials = string.Empty,
            OutputFormat         = "xml",
            CreatedAt            = DateTime.UtcNow,
            UpdatedAt            = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (orgId, supplier.Id);
    }

    [Fact]
    public async Task ForeignTarget_IsReported_NotMistakenForNoDeliveryAtAll()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        await WithForeignHttpConfigAsync(db, orgId);
        // No email provider: the seeder cannot replace the foreign config with the practice mailbox.
        var svc = TestSampleOrderService.Create(db, emailConfigured: false);

        var result = await svc.CreateAndEnqueueAsync(orgId, "user_abc", "maria@northgate.example", default);

        result.PracticeDelivery.Should().Be(PracticeDeliveryState.ExistingTarget);
        result.DeliveryConfigured.Should().BeFalse("the practice mailbox was not set up");
    }

    [Fact]
    public async Task ForeignTarget_IsLeftAlone_NotDeleted()
    {
        // Reporting it is this packet's job. Deleting an operator's row is not, and a seeder that
        // quietly removes delivery configuration is a worse surprise than the one being fixed.
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        await WithForeignHttpConfigAsync(db, orgId);
        var svc = TestSampleOrderService.Create(db, emailConfigured: false);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", "maria@northgate.example", default);

        var config = await db.SupplierDeliveryConfigs.SingleAsync(c => c.OrgId == orgId);
        config.Protocol.Should().Be(DeliveryProtocolConstants.Http);
        config.ConfigJson.Should().Contain("webhook.site");
    }

    [Fact]
    public async Task ForeignTarget_IsStillRepointedToThePracticeMailbox_WhenItCanBe()
    {
        // Unchanged behaviour, pinned so the new reporting path cannot quietly disable it: with a
        // usable address and an email provider, the practice run takes the supplier back over.
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        await WithForeignHttpConfigAsync(db, orgId);
        var svc = TestSampleOrderService.Create(db);

        var result = await svc.CreateAndEnqueueAsync(orgId, "user_abc", "maria@northgate.example", default);

        result.PracticeDelivery.Should().Be(PracticeDeliveryState.EmailedToYou);
        result.DeliveryConfigured.Should().BeTrue();

        var config = await db.SupplierDeliveryConfigs.SingleAsync(c => c.OrgId == orgId);
        config.Protocol.Should().Be(DeliveryProtocolConstants.Email);
        config.AutoDeliver.Should().BeFalse();
        config.ConfigJson.Should().Contain("maria@northgate.example");
    }

    [Fact]
    public async Task NoConfigAtAll_IsStillTheHonestNotSetUpEnding()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db, emailConfigured: false);

        var result = await svc.CreateAndEnqueueAsync(orgId, "user_abc", "maria@northgate.example", default);

        result.PracticeDelivery.Should().Be(PracticeDeliveryState.NotSetUp);
        result.DeliveryConfigured.Should().BeFalse();
        (await db.SupplierDeliveryConfigs.Where(c => c.OrgId == orgId).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NoAddressSupplied_AndNothingConfigured_IsAlsoNotSetUp()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        var result = await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default);

        result.PracticeDelivery.Should().Be(PracticeDeliveryState.NotSetUp);
        result.DeliveryConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task NoAddressSupplied_ButAForeignTargetExists_IsReported()
    {
        // The path a pre-WP-27 caller (or a direct POST with no body) takes. The old code answered
        // `false` here and the screen said the run would stop; it would in fact have fired at the
        // existing endpoint.
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        await WithForeignHttpConfigAsync(db, orgId);
        var svc = TestSampleOrderService.Create(db);

        var result = await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default);

        result.PracticeDelivery.Should().Be(PracticeDeliveryState.ExistingTarget);
    }

    [Fact]
    public void DeliveryConfiguredMeansExactlyThePracticeMailbox()
    {
        // The bool is wire contract — the API still returns it and older clients still read it.
        // Bind it to the state rather than letting the two drift into disagreeing.
        PracticeDeliveryState.All.Should().HaveCount(3, "a fourth state needs a decision, not a default");

        foreach (var state in PracticeDeliveryState.All)
            new ProcuLink.Core.Services.SampleOrderResult(Guid.NewGuid(), state)
                .DeliveryConfigured
                .Should().Be(state == PracticeDeliveryState.EmailedToYou, $"state was {state}");
    }
}
