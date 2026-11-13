using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WAVE 1 "STOP LYING" — the orphan proof for the three retired subsystems, and the guard that
/// keeps them retired.
///
/// <para><b>Why a SOURCE scan and not reflection.</b> The proof obligation for a deletion is
/// "nothing downstream consumes this". Reflection can enumerate types, fields and signatures but
/// NOT method bodies, so it cannot see the one thing that matters here — a service quietly reading
/// <c>ConfigJson</c> or a job quietly reading <c>WebhookSecretEncrypted</c>. Scanning the checked-in
/// C# is the only check that actually answers the question, and it stays honest after the delete:
/// resurrect any of these identifiers anywhere in the solution and this test goes red.</para>
///
/// <para><b>Ran RED before the deletion.</b> Each subsystem's failure listed exactly its own CRUD
/// triangle (entity + service + controller + its own test) and nothing from parse, mapping,
/// transform, delivery or revision — which IS the orphan proof. The lists are quoted in the PR
/// body.</para>
///
/// <para><b>Disambiguation is the whole game here</b> — three near-namesakes survive on purpose and
/// MUST NOT be matched by these patterns:</para>
/// <list type="bullet">
///   <item><c>OutputTemplateEmitter</c> — the live
///     OutputNode AST emitter. Unrelated to the retired
///     <c>OutputTemplate</c> ENTITY.</item>
///   <item><c>RuleDefinition</c> + <c>SupplierAcceptanceRule</c> — the rule engine that actually
///     runs. The retired one is <c>ValidationRule</c>.</item>
///   <item><c>InboundEmailController</c> (Postmark, LIVE). The retired one is INBOUND webhook
///     ingress. (<c>BillingFeature.WebhookDelivery</c> was listed here too until plan 13b deleted it
///     with the legacy supplier delivery config it gated.) <c>IntegrationSubscription</c>
///     (order-event webhooks the org pointed at THEIR systems) is
///     RETIRED too since plan 13b (2026-11-13): its rows are kept but inert, with no sender and no
///     revocation surface. <see cref="IntegrationSubscriptionSender_StaysRetired"/> keeps it so.</item>
/// </list>
/// </summary>
public class RetiredSubsystemsStayRetiredTests
{
    /// <summary>WP-06 — the <c>OutputTemplate</c> entity and its CRUD-only triangle.</summary>
    [Fact]
    public void OutputTemplateEntity_HasNoConsumerAnywhere()
    {
        AssertNoSourceReference(
            "WP-06 OutputTemplate (entity + its CRUD service/controller)",
            new[]
            {
                @"\bOutputTemplates\b",              // the DbSet
                @"\bOutputTemplatesController\b",
                @"\bIOutputTemplateService\b",
                @"\bOutputTemplateService\b",
                @"<\s*OutputTemplate\s*>",           // DbSet<>/Entity<>/Ignore<>
                @"\bnew\s+OutputTemplate\s*\{",
                @"Entities\.OutputTemplate\b",
                @"\bTemplateView\b",
                @"\bCreateTemplateRequest\b",
                @"\bCreateTemplateBody\b",
                @"\bTemplateDto\b",
            });
    }

    /// <summary>WP-07 — the <c>ValidationRule</c> entity. NOT <c>SupplierAcceptanceRule</c>.</summary>
    [Fact]
    public void ValidationRuleEntity_HasNoConsumerAnywhere()
    {
        AssertNoSourceReference(
            "WP-07 ValidationRule (second, never-evaluated rule engine)",
            new[]
            {
                @"\bValidationRules\b",              // the DbSet AND the BillingFeature member
                @"\bValidationRulesController\b",
                @"\bIValidationRuleService\b",
                @"\bValidationRuleService\b",
                @"<\s*ValidationRule\s*>",
                @"\bnew\s+ValidationRule\s*\{",
                @"Entities\.ValidationRule\b",
                @"\bCreateRuleRequest\b",
                @"\bRuleDto\b",
            });
    }

    /// <summary>
    /// WP-09 — INBOUND webhook ingress. Its authenticator, <c>Organisation.WebhookSecretEncrypted</c>,
    /// never had a writer, so every supplier callback 401'd from the day it shipped.
    /// </summary>
    [Fact]
    public void InboundWebhookIngress_HasNoConsumerAnywhere()
    {
        AssertNoSourceReference(
            "WP-09 inbound webhook ingress (NOT inbound email, NOT the kept integration_subscriptions rows)",
            new[]
            {
                @"\bWebhookIngressController\b",
                @"\bWebhookSecretEncrypted\b",
                @"\bIHmacWebhookVerifier\b",
                @"\bHmacWebhookVerifier\b",
                @"\bWebhookVerificationResult\b",
                // The deleted status gate. It was missing from this list, and two comments went on
                // citing it after the symbol was gone (OrderStatusTransitionObserver and
                // OrderStatusMachineTests) — a comment naming a symbol that no longer exists reads
                // as live justification and is exactly what this scan is for. It matches prose as
                // well as code ON PURPOSE: a stale citation is the defect here, not a side effect.
                @"\bWebhookReportableFrom\b",
                @"webhook-ingress",
            });
    }

    /// <summary>
    /// The counter-test: the LIVE near-namesakes must still be present. Without it, a future
    /// over-broad "cleanup" could satisfy every assertion above by deleting inbound email or the
    /// kept integration_subscriptions mapping and this file would applaud.
    ///
    /// <para><b>This test used to be decorative and is deliberately no longer a source scan.</b> It
    /// asserted only that an identifier STRING appeared in some <c>.cs</c> file. Every subject is
    /// referenced from compiled production code, so <c>dotnet build</c> failed first in every real
    /// case — and worse, A COMMENT SATISFIED IT: deleting <c>InboundEmailController.cs</c> plus its
    /// three test classes still passed, on the strength of one prose mention in
    /// <c>ProcuLink.Worker/Program.cs</c>. So each subject is now asserted through something a
    /// comment cannot produce — a loaded type, a route attribute, an EF model entity, a populated
    /// catalog, a plan-gate answer.</para>
    ///
    /// <para><b>Why types are resolved BY NAME rather than with <c>typeof</c>.</b> <c>typeof</c>
    /// would put the compiler back in front of the assertion — delete the subject and the test
    /// project stops building, which is the very redundancy this rewrite removes. Resolving by name
    /// makes the deletion fail HERE, with a message that says which live subsystem was destroyed and
    /// why it matters. A rename fails the same way, which is correct: renaming a live public
    /// controller route or entity is exactly the kind of change this guard should make someone
    /// confirm. Proven by scratch mutation — deleting <c>InboundEmailController.cs</c> and its three
    /// test classes fails this test on its own assertion; the verbatim output is in PR #75.</para>
    /// </summary>
    [Fact]
    public void TheLiveNearNamesakes_AreStillPresent()
    {
        // ── Postmark inbound email — LIVE. A public, unauthenticated webhook: the ROUTE is the
        //    contract with Postmark's configured URL, so assert the attribute, not just the type.
        var inboundEmail = LiveType("ProcuLink.Api", "ProcuLink.Api.Controllers.InboundEmailController",
            "Postmark inbound email is LIVE on prod ({slug}@orders.proculink.eu → CF MX → Postmark → this " +
            "controller). It is the near-namesake of the retired INBOUND WEBHOOK ingress and must not be " +
            "collateral damage");
        typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(inboundEmail)
            .Should().BeTrue("InboundEmailController must still be a real MVC controller");
        RouteTemplateOf(inboundEmail).Should().Be("api/inbound-email",
            "Postmark posts to this exact path; changing or losing the route silently drops every inbound order email");
        HttpActionCount(inboundEmail).Should().BeGreaterThan(0,
            "a controller with no HTTP action answers 404 for every inbound delivery");

        // ── The entities behind the LIVE subsystems, asserted against the EF MODEL rather than the
        //    source text: an entity class that survives but is no longer mapped is a silent
        //    retirement, which is precisely the failure mode this file exists to catch.
        var mapped = BuildModel().GetEntityTypes()
            .Select(e => e.ClrType.FullName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (fullName, why) in new[]
                 {
                     ("ProcuLink.Core.Entities.IntegrationSubscription",
                      "its sender is retired (plan 13b, 2026-11-13) but the rows are kept, inert, and " +
                      "CredentialBindingBackfillService still re-encrypts their secrets — no drops"),
                     // 2026-11-13 (plan 13b, Task 7b): the acceptance evaluator, the rule-definition
                     // service and their RuleCatalog seed source were deleted as orphans. The two
                     // entities stay mapped because their tables stay (R2: no drops); their
                     // RuleCatalog.Entries assertion went with RuleCatalog.
                     ("ProcuLink.Core.Entities.SupplierAcceptanceRule",
                      "its evaluator is deleted (plan 13b) but the rows are kept, inert — no drops"),
                     ("ProcuLink.Core.Entities.RuleDefinition",
                      "its service and seed are deleted (plan 13b) but the rows are kept, inert — no drops"),
                 })
        {
            mapped.Should().Contain(fullName,
                $"{fullName} must still be mapped by ProcuLinkDbContext: {why}");
        }

        // ── The OutputNode AST emitter — LIVE, and the near-namesake of the retired OutputTemplate
        //    ENTITY. Assert its rendering entry point, not its name.
        LiveType("ProcuLink.Transform", "ProcuLink.Transform.Output.OutputTemplateEmitter",
            "the OutputNode AST emitter is the live structured-output path (DocumentOutputRenderer, " +
            "every flow output); it is unrelated to the retired OutputTemplate ENTITY")
            .GetMethod("EmitRows").Should().NotBeNull("OutputTemplateEmitter.EmitRows is that path's entry point");
    }

    /// <summary>
    /// Plan 13b (2026-11-13) — the order-event webhook SENDER (<c>FireIntegrationTriggerJob</c>) is
    /// retired. <c>IntegrationSubscription</c> rows are kept (no drops) but inert: nothing sends to
    /// them, and no surface is left to list or revoke them. A sender that came back would POST to URLs
    /// nobody can switch off, so no production file may name the subscription URL except the three
    /// that declare and map the column. Prose counts on purpose, as in WP-09 above.
    /// </summary>
    [Fact]
    public void IntegrationSubscriptionSender_StaysRetired()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "ProcuLink.Core/Entities/IntegrationSubscription.cs",                    // the declaration
            "ProcuLink.Infrastructure/ProcuLinkDbContext.cs",                        // the column mapping
            "ProcuLink.Infrastructure/Migrations/ProcuLinkDbContextModelSnapshot.cs", // the current model
        };
        var rx = new Regex(@"\bTargetUrl\b|\btarget_url\b", RegexOptions.Compiled);
        var root = RepoRoot();
        var seenAllowed = 0;
        var hits = new List<string>();

        foreach (var file in SourceFiles())
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains(".Tests/", StringComparison.Ordinal) ||
                rel.StartsWith("ProcuLink.TestSupport/", StringComparison.Ordinal))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!rx.IsMatch(lines[i])) continue;
                if (allowed.Contains(rel)) seenAllowed++;
                else hits.Add($"  {rel}:{i + 1}  {lines[i].Trim()}");
            }
        }

        seenAllowed.Should().BeGreaterThan(0,
            "the scan must at least see the column mapping, or it read nothing and its silence proves nothing");
        hits.Should().BeEmpty(
            "the integration-subscription sender is RETIRED (rows inert, no sender, no revocation surface) — " +
            $"no production file may read the subscription URL.\nFound {hits.Count} reference(s):\n{string.Join("\n", hits)}\n");
    }

    /// <summary>Resolve a type by name, failing with WHY it matters rather than with a null-ref.</summary>
    private static Type LiveType(string assemblyName, string fullName, string why)
    {
        var type = Assembly.Load(assemblyName).GetType(fullName, throwOnError: false);
        type.Should().NotBeNull($"{fullName} must still exist: {why}");
        return type!;
    }

    private static string? RouteTemplateOf(Type controller) =>
        controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: true)
                  .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
                  .Select(a => a.Template)
                  .FirstOrDefault();

    private static int HttpActionCount(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .Count(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), inherit: true).Length > 0);

    /// <summary>
    /// The CURRENT EF model, built through <c>OnModelCreating</c>. The InMemory provider is used
    /// only because building a model needs one — nothing is queried, so none of the InMemory
    /// fidelity caveats apply.
    /// </summary>
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"model-probe-{Guid.NewGuid()}")
            .Options;
        using var db = new ProcuLinkDbContext(options);
        return db.Model;
    }

    // ── scan ──────────────────────────────────────────────────────────────────

    private static void AssertNoSourceReference(string subsystem, string[] patterns)
    {
        var regexes = patterns.Select(p => (Pattern: p, Rx: new Regex(p, RegexOptions.Compiled))).ToArray();
        var root    = RepoRoot();
        var hits    = new List<string>();

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (pattern, rx) in regexes)
                {
                    if (!rx.IsMatch(lines[i])) continue;
                    hits.Add($"  {Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}  [{pattern}]  {lines[i].Trim()}");
                    break;
                }
            }
        }

        hits.Should().BeEmpty(
            $"{subsystem} is RETIRED — no source file may reference it any more.\n" +
            $"Found {hits.Count} reference(s):\n{string.Join("\n", hits)}\n");
    }

    /// <summary>
    /// Every C# file of THIS checkout, minus the historical migration bodies.
    ///
    /// <para>Old migrations and their <c>.Designer.cs</c> snapshots are DELIBERATELY excluded: they
    /// are an append-only record of what the model used to be, and they must keep mentioning the
    /// dropped entities. <c>ProcuLinkDbContextModelSnapshot.cs</c> is deliberately NOT excluded —
    /// it describes the CURRENT model, so a leftover mention there means the drop migration did not
    /// actually drop the entity.</para>
    ///
    /// <para>Which files are this checkout's at all is <see cref="RepoSourceCorpus"/>'s question.
    /// It matters here more than anywhere: this scan walked the repository root, and an untracked
    /// copy of an OLDER revision left beside the projects still contained every entity Wave 1
    /// deleted — so all three assertions below failed, reporting a retirement that had in fact
    /// happened months earlier as though it had been undone.</para>
    /// </summary>
    private static IEnumerable<string> SourceFiles() =>
        RepoSourceCorpus.CsFiles(RepoRoot())
            .Where(f =>
            {
                var rel = f.Relative;
                if (rel.Contains("/Migrations/", StringComparison.Ordinal))
                    return rel.EndsWith("/ProcuLinkDbContextModelSnapshot.cs", StringComparison.Ordinal);
                // This file names the retired identifiers on purpose.
                return !rel.EndsWith("/" + nameof(RetiredSubsystemsStayRetiredTests) + ".cs", StringComparison.Ordinal);
            })
            .Select(f => f.FullPath);

    private static string RepoRoot() => RepoSourceCorpus.FindRepoRoot();
}
