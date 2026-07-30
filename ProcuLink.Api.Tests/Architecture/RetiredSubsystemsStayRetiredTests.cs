using System.Text.RegularExpressions;
using FluentAssertions;
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
///   <item><c>OutputTemplateEmitter</c> / <c>OrderMappingOverride.OutputTemplate</c> — the live
///     OutputNode AST emitter and the per-order Scriban escape hatch. Unrelated to the retired
///     <c>OutputTemplate</c> ENTITY.</item>
///   <item><c>RuleDefinition</c> + <c>SupplierAcceptanceRule</c> — the rule engine that actually
///     runs. The retired one is <c>ValidationRule</c>.</item>
///   <item><c>InboundEmailController</c> (Postmark, LIVE) and <c>BillingFeature.WebhookDelivery</c> /
///     <c>IntegrationSubscription</c> (OUTBOUND webhooks the org fires at THEIR systems, LIVE).
///     The retired one is INBOUND webhook ingress.</item>
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
            "WP-09 inbound webhook ingress (NOT inbound email, NOT outbound webhook subscriptions)",
            new[]
            {
                @"\bWebhookIngressController\b",
                @"\bWebhookSecretEncrypted\b",
                @"\bIHmacWebhookVerifier\b",
                @"\bHmacWebhookVerifier\b",
                @"\bWebhookVerificationResult\b",
                @"webhook-ingress",
            });
    }

    /// <summary>
    /// The counter-test: the three LIVE near-namesakes must still be present. Without this, a
    /// future over-broad "cleanup" could satisfy every assertion above by deleting inbound email
    /// or the org's outbound webhook subscriptions and this file would applaud.
    /// </summary>
    [Fact]
    public void TheLiveNearNamesakes_AreStillPresent()
    {
        var files = SourceFiles().ToList();
        files.Should().NotBeEmpty("the source scan must actually find the solution's C# files");

        foreach (var mustExist in new[]
                 {
                     @"\bInboundEmailController\b",          // Postmark inbound email — LIVE
                     @"BillingFeature\.WebhookDelivery",     // outbound webhook subscriptions — LIVE
                     @"\bIntegrationSubscription\b",         // ditto
                     @"\bOutputTemplateEmitter\b",           // OutputNode AST emitter — LIVE
                     @"\bSupplierAcceptanceRule\b",          // the rule engine that runs — LIVE
                     @"\bRuleDefinition\b",                  // bound to SupplierAcceptanceRule — LIVE
                 })
        {
            var rx = new Regex(mustExist, RegexOptions.Compiled);
            files.Any(f => rx.IsMatch(File.ReadAllText(f)))
                 .Should().BeTrue($"'{mustExist}' is a LIVE subsystem and must not be collateral damage");
        }
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
    /// Every checked-in C# file, minus build output and minus the historical migration bodies.
    ///
    /// <para>Old migrations and their <c>.Designer.cs</c> snapshots are DELIBERATELY excluded: they
    /// are an append-only record of what the model used to be, and they must keep mentioning the
    /// dropped entities. <c>ProcuLinkDbContextModelSnapshot.cs</c> is deliberately NOT excluded —
    /// it describes the CURRENT model, so a leftover mention there means the drop migration did not
    /// actually drop the entity.</para>
    /// </summary>
    private static IEnumerable<string> SourceFiles()
    {
        var root = RepoRoot();
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/")) return false;
                if (rel.StartsWith("bin/") || rel.StartsWith("obj/")) return false;
                if (rel.StartsWith(".claude/")) return false;
                if (rel.Contains("/Migrations/"))
                    return rel.EndsWith("/ProcuLinkDbContextModelSnapshot.cs");
                // This file names the retired identifiers on purpose.
                return !rel.EndsWith("/" + nameof(RetiredSubsystemsStayRetiredTests) + ".cs");
            });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests must run from inside the ProcuLink checkout (ProcuLink.slnx not found above the test binaries)");
        return dir!.FullName;
    }
}
