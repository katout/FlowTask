using Microsoft.CodeAnalysis.Text;

namespace Katout.FlowTask.Analyzers;

/// <summary>
/// FLOW001: a catch clause of a FlowTask method that can take FlowCanceledException must let
/// it pass. A clause of the cancellation's own types (FlowCanceledException, OperationCanceledException) ends every path
/// with 'throw'; a catch-all ('catch', Exception, SystemException, a type parameter) excludes it with a filter,
/// since its body runs for every cancellation even when it rethrows. A clause that cannot take it is not reported: the
/// try block has no await, the type cannot hold it, an earlier clause takes it, or the filter is false for it
/// (<see cref="CatchClassifier.ReceivesCancellation"/>).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CatchCancellationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.CatchAllTakesCancellation,
        Descriptors.CancellationCatchNotRethrown,
        Descriptors.OperationCanceledCatchNotRethrown);

    public override void Initialize(AnalysisContext context)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = FlowTaskTypes.TryCreate(start.Compilation);
            if (types == null) return;
            start.RegisterSyntaxNodeAction(c => AnalyzeCatch(c, types), SyntaxKind.CatchClause);
        });
    }

    static void AnalyzeCatch(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var catchClause = (CatchClauseSyntax)context.Node;
        var model = context.SemanticModel;
        var cancellationToken = context.CancellationToken;

        // Only the body of the FlowTask method itself: a catch inside a nested synchronous lambda or local function
        // belongs to that function (an async FlowTask lambda is a FlowTask method of its own).
        var function = FunctionContext.Get(catchClause, model, types, cancellationToken);
        if (!function.IsFlowTaskMethod) return;
        if (!CatchClassifier.ReceivesCancellation(catchClause, model, types, cancellationToken)) return;

        DiagnosticDescriptor descriptor;
        if (!CatchClassifier.IsCancellationClause(catchClause, model, types, cancellationToken))
        {
            descriptor = Descriptors.CatchAllTakesCancellation;
        }
        else
        {
            if (EndsWithThrow(catchClause.Block, model)) return;
            var caught = model.GetTypeInfo(catchClause.Declaration.Type, cancellationToken).Type;
            descriptor = SymbolEqualityComparer.Default.Equals(caught, types.FlowCanceledException)
                ? Descriptors.CancellationCatchNotRethrown
                : Descriptors.OperationCanceledCatchNotRethrown;
        }

        // The diagnostic covers 'catch (Type name) when (...)', i.e. everything but the block.
        var end = catchClause.Filter?.Span.End ?? catchClause.Declaration?.Span.End ?? catchClause.CatchKeyword.Span.End;
        var span = TextSpan.FromBounds(catchClause.CatchKeyword.SpanStart, end);
        context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.Create(catchClause.SyntaxTree, span)));
    }

    /// <summary>
    /// Every path through <paramref name="block"/> ends with a throw: its end point cannot be reached, and nothing leaves
    /// it another way (return, break, continue, goto). A block the compiler cannot analyze counts as ending with a throw,
    /// so that it is not reported.
    /// </summary>
    internal static bool EndsWithThrow(BlockSyntax block, SemanticModel model)
    {
        if (block.Statements.Count == 0) return false;
        var flow = model.AnalyzeControlFlow(block);
        return flow == null || !flow.Succeeded || (!flow.EndPointIsReachable && flow.ExitPoints.IsEmpty);
    }
}
