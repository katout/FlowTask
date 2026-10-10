using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace Katout.FlowTask.Analyzers.Tests.Harness;

/// <summary>Applies a code fix in an AdhocWorkspace and compares the resulting text.</summary>
public static class CodeFixHarness
{
    /// <summary>
    /// Runs the analyzers on <paramref name="before"/>, applies the code action of <paramref name="provider"/> for
    /// the diagnostic with <paramref name="diagnosticId"/> (the <paramref name="diagnosticIndex"/>-th, by position),
    /// and asserts that the document becomes <paramref name="after"/> and still compiles, and that the fixed code has no
    /// diagnostic with an ID in <paramref name="absentAfter"/>.
    /// </summary>
    public static async Task VerifyAsync(
        string before,
        string after,
        CodeFixProvider provider,
        string diagnosticId,
        string equivalenceKey = null,
        int diagnosticIndex = 0,
        string[] absentAfter = null)
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, before);
        var actions = await GetActionsAsync(document, provider, diagnosticId, diagnosticIndex);
        var action = equivalenceKey == null
            ? actions.Single()
            : actions.Single(a => a.EquivalenceKey == equivalenceKey);

        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetDocument(document.Id);
        var text = (await changed.GetTextAsync()).ToString();
        Assert.That(Normalize(text), Is.EqualTo(Normalize(after)));

        var compilation = await changed.Project.GetCompilationAsync();
        AnalyzerHarness.AssertCompiles(compilation);
        if (absentAfter == null) return;
        var left = (await AnalyzerHarness.GetFlowDiagnosticsAsync(compilation)).Where(d => absentAfter.Contains(d.Id)).ToList();
        Assert.That(left, Is.Empty, "diagnostics in the fixed code: " + string.Join("; ", left.Select(d => d.Id + " " + d.GetMessage())));
    }

    /// <summary>The code actions <paramref name="provider"/> registers for the diagnostic (may be empty).</summary>
    public static async Task<IReadOnlyList<CodeAction>> GetActionsAsync(string source, CodeFixProvider provider, string diagnosticId, int diagnosticIndex = 0)
    {
        using var workspace = new AdhocWorkspace();
        return await GetActionsAsync(CreateDocument(workspace, source), provider, diagnosticId, diagnosticIndex);
    }

    static async Task<IReadOnlyList<CodeAction>> GetActionsAsync(Document document, CodeFixProvider provider, string diagnosticId, int diagnosticIndex)
    {
        Assert.That(provider.FixableDiagnosticIds, Does.Contain(diagnosticId));
        var compilation = await document.Project.GetCompilationAsync();
        AnalyzerHarness.AssertCompiles(compilation);
        var diagnostics = (await AnalyzerHarness.GetFlowDiagnosticsAsync(compilation)).Where(d => d.Id == diagnosticId).ToList();
        Assert.That(diagnostics.Count, Is.GreaterThan(diagnosticIndex), $"expected a {diagnosticId} diagnostic to fix");

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostics[diagnosticIndex], (a, _) => actions.Add(a), CancellationToken.None);
        await provider.RegisterCodeFixesAsync(context);
        return actions;
    }

    static Document CreateDocument(AdhocWorkspace workspace, string source)
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestAssembly", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, AnalyzerHarness.CompilationOptions)
            .WithProjectParseOptions(projectId, AnalyzerHarness.ParseOptions)
            .AddMetadataReferences(projectId, AnalyzerHarness.References)
            .AddDocument(documentId, "Test.cs", SourceText.From(source));
        return solution.GetDocument(documentId);
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n");
}
