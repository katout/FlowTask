using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis.Formatting;

namespace Katout.FlowTask.Analyzers.CodeFixes;

/// <summary>
/// FLOW001. A catch clause of the cancellation's own types that does not end with 'throw'
/// gets 'throw;' where it returns, breaks, continues or falls off its end; one whose body is only a return (or nothing)
/// can be removed instead (when nothing else is left of the try statement, its statements take its place, or its block
/// does when they would change meaning outside it: a using declaration, a name used around it). A clause of
/// OperationCanceledException, and every catch-all, can take only the other exceptions with
/// 'when (e is not FlowCanceledException)' in front of its filter (the cancellation passes before a filter with side
/// effects runs; C# 8 gets '!(e is FlowCanceledException)'); a clause without a variable gets one.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CatchCancellationCodeFixProvider)), Shared]
public sealed class CatchCancellationCodeFixProvider : CodeFixProvider
{
    public const string RethrowEquivalenceKey = nameof(CatchCancellationCodeFixProvider) + ".Rethrow";
    public const string RemoveEquivalenceKey = nameof(CatchCancellationCodeFixProvider) + ".Remove";
    public const string ExcludeEquivalenceKey = nameof(CatchCancellationCodeFixProvider) + ".Exclude";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.CatchCanObserveCancellation);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        if (types?.FlowCanceledException == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var clause = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)?.FirstAncestorOrSelf<CatchClauseSyntax>();
            if (clause == null) continue;

            var cancellationClause = CatchClassifier.IsCancellationClause(clause, model, types, context.CancellationToken);
            if (cancellationClause)
            {
                if (TryFindExits(clause, model, out var exits, out var appendAtEnd))
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            Resources.CodeFixRethrowCancellation,
                            ct => ApplyRethrowAsync(context.Document, clause, exits, appendAtEnd, ct),
                            equivalenceKey: RethrowEquivalenceKey),
                        diagnostic);
                }

                if (OnlyReturns(clause.Block))
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            Resources.CodeFixRemoveCancellationCatch,
                            ct => ApplyRemoveAsync(context.Document, clause, ct),
                            equivalenceKey: RemoveEquivalenceKey),
                        diagnostic);
                }

                // A clause of FlowCanceledException itself would take nothing with the filter.
                var caught = model.GetTypeInfo(clause.Declaration.Type, context.CancellationToken).Type;
                if (SymbolEqualityComparer.Default.Equals(caught, types.FlowCanceledException)) continue;
            }

            var variable = VariableName(clause, model);
            context.RegisterCodeFix(
                CodeAction.Create(
                    string.Format(CultureInfo.InvariantCulture, Resources.CodeFixExcludeFlowCancellation, variable),
                    ct => ApplyExcludeAsync(context.Document, clause, variable, ct),
                    equivalenceKey: ExcludeEquivalenceKey),
                diagnostic);
        }
    }

    // ------------------------------------------------------------------ 'throw;'

    /// <summary>
    /// The statements that leave the catch block without a throw (return, break, continue, goto), and whether its end
    /// can be reached. False when a 'throw;' could not replace one: it is inside a nested catch (where 'throw;' rethrows
    /// that clause's exception) or finally block (where it does not compile).
    /// </summary>
    static bool TryFindExits(CatchClauseSyntax clause, SemanticModel model, out ImmutableArray<SyntaxNode> exits, out bool appendAtEnd)
    {
        var block = clause.Block;
        exits = ImmutableArray<SyntaxNode>.Empty;
        appendAtEnd = true;
        if (block.Statements.Count == 0) return true;

        var flow = model.AnalyzeControlFlow(block);
        if (flow == null || !flow.Succeeded) return false;
        foreach (var exit in flow.ExitPoints)
        {
            for (var n = exit.Parent; n != null && n != block; n = n.Parent)
            {
                if (n is CatchClauseSyntax or FinallyClauseSyntax) return false;
            }
        }

        exits = flow.ExitPoints.Cast<SyntaxNode>().ToImmutableArray();
        appendAtEnd = flow.EndPointIsReachable;
        return true;
    }

    static async Task<Document> ApplyRethrowAsync(Document document, CatchClauseSyntax clause, ImmutableArray<SyntaxNode> exits, bool appendAtEnd, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var block = clause.Block.ReplaceNodes(exits, (original, _) => ThrowStatement().WithTriviaFrom(original));
        if (appendAtEnd) block = AppendThrow(block);
        return document.WithSyntaxRoot(root.ReplaceNode(clause.Block, block));
    }

    /// <summary>Adds 'throw;' as the last statement, on its own line like the one before it ('{ throw; }' in an empty one-line block).</summary>
    static BlockSyntax AppendThrow(BlockSyntax block)
    {
        if (block.Statements.Count > 0)
        {
            var last = block.Statements.Last();
            var statement = ThrowStatement()
                .WithLeadingTrivia(Indentation(last))
                .WithTrailingTrivia(last.GetTrailingTrivia());
            return block.AddStatements(statement);
        }

        var newLine = CodeFixHelpers.DetectNewLine(block.SyntaxTree.GetRoot());
        if (!block.OpenBraceToken.TrailingTrivia.Any(SyntaxKind.EndOfLineTrivia))
        {
            return block
                .WithOpenBraceToken(block.OpenBraceToken.WithTrailingTrivia(Space))
                .WithStatements(SingletonList<StatementSyntax>(ThrowStatement().WithTrailingTrivia(Space)))
                .WithCloseBraceToken(block.CloseBraceToken.WithLeadingTrivia());
        }

        return block.WithStatements(SingletonList<StatementSyntax>(ThrowStatement()
            .WithLeadingTrivia(Whitespace(IndentationText(block.CloseBraceToken) + "    "))
            .WithTrailingTrivia(newLine)));
    }

    static SyntaxTriviaList Indentation(SyntaxNode node)
    {
        var text = IndentationText(node.GetFirstToken());
        return text.Length == 0 ? SyntaxTriviaList.Empty : TriviaList(Whitespace(text));
    }

    /// <summary>The white space between the start of the token's line and the token.</summary>
    static string IndentationText(SyntaxToken token)
    {
        var leading = token.LeadingTrivia;
        if (leading.Count == 0 || !leading.Last().IsKind(SyntaxKind.WhitespaceTrivia)) return "";
        return leading.Last().ToString();
    }

    // ------------------------------------------------------------------ removal

    /// <summary>A body that only returns (with or without a value), or is empty: removing the clause loses nothing but the swallow.</summary>
    static bool OnlyReturns(BlockSyntax block) =>
        block.Statements.Count == 0 || (block.Statements.Count == 1 && block.Statements[0] is ReturnStatementSyntax);

    static async Task<Document> ApplyRemoveAsync(Document document, CatchClauseSyntax clause, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var tryStatement = (TryStatementSyntax)clause.Parent;
        if (tryStatement.Catches.Count > 1 || tryStatement.Finally != null)
        {
            return document.WithSyntaxRoot(root.RemoveNode(clause, SyntaxRemoveOptions.KeepNoTrivia));
        }

        // Nothing is left of the try statement: its statements take its place, formatted where they now are, or its block
        // does when they would change meaning there.
        var statements = tryStatement.Block.Statements;
        if (statements.Count == 0) return document.WithSyntaxRoot(root.RemoveNode(tryStatement, SyntaxRemoveOptions.KeepExteriorTrivia));
        if ((tryStatement.Parent is BlockSyntax || tryStatement.Parent is SwitchSectionSyntax) && CanUnwrap(tryStatement))
        {
            var moved = new List<StatementSyntax>();
            for (var i = 0; i < statements.Count; i++)
            {
                var s = statements[i];
                if (i == 0) s = s.WithLeadingTrivia(tryStatement.GetLeadingTrivia().AddRange(s.GetLeadingTrivia().Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia))));
                if (i == statements.Count - 1) s = s.WithTrailingTrivia(tryStatement.GetTrailingTrivia());
                moved.Add(s.WithAdditionalAnnotations(Formatter.Annotation));
            }

            return document.WithSyntaxRoot(root.ReplaceNode(tryStatement, moved));
        }

        return document.WithSyntaxRoot(root.ReplaceNode(tryStatement, tryStatement.Block.WithTriviaFrom(tryStatement).WithAdditionalAnnotations(Formatter.Annotation)));
    }

    /// <summary>
    /// Whether the statements of the try block can move into the block around the try statement unchanged in meaning.
    /// Not when one of them is a using declaration (its resource would live to the end of the outer block) or a label,
    /// nor when a name they declare in the try block's scope (a local, a local function, an out variable or pattern
    /// variable of a statement that is not a block) appears anywhere else in the outer block: it could be declared there
    /// too (CS0128, CS0136), or name a field or another symbol that the moved declaration would hide. Out and pattern
    /// variables are collected from the whole statement, also where their scope is narrower: a block is kept then.
    /// </summary>
    static bool CanUnwrap(TryStatementSyntax tryStatement)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var statement in tryStatement.Block.Statements)
        {
            switch (statement)
            {
                case LabeledStatementSyntax _:
                    return false;
                case LocalDeclarationStatementSyntax declaration:
                    if (!declaration.UsingKeyword.IsKind(SyntaxKind.None)) return false;
                    foreach (var variable in declaration.Declaration.Variables) names.Add(variable.Identifier.ValueText);
                    break;
                case LocalFunctionStatementSyntax localFunction:
                    names.Add(localFunction.Identifier.ValueText);
                    break;
            }

            if (statement is BlockSyntax) continue;
            foreach (var designation in statement.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
                names.Add(designation.Identifier.ValueText);
        }

        if (names.Count == 0) return true;

        // A switch section shares its declaration space with the other sections of its switch statement.
        var outer = tryStatement.Parent is SwitchSectionSyntax section ? section.Parent : tryStatement.Parent;
        foreach (var token in outer.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken) && !tryStatement.Span.Contains(token.Span) && names.Contains(token.ValueText)) return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ 'when (e is not FlowCanceledException)'

    /// <summary>The catch variable, or the name the fix gives it ('e', or 'e1', ... when that is taken).</summary>
    static string VariableName(CatchClauseSyntax clause, SemanticModel model)
    {
        var identifier = clause.Declaration?.Identifier ?? default;
        if (identifier.IsKind(SyntaxKind.IdentifierToken) && !string.IsNullOrEmpty(identifier.ValueText)) return identifier.ValueText;
        return CodeFixHelpers.UniqueName("e", model, clause.SpanStart, clause);
    }

    static async Task<Document> ApplyExcludeAsync(Document document, CatchClauseSyntax clause, string variable, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);

        // The declaration, with a variable: 'catch' becomes 'catch (Exception e)'.
        CatchDeclarationSyntax declaration;
        SyntaxTriviaList afterHeader;
        if (clause.Declaration == null)
        {
            afterHeader = clause.CatchKeyword.TrailingTrivia;
            declaration = CatchDeclaration(
                Bare(SyntaxKind.OpenParenToken),
                ExceptionType(model, clause.SpanStart).WithTrailingTrivia(Space),
                Identifier(TriviaList(), variable, TriviaList()),
                Bare(SyntaxKind.CloseParenToken));
        }
        else
        {
            declaration = clause.Declaration;
            afterHeader = (clause.Filter?.GetLastToken() ?? declaration.CloseParenToken).TrailingTrivia;
            if (!declaration.Identifier.IsKind(SyntaxKind.IdentifierToken) || declaration.Identifier.ValueText.Length == 0)
                declaration = declaration.WithType(declaration.Type.WithTrailingTrivia(Space)).WithIdentifier(Identifier(TriviaList(), variable, TriviaList()));
        }

        var type = CodeFixHelpers.TypeReference(model, clause.SpanStart, types.FlowCanceledException, out var needsUsing);
        var typeText = type.ToString();
        var languageVersion = ((CSharpParseOptions)clause.SyntaxTree.Options).LanguageVersion;
        var exclusion = ParseExpression(languageVersion >= LanguageVersion.CSharp9
            ? variable + " is not " + typeText
            : "!(" + variable + " is " + typeText + ")");
        var condition = clause.Filter == null
            ? exclusion
            : BinaryExpression(SyntaxKind.LogicalAndExpression,
                exclusion.WithTrailingTrivia(Space),
                Token(TriviaList(), SyntaxKind.AmpersandAmpersandToken, TriviaList(Space)),
                AsOperand(clause.Filter.FilterExpression.WithoutTrivia()));

        // Tokens with explicit trivia only: elastic trivia would let the code action's formatting move the block.
        var filter = CatchFilterClause(
            Token(TriviaList(Space), SyntaxKind.WhenKeyword, TriviaList(Space)),
            Bare(SyntaxKind.OpenParenToken),
            condition,
            Token(TriviaList(), SyntaxKind.CloseParenToken, afterHeader));
        var fixedClause = clause
            .WithCatchKeyword(clause.CatchKeyword.WithTrailingTrivia(Space))
            .WithDeclaration(declaration.WithoutTrailingTrivia())
            .WithFilter(filter);

        var newRoot = root.ReplaceNode(clause, fixedClause);
        if (needsUsing) newRoot = CodeFixHelpers.AddFlowTaskUsing(newRoot);
        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>'Exception' when it binds to System.Exception at <paramref name="position"/>, else 'global::System.Exception'.</summary>
    static TypeSyntax ExceptionType(SemanticModel model, int position)
    {
        var info = model.GetSpeculativeSymbolInfo(position, IdentifierName("Exception"), SpeculativeBindingOption.BindAsTypeOrNamespace);
        var exception = model.Compilation.GetTypeByMetadataName("System.Exception");
        return ParseTypeName(SymbolEqualityComparer.Default.Equals(info.Symbol, exception) ? "Exception" : "global::System.Exception");
    }

    static SyntaxToken Bare(SyntaxKind kind) => Token(TriviaList(), kind, TriviaList());

    /// <summary>An operand of '&amp;&amp;' as it is, or in parentheses when it binds more loosely (an '||', a conditional, an assignment, a lambda).</summary>
    static ExpressionSyntax AsOperand(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression) || binary.IsKind(SyntaxKind.CoalesceExpression):
            case ConditionalExpressionSyntax _:
            case AssignmentExpressionSyntax _:
            case LambdaExpressionSyntax _:
                return ParenthesizedExpression(Bare(SyntaxKind.OpenParenToken), expression, Bare(SyntaxKind.CloseParenToken));
            default:
                return expression;
        }
    }
}
