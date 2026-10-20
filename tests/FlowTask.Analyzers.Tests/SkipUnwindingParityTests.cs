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

    /// <summary>
    /// The shapes of UnwindShapesCSharp14.cs (C# 14, which Roslyn 3.8 cannot parse): the analyzer runs in the compiler of
    /// the .NET SDK (csc), as it does in a user's build, and the check reads the IL of this test assembly.
    /// </summary>
    [Test]
    public void TheAnalyzerInTheCompilerOfTheSdkAndTheCheckGiveTheAnswerOfEveryCSharp14Shape()
    {
        var shapes = typeof(Shared).Assembly.GetTypes()
            .Where(t => t.Namespace == ShapesNamespace + "CSharp14" && (t.Name.StartsWith("Pass_", StringComparison.Ordinal) || t.Name.StartsWith("Reject_", StringComparison.Ordinal)))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.That(shapes.Count(Expected), Is.GreaterThanOrEqualTo(2));
        Assert.That(shapes.Count(s => !Expected(s)), Is.GreaterThanOrEqualTo(1));

        var registrations = new StringBuilder();
        registrations.AppendLine("static class Registrations");
        registrations.AppendLine("{");
        registrations.AppendLine("    static void Register(Katout.FlowTask.FlowWorld world)");
        registrations.AppendLine("    {");
        var firstLine = 5;
        foreach (var shape in shapes) registrations.AppendLine($"        world.SkipUnwinding(typeof({CSharpName(shape)}));");
        registrations.AppendLine("    }");
        registrations.AppendLine("}");
        var reported = SdkCompilerDiagnostics(Path.Combine(RepositoryRoot(), "tests", "FlowTask.Analyzers.Tests", "UnwindShapesCSharp14.cs"), registrations.ToString());
        Assert.That(reported.Where(d => d.Id == "FLOW012"), Is.Empty, "no FLOW012");

        var table = new StringBuilder();
        var differences = 0;
        foreach (var (shape, i) in shapes.Select((s, i) => (s, i)))
        {
            var expected = Expected(shape);
            var check = CheckRejects(shape);
            var flow011 = reported.Any(d => d.Id == "FLOW011" && d.Line == firstLine + i);
            if (check != expected || flow011 != expected) differences++;
            table.AppendLine($"{(check == expected && flow011 == expected ? "  " : "! ")}{CSharpName(shape),-70} expected {Answer(expected)}, check {Answer(check)}, FLOW011 {Answer(flow011)}");
        }

        Assert.That(differences, Is.Zero, table.ToString());
    }

    /// <summary>
    /// Compiles <paramref name="shapesFile"/> and <paramref name="registrations"/> with the csc of the newest .NET SDK
    /// next to the running runtime and the FlowTask analyzers: its FLOW011 and FLOW012, by line (1-based).
    /// </summary>
    static List<(string Id, int Line)> SdkCompilerDiagnostics(string shapesFile, string registrations)
    {
        // The runtime is in <dotnet>/shared/Microsoft.NETCore.App/<version>/; the SDKs are in <dotnet>/sdk/<version>/.
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        var csc = Directory.GetDirectories(Path.Combine(dotnetRoot, "sdk"))
            .Select(d => Path.Combine(d, "Roslyn", "bincore", "csc.dll"))
            .Where(File.Exists)
            .OrderByDescending(p => SdkVersion(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(p))))))
            .FirstOrDefault();
        Assert.That(csc, Is.Not.Null, "no csc.dll under " + Path.Combine(dotnetRoot, "sdk"));
        var dotnet = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        var work = Path.Combine(Path.GetTempPath(), "FlowTask.Analyzers.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // The analyzer alone in its folder: the compiler gives it its own Roslyn, not the 3.8 of this test's output.
            var analyzer = Path.Combine(work, "FlowTask.Analyzers.dll");
            File.Copy(typeof(SkipUnwindingAnalyzer).Assembly.Location, analyzer);
            var registrationsFile = Path.Combine(work, "Registrations.cs");
            File.WriteAllText(registrationsFile, registrations);
            var arguments = new StringBuilder();
            arguments.AppendLine("/nologo /noconfig /nostdlib+ /target:library /langversion:14 /nullable:disable /preferreduilang:en /warn:4");
            arguments.AppendLine($"/out:\"{Path.Combine(work, "Shapes.dll")}\"");
            arguments.AppendLine($"/analyzer:\"{analyzer}\"");
            foreach (var reference in AnalyzerHarness.References.OfType<Microsoft.CodeAnalysis.PortableExecutableReference>()) arguments.AppendLine($"/reference:\"{reference.FilePath}\"");
            arguments.AppendLine($"\"{shapesFile}\"");
            arguments.AppendLine($"\"{registrationsFile}\"");
            var responseFile = Path.Combine(work, "csc.rsp");
            File.WriteAllText(responseFile, arguments.ToString());

            var start = new System.Diagnostics.ProcessStartInfo(dotnet)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(csc);
            start.ArgumentList.Add("@" + responseFile);
            start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            using var process = System.Diagnostics.Process.Start(start);
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                process.Kill();
                Assert.Fail("csc did not end in 60 seconds");
            }

            output += error.Result;
            var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            Assert.That(lines.Where(l => l.Contains(": error CS", StringComparison.Ordinal)), Is.Empty, output);
            var diagnostics = new List<(string Id, int Line)>();
            foreach (var line in lines)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^(?<file>.+)\((?<line>\d+),\d+\): (error|warning) (?<id>FLOW01[12]):");
                if (!match.Success) continue;
                Assert.That(Path.GetFileName(match.Groups["file"].Value), Is.EqualTo("Registrations.cs"), "FLOW011 or FLOW012 outside the calls: " + line);
                diagnostics.Add((match.Groups["id"].Value, int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }

            return diagnostics;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>"10.0.402" of an SDK folder (a preview's suffix dropped); 0.0 when the name is not a version.</summary>
    static Version SdkVersion(string name)
    {
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return Version.TryParse(dash >= 0 ? name[..dash] : name, out var version) ? version : new Version(0, 0);
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FlowTask.slnx"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null, "repository root (FlowTask.slnx) not found above the test directory");
        return directory.FullName;
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
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "FlowTask.Analyzers.Tests", "UnwindShapes.cs"));
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
