namespace Katout.FlowTask.Analyzers;

/// <summary>
/// Where and how Flow.Spawn is called:
/// FLOW009 (in an event handler: a lambda, anonymous method or local function written in a FlowTask method and
/// subscribed to an event with '+=' runs when the event fires, and Flow.Spawn there attaches to whatever scope is
/// current at that moment: none (FlowMisuseException), or the flow that raised the event, and ends with it),
/// FLOW008 (a statement that drops the handle, 'Flow.Spawn(x);', an expression lambda in a FlowTask method whose body
/// is the call, or a local that holds the handle and is never read: written like UniTask's Forget(), it reads as work
/// that runs on, but the child is stopped when the current scope ends; '_ = Flow.Spawn(x);' says that this is intended).
/// A spawn that FLOW009 reports is not reported again by FLOW008.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SpawnContextAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.SpawnInEventHandler,
        Descriptors.SpawnHandleDropped);

    public override void Initialize(AnalysisContext context)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = FlowTaskTypes.TryCreate(start.Compilation);
            if (types?.Flow == null) return;
            start.RegisterSyntaxNodeAction(c => AnalyzeInvocation(c, types), SyntaxKind.InvocationExpression);
        });
    }

    static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, FlowTaskTypes types)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (GetInvokedName(invocation.Expression) != "Spawn") return; // cheap syntactic filter first

        var model = context.SemanticModel;
        var cancellationToken = context.CancellationToken;
        var function = FunctionContext.Get(invocation, model, types, cancellationToken);
        if (function.Node == null) return;
        if (!types.IsFlowMethod(model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol, "Spawn")) return;

        var reported = !function.IsFlowTaskMethod && AnalyzeSpawnInEventHandler(context, invocation, function.Node, types);
        if (!reported) AnalyzeDroppedHandle(context, invocation, types);
    }

    /// <summary>FLOW009: Flow.Spawn in a handler written in a FlowTask method and subscribed to an event there.</summary>
    static bool AnalyzeSpawnInEventHandler(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, SyntaxNode handler, FlowTaskTypes types)
    {
        if (handler is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax) return false;

        var owner = FindEnclosingFlowTaskMethod(handler, context.SemanticModel, types, context.CancellationToken);
        if (owner == null || !IsSubscribedToAnEvent(handler, owner, context.SemanticModel, context.CancellationToken)) return false;

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.SpawnInEventHandler, invocation.GetLocation()));
        return true;
    }

    /// <summary>
    /// FLOW008: 'Flow.Spawn(x);', 'void M() => Flow.Spawn(x);' (an expression body whose value is dropped, as
    /// DiscardedValueAnalyzer sees it), the same body in a lambda written in a FlowTask method
    /// ('items.ForEach(x => Flow.Spawn(Do(x)))'), and 'var h = Flow.Spawn(x);' where h is never read (as FLOW004 sees a
    /// lifetime handle; 'var _ =' is the discard).
    /// An expression lambda outside a FlowTask method is not checked: there a spawn that runs now throws, and
    /// 'Assert.Throws(() => Flow.Spawn(x))' expects it to. The result type of the spawned flow does not matter.
    /// </summary>
    static void AnalyzeDroppedHandle(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, FlowTaskTypes types)
    {
        var model = context.SemanticModel;
        var cancellationToken = context.CancellationToken;
        var dropped = invocation.Parent switch
        {
            ExpressionStatementSyntax _ => true,
            ArrowExpressionClauseSyntax arrow when arrow.Parent is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax =>
                DiscardedValueAnalyzer.DiscardsBodyValue(model.GetDeclaredSymbol(arrow.Parent, cancellationToken) as IMethodSymbol),
            LambdaExpressionSyntax lambda when lambda.Body == invocation =>
                DiscardedValueAnalyzer.DiscardsBodyValue(model.GetSymbolInfo(lambda, cancellationToken).Symbol as IMethodSymbol) &&
                FindEnclosingFlowTaskMethod(lambda, model, types, cancellationToken) != null,
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } => IsLocalNeverRead(variable, model, cancellationToken),
            _ => false,
        };
        if (!dropped) return;

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.SpawnHandleDropped, invocation.GetLocation()));
    }

    /// <summary>A local declared without 'using', not named '_', and never read in its scope.</summary>
    static bool IsLocalNeverRead(VariableDeclaratorSyntax variable, SemanticModel model, CancellationToken cancellationToken)
    {
        if (variable.Parent?.Parent is not LocalDeclarationStatementSyntax declaration ||
            declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) ||
            model.GetDeclaredSymbol(variable, cancellationToken) is not ILocalSymbol local ||
            local.Name == "_") return false;

        var scope = LocalUsage.GetScope(declaration);
        return scope != null && !LocalUsage.IsRead(local, scope, model, cancellationToken);
    }

    static string GetInvokedName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
        // 'using static Katout.FlowTask.Flow;'
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The nearest FlowTask method that lexically contains <paramref name="function"/>, or null.</summary>
    static SyntaxNode FindEnclosingFlowTaskMethod(SyntaxNode function, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        for (var outer = FunctionContext.Get(function, model, types, cancellationToken);
             outer.Node != null;
             outer = FunctionContext.Get(outer.Node, model, types, cancellationToken))
        {
            if (outer.IsFlowTaskMethod) return outer.Node;
            if (outer.Node is CompilationUnitSyntax) return null;
        }

        return null;
    }

    /// <summary>
    /// 'e += handler' where e is an event and handler is the lambda itself, a local that the lambda initializes, or
    /// the local function (as a method group). The '+=' is searched for in <paramref name="owner"/>.
    /// </summary>
    static bool IsSubscribedToAnEvent(SyntaxNode handler, SyntaxNode owner, SemanticModel model, CancellationToken cancellationToken)
    {
        ISymbol reference;
        if (handler is LocalFunctionStatementSyntax localFunction)
        {
            reference = model.GetDeclaredSymbol(localFunction, cancellationToken);
        }
        else
        {
            var expression = handler;
            while (expression.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax) expression = expression.Parent;
            switch (expression.Parent)
            {
                case AssignmentExpressionSyntax assignment when assignment.Right == expression:
                    return IsEventSubscription(assignment, model, cancellationToken);
                case EqualsValueClauseSyntax equals when equals.Parent is VariableDeclaratorSyntax variable:
                    reference = model.GetDeclaredSymbol(variable, cancellationToken) as ILocalSymbol;
                    break;
                default:
                    return false;
            }
        }

        if (reference == null) return false;
        foreach (var node in owner.DescendantNodes())
        {
            if (node is AssignmentExpressionSyntax assignment &&
                assignment.IsKind(SyntaxKind.AddAssignmentExpression) &&
                assignment.Right is IdentifierNameSyntax name &&
                name.Identifier.ValueText == reference.Name &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, cancellationToken).Symbol?.OriginalDefinition, reference) &&
                IsEventSubscription(assignment, model, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsEventSubscription(AssignmentExpressionSyntax assignment, SemanticModel model, CancellationToken cancellationToken) =>
        assignment.IsKind(SyntaxKind.AddAssignmentExpression) &&
        model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is IEventSymbol;
}
