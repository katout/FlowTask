using System;
using System.Collections.Generic;

namespace Katout.FlowTask.Analyzers;

/// <summary>
/// For FLOW011: the async FlowTask code declared in a type (methods, lambdas and local functions, those of its C# 14
/// extension blocks too; not the named types nested in it, which are types of their own) that has a block around an
/// await that unwinding would run. Only a FlowTask method ends without unwinding: a Task, ValueTask or async void method
/// in the type unwinds, and is not searched. The same judgement as
/// FlowAssert.CanSkipUnwinding (FlowTask.Testing), which reads the exception regions of the compiled state machines,
/// made on the source: the blocks the compiler makes a region of, and the catch clauses of
/// <see cref="CatchClassifier.RunsOnUnwind"/>. tests/FlowTask.Analyzers.Tests/UnwindShapes.cs holds the shapes both are
/// held to.
/// </summary>
internal static class CleanupAroundAwait
{
    /// <summary>What <see cref="Find"/> found: the names of the code, sorted, and where each block begins.</summary>
    internal readonly struct Found
    {
        internal Found(ImmutableArray<string> names, ImmutableArray<Location> blocks)
        {
            Names = names;
            Blocks = blocks;
        }

        /// <summary>
        /// The methods and local functions, quoted ("'Run'"); a lambda is named by the method, local function or field
        /// it is written in.
        /// </summary>
        internal ImmutableArray<string> Names { get; }

        internal ImmutableArray<Location> Blocks { get; }

        internal bool IsEmpty => Names.IsDefaultOrEmpty;
    }

    /// <summary>
    /// Searches every declaration of <paramref name="type"/> (a definition with source). <paramref name="known"/> is the
    /// semantic model of one of the trees, used for the declarations in it.
    /// </summary>
    internal static Found Find(INamedTypeSymbol type, Compilation compilation, SemanticModel known, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var blocks = new List<Location>();
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax(cancellationToken);
            SemanticModel model = null;
            foreach (var node in declaration.DescendantNodes(n => n == declaration || !IsTypeOfItsOwn(n)))
            {
                if (!SuspendsAt(node)) continue;
                model ??= known != null && known.SyntaxTree == declaration.SyntaxTree ? known : compilation.GetSemanticModel(declaration.SyntaxTree);
                // Only a FlowTask method ends without unwinding: a Task, ValueTask or async void method unwinds in any type.
                if (!FunctionContext.Get(node, model, types, cancellationToken).IsFlowTaskMethod) continue;

                var block = EnclosingBlock(node, model, types, cancellationToken);
                if (block == null) continue;
                names.Add(Name(FunctionContext.FindContainingFunction(node), type));
                blocks.Add(block.Value.GetLocation());
            }
        }

        return new Found(ImmutableArray.CreateRange(names), blocks.ToImmutableArray());
    }

    /// <summary>
    /// Where an async function waits: an await, and the awaits the compiler writes for 'await foreach', 'await using' and
    /// 'await using var' (the disposal, where the statement ends).
    /// </summary>
    static bool SuspendsAt(SyntaxNode node) => node switch
    {
        AwaitExpressionSyntax _ => true,
        CommonForEachStatementSyntax forEach => forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
        UsingStatementSyntax usingStatement => usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
        LocalDeclarationStatementSyntax declaration => declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
        _ => false,
    };

    /// <summary>
    /// The keyword of the innermost block around <paramref name="node"/>, in its function, that unwinding would run;
    /// null when there is none. 'await foreach' waits inside its own block (MoveNextAsync) when it disposes its
    /// enumerator, and 'await using' of two or more resources awaits the disposal of each after the first inside the
    /// blocks of the ones before it; the others wait outside their own blocks.
    /// </summary>
    static SyntaxToken? EnclosingBlock(SyntaxNode node, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case CommonForEachStatementSyntax awaitForEach when awaitForEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) && Disposes(awaitForEach, model, cancellationToken):
                return awaitForEach.ForEachKeyword;
            case UsingStatementSyntax usingStatement when usingStatement.Declaration?.Variables.Count > 1:
                return usingStatement.UsingKeyword;
            case LocalDeclarationStatementSyntax declaration when declaration.Declaration.Variables.Count > 1:
                return declaration.UsingKeyword;
        }

        // Find only asks for the code of a FlowTask method: the walk ends at its function.
        var function = FunctionContext.FindContainingFunction(node);
        for (SyntaxNode child = node, parent = node.Parent; parent != null && child != function && !IsTypeOfItsOwn(parent); child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                case TryStatementSyntax tryStatement when tryStatement.Block == child:
                    if (tryStatement.Finally != null) return tryStatement.Finally.FinallyKeyword;
                    foreach (var clause in tryStatement.Catches)
                    {
                        if (CatchClassifier.RunsOnUnwind(clause, model, types, cancellationToken)) return clause.CatchKeyword;
                    }

                    break;
                case TryStatementSyntax tryStatement when child is CatchClauseSyntax && tryStatement.Finally != null:
                    // An await of a catch clause leaves the clause, not the try block of the finally around both.
                    return tryStatement.Finally.FinallyKeyword;
                case UsingStatementSyntax usingStatement when usingStatement.Statement == child:
                    return usingStatement.UsingKeyword;
                case VariableDeclarationSyntax declaration when child is VariableDeclaratorSyntax declarator && declaration.Variables.IndexOf(declarator) > 0 && UsingOf(declaration) is SyntaxToken keyword:
                    // 'using (A a = …, b = …)' and 'using A a = …, b = …;' put each declarator in the try block of
                    // the ones before it.
                    return keyword;
                case CommonForEachStatementSyntax forEach when forEach.Statement == child && Disposes(forEach, model, cancellationToken):
                    return forEach.ForEachKeyword;
                case BlockSyntax block when child is StatementSyntax statement:
                    // 'using var' (and 'await using var') covers the statements after it in its block, labeled or not.
                    foreach (var earlier in block.Statements)
                    {
                        if (earlier == statement) break;
                        var unlabeled = earlier;
                        while (unlabeled is LabeledStatementSyntax labeled) unlabeled = labeled.Statement;
                        if (unlabeled is LocalDeclarationStatementSyntax declaration && declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return declaration.UsingKeyword;
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>The 'using' keyword of a using statement or declaration that declares <paramref name="declaration"/>.</summary>
    static SyntaxToken? UsingOf(VariableDeclarationSyntax declaration) => declaration.Parent switch
    {
        UsingStatementSyntax usingStatement => usingStatement.UsingKeyword,
        LocalDeclarationStatementSyntax local when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) => local.UsingKeyword,
        _ => null,
    };

    /// <summary>
    /// A type declaration whose members are not those of the type around it: every one but a C# 14 extension block
    /// ('extension(string s) { … }'), whose members the compiler emits in the static class it is written in, so the
    /// runtime counts their state machines as that class's. Roslyn 3.8 has no type for the block: its name is read.
    /// </summary>
    static bool IsTypeOfItsOwn(SyntaxNode node) =>
        node is BaseTypeDeclarationSyntax && node.GetType().Name != "ExtensionBlockDeclarationSyntax";

    /// <summary>
    /// A foreach the compiler puts in a try block with a finally that disposes the enumerator. A foreach: not over an
    /// array or a string (a loop over the elements), and with an enumerator that is or may be disposable (an interface or
    /// a class that is not sealed is checked at run time). An 'await foreach': with an enumerator that has DisposeAsync or
    /// implements IAsyncDisposable (nothing is checked at run time).
    /// </summary>
    static bool Disposes(CommonForEachStatementSyntax forEach, SemanticModel model, CancellationToken cancellationToken)
    {
        var isAsync = forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword);
        if (!isAsync)
        {
            var collection = model.GetTypeInfo(forEach.Expression, cancellationToken).Type;
            if (collection is IArrayTypeSymbol || collection?.SpecialType == SpecialType.System_String) return false;
        }

        var info = model.GetForEachStatementInfo(forEach);
        if (info.DisposeMethod != null) return true;
        var enumerator = info.GetEnumeratorMethod?.ReturnType;
        if (enumerator == null) return true;
        if (isAsync) return IsOrImplementsAsyncDisposable(enumerator);
        if (enumerator.SpecialType == SpecialType.System_IDisposable) return true;
        foreach (var i in enumerator.AllInterfaces)
        {
            if (i.SpecialType == SpecialType.System_IDisposable) return true;
        }

        return !enumerator.IsSealed && !enumerator.IsValueType;
    }

    static bool IsOrImplementsAsyncDisposable(ITypeSymbol type)
    {
        if (IsAsyncDisposable(type)) return true;
        foreach (var i in type.AllInterfaces)
        {
            if (IsAsyncDisposable(i)) return true;
        }

        return false;
    }

    static bool IsAsyncDisposable(ITypeSymbol type) =>
        type.Name == "IAsyncDisposable" && type.ContainingNamespace is { Name: "System" } ns && ns.ContainingNamespace.IsGlobalNamespace;

    /// <summary>The name of a function, as <see cref="Found.Names"/> gives it.</summary>
    static string Name(SyntaxNode function, INamedTypeSymbol type)
    {
        switch (function)
        {
            case MethodDeclarationSyntax method:
                return Quote(method.Identifier.ValueText);
            case LocalFunctionStatementSyntax local:
                return Quote(local.Identifier.ValueText);
            case AnonymousFunctionExpressionSyntax lambda:
                var outer = FunctionContext.FindContainingFunction(lambda);
                if (outer != null) return Name(outer, type);
                foreach (var ancestor in lambda.Ancestors())
                {
                    switch (ancestor)
                    {
                        case VariableDeclaratorSyntax field:
                            return Quote(field.Identifier.ValueText);
                        case PropertyDeclarationSyntax property:
                            return Quote(property.Identifier.ValueText);
                        case MemberDeclarationSyntax _:
                            return Quote(type.Name);
                    }
                }

                return Quote(type.Name);
            case ConstructorDeclarationSyntax constructor:
                return Quote(constructor.Identifier.ValueText);
            case AccessorDeclarationSyntax accessor when accessor.Parent?.Parent is BasePropertyDeclarationSyntax property:
                return Name(property, type);
            case PropertyDeclarationSyntax property:
                return Quote(property.Identifier.ValueText);
            default:
                return Quote(type.Name);
        }
    }

    static string Quote(string name) => "'" + name + "'";
}
