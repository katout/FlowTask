namespace Katout.FlowTask.Analyzers.CodeFixes;

/// <summary>
/// FLOW003: a dropped FlowTask statement ('DoThing();', '_ = DoThing();' or 'DoThing().Discard();') becomes
/// 'await DoThing();', '_ = Flow.Spawn(DoThing());' (a child that stops with this scope; the '_ =' says so, FLOW008) or
/// 'FlowWorld.Current.Run(DoThing());' (independent of this flow). Offered inside FlowTask methods only: awaiting a
/// FlowTask anywhere else is FLOW005, Flow.Spawn needs a current scope, and FlowWorld.Current is set while a flow runs.
/// Catch and finally blocks get the same fixes (the awaits of a canceled scope's cleanup run to their end, and
/// Flow.Spawn starts there).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(StartFlowTaskCodeFixProvider)), Shared]
public sealed class StartFlowTaskCodeFixProvider : CodeFixProvider
{
    public const string AwaitEquivalenceKey = "FLOW003.Await";
    public const string SpawnEquivalenceKey = "FLOW003.Spawn";
    public const string RunEquivalenceKey = "FLOW003.Run";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.FlowTaskDropped);

    public override FixAllProvider GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        if (types?.Flow == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            // The unused-local form of FLOW003 is reported on an identifier and has no fix.
            var statement = CodeFixHelpers.FindExpressionStatement(root, diagnostic.Location.SourceSpan);
            if (statement == null) continue;

            var task = GetTaskExpression(statement);
            var taskType = model.GetTypeInfo(task, context.CancellationToken).Type;
            // 'a?.DoThing()' is a FlowTask? (Nullable), which can be neither awaited nor spawned as is.
            if (taskType == null || !types.IsFlowTask(taskType) ||
                taskType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) continue;

            var function = FunctionContext.Get(statement, model, types, context.CancellationToken);
            if (!function.IsFlowTaskMethod) continue;

            if (!IsInsideLock(statement, function.Node))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Resources.CodeFixAwait,
                        ct => ReplaceAsync(context.Document, statement, CodeFixHelpers.Await(task), needsUsing: false, ct),
                        equivalenceKey: AwaitEquivalenceKey),
                    diagnostic);
            }

            // '_ = Flow.Spawn(...)': a Spawn statement is FLOW008, and with a variable named '_' in scope there is no discard.
            if (CodeFixHelpers.CanDiscard(model, statement.SpanStart))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Resources.CodeFixSpawn,
                        ct => SpawnAsync(context.Document, statement, task, ct),
                        equivalenceKey: SpawnEquivalenceKey),
                    diagnostic);
            }

            if (types.FlowWorld != null)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Resources.CodeFixRun,
                        ct => RunAsync(context.Document, statement, task, ct),
                        equivalenceKey: RunEquivalenceKey),
                    diagnostic);
            }
        }
    }

    /// <summary>'DoThing()' for 'DoThing();', '_ = DoThing();' and 'DoThing().Discard();'.</summary>
    static ExpressionSyntax GetTaskExpression(ExpressionStatementSyntax statement) => statement.Expression switch
    {
        AssignmentExpressionSyntax assignment => assignment.Right,
        InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Discard" } memberAccess,
            ArgumentList.Arguments.Count: 0,
        } => memberAccess.Expression,
        _ => statement.Expression,
    };

    static bool IsInsideLock(SyntaxNode node, SyntaxNode function)
    {
        for (var n = node.Parent; n != null && n != function; n = n.Parent)
        {
            if (n.IsKind(SyntaxKind.LockStatement)) return true;
        }

        return false;
    }

    static async Task<Document> SpawnAsync(Document document, ExpressionStatementSyntax statement, ExpressionSyntax task, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        var flow = CodeFixHelpers.TypeReference(model, statement.SpanStart, types.Flow, out var needsUsing);
        var spawn = CodeFixHelpers.Invoke(flow, "Spawn", task.WithoutTrivia());
        return await ReplaceAsync(document, statement, CodeFixHelpers.Discard(spawn), needsUsing, cancellationToken).ConfigureAwait(false);
    }

    static async Task<Document> RunAsync(Document document, ExpressionStatementSyntax statement, ExpressionSyntax task, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        var current = CodeFixHelpers.CurrentWorld(model, statement.SpanStart, types.FlowWorld, out var needsUsing);
        var run = CodeFixHelpers.Invoke(current, "Run", task.WithoutTrivia());
        return await ReplaceAsync(document, statement, run, needsUsing, cancellationToken).ConfigureAwait(false);
    }

    static async Task<Document> ReplaceAsync(Document document, ExpressionStatementSyntax statement, ExpressionSyntax replacement, bool needsUsing, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var expression = statement.Expression;
        var newRoot = root.ReplaceNode(expression, replacement.WithTriviaFrom(expression));
        if (needsUsing) newRoot = CodeFixHelpers.AddFlowTaskUsing(newRoot);
        return document.WithSyntaxRoot(newRoot);
    }
}
