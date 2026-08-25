using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// A GDPR erasure has to remove EVERY row tied to the erased order. Nothing enforced that.
///
/// <para><b>The defect this exists to catch.</b> <c>order_supplier_suggestions</c> carries an
/// <c>OrderId</c> and survived an erasure completely: unlike every other order child it has no
/// foreign key to <c>purchase_orders</c> (its only FK is to <c>organisations</c>), so deleting the
/// order neither cascaded to it nor errored, and <c>DataErasureService</c> did not name it. Rows
/// were left holding a dangling <c>OrderId</c>, <c>SignalsJson</c> — which embeds document-derived
/// identity text such as the sender's email domain — and <c>DecidedBy</c>, an operator's Clerk user
/// id. Neither retention sweep touches that table, so nothing would ever have removed them.</para>
///
/// <para><b>Why the existing tests could not see it.</b> <c>DataErasureServiceTests</c> seeds a
/// hand-written graph against a reduced <c>ErasureTestDbContext</c> on EF InMemory, which
/// <c>Ignore</c>s <see cref="ProcuLink.Core.Entities.OrderParty"/> and
/// <see cref="ProcuLink.Core.Entities.SourceCapture"/> outright and never seeds this table at all.
/// A test whose corpus is a hand-written list cannot report a table nobody thought of — which is
/// the whole failure mode here. So this guard derives its corpus from the MODEL instead, and its
/// answer from the FKs and the service source, never from a list typed out below.</para>
///
/// <para><b>What it asserts.</b> Every mapped entity carrying an <c>OrderId</c> must be accounted
/// for in exactly one of three ways, and each answer is read from a producer rather than declared:
/// <list type="number">
///   <item><description>the service deletes it — read out of <c>DataErasureService.cs</c> itself;
///   </description></item>
///   <item><description>Postgres cascades it away — read out of the EF model's foreign keys;
///   </description></item>
///   <item><description>it is a declared residual with a stated reason — the ONLY hand-written
///   input, and <see cref="EveryDeclaredResidual_IsStillAnOrderTiedEntity"/> walks it back the
///   other way so it cannot rot into cover for a table that changed shape.</description></item>
/// </list></para>
///
/// <para>The cascade arm matters on its own: <see cref="ProcuLink.Core.Entities.OrderParty"/> (the
/// only person-level contact columns in the schema — <c>ContactName</c>, <c>Email</c>,
/// <c>Phone</c>) and <see cref="ProcuLink.Core.Entities.SourceCapture"/> (<c>RawText</c>, the full
/// extracted document) are erased ONLY by <c>ON DELETE CASCADE</c>. The service never names them.
/// One migration authored without that cascade would turn the highest-PII rows in the schema into
/// permanent leftovers, silently. This test is what makes that a build failure.</para>
///
/// <para><b>Widened 2026-08-25, because the corpus was narrower than the question.</b> It was built
/// from entities carrying a property literally named <c>OrderId</c>, so it was blind to every order
/// child that spells the link differently.
/// <see cref="ProcuLink.Core.Entities.OrderConfirmationEntity"/> ties to the order through
/// <c>PurchaseOrderId</c>, and <see cref="ProcuLink.Core.Entities.OrderConfirmationLineEntity"/>
/// through <c>OrderConfirmationId</c> plus <c>PurchaseOrderLineId</c>. Neither carries an
/// <c>OrderId</c>, so neither was ever in the corpus, and an erasure that left supplier
/// acknowledgement rows behind — with their confirmed prices, quantities and supplier reference
/// text — would have kept this guard green. A guard whose corpus is narrower than the property it
/// checks reports only on the tables it happens to see, which is the same failure mode as a
/// hand-written list, just harder to notice.</para>
///
/// <para>The corpus now takes any entity with a foreign key targeting <c>purchase_orders</c> or
/// <c>purchase_order_lines</c>, plus any entity with a scalar <c>OrderId</c>,
/// <c>PurchaseOrderId</c> or <c>PurchaseOrderLineId</c>. The cascade arm walks TRANSITIVELY for the
/// same reason: <c>order_confirmation_lines</c> reaches the order through <c>order_confirmations</c>,
/// so a single-hop FK read would have called it unaccounted for even though the database really does
/// erase it. <see cref="MinimumOrderTiedEntities"/> is the floor that stops the widened sweep quietly
/// shrinking back, and <see cref="TheCorpusSeesOrderChildrenThatDoNotSpellItOrderId"/> pins the two
/// entities the narrow version missed by name.</para>
///
/// <para><b>Both newly-visible tables turned out to be genuinely erased</b> — <c>DataErasureService</c>
/// removes confirmations and confirmation lines explicitly (<c>OrderConfirmationLines.RemoveRange</c>
/// then <c>OrderConfirmations.RemoveRange</c>), and the database cascades them too. So the widening
/// closed a blind spot rather than a live GDPR gap. It closed it after the fact, which is the
/// argument for widening now rather than the next time someone adds a table.</para>
/// </summary>
public sealed class OrderErasureCoverageTests
{
    /// <summary>
    /// Order-tied entities the erasure deliberately does NOT delete, each with the reason. This is
    /// the one hand-written input in the file, so it is kept to the rows that genuinely must
    /// survive and is checked in both directions.
    /// </summary>
    private static readonly Dictionary<string, string> DeclaredResiduals = new(StringComparer.Ordinal)
    {
        [nameof(ProcuLink.Core.Entities.ImportedSftpFile)] =
            "Pull-ingress ledger. The source file still lives on the CUSTOMER's SFTP server and the " +
            "poller lists it every cycle, so deleting the row would re-import the erased order as a new " +
            "one. DataErasureService tombstones it instead (OrderId = IngressDedupe.TerminalOrderId), " +
            "which both keeps the file skipped and severs the link to the erased order.",
        [nameof(ProcuLink.Core.Entities.ImportedS3Object)] =
            "Same pull-ingress tombstone as ImportedSftpFile: the object remains in the customer's own " +
            "S3 bucket, so the ledger row must outlive the order to stop a re-import, with its OrderId " +
            "severed so the erased order can never be resurrected.",
    };

    /// <summary>
    /// A floor under the model sweep. If the walk ever finds fewer order-tied entities than this,
    /// the sweep itself broke and every other assertion in the file went vacuously green.
    ///
    /// <para>Raised from 12 to 16 with the 2026-08-25 widening. Both numbers are measured, not
    /// guessed: the narrow, name-only corpus finds 15 entities and the widened one finds 17. The
    /// floor is deliberately set ABOVE the narrow count, so reverting
    /// <see cref="IsOrderTied"/> to <c>FindProperty("OrderId")</c> — which would silently drop
    /// <c>OrderConfirmationEntity</c> and <c>OrderConfirmationLineEntity</c> back out of view —
    /// fails here as well as in
    /// <see cref="TheCorpusSeesOrderChildrenThatDoNotSpellItOrderId"/>. A floor set to the narrow
    /// count would have let that revert pass. Raise it as the schema grows; never lower it to make
    /// a red build green.</para>
    /// </summary>
    private const int MinimumOrderTiedEntities = 16;

    private static IModel BuildModel() =>
        new ProcuLinkDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase($"erasure-coverage-{Guid.NewGuid()}")
                .Options).Model;

    /// <summary>
    /// The two order tables an order child can be tied to. A foreign key at either one makes the
    /// entity order-tied regardless of what the property is called, which is the half the old
    /// name-only corpus could not see.
    /// </summary>
    private static readonly Type[] OrderTables =
    [
        typeof(ProcuLink.Core.Entities.PurchaseOrderEntity),
        typeof(ProcuLink.Core.Entities.PurchaseOrderLineEntity),
    ];

    /// <summary>
    /// The scalar spellings of an order link in this model. Names alone are not enough (hence
    /// <see cref="OrderTables"/>) but they are not redundant either: a column can point at an order
    /// without a foreign key behind it, which is exactly the shape
    /// <c>order_supplier_suggestions</c> had when it produced this schema's first GDPR orphan.
    /// </summary>
    private static readonly string[] OrderLinkProperties =
        ["OrderId", "PurchaseOrderId", "PurchaseOrderLineId"];

    /// <summary>
    /// Every mapped entity tied to an order — by a foreign key at either order table, or by a
    /// scalar order-link property — excluding the order itself. Derived from the model, so a table
    /// added tomorrow is in this corpus without anyone editing this file.
    /// </summary>
    private static IReadOnlyList<IEntityType> OrderTiedEntities() =>
        BuildModel().GetEntityTypes()
            .Where(e => !e.IsOwned() && e.BaseType is null)
            .Where(e => e.ClrType != typeof(ProcuLink.Core.Entities.PurchaseOrderEntity))
            .Where(IsOrderTied)
            .OrderBy(e => e.ClrType.Name, StringComparer.Ordinal)
            .ToList();

    private static bool IsOrderTied(IEntityType entity) =>
        OrderLinkProperties.Any(name => entity.FindProperty(name) is not null)
        || entity.GetForeignKeys().Any(fk => OrderTables.Contains(fk.PrincipalEntityType.ClrType));

    /// <summary>
    /// True when the model cascades this entity away with its parent order, through any number of
    /// hops. Transitive on purpose: <c>order_confirmation_lines</c> reaches the order only through
    /// <c>order_confirmations</c>, and a single-hop read would report a row the database really
    /// does erase as an unaccounted-for leftover.
    /// </summary>
    private static bool IsCascadedFromTheOrder(IEntityType entity) =>
        IsCascadedFromTheOrder(entity, []);

    private static bool IsCascadedFromTheOrder(IEntityType entity, HashSet<IEntityType> visited)
    {
        // The model has no cascade cycle today; the visited set keeps the walk terminating if one
        // is ever introduced, rather than turning a schema change into a stack overflow.
        if (!visited.Add(entity))
            return false;

        foreach (var fk in entity.GetForeignKeys())
        {
            if (fk.DeleteBehavior is not DeleteBehavior.Cascade)
                continue;

            if (fk.PrincipalEntityType.ClrType == typeof(ProcuLink.Core.Entities.PurchaseOrderEntity))
                return true;

            if (IsCascadedFromTheOrder(fk.PrincipalEntityType, visited))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The CLR types <c>DataErasureService</c> removes, read out of its source rather than listed
    /// here. Each `_db.&lt;Set&gt;.RemoveRange(` is mapped back through the context's DbSet
    /// properties to the entity type it removes, so renaming a DbSet cannot leave this guard
    /// asserting against a name that no longer exists.
    /// </summary>
    private static IReadOnlySet<Type> TypesTheServiceRemoves()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoSourceCorpus.FindRepoRoot(),
            "ProcuLink.Infrastructure", "Services", "DataErasureService.cs"));

        var removedSets = Regex.Matches(source, @"_db\.(\w+)\s*\.RemoveRange\s*\(")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        removedSets.Should().NotBeEmpty(
            "the RemoveRange scan found nothing in DataErasureService.cs — the regex or the path is " +
            "broken, and every coverage assertion below would pass vacuously");

        var setToType = typeof(ProcuLinkDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                     && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToDictionary(p => p.Name, p => p.PropertyType.GetGenericArguments()[0], StringComparer.Ordinal);

        // The service also removes the order row itself; that set is not an order-tied child.
        return removedSets
            .Where(setToType.ContainsKey)
            .Select(name => setToType[name])
            .ToHashSet();
    }

    // ── direction 1: a new order-tied table nobody erased ─────────────────────

    [Fact]
    public void EveryEntityCarryingAnOrderId_IsErasedCascadedOrDeclaredResidual()
    {
        var entities = OrderTiedEntities();

        entities.Count.Should().BeGreaterThanOrEqualTo(MinimumOrderTiedEntities,
            $"the model sweep found only {entities.Count} order-tied entity type(s), which is fewer " +
            "than this schema is known to carry — the sweep is broken, not the schema");

        var removed = TypesTheServiceRemoves();

        var unaccounted = entities
            .Where(e => !removed.Contains(e.ClrType))
            .Where(e => !IsCascadedFromTheOrder(e))
            .Where(e => !DeclaredResiduals.ContainsKey(e.ClrType.Name))
            .Select(e => e.ClrType.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        unaccounted.Should().BeEmpty(
            "these entities carry an OrderId and would survive a GDPR erasure of that order: nothing " +
            "deletes them, no ON DELETE CASCADE reaches them from purchase_orders, and no residual " +
            "reason is declared for them. That is precisely how order_supplier_suggestions kept a " +
            "dangling OrderId, the sender email domain inside SignalsJson, and an operator's Clerk " +
            "user id after the order was erased. Either delete the rows in " +
            "DataErasureService.EraseOrderAsync, give the table an ON DELETE CASCADE to " +
            "purchase_orders, or declare it in DeclaredResiduals with the reason it must outlive the " +
            "order — and say so in the public erasure copy on /privacy and in " +
            "docs/ops/2026-08-18-gdpr-erasure-runbook.md");
    }

    // ── the arms are real: none of the three may quietly go empty ─────────────

    [Fact]
    public void TheThreeWaysAnOrderChildIsAccountedFor_AreAllStillPopulated()
    {
        // Without this, a broken FK read or a broken source scan would move every entity into one
        // surviving arm and the coverage test above would still pass. Each arm is asserted to hold
        // a specific, named member, so "all cascade" and "all erased" both fail loudly.
        var entities = OrderTiedEntities();
        var removed = TypesTheServiceRemoves();

        entities.Where(e => removed.Contains(e.ClrType)).Should().NotBeEmpty(
            "the source scan found no order-tied entity that DataErasureService removes");
        entities.Where(IsCascadedFromTheOrder).Should().NotBeEmpty(
            "the FK read found no order-tied entity cascaded from purchase_orders");

        removed.Should().Contain(typeof(ProcuLink.Core.Entities.OrderSupplierSuggestion),
            "the table this guard was written for must stay explicitly erased — it has no FK to " +
            "purchase_orders, so if the RemoveRange call goes, nothing else removes it");

        // The two highest-PII order children are erased by DDL alone. Pin that, because the service
        // never mentions them and a migration written without the cascade would strand contact
        // names, emails, phone numbers and the full extracted document text.
        IsCascadedFromTheOrder(entities.Single(e => e.ClrType == typeof(ProcuLink.Core.Entities.OrderParty)))
            .Should().BeTrue("order_parties holds ContactName/Email/Phone and is erased only by cascade");
        IsCascadedFromTheOrder(entities.Single(e => e.ClrType == typeof(ProcuLink.Core.Entities.SourceCapture)))
            .Should().BeTrue("source_captures holds RawText, the full extracted document, and is erased only by cascade");
    }

    // ── the widening itself, pinned by name so it cannot be undone quietly ────

    /// <summary>
    /// The corpus must keep seeing the order children that do not spell their link
    /// <c>OrderId</c>. Reverting <see cref="OrderTiedEntities"/> to a name-only match would drop
    /// both of these and leave every other assertion in this file green while a whole family of
    /// supplier-acknowledgement rows went unchecked. Naming them is the point: a count alone would
    /// be satisfied by any two entities.
    /// </summary>
    [Fact]
    public void TheCorpusSeesOrderChildrenThatDoNotSpellItOrderId()
    {
        var entities = OrderTiedEntities();
        var names = entities.Select(e => e.ClrType.Name).ToHashSet(StringComparer.Ordinal);

        names.Should().Contain(nameof(ProcuLink.Core.Entities.OrderConfirmationEntity),
            "it ties to the order through PurchaseOrderId, carries the supplier's reference text and " +
            "source file key, and was invisible to the name-only corpus");
        names.Should().Contain(nameof(ProcuLink.Core.Entities.OrderConfirmationLineEntity),
            "it ties to the order through OrderConfirmationId and PurchaseOrderLineId, carries " +
            "confirmed prices and quantities, and was invisible to the name-only corpus");

        // The transitive cascade walk is the other half of the widening: confirmation lines reach
        // the order through confirmations, so a single-hop FK read would report a row the database
        // really does erase as an unaccounted-for leftover.
        IsCascadedFromTheOrder(entities.Single(e =>
                e.ClrType == typeof(ProcuLink.Core.Entities.OrderConfirmationLineEntity)))
            .Should().BeTrue(
                "order_confirmation_lines cascades from order_confirmations, which cascades from " +
                "purchase_orders — the walk has to follow both hops or it under-reports coverage");
    }

    // ── direction 2: a declared residual that stopped being one ───────────────

    [Fact]
    public void EveryDeclaredResidual_IsStillAnOrderTiedEntity()
    {
        var names = OrderTiedEntities().Select(e => e.ClrType.Name).ToHashSet(StringComparer.Ordinal);

        var stale = DeclaredResiduals.Keys
            .Where(name => !names.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        stale.Should().BeEmpty(
            "a declared residual names an entity that no longer carries an OrderId — delete the entry " +
            "rather than leaving an excuse standing for a table that changed shape");
    }

    [Fact]
    public void EveryDeclaredResidual_CarriesARealReason()
    {
        // Unconditional floor first: every assertion below lives inside the loop, so an empty
        // map would make this test pass by iterating nothing.
        DeclaredResiduals.Should().NotBeEmpty(
            "the residual set is what this file hand-declares; an empty one means the reason check " +
            "below asserts nothing at all");

        foreach (var (name, reason) in DeclaredResiduals)
        {
            reason.Trim().Length.Should().BeGreaterThan(60,
                $"{name}: a residual must explain why the row outlives the erased order, not label it");
            reason.Should().MatchRegex("[a-z]",
                $"{name}: a residual reason must be a sentence");
        }
    }
}
