using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Katout.FlowTask.Testing;

/// <summary>
/// For <see cref="FlowAssert.CanSkipUnwinding"/>: reads the exception regions of the MoveNext of each state machine
/// declared in a type, and the awaits inside them.
/// </summary>
internal static class UnwindRegions
{
    /// <summary>
    /// The async methods declared in <paramref name="type"/> (its lambdas and local functions included) that have a block
    /// around an await that unwinding would run. Throws NotSupportedException where method bodies cannot be read.
    /// </summary>
    internal static List<string> MethodsWithCleanupAroundAwait(Type type)
    {
        var found = new List<string>();
        // The state machines are nested in the type, or in a closure class ("<>c", "<>c__DisplayClass") nested there.
        // Iterative, with an explicit stack: closure classes nest as deep as the lambdas do.
        var pending = new Stack<Type>();
        foreach (var t in type.GetNestedTypes(BindingFlags.NonPublic)) pending.Push(t);
        while (pending.Count > 0)
        {
            var t = pending.Pop();
            if (t.Name.Length == 0 || t.Name[0] != '<') continue;
            if (typeof(IAsyncStateMachine).IsAssignableFrom(t))
            {
                var moveNext = t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    ?? throw Unreadable(t);
                if (HasCleanupAroundAwait(moveNext, t)) found.Add(MethodName(t.Name));
            }

            foreach (var n in t.GetNestedTypes(BindingFlags.NonPublic)) pending.Push(n);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    static NotSupportedException Unreadable(Type stateMachine) =>
        new($"The body of {stateMachine.FullName}.MoveNext cannot be read on this runtime (NativeAOT and IL2CPP have none): run FlowAssert.CanSkipUnwinding under the JIT or Mono, in the editor or in .NET tests.");

    /// <summary>
    /// True when an exception region of <paramref name="moveNext"/> (other than the compiler's outermost catch) that
    /// unwinding would enter contains an await.
    /// </summary>
    static bool HasCleanupAroundAwait(MethodInfo moveNext, Type stateMachine)
    {
        MethodBody body;
        try
        {
            body = moveNext.GetMethodBody();
        }
        catch (NotSupportedException)
        {
            body = null;
        }

        var il = body?.GetILAsByteArray() ?? throw Unreadable(stateMachine);
        var clauses = body.ExceptionHandlingClauses;
        if (clauses.Count < 2) return false;
        var awaits = FindAwaits(il, moveNext.Module, stateMachine) ?? throw Unreadable(stateMachine);
        if (awaits.Count == 0) return false;

        // The compiler's catch, which hands the method's exception to the builder, encloses all the code: the widest try.
        var outer = 0;
        for (var i = 1; i < clauses.Count; i++)
        {
            if (clauses[i].TryLength > clauses[outer].TryLength) outer = i;
        }

        for (var i = 0; i < clauses.Count; i++)
        {
            if (i == outer || !RunsOnUnwind(clauses[i])) continue;
            var start = clauses[i].TryOffset;
            var end = start + clauses[i].TryLength;
            foreach (var offset in awaits)
            {
                if (offset >= start && offset < end) return true;
            }
        }

        return false;
    }

    /// <summary>A finally or fault block runs as FlowCanceledException passes; a filter may take it; a catch does when its type does.</summary>
    static bool RunsOnUnwind(ExceptionHandlingClause clause) =>
        clause.Flags != ExceptionHandlingClauseOptions.Clause || clause.CatchType == null || clause.CatchType.IsAssignableFrom(typeof(FlowCanceledException));

    /// <summary>
    /// The offsets of the builder calls an await suspends with (AwaitOnCompleted, AwaitUnsafeOnCompleted); null when the
    /// instructions cannot be read.
    /// </summary>
    static List<int> FindAwaits(byte[] il, Module module, Type stateMachine)
    {
        var typeArgs = stateMachine.IsGenericType ? stateMachine.GetGenericArguments() : null;
        var result = new List<int>();
        var i = 0;
        while (i < il.Length)
        {
            var at = i;
            OpCode op;
            if (il[i] == 0xFE)
            {
                if (i + 1 >= il.Length) return null;
                op = OpCodeTable.TwoByte[il[i + 1]];
                i += 2;
            }
            else
            {
                op = OpCodeTable.OneByte[il[i]];
                i++;
            }

            if (op.Size == 0) return null;
            if ((op == OpCodes.Call || op == OpCodes.Callvirt) && i + 4 <= il.Length)
            {
                MethodBase m;
                try
                {
                    m = module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, null);
                }
                catch (ArgumentException)
                {
                    return null;
                }

                if (m.Name is "AwaitUnsafeOnCompleted" or "AwaitOnCompleted") result.Add(at);
            }

            i += OperandSize(op, il, i);
        }

        return result;
    }

    static int OperandSize(OpCode op, byte[] il, int at) =>
        op.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => at + 4 <= il.Length ? 4 + (4 * BitConverter.ToInt32(il, at)) : il.Length,
            _ => 4,
        };

    /// <summary>
    /// The name of the method a state machine type was made for: "&lt;Run&gt;d__3" gives "Run", a local function's
    /// "&lt;&lt;Outer&gt;g__Inner|0_0&gt;d" gives "Inner", a lambda's "&lt;&lt;Make&gt;b__0_0&gt;d" gives "Make".
    /// </summary>
    static string MethodName(string typeName)
    {
        var local = typeName.IndexOf(">g__", StringComparison.Ordinal);
        if (local >= 0)
        {
            var name = typeName[(local + 4)..];
            var bar = name.IndexOf('|', StringComparison.Ordinal);
            return bar >= 0 ? name[..bar] : name;
        }

        var start = 0;
        while (start < typeName.Length && typeName[start] == '<') start++;
        var end = typeName.IndexOf('>', start);
        return end > start ? typeName[start..end] : typeName;
    }

    /// <summary>The IL opcodes by their bytes, filled once (a static constructor runs once, on one thread).</summary>
    static class OpCodeTable
    {
        internal static readonly OpCode[] OneByte = new OpCode[256];
        internal static readonly OpCode[] TwoByte = new OpCode[256];

#pragma warning disable CA1810 // Both arrays are filled in one pass over the fields.
        static OpCodeTable()
#pragma warning restore CA1810
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is not OpCode op) continue;
                var value = (ushort)op.Value;
                if (op.Size == 1) OneByte[value] = op;
                else TwoByte[value & 0xFF] = op;
            }
        }
    }
}
