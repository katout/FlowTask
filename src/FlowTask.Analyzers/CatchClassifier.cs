using Microsoft.CodeAnalysis.Operations;

namespace Katout.FlowTask.Analyzers;

/// <summary>
/// Whether the cancellation of a FlowTask method (FlowCanceledException, thrown only by the GetResult of a FlowTask
/// awaiter) can enter a catch clause, and whether the clause names the cancellation itself. Judged from the types, and
/// a 'when' filter from its IOperation tree (what the compiler bound, whatever the syntax), so it follows whatever base
/// type FlowCanceledException has. Used by FLOW001 and its code fix, and (<see cref="RunsOnUnwind"/>) by FLOW011.
/// </summary>
internal static class CatchClassifier
{
    /// <summary>The value of a 'when' filter when the exception is FlowCanceledException.</summary>
    enum Truth
    {
        Unknown,
        True,
        False,
    }

    /// <summary>
    /// True when FlowCanceledException, thrown at an await of the try block, can enter <paramref name="clause"/>: the
    /// try block awaits (outside nested functions), the caught type can hold it, no earlier clause without a filter
    /// takes it first, and the 'when' filter is not provably false for it.
    /// </summary>
    public static bool ReceivesCancellation(CatchClauseSyntax clause, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        if (clause.Parent is not TryStatementSyntax tryStatement || !ContainsAwait(tryStatement.Block)) return false;
        if (clause.Declaration != null && !CanHoldCancellation(CaughtType(clause, model, cancellationToken), types)) return false;

        foreach (var earlier in tryStatement.Catches)
        {
            if (earlier == clause) break;
            if (earlier.Filter != null) continue;
            if (earlier.Declaration == null) return false;
            var earlierType = CaughtType(earlier, model, cancellationToken);
            if (earlierType is not ITypeParameterSymbol && CanHoldCancellation(earlierType, types)) return false;
        }

        return clause.Filter == null || EvaluateFilter(clause, model, types, inOrder: false, cancellationToken) != Truth.False;
    }

    /// <summary>
    /// True when FlowCanceledException, thrown at an await of the try block, may enter <paramref name="clause"/>, judged
    /// as FlowAssert.CanSkipUnwinding (FlowTask.Testing) judges the clause in the compiled code: a catch without a type
    /// or of a type that can hold it, any catch of a type parameter (its constraints are not read), and a 'when' filter
    /// unless it is known to be false for it when followed in the order it runs (a call or a field before the type test,
    /// or a non-short-circuit '&amp;' or '|', is not known). Earlier clauses are not looked at: one that takes the
    /// cancellation first is such a clause itself.
    /// </summary>
    public static bool RunsOnUnwind(CatchClauseSyntax clause, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        if (clause.Declaration != null)
        {
            var type = CaughtType(clause, model, cancellationToken);
            if (type is null or ITypeParameterSymbol) return true;
            if (!CanHoldCancellation(type, types)) return false;
        }

        return clause.Filter == null || EvaluateFilter(clause, model, types, inOrder: true, cancellationToken) != Truth.False;
    }

    /// <summary>
    /// The value of the 'when' filter of <paramref name="clause"/> when the exception is FlowCanceledException, from the
    /// filter's IOperation tree: type tests and patterns on the catch variable ('e is not FlowCanceledException',
    /// '!(e is FlowCanceledException)', 'e is IOException { … }', 'e is not (A or B)'), '!', '&amp;&amp;', '||', '&amp;',
    /// '|' and constants. Anything else (a method call, a property of the exception) is unknown. With
    /// <paramref name="inOrder"/>, the operands of '&amp;&amp;', '||' and the pattern combinators are taken in the order
    /// they run, so an unknown one before a known one makes the whole unknown, and '&amp;' and '|' are unknown; and, as
    /// the check follows the comparisons and branches of the compiled filter, '==' and '!=' of two bools or of the catch
    /// variable and null, 'c ? a : b', and 'is true', 'is false' and 'is not …' of a bool are known when their operands are.
    /// </summary>
    static Truth EvaluateFilter(CatchClauseSyntax clause, SemanticModel model, FlowTaskTypes types, bool inOrder, CancellationToken cancellationToken)
    {
        var variable = clause.Declaration != null ? model.GetDeclaredSymbol(clause.Declaration, cancellationToken) : null;
        var operation = model.GetOperation(clause.Filter.FilterExpression, cancellationToken);
        return operation == null ? Truth.Unknown : Evaluate(operation, variable, types, inOrder);
    }

    /// <summary>
    /// True when <paramref name="clause"/> names the cancellation: FlowCanceledException, or a base type of it that is
    /// not a catch-all (System.Exception, System.SystemException, object) nor a type parameter, i.e.
    /// OperationCanceledException once FlowCanceledException derives from it.
    /// </summary>
    public static bool IsCancellationClause(CatchClauseSyntax clause, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        if (clause.Declaration == null) return false;
        var type = CaughtType(clause, model, cancellationToken);
        if (type == null || type is ITypeParameterSymbol || type.SpecialType == SpecialType.System_Object) return false;
        var name = type.ToDisplayString();
        if (name is "System.Exception" or "System.SystemException") return false;
        return CanHoldCancellation(type, types);
    }

    static ITypeSymbol CaughtType(CatchClauseSyntax clause, SemanticModel model, CancellationToken cancellationToken)
    {
        var type = model.GetTypeInfo(clause.Declaration.Type, cancellationToken).Type;
        return type is null or { TypeKind: TypeKind.Error } ? null : type;
    }

    /// <summary>
    /// An 'await', 'await foreach' or 'await using' in <paramref name="block"/>, outside nested functions: where
    /// FlowCanceledException can be thrown, in a catch or finally block too (while FlowWorld.Dispose ends the flow).
    /// </summary>
    static bool ContainsAwait(BlockSyntax block)
    {
        foreach (var node in block.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
        {
            switch (node)
            {
                case AwaitExpressionSyntax _:
                    return true;
                case CommonForEachStatementSyntax forEach when forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    return true;
                case UsingStatementSyntax usingStatement when usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    return true;
                case LocalDeclarationStatementSyntax declaration when declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A catch type that FlowCanceledException converts to. A type parameter can hold it when every constraint can
    /// ('where TEx : Exception' can, 'where TEx : IOException' cannot).
    /// </summary>
    static bool CanHoldCancellation(ITypeSymbol type, FlowTaskTypes types)
    {
        if (type == null) return false;
        if (type is ITypeParameterSymbol typeParameter)
        {
            foreach (var constraint in typeParameter.ConstraintTypes)
            {
                if (!CanHoldCancellation(constraint, types)) return false;
            }

            return true;
        }

        var cancellation = types.FlowCanceledException;
        if (cancellation == null) return types.CanCatchFlowCanceledException(type);
        for (ITypeSymbol t = cancellation; t != null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, type)) return true;
        }

        foreach (var i in cancellation.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(i, type)) return true;
        }

        return false;
    }

    static Truth Evaluate(IOperation operation, ILocalSymbol variable, FlowTaskTypes types, bool inOrder)
    {
        if (operation.ConstantValue.HasValue && operation.ConstantValue.Value is bool constant) return constant ? Truth.True : Truth.False;
        switch (operation)
        {
            case IParenthesizedOperation parenthesized:
                return Evaluate(parenthesized.Operand, variable, types, inOrder);
            case IConversionOperation conversion when conversion.IsImplicit:
                return Evaluate(conversion.Operand, variable, types, inOrder);
            case IUnaryOperation unary when unary.OperatorKind == UnaryOperatorKind.Not:
                return Not(Evaluate(unary.Operand, variable, types, inOrder));
            case IBinaryOperation binary when inOrder && binary.OperatorKind is BinaryOperatorKind.And or BinaryOperatorKind.Or:
                // Both operands run, and an instruction the check does not follow combines their values.
                return Truth.Unknown;
            case IBinaryOperation binary when binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And:
                return And(Evaluate(binary.LeftOperand, variable, types, inOrder), () => Evaluate(binary.RightOperand, variable, types, inOrder), inOrder);
            case IBinaryOperation binary when binary.OperatorKind is BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.Or:
                return Or(Evaluate(binary.LeftOperand, variable, types, inOrder), () => Evaluate(binary.RightOperand, variable, types, inOrder), inOrder);
            case IIsTypeOperation isType when IsVariable(isType.ValueOperand, variable):
                var test = TypeTest(isType.TypeOperand, types);
                return isType.IsNegated ? Not(test) : test;
            case IIsPatternOperation isPattern when IsVariable(isPattern.Value, variable):
                return EvaluatePattern(isPattern.Pattern, types, inOrder);
            case IBinaryOperation binary when inOrder && binary.OperatorMethod == null && binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals:
                var equal = Equality(binary.LeftOperand, binary.RightOperand, variable, types);
                return binary.OperatorKind == BinaryOperatorKind.Equals ? equal : Not(equal);
            case IConditionalOperation conditional when inOrder && conditional.WhenFalse != null:
                // 'c ? a : b' runs c, then one of a and b.
                var condition = Evaluate(conditional.Condition, variable, types, inOrder);
                return condition == Truth.Unknown ? Truth.Unknown : Evaluate(condition == Truth.True ? conditional.WhenTrue : conditional.WhenFalse, variable, types, inOrder);
            case IIsPatternOperation boolPattern when inOrder && IsBoolean(boolPattern.Value):
                return MatchBoolean(Evaluate(boolPattern.Value, variable, types, inOrder), boolPattern.Pattern);
            default:
                return Truth.Unknown;
        }
    }

    /// <summary>
    /// 'a == b' (in order): of two bools, both known; of the catch variable and null, false (the exception is never null).
    /// </summary>
    static Truth Equality(IOperation left, IOperation right, ILocalSymbol variable, FlowTaskTypes types)
    {
        if (IsBoolean(left) && IsBoolean(right))
        {
            var l = Evaluate(left, variable, types, inOrder: true);
            if (l == Truth.Unknown) return Truth.Unknown;
            var r = Evaluate(right, variable, types, inOrder: true);
            return r == Truth.Unknown ? Truth.Unknown : l == r ? Truth.True : Truth.False;
        }

        if ((IsVariable(left, variable) && IsNull(right)) || (IsNull(left) && IsVariable(right, variable))) return Truth.False;
        return Truth.Unknown;
    }

    /// <summary>A bool, known or not, against a pattern: 'is true', 'is false', 'is not …' and '_'; others are unknown.</summary>
    static Truth MatchBoolean(Truth value, IPatternOperation pattern) => pattern switch
    {
        IDiscardPatternOperation _ => Truth.True,
        INegatedPatternOperation negated => Not(MatchBoolean(value, negated.Pattern)),
        IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: bool expected } } when value != Truth.Unknown =>
            (value == Truth.True) == expected ? Truth.True : Truth.False,
        _ => Truth.Unknown,
    };

    static bool IsBoolean(IOperation operation) => operation.Type?.SpecialType == SpecialType.System_Boolean;

    static bool IsNull(IOperation operation)
    {
        while (operation is IConversionOperation conversion && conversion.IsImplicit) operation = conversion.Operand;
        return operation.ConstantValue.HasValue && operation.ConstantValue.Value == null;
    }

    static Truth EvaluatePattern(IPatternOperation pattern, FlowTaskTypes types, bool inOrder)
    {
        switch (pattern)
        {
            case INegatedPatternOperation negated:
                return Not(EvaluatePattern(negated.Pattern, types, inOrder));
            case IBinaryPatternOperation binary when binary.OperatorKind == BinaryOperatorKind.And:
                return And(EvaluatePattern(binary.LeftPattern, types, inOrder), () => EvaluatePattern(binary.RightPattern, types, inOrder), inOrder);
            case IBinaryPatternOperation binary when binary.OperatorKind == BinaryOperatorKind.Or:
                return Or(EvaluatePattern(binary.LeftPattern, types, inOrder), () => EvaluatePattern(binary.RightPattern, types, inOrder), inOrder);
            case ITypePatternOperation typePattern:
                return TypeTest(typePattern.MatchedType, types);
            case IDeclarationPatternOperation declaration:
                // 'var x' matches everything (MatchesNull); 'T x' tests the type.
                return declaration.MatchesNull ? Truth.True : TypeTest(declaration.MatchedType, types);
            case IRecursivePatternOperation recursive:
                // 'e is IOException { … }' is false when the type does not match; the subpatterns are not evaluated.
                var typeTest = TypeTest(recursive.MatchedType, types);
                var hasSubpatterns = !recursive.DeconstructionSubpatterns.IsEmpty || !recursive.PropertySubpatterns.IsEmpty;
                return typeTest == Truth.False || !hasSubpatterns ? typeTest : Truth.Unknown;
            case IConstantPatternOperation constant:
                // Only 'null' is known: the exception is never null.
                return constant.Value.ConstantValue.HasValue && constant.Value.ConstantValue.Value == null ? Truth.False : Truth.Unknown;
            case IDiscardPatternOperation _:
                return Truth.True;
            default:
                return Truth.Unknown;
        }
    }

    /// <summary>A read of the catch variable (through implicit conversions).</summary>
    static bool IsVariable(IOperation operation, ILocalSymbol variable)
    {
        while (operation is IConversionOperation conversion && conversion.IsImplicit) operation = conversion.Operand;
        while (operation is IParenthesizedOperation parenthesized) operation = parenthesized.Operand;
        return variable != null && operation is ILocalReferenceOperation local && SymbolEqualityComparer.Default.Equals(local.Local, variable);
    }

    static Truth TypeTest(ITypeSymbol type, FlowTaskTypes types)
    {
        if (type is null or { TypeKind: TypeKind.Error } or ITypeParameterSymbol) return Truth.Unknown;
        return CanHoldCancellation(type, types) ? Truth.True : Truth.False;
    }

    static Truth Not(Truth value) => value == Truth.True ? Truth.False : value == Truth.False ? Truth.True : Truth.Unknown;

    /// <summary>'&amp;&amp;': false when either side is; in order, an unknown left side is the answer (what runs after it is not known).</summary>
    static Truth And(Truth left, System.Func<Truth> right, bool inOrder)
    {
        if (left == Truth.False || (inOrder && left == Truth.Unknown)) return left;
        var r = right();
        return r == Truth.False ? Truth.False : left == Truth.True && r == Truth.True ? Truth.True : Truth.Unknown;
    }

    /// <summary>'||': true when either side is; in order, an unknown left side is the answer.</summary>
    static Truth Or(Truth left, System.Func<Truth> right, bool inOrder)
    {
        if (left == Truth.True || (inOrder && left == Truth.Unknown)) return left;
        var r = right();
        return r == Truth.True ? Truth.True : left == Truth.False && r == Truth.False ? Truth.False : Truth.Unknown;
    }
}
