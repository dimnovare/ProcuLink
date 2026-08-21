using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ProcuLink.Infrastructure;

namespace ProcuLink.TestSupport;

/// <summary>
/// Answers one question about every <see cref="ProcuLinkDbContext"/> subclass in a test assembly:
/// <i>does its model still contain an entity type that EF discovered but nothing ever gave a key
/// to?</i>
///
/// <para><b>The defect this exists to stop repeating.</b> Thirty-nine test contexts in this
/// codebase subclass <see cref="ProcuLinkDbContext"/> and override <c>OnModelCreating</c>
/// <b>without calling <c>base.OnModelCreating</c></b>. They hand-declare the two or three entities
/// the test needs and <c>modelBuilder.Ignore&lt;T&gt;()</c> the rest. That is a legitimate,
/// deliberate pattern — it keeps a focused test from paying for the whole sixty-entity model.</para>
///
/// <para>What it does NOT do is stop EF discovering entity types. <c>DbSet&lt;T&gt;</c> properties
/// are inherited, and EF's <see cref="ModelCustomizer"/> adds an entity type for every one of them
/// <b>before</b> it calls <c>OnModelCreating</c>. So a newly added <c>DbSet&lt;NewThing&gt;</c> on
/// <see cref="ProcuLinkDbContext"/> lands in all thirty-nine models, unconfigured. If
/// <c>NewThing</c> happens to have a property called <c>Id</c> (or <c>NewThingId</c>) EF's
/// key-discovery convention keys it and nobody notices. If its key is anything else — a composite,
/// or a string column with a domain name — model validation throws
/// <c>"The entity type 'NewThing' requires a primary key to be defined"</c>.</para>
///
/// <para><b>And it throws somewhere else.</b> The failure surfaces the first time any test touches
/// any of those thirty-nine contexts, which is why it reads as unrelated collateral:
/// <c>WorkerHealthAlertCooldown</c> (key <c>AlertKey</c>, a string) took nineteen of twenty
/// <c>SftpIngressServiceTests</c> cases down with it, and the SFTP tests have nothing whatever to
/// do with worker health. <c>AiUsageMonthly</c> (composite key <c>OrgId, Year, Month</c>) had
/// already cost the same thirty-one-file fix once. Two occurrences, both diagnosed from a stack
/// trace that names the wrong subsystem.</para>
///
/// <para><b>Why this is behavioural and not a source or IL scan.</b> The obvious shape — grep each
/// override for a <c>base.OnModelCreating</c> call, then grep it for an <c>Ignore&lt;T&gt;</c> line
/// — answers a question about text. This asks EF. Each context is constructed, its model is built
/// through EF's own pipeline (inherited-<c>DbSet</c> discovery included, via the real
/// <see cref="ModelCustomizer"/>), and the resulting model is inspected. A context that calls
/// <c>base</c> passes because its model is genuinely complete, not because a regex saw the word
/// <c>base</c>; a context that ignores the type passes because the type is genuinely absent. The
/// only cost is that model validation must be allowed to fail without taking the guard down with
/// it, which is what <see cref="CapturingModelCustomizer"/> is for: it snapshots the model builder
/// at the end of customization, before validation ever runs.</para>
///
/// <para><b>The list of entity types at risk is derived, never typed.</b> It comes from the real
/// <see cref="ProcuLinkDbContext"/> model at run time. A hand-maintained list would have been the
/// third occurrence of this defect rather than the fix for it — the entity that breaks next has
/// not been written yet.</para>
/// </summary>
public static class InheritedDbSetModelScanner
{
    /// <summary>
    /// An entity type <see cref="ProcuLinkDbContext"/> maps whose key EF's key-discovery
    /// convention cannot find on its own, so every model that discovers it must be told about it.
    /// </summary>
    /// <param name="ClrType">The entity's CLR type.</param>
    /// <param name="KeyDescription">Its primary key in the real model, for the failure message.</param>
    public sealed record FragileEntity(Type ClrType, string KeyDescription);

    /// <summary>What the scan found for one <see cref="ProcuLinkDbContext"/> subclass.</summary>
    /// <param name="ContextType">The subclass.</param>
    /// <param name="UnhandledFragileEntities">
    /// Entity types from <see cref="ScanResult.FragileEntities"/> that this context's model
    /// contains with no primary key. Empty is the passing answer.
    /// </param>
    /// <param name="Inspected">
    /// False when the guard could not build this context's model at all. Reported as a failure,
    /// never as a pass — a context the guard cannot see is a context the guard does not cover.
    /// </param>
    /// <param name="Note">Why <paramref name="Inspected"/> is false, or null.</param>
    public sealed record ContextReport(
        Type ContextType,
        IReadOnlyList<Type> UnhandledFragileEntities,
        bool Inspected,
        string? Note)
    {
        /// <summary>True when this context is the guard's own deliberately broken control.</summary>
        public bool IsNegativeControl =>
            ContextType.GetCustomAttribute<UnconfiguredEntitySetNegativeControlAttribute>() is not null;

        /// <summary>True when this context is the guard's own deliberately correct control.</summary>
        public bool IsPositiveControl =>
            ContextType.GetCustomAttribute<UnconfiguredEntitySetPositiveControlAttribute>() is not null;

        /// <summary>Controls are the guard testing itself; they are not part of what it polices.</summary>
        public bool IsControl => IsNegativeControl || IsPositiveControl;
    }

    /// <summary>The whole answer for one test assembly.</summary>
    /// <param name="FragileEntities">Derived from the real model; see the class remarks.</param>
    /// <param name="ConventionKeyedEntities">
    /// The complement — entity types EF can key unaided. Exposed because the guard's own negative
    /// control ignores exactly these, which is how it reproduces the defect without a typed list.
    /// </param>
    /// <param name="Contexts">One entry per <see cref="ProcuLinkDbContext"/> subclass found.</param>
    public sealed record ScanResult(
        IReadOnlyList<FragileEntity> FragileEntities,
        IReadOnlyList<Type> ConventionKeyedEntities,
        IReadOnlyList<ContextReport> Contexts);

    private static readonly ConcurrentDictionary<Assembly, ScanResult> Scans = new();

    private static readonly Lazy<(IReadOnlyList<FragileEntity> Fragile, IReadOnlyList<Type> ConventionKeyed)>
        RealModel = new(ReadRealModel, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Entity types the real model maps that EF keys by convention. The negative control ignores
    /// these and nothing else, so what it leaves discovered-but-unkeyed is exactly the fragile set.
    /// </summary>
    public static IReadOnlyList<Type> ConventionKeyedEntityClrTypes => RealModel.Value.ConventionKeyed;

    /// <summary>
    /// The entity types every model that discovers them must be told about. Exposed separately
    /// from <see cref="Scan"/> so the guard's positive control can read it while a scan is in
    /// flight — asking <see cref="Scan"/> from inside a context's <c>OnModelCreating</c> would
    /// re-enter the scan that is building that very context.
    /// </summary>
    public static IReadOnlyList<Type> FragileEntityClrTypes =>
        RealModel.Value.Fragile.Select(f => f.ClrType).ToList();

    /// <summary>Scans <paramref name="assembly"/> once; later calls return the same result.</summary>
    public static ScanResult Scan(Assembly assembly) => Scans.GetOrAdd(assembly, ScanCore);

    /// <summary>
    /// EF's key-discovery convention looks for a property called <c>Id</c> or
    /// <c>&lt;TypeName&gt;Id</c>, case-insensitively. Nothing else. This mirrors that rule, and it
    /// is the whole reason some entities are safe to leave unconfigured and others are not.
    /// </summary>
    public static bool HasConventionDiscoverableKey(Type clrType) =>
        NamedProperty(clrType, "Id") is not null || NamedProperty(clrType, clrType.Name + "Id") is not null;

    private static PropertyInfo? NamedProperty(Type clrType, string name) =>
        clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .FirstOrDefault(p => p.GetIndexParameters().Length == 0
                                    && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static (IReadOnlyList<FragileEntity>, IReadOnlyList<Type>) ReadRealModel()
    {
        using var context = new ProcuLinkDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase("inherited-dbset-guard-reference")
                .Options);

        var fragile = new List<FragileEntity>();
        var conventionKeyed = new List<Type>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            // Owned and shared-CLR-type entities are not discovered from a DbSet<> property and
            // cannot be handed an Ignore<T>() line of their own, so they are not in scope.
            if (entityType.IsOwned() || entityType.HasSharedClrType) continue;

            var clrType = entityType.ClrType;
            if (HasConventionDiscoverableKey(clrType))
            {
                if (!conventionKeyed.Contains(clrType)) conventionKeyed.Add(clrType);
                continue;
            }

            var key = entityType.FindPrimaryKey();
            var described = key is null
                ? "(none in the real model either)"
                : string.Join(", ", key.Properties.Select(p => p.Name));
            if (fragile.All(f => f.ClrType != clrType)) fragile.Add(new FragileEntity(clrType, described));
        }

        return (fragile.OrderBy(f => f.ClrType.Name, StringComparer.Ordinal).ToList(),
                conventionKeyed.OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
    }

    private static ScanResult ScanCore(Assembly assembly)
    {
        var (fragile, conventionKeyed) = RealModel.Value;
        var reports = SubclassesIn(assembly).Select(t => Inspect(t, fragile)).ToList();
        return new ScanResult(fragile, conventionKeyed, reports);
    }

    private static IEnumerable<Type> SubclassesIn(Assembly assembly)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        return types.Where(t => t is not null)
                    .Select(t => t!)
                    .Where(t => t.IsSubclassOf(typeof(ProcuLinkDbContext)) && !t.IsAbstract)
                    .OrderBy(t => t.FullName, StringComparer.Ordinal)
                    .ToList();
    }

    private static ContextReport Inspect(Type contextType, IReadOnlyList<FragileEntity> fragile)
    {
        ProcuLinkDbContext context;
        try
        {
            context = Construct(contextType);
        }
        catch (Exception ex)
        {
            return new ContextReport(contextType, Array.Empty<Type>(), Inspected: false,
                $"could not be constructed: {ex.GetBaseException().Message}");
        }

        using (context)
        {
            CapturingModelCustomizer.Captured = null;

            // An invalid model is precisely what this guard reports on, so validation failing here
            // is expected and must not become the guard's own failure. The model builder has
            // already been snapshotted by the time validation runs.
            try { _ = context.Model; }
            catch { /* intentionally swallowed — see above */ }

            var builder = CapturingModelCustomizer.Captured;
            if (builder is null)
            {
                return new ContextReport(contextType, Array.Empty<Type>(), Inspected: false,
                    "EF did not run the capturing model customizer for it, so its model was never inspected.");
            }

            var model = builder.Model;
            var unhandled = fragile
                .Where(f => model.FindEntityType(f.ClrType) is { } mapped && mapped.FindPrimaryKey() is null)
                .Select(f => f.ClrType)
                .ToList();

            return new ContextReport(contextType, unhandled, Inspected: true, Note: null);
        }
    }

    private static ProcuLinkDbContext Construct(Type contextType)
    {
        var constructor = contextType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(c => c.GetParameters().Length >= 1
                        && c.GetParameters()[0].ParameterType
                            .IsAssignableFrom(typeof(DbContextOptions<ProcuLinkDbContext>)))
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "it has no constructor whose first parameter is a DbContextOptions<ProcuLinkDbContext>");

        var parameters = constructor.GetParameters();
        var arguments = new object?[parameters.Length];
        arguments[0] = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"inherited-dbset-guard-{contextType.GUID:N}")
            .ReplaceService<IModelCustomizer, CapturingModelCustomizer>()
            .Options;

        // Extra constructor parameters on these contexts are test knobs (an exception to throw, a
        // one-shot failure flag). The guard never runs a query, so a default is enough.
        for (var i = 1; i < parameters.Length; i++)
        {
            arguments[i] = parameters[i].HasDefaultValue
                ? parameters[i].DefaultValue
                : parameters[i].ParameterType.IsValueType
                    ? Activator.CreateInstance(parameters[i].ParameterType)
                    : null;
        }

        return (ProcuLinkDbContext)constructor.Invoke(arguments);
    }

    /// <summary>
    /// Renders <paramref name="offenders"/> as the one-line instruction the next person needs,
    /// rather than the stack trace they would otherwise get from an unrelated test.
    /// </summary>
    public static string DescribeOffenders(
        IReadOnlyList<FragileEntity> fragile,
        IReadOnlyList<ContextReport> offenders,
        Assembly assembly)
    {
        // Leading blank lines: the caller passes this as FluentAssertions' `because`, which prefixes
        // it with "Expected … to be empty because". Starting on a fresh line keeps the instruction
        // legible instead of trailing off the end of that sentence.
        var report = new StringBuilder();
        report.AppendLine().AppendLine()
              .Append(offenders.Count).Append(" DbContext(s) in ").Append(assembly.GetName().Name)
              .AppendLine(" inherit a DbSet<> for an entity")
              .AppendLine("type EF cannot key by convention, and neither ignore it nor give it a key.")
              .AppendLine()
              .AppendLine("These contexts override OnModelCreating WITHOUT calling base.OnModelCreating, but DbSet<>")
              .AppendLine("properties are still inherited and EF still discovers an entity type for each one. Left")
              .AppendLine("unconfigured, model validation throws \"The entity type 'X' requires a primary key to be")
              .AppendLine("defined\" the first time ANY test touches the context — usually a test with no connection")
              .AppendLine("to X at all. Fix it here, not where it surfaced.")
              .AppendLine();

        foreach (var entity in fragile)
        {
            var affected = offenders.Where(o => o.UnhandledFragileEntities.Contains(entity.ClrType)).ToList();
            if (affected.Count == 0) continue;

            report.Append("  ").Append(entity.ClrType.Name)
                  .Append(" — primary key: ").Append(entity.KeyDescription)
                  .Append(". No \"Id\" or \"").Append(entity.ClrType.Name)
                  .AppendLine("Id\" property,")
                  .AppendLine("  so EF's key-discovery convention finds nothing. Add this line to OnModelCreating:")
                  .AppendLine()
                  .Append("      modelBuilder.Ignore<").Append(entity.ClrType.Name).AppendLine(">();")
                  .AppendLine()
                  .AppendLine("    …in each of:");
            foreach (var context in affected) report.Append("      • ").AppendLine(context.ContextType.FullName);
            report.AppendLine();
        }

        return report.ToString();
    }
}

/// <summary>
/// EF's own <see cref="ModelCustomizer"/>, with one addition: it hands the finished
/// <see cref="ModelBuilder"/> back to <see cref="InheritedDbSetModelScanner"/>.
///
/// <para>This is the seam that lets the guard report instead of crash. <c>Customize</c> is where EF
/// discovers entity types from <c>DbSet&lt;&gt;</c> properties and then calls the context's
/// <c>OnModelCreating</c> — everything the guard asks about has happened by the time it returns,
/// and model validation has not run yet. Snapshotting here means a context with a keyless entity
/// type still yields an inspectable model, so the guard can name the entity and the context rather
/// than re-throwing EF's message from wherever it happened to land.</para>
///
/// <para>Capture is <c>[ThreadStatic]</c> because EF builds the model synchronously on the thread
/// that first touches <c>DbContext.Model</c>, and because xUnit runs test classes in parallel.</para>
/// </summary>
public sealed class CapturingModelCustomizer : ModelCustomizer
{
    /// <summary>The model builder from the most recent customization on this thread.</summary>
    [ThreadStatic]
    public static ModelBuilder? Captured;

    /// <summary>Constructed by EF's internal service provider.</summary>
    public CapturingModelCustomizer(ModelCustomizerDependencies dependencies) : base(dependencies) { }

    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        Captured = modelBuilder;
    }
}

/// <summary>
/// Marks a <see cref="ProcuLinkDbContext"/> subclass that is deliberately broken so the guard has
/// something it must fail on. Excluded from the set the guard polices.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UnconfiguredEntitySetNegativeControlAttribute : Attribute { }

/// <summary>
/// Marks a <see cref="ProcuLinkDbContext"/> subclass built the correct way, so the guard has
/// something it must pass on. Excluded from the set the guard polices.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UnconfiguredEntitySetPositiveControlAttribute : Attribute { }
