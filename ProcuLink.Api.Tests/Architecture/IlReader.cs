using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The one IL decoder the architecture guards share.
///
/// <para><b>Why it is shared and not copied.</b> Two guards now read compiled IL to answer a
/// question prose cannot be trusted with — <see cref="BillingGateIlScanner"/> ("does this method
/// provably reach a billing gate?") and <see cref="OutboundArtifactSelectionIlScanner"/> ("does
/// this method pick an outbound artifact by recency, and does it consult the rule?"). Both need
/// the same three primitives: the opcode table, the operand-size table, and the trick of following
/// an async method to its state machine's <c>MoveNext</c>. The progress log records what happens
/// when a scanner-shaped helper gets duplicated instead: two copies drift, and the one nobody
/// fixed is the one still shipping the bug (backend <c>504d9cc</c> deleted a duplicated comment
/// stripper for exactly that reason).</para>
///
/// <para>Nothing here interprets meaning. It hands back instructions; deciding what a call
/// <i>means</i> is each scanner's own business.</para>
/// </summary>
internal static class IlReader
{
    /// <summary>One decoded instruction: its opcode and the offset its operand starts at.</summary>
    public readonly record struct Instruction(OpCode Op, int OperandStart);

    /// <summary>opcode byte(s) → <see cref="OpCode"/>, built once from the BCL's own table.</summary>
    private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodeTable();

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var map = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op) map[op.Value] = op;
        }
        return map;
    }

    /// <summary>
    /// The body to actually read for <paramref name="method"/>. For an <c>async</c> or iterator
    /// method that is the generated state machine's <c>MoveNext</c>, not the stub the compiler
    /// leaves behind under the original name.
    ///
    /// <para>Skipping this step is not a small loss of fidelity — nearly every method worth
    /// scanning in this codebase is <c>async</c>, and a scanner that read the stub would report
    /// "no calls at all" for all of them and be worse than useless.</para>
    /// </summary>
    public static (MethodBody Body, MethodBase Owner)? BodyOf(MethodBase method)
    {
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                        ?? method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;

        if (stateMachine is not null)
        {
            var moveNext = stateMachine.GetMethod(
                "MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext?.GetMethodBody() is { } smBody) return (smBody, moveNext);
        }

        try
        {
            return method.GetMethodBody() is { } body ? (body, method) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The raw IL of a body, or an empty array when the runtime will not hand it over.</summary>
    public static byte[] IlOf(MethodBody body)
    {
        try { return body.GetILAsByteArray() ?? []; }
        catch { return []; }
    }

    /// <summary>
    /// Walks <paramref name="il"/> instruction by instruction.
    ///
    /// <para>Stops — rather than guessing — at an opcode encoding it does not recognise or an
    /// operand that runs off the end. Every consumer of this reader uses it to <i>prove</i>
    /// something (a gate is reached, a rule is consulted), so stopping early can only make a caller
    /// stricter, never wave a violation through.</para>
    /// </summary>
    public static IEnumerable<Instruction> Decode(byte[] il)
    {
        var pos = 0;
        while (pos < il.Length)
        {
            short code = il[pos];
            pos++;
            if (code == 0xFE && pos < il.Length)
            {
                code = (short)(0xFE00 | il[pos]);
                pos++;
            }

            if (!OpCodeByValue.TryGetValue(code, out var op)) yield break; // unknown encoding: stop, never guess

            var operandStart = pos;
            var size = OperandSize(op, il, operandStart);
            if (size < 0) yield break;
            pos += size;
            if (pos > il.Length) yield break;

            yield return new Instruction(op, operandStart);
        }
    }

    /// <summary>Operand width in bytes, or -1 when the operand cannot be measured within bounds.</summary>
    public static int OperandSize(OpCode op, byte[] il, int operandStart) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => operandStart + 4 <= il.Length
            ? 4 + 4 * BitConverter.ToInt32(il, operandStart)
            : -1,
        _ => 0,
    };

    /// <summary>The <see cref="int"/> this instruction pushes, when it pushes one.</summary>
    public static int? Int32Constant(OpCode op, byte[] il, int operandStart)
    {
        if (op == OpCodes.Ldc_I4_M1) return -1;
        if (op == OpCodes.Ldc_I4_0) return 0;
        if (op == OpCodes.Ldc_I4_1) return 1;
        if (op == OpCodes.Ldc_I4_2) return 2;
        if (op == OpCodes.Ldc_I4_3) return 3;
        if (op == OpCodes.Ldc_I4_4) return 4;
        if (op == OpCodes.Ldc_I4_5) return 5;
        if (op == OpCodes.Ldc_I4_6) return 6;
        if (op == OpCodes.Ldc_I4_7) return 7;
        if (op == OpCodes.Ldc_I4_8) return 8;
        if (op == OpCodes.Ldc_I4_S) return operandStart < il.Length ? (sbyte)il[operandStart] : null;
        if (op == OpCodes.Ldc_I4) return operandStart + 4 <= il.Length ? BitConverter.ToInt32(il, operandStart) : null;
        return null;
    }

    // ── Reflection plumbing shared by both scanners ───────────────────────────

    public static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    public static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        try { return type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)).ToArray(); }
        catch { return []; }
    }
}
