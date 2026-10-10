using System.Collections.Concurrent;

namespace Katout.FlowTask.Internal;

/// <summary>Names derived from a compiler-generated state machine type, computed once per type on first use (diagnostics only).</summary>
internal static class StateMachineNames<TStateMachine>
{
    static string s_name;
    static string s_methodName;
    static Type s_declaringType;

    internal static string Name => s_name ??= StateMachineNameParser.Parse(typeof(TStateMachine).Name, lambdaAsMethod: false);

    internal static string MethodName => s_methodName ??= StateMachineNameParser.Parse(typeof(TStateMachine).Name, lambdaAsMethod: true);

    internal static Type DeclaringType => s_declaringType ??= StateMachineNameParser.UserDeclaringType(typeof(TStateMachine));
}

internal static class StateMachineNameParser
{
    static readonly ConcurrentDictionary<string, string> s_names = new();

    /// <summary>
    /// "&lt;EnemyAI&gt;d__3" gives "EnemyAI", "&lt;&lt;Test&gt;g__Inner|0_0&gt;d" gives "Inner", and a lambda "Outer.lambda", or
    /// "Outer" with <paramref name="lambdaAsMethod"/> (the method a tool can find in the source).
    /// </summary>
    internal static string Parse(string typeName, bool lambdaAsMethod) =>
        lambdaAsMethod ? ParseCore(typeName, true) : s_names.GetOrAdd(typeName, static n => ParseCore(n, false));

    /// <summary>The type that declares the method: the state machine is nested in it, or in a closure class ("&lt;&gt;c") nested there.</summary>
    internal static Type UserDeclaringType(Type stateMachine)
    {
        var t = stateMachine.DeclaringType;
        while (t != null && t.Name.Length > 0 && t.Name[0] == '<') t = t.DeclaringType;
        return t;
    }

    static string ParseCore(string n, bool lambdaAsMethod)
    {
        if (string.IsNullOrEmpty(n) || n[0] != '<') return n;
        var depth = 0;
        var end = -1;
        for (var i = 0; i < n.Length && end < 0; i++)
        {
            if (n[i] == '<') depth++;
            else if (n[i] == '>' && --depth == 0) end = i;
        }

        if (end < 0) return n;
        var inner = n[1..end];
        if (inner.Length == 0 || inner[0] != '<') return inner;
        var close = inner.IndexOf('>', StringComparison.Ordinal);
        var outer = close > 1 ? inner[1..close] : inner;
        var local = inner.IndexOf(">g__", StringComparison.Ordinal);
        if (local >= 0)
        {
            var name = inner[(local + 4)..];
            var bar = name.IndexOf('|', StringComparison.Ordinal);
            return bar >= 0 ? name[..bar] : name;
        }

        return !lambdaAsMethod && inner.Contains(">b__", StringComparison.Ordinal) ? outer + ".lambda" : outer;
    }
}
