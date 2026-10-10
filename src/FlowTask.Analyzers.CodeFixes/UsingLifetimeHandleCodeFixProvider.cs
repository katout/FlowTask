using System.Globalization;
using Microsoft.CodeAnalysis.Text;

namespace Katout.FlowTask.Analyzers.CodeFixes;

/// <summary>
/// FLOW004: 'clock.Pause();' becomes 'using var pause = clock.Pause();', and 'var pause = clock.Pause();' (never used)
/// becomes 'using var pause = clock.Pause();'. Either way the handle is released at the end of the block.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UsingLifetimeHandleCodeFixProvider)), Shared]
public sealed class UsingLifetimeHandleCodeFixProvider : CodeFixProvider
{
    public const string UsingDeclarationEquivalenceKey = "FLOW004.UsingDeclaration";

    const string FallbackName = "handle";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.LifetimeHandleDropped);

    // Batch fixing could pick the same local name twice in one block.
    public override FixAllProvider GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        if (types == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var declaration = FindUnusedDeclaration(root, diagnostic.Location.SourceSpan, model, types, context.CancellationToken);
            if (declaration != null)
            {
                var variable = declaration.Declaration.Variables[0].Identifier.ValueText;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        string.Format(CultureInfo.InvariantCulture, Resources.CodeFixUsingDeclaration, variable),
                        ct => AddUsingAsync(context.Document, declaration, ct),
                        equivalenceKey: UsingDeclarationEquivalenceKey),
                    diagnostic);
                continue;
            }

            var statement = CodeFixHelpers.FindExpressionStatement(root, diagnostic.Location.SourceSpan);
            if (statement == null) continue;

            // A using declaration must sit directly in a block (or in top-level statements), not in a switch section
            // or an embedded statement.
            if (statement.Parent is not BlockSyntax and not GlobalStatementSyntax) continue;

            var type = model.GetTypeInfo(statement.Expression, context.CancellationToken).Type;
            if (type == null || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T || !types.IsDisposable(type)) continue;

            var name = ChooseName(statement, model, context.CancellationToken);
            context.RegisterCodeFix(
                CodeAction.Create(
                    string.Format(CultureInfo.InvariantCulture, Resources.CodeFixUsingVar, name),
                    ct => ApplyAsync(context.Document, statement, name, ct),
                    equivalenceKey: nameof(UsingLifetimeHandleCodeFixProvider)),
                diagnostic);
        }
    }

    /// <summary>
    /// The declaration 'var h = clock.Pause();' whose variable identifier has <paramref name="span"/>, when 'using' can be
    /// added: a single disposable variable, directly in a block or in top-level statements, that is not written again
    /// (a using variable is read-only, CS1656). Null otherwise.
    /// </summary>
    static LocalDeclarationStatementSyntax FindUnusedDeclaration(SyntaxNode root, TextSpan span, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        var token = root.FindToken(span.Start);
        if (!token.IsKind(SyntaxKind.IdentifierToken) || token.Span != span) return null;
        if (token.Parent is not VariableDeclaratorSyntax variable ||
            variable.Parent is not VariableDeclarationSyntax declaration ||
            declaration.Parent is not LocalDeclarationStatementSyntax statement) return null;
        if (declaration.Variables.Count != 1 || statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return null;
        if (statement.Parent is not BlockSyntax and not GlobalStatementSyntax) return null;
        if (model.GetDeclaredSymbol(variable, cancellationToken) is not ILocalSymbol local || !types.IsDisposable(local.Type)) return null;
        var scope = LocalUsage.GetScope(statement);
        return scope != null && !LocalUsage.IsWritten(local, scope, model, cancellationToken) ? statement : null;
    }

    static async Task<Document> AddUsingAsync(Document document, LocalDeclarationStatementSyntax declaration, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var withUsing = declaration
            .WithoutLeadingTrivia()
            .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space))
            .WithLeadingTrivia(declaration.GetLeadingTrivia());
        return document.WithSyntaxRoot(root.ReplaceNode(declaration, withUsing));
    }

    static string ChooseName(ExpressionStatementSyntax statement, SemanticModel model, CancellationToken cancellationToken)
    {
        var baseName = SuggestName(statement.Expression, model, cancellationToken);
        var scope = (SyntaxNode)FunctionContext.FindContainingFunction(statement) ?? statement.Parent;
        return CodeFixHelpers.UniqueName(baseName, model, statement.SpanStart, scope);
    }

    /// <summary>A name from the producing method: Pause() -> pause, Subscribe() -> subscription.</summary>
    static string SuggestName(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
    {
        while (expression is AwaitExpressionSyntax awaitExpression) expression = awaitExpression.Expression;
        if (model.GetSymbolInfo(expression, cancellationToken).Symbol is not IMethodSymbol method) return FallbackName;
        string name = method.Name switch
        {
            "Subscribe" => "subscription",
            _ => method.Name.Length == 0 ? "" : char.ToLowerInvariant(method.Name[0]) + method.Name.Substring(1),
        };
        return CodeFixHelpers.IsUsableIdentifier(name) ? name : FallbackName;
    }

    static async Task<Document> ApplyAsync(Document document, ExpressionStatementSyntax statement, string name, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var declaration = LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName("var").WithTrailingTrivia(Space),
                    SingletonSeparatedList(
                        VariableDeclarator(Identifier(name).WithTrailingTrivia(Space))
                            .WithInitializer(EqualsValueClause(
                                Token(SyntaxKind.EqualsToken).WithTrailingTrivia(Space),
                                statement.Expression.WithoutTrivia())))))
            .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space))
            .WithSemicolonToken(statement.SemicolonToken)
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithTrailingTrivia(statement.GetTrailingTrivia());
        return document.WithSyntaxRoot(root.ReplaceNode(statement, declaration));
    }
}
