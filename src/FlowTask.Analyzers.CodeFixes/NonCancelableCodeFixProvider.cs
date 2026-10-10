namespace Katout.FlowTask.Analyzers.CodeFixes;

/// <summary>
/// FLOW010: 'await x' becomes 'await Flow.NonCancelable(x)', with Flow spelled as it binds (adding 'using
/// Katout.FlowTask;' when needed). No Fix All: a marked wait for input can no longer be canceled, so each await is a
/// choice.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NonCancelableCodeFixProvider)), Shared]
public sealed class NonCancelableCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.CleanupAwaitNotProtected);

    public override FixAllProvider GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in context.Diagnostics)
        {
            if (CodeFixHelpers.FindAwaitExpression(root, diagnostic.Location.SourceSpan) is not { } awaitExpression) continue;
            context.RegisterCodeFix(
                CodeAction.Create(
                    Resources.CodeFixNonCancelable,
                    ct => MarkAsync(context.Document, awaitExpression, ct),
                    equivalenceKey: nameof(NonCancelableCodeFixProvider)),
                diagnostic);
        }
    }

    static async Task<Document> MarkAsync(Document document, AwaitExpressionSyntax awaitExpression, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var flow = FlowTaskTypes.TryCreate(model.Compilation)?.Flow;
        if (flow == null) return document;

        var operand = awaitExpression.Expression;
        var flowName = CodeFixHelpers.TypeReference(model, awaitExpression.SpanStart, flow, out var needsUsing);
        var marked = CodeFixHelpers.Invoke(flowName, "NonCancelable", operand.WithoutTrivia()).WithTriviaFrom(operand);
        root = root.ReplaceNode(operand, marked);
        if (needsUsing) root = CodeFixHelpers.AddFlowTaskUsing(root);
        return document.WithSyntaxRoot(root);
    }
}
