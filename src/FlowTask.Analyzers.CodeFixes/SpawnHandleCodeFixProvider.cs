namespace Katout.FlowTask.Analyzers.CodeFixes;

/// <summary>
/// FLOW008: 'Flow.Spawn(x);' (or 'void M() => Flow.Spawn(x);', 'x => Flow.Spawn(x)') becomes '_ = Flow.Spawn(x);' (the
/// child ends with this scope, as before, and the code now says so) or 'FlowWorld.Current.Run(x);' (a root flow that
/// outlives this one). 'var h = Flow.Spawn(x);' with h never read becomes '_ = Flow.Spawn(x);' as a whole statement,
/// unless h is written again.
/// Discard is not offered with a variable named '_' in scope; Run only in a FlowTask method, where FlowWorld.Current is
/// set (as for FLOW003). Run keeps the arguments and type arguments as written (FlowWorld.Run takes the same task),
/// and spells FlowWorld in full where the name does not bind, so that Fix All changes nothing but the calls.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SpawnHandleCodeFixProvider)), Shared]
public sealed class SpawnHandleCodeFixProvider : CodeFixProvider
{
    public const string DiscardEquivalenceKey = "FLOW008.Discard";
    public const string RunEquivalenceKey = "FLOW008.Run";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.SpawnHandleDropped);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        if (types?.Flow == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var span = diagnostic.Location.SourceSpan;
            if (root.FindNode(span, getInnermostNodeForTie: true)?.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } invocation ||
                invocation.Span != span) continue;

            if (CodeFixHelpers.CanDiscard(model, invocation.SpanStart) &&
                (invocation.Parent is not EqualsValueClauseSyntax ||
                 GetSingleLocalDeclaration(invocation) is { } declaration && !IsWrittenAgain(declaration, model, context.CancellationToken)))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Resources.CodeFixDiscardSpawnHandle,
                        ct => DiscardAsync(context.Document, invocation, ct),
                        equivalenceKey: DiscardEquivalenceKey),
                    diagnostic);
            }

            if (types.FlowWorld != null && FunctionContext.Get(invocation, model, types, context.CancellationToken).IsFlowTaskMethod)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Resources.CodeFixRunInsteadOfSpawn,
                        ct => RunAsync(context.Document, invocation, ct),
                        equivalenceKey: RunEquivalenceKey),
                    diagnostic);
            }
        }
    }

    static async Task<Document> DiscardAsync(Document document, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (GetSingleLocalDeclaration(invocation) is { } declaration)
        {
            // 'var h = Flow.Spawn(x);' (h never read) -> '_ = Flow.Spawn(x);'
            var statement = ExpressionStatement(CodeFixHelpers.Discard(invocation)).WithTriviaFrom(declaration);
            return document.WithSyntaxRoot(root.ReplaceNode(declaration, statement));
        }

        return document.WithSyntaxRoot(root.ReplaceNode(invocation, CodeFixHelpers.Discard(invocation)));
    }

    /// <summary>The declaration when the invocation initializes its only variable ('var h = Flow.Spawn(x);'), or null.</summary>
    static LocalDeclarationStatementSyntax GetSingleLocalDeclaration(InvocationExpressionSyntax invocation) =>
        invocation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax declaration } } }
            ? declaration
            : null;

    /// <summary>
    /// True when the variable of 'var h = Flow.Spawn(x);' is written again ('h = Flow.Spawn(y);', 'out h'): removing the
    /// declaration would leave those writes without a variable (CS0103).
    /// </summary>
    static bool IsWrittenAgain(LocalDeclarationStatementSyntax declaration, SemanticModel model, CancellationToken cancellationToken)
    {
        var scope = LocalUsage.GetScope(declaration);
        return scope == null ||
               model.GetDeclaredSymbol(declaration.Declaration.Variables[0], cancellationToken) is not ILocalSymbol local ||
               LocalUsage.IsWritten(local, scope, model, cancellationToken);
    }

    /// <summary>'Flow.Spawn(x)', 'Spawn&lt;T&gt;(x)' (using static) and so on become 'FlowWorld.Current.Run(x)' / '.Run&lt;T&gt;(x)'.</summary>
    static async Task<Document> RunAsync(Document document, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        var current = CodeFixHelpers.CurrentWorld(model, invocation.SpanStart, types.FlowWorld, out var needsUsing);
        if (needsUsing)
        {
            current = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, CodeFixHelpers.QualifiedType(types.FlowWorld.Name), IdentifierName("Current"));
        }

        SimpleNameSyntax run = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => GenericName(Identifier("Run"), generic.TypeArgumentList),
            GenericNameSyntax generic => GenericName(Identifier("Run"), generic.TypeArgumentList),
            _ => IdentifierName("Run"),
        };
        var replacement = invocation.WithExpression(
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, current, run).WithTriviaFrom(invocation.Expression));
        return document.WithSyntaxRoot(root.ReplaceNode(invocation, replacement));
    }
}
