using System.Collections.Generic;

namespace Katout.FlowTask.Analyzers;

/// <summary>
/// FLOW006 (Info): Signal&lt;T&gt;.Next() / EventSignal&lt;T&gt;.Next() awaited in a loop of a FlowTask method. Next only
/// waits for the next emit, so every value emitted while the loop body runs is dropped; a subscription made
/// before the loop keeps them.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SignalNextInLoopAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.SignalNextInLoop);

    public override void Initialize(AnalysisContext context)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = FlowTaskTypes.TryCreate(start.Compilation);
            if (types == null || (types.SignalOfT == null && types.EventSignalOfT == null)) return;
            start.RegisterSyntaxNodeAction(c => AnalyzeAwait(c, types), SyntaxKind.AwaitExpression);
        });
    }

    static void AnalyzeAwait(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var awaitExpression = (AwaitExpressionSyntax)context.Node;
        var loop = SignalLoops.InnermostRepeatingLoop(awaitExpression);
        if (loop == null) return;

        var model = context.SemanticModel;
        if (!FunctionContext.Get(awaitExpression, model, types, context.CancellationToken).IsFlowTaskMethod) return;

        foreach (var call in SignalLoops.NextCalls(awaitExpression.Expression, intoAwaits: false))
        {
            if (!SignalLoops.IsSignalNext(call, model, types, context.CancellationToken, out var method)) continue;
            var receiver = ((MemberAccessExpressionSyntax)call.Expression).Expression;

            // A receiver that can be another signal on every iteration ('arr[i]', a local made or reassigned in the
            // body): there is no single signal to subscribe to beforehand.
            if (SignalLoops.VariesInside(receiver, loop, model, context.CancellationToken)) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.SignalNextInLoop,
                call.GetLocation(),
                receiver.WithoutTrivia().ToString(),
                method.Name));
        }
    }
}

/// <summary>Syntax shared by FLOW006 and its code fix.</summary>
internal static class SignalLoops
{
    /// <summary>
    /// The innermost loop (while, do, for, foreach) that evaluates <paramref name="node"/> again on every iteration,
    /// within the function that contains it. A for loop's initializer and a foreach's collection run once and do not
    /// count. Null when there is none.
    /// </summary>
    public static StatementSyntax InnermostRepeatingLoop(SyntaxNode node)
    {
        foreach (var loop in RepeatingLoops(node)) return loop;
        return null;
    }

    /// <summary>The loops that repeat <paramref name="node"/>, innermost first (see InnermostRepeatingLoop).</summary>
    public static IEnumerable<StatementSyntax> RepeatingLoops(SyntaxNode node)
    {
        for (SyntaxNode child = node, parent = node.Parent; parent != null; child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                case AnonymousFunctionExpressionSyntax _:
                case LocalFunctionStatementSyntax _:
                case BaseMethodDeclarationSyntax _:
                case AccessorDeclarationSyntax _:
                case CompilationUnitSyntax _:
                    yield break;
                case WhileStatementSyntax _:
                case DoStatementSyntax _:
                    yield return (StatementSyntax)parent;
                    break;
                case ForStatementSyntax forStatement when child != forStatement.Declaration && !forStatement.Initializers.Contains(child as ExpressionSyntax):
                    yield return forStatement;
                    break;
                case CommonForEachStatementSyntax forEach when child != forEach.Expression:
                    yield return forEach;
                    break;
            }
        }
    }

    /// <summary>
    /// 'x.Next()' and 'x.NextOrClosed()' calls in <paramref name="root"/>, outside nested functions, and outside nested
    /// awaits unless <paramref name="intoAwaits"/> (each await is analyzed on its own).
    /// </summary>
    public static IEnumerable<InvocationExpressionSyntax> NextCalls(SyntaxNode root, bool intoAwaits)
    {
        var descendants = root.DescendantNodesAndSelf(n => n == root ||
            (n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) && (intoAwaits || n is not AwaitExpressionSyntax)));
        foreach (var node in descendants)
        {
            if (node is InvocationExpressionSyntax invocation &&
                invocation.ArgumentList.Arguments.Count == 0 &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                (memberAccess.Name.Identifier.ValueText == "Next" || memberAccess.Name.Identifier.ValueText == "NextOrClosed"))
            {
                yield return invocation;
            }
        }
    }

    /// <summary>A call of Signal&lt;T&gt; / EventSignal&lt;T&gt; Next() or NextOrClosed().</summary>
    public static bool IsSignalNext(InvocationExpressionSyntax call, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken, out IMethodSymbol method)
    {
        method = model.GetSymbolInfo(call, cancellationToken).Symbol as IMethodSymbol;
        return method != null && !method.IsStatic && types.IsSignal(method.ContainingType);
    }

    /// <summary>
    /// The variables and members the receiver reads ('s', 'Changed' in 's.Changed'; 'arr', 'i' in 'arr[i]'): locals,
    /// parameters, fields and properties, as their original definitions.
    /// </summary>
    public static List<ISymbol> ReceiverSymbols(ExpressionSyntax receiver, SemanticModel model, CancellationToken cancellationToken)
    {
        var symbols = new List<ISymbol>();
        foreach (var node in receiver.DescendantNodesAndSelf(n => n is not AnonymousFunctionExpressionSyntax))
        {
            if (node is not IdentifierNameSyntax identifier) continue;
            var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            if (symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol)
            {
                symbols.Add(symbol.OriginalDefinition);
            }
        }

        return symbols;
    }

    /// <summary>
    /// True when the receiver can be another object on each iteration of <paramref name="loop"/>: it reads a local
    /// declared inside the loop (a foreach variable, a for counter, a local of the body), or a variable or member
    /// that the loop assigns ('s = b;', 'this.Target = next;', 'i++', 'ref s', '(s, x) = ...').
    /// </summary>
    public static bool VariesInside(ExpressionSyntax receiver, SyntaxNode loop, SemanticModel model, CancellationToken cancellationToken)
    {
        var symbols = ReceiverSymbols(receiver, model, cancellationToken);
        foreach (var symbol in symbols)
        {
            if (symbol is not ILocalSymbol local) continue;
            foreach (var reference in local.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree == loop.SyntaxTree && loop.Span.Contains(reference.Span)) return true;
            }
        }

        if (symbols.Count == 0) return false;
        foreach (var node in loop.DescendantNodes())
        {
            var target = WriteTarget(node);
            if (target != null && WritesAny(target, symbols, model, cancellationToken)) return true;
        }

        return false;
    }

    /// <summary>The expression that <paramref name="node"/> writes, or null.</summary>
    static ExpressionSyntax WriteTarget(SyntaxNode node) => node switch
    {
        AssignmentExpressionSyntax assignment => assignment.Left,
        PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression) =>
            prefix.Operand,
        PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression) =>
            postfix.Operand,
        ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) =>
            argument.Expression,
        _ => null,
    };

    /// <summary>
    /// Whether writing <paramref name="target"/> changes one of <paramref name="symbols"/> (an element write counts for
    /// its array or list).
    /// </summary>
    static bool WritesAny(ExpressionSyntax target, List<ISymbol> symbols, SemanticModel model, CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (target)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    target = parenthesized.Expression;
                    continue;
                case ElementAccessExpressionSyntax elementAccess:
                    target = elementAccess.Expression;
                    continue;
                case TupleExpressionSyntax tuple:
                    foreach (var argument in tuple.Arguments)
                    {
                        if (WritesAny(argument.Expression, symbols, model, cancellationToken)) return true;
                    }

                    return false;
                case IdentifierNameSyntax _:
                case MemberAccessExpressionSyntax _:
                    var symbol = model.GetSymbolInfo(target, cancellationToken).Symbol?.OriginalDefinition;
                    return symbol != null && Contains(symbols, symbol);
                default:
                    return false;
            }
        }
    }

    public static bool Contains(List<ISymbol> symbols, ISymbol symbol)
    {
        foreach (var s in symbols)
        {
            if (SymbolEqualityComparer.Default.Equals(s, symbol)) return true;
        }

        return false;
    }
}
