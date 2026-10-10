namespace Katout.FlowTask.Analyzers;

/// <summary>
/// The innermost function (method, local function, lambda, anonymous method, accessor or top-level statements)
/// that lexically contains a node, and whether it is a "FlowTask method": async and returning FlowTask/FlowTask&lt;T&gt;.
/// </summary>
internal readonly struct FunctionContext
{
    FunctionContext(SyntaxNode node, IMethodSymbol symbol, bool isAsync, bool returnsFlowTask)
    {
        Node = node;
        Symbol = symbol;
        IsAsync = isAsync;
        ReturnsFlowTask = returnsFlowTask;
    }

    /// <summary>The function syntax, or null when the node is not inside a function body.</summary>
    public SyntaxNode Node { get; }

    public IMethodSymbol Symbol { get; }

    public bool IsAsync { get; }

    public bool ReturnsFlowTask { get; }

    /// <summary>An async method, local function or lambda returning FlowTask or FlowTask&lt;T&gt;.</summary>
    public bool IsFlowTaskMethod => IsAsync && ReturnsFlowTask;

    public static FunctionContext Get(SyntaxNode node, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        var function = FindContainingFunction(node);
        if (function == null) return default;

        IMethodSymbol symbol;
        switch (function)
        {
            case AnonymousFunctionExpressionSyntax lambda:
                symbol = model.GetSymbolInfo(lambda, cancellationToken).Symbol as IMethodSymbol;
                break;
            case LocalFunctionStatementSyntax local:
                symbol = model.GetDeclaredSymbol(local, cancellationToken) as IMethodSymbol;
                break;
            case BaseMethodDeclarationSyntax method:
                symbol = model.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol;
                break;
            case CompilationUnitSyntax _:
                // Top-level statements: an implicit async Main when it contains await; never a FlowTask method.
                return new FunctionContext(function, null, isAsync: true, returnsFlowTask: false);
            default:
                // Accessors and expression-bodied properties: never async.
                return new FunctionContext(function, null, isAsync: false, returnsFlowTask: false);
        }

        if (symbol == null) return new FunctionContext(function, null, isAsync: false, returnsFlowTask: false);
        return new FunctionContext(function, symbol, symbol.IsAsync, types.IsFlowTask(symbol.ReturnType));
    }

    /// <summary>Walks up to the innermost function-like node; null when the node is not inside a function body.</summary>
    public static SyntaxNode FindContainingFunction(SyntaxNode node)
    {
        for (var n = node.Parent; n != null; n = n.Parent)
        {
            switch (n)
            {
                case AnonymousFunctionExpressionSyntax _:
                case LocalFunctionStatementSyntax _:
                case BaseMethodDeclarationSyntax _:
                case AccessorDeclarationSyntax _:
                    return n;
                case ArrowExpressionClauseSyntax arrow when arrow.Parent is BasePropertyDeclarationSyntax:
                    return n;
                case GlobalStatementSyntax global:
                    return global.Parent as CompilationUnitSyntax;
                case MemberDeclarationSyntax _:
                case AttributeSyntax _:
                    return null;
            }
        }

        return null;
    }
}
