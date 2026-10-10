using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis.Operations;

namespace Katout.FlowTask.Analyzers.CodeFixes;

/// <summary>
/// FLOW002, two fixes:
/// <list type="number">
/// <item>'await Load(x)' becomes 'await FlowBridge.FromTask(ct => Load(x, ct))' when the call can take the scope's
/// token: an optional CancellationToken parameter left out, a 'default' / CancellationToken.None argument, or an
/// overload with a trailing CancellationToken. A ValueTask gets '.AsTask()'.</item>
/// <item>'await task' becomes 'await task.AsFlow()' (also through ConfigureAwait, and for other awaitables whose AsFlow
/// extension is in scope). The task keeps running when the scope is canceled, so this fix comes second.</item>
/// </list>
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BridgeAwaitCodeFixProvider)), Shared]
public sealed class BridgeAwaitCodeFixProvider : CodeFixProvider
{
    public const string FromTaskEquivalenceKey = "FLOW002.FromTask";
    public const string AsFlowEquivalenceKey = "FLOW002.AsFlow";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.ForeignAwaitInFlowTask);

    public override FixAllProvider GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var types = FlowTaskTypes.TryCreate(model.Compilation);
        if (types?.FlowBridge == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var awaitExpression = CodeFixHelpers.FindAwaitExpression(root, diagnostic.Location.SourceSpan);
            if (awaitExpression == null) continue;

            var task = GetBridgeableTask(awaitExpression.Expression, model, types, context.CancellationToken);
            if (task == null) continue;

            var fromTask = await BuildFromTaskFixAsync(context.Document, root, model, types, awaitExpression, task, context.CancellationToken).ConfigureAwait(false);
            if (fromTask != null)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(Resources.CodeFixBridgeWithFromTask, _ => Task.FromResult(fromTask), equivalenceKey: FromTaskEquivalenceKey),
                    diagnostic);
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    Resources.CodeFixBridgeWithAsFlow,
                    ct => ApplyAsFlowAsync(context.Document, awaitExpression, task, ct),
                    equivalenceKey: AsFlowEquivalenceKey),
                diagnostic);
        }
    }

    /// <summary>
    /// The awaited operation to bridge: the operand itself, or the receiver of a trailing ConfigureAwait(...) on a
    /// Task/ValueTask. Null when it is neither a Task/ValueTask nor a type with an AsFlow extension in scope.
    /// </summary>
    static ExpressionSyntax GetBridgeableTask(ExpressionSyntax operand, SemanticModel model, FlowTaskTypes types, CancellationToken cancellationToken)
    {
        var type = model.GetTypeInfo(operand, cancellationToken).Type;
        if (types.IsTaskLike(type)) return operand;

        if (operand is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name.Identifier.ValueText == "ConfigureAwait" &&
            model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
            types.IsTaskLike(method.ContainingType) &&
            types.IsTaskLike(model.GetTypeInfo(memberAccess.Expression, cancellationToken).Type))
        {
            return memberAccess.Expression;
        }

        // An AsFlow extension that already binds here: UniTask, Unity's Awaitable and AsyncOperation, Godot's SignalAwaiter.
        return type is INamespaceOrTypeSymbol container &&
               !model.LookupSymbols(operand.SpanStart, container, "AsFlow", includeReducedExtensionMethods: true).IsEmpty
            ? operand
            : null;
    }

    static async Task<Document> ApplyAsFlowAsync(Document document, AwaitExpressionSyntax awaitExpression, ExpressionSyntax task, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        var operand = awaitExpression.Expression;
        var bridged = CodeFixHelpers.Invoke(CodeFixHelpers.AsReceiver(task.WithoutTrivia()), "AsFlow").WithTriviaFrom(operand);

        // FlowBridge.AsFlow is an extension method in Katout.FlowTask: add the using unless an AsFlow already binds here.
        var binds = model.GetSpeculativeSymbolInfo(awaitExpression.SpanStart, bridged, SpeculativeBindingOption.BindAsExpression).Symbol is IMethodSymbol;

        var newRoot = root.ReplaceNode(operand, bridged);
        if (!binds) newRoot = CodeFixHelpers.AddFlowTaskUsing(newRoot);
        return document.WithSyntaxRoot(newRoot);
    }

    // ------------------------------------------------------------------ FlowBridge.FromTask

    /// <summary>
    /// The document with 'await FlowBridge.FromTask(ct => call(..., ct))', or null when the awaited Task/ValueTask is not
    /// a call that can take the token, when it already receives another token, or when the member would no longer
    /// compile (a struct's 'this' cannot be captured in the lambda; an 'out var' argument used after the await).
    /// </summary>
    static async Task<Document> BuildFromTaskFixAsync(
        Document document,
        SyntaxNode root,
        SemanticModel model,
        FlowTaskTypes types,
        AwaitExpressionSyntax awaitExpression,
        ExpressionSyntax task,
        CancellationToken cancellationToken)
    {
        if (task is not InvocationExpressionSyntax call) return null;
        var taskType = model.GetTypeInfo(call, cancellationToken).Type;
        if (!types.IsTaskLike(taskType)) return null;

        var function = FunctionContext.FindContainingFunction(awaitExpression);
        var tokenName = CodeFixHelpers.UniqueName("ct", model, awaitExpression.SpanStart, function);
        var callWithToken = PassToken(call, IdentifierName(tokenName), model, cancellationToken);
        if (callWithToken == null) return null;

        ExpressionSyntax body = callWithToken.WithoutTrivia();
        if (!SymbolEqualityComparer.Default.Equals(taskType.OriginalDefinition, types.Task) &&
            !SymbolEqualityComparer.Default.Equals(taskType.OriginalDefinition, types.TaskOfT))
        {
            body = CodeFixHelpers.Invoke(CodeFixHelpers.AsReceiver(body), "AsTask"); // FromTask takes a Task
        }

        var lambda = SimpleLambdaExpression(Parameter(Identifier(TriviaList(), tokenName, TriviaList(Space))), body)
            .WithArrowToken(Token(TriviaList(), SyntaxKind.EqualsGreaterThanToken, TriviaList(Space)));
        var bridge = CodeFixHelpers.TypeReference(model, awaitExpression.SpanStart, types.FlowBridge, out var needsUsing);
        var annotation = new SyntaxAnnotation();
        var operand = awaitExpression.Expression;
        var replacement = CodeFixHelpers.Invoke(bridge, "FromTask", lambda).WithTriviaFrom(operand).WithAdditionalAnnotations(annotation);

        var newRoot = root.ReplaceNode(operand, replacement);
        if (needsUsing) newRoot = CodeFixHelpers.AddFlowTaskUsing(newRoot);
        var changed = document.WithSyntaxRoot(newRoot);

        // The lambda must compile where the call did (a struct's 'this', ref-like values, ...).
        var changedRoot = await changed.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var changedModel = await changed.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var node = changedRoot.GetAnnotatedNodes(annotation).FirstOrDefault();
        if (node == null) return null;
        foreach (var d in changedModel.GetDiagnostics(node.Span, cancellationToken))
        {
            if (d.Severity == DiagnosticSeverity.Error) return null;
        }

        // Code after the await must still compile: a variable the arguments declare ('out var y', a pattern) is
        // scoped to the lambda now, and one they assign ('out y') is no longer definitely assigned after it.
        var before = Errors(model, ContainingMember(awaitExpression, root), cancellationToken);
        foreach (var error in Errors(changedModel, ContainingMember(node, changedRoot), cancellationToken))
        {
            if (!before.Remove(error)) return null;
        }

        return changed;
    }

    /// <summary>The member whose body holds <paramref name="node"/>; the whole file for top-level statements.</summary>
    static SyntaxNode ContainingMember(SyntaxNode node, SyntaxNode root)
    {
        var member = node.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        return member is null or GlobalStatementSyntax ? root : member;
    }

    /// <summary>The compile errors in <paramref name="node"/>, by id and message (their positions move with the fix).</summary>
    static List<string> Errors(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var d in model.GetDiagnostics(node.Span, cancellationToken))
        {
            if (d.Severity == DiagnosticSeverity.Error) errors.Add(d.Id + " " + d.GetMessage(CultureInfo.InvariantCulture));
        }

        return errors;
    }

    /// <summary>
    /// <paramref name="call"/> with <paramref name="token"/> passed to its CancellationToken parameter, or null when
    /// that is not possible or another token is already passed.
    /// </summary>
    static InvocationExpressionSyntax PassToken(InvocationExpressionSyntax call, ExpressionSyntax token, SemanticModel model, CancellationToken cancellationToken)
    {
        if (model.GetOperation(call, cancellationToken) is not IInvocationOperation invocation) return null;

        IParameterSymbol omitted = null;
        ArgumentSyntax none = null;
        var hasTokenParameter = false;
        foreach (var argument in invocation.Arguments)
        {
            if (!IsCancellationToken(argument.Parameter?.Type)) continue;
            hasTokenParameter = true;
            if (argument.ArgumentKind == ArgumentKind.DefaultValue)
            {
                omitted ??= argument.Parameter;
            }
            else if (argument.Syntax is ArgumentSyntax syntax && IsNoToken(argument.Value))
            {
                none ??= syntax;
            }
            else
            {
                return null; // a real token is passed already: which one the bridge should use is not ours to decide
            }
        }

        if (none != null) return call.ReplaceNode(none.Expression, token.WithTriviaFrom(none.Expression));

        if (omitted != null)
        {
            var named = Argument(token).WithNameColon(NameColon(IdentifierName(omitted.Name)).WithTrailingTrivia(Space));
            return AppendArgument(call, named);
        }

        if (hasTokenParameter) return null;

        // An overload that takes the same arguments plus a trailing token (Task.Delay(int) -> Task.Delay(int, CancellationToken)).
        var probe = AppendArgument(call, Argument(ParseExpression("default(global::System.Threading.CancellationToken)")));
        var info = model.GetSpeculativeSymbolInfo(call.SpanStart, probe, SpeculativeBindingOption.BindAsExpression);
        if (info.Symbol is IMethodSymbol overload &&
            overload.Parameters.Length == probe.ArgumentList.Arguments.Count &&
            IsCancellationToken(overload.Parameters[overload.Parameters.Length - 1].Type))
        {
            return AppendArgument(call, Argument(token));
        }

        return null;
    }

    static InvocationExpressionSyntax AppendArgument(InvocationExpressionSyntax call, ArgumentSyntax argument)
    {
        var arguments = call.ArgumentList.Arguments;
        if (arguments.Count == 0) return call.WithArgumentList(call.ArgumentList.WithArguments(SingletonSeparatedList(argument)));
        var separators = arguments.GetSeparators().ToList();
        separators.Add(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space));
        var nodes = arguments.ToList();
        nodes.Add(argument);
        return call.WithArgumentList(call.ArgumentList.WithArguments(SeparatedList(nodes, separators)));
    }

    static bool IsCancellationToken(ITypeSymbol type) =>
        type != null && type.Name == "CancellationToken" && type.ContainingNamespace?.ToDisplayString() == "System.Threading";

    /// <summary>'default', 'default(CancellationToken)', 'new CancellationToken()' or 'CancellationToken.None'.</summary>
    static bool IsNoToken(IOperation value)
    {
        while (value is IConversionOperation conversion) value = conversion.Operand;
        return value switch
        {
            IDefaultValueOperation => true,
            IObjectCreationOperation creation => creation.Arguments.IsEmpty && creation.Initializer == null,
            IPropertyReferenceOperation property => property.Property.Name == "None" && IsCancellationToken(property.Property.ContainingType),
            _ => false,
        };
    }
}
