namespace Katout.FlowTask.Analyzers.CodeFixes;

/// <summary>
/// FLOW006: 'while (...) { await s.Next(); ... }' becomes
/// 'using var subscription = s.Subscribe(BufferPolicy.Latest); while (...) { await subscription.Next(); ... }'.
/// Every Next() / NextOrClosed() of the same receiver in the loop is redirected to the subscription. Offered only
/// when the receiver has no side effects (locals, parameters, fields and property chains), stays the same object in
/// the loop (FLOW006 is not reported otherwise), and the loop sits directly in a block. Subscribing before the loop
/// evaluates the receiver earlier, so a loop is not chosen when its condition reads the receiver
/// ('while (target != null)') or when it checks the receiver for null ('if (target == null) continue;', 'target?.').
/// A method called in the loop that replaces the receiver cannot be seen. BufferPolicy.Queue is not offered: its
/// capacity and overflow policy are the user's decision (docs/en/tools/analyzers.md shows how to write it).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SubscribeBeforeLoopCodeFixProvider)), Shared]
public sealed class SubscribeBeforeLoopCodeFixProvider : CodeFixProvider
{
    public const string LatestEquivalenceKey = "FLOW006.SubscribeLatest";

    const string SubscriptionName = "subscription";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.SignalNextInLoop);

    // Two diagnostics in one loop would insert two subscriptions with clashing names.
    public override FixAllProvider GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        if (types?.BufferPolicy == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not InvocationExpressionSyntax call ||
                call.Span != diagnostic.Location.SourceSpan ||
                call.Expression is not MemberAccessExpressionSyntax memberAccess ||
                !IsSideEffectFree(memberAccess.Expression)) continue;

            var loop = ChooseLoop(call, memberAccess.Expression, model, context.CancellationToken);
            if (loop == null) continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    Resources.CodeFixSubscribeBeforeLoop,
                    ct => ApplyAsync(context.Document, loop, memberAccess.Expression, types, ct),
                    equivalenceKey: LatestEquivalenceKey),
                diagnostic);
        }
    }

    /// <summary>A local, parameter, field or property, possibly through 'this.' and member accesses.</summary>
    static bool IsSideEffectFree(ExpressionSyntax receiver)
    {
        while (true)
        {
            switch (receiver)
            {
                case IdentifierNameSyntax _:
                case ThisExpressionSyntax _:
                    return true;
                case MemberAccessExpressionSyntax memberAccess when memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression) &&
                                                                   memberAccess.Name is IdentifierNameSyntax:
                    receiver = memberAccess.Expression;
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The outermost loop that repeats the call while the receiver stays the same object, and before which the receiver
    /// can be evaluated (the loop neither tests it in its condition nor checks it for null), provided that loop sits
    /// directly in a block. Null when there is no such loop.
    /// </summary>
    static StatementSyntax ChooseLoop(InvocationExpressionSyntax call, ExpressionSyntax receiver, SemanticModel model, CancellationToken cancellationToken)
    {
        var symbols = SignalLoops.ReceiverSymbols(receiver, model, cancellationToken);
        StatementSyntax chosen = null;
        foreach (var loop in SignalLoops.RepeatingLoops(call))
        {
            if (SignalLoops.VariesInside(receiver, loop, model, cancellationToken) ||
                ConditionReads(loop, symbols, model, cancellationToken) ||
                ChecksForNull(loop, symbols, model, cancellationToken)) break;
            chosen = loop;
        }

        return chosen?.Parent is BlockSyntax ? chosen : null;
    }

    /// <summary>A while / do / for condition (and a for's incrementors) that reads one of the receiver's symbols.</summary>
    static bool ConditionReads(StatementSyntax loop, List<ISymbol> symbols, SemanticModel model, CancellationToken cancellationToken)
    {
        switch (loop)
        {
            case WhileStatementSyntax whileStatement:
                return Reads(whileStatement.Condition, symbols, model, cancellationToken);
            case DoStatementSyntax doStatement:
                return Reads(doStatement.Condition, symbols, model, cancellationToken);
            case ForStatementSyntax forStatement:
                if (forStatement.Condition != null && Reads(forStatement.Condition, symbols, model, cancellationToken)) return true;
                foreach (var incrementor in forStatement.Incrementors)
                {
                    if (Reads(incrementor, symbols, model, cancellationToken)) return true;
                }

                return false;
            default:
                return false;
        }
    }

    static bool Reads(SyntaxNode node, List<ISymbol> symbols, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var descendant in node.DescendantNodesAndSelf())
        {
            if (descendant is IdentifierNameSyntax identifier &&
                model.GetSymbolInfo(identifier, cancellationToken).Symbol is ISymbol symbol &&
                SignalLoops.Contains(symbols, symbol.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>'x == null', 'x != null', 'x is ...', 'x?.' or 'x ?? ...' on one of the receiver's symbols, inside the loop.</summary>
    static bool ChecksForNull(StatementSyntax loop, List<ISymbol> symbols, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var node in loop.DescendantNodes())
        {
            ExpressionSyntax tested = node switch
            {
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression) =>
                    binary.Right.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Left
                    : binary.Left.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Right
                    : null,
                BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression) => coalesce.Left,
                IsPatternExpressionSyntax isPattern => isPattern.Expression,
                ConditionalAccessExpressionSyntax conditionalAccess => conditionalAccess.Expression,
                _ => null,
            };
            while (tested is ParenthesizedExpressionSyntax parenthesized) tested = parenthesized.Expression;
            if (tested != null &&
                model.GetSymbolInfo(tested, cancellationToken).Symbol is ISymbol symbol &&
                SignalLoops.Contains(symbols, symbol.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    static async Task<Document> ApplyAsync(Document document, StatementSyntax loop, ExpressionSyntax receiver, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        var function = (SyntaxNode)FunctionContext.FindContainingFunction(loop) ?? loop.Parent;
        var name = CodeFixHelpers.UniqueName(SubscriptionName, model, loop.SpanStart, function);
        var bufferPolicy = CodeFixHelpers.TypeReference(model, loop.SpanStart, types.BufferPolicy, out var needsUsing);

        // Redirect every Next() / NextOrClosed() of the same receiver that this loop repeats.
        var receivers = new List<ExpressionSyntax>();
        foreach (var call in SignalLoops.NextCalls(loop, intoAwaits: true))
        {
            var callReceiver = ((MemberAccessExpressionSyntax)call.Expression).Expression;
            if (callReceiver.IsEquivalentTo(receiver, topLevel: false) &&
                SignalLoops.IsSignalNext(call, model, types, cancellationToken, out _))
            {
                receivers.Add(callReceiver);
            }
        }

        var newLoop = loop.ReplaceNodes(receivers, (old, _) => IdentifierName(name).WithTriviaFrom(old));

        var indentation = SyntaxTriviaList.Empty;
        var leading = loop.GetLeadingTrivia();
        if (leading.Count > 0 && leading[leading.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia)) indentation = TriviaList(leading[leading.Count - 1]);

        // using var subscription = receiver.Subscribe(BufferPolicy.Latest);
        var subscribe = CodeFixHelpers.Invoke(
            CodeFixHelpers.AsReceiver(receiver.WithoutTrivia()),
            "Subscribe",
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, bufferPolicy, IdentifierName("Latest")));
        var declaration = LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName("var").WithTrailingTrivia(Space),
                    SingletonSeparatedList(
                        VariableDeclarator(Identifier(name).WithTrailingTrivia(Space))
                            .WithInitializer(EqualsValueClause(Token(SyntaxKind.EqualsToken).WithTrailingTrivia(Space), subscribe)))))
            .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space))
            .WithLeadingTrivia(indentation)
            .WithTrailingTrivia(CodeFixHelpers.DetectNewLine(root));

        var block = (BlockSyntax)loop.Parent;
        var index = block.Statements.IndexOf(loop);
        var newBlock = block.WithStatements(block.Statements.Replace(loop, newLoop).Insert(index, declaration));
        var newRoot = root.ReplaceNode(block, newBlock);
        if (needsUsing) newRoot = CodeFixHelpers.AddFlowTaskUsing(newRoot);
        return document.WithSyntaxRoot(newRoot);
    }
}
