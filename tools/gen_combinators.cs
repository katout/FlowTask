// Generates the typed Race / WhenAll overloads for 2..3 branches: src/FlowTask/Combinators.g.cs (the
// overloads and RaceResult) and src/FlowTask/Internal/CombinatorNodes.g.cs (the nodes behind them), one file per
// namespace.
//
// Every branch may be a FlowTask (no result, treated as FlowUnit) or a FlowTask<T>, so each arity n gets 2^n overloads.
// The arity stops at 3 on purpose: the overload count doubles per branch, and every instantiation is AOT code
// under IL2CPP. Four or more branches use the list overloads in FlowTask.cs.
//
// Usage:
//   dotnet tools/gen_combinators.cs            # rewrite both files
//   dotnet tools/gen_combinators.cs --check    # exit 1 if either file differs from the generator's output (CI)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal static class GenCombinators
{
    const int Max = 3;
    static readonly string Root = Path.GetFullPath(Path.Combine(ScriptDirectory(), ".."));
    static readonly string OverloadsOut = Path.Combine(Root, "src", "FlowTask", "Combinators.g.cs");
    static readonly string NodesOut = Path.Combine(Root, "src", "FlowTask", "Internal", "CombinatorNodes.g.cs");

    // The XML comments of the generated factories: the first overload of each group carries them, and the others inherit
    // them (<inheritdoc>), so the file does not repeat one text a dozen times. The list overloads in FlowTask.cs say the
    // same by hand.
    static readonly string[] RaceXml =
    {
        "<summary>",
        "Returns the first branch to complete, with its index and value; the other branches are canceled and unwound (their",
        "finally blocks run to their end) before the caller resumes. A branch that throws first ends the Race as in WhenAll.",
        "</summary>",
        "<remarks>",
        "The branches start in argument order, each up to its first wait, and one that completes as it starts wins at once.",
        "Branches that complete in the same flush win in the order they are processed (time waits satisfied by one Tick, in the",
        "order they began), so put an event before the work it interrupts: <c>Race(hits.Next(), Patrol())</c>. A value that a",
        "losing wait on a <see cref=\"Subscription{T}\"/> already received goes back to the subscription. For a timeout, race the",
        "work against <see cref=\"WaitForSeconds\"/>.",
        "</remarks>",
    };

    static readonly string[] WhenAllXml =
    {
        "<summary>",
        "Waits for every branch. With a branch that has a value, the result is a tuple of the branches' values in order",
        "(<see cref=\"FlowUnit\"/> for a branch without one); with none, it is a FlowTask. When one throws, the others are",
        "canceled and unwound before the exception is thrown at the await (Task.WhenAll lets them run on).",
        "</summary>",
    };

    const string RaceInherit = "<inheritdoc cref=\"Race(FlowTask, FlowTask, string, int)\"/>";
    const string WhenAllInherit = "<inheritdoc cref=\"WhenAll(FlowTask, FlowTask, string, int)\"/>";

    // The caller's file and line, for diagnostics (FlowScopeInfo.WaitingFile), as on the list overloads in FlowTask.cs.
    const string CallerParams = ", [CallerFilePath] string callerFilePath = \"\", [CallerLineNumber] int callerLineNumber = 0";

    static int Main(string[] args)
    {
        var check = false;
        foreach (var a in args)
        {
            if (a == "--check")
                check = true;
            else
                return Usage($"unknown argument: {a}");
        }
        var stale = false;
        foreach (var (path, text) in new[] { (OverloadsOut, GenerateOverloads()), (NodesOut, GenerateNodes()) })
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            var rel = Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!check)
            {
                File.WriteAllBytes(path, bytes);
                Console.WriteLine($"wrote {rel}");
            }
            else if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                Console.WriteLine($"OK: {rel} is up to date");
            }
            else
            {
                Console.WriteLine($"stale: {rel} differs from the generator's output; run dotnet tools/gen_combinators.cs");
                stale = true;
            }
        }
        return stale ? 1 : 0;
    }

    static int Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine("usage: dotnet tools/gen_combinators.cs [--check]");
        return 2;
    }

    /// <summary>Type parameter names. Race counts from 0, like RaceResult.Index, TryGet0 and Value0; WhenAll counts from 1,
    /// like the Item1..ItemN of the tuple it returns.</summary>
    static string[] TypeParams(int n, int first) => Enumerable.Range(first, n).Select(i => $"T{i}").ToArray();

    static string GenResult(int n)
    {
        var ts = TypeParams(n, 0);
        var tl = string.Join(", ", ts);
        var lines = new List<string>
        {
            $"/// <summary>Winner of a {n}-branch race: <see cref=\"Index\"/> is the 0-based branch that completed first.</summary>",
            $"public readonly struct RaceResult<{tl}>",
            "{",
        };
        for (var i = 0; i < n; i++)
            lines.Add($"    readonly {ts[i]} _v{i};");
        lines.Add("");
        var parameters = string.Join(", ", ts.Select((t, i) => $"{t} v{i}"));
        lines.Add($"    internal RaceResult(int index, {parameters})");
        lines.Add("    {");
        lines.Add("        Index = index;");
        for (var i = 0; i < n; i++)
            lines.Add($"        _v{i} = v{i};");
        lines.Add("    }");
        lines.Add("");
        lines.Add("    /// <summary>0-based index of the winning branch.</summary>");
        lines.Add("    public int Index { get; }");
        for (var i = 0; i < n; i++)
        {
            lines.Add("");
            lines.Add($"    /// <summary>True when branch {i} won; its value is returned in <paramref name=\"value\"/>.</summary>");
            lines.Add($"    public bool TryGet{i}(out {ts[i]} value)");
            lines.Add("    {");
            lines.Add($"        value = _v{i};");
            lines.Add($"        return Index == {i};");
            lines.Add("    }");
            lines.Add("");
            lines.Add($"    /// <summary>Value of branch {i}. Throws when another branch won.</summary>");
            lines.Add($"    public {ts[i]} Value{i} => Index == {i} ? _v{i} : throw new System.InvalidOperationException($\"Branch {i} did not win (winner: {{Index}}).\");");
        }
        lines.Add("");
        lines.Add("    /// <summary><c>Race[index] = value</c> of the winner.</summary>");
        lines.Add("    public override string ToString() => Index switch");
        lines.Add("    {");
        for (var i = 0; i < n; i++)
            lines.Add($"        {i} => $\"Race[{i}] = {{_v{i}}}\",");
        lines.Add("        _ => \"Race[?]\",");
        lines.Add("    };");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    static string GenNodes(int n)
    {
        var ts = TypeParams(n, 0);
        var tl = string.Join(", ", ts);
        var rr = $"RaceResult<{tl}>";
        var lines = new List<string>
        {
            // Race node
            $"internal sealed class RaceNode<{tl}> : RaceNodeBase<{rr}>",
            "{",
            $"    static NodePool<RaceNode<{tl}>> s_pool;",
            "",
            $"    internal static RaceNode<{tl}> Rent()",
            "    {",
            $"        var n = s_pool.Rent() ?? new RaceNode<{tl}>();",
            "        n.InitUnstarted();",
            "        return n;",
            "    }",
            "",
            "    internal override string Name => \"Race\";",
            "",
        };
        var values = string.Join(", ", ts.Select((t, i) => $"index == {i} ? ((FlowNode<{t}>)Branches[{i}].Node).Result : default"));
        lines.Add($"    protected override {rr} BuildResult(int index) => new {rr}(index, {values});");
        lines.Add("");
        lines.Add("    protected override void ReturnToPool() => s_pool.Return(this);");
        lines.Add("}");
        lines.Add("");
        // WhenAll node
        ts = TypeParams(n, 1);
        tl = string.Join(", ", ts);
        var tuple = $"({tl})";
        lines.Add($"internal sealed class WhenAllNode<{tl}> : WhenAllNodeBase<{tuple}>");
        lines.Add("{");
        lines.Add($"    static NodePool<WhenAllNode<{tl}>> s_pool;");
        lines.Add("");
        lines.Add($"    internal static WhenAllNode<{tl}> Rent()");
        lines.Add("    {");
        lines.Add($"        var n = s_pool.Rent() ?? new WhenAllNode<{tl}>();");
        lines.Add("        n.InitUnstarted();");
        lines.Add("        return n;");
        lines.Add("    }");
        lines.Add("");
        lines.Add("    internal override string Name => \"WhenAll\";");
        lines.Add("");
        values = string.Join(", ", ts.Select((t, i) => $"((FlowNode<{t}>)Branches[{i}].Node).Result"));
        lines.Add($"    protected override {tuple} BuildResult() => ({values});");
        lines.Add("");
        lines.Add("    protected override void ReturnToPool() => s_pool.Return(this);");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    /// <summary>Type parameter list, parameter list (ending with the caller's file and line), branch result types, argument
    /// checks, and the lines that record the place and add the branches, with names counted from first. The checks come before the node is rented, so a rejected argument (default, already started)
    /// leaves no unstarted combinator behind.</summary>
    static (string TypeDecl, string Params, string Results, string Checks, string Adds) GenSignature(int n, bool[] mask, int first)
    {
        var generic = Enumerable.Range(0, n).Where(i => mask[i]).Select(i => $"T{i + first}").ToArray();
        var typeDecl = generic.Length > 0 ? $"<{string.Join(", ", generic)}>" : "";
        var parameters = string.Join(", ", Enumerable.Range(0, n).Select(i => mask[i] ? $"FlowTask<T{i + first}> t{i + first}" : $"FlowTask t{i + first}")) + CallerParams;
        var results = string.Join(", ", Enumerable.Range(0, n).Select(i => mask[i] ? $"T{i + first}" : "FlowUnit"));
        var checks = string.Join("\n", Enumerable.Range(0, n).Select(i => $"        Flow.CheckStartable{(mask[i] ? "" : "<FlowUnit>")}(t{i + first});"));
        var adds = "        n.SetSite(callerFilePath, callerLineNumber);\n" + string.Join("\n", Enumerable.Range(0, n).Select(i => $"        n.AddBranch(Flow.Materialize{(mask[i] ? "" : "<FlowUnit>")}(t{i + first}));"));
        return (typeDecl, parameters, results, checks, adds);
    }

    /// <summary>Every combination of FlowTask (false) and FlowTask&lt;T&gt; (true) for n branches, the last branch changing
    /// fastest.</summary>
    static IEnumerable<bool[]> Masks(int n) =>
        Enumerable.Range(0, 1 << n).Select(m => Enumerable.Range(0, n).Select(i => ((m >> (n - 1 - i)) & 1) == 1).ToArray());

    static string GenOverloads(int n)
    {
        var lines = new List<string>();
        foreach (var mask in Masks(n))
        {
            var (typeDecl, parameters, results, checks, adds) = GenSignature(n, mask, 0);
            // Race
            var first = n == 2 && !mask.Any(m => m);
            lines.AddRange((first ? RaceXml : new[] { RaceInherit }).Select(line => "    /// " + line));
            lines.Add($"    public static FlowTask<RaceResult<{results}>> Race{typeDecl}({parameters})");
            lines.Add("    {");
            lines.Add(checks);
            lines.Add($"        var n = RaceNode<{results}>.Rent();");
            lines.Add(adds);
            lines.Add($"        return new FlowTask<RaceResult<{results}>>(n, n.Token);");
            lines.Add("    }");
            lines.Add("");
            // WhenAll
            (typeDecl, parameters, results, checks, adds) = GenSignature(n, mask, 1);
            lines.AddRange((first ? WhenAllXml : new[] { WhenAllInherit }).Select(line => "    /// " + line));
            if (!mask.Any(m => m))
            {
                lines.Add($"    public static FlowTask WhenAll({parameters})");
                lines.Add("    {");
                lines.Add(checks);
                lines.Add($"        var n = WhenAllVoidNode.Rent({n});");
                lines.Add(adds);
                lines.Add("        return new FlowTask(n, n.Token);");
                lines.Add("    }");
            }
            else
            {
                lines.Add($"    public static FlowTask<({results})> WhenAll{typeDecl}({parameters})");
                lines.Add("    {");
                lines.Add(checks);
                lines.Add($"        var n = WhenAllNode<{results}>.Rent();");
                lines.Add(adds);
                lines.Add($"        return new FlowTask<({results})>(n, n.Token);");
                lines.Add("    }");
            }
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    const string Header = "// <auto-generated> by tools/gen_combinators.cs. Do not edit by hand. </auto-generated>";

    static string GenerateOverloads()
    {
        var parts = new List<string>
        {
            Header,
            "namespace Katout.FlowTask;",
            "",
            "public readonly partial struct FlowTask",
            "{",
        };
        for (var n = 2; n <= Max; n++)
            parts.Add(GenOverloads(n));
        parts.Add("}");
        for (var n = 2; n <= Max; n++)
        {
            parts.Add("");
            parts.Add(GenResult(n));
        }
        return string.Join("\n", parts) + "\n";
    }

    static string GenerateNodes()
    {
        var parts = new List<string> { Header, "namespace Katout.FlowTask.Internal;" };
        for (var n = 2; n <= Max; n++)
        {
            parts.Add("");
            parts.Add(GenNodes(n));
        }
        return string.Join("\n", parts) + "\n";
    }

    static string ScriptDirectory() => AppContext.GetData("EntryPointFileDirectoryPath") as string
        ?? throw new InvalidOperationException("Run this file as a .NET file-based app: dotnet tools/gen_combinators.cs");
}
