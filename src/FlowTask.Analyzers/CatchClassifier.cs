using Microsoft.CodeAnalysis.Operations;

namespace Katout.FlowTask.Analyzers;

/// <summary>
/// Whether the cancellation of a FlowTask method (FlowCanceledException, thrown only by the GetResult of a FlowTask
/// awaiter) can enter a catch clause, and whether the clause names the cancellation itself. Judged from the types, and
/// a 'when' filter from its IOperation tree (what the compiler bound, whatever the syntax), so it follows whatever base
/// type FlowCanceledException has. Used by FLOW001 and its code fix.
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

        return clause.Filter == null || EvaluateFilter(clause, model, types, cancellationToken) != Truth.False;
    }

    /// <summary>
    /// The value of the 'when' filter of <paramref name="clause"/> when the exception is FlowCanceledException, from the
    /// filter's IOperation tree: type tests and patterns on the catch variable ('e is not FlowCanceledException',
    /// '!(e is FlowCanceledException)', 'e is IOException { … }', 'e is not (A or B)'), '!', '&amp;&amp;', '||', '&amp;',
    /// '|' and constants. Anything else (a method call, a property of the exception) is unknown.
    /// </summary>
    static Truth EvaluateFilter(CatchClauseSyntax clause, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        var variable = clause.Declaration != null ? model.GetDeclaredSymbol(clause.Declaration, cancellationToken) : null;
        var operation = model.GetOperation(clause.Filter.FilterExpression, cancellationToken);
        return operation == null ? Truth.Unknown : Evaluate(operation, variable, types);
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

    static Truth Evaluate(IOperation operation, ILocalSymbol variable, FlowTaskTypes types)
    {
        if (operation.ConstantValue.HasValue && operation.ConstantValue.Value is bool constant) return constant ? Truth.True : Truth.False;
        switch (operation)
        {
            case IParenthesizedOperation parenthesized:
                return Evaluate(parenthesized.Operand, variable, types);
            case IConversionOperation conversion when conversion.IsImplicit:
                return Evaluate(conversion.Operand, variable, types);
            case IUnaryOperation unary when unary.OperatorKind == UnaryOperatorKind.Not:
                return Not(Evaluate(unary.Operand, variable, types));
            case IBinaryOperation binary when binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And:
                return And(Evaluate(binary.LeftOperand, variable, types), Evaluate(binary.RightOperand, variable, types));
            case IBinaryOperation binary when binary.OperatorKind is BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.Or:
                return Or(Evaluate(binary.LeftOperand, variable, types), Evaluate(binary.RightOperand, variable, types));
            case IIsTypeOperation isType when IsVariable(isType.ValueOperand, variable):
                var test = TypeTest(isType.TypeOperand, types);
                return isType.IsNegated ? Not(test) : test;
            case IIsPatternOperation isPattern when IsVariable(isPattern.Value, variable):
                return EvaluatePattern(isPattern.Pattern, types);
            default:
                return Truth.Unknown;
        }
    }

    static Truth EvaluatePattern(IPatternOperation pattern, FlowTaskTypes types)
    {
        switch (pattern)
        {
            case INegatedPatternOperation negated:
                return Not(EvaluatePattern(negated.Pattern, types));
            case IBinaryPatternOperation binary when binary.OperatorKind == BinaryOperatorKind.And:
                return And(EvaluatePattern(binary.LeftPattern, types), EvaluatePattern(binary.RightPattern, types));
            case IBinaryPatternOperation binary when binary.OperatorKind == BinaryOperatorKind.Or:
                return Or(EvaluatePattern(binary.LeftPattern, types), EvaluatePattern(binary.RightPattern, types));
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

    static Truth And(Truth left, Truth right) =>
        left == Truth.False || right == Truth.False ? Truth.False :
        left == Truth.True && right == Truth.True ? Truth.True : Truth.Unknown;

    static Truth Or(Truth left, Truth right) =>
        left == Truth.True || right == Truth.True ? Truth.True :
        left == Truth.False && right == Truth.False ? Truth.False : Truth.Unknown;
}
