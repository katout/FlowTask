using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Katout.FlowTask.Analyzers.Tests.Harness;

/// <summary>
/// Compiles C# snippets against FlowTask and the running .NET framework, runs every FlowTask analyzer and compares
/// the FLOW diagnostics with the markup.
/// </summary>
public static class AnalyzerHarness
{
    /// <summary>
    /// C# 9: the language version Unity compiles its users' scripts with (FlowTask's own packages raise theirs to C# 10
    /// with a csc.rsp; the users' code keeps the default).
    /// </summary>
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp9);

    public static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable);

    public static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    public static readonly ImmutableArray<DiagnosticAnalyzer> Analyzers = typeof(AwaitAnalyzer).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t) &&
                    t.GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), false).Length > 0)
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t))
        .ToImmutableArray();

    static ImmutableArray<MetadataReference> BuildReferences()
    {
        // The framework assemblies of the running runtime (net10.0 test host) plus FlowTask.dll.
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
        var references = trusted
            .Where(p => p.Length > 0 && string.Equals(Path.GetDirectoryName(p), frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) ||
                        Path.GetFileName(p) == "mscorlib.dll" || Path.GetFileName(p) == "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(FlowTask).Assembly.Location));
        return references.ToImmutableArray();
    }

    public static CSharpCompilation CreateCompilation(string source, OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary, params string[] otherFiles) =>
        CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source, ParseOptions, "Test.cs") }
                .Concat(otherFiles.Select((text, i) => CSharpSyntaxTree.ParseText(text, ParseOptions, $"Other{i}.cs"))),
            References,
            CompilationOptions.WithOutputKind(outputKind));

    /// <summary>FLOW diagnostics for <paramref name="source"/>, sorted by position (suppressed ones excluded).</summary>
    public static async Task<ImmutableArray<Diagnostic>> GetFlowDiagnosticsAsync(Compilation compilation)
    {
        var exceptions = new List<string>();
        var withAnalyzers = compilation.WithAnalyzers(Analyzers, new CompilationWithAnalyzersOptions(
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
            onAnalyzerException: (e, analyzer, _) =>
            {
                lock (exceptions) exceptions.Add(analyzer.GetType().Name + ": " + e);
            },
            concurrentAnalysis: false,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        // Analyzer crashes (AD0001) must fail the test rather than silently report nothing.
        var crashes = diagnostics.Where(d => d.Id == "AD0001").Select(d => d.GetMessage()).Concat(exceptions).ToList();
        if (crashes.Count > 0) Assert.Fail("Analyzer threw:\n" + string.Join("\n", crashes));

        return diagnostics
            .Where(d => d.Id.StartsWith("FLOW", StringComparison.Ordinal))
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>
    /// Asserts that the FLOW diagnostics of the marked-up source are exactly the marked ones
    /// (<c>{|FLOW003:text|}</c>), and that the source compiles.
    /// </summary>
    public static async Task VerifyAsync(string markup, OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary) =>
        await VerifyDiagnosticsAsync(markup, outputKind, null);

    /// <summary>
    /// Like <see cref="VerifyAsync"/>, but compares only the diagnostics whose id is in <paramref name="ids"/> (so that a
    /// test of one rule does not depend on how another rule treats the same code), and compiles
    /// <paramref name="otherFiles"/> (without markup) into the same assembly.
    /// </summary>
    public static async Task VerifyOnlyAsync(string markup, string[] ids, params string[] otherFiles) =>
        await VerifyDiagnosticsAsync(markup, OutputKind.DynamicallyLinkedLibrary, ids, otherFiles);

    /// <summary>
    /// Like <see cref="VerifyAsync"/>, and also asserts that the message of the i-th diagnostic (by position) contains
    /// <paramref name="messageFragments"/>[i]. Pass one fragment per expected diagnostic.
    /// </summary>
    public static async Task VerifyMessagesAsync(string markup, params string[] messageFragments)
    {
        var actual = await VerifyDiagnosticsAsync(markup, OutputKind.DynamicallyLinkedLibrary, null);
        Assert.That(actual.Length, Is.EqualTo(messageFragments.Length), "one message fragment per diagnostic");
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.That(actual[i].GetMessage(System.Globalization.CultureInfo.InvariantCulture), Does.Contain(messageFragments[i]), $"diagnostic #{i} ({actual[i].Id})");
        }
    }

    static async Task<ImmutableArray<Diagnostic>> VerifyDiagnosticsAsync(string markup, OutputKind outputKind, string[] ids, params string[] otherFiles)
    {
        var source = Markup.Parse(markup, out var expected);
        var compilation = CreateCompilation(source, outputKind, otherFiles);
        AssertCompiles(compilation);

        var actual = await GetFlowDiagnosticsAsync(compilation);
        actual = actual.Where(d => d.Location.SourceTree == compilation.SyntaxTrees.First()).ToImmutableArray();
        if (ids != null)
        {
            actual = actual.Where(d => ids.Contains(d.Id)).ToImmutableArray();
            expected = expected.Where(e => ids.Contains(e.Id)).ToList();
        }

        var actualSet = actual.Select(d => new ExpectedDiagnostic(d.Id, d.Location.SourceSpan)).ToList();

        var missing = expected.Where(e => !actualSet.Contains(e)).ToList();
        var unexpected = actual.Where(d => !expected.Contains(new ExpectedDiagnostic(d.Id, d.Location.SourceSpan))).ToList();
        if (missing.Count == 0 && unexpected.Count == 0) return actual;

        var message = new StringBuilder();
        foreach (var m in missing) message.AppendLine($"Missing   {m.Id} at {Describe(source, m.Span)}");
        foreach (var u in unexpected) message.AppendLine($"Unexpected {u.Id} at {Describe(source, u.Location.SourceSpan)}: {u.GetMessage()}");
        message.AppendLine("All actual diagnostics:");
        foreach (var d in actual) message.AppendLine($"  {d.Id} at {Describe(source, d.Location.SourceSpan)}: {d.GetMessage()}");
        Assert.Fail(message.ToString());
        return actual;
    }

    public static void AssertCompiles(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) Assert.Fail("Test source does not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    static string Describe(string source, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < span.Start && i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        var text = span.End <= source.Length ? source.Substring(span.Start, span.Length) : "<out of range>";
        return $"({line},{column}) \"{text}\"";
    }
}
