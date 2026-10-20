using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Katout.FlowTask.Analyzers.Tests.UnwindShapes;
using Katout.FlowTask.Testing;

namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>
/// FLOW011 and FlowAssert.CanSkipUnwinding (FlowTask.Testing) give the same answer for every shape of UnwindShapes.cs:
/// the check reads the IL that this test assembly was compiled to, the analyzer reads the same file as source. Each
/// is held to the answer in the type's name (Pass_ or Reject_), so a judge that drifts from the other fails here,
/// whichever of them moved.
/// </summary>
public class SkipUnwindingParityTests
{
    const string ShapesNamespace = "Katout.FlowTask.Analyzers.Tests.UnwindShapes";

    static readonly Type[] Shapes = typeof(Shared).Assembly.GetTypes()
        .Where(t => t.Namespace == ShapesNamespace && (t.Name.StartsWith("Pass_", StringComparison.Ordinal) || t.Name.StartsWith("Reject_", StringComparison.Ordinal)))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToArray();

    static bool Expected(Type shape) => shape.Name.StartsWith("Reject_", StringComparison.Ordinal);

    [Test]
    public void TheListHasShapesOfBothAnswers()
    {
        Assert.That(Shapes.Count(Expected), Is.GreaterThanOrEqualTo(20));
        Assert.That(Shapes.Count(s => !Expected(s)), Is.GreaterThanOrEqualTo(15));
    }

    [Test]
    public async Task TheAnalyzerAndTheCheckGiveTheAnswerOfEveryShape()
    {
        var analyzer = await AnalyzerAnswers();
        var table = new StringBuilder();
        var differences = 0;
        foreach (var shape in Shapes)
        {
            var expected = Expected(shape);
            var check = CheckRejects(shape);
            var flow011 = analyzer[shape];
            if (check != expected || flow011 != expected) differences++;
            table.AppendLine($"{(check == expected && flow011 == expected ? "  " : "! ")}{CSharpName(shape),-70} expected {Answer(expected)}, check {Answer(check)}, FLOW011 {Answer(flow011)}");
        }

        Assert.That(differences, Is.Zero, table.ToString());
    }

    static string Answer(bool rejects) => rejects ? "reject" : "pass";

    /// <summary>FlowAssert.CanSkipUnwinding on the compiled type: true when it rejects it.</summary>
    static bool CheckRejects(Type shape)
    {
        try
        {
            FlowAssert.CanSkipUnwinding(shape);
            return false;
        }
        catch (FlowAssertionException)
        {
            return true;
        }
    }

    /// <summary>
    /// Compiles UnwindShapes.cs with a call of FlowWorld.SkipUnwinding for each shape, one per line, and runs the
    /// analyzers: true for a shape when FLOW011 is reported at its call.
    /// </summary>
    static async Task<Dictionary<Type, bool>> AnalyzerAnswers()
    {
        var registrations = new StringBuilder();
        registrations.AppendLine("static class Registrations");
        registrations.AppendLine("{");
        registrations.AppendLine("    static void Register(Katout.FlowTask.FlowWorld world)");
        registrations.AppendLine("    {");
        var firstLine = 4;
        foreach (var shape in Shapes) registrations.AppendLine($"        world.SkipUnwinding(typeof({CSharpName(shape)}));");
        registrations.AppendLine("    }");
        registrations.AppendLine("}");

        var compilation = AnalyzerHarness.CreateCompilation(registrations.ToString(), Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, ShapesSource());
        AnalyzerHarness.AssertCompiles(compilation);
        var diagnostics = (await AnalyzerHarness.GetFlowDiagnosticsAsync(compilation))
            .Where(d => d.Id is "FLOW011" or "FLOW012")
            .ToList();
        var tree = compilation.SyntaxTrees.First();
        Assert.That(diagnostics.Where(d => d.Id == "FLOW012" || d.Location.SourceTree != tree).Select(d => d.ToString()), Is.Empty,
            "FLOW011 at the calls only, and no FLOW012");

        var reported = diagnostics.Select(d => d.Location.GetLineSpan().StartLinePosition.Line - firstLine).ToHashSet();
        var answers = new Dictionary<Type, bool>();
        for (var i = 0; i < Shapes.Length; i++) answers[Shapes[i]] = reported.Contains(i);
        return answers;
    }

    /// <summary>
    /// UnwindShapes.cs as the analyzer tests parse it (C# 9, Roslyn 3.8): its file-scoped namespace as a block, and the
    /// global usings of this project that it relies on.
    /// </summary>
    static string ShapesSource()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FlowTask.slnx"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null, "repository root (FlowTask.slnx) not found above the test directory");
        var text = File.ReadAllText(Path.Combine(directory.FullName, "tests", "FlowTask.Analyzers.Tests", "UnwindShapes.cs"));
        var declaration = "namespace " + ShapesNamespace + ";";
        Assert.That(text, Does.Contain(declaration));
        return text.Replace(declaration, "namespace " + ShapesNamespace + " {", StringComparison.Ordinal) + "\n}\n";
    }

    /// <summary>The name of a shape in C#: nested types through their outer type, generic definitions as 'G&lt;&gt;'.</summary>
    static string CSharpName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick] + "<" + new string(',', int.Parse(name[(tick + 1)..], System.Globalization.CultureInfo.InvariantCulture) - 1) + ">";
        return type.IsNested ? CSharpName(type.DeclaringType) + "." + name : "global::" + type.Namespace + "." + name;
    }
}
