using System;
using System.Linq;
using Microsoft.CodeAnalysis.Text;

namespace Katout.FlowTask.Analyzers.CodeFixes;

/// <summary>Syntax helpers shared by the FlowTask code fixes.</summary>
internal static class CodeFixHelpers
{
    const string FlowTaskNamespace = "Katout.FlowTask";

    /// <summary>The ExpressionStatement whose expression has exactly <paramref name="span"/>, or null.</summary>
    public static ExpressionStatementSyntax FindExpressionStatement(SyntaxNode root, TextSpan span)
    {
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var statement = node.FirstAncestorOrSelf<ExpressionStatementSyntax>();
        return statement != null && statement.Expression.Span == span ? statement : null;
    }

    /// <summary>The await expression with exactly <paramref name="span"/>, or null.</summary>
    public static AwaitExpressionSyntax FindAwaitExpression(SyntaxNode root, TextSpan span)
    {
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var awaitExpression = node.FirstAncestorOrSelf<AwaitExpressionSyntax>();
        return awaitExpression != null && awaitExpression.Span == span ? awaitExpression : null;
    }

    /// <summary>
    /// How to spell the Katout.FlowTask type <paramref name="type"/> (Flow, FlowBridge, FlowWorld, ...) at
    /// <paramref name="position"/>: its simple name when it binds (or will bind once 'using Katout.FlowTask;' is
    /// added, then <paramref name="needsUsing"/> is true), otherwise 'global::Katout.FlowTask.Name'.
    /// </summary>
    public static ExpressionSyntax TypeReference(SemanticModel model, int position, INamedTypeSymbol type, out bool needsUsing)
    {
        var info = model.GetSpeculativeSymbolInfo(position, IdentifierName(type.Name), SpeculativeBindingOption.BindAsTypeOrNamespace);
        needsUsing = false;
        if (SymbolEqualityComparer.Default.Equals(info.Symbol, type)) return IdentifierName(type.Name);
        if (info.Symbol == null && info.CandidateSymbols.IsEmpty)
        {
            needsUsing = true;
            return IdentifierName(type.Name);
        }

        return QualifiedType(type.Name);
    }

    /// <summary>'global::Katout.FlowTask.<paramref name="typeName"/>', resolvable anywhere.</summary>
    public static ExpressionSyntax QualifiedType(string typeName)
    {
        // The namespace has two parts: 'global::Katout', then '.FlowTask' as a member access. A single identifier
        // "Katout.FlowTask" would not bind.
        var parts = FlowTaskNamespace.Split('.');
        ExpressionSyntax name = AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), IdentifierName(parts[0]));
        for (var i = 1; i < parts.Length; i++)
            name = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, name, IdentifierName(parts[i]));
        return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, name, IdentifierName(typeName));
    }

    /// <summary>'receiver.Name(arguments)' with normal spacing.</summary>
    public static InvocationExpressionSyntax Invoke(ExpressionSyntax receiver, string name, params ExpressionSyntax[] arguments)
    {
        var list = new List<SyntaxNodeOrToken>();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i > 0) list.Add(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space));
            list.Add(Argument(arguments[i]));
        }

        return InvocationExpression(
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, IdentifierName(name)),
            ArgumentList(SeparatedList<ArgumentSyntax>(list)));
    }

    /// <summary>Wraps <paramref name="expression"/> in parentheses unless it can be the receiver of '.Member' as is.</summary>
    public static ExpressionSyntax AsReceiver(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or
            ElementAccessExpressionSyntax or ParenthesizedExpressionSyntax or ThisExpressionSyntax or BaseExpressionSyntax or
            ObjectCreationExpressionSyntax => expression,
        _ => ParenthesizedExpression(expression),
    };

    /// <summary>'await expression' keeping the expression's leading trivia in front of 'await'.</summary>
    public static AwaitExpressionSyntax Await(ExpressionSyntax expression) =>
        AwaitExpression(Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space), AsReceiver(expression.WithoutTrivia()))
            .WithTriviaFrom(expression);

    /// <summary>'_ = expression', keeping the expression's trivia outside. See <see cref="CanDiscard"/>.</summary>
    public static AssignmentExpressionSyntax Discard(ExpressionSyntax expression)
    {
        // The token must carry the UnderscoreToken contextual kind, or the binder sees a variable named '_'.
        var underscore = Identifier(TriviaList(), SyntaxKind.UnderscoreToken, "_", "_", TriviaList(Space));
        return AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(underscore),
                Token(SyntaxKind.EqualsToken).WithTrailingTrivia(Space),
                expression.WithoutTrivia())
            .WithTriviaFrom(expression);
    }

    /// <summary>
    /// True when '_ = x' discards at <paramref name="position"/>: with a variable, parameter or member named '_' in
    /// scope, it would assign to that instead.
    /// </summary>
    public static bool CanDiscard(SemanticModel model, int position) => model.LookupSymbols(position, name: "_").IsEmpty;

    /// <summary>'FlowWorld.Current', with FlowWorld spelled as <see cref="TypeReference"/> does.</summary>
    public static MemberAccessExpressionSyntax CurrentWorld(SemanticModel model, int position, INamedTypeSymbol flowWorld, out bool needsUsing) =>
        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, TypeReference(model, position, flowWorld, out needsUsing), IdentifierName("Current"));

    /// <summary>Adds 'using Katout.FlowTask;' when the file does not import it yet, keeping usings sorted (System first).</summary>
    public static SyntaxNode AddFlowTaskUsing(SyntaxNode root)
    {
        if (root is not CompilationUnitSyntax unit) return root;
        if (unit.Usings.Any(IsFlowTaskUsing)) return root;

        var newLine = DetectNewLine(unit);
        var directive = UsingDirective(ParseName(FlowTaskNamespace))
            .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space))
            .WithTrailingTrivia(newLine);

        if (unit.Usings.Count == 0)
        {
            // Move the file header (leading trivia of the first member) above the new using.
            var first = unit.Members.FirstOrDefault();
            if (first == null) return unit.WithUsings(SingletonList(directive));
            var header = first.GetLeadingTrivia();
            directive = directive.WithLeadingTrivia(header).WithTrailingTrivia(newLine, newLine);
            return unit.ReplaceNode(first, first.WithLeadingTrivia(SyntaxTriviaList.Empty))
                .WithUsings(SingletonList(directive));
        }

        var usings = unit.Usings;
        var index = usings.Count;
        for (var i = 0; i < usings.Count; i++)
        {
            if (ComesAfterFlowTask(usings[i]))
            {
                index = i;
                break;
            }
        }

        if (index == 0)
        {
            // Keep the file header on the first line.
            var old = usings[0];
            directive = directive.WithLeadingTrivia(old.GetLeadingTrivia());
            usings = usings.Replace(old, old.WithLeadingTrivia(SyntaxTriviaList.Empty));
        }

        return unit.WithUsings(usings.Insert(index, directive));
    }

    static bool IsFlowTaskUsing(UsingDirectiveSyntax u) =>
        u.Alias == null && !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) && u.Name.ToString() == FlowTaskNamespace;

    static bool ComesAfterFlowTask(UsingDirectiveSyntax u)
    {
        if (u.Alias != null || u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) return true;
        var name = u.Name.ToString();
        if (name == "System" || name.StartsWith("System.", StringComparison.Ordinal)) return false;
        // Case-insensitive like the IDE's sort, with the ordinal order only to break ties.
        var order = string.Compare(name, FlowTaskNamespace, StringComparison.OrdinalIgnoreCase);
        return (order != 0 ? order : string.CompareOrdinal(name, FlowTaskNamespace)) > 0;
    }

    /// <summary>The end-of-line trivia the file uses (CRLF when it has none).</summary>
    public static SyntaxTrivia DetectNewLine(SyntaxNode root)
    {
        foreach (var trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia)) return trivia.ToString() == "\n" ? LineFeed : CarriageReturnLineFeed;
        }

        return CarriageReturnLineFeed;
    }

    /// <summary>Picks '<paramref name="baseName"/>', '<paramref name="baseName"/>1', ... not used by any symbol or identifier in scope.</summary>
    public static string UniqueName(string baseName, SemanticModel model, int position, SyntaxNode searchScope)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in model.LookupSymbols(position)) used.Add(symbol.Name);
        if (searchScope != null)
        {
            foreach (var token in searchScope.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken)) used.Add(token.ValueText);
            }
        }

        if (!used.Contains(baseName)) return baseName;
        for (var i = 1; ; i++)
        {
            var candidate = baseName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!used.Contains(candidate)) return candidate;
        }
    }

    public static bool IsUsableIdentifier(string name) =>
        !string.IsNullOrEmpty(name) &&
        SyntaxFacts.IsValidIdentifier(name) &&
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None &&
        SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None;
}
