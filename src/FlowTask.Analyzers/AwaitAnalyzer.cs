using Microsoft.CodeAnalysis.Text;

namespace Katout.FlowTask.Analyzers;

/// <summary>
/// Rules about awaits:
/// FLOW002 (foreign awaitable in a FlowTask method),
/// FLOW005 (FlowTask awaitable outside a FlowTask method).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AwaitAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.ForeignAwaitTaskLike,
        Descriptors.ForeignAwaitWithAsFlow,
        Descriptors.ForeignAwaitYield,
        Descriptors.ForeignAwaitOther,
        Descriptors.ForeignAwaitForEach,
        Descriptors.ForeignAwaitUsing,
        Descriptors.FlowAwaitOutsideFlowTask);

    public override void Initialize(AnalysisContext context)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = FlowTaskTypes.TryCreate(start.Compilation);
            if (types == null) return;
            start.RegisterSyntaxNodeAction(c => AnalyzeAwait(c, types), SyntaxKind.AwaitExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeAwaitForEach(c, types), SyntaxKind.ForEachStatement, SyntaxKind.ForEachVariableStatement);
            start.RegisterSyntaxNodeAction(c => AnalyzeAwaitUsing(c, types), SyntaxKind.LocalDeclarationStatement, SyntaxKind.UsingStatement);
        });
    }

    static void AnalyzeAwait(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var awaitExpression = (AwaitExpressionSyntax)context.Node;
        var function = FunctionContext.Get(awaitExpression, context.SemanticModel, types, context.CancellationToken);
        if (function.Node == null || !function.IsAsync) return;

        var awaitedType = context.SemanticModel.GetTypeInfo(awaitExpression.Expression, context.CancellationToken).Type;
        var hasType = awaitedType is { TypeKind: not TypeKind.Error };
        var awaitsFlow = hasType && types.AwaitsThroughFlowAwaiter(awaitExpression, context.SemanticModel, context.CancellationToken);

        if (function.ReturnsFlowTask)
        {
            // FLOW002: only FlowTask awaiters take part in scope registration and unwinding.
            if (hasType && !awaitsFlow)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ChooseForeignAwaitDescriptor(awaitedType, awaitExpression.SpanStart, context.SemanticModel, types),
                    awaitExpression.GetLocation(),
                    awaitedType.ToMinimalDisplayString(context.SemanticModel, awaitExpression.SpanStart)));
            }
        }
        else if (awaitsFlow)
        {
            // FLOW005: a FlowTask awaitable awaited by a method that is not a scope.
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.FlowAwaitOutsideFlowTask,
                awaitExpression.GetLocation(),
                awaitedType.ToMinimalDisplayString(context.SemanticModel, awaitExpression.SpanStart)));
        }
    }

    /// <summary>FLOW002's message names the bridge that exists for the awaited type.</summary>
    static DiagnosticDescriptor ChooseForeignAwaitDescriptor(ITypeSymbol awaitedType, int position, SemanticModel model, FlowTaskTypes types)
    {
        if (types.IsTaskLikeOrConfigured(awaitedType)) return Descriptors.ForeignAwaitTaskLike;
        if (types.IsYieldAwaitable(awaitedType)) return Descriptors.ForeignAwaitYield;
        // An AsFlow bridge in scope for the type: UniTask, Unity's Awaitable and AsyncOperation, Godot's SignalAwaiter.
        if (awaitedType is INamespaceOrTypeSymbol container &&
            !model.LookupSymbols(position, container, "AsFlow", includeReducedExtensionMethods: true).IsEmpty)
        {
            return Descriptors.ForeignAwaitWithAsFlow;
        }

        return Descriptors.ForeignAwaitOther;
    }

    static void AnalyzeAwaitForEach(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var forEach = (CommonForEachStatementSyntax)context.Node;
        if (!forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)) return;
        var function = FunctionContext.Get(forEach, context.SemanticModel, types, context.CancellationToken);
        if (function.Node == null || !function.IsAsync) return;

        var info = context.SemanticModel.GetForEachStatementInfo(forEach);
        var awaitedType = info.MoveNextMethod?.ReturnType;
        if (awaitedType is null or { TypeKind: TypeKind.Error }) return;

        var span = TextSpan.FromBounds(forEach.AwaitKeyword.SpanStart, forEach.ForEachKeyword.Span.End);
        var location = Location.Create(forEach.SyntaxTree, span);
        var display = awaitedType.ToMinimalDisplayString(context.SemanticModel, forEach.SpanStart);
        if (function.ReturnsFlowTask)
        {
            // IAsyncEnumerable (MoveNextAsync returns ValueTask<bool>) is foreign: every iteration ignores cancellation.
            if (!types.IsLibraryAwaitable(awaitedType))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ForeignAwaitForEach, location, display));
            }
        }
        else if (types.IsLibraryAwaitable(awaitedType))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.FlowAwaitOutsideFlowTask, location, display));
        }
    }

    /// <summary>
    /// 'await using' awaits the DisposeAsync of each resource when the block ends: in a FlowTask method, one that is not a
    /// FlowTask awaitable (IAsyncDisposable's ValueTask) is FLOW002; outside one, a FlowTask awaitable is FLOW005.
    /// </summary>
    static void AnalyzeAwaitUsing(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        SyntaxToken awaitKeyword;
        SyntaxToken usingKeyword;
        VariableDeclarationSyntax declaration;
        ExpressionSyntax resource = null;
        switch (context.Node)
        {
            case LocalDeclarationStatementSyntax local when local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                awaitKeyword = local.AwaitKeyword;
                usingKeyword = local.UsingKeyword;
                declaration = local.Declaration;
                break;
            case UsingStatementSyntax statement when statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                awaitKeyword = statement.AwaitKeyword;
                usingKeyword = statement.UsingKeyword;
                declaration = statement.Declaration;
                resource = statement.Expression;
                break;
            default:
                return;
        }

        var model = context.SemanticModel;
        var function = FunctionContext.Get(context.Node, model, types, context.CancellationToken);
        if (function.Node == null || !function.IsAsync) return;

        var inFlowTask = function.ReturnsFlowTask;
        ITypeSymbol awaitedType = null;
        if (resource != null)
        {
            awaitedType = DisposeAsyncResult(model.GetTypeInfo(resource, context.CancellationToken).Type, model.Compilation);
        }
        else if (declaration != null)
        {
            // One report per statement: the first resource whose DisposeAsync is reported.
            foreach (var variable in declaration.Variables)
            {
                awaitedType = DisposeAsyncResult((model.GetDeclaredSymbol(variable, context.CancellationToken) as ILocalSymbol)?.Type, model.Compilation);
                if (IsReported(awaitedType, types, inFlowTask)) break;
            }
        }

        if (!IsReported(awaitedType, types, inFlowTask)) return;
        var location = Location.Create(context.Node.SyntaxTree, TextSpan.FromBounds(awaitKeyword.SpanStart, usingKeyword.Span.End));
        var descriptor = inFlowTask ? Descriptors.ForeignAwaitUsing : Descriptors.FlowAwaitOutsideFlowTask;
        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, awaitedType.ToMinimalDisplayString(model, context.Node.SpanStart)));
    }

    /// <summary>A foreign awaitable in a FlowTask method (FLOW002), or a FlowTask awaitable outside one (FLOW005).</summary>
    static bool IsReported(ITypeSymbol awaitedType, FlowTaskTypes types, bool inFlowTask) =>
        awaitedType != null && types.IsLibraryAwaitable(awaitedType) != inFlowTask;

    /// <summary>
    /// What 'await using' awaits for a resource of <paramref name="resourceType"/>, as the compiler binds it: the
    /// DisposeAsync of IAsyncDisposable when the type converts to it, otherwise its own DisposeAsync().
    /// </summary>
    static ITypeSymbol DisposeAsyncResult(ITypeSymbol resourceType, Compilation compilation)
    {
        if (resourceType is null or { TypeKind: TypeKind.Error }) return null;
        var asyncDisposable = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
        var type = asyncDisposable != null && compilation is CSharpCompilation cs && cs.ClassifyConversion(resourceType, asyncDisposable).IsImplicit
            ? asyncDisposable
            : resourceType;
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers("DisposeAsync"))
            {
                if (member is IMethodSymbol { IsStatic: false, Parameters.IsEmpty: true } method) return method.ReturnType;
            }
        }

        return null;
    }
}
