using Microsoft.CodeAnalysis.Operations;

namespace Katout.FlowTask.Analyzers;

/// <summary>
/// FLOW007 (Warning): GetAwaiter() of a FlowTask awaitable called in code instead of by await. The await of a FlowTask
/// begins in GetAwaiter, which starts the lazy task, so a direct call starts it too, even if the awaiter is never
/// awaited, and the awaiter's IsCompleted keeps the state of that moment. The await itself calls GetAwaiter without an
/// invocation operation, so only calls written in code are seen. A call inside a GetAwaiter method (an awaitable of
/// the user's that hands out FlowTask's awaiter to its own await) is not reported.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DirectGetAwaiterAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.DirectGetAwaiter);

    public override void Initialize(AnalysisContext context)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = FlowTaskTypes.TryCreate(start.Compilation);
            if (types == null || (types.FlowTaskAwaiter == null && types.FlowTaskOfTAwaiter == null)) return;
            start.RegisterOperationAction(c => AnalyzeInvocation(c, types), OperationKind.Invocation);
        });
    }

    static void AnalyzeInvocation(OperationAnalysisContext context, FlowTaskTypes types)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name != "GetAwaiter" || !types.IsFlowTaskAwaiter(method.ReturnType)) return;
        if (context.ContainingSymbol is IMethodSymbol container && container.Name == "GetAwaiter") return;

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.DirectGetAwaiter, invocation.Syntax.GetLocation(), Receiver(invocation, method)));
    }

    /// <summary>
    /// The expression the awaiter is taken from, for the message: the instance ('x' in 'x.GetAwaiter()' and in
    /// 'x?.GetAwaiter()'), or the first argument of an extension method called as a static method; 'this' when it is
    /// implicit.
    /// </summary>
    static string Receiver(IInvocationOperation invocation, IMethodSymbol method)
    {
        var receiver = invocation.Instance;
        if (receiver == null && method.IsExtensionMethod && invocation.Arguments.Length > 0) receiver = invocation.Arguments[0].Value;
        // 'x?.GetAwaiter()': the instance is an implicit placeholder for 'x', so it is looked at before IsImplicit.
        if (receiver is IConditionalAccessInstanceOperation && invocation.Syntax.Parent is ConditionalAccessExpressionSyntax conditional)
        {
            return conditional.Expression.WithoutTrivia().ToString();
        }

        if (receiver == null || receiver.IsImplicit) return "this";
        return receiver.Syntax.WithoutTrivia().ToString();
    }
}
