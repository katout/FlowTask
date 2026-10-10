namespace Katout.FlowTask.Analyzers;

/// <summary>How a local variable is used in its scope (FLOW003 and FLOW004 locals).</summary>
internal static class LocalUsage
{
    /// <summary>The syntax that bounds the local's scope (a local is visible in its whole enclosing block).</summary>
    public static SyntaxNode GetScope(LocalDeclarationStatementSyntax declaration) => declaration.Parent switch
    {
        SwitchSectionSyntax section => section.Parent, // switch-section locals are scoped to the whole switch block
        GlobalStatementSyntax global => global.Parent, // top-level statements share one scope
        _ => declaration.Parent,
    };

    /// <summary>True when the local is read anywhere in its scope (writes such as 't = x' or 'out t' do not count).</summary>
    public static bool IsRead(ILocalSymbol local, SyntaxNode scope, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var node in scope.DescendantNodes())
        {
            if (IsReference(node, local, model, cancellationToken, out var identifier) && !IsWriteOnly(identifier)) return true;
        }

        return false;
    }

    /// <summary>
    /// For a collection local: whether it is read other than being filled ('list.Add(x)', 'list.AddRange(xs)',
    /// 'list.Insert(i, x)', 'array[i] = x'), and whether it is filled that way anywhere in its scope.
    /// </summary>
    public static bool IsReadOtherThanFilling(ILocalSymbol local, SyntaxNode scope, SemanticModel model, CancellationToken cancellationToken, out bool filled)
    {
        filled = false;
        foreach (var node in scope.DescendantNodes())
        {
            if (!IsReference(node, local, model, cancellationToken, out var identifier) || IsWriteOnly(identifier)) continue;
            if (IsFilling(identifier))
            {
                filled = true;
                continue;
            }

            return true;
        }

        return false;
    }

    static bool IsFilling(IdentifierNameSyntax identifier)
    {
        switch (identifier.Parent)
        {
            case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression == identifier:
                var name = memberAccess.Name.Identifier.ValueText;
                return (name == "Add" || name == "AddRange" || name == "Insert") &&
                       memberAccess.Parent is InvocationExpressionSyntax invocation && invocation.Expression == memberAccess;
            case ElementAccessExpressionSyntax elementAccess when elementAccess.Expression == identifier:
                return elementAccess.Parent is AssignmentExpressionSyntax assignment &&
                       assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Left == elementAccess;
            default:
                return false;
        }
    }

    /// <summary>An identifier that binds to <paramref name="local"/>.</summary>
    public static bool IsReference(SyntaxNode node, ILocalSymbol local, SemanticModel model, CancellationToken cancellationToken, out IdentifierNameSyntax identifier)
    {
        identifier = node as IdentifierNameSyntax;
        return identifier != null &&
               identifier.Identifier.ValueText == local.Name &&
               SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, local);
    }

    /// <summary>
    /// True when the local is written after its declaration: '=' and compound assignments, '++' / '--', a ref or out
    /// argument, 'ref local', or an element of a deconstruction ('(h, x) = ...').
    /// </summary>
    public static bool IsWritten(ILocalSymbol local, SyntaxNode scope, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var node in scope.DescendantNodes())
        {
            if (!IsReference(node, local, model, cancellationToken, out var identifier)) continue;
            SyntaxNode target = identifier;
            while (target.Parent is ParenthesizedExpressionSyntax) target = target.Parent;
            switch (target.Parent)
            {
                case AssignmentExpressionSyntax assignment when assignment.Left == target:
                case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                case RefExpressionSyntax _:
                    return true;
                case ArgumentSyntax argument when IsDeconstructionTarget(argument):
                    return true;
            }
        }

        return false;
    }

    /// <summary>An element of the tuple on the left of '(a, b) = ...', at any depth.</summary>
    public static bool IsDeconstructionTarget(ArgumentSyntax argument)
    {
        if (argument.Parent is not TupleExpressionSyntax tuple) return false;
        while (tuple.Parent is ArgumentSyntax outer && outer.Parent is TupleExpressionSyntax outerTuple) tuple = outerTuple;
        return tuple.Parent is AssignmentExpressionSyntax assignment && assignment.Left == tuple;
    }

    /// <summary>'t = x', 'out t' and '(t, n) = ...' write the local without reading it.</summary>
    public static bool IsWriteOnly(IdentifierNameSyntax identifier) => identifier.Parent switch
    {
        AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Left == identifier,
        ArgumentSyntax argument => argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) || IsDeconstructionTarget(argument),
        _ => false,
    };

    /// <summary>
    /// True when, after <paramref name="statement"/> (which stores a value in <paramref name="local"/>), the local is
    /// read on every path through the rest of <paramref name="scope"/>. Judged from the syntax: the following
    /// statements of each enclosing block are searched for one that reads the local outside a nested condition (one
    /// branch of 'if', 'switch' without default, a loop body, a catch, one side of '?:', the right of '&amp;&amp;',
    /// '||' and '??', '?.'). A read inside a nested function counts: the local escapes and may be read anywhere.
    /// A path that throws ('else throw ...;', 'default: throw ...;') leaves the function and does not need to start
    /// the task. Early exits ('if (!c) return; await t;') are not followed, so such code is treated as reading it.
    /// A loop whose condition is the constant true ('while (true)', 'for (;;)') ends only by break, return or throw,
    /// which are not followed either: a read anywhere in its body counts. From a store in a loop body, the path goes
    /// on to the condition (and a for loop's incrementors) of the next iteration, or around the body again when the
    /// condition is the constant true.
    /// </summary>
    public static bool IsDefinitelyReadAfter(StatementSyntax statement, ILocalSymbol local, SyntaxNode scope, SemanticModel model, CancellationToken cancellationToken)
    {
        var reader = new DefiniteReader(local, model, cancellationToken);
        SyntaxNode node = statement;
        while (node != null && node != scope && !IsFunction(node))
        {
            var parent = node.Parent;
            switch (parent)
            {
                case BlockSyntax block:
                    if (reader.AnyAfter(block.Statements, node)) return true;
                    break;
                case WhileStatementSyntax whileStatement when whileStatement.Statement == node:
                    if (reader.Expression(whileStatement.Condition) || reader.RepeatsReading(whileStatement.Condition, whileStatement.Statement)) return true;
                    break;
                case DoStatementSyntax doStatement when doStatement.Statement == node:
                    if (reader.Expression(doStatement.Condition) || reader.RepeatsReading(doStatement.Condition, doStatement.Statement)) return true;
                    break;
                case ForStatementSyntax forStatement when forStatement.Statement == node:
                    foreach (var incrementor in forStatement.Incrementors)
                    {
                        if (reader.Expression(incrementor)) return true;
                    }

                    if ((forStatement.Condition != null && reader.Expression(forStatement.Condition)) ||
                        reader.RepeatsReading(forStatement.Condition, forStatement.Statement)) return true;
                    break;
                case SwitchSectionSyntax section:
                    if (reader.AnyAfter(section.Statements, node)) return true;
                    parent = section.Parent; // leave the switch statement, not the other sections
                    break;
                case GlobalStatementSyntax global when global.Parent is CompilationUnitSyntax unit:
                    var passed = false;
                    foreach (var member in unit.Members)
                    {
                        if (passed && member is GlobalStatementSyntax next && reader.Statement(next.Statement)) return true;
                        if (member == global) passed = true;
                    }

                    return false;
            }

            node = parent;
        }

        return false;
    }

    static bool IsFunction(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax or
            AccessorDeclarationSyntax;

    /// <summary>Whether a statement or expression reads a local on every path through it (see IsDefinitelyReadAfter).</summary>
    readonly struct DefiniteReader
    {
        readonly ILocalSymbol _local;
        readonly SemanticModel _model;
        readonly CancellationToken _cancellationToken;

        public DefiniteReader(ILocalSymbol local, SemanticModel model, CancellationToken cancellationToken)
        {
            _local = local;
            _model = model;
            _cancellationToken = cancellationToken;
        }

        public bool AnyAfter(SyntaxList<StatementSyntax> statements, SyntaxNode after)
        {
            var passed = false;
            foreach (var s in statements)
            {
                if (passed && Statement(s)) return true;
                if (s == after) passed = true;
            }

            return false;
        }

        bool Any(SyntaxList<StatementSyntax> statements)
        {
            foreach (var s in statements)
            {
                if (Statement(s)) return true;
            }

            return false;
        }

        public bool Statement(StatementSyntax statement)
        {
            switch (statement)
            {
                case null:
                    return false;
                case BlockSyntax block:
                    return Any(block.Statements);
                case ExpressionStatementSyntax expression:
                    return Expression(expression.Expression);
                case LocalDeclarationStatementSyntax declaration:
                    return Expression(declaration.Declaration);
                case IfStatementSyntax ifStatement:
                    return Expression(ifStatement.Condition) ||
                           (ifStatement.Else != null && Statement(ifStatement.Statement) && Statement(ifStatement.Else.Statement));
                case SwitchStatementSyntax switchStatement:
                    return Expression(switchStatement.Expression) || EverySectionReads(switchStatement);
                case WhileStatementSyntax whileStatement:
                    // The body may not run, unless the condition is the constant true.
                    return Expression(whileStatement.Condition) || RepeatsReading(whileStatement.Condition, whileStatement.Statement);
                case DoStatementSyntax doStatement:
                    return Statement(doStatement.Statement) || Expression(doStatement.Condition) ||
                           RepeatsReading(doStatement.Condition, doStatement.Statement);
                case ForStatementSyntax forStatement:
                    if (forStatement.Declaration != null && Expression(forStatement.Declaration)) return true;
                    foreach (var initializer in forStatement.Initializers)
                    {
                        if (Expression(initializer)) return true;
                    }

                    return (forStatement.Condition != null && Expression(forStatement.Condition)) ||
                           RepeatsReading(forStatement.Condition, forStatement.Statement);
                case CommonForEachStatementSyntax forEach:
                    return Expression(forEach.Expression);
                case TryStatementSyntax tryStatement:
                    return Statement(tryStatement.Block) || (tryStatement.Finally != null && Statement(tryStatement.Finally.Block));
                case UsingStatementSyntax usingStatement:
                    return (usingStatement.Declaration != null && Expression(usingStatement.Declaration)) ||
                           (usingStatement.Expression != null && Expression(usingStatement.Expression)) ||
                           Statement(usingStatement.Statement);
                case LockStatementSyntax lockStatement:
                    return Expression(lockStatement.Expression) || Statement(lockStatement.Statement);
                case FixedStatementSyntax fixedStatement:
                    return Expression(fixedStatement.Declaration) || Statement(fixedStatement.Statement);
                case CheckedStatementSyntax checkedStatement:
                    return Statement(checkedStatement.Block);
                case UnsafeStatementSyntax unsafeStatement:
                    return Statement(unsafeStatement.Block);
                case LabeledStatementSyntax labeled:
                    return Statement(labeled.Statement);
                case ReturnStatementSyntax returnStatement:
                    return returnStatement.Expression != null && Expression(returnStatement.Expression);
                case ThrowStatementSyntax _:
                    return true; // the path leaves the function by an exception: nothing to start on it
                case YieldStatementSyntax yieldStatement:
                    return yieldStatement.Expression != null && Expression(yieldStatement.Expression);
                case LocalFunctionStatementSyntax localFunction:
                    return References(localFunction); // captured: it escapes
                default:
                    return false;
            }
        }

        bool EverySectionReads(SwitchStatementSyntax switchStatement)
        {
            var hasDefault = false;
            foreach (var section in switchStatement.Sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label.IsKind(SyntaxKind.DefaultSwitchLabel) || MatchesEverything(label)) hasDefault = true;
                }

                if (!Any(section.Statements)) return false;
            }

            return hasDefault;
        }

        /// <summary>
        /// A 'case' label that matches every value, as 'default' does: a 'var' pattern with a discard or one variable
        /// ('case var _:', 'case var x:'), without 'when'. ('case _:' names a constant '_' in a switch statement.)
        /// </summary>
        static bool MatchesEverything(SwitchLabelSyntax label)
        {
            if (label is not CasePatternSwitchLabelSyntax casePattern || casePattern.WhenClause != null) return false;
            return casePattern.Pattern switch
            {
                VarPatternSyntax varPattern => varPattern.Designation is not ParenthesizedVariableDesignationSyntax,
                DeclarationPatternSyntax declaration => declaration.Type.IsVar && declaration.Designation is not ParenthesizedVariableDesignationSyntax,
                _ => false,
            };
        }

        /// <summary>
        /// A loop whose condition is the constant true (or a 'for' without one) and whose body reads the local
        /// somewhere: the loop is left only by break, return or throw, which are not followed (see IsDefinitelyReadAfter).
        /// </summary>
        public bool RepeatsReading(ExpressionSyntax condition, StatementSyntax body)
        {
            if (condition != null)
            {
                var constant = _model.GetConstantValue(condition, _cancellationToken);
                if (!constant.HasValue || constant.Value is not bool value || !value) return false;
            }

            foreach (var node in body.DescendantNodes())
            {
                if (IsReference(node, _local, _model, _cancellationToken, out var identifier) && !IsWriteOnly(identifier)) return true;
            }

            return false;
        }

        public bool Expression(SyntaxNode node)
        {
            switch (node)
            {
                case IdentifierNameSyntax identifier:
                    return IsReference(identifier, _local, _model, _cancellationToken, out _) && !IsWriteOnly(identifier);
                case AnonymousFunctionExpressionSyntax lambda:
                    return References(lambda); // captured: it escapes
                case ThrowExpressionSyntax _:
                    return true; // 'c ? t : throw ...': that side leaves the function
                case ConditionalExpressionSyntax conditional:
                    return Expression(conditional.Condition) || (Expression(conditional.WhenTrue) && Expression(conditional.WhenFalse));
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                                                        binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                                                        binary.IsKind(SyntaxKind.CoalesceExpression):
                    return Expression(binary.Left);
                case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                    return Expression(assignment.Left);
                case ConditionalAccessExpressionSyntax conditionalAccess:
                    return Expression(conditionalAccess.Expression);
                case SwitchExpressionSyntax switchExpression:
                    if (Expression(switchExpression.GoverningExpression)) return true;
                    if (switchExpression.Arms.Count == 0) return false;
                    foreach (var arm in switchExpression.Arms)
                    {
                        if (!Expression(arm.Expression)) return false;
                    }

                    return true;
                default:
                    foreach (var child in node.ChildNodes())
                    {
                        if (Expression(child)) return true;
                    }

                    return false;
            }
        }

        bool References(SyntaxNode node)
        {
            foreach (var descendant in node.DescendantNodes())
            {
                if (IsReference(descendant, _local, _model, _cancellationToken, out _)) return true;
            }

            return false;
        }
    }
}
