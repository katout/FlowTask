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
    /// around an await that unwinding would run, as the source names them ("'Run'", "a lambda in 'Make'"). Throws
    /// NotSupportedException where method bodies cannot be read.
    /// </summary>
    internal static List<string> MethodsWithCleanupAroundAwait(Type type)
    {
        // Where the runtime has no method bodies or state machine types (NativeAOT, IL2CPP), reading the type alone would
        // find nothing and pass: read a known method first. A trimmed build removes the methods that nothing calls, with
        // their state machines, so the check sees only the methods that build keeps (the only ones that can run in it).
        if (!Probe.Readable) throw Unreadable(type);
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
                    ?? throw Unreadable(type);
                if (HasCleanupAroundAwait(moveNext, t, type)) found.Add(SourceName(t.Name));
            }

            foreach (var n in t.GetNestedTypes(BindingFlags.NonPublic)) pending.Push(n);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    static NotSupportedException Unreadable(Type type) =>
        new($"The async method bodies of {type} cannot be read on this runtime (NativeAOT and IL2CPP have none): run FlowAssert.CanSkipUnwinding under the JIT or Mono, in the editor or in .NET tests.");

    /// <summary>
    /// True when an exception region of <paramref name="moveNext"/> (other than the compiler's outermost catch) that
    /// unwinding would enter contains an await.
    /// </summary>
    static bool HasCleanupAroundAwait(MethodInfo moveNext, Type stateMachine, Type owner)
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

        var il = body?.GetILAsByteArray() ?? throw Unreadable(owner);
        var clauses = body.ExceptionHandlingClauses;
        // MoveNext always has the compiler's outermost catch: no instructions or no region means the body was not read.
        if (il.Length == 0 || clauses.Count == 0) throw Unreadable(owner);
        if (clauses.Count < 2) return false;
        var typeArgs = stateMachine.IsGenericType ? stateMachine.GetGenericArguments() : null;
        var awaits = FindAwaits(il, moveNext.Module, typeArgs) ?? throw Unreadable(owner);
        if (awaits.Count == 0) return false;

        // The compiler's catch, which hands the method's exception to the builder, encloses all the code: the widest try.
        var outer = 0;
        for (var i = 1; i < clauses.Count; i++)
        {
            if (clauses[i].TryLength > clauses[outer].TryLength) outer = i;
        }

        for (var i = 0; i < clauses.Count; i++)
        {
            if (i == outer || !RunsOnUnwind(clauses[i], il, moveNext.Module, typeArgs)) continue;
            var start = clauses[i].TryOffset;
            var end = start + clauses[i].TryLength;
            foreach (var offset in awaits)
            {
                if (offset >= start && offset < end) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A finally or fault block runs as FlowCanceledException passes; a catch does when its type does, or may when its
    /// type is a type parameter; a filter may take it, unless its instructions are seen to return false for it.
    /// </summary>
    static bool RunsOnUnwind(ExceptionHandlingClause clause, byte[] il, Module module, Type[] typeArgs) =>
        clause.Flags switch
        {
            ExceptionHandlingClauseOptions.Clause => clause.CatchType == null || clause.CatchType.IsGenericParameter
                || clause.CatchType.IsAssignableFrom(typeof(FlowCanceledException)),
            ExceptionHandlingClauseOptions.Filter => !FilterLetsCancellationPass(il, clause.FilterOffset, clause.HandlerOffset, module, typeArgs),
            _ => true,
        };

    /// <summary>
    /// True when the filter at [<paramref name="start"/>, <paramref name="end"/>) comes to endfilter with 0 (does not
    /// take the exception) when the exception is a FlowCanceledException, as <c>when (e is not FlowCanceledException)</c>
    /// does. It follows the instructions with that exception: type tests (isinst; FlowCanceledException is sealed, so each
    /// has one answer), constants, comparisons, branches, and the locals and fields of the state machine they store to.
    /// Any other instruction (a call, a static field, another object's field) has no known result, so the filter may
    /// take the exception: false.
    /// </summary>
    static bool FilterLetsCancellationPass(byte[] il, int start, int end, Module module, Type[] typeArgs)
    {
        var stack = new Stack<Value>();
        stack.Push(Value.Exception);
        var locals = new Dictionary<int, Value>();
        var fields = new Dictionary<int, Value>();
        var at = start;
        // The compiler's filters only branch forward: the bound is for bodies it did not write.
        for (var steps = 0; steps < 1024; steps++)
        {
            if (at < start || at >= end || !Decode(il, at, out var op, out var operand)) return false;
            var next = operand + OperandSize(op, il, operand);
            if (next > end) return false;
            if (op == OpCodes.Endfilter) return stack.Count == 1 && stack.Pop().IsInt(0);
            if (!Step(op, il, operand, next, module, typeArgs, stack, locals, fields, out at)) return false;
        }

        return false;
    }

    /// <summary>One instruction of <see cref="FilterLetsCancellationPass"/>; false when its result is not known.</summary>
    static bool Step(OpCode op, byte[] il, int operand, int next, Module module, Type[] typeArgs, Stack<Value> stack,
        Dictionary<int, Value> locals, Dictionary<int, Value> fields, out int target)
    {
        target = next;
        if (op == OpCodes.Nop) return true;
        if (op == OpCodes.Ldnull) return Push(stack, Value.Null);
        if (op == OpCodes.Ldarg_0) return Push(stack, Value.This);
        if (op == OpCodes.Ldc_I4_M1) return Push(stack, Value.Int(-1));
        if (op.Size == 1 && op.Value >= OpCodes.Ldc_I4_0.Value && op.Value <= OpCodes.Ldc_I4_8.Value)
            return Push(stack, Value.Int(op.Value - OpCodes.Ldc_I4_0.Value));
        if (op == OpCodes.Ldc_I4_S) return Push(stack, Value.Int((sbyte)il[operand]));
        if (op == OpCodes.Ldc_I4) return Push(stack, Value.Int(BitConverter.ToInt32(il, operand)));
        if (LocalIndex(op, il, operand, OpCodes.Ldloc_0, OpCodes.Ldloc_S, OpCodes.Ldloc) is int load)
            return locals.TryGetValue(load, out var local) && Push(stack, local);
        if (op == OpCodes.Br_S || op == OpCodes.Br)
        {
            target = BranchTarget(op, il, operand, next);
            return true;
        }

        if (stack.Count == 0) return false;
        if (op == OpCodes.Pop)
        {
            stack.Pop();
            return true;
        }

        if (op == OpCodes.Dup) return Push(stack, stack.Peek());
        if (LocalIndex(op, il, operand, OpCodes.Stloc_0, OpCodes.Stloc_S, OpCodes.Stloc) is int store)
        {
            locals[store] = stack.Pop();
            return true;
        }

        if (op == OpCodes.Brtrue_S || op == OpCodes.Brtrue || op == OpCodes.Brfalse_S || op == OpCodes.Brfalse)
        {
            var truth = stack.Pop().Truth;
            if (truth == null) return false;
            if (truth == (op == OpCodes.Brtrue_S || op == OpCodes.Brtrue)) target = BranchTarget(op, il, operand, next);
            return true;
        }

        if (op == OpCodes.Isinst)
        {
            var value = stack.Pop();
            if (value.Kind == ValueKind.Null) return Push(stack, Value.Null);
            if (value.Kind != ValueKind.Exception) return false;
            Type type;
            try
            {
                type = module.ResolveType(BitConverter.ToInt32(il, operand), typeArgs, null);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (type.ContainsGenericParameters) return false;
            return Push(stack, type.IsAssignableFrom(typeof(FlowCanceledException)) ? Value.Exception : Value.Null);
        }

        if (op == OpCodes.Ldfld)
        {
            if (stack.Pop().Kind != ValueKind.This) return false;
            return fields.TryGetValue(BitConverter.ToInt32(il, operand), out var field) && Push(stack, field);
        }

        if (stack.Count < 2) return false;
        if (op == OpCodes.Stfld)
        {
            var value = stack.Pop();
            if (stack.Pop().Kind != ValueKind.This) return false;
            fields[BitConverter.ToInt32(il, operand)] = value;
            return true;
        }

        if (op == OpCodes.Ceq || op == OpCodes.Cgt || op == OpCodes.Cgt_Un || op == OpCodes.Clt || op == OpCodes.Clt_Un)
        {
            var right = stack.Pop();
            var compared = Compare(op, stack.Pop(), right);
            return compared != null && Push(stack, Value.Int(compared.Value ? 1 : 0));
        }

        return false;
    }

    static bool Push(Stack<Value> stack, Value value)
    {
        stack.Push(value);
        return true;
    }

    /// <summary>A comparison the filter makes; null when its answer is not known.</summary>
    static bool? Compare(OpCode op, Value left, Value right)
    {
        if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
        {
            if (op == OpCodes.Ceq) return left.Number == right.Number;
            if (op == OpCodes.Cgt) return left.Number > right.Number;
            if (op == OpCodes.Clt) return left.Number < right.Number;
            if (op == OpCodes.Cgt_Un) return (uint)left.Number > (uint)right.Number;
            return (uint)left.Number < (uint)right.Number;
        }

        // References (the exception, or null): equality, and "greater than null", the compiler's test for non-null.
        if (!left.IsReference || !right.IsReference) return null;
        if (op == OpCodes.Ceq) return left.Kind == right.Kind;
        if (op == OpCodes.Cgt_Un) return left.Kind == ValueKind.Exception && right.Kind == ValueKind.Null;
        if (op == OpCodes.Clt_Un) return left.Kind == ValueKind.Null && right.Kind == ValueKind.Exception;
        return null;
    }

    /// <summary>The index of the local an stloc or ldloc form names (first of the four short ones, _S, long); else null.</summary>
    static int? LocalIndex(OpCode op, byte[] il, int operand, OpCode first, OpCode shortForm, OpCode longForm)
    {
        if (op.Size == 1 && op.Value >= first.Value && op.Value <= first.Value + 3) return op.Value - first.Value;
        if (op == shortForm) return il[operand];
        if (op == longForm) return BitConverter.ToUInt16(il, operand);
        return null;
    }

    static int BranchTarget(OpCode op, byte[] il, int operand, int next) =>
        op.OperandType == OperandType.ShortInlineBrTarget ? next + (sbyte)il[operand] : next + BitConverter.ToInt32(il, operand);

    enum ValueKind
    {
        Exception,
        Null,
        Int,
        This,
    }

    /// <summary>A value on the stack of a filter that <see cref="FilterLetsCancellationPass"/> follows.</summary>
    readonly struct Value
    {
        internal static readonly Value Exception = new(ValueKind.Exception, 0);
        internal static readonly Value Null = new(ValueKind.Null, 0);
        internal static readonly Value This = new(ValueKind.This, 0);

        internal readonly ValueKind Kind;
        internal readonly int Number;

        Value(ValueKind kind, int number)
        {
            Kind = kind;
            Number = number;
        }

        internal static Value Int(int number) => new(ValueKind.Int, number);

        internal bool IsInt(int number) => Kind == ValueKind.Int && Number == number;

        internal bool IsReference => Kind is ValueKind.Exception or ValueKind.Null;

        /// <summary>What brtrue sees; null for the state machine itself.</summary>
        internal bool? Truth => Kind switch
        {
            ValueKind.Exception => true,
            ValueKind.Null => false,
            ValueKind.Int => Number != 0,
            _ => null,
        };
    }

    /// <summary>
    /// The offsets of the builder calls an await suspends with (AwaitOnCompleted, AwaitUnsafeOnCompleted); null when the
    /// instructions cannot be read.
    /// </summary>
    static List<int> FindAwaits(byte[] il, Module module, Type[] typeArgs)
    {
        var result = new List<int>();
        var i = 0;
        while (i < il.Length)
        {
            var at = i;
            if (!Decode(il, at, out var op, out i)) return null;
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

    /// <summary>The instruction at <paramref name="at"/> and where its operand starts; false when it is not one.</summary>
    static bool Decode(byte[] il, int at, out OpCode op, out int operand)
    {
        operand = at;
        op = default;
        if (at >= il.Length) return false;
        if (il[at] == 0xFE)
        {
            if (at + 1 >= il.Length) return false;
            op = OpCodeTable.TwoByte[il[at + 1]];
            operand = at + 2;
        }
        else
        {
            op = OpCodeTable.OneByte[il[at]];
            operand = at + 1;
        }

        return op.Size != 0;
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
    /// The method a state machine type was made for, as the source names it: "&lt;Run&gt;d__3" gives "'Run'", a local
    /// function's "&lt;&lt;Outer&gt;g__Inner|0_0&gt;d" gives "'Inner'", a lambda's "&lt;&lt;Make&gt;b__0_0&gt;d" gives
    /// "a lambda in 'Make'" (the method it is written in, local functions included), and a lambda in a field initializer
    /// ("&lt;&lt;-cctor&gt;b__0_0&gt;d", or "-ctor") gives "a lambda in a field initializer or constructor".
    /// </summary>
    static string SourceName(string typeName)
    {
        var local = typeName.IndexOf(">g__", StringComparison.Ordinal);
        if (local >= 0)
        {
            var name = typeName[(local + 4)..];
            var bar = name.IndexOf('|', StringComparison.Ordinal);
            return "'" + (bar >= 0 ? name[..bar] : name) + "'";
        }

        var start = 0;
        while (start < typeName.Length && typeName[start] == '<') start++;
        var end = typeName.IndexOf('>', start);
        var method = end > start ? typeName[start..end] : typeName;
        if (typeName.IndexOf(">b__", StringComparison.Ordinal) < 0) return "'" + method + "'";
        return method is "-cctor" or ".cctor" or "-ctor" or ".ctor"
            ? "a lambda in a field initializer or constructor"
            : "a lambda in '" + method + "'";
    }

    /// <summary>
    /// A method the check must reject, read once: when its state machine, its body or its finally around the await is
    /// missing, this runtime does not give what the check reads.
    /// </summary>
    static class Probe
    {
        internal static readonly bool Readable = Read();

        internal static async FlowTask WithFinally()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            finally
            {
                GC.KeepAlive(typeof(Probe));
            }
        }

        static bool Read()
        {
            foreach (var t in typeof(Probe).GetNestedTypes(BindingFlags.NonPublic))
            {
                if (!typeof(IAsyncStateMachine).IsAssignableFrom(t)) continue;
                var moveNext = t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (moveNext == null) return false;
                try
                {
                    return HasCleanupAroundAwait(moveNext, t, typeof(Probe));
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            }

            return false;
        }
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
