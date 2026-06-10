using System.Globalization;
using Scriban;
using Scriban.Functions;
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

    // ── Shared sandbox (also used by ScribanTemplateTransformService) ──────────────

    /// <summary>
    /// Build a <see cref="TemplateContext"/> with the SAME sandbox posture the per-field evaluator
    /// uses (identity member renamer so <c>order.PoNumber</c> matches the literal key; relaxed member
    /// access so an unknown member renders empty instead of throwing; bounded loops/recursion), and
    /// push <paramref name="global"/> as the global scope.
    ///
    /// <para><b>Builtins.</b> A bare per-field expression needs no helper functions, so the per-field
    /// path passes <paramref name="enableSafeBuiltins"/> = <c>false</c> (a bare <see cref="ScriptObject"/>
    /// global — no <c>BuiltinFunctions</c> surface at all). WHOLE-DOCUMENT templates legitimately need
    /// the pure value-transform helpers (<c>string.*</c>, <c>array.*</c>, <c>math.*</c>, <c>date.*</c>,
    /// <c>object.*</c>) to reshape values into a supplier's structure, so the template path passes
    /// <c>true</c> to import that CURATED set. Even then the context is still sandboxed: Scriban has no
    /// file/network/process surface, and <c>include</c>/<c>import</c> are inert because
    /// <see cref="TemplateContext.TemplateLoader"/> is left null (no loader = those statements fail
    /// closed). The <c>html</c>/<c>regex</c>/<c>object.eval</c>-style reflection helpers are NOT
    /// imported.</para>
    /// </summary>
    internal static TemplateContext BuildSafeTemplateContext(ScriptObject global, bool enableSafeBuiltins)
    {
        var context = new TemplateContext
        {
            // Identity renamer: {{ OrderNr }} / {{ order.PoNumber }} look up the literal key, NOT
            // Scriban's default snake_case.
            MemberRenamer = member => member.Name,
            // Unknown members render as empty rather than throwing — additive + safe.
            EnableRelaxedMemberAccess = true,
            // Bound loops/recursion a (misguided) template might contain.
            LoopLimit = 100_000,
            RecursiveLimit = 100,
            // No template loader → {{ include }} / {{ import }} fail closed (no file/IO surface).
            TemplateLoader = null,
        };

        if (enableSafeBuiltins)
        {
            // Curated, PURE value-transform builtins only — string/array/math/date/object — each
            // exposed under its conventional lowercase family name (string.upcase, array.size,
            // math.round, date.to_string, object.default). Built from Scriban's own function classes,
            // which do no IO. The risky families are deliberately NOT imported: there is no `regex`,
            // no `html`, no `include`/`import` (the latter need a TemplateLoader, left null above).
            // Pushed UNDER the data global so order data still wins on a name clash.
            var families = new ScriptObject
            {
                ["string"] = NewFunctionFamily<StringFunctions>(),
                ["array"]  = NewFunctionFamily<ArrayFunctions>(),
                ["math"]   = NewFunctionFamily<MathFunctions>(),
                ["date"]   = NewFunctionFamily<DateTimeFunctions>(),
                ["object"] = NewFunctionFamily<ObjectFunctions>(),
            };
            context.PushGlobal(families);
        }

        context.PushGlobal(global);
        return context;
    }

    /// <summary>
    /// Instantiate one of Scriban's built-in function families. Each family type derives from
    /// <see cref="ScriptObject"/> and self-registers its snake_case functions in its constructor,
    /// so a bare <c>new()</c> yields a ready-to-use <c>string</c>/<c>array</c>/… object.
    /// </summary>
    private static ScriptObject NewFunctionFamily<T>() where T : ScriptObject, new() => new T();
}
