namespace Katout.FlowTask.Analyzers;

/// <summary>
/// Values that must not be dropped:
/// FLOW003 (FlowTask never awaited or started), FLOW004 (lifetime handle not stored).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedValueAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.FlowTaskDropped,
        Descriptors.FlowTaskDiscarded,
        Descriptors.FlowTaskDiscardedWithoutRunning,
        Descriptors.FlowTaskLocalNeverRead,
        Descriptors.FlowTaskCollectionNeverRead,
        Descriptors.FlowTaskStartedOnSomePaths,
        Descriptors.LifetimeHandleDropped,
        Descriptors.LifetimeHandleLocalNeverUsed);

    public override void Initialize(AnalysisContext context)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = FlowTaskTypes.TryCreate(start.Compilation);
            if (types == null) return;
            start.RegisterSyntaxNodeAction(c => AnalyzeExpressionStatement(c, types), SyntaxKind.ExpressionStatement);
            start.RegisterSyntaxNodeAction(c => AnalyzeExpressionBody(c, types), SyntaxKind.ArrowExpressionClause);
            start.RegisterSyntaxNodeAction(c => AnalyzeLambdaBody(c, types), SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeLocalDeclaration(c, types), SyntaxKind.LocalDeclarationStatement);
        });
    }

    static void AnalyzeExpressionStatement(SyntaxNodeAnalysisContext context, FlowTaskTypes types) =>
        AnalyzeDiscardedExpression(context, types, ((ExpressionStatementSyntax)context.Node).Expression, flowTaskOnly: false);

    /// <summary>'void M() => expr;' and 'async FlowTask M() => expr;' discard the value like a statement does.</summary>
    static void AnalyzeExpressionBody(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var arrow = (ArrowExpressionClauseSyntax)context.Node;
        if (arrow.Parent is not BaseMethodDeclarationSyntax and not LocalFunctionStatementSyntax and
            not AccessorDeclarationSyntax) return;
        var symbol = context.SemanticModel.GetDeclaredSymbol(arrow.Parent, context.CancellationToken) as IMethodSymbol;
        if (DiscardsBodyValue(symbol)) AnalyzeDiscardedExpression(context, types, arrow.Expression, flowTaskOnly: false);
    }

    /// <summary>
    /// 'Action a = () => DoThing();' drops the FlowTask (FLOW003). Only FLOW003 is checked in lambdas: for
    /// lifetime handles a void lambda is usually the 'Assert.Throws(() => x.Pause())' idiom, where the call is expected
    /// to throw, while a FlowTask method never throws when called (it is lazy).
    /// </summary>
    static void AnalyzeLambdaBody(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var lambda = (LambdaExpressionSyntax)context.Node;
        if (lambda.Body is not ExpressionSyntax body) return;
        var symbol = context.SemanticModel.GetSymbolInfo(lambda, context.CancellationToken).Symbol as IMethodSymbol;
        if (DiscardsBodyValue(symbol)) AnalyzeDiscardedExpression(context, types, body, flowTaskOnly: true);
    }

    /// <summary>
    /// An expression body's value is discarded when the function returns void, or is async and returns a
    /// non-generic task type (FlowTask, Task, ValueTask, ...). FLOW008 (SpawnContextAnalyzer) uses it too.
    /// </summary>
    internal static bool DiscardsBodyValue(IMethodSymbol symbol)
    {
        if (symbol == null) return false;
        if (symbol.ReturnsVoid) return true;
        return symbol.IsAsync && symbol.ReturnType is INamedTypeSymbol named && named.Arity == 0;
    }

    static void AnalyzeDiscardedExpression(SyntaxNodeAnalysisContext context, FlowTaskTypes types, ExpressionSyntax expression, bool flowTaskOnly)
    {
        var model = context.SemanticModel;

        switch (expression)
        {
            case AssignmentExpressionSyntax assignment:
                // '_ = value;' is an explicit discard: fine for FLOW004, but a discarded FlowTask never runs.
                if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                    IsDiscard(assignment.Left, model, context.CancellationToken) &&
                    types.IsLazyTask(model.GetTypeInfo(assignment.Right, context.CancellationToken).Type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.FlowTaskDiscarded, assignment.GetLocation()));
                }

                return;
            case PrefixUnaryExpressionSyntax _:
            case PostfixUnaryExpressionSyntax _:
                return;
            case InvocationExpressionSyntax invocation when IsDiscardOfNewTask(invocation, model, types, context.CancellationToken):
                // 'Load().Discard();' (the UniTask 'Forget' habit): the task is released without ever running.
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.FlowTaskDiscardedWithoutRunning, invocation.GetLocation()));
                return;
        }

        var type = model.GetTypeInfo(expression, context.CancellationToken).Type;
        if (type is null or { TypeKind: TypeKind.Error } or { SpecialType: SpecialType.System_Void }) return;

        if (types.IsLazyTask(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.FlowTaskDropped, expression.GetLocation()));
            return;
        }

        if (flowTaskOnly) return;

        if (types.IsLifetimeHandle(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.LifetimeHandleDropped,
                expression.GetLocation(),
                FlowTaskTypes.UnwrapNullable(type).ToMinimalDisplayString(model, expression.SpanStart)));
        }
    }

    /// <summary>
    /// 'R.Discard()' where Discard is FlowTask.Discard / FlowTask&lt;T&gt;.Discard and R creates the task right there (a
    /// call or 'new'). Discarding a stored task ('t.Discard()', a branch decided not to run it) is the intended use.
    /// </summary>
    static bool IsDiscardOfNewTask(InvocationExpressionSyntax invocation, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Name.Identifier.ValueText != "Discard" ||
            invocation.ArgumentList.Arguments.Count != 0) return false;

        var receiver = memberAccess.Expression;
        while (receiver is ParenthesizedExpressionSyntax parenthesized) receiver = parenthesized.Expression;
        if (receiver is not InvocationExpressionSyntax and not ObjectCreationExpressionSyntax) return false;

        return model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
               types.IsFlowTask(method.ContainingType);
    }

    static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;
        if (declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) || declaration.IsConst) return;

        SyntaxNode scope = null;
        foreach (var variable in declaration.Declaration.Variables)
        {
            if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not ILocalSymbol local) continue;
            if (types.IsLazyTask(local.Type))
            {
                scope ??= LocalUsage.GetScope(declaration);
                if (scope == null) continue;
                if (LocalUsage.IsRead(local, scope, context.SemanticModel, context.CancellationToken))
                {
                    // Only a FlowTask holds a node to release where it is skipped (Discard). A TaskBridge creates
                    // nothing until it is awaited, and has no Discard.
                    if (types.IsFlowTask(local.Type)) ReportStoresNotReadOnEveryPath(context, declaration, variable, local, scope);
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(Descriptors.FlowTaskLocalNeverRead, variable.Identifier.GetLocation(), local.Name));
            }
            else if (types.IsLazyTaskCollection(local.Type))
            {
                // 'var list = new List<FlowTask> { A(), B() };' and then never awaited: none of them runs.
                scope ??= LocalUsage.GetScope(declaration);
                if (scope == null ||
                    LocalUsage.IsReadOtherThanFilling(local, scope, context.SemanticModel, context.CancellationToken, out var filled) ||
                    !(filled || CreatesValue(variable))) continue;
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.FlowTaskCollectionNeverRead, variable.Identifier.GetLocation(), local.Name));
            }
            else if (types.IsLifetimeHandle(local.Type) && CreatesHandle(variable.Initializer?.Value) && local.Name != "_")
            {
                // FLOW004: 'var h = clock.Pause();' and then never disposed or used: active until the scope ends.
                // 'var _ = clock.Pause();' reads as the discard '_ = clock.Pause();', which keeps it for the scope on purpose.
                scope ??= LocalUsage.GetScope(declaration);
                if (scope == null || LocalUsage.IsRead(local, scope, context.SemanticModel, context.CancellationToken)) continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.LifetimeHandleLocalNeverUsed,
                    variable.Identifier.GetLocation(),
                    FlowTaskTypes.UnwrapNullable(local.Type).ToMinimalDisplayString(context.SemanticModel, variable.SpanStart),
                    local.Name));
            }
        }
    }

    /// <summary>
    /// 'var t = A(); if (c) await t;': the local is read, but a task stored in it (by the initializer or by a later
    /// 't = B();' of the same function) is not read on every path after the store, so it never runs on the others.
    /// </summary>
    static void ReportStoresNotReadOnEveryPath(
        SyntaxNodeAnalysisContext context, LocalDeclarationStatementSyntax declaration, VariableDeclaratorSyntax variable, ILocalSymbol local, SyntaxNode scope)
    {
        var model = context.SemanticModel;
        if (CreatesValue(variable) && !LocalUsage.IsDefinitelyReadAfter(declaration, local, scope, model, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.FlowTaskStartedOnSomePaths, variable.Identifier.GetLocation(), local.Name));
        }

        var function = FunctionContext.FindContainingFunction(declaration);
        foreach (var node in scope.DescendantNodes())
        {
            // Only 't = X();' statements of the declaring function: a store whose value is used ('await (t = X())')
            // or that happens in a nested function is not followed.
            if (node is not AssignmentExpressionSyntax assignment ||
                !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                assignment.Parent is not ExpressionStatementSyntax statement ||
                !LocalUsage.IsReference(assignment.Left, local, model, context.CancellationToken, out var target) ||
                !CreatesValue(assignment.Right) ||
                FunctionContext.FindContainingFunction(statement) != function) continue;

            if (!LocalUsage.IsDefinitelyReadAfter(statement, local, scope, model, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.FlowTaskStartedOnSomePaths, target.GetLocation(), local.Name));
            }
        }
    }

    /// <summary>An initializer that produces a value ('= default' and '= null' create nothing to release).</summary>
    static bool CreatesValue(VariableDeclaratorSyntax variable) => CreatesValue(variable.Initializer?.Value);

    static bool CreatesValue(ExpressionSyntax value)
    {
        while (value is ParenthesizedExpressionSyntax parenthesized) value = parenthesized.Expression;
        return value != null &&
               !value.IsKind(SyntaxKind.DefaultLiteralExpression) &&
               !value.IsKind(SyntaxKind.DefaultExpression) &&
               !value.IsKind(SyntaxKind.NullLiteralExpression);
    }

    /// <summary>
    /// An initializer that makes a new handle: a call or 'new', also through 'await', '?.', '!' and either side of
    /// '?:'. Copying a handle that exists elsewhere ('var s = this.Damaged;', 'var h = handles[0];') creates nothing
    /// that this local owns.
    /// </summary>
    static bool CreatesHandle(ExpressionSyntax value)
    {
        while (true)
        {
            switch (value)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    value = parenthesized.Expression;
                    continue;
                case AwaitExpressionSyntax awaitExpression:
                    value = awaitExpression.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax suppress when suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    value = suppress.Operand;
                    continue;
                case ConditionalAccessExpressionSyntax conditionalAccess:
                    value = conditionalAccess.WhenNotNull;
                    continue;
                case ConditionalExpressionSyntax conditional:
                    return CreatesHandle(conditional.WhenTrue) || CreatesHandle(conditional.WhenFalse);
                case InvocationExpressionSyntax _:
                case ObjectCreationExpressionSyntax _:
                case ImplicitObjectCreationExpressionSyntax _:
                    return true;
                default:
                    return false;
            }
        }
    }

    static bool IsDiscard(ExpressionSyntax left, SemanticModel model, CancellationToken cancellationToken)
    {
        if (left is not IdentifierNameSyntax identifier || identifier.Identifier.ValueText != "_") return false;
        var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
        return symbol is null or { Kind: SymbolKind.Discard };
    }
}
