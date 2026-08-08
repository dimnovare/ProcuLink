using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Answers, from compiled IL rather than from a list somebody typed: <b>which controller actions
/// perform a destructive operation, and which ones carry the org-admin gate?</b>
///
/// <para><b>Why the reverse index is the whole point.</b> A guard that walked a hand-written list of
/// gated endpoint names would pass forever on the day someone adds endpoint thirteen. This repo has
/// paid for that lesson more than once — <see cref="BillingGateIlScanner"/> exists because a
/// <c>Dictionary&lt;BillingFeature, string&gt;</c> asserted only with <c>ContainKey</c> stayed green
/// after the gate it named was deleted. So the two sets here are both COMPUTED:
/// <see cref="GatedActions"/> from the attribute actually present on the compiled method, and
/// <see cref="DestructiveActions"/> from the IL of every action in the API. The test asserts they
/// are equal, which fails in both directions: an ungated destructive endpoint, and a gate on
/// something whose destructive primitive nobody declared.</para>
///
/// <para><b>What "destructive" means here is a declaration, and that is deliberate.</b>
/// <see cref="DestructivePrimitives"/> is the one hand-authored thing in this file, because no
/// scanner can infer the founder's judgement about which operations are irreversible, financial, or
/// able to silently redirect where an organisation's documents go. Every entry is resolved by
/// reflection, so a rename fails loudly at type-init instead of quietly emptying the set — the
/// failure mode that turns a guard vacuous.</para>
///
/// <para><b>Depth one, on purpose.</b> The scan looks at the action's own body only (following the
/// async state machine, without which every action in this codebase reads as calling nothing). Every
/// gated action calls its primitive directly, so depth is not needed, and transitive following would
/// buy false positives: a shared read helper that happens to sit above a primitive would drag
/// unrelated read endpoints into the destructive set. The stated limit is that a primitive moved
/// behind a new private helper would escape this scan — which is why the primitive list names
/// service-interface methods (the seam a new endpoint must cross) rather than implementation
/// internals.</para>
/// </summary>
internal static class OrgAdminGateIlScanner
{
    /// <summary>The compiled API. Resolved through a type rather than by name so it cannot go stale.</summary>
    public static Assembly ApiAssembly => typeof(ProcuLink.Api.Controllers.OrdersController).Assembly;

    /// <summary>
    /// One controller action, identified the way a human would go and find it.
    /// </summary>
    public sealed record ActionSite(MethodInfo Method)
    {
        public string Display => $"{Method.DeclaringType?.Name}.{Method.Name}";
    }

    // ── The declaration: what counts as destructive ───────────────────────────

    /// <summary>
    /// A destructive primitive, named so a failure can explain WHY the endpoint that reaches it
    /// needs an administrator.
    /// </summary>
    /// <param name="Describe">A stable label for failure messages.</param>
    /// <param name="Matches">True when a resolved IL call target is this primitive.</param>
    /// <param name="Why">The character that earns it a gate: irreversible, financial, or redirection.</param>
    public sealed record Primitive(string Describe, Func<MemberInfo, bool> Matches, string Why);

    /// <summary>
    /// Every operation this packet treats as requiring an organisation administrator.
    ///
    /// <para>Adding an entry widens what the guard demands a gate for; removing one narrows it. Both
    /// are decisions someone has to make on purpose, which is the point of writing them down here
    /// instead of leaving "destructive" to be re-litigated per endpoint.</para>
    /// </summary>
    public static readonly IReadOnlyList<Primitive> DestructivePrimitives =
    [
        new("IDeliveryConfigService.UpsertAsync",
            OnInterface<IDeliveryConfigService>(nameof(IDeliveryConfigService.UpsertAsync)),
            "Writes the endpoint, protocol and credentials every future order for a supplier is "
          + "sent to — the definition of silently redirecting where documents go."),

        new("IDeliveryConfigService.DeleteAsync",
            OnInterface<IDeliveryConfigService>(nameof(IDeliveryConfigService.DeleteAsync)),
            "Destroys that route and its credentials; every subsequent send for the supplier fails."),

        new("ISupplierConnectionService.PublishAsync",
            OnInterface<ISupplierConnectionService>(nameof(ISupplierConnectionService.PublishAsync)),
            "Moves the connection's ACTIVE pointer — from here the revision is what pinned orders "
          + "deliver through."),

        new("ISupplierConnectionService.RollbackAsync",
            OnInterface<ISupplierConnectionService>(nameof(ISupplierConnectionService.RollbackAsync)),
            "Publishes a previously-archived bundle nobody re-approved and moves the active pointer "
          + "to it: a redirection expressed as an undo."),

        new("ISupplierConnectionService.RepublishLiveDeliveryAsync",
            OnInterface<ISupplierConnectionService>(nameof(ISupplierConnectionService.RepublishLiveDeliveryAsync)),
            "Snapshots a delivery edit into a NEW published revision, which is how a live-row edit "
          + "reaches revision-governed orders."),

        new("IApiKeyService.CreateAsync",
            OnInterface<IApiKeyService>(nameof(IApiKeyService.CreateAsync)),
            "Mints a bearer credential with org-wide machine access, shown once, and attributed to "
          + "nobody."),

        new("IBillingService.CreateCheckoutSessionAsync",
            OnInterface<IBillingService>(nameof(IBillingService.CreateCheckoutSessionAsync)),
            "Commits the organisation to a recurring charge."),

        new("IBillingService.CreatePortalSessionAsync",
            OnInterface<IBillingService>(nameof(IBillingService.CreatePortalSessionAsync)),
            "The Stripe Billing Portal is where the subscription is CANCELLED, and cancelling stops "
          + "every ingest path at once with nothing queued for later."),

        new("IAcceptanceGate.RecordOverrideAsync",
            OnInterface<IAcceptanceGate>(nameof(IAcceptanceGate.RecordOverrideAsync)),
            "Overrules the supplier's own stated terms; what follows cannot be recalled."),

        new("IEmailSettingsService.UpdateAsync",
            OnInterface<IEmailSettingsService>(nameof(IEmailSettingsService.UpdateAsync)),
            "Stores IMAP host and credentials and names the supplier arriving documents are "
          + "attributed to — the inbound mirror of redirecting deliveries."),

        new("IPullIngressSettingsService.UpdateSftpAsync",
            OnInterface<IPullIngressSettingsService>(nameof(IPullIngressSettingsService.UpdateSftpAsync)),
            "Repoints the SFTP poller at an external host, with credentials."),

        new("IPullIngressSettingsService.UpdateS3Async",
            OnInterface<IPullIngressSettingsService>(nameof(IPullIngressSettingsService.UpdateS3Async)),
            "Repoints the S3/R2 poller at an external bucket, with access keys."),

        new("new IntegrationSubscription / DbSet<IntegrationSubscription>.Add",
            CreatesIntegrationSubscription,
            "Stands up an outbound subscription that ships the payload of every matching order to "
          + "an arbitrary URL. There is no service seam here, so the primitive is the entity being "
          + "minted and persisted."),
    ];

    /// <summary>
    /// Matches a call to <paramref name="name"/> declared on <typeparamref name="TService"/>.
    ///
    /// <para>The reflection lookup runs NOW, at construction, so a renamed interface method throws
    /// while the list is being built rather than silently matching nothing. A primitive that matches
    /// nothing is the failure this whole file exists to prevent.</para>
    /// </summary>
    private static Func<MemberInfo, bool> OnInterface<TService>(string name)
    {
        if (typeof(TService).GetMethod(name) is null)
            throw new InvalidOperationException(
                $"{typeof(TService).Name}.{name} does not exist — a destructive primitive was renamed. "
              + "Update OrgAdminGateIlScanner in the same change, or the endpoint that calls it "
              + "silently stops counting as destructive and its gate can be deleted unnoticed.");

        return member =>
            member is MethodBase method
            && method.Name == name
            && method.DeclaringType == typeof(TService);
    }

    /// <summary>
    /// Matches either half of "mint a webhook subscription": constructing the entity, or adding it
    /// to its <see cref="DbSet{TEntity}"/>. Both are accepted because this is the one primitive with
    /// no service interface to name, and either IL shape is conclusive on its own.
    /// </summary>
    private static bool CreatesIntegrationSubscription(MemberInfo member)
    {
        if (member is ConstructorInfo ctor)
            return ctor.DeclaringType == typeof(IntegrationSubscription);

        if (member is not MethodBase { Name: nameof(DbSet<IntegrationSubscription>.Add) } add)
            return false;

        var declaring = add.DeclaringType;
        return declaring is { IsGenericType: true }
            && declaring.GetGenericTypeDefinition() == typeof(DbSet<>)
            && declaring.GetGenericArguments()[0] == typeof(IntegrationSubscription);
    }

    // ── The two computed sets ─────────────────────────────────────────────────

    /// <summary>
    /// Every controller action in the API. An action is a public method carrying an
    /// <see cref="HttpMethodAttribute"/> — the same thing routing uses, so this cannot drift from
    /// what is actually reachable over HTTP.
    /// </summary>
    public static IReadOnlyList<ActionSite> AllActions() =>
        IlReader.SafeTypes(ApiAssembly)
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t is { IsAbstract: false })
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .Select(m => new ActionSite(m))
            .OrderBy(a => a.Display, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Actions that carry <see cref="RequireOrgAdminAttribute"/>, read off the compiled method
    /// (including a class-level application, which is how a whole controller could be gated).
    /// </summary>
    public static IReadOnlyList<ActionSite> GatedActions() =>
        AllActions()
            .Where(a => a.Method.GetCustomAttribute<RequireOrgAdminAttribute>(inherit: true) is not null
                     || a.Method.DeclaringType?.GetCustomAttribute<RequireOrgAdminAttribute>(inherit: true) is not null)
            .ToList();

    /// <summary>
    /// Actions whose own IL calls at least one <see cref="DestructivePrimitives"/> entry, each paired
    /// with what it reaches so a failure can say why.
    /// </summary>
    public static IReadOnlyList<(ActionSite Action, IReadOnlyList<Primitive> Reaches)> DestructiveActions()
    {
        var results = new List<(ActionSite, IReadOnlyList<Primitive>)>();

        foreach (var action in AllActions())
        {
            var reached = new List<Primitive>();

            foreach (var target in DirectCallTargets(action.Method))
            foreach (var primitive in DestructivePrimitives)
            {
                if (!reached.Contains(primitive) && primitive.Matches(target))
                    reached.Add(primitive);
            }

            if (reached.Count > 0) results.Add((action, reached));
        }

        return results;
    }

    // ── IL walking ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every member this method's compiled body names through a call-shaped opcode.
    ///
    /// <para>Reads through <see cref="IlReader.BodyOf"/>, which follows
    /// <c>AsyncStateMachineAttribute</c> to the generated <c>MoveNext</c>. Skipping that step would
    /// make every async action — which is all of them — read as calling nothing, and the guard would
    /// pass while enforcing nothing.</para>
    ///
    /// <para>An unresolvable token is skipped rather than guessed at. That can only SHRINK the
    /// destructive set, so it can never turn a real ungated endpoint green by accident; it would
    /// instead show up as the equality assertion failing, which is a visible outcome.</para>
    /// </summary>
    private static IEnumerable<MemberInfo> DirectCallTargets(MethodBase method)
    {
        var located = IlReader.BodyOf(method);
        if (located is null) yield break;
        var (body, owner) = located.Value;

        var il = IlReader.IlOf(body);
        if (il.Length == 0) yield break;

        var module = owner.Module;
        var typeArgs = owner.DeclaringType?.GetGenericArguments();
        var methodArgs = owner is MethodInfo mi && mi.IsGenericMethodDefinition ? mi.GetGenericArguments() : null;

        foreach (var instruction in IlReader.Decode(il))
        {
            if (instruction.Op.OperandType is not (OperandType.InlineMethod or OperandType.InlineTok)) continue;
            if (instruction.OperandStart + 4 > il.Length) continue;

            MemberInfo? member = null;
            try
            {
                member = module.ResolveMember(
                    BitConverter.ToInt32(il, instruction.OperandStart), typeArgs, methodArgs);
            }
            catch
            {
                // Generic context we cannot reconstruct, or an operand that is not a member token.
                // Skipping is safe in this scanner's direction — see the summary above.
            }

            if (member is not null) yield return member;
        }
    }
}
