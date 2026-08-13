using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>One routable endpoint: a single HTTP method against one resolved route template.</summary>
/// <param name="Method">Upper-case verb.</param>
/// <param name="Path">Normalised path — leading slash, every route parameter folded to <c>{}</c>.</param>
/// <param name="Template">The template exactly as routing resolved it, for the failure message.</param>
/// <param name="Site">Declaring type + action, so a finding names code and not just a URL.</param>
public sealed record ApiEndpoint(string Method, string Path, string Template, string Site)
{
    public string Key => $"{Method} {Path}";

    public override string ToString() => $"{Method,-6} {Path}   [{Site}]";
}

/// <summary>One place a caller names an API path, and the verb it issues it under.</summary>
public sealed record CallerReference(string Method, string Path, string Source)
{
    public string Key => $"{Method} {Path}";
}

/// <summary>One file of a caller corpus, comment-stripped, with a corpus-relative path.</summary>
public sealed record CallerFile(string RelativePath, string Text);

/// <summary>
/// The detection functions behind <see cref="EndpointReachabilityGuardTests"/>. Pure with respect
/// to their inputs — an endpoint list and a caller corpus — so the synthetic fixtures in the guard
/// exercise exactly the code the real sweep runs.
/// </summary>
public static class EndpointReachabilityScanner
{
    /// <summary>Marks a segment the caller computes. Chosen so no source file can contain it.</summary>
    public const char Dynamic = '\u0001';

    // ── Half 1: the endpoint inventory, resolved the way production resolves it ──────────

    /// <summary>
    /// Every MVC endpoint in <paramref name="apiAssembly"/>, read out of ASP.NET Core's OWN
    /// action-descriptor table rather than off the source text.
    ///
    /// <para><b>Why this and not a regex over the controllers.</b> The thing being inventoried is
    /// "which URLs does this service answer", and MVC decides that — <c>ControllerFeatureProvider</c>
    /// decides what is a controller, <c>IsAction</c> what is an action,
    /// <c>AttributeRouteModel.CombineTemplates</c> how a controller prefix and an action template
    /// compose, and token replacement expands <c>[controller]</c>. A regex re-implements all four
    /// and is wrong the first time one of them appears in a shape it did not anticipate: an
    /// absolute <c>[HttpGet("/api/…")]</c> that DISCARDS its controller prefix (three of those
    /// exist here — <c>DashboardController.GetSummary</c>, <c>RuleDefinitionsController</c>'s
    /// supplier bindings, <c>HealthController</c>), a <c>[NonAction]</c> public method, an
    /// attribute inherited from a base controller. Building the real
    /// <see cref="IActionDescriptorCollectionProvider"/> over the real assembly cannot drift from
    /// routing, because it IS routing.</para>
    ///
    /// <para>A bare <see cref="ServiceCollection"/> is used rather than a
    /// <c>WebApplicationFactory&lt;Program&gt;</c> on purpose: booting the host would drag this
    /// guard into the serialised <c>postgres-container</c> collection (see
    /// <c>ProcessGlobalStateIsSerializedTests</c>) and make an architecture check depend on Docker.
    /// The MVC half of routing needs neither a host nor a database.</para>
    /// </summary>
    public static IReadOnlyList<ApiEndpoint> Endpoints(Assembly apiAssembly)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddApplicationPart(apiAssembly);

        using var provider = services.BuildServiceProvider();
        var items = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;

        var endpoints = new List<ApiEndpoint>();

        foreach (var descriptor in items.OfType<ControllerActionDescriptor>())
        {
            var site = $"{descriptor.ControllerTypeInfo.Name}.{descriptor.ActionName}";
            var template = descriptor.AttributeRouteInfo?.Template;

            if (string.IsNullOrWhiteSpace(template))
            {
                // Conventional routing. This service has none — every controller is attribute
                // routed — so an action arriving without a template is a shape this scanner has
                // not been taught. It is surfaced as unreachable rather than dropped.
                endpoints.Add(new ApiEndpoint("ANY", "/(NO-ATTRIBUTE-ROUTE)", "(none)", site));
                continue;
            }

            var methods = descriptor.ActionConstraints?
                .OfType<HttpMethodActionConstraint>()
                .SelectMany(c => c.HttpMethods)
                .Select(m => m.ToUpperInvariant())
                .Distinct()
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList() ?? [];

            if (methods.Count == 0) methods.Add("ANY");

            var path = NormalisePath(template);
            foreach (var method in methods)
            {
                endpoints.Add(new ApiEndpoint(method, path, template, site));
            }
        }

        return endpoints
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// <c>api/orders/{id:guid}/transform</c> → <c>/api/orders/{}/transform</c>;
    /// <c>api/dev/files/{**key}</c> → <c>/api/dev/files/{**}</c>. Route constraints and parameter
    /// names are noise for reachability — what matters is which segments a caller must supply.
    /// </summary>
    public static string NormalisePath(string template)
    {
        var trimmed = template.Trim();
        if (trimmed.StartsWith("~/", StringComparison.Ordinal)) trimmed = trimmed[1..];
        if (!trimmed.StartsWith('/')) trimmed = "/" + trimmed;

        var segments = trimmed.Split('/').Select(segment =>
            !segment.Contains('{') ? segment
            : segment.Contains("{*", StringComparison.Ordinal) ? "{**}"
            : "{}");

        var joined = string.Join('/', segments).TrimEnd('/');
        return joined.Length == 0 ? "/" : joined;
    }

    // ── Half 2: what the callers name ───────────────────────────────────────────────────

    /// <summary>
    /// <c>${API_BASE_URL}/api/…</c> — the frontend's normal form. The base is always an absolute
    /// origin (<c>src/lib/api/core.ts</c> normalises it and defaults it), so a product-code path
    /// always follows a closing brace and never stands alone.
    /// </summary>
    private static readonly Regex InterpolatedBase = new(
        @"\$\{\s*[A-Za-z_$][\w$]*\s*\}(/api/[^`""'\s\\]*)", RegexOptions.Compiled);

    /// <summary>A path written straight into a literal — the live-matrix harnesses do this.</summary>
    private static readonly Regex BareLiteralPath = new(
        @"[`""'](/api/[^`""'\s\\]*)", RegexOptions.Compiled);

    /// <summary>
    /// A module-private wrapper that prefixes <c>${API_BASE_URL}/api</c> itself, so its call sites
    /// carry a bare fragment with neither the base nor the word <c>api</c> anywhere on the line.
    /// Three modules under <c>src/lib/api/</c> are written this way, and one of them —
    /// <c>delivery.ts</c> — contains the string <c>/api/</c> zero times while calling four
    /// endpoints. A sweep that knows only the first two forms misses that file completely.
    ///
    /// <para>The wrapper's NAME is captured rather than assumed: <c>mapping.ts</c> declares two of
    /// them, <c>apiFetch</c> and <c>magicFetch</c>, and hard-coding the first name silently drops
    /// every endpoint reached through the second.</para>
    /// </summary>
    private static readonly Regex WrapperBody = new(
        @"\$\{\s*API_BASE_URL\s*\}/api\$\{", RegexOptions.Compiled);

    /// <summary>
    /// One hop of PATH indirection: <c>function basePath(id) { return `${API_BASE_URL}/api/…`; }</c>
    /// used later either as <c>`${basePath(id)}/test-fetch`</c> or as a bare argument,
    /// <c>fetchWithTimeout(basePath(id), { method: "PUT" })</c>. Without it, three genuinely-called
    /// catalog-source endpoints read as uncalled.
    /// </summary>
    private static readonly Regex PathHelperBody = new(
        @"\$\{\s*API_BASE_URL\s*\}(/api/[^`""'\s\\]*)", RegexOptions.Compiled);

    /// <summary>
    /// The FUNCTION a helper body sits inside, searched BACKWARDS from that body.
    ///
    /// <para>Both of the obvious ways to do this are wrong, and each was tried. Searching FORWARDS
    /// from a declaration takes the leftmost one: <c>const _mockSources = {};</c> three lines above
    /// <c>function basePath()</c> claims the name, swallows the real declaration inside its own
    /// match, and the helper is never seen — three catalog-source endpoints, plus
    /// <c>magicFetch</c>, whose name went to <c>const MAGIC_TIMEOUT_MS</c>. Searching BACKWARDS for
    /// any declaration takes the nearest LOCAL: <c>const res = await fetch(…)</c> sits between the
    /// URL and its enclosing function, so the wrapper gets named <c>res</c> and the three
    /// <c>po-mapping</c> endpoints go dark.</para>
    ///
    /// <para>So the pattern matches a FUNCTION shape only — <c>function name(</c>, or a
    /// <c>const name = (…) =&gt;</c> / <c>= async (</c> / <c>= function</c> — which the intervening
    /// locals (<c>= await …</c>, <c>= new …</c>, <c>= setTimeout(…)</c>, <c>= {}</c>) are not.</para>
    /// </summary>
    private static readonly Regex EnclosingFunctionBefore = new(
        @"function\s+([A-Za-z_$][\w$]*)"
        + @"|(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=;]{0,80})?=\s*(?:async\s*)?(?:function\b|\()",
        RegexOptions.Compiled | RegexOptions.RightToLeft);

    /// <summary>How far back a helper's declaration may sit from the URL it builds.</summary>
    private const int DeclarationWindow = 400;

    private static readonly Regex MethodOption = new(
        @"method\s*:\s*[""'`]([A-Za-z]+)[""'`]", RegexOptions.Compiled);

    /// <summary>`'…' + expr + '…'` — one splice, both quotes consumed.</summary>
    private static readonly Regex ConcatSplice = new(
        @"(['""])\s*\+\s*[A-Za-z_$][\w$.\[\]()]*\s*\+\s*\1", RegexOptions.Compiled);

    /// <summary>`'…/' + expr` — a literal that ends where a computed segment begins.</summary>
    private static readonly Regex ConcatTail = new(
        @"(['""])(\s*\+\s*[A-Za-z_$][\w$.\[\]()]*)", RegexOptions.Compiled);

    /// <summary>How far past a path literal a <c>method:</c> may sit and still belong to it.</summary>
    private const int MethodWindow = 600;

    /// <summary>
    /// Every API path named by <paramref name="text"/>, with the verb each is issued under.
    ///
    /// <para><paramref name="text"/> must already be comment-stripped: a path that exists only in
    /// a comment calls nothing, and crediting it is the exact shape this guard exists to refuse.
    /// <c>api-client.ts</c> documents <c>POST /api/suppliers/{id}/profiles</c> in a comment beside
    /// two GET callers and never posts to it; <c>AdminController</c> spells two admin routes into
    /// operator-facing error messages.</para>
    /// </summary>
    public static IReadOnlyList<CallerReference> ExtractCalls(string text, string source)
    {
        var joined = JoinConcatenations(text);
        var hits = new List<(int Index, string Path)>();

        foreach (Match m in InterpolatedBase.Matches(joined)) hits.Add((m.Index, m.Groups[1].Value));
        foreach (Match m in BareLiteralPath.Matches(joined)) hits.Add((m.Index, m.Groups[1].Value));

        foreach (var wrapper in Wrappers(joined))
        {
            var use = new Regex(@"\b" + Regex.Escape(wrapper) + @"\s*(?:<[^;{}]*?>)?\s*\(\s*[`""'](/[^`""'\s\\]*)");
            foreach (Match m in use.Matches(joined)) hits.Add((m.Index, "/api" + m.Groups[1].Value));
        }

        foreach (var (name, prefix) in PathHelpers(joined))
        {
            // `${basePath(id)}/test-fetch` — the helper's path plus a literal tail.
            var interpolated = new Regex(@"\$\{\s*" + Regex.Escape(name) + @"\s*\([^}]*\)\s*\}([^`""'\s\\]*)");
            foreach (Match m in interpolated.Matches(joined)) hits.Add((m.Index, prefix + m.Groups[1].Value));

            // `fetchWithTimeout(basePath(id), { method: "PUT" })` — a bare call, which is how the
            // catalog-source module writes its PUT and its DELETE. The declaration itself is
            // skipped: `function basePath(supplierId)` is not a use of basePath.
            var bare = new Regex(@"(?<!\$\{\s*)(?<!function\s)(?<!function\s{2})\b"
                                 + Regex.Escape(name) + @"\s*\(");
            foreach (Match m in bare.Matches(joined)) hits.Add((m.Index, prefix));
        }

        if (hits.Count == 0) return [];

        var ordered = hits.OrderBy(h => h.Index).ToList();
        var references = new List<CallerReference>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            // The verb is read from a window that STOPS at the next path literal, so one call's
            // options object can never be attributed to the call after it. No `method:` at all is
            // a GET, which is what fetch() does.
            var start = ordered[i].Index;
            var limit = i + 1 < ordered.Count
                ? Math.Min(ordered[i + 1].Index, start + MethodWindow)
                : Math.Min(joined.Length, start + MethodWindow);

            var window = joined[start..Math.Max(start, limit)];
            var verb = MethodOption.Match(window) is { Success: true } m
                ? m.Groups[1].Value.ToUpperInvariant()
                : "GET";

            references.Add(new CallerReference(verb, NormaliseCallPath(ordered[i].Path), source));
        }

        return references;
    }

    /// <summary>Helpers that return a whole API URL, as (declared name, the path it builds).</summary>
    public static IEnumerable<(string Name, string Prefix)> PathHelpers(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match body in PathHelperBody.Matches(text))
        {
            var name = DeclaredNameBefore(text, body.Index);
            if (name is not null && seen.Add(name)) yield return (name, body.Groups[1].Value);
        }
    }

    /// <summary>Module-private wrappers that prefix <c>${API_BASE_URL}/api</c> for their callers.</summary>
    public static IEnumerable<string> Wrappers(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match body in WrapperBody.Matches(text))
        {
            var name = DeclaredNameBefore(text, body.Index);
            if (name is not null && seen.Add(name)) yield return name;
        }
    }

    private static string? DeclaredNameBefore(string text, int index)
    {
        var from = Math.Max(0, index - DeclarationWindow);
        var match = EnclosingFunctionBefore.Match(text, from, index - from);
        if (!match.Success) return null;

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    /// <summary>
    /// Collapses <c>'…' + expr + '…'</c> back into one literal carrying a dynamic marker, so the
    /// concatenation form survives extraction. <c>scripts/live-matrix/runner.js</c> is the only
    /// place in either repository that writes paths this way —
    /// <c>API_BASE + '/api/orders/' + orderId + '/mapping-override/preview?format=' + outFmt</c> —
    /// and without this the sweep sees the stump <c>/api/orders/</c>, credits <c>GET /api/orders</c>
    /// it never called, and misses the two endpoints it did.
    /// </summary>
    public static string JoinConcatenations(string text) =>
        ConcatTail.Replace(ConcatSplice.Replace(text, Dynamic.ToString()), Dynamic + "$1$2");

    /// <summary>
    /// Drops the query and fragment, and folds every computed segment to <c>{}</c> so a caller
    /// path can be compared against a route template segment by segment.
    ///
    /// <para><b>A segment with a literal PREFIX keeps that prefix</b>, and getting this wrong is
    /// not cosmetic. The frontend appends an interpolated query string to the last segment —
    /// <c>`${API_BASE_URL}/api/exceptions${qs}`</c>, <c>`…/api/ops/dead-letter${qs}`</c> — and
    /// folding the whole segment to <c>{}</c> turns those into <c>/api/{}</c> and
    /// <c>/api/ops/{}</c>: two calls that reach nothing and two endpoints that then read
    /// unreachable while a screen is calling them every day. The route side never mixes a literal
    /// and a parameter inside one segment, so truncating at the interpolation is exact.</para>
    /// </summary>
    public static string NormaliseCallPath(string raw)
    {
        var path = raw;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];

        var segments = path.Split('/').Select(segment =>
        {
            var hole = FirstHole(segment);
            if (hole < 0) return segment;
            return hole == 0 ? "{}" : segment[..hole];
        });

        var joined = string.Join('/', segments).TrimEnd('/');
        return joined.Length == 0 ? "/" : joined;
    }

    /// <summary>Index of the first computed run in a segment, or -1.</summary>
    private static int FirstHole(string segment)
    {
        var interpolation = segment.IndexOf("${", StringComparison.Ordinal);
        var marker = segment.IndexOf(Dynamic);

        return (interpolation, marker) switch
        {
            (< 0, < 0) => -1,
            (< 0, _) => marker,
            (_, < 0) => interpolation,
            _ => Math.Min(interpolation, marker),
        };
    }

    // ── The matcher ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether <paramref name="call"/> could produce a request that routes to
    /// <paramref name="endpoint"/>.
    ///
    /// <para><b>The asymmetry is deliberate.</b> A route PARAMETER matches any caller segment; a
    /// route LITERAL matches only the same literal. A computed caller segment is therefore NOT
    /// credited against a literal route segment — <c>/revisions/${revisionId}/${action}</c> does
    /// not mark <c>/revisions/{}/publish</c> reachable. Crediting it would let one template with a
    /// computed tail declare a whole controller consumed, which is the loudest way this guard
    /// could go quietly false-green. The cost is a reasoned declaration for the few calls written
    /// that way; the cost of the other choice is silence.</para>
    /// </summary>
    public static bool Matches(ApiEndpoint endpoint, CallerReference call)
    {
        if (!string.Equals(endpoint.Method, "ANY", StringComparison.Ordinal)
            && !string.Equals(endpoint.Method, call.Method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var route = endpoint.Path.Split('/');
        var called = call.Path.Split('/');

        var catchAll = route.Length > 0 && route[^1] == "{**}";
        if (catchAll)
        {
            if (called.Length < route.Length - 1) return false;
        }
        else if (route.Length != called.Length)
        {
            return false;
        }

        var compare = catchAll ? route.Length - 1 : route.Length;

        for (var i = 0; i < compare; i++)
        {
            if (route[i] == "{}") continue;
            if (!string.Equals(route[i], called[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>Endpoints that no reference in <paramref name="calls"/> can reach.</summary>
    public static IReadOnlyList<ApiEndpoint> Unreachable(
        IReadOnlyList<ApiEndpoint> endpoints,
        IReadOnlyList<CallerReference> calls) =>
        endpoints.Where(endpoint => !calls.Any(call => Matches(endpoint, call))).ToList();

    // ── Corpus loading ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Locates the frontend checkout, or throws with the two ways to supply it.
    ///
    /// <para><b>There is no skip path, and that is the point.</b> The audit that commissioned this
    /// guard named <c>backendMirror.test.ts</c> as a guard that "has never run": it was
    /// <c>skipIf(!BACKEND)</c> and frontend CI never supplied a backend, so it reported green for
    /// months having compared nothing. A cross-repo guard gets exactly one chance not to repeat
    /// that, and it is refusing to have an "unable to check" state at all. CI supplies the checkout
    /// (see the frontend-checkout step in <c>.github/workflows/ci.yml</c>); a developer supplies it
    /// by having the two repositories side by side.</para>
    /// </summary>
    public static string FindFrontendRoot(string backendRoot)
    {
        var candidates = new List<string>();

        var configured = Environment.GetEnvironmentVariable("PROCULINK_FRONTEND_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);

        var parent = Directory.GetParent(backendRoot)?.FullName;
        if (parent is not null) candidates.Add(Path.Combine(parent, "project-proculink"));

        foreach (var candidate in candidates.Where(LooksLikeTheFrontend))
        {
            return Path.GetFullPath(candidate);
        }

        throw new InvalidOperationException(
            "ENDPOINT REACHABILITY GUARD cannot find the project-proculink checkout, and it has no "
            + "'unable to check' state on purpose — a cross-repo guard that shrugs is the "
            + "backendMirror.test.ts failure (green for months on skipIf(!BACKEND)).\n\n"
            + "Supply it either way:\n"
            + "  • clone dimnovare/project-proculink beside this repository, or\n"
            + "  • set PROCULINK_FRONTEND_PATH=/path/to/project-proculink\n\n"
            + "Looked in:\n  "
            + string.Join("\n  ", candidates.DefaultIfEmpty("(nowhere — no env var, and no parent directory)"))
            + "\n\nA candidate counts only if it holds package.json and src/lib/api-client.ts.");
    }

    private static bool LooksLikeTheFrontend(string root) =>
        File.Exists(Path.Combine(root, "package.json"))
        && File.Exists(Path.Combine(root, "src", "lib", "api-client.ts"));

    /// <summary>
    /// The caller corpus: frontend product code and the live harnesses, comment-stripped.
    ///
    /// <para><b>What is deliberately NOT in here</b>, because every one of them looks like a caller
    /// and is not: tests in either repository (a test is not a user path — the stance
    /// <c>OrphanGuardTests</c> takes on readers, for the same reason); <c>src/mocks/</c>, whose MSW
    /// handlers stand in FOR the API rather than call it, and which are stale enough to name routes
    /// that no longer exist; markdown and MDX documentation, because "documented and unreachable"
    /// is the defect itself; and the backend's own source, where <c>AdminController</c> spells a
    /// route into an error message and <c>PassportService</c> into a hint — prose that reads
    /// exactly like a call site to any text scanner.</para>
    /// </summary>
    public static IReadOnlyList<CallerFile> LoadCallerCorpus(string frontendRoot)
    {
        var files = new List<CallerFile>();

        Collect(Path.Combine(frontendRoot, "src"), [".ts", ".tsx"]);
        Collect(Path.Combine(frontendRoot, "scripts"), [".mjs", ".js", ".ts"]);

        return files;

        void Collect(string directory, string[] extensions)
        {
            if (!Directory.Exists(directory)) return;

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(frontendRoot, path);
                if (IsExcludedCallerFile(relative)) continue;

                files.Add(new CallerFile(relative, StripJsComments(File.ReadAllText(path))));
            }
        }
    }

    /// <summary>
    /// <c>.claude/</c> is excluded structurally: it holds full worktree COPIES of the frontend, and
    /// reading them would let another session's branch answer this repository's question.
    /// </summary>
    public static bool IsExcludedCallerFile(string relativePath)
    {
        var p = relativePath.Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..];

        return p.Contains("node_modules/", StringComparison.Ordinal)
            || p.Contains(".claude", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/.next/", StringComparison.Ordinal)
            || p.StartsWith("src/test/", StringComparison.Ordinal)
            || p.StartsWith("src/mocks/", StringComparison.Ordinal)
            || p.Contains("/__tests__/", StringComparison.Ordinal)
            || name.Contains(".test.", StringComparison.Ordinal)
            || name.Contains(".spec.", StringComparison.Ordinal)
            || name.EndsWith(".d.ts", StringComparison.Ordinal);
    }

    // ── The comment stripper ────────────────────────────────────────────────────────────

    private enum Frame { Template, Interpolation }

    /// <summary>
    /// Strips JavaScript/TypeScript comments in ONE left-to-right pass that knows what a literal
    /// is — including template literals and the <c>${…}</c> holes inside them, which nest:
    /// <c>api-client.ts:1171</c> builds a query with a backtick literal inside another backtick
    /// literal's interpolation.
    ///
    /// <para>Two regex passes cannot do this, and this codebase has already paid to find out. The
    /// orphan guard's stripper opened a block comment inside the string
    /// <c>"https://*.vercel.app"</c> and deleted 677 lines of the API composition root before any
    /// matching ran. Reordering the passes only moves the hole. The failure direction there was a
    /// FALSE finding, which is how a guard gets switched off.</para>
    /// </summary>
    public static string StripJsComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var stack = new Stack<Frame>();
        var braceDepth = new Stack<int>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            // Inside template-literal TEXT: nothing is a comment, and only ` or ${ changes state.
            if (stack.Count > 0 && stack.Peek() == Frame.Template)
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(text, i, 2);
                    i += 2;
                    continue;
                }

                if (c == '`')
                {
                    sb.Append('`');
                    stack.Pop();
                    i++;
                    continue;
                }

                if (c == '$' && i + 1 < text.Length && text[i + 1] == '{')
                {
                    sb.Append("${");
                    stack.Push(Frame.Interpolation);
                    braceDepth.Push(0);
                    i += 2;
                    continue;
                }

                sb.Append(c);
                i++;
                continue;
            }

            // Otherwise we are in code — top level, or inside a ${…} hole, which is also code.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n') sb.Append('\n');
                    i++;
                }

                i = Math.Min(i + 2, text.Length);
                continue;
            }

            if (c is '\'' or '"')
            {
                i = CopyQuoted(text, i, c, sb);
                continue;
            }

            if (c == '`')
            {
                sb.Append('`');
                stack.Push(Frame.Template);
                i++;
                continue;
            }

            if (stack.Count > 0 && stack.Peek() == Frame.Interpolation)
            {
                if (c == '{')
                {
                    braceDepth.Push(braceDepth.Pop() + 1);
                }
                else if (c == '}')
                {
                    var depth = braceDepth.Pop();
                    if (depth == 0)
                    {
                        // The hole closes; we are back inside the enclosing template's text.
                        sb.Append('}');
                        stack.Pop();
                        i++;
                        continue;
                    }

                    braceDepth.Push(depth - 1);
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Copies a <c>'…'</c> / <c>"…"</c> literal through; returns the index just past it.</summary>
    private static int CopyQuoted(string text, int i, char quote, StringBuilder sb)
    {
        sb.Append(quote);
        for (var k = i + 1; k < text.Length; k++)
        {
            if (text[k] == '\\' && k + 1 < text.Length)
            {
                sb.Append(text, k, 2);
                k++;
                continue;
            }

            sb.Append(text[k]);
            if (text[k] == quote) return k + 1;

            // A single-quoted literal cannot span lines. If we are here the source was
            // unbalanced (or this was an apostrophe in prose the stripper already removed);
            // resync at the newline rather than consuming the rest of the file.
            if (text[k] == '\n') return k + 1;
        }

        return text.Length;
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────

    public static string Render(
        IReadOnlyList<ApiEndpoint> unexplained,
        int endpointCount,
        int callerFileCount,
        int callCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ENDPOINT REACHABILITY GUARD — rule R1, pointed at the API surface.");
        sb.AppendLine();
        sb.AppendLine($"{unexplained.Count} endpoint(s) of {endpointCount} can be routed to and are reached by");
        sb.AppendLine($"nothing ({callCount} call sites read from {callerFileCount} caller files):");
        sb.AppendLine();

        foreach (var endpoint in unexplained
                     .OrderBy(e => e.Path, StringComparer.Ordinal)
                     .ThenBy(e => e.Method, StringComparer.Ordinal))
        {
            sb.AppendLine($"  • {endpoint.Method,-6} {endpoint.Path}");
            sb.AppendLine($"        {endpoint.Site}   (route template: {endpoint.Template})");
        }

        sb.AppendLine();
        sb.AppendLine("An endpoint nothing calls is a door with no handle: it can be documented, tested and");
        sb.AppendLine("deployed while no user can ever reach it. Fix it one of three ways:");
        sb.AppendLine("  1. give it a caller — a screen, a control, or a live harness; or");
        sb.AppendLine("  2. delete it; or");
        sb.AppendLine("  3. if the caller is outside both repositories (a customer's ERP, a provider webhook,");
        sb.AppendLine("     a runbook curl), DECLARE it in EndpointReachabilityGuardTests with a written");
        sb.AppendLine("     reason citing something a reviewer can open — and move the baseline in the PR,");
        sb.AppendLine("     in the open. That list is shrink-only; it is not a place to put new doors.");

        return sb.ToString();
    }
}
