namespace Katout.FlowTask.Analyzers;

/// <summary>
/// FLOW010: an await of a FlowTask awaitable in a finally block, or in a catch clause that passes its exception on, of a
/// FlowTask method, not marked with Flow.NonCancelable. A block entered before the scope was canceled (by a return, the
/// end of the try block or an exception) is not cleanup yet: a cancel that reaches such an await throws
/// FlowCanceledException, which skips the rest of the block and replaces the exception the block was carrying.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CleanupAwaitAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Descriptors.CleanupAwaitNotProtected);

    public override void Initialize(AnalysisContext context)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = FlowTaskTypes.TryCreate(start.Compilation);
            // A FlowTask without Flow.NonCancelable offers no fix to point to.
            if (types?.Flow == null || types.Flow.GetMembers(NonCancelable).IsEmpty) return;
            start.RegisterSyntaxNodeAction(c => Analyze(c, types), SyntaxKind.AwaitExpression);
        });
    }

    const string NonCancelable = "NonCancelable";

    static void Analyze(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var awaitExpression = (AwaitExpressionSyntax)context.Node;
        var model = context.SemanticModel;
        var function = FunctionContext.Get(awaitExpression, model, types, context.CancellationToken);
        if (!function.IsFlowTaskMethod || !IsInBlockEnteredBeforeCancel(awaitExpression, function.Node, model, types, context.CancellationToken)) return;
        if (!types.AwaitsThroughFlowAwaiter(awaitExpression, model, context.CancellationToken)) return;
        if (IsMarked(awaitExpression.Expression, model, types, context.CancellationToken)) return;
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.CleanupAwaitNotProtected, awaitExpression.GetLocation()));
    }

    /// <summary>
    /// In a finally block, or in a catch clause that the cancellation cannot enter and that passes its exception on (a
    /// throw), of <paramref name="function"/>. A catch of FlowCanceledException is entered by the cancellation, after
    /// which the awaits of the scope's catch and finally blocks run to their end (a catch-all that takes it is FLOW001's);
    /// a catch that handles its exception can be stopped by a cancel without losing anything.
    /// </summary>
    static bool IsInBlockEnteredBeforeCancel(SyntaxNode node, SyntaxNode function, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        for (var n = node.Parent; n != null && n != function; n = n.Parent)
        {
            switch (n)
            {
                case FinallyClauseSyntax _:
                    return true;
                case CatchClauseSyntax clause when Throws(clause.Block) &&
                                                   !CatchClassifier.IsCancellationClause(clause, model, types, cancellationToken) &&
                                                   !CatchClassifier.ReceivesCancellation(clause, model, types, cancellationToken):
                    return true;
            }
        }

        return false;
    }

    static bool Throws(BlockSyntax block)
    {
        foreach (var n in block.DescendantNodes(d => d is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
        {
            if (n is ThrowStatementSyntax or ThrowExpressionSyntax) return true;
        }

        return false;
    }

    /// <summary>'Flow.NonCancelable(x)', or a local initialized with it.</summary>
    static bool IsMarked(ExpressionSyntax expression, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
        if (expression is InvocationExpressionSyntax invocation)
        {
            return types.IsFlowMethod(model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol, NonCancelable);
        }

        if (model.GetSymbolInfo(expression, cancellationToken).Symbol is not ILocalSymbol local) return false;
        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer.Value: { } value } &&
                IsMarked(value, model, types, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
