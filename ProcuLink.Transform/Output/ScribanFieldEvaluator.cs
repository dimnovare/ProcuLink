using System.Globalization;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Sandboxed Scriban evaluator for the per-field <c>Expression</c> on an
/// <see cref="ProcuLink.Core.Services.Mapping.OutputFieldRule"/> /
/// <see cref="ProcuLink.Core.Services.Mapping.SourceFieldRule"/> (heart-piece-flex flexible mapping).
///
/// <para>The founder wants the canonical→output mapping to be "flexible with Scriban templating" for
/// ANY field. Today a rule resolves <c>canonicalField | fixedValue | manipulator-chain</c>; an
/// optional free-form expression (e.g. <c>{{ order.PoNumber }}</c>, <c>{{ line.Quantity * line.UnitPrice }}</c>,
/// <c>{{ order.Currency }}-{{ line.SupplierItemCode }}</c>) is now ALSO supported and, when present,
/// takes precedence over the field/fixed value before the existing manipulator chain runs.</para>
///
/// <para><b>Scope.</b> Header expressions see <c>order</c> only. Line expressions see <c>order</c> +
/// <c>line</c>. Both scope objects are built from the same canonical field bag the rest of
/// <see cref="MappedTransformService"/> uses, with the numeric line fields (Quantity / UnitPrice /
/// LineTotal / LineNumber) exposed as real numbers so arithmetic (<c>line.Quantity * line.UnitPrice</c>)
/// works. Any custom field is reachable by its key under the matching scope.</para>
///
/// <para><b>Sandbox.</b> The template is rendered with a <see cref="TemplateContext"/> whose member
/// renamer is identity (so <c>order.PoNumber</c> matches the dictionary key verbatim, NOT Scriban's
/// default snake_case), relaxed member access is ON (an unknown member yields empty rather than
/// throwing), and loops/recursion are bounded. Scriban is a pure string-template engine — it has no
/// file, network, process, or reflection-into-arbitrary-CLR surface — so the only thing reachable is
/// the data we put in scope. We additionally inject NO built-in helper functions (a bare
/// <see cref="ScriptObject"/> global, NOT <c>BuiltinFunctions</c>), so there is no
/// <c>include</c>/<c>import</c>/IO/regex surface at all.</para>
///
/// <para><b>Never crashes the transform.</b> A compile or runtime error returns
/// <see cref="EvaluationResult.Failure"/> with a clear message; the caller decides what to do
/// (flag the line for review / keep going). The evaluator never throws.</para>
/// </summary>
public static class ScribanFieldEvaluator
{
    /// <summary>
    /// Outcome of evaluating one expression. Either a rendered string <see cref="Value"/> (success),
    /// or an <see cref="Error"/> message (compile/runtime failure). Never both; never throws.
    /// </summary>
    public readonly record struct EvaluationResult(bool Ok, string? Value, string? Error)
    {
        public static EvaluationResult Success(string? value) => new(true, value, null);
        public static EvaluationResult Failure(string error)  => new(false, null, error);
    }

    /// <summary>
    /// Bound on the total rendered output length, as a defence-in-depth guard against a pathological
    /// expression producing an enormous string. Generous for any realistic field value.
    /// </summary>
    private const int MaxOutputLength = 100_000;

    /// <summary>
    /// Evaluate a header-scope expression. Only <c>order</c> is in scope.
    /// </summary>
    public static EvaluationResult EvaluateHeader(
        string expression, IReadOnlyDictionary<string, string> headerRow)
        => Evaluate(expression, BuildScope(orderRow: headerRow, lineRow: null));

    /// <summary>
    /// Evaluate a line-scope expression. Both <c>order</c> and <c>line</c> are in scope. The
    /// <paramref name="lineRow"/> bag carries header keys too (see <c>MappedTransformService.BuildLineRow</c>),
    /// so <c>order.*</c> is still resolvable from it.
    /// </summary>
    public static EvaluationResult EvaluateLine(
        string expression, IReadOnlyDictionary<string, string> lineRow)
        => Evaluate(expression, BuildScope(orderRow: lineRow, lineRow: lineRow));

    // ── Core ────────────────────────────────────────────────────────────────────

    private static EvaluationResult Evaluate(string expression, ScriptObject scope)
    {
        if (expression is null)
            return EvaluationResult.Failure("Expression is null.");

        Template template;
        try
        {
            template = Template.Parse(expression);
        }
        catch (Exception ex)
        {
            return EvaluationResult.Failure($"Expression failed to compile: {ex.Message}");
        }

        if (template.HasErrors)
        {
            var first = template.Messages.FirstOrDefault(m => m.Type == ParserMessageType.Error);
            return EvaluationResult.Failure(
                $"Expression failed to compile: {first?.Message ?? "syntax error"}");
        }

        try
        {
            var context = new TemplateContext
            {
                // Identity renamer: {{ order.PoNumber }} must look up the literal key "PoNumber",
                // not Scriban's default snake_case "po_number".
                MemberRenamer = member => member.Name,
                // Unknown members render as empty rather than throwing — additive + safe.
                EnableRelaxedMemberAccess = true,
                // Bound any loop a (misguided) expression might contain.
                LoopLimit = 1000,
                RecursiveLimit = 100,
            };
            context.PushGlobal(scope);

            var rendered = template.Render(context);

            if (rendered is { Length: > MaxOutputLength })
                return EvaluationResult.Failure(
                    $"Expression output exceeded {MaxOutputLength} characters.");

            return EvaluationResult.Success(rendered);
        }
        catch (Exception ex)
        {
            return EvaluationResult.Failure($"Expression failed to evaluate: {ex.Message}");
        }
    }

    // ── Scope construction ────────────────────────────────────────────────────────

    /// <summary>
    /// Build the Scriban global scope. <c>order</c> is built from <paramref name="orderRow"/>;
    /// <c>line</c> is built from <paramref name="lineRow"/> when non-null (line scope), else absent
    /// (header scope). Numeric line fields are exposed as real numbers so arithmetic works.
    /// </summary>
    private static ScriptObject BuildScope(
        IReadOnlyDictionary<string, string> orderRow,
        IReadOnlyDictionary<string, string>? lineRow)
    {
        var scope = new ScriptObject();

        scope["order"] = BuildScopeObject(orderRow, NumericOrderKeys);

        // Always expose `line` so a header expression that touches `line.X` renders empty (relaxed
        // member access) instead of failing — same "unknown member → empty" contract as `order.X`.
        // In header scope it is an empty object; in line scope it carries the line's fields.
        scope["line"] = lineRow is not null
            ? BuildScopeObject(lineRow, NumericLineKeys)
            : new ScriptObject();

        return scope;
    }

    /// <summary>Canonical numeric header fields — none today, but kept explicit for symmetry.</summary>
    private static readonly HashSet<string> NumericOrderKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Canonical numeric line fields. Exposed as <see cref="decimal"/> so expressions can do
    /// arithmetic (<c>line.Quantity * line.UnitPrice</c>) instead of string concatenation.
    /// </summary>
    private static readonly HashSet<string> NumericLineKeys =
        new(StringComparer.Ordinal) { "Quantity", "UnitPrice", "LineTotal", "LineNumber" };

    private static ScriptObject BuildScopeObject(
        IReadOnlyDictionary<string, string> row, HashSet<string> numericKeys)
    {
        var obj = new ScriptObject();
        foreach (var (key, value) in row)
        {
            if (string.IsNullOrEmpty(key)) continue;

            if (numericKeys.Contains(key)
                && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
            {
                obj[key] = num;
            }
            else
            {
                obj[key] = value;
            }
        }

        return obj;
    }
}
