using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Operations;

namespace Katout.FlowTask.Analyzers;

/// <summary>
/// FLOW011 / FLOW012: the type passed to FlowWorld.SkipUnwinding. Canceled in that World, its async methods end at
/// their await without unwinding, so the finally blocks, the disposals and the catch clauses around that await do not
/// run. FLOW011 (Error): a type written as typeof(X) whose source has such a block around an await, judged as
/// FlowAssert.CanSkipUnwinding (FlowTask.Testing) judges the compiled code. FLOW012 (Warning): a type the analyzer
/// cannot read (not written as typeof, a type parameter, declared in another assembly, or SkipUnwinding made a delegate),
/// for which that check in a test is the only one. Generated code is analyzed too: a source generator may write the calls and the types.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SkipUnwindingAnalyzer : DiagnosticAnalyzer
{
    const string SkipUnwinding = "SkipUnwinding";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.SkipUnwindingTypeHasCleanup,
        Descriptors.SkipUnwindingTypeNotWritten,
        Descriptors.SkipUnwindingTypeWithoutSource);

    public override void Initialize(AnalysisContext context)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = FlowTaskTypes.TryCreate(start.Compilation);
            if (types?.FlowWorld == null || types.FlowWorld.GetMembers(SkipUnwinding).IsEmpty) return;
            // One search per type: a generator may pass the same type to every World it makes.
            var found = new ConcurrentDictionary<INamedTypeSymbol, CleanupAroundAwait.Found>(SymbolEqualityComparer.Default);
            start.RegisterOperationAction(c => Analyze(c, types, found), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeReference(c, types), OperationKind.MethodReference);
        });
    }

    /// <summary>
    /// 'world.SkipUnwinding' as a delegate ('Action&lt;Type&gt; skip = world.SkipUnwinding;'): the types it is called with
    /// are not written at the call, so it is FLOW012 as a variable is.
    /// </summary>
    static void AnalyzeReference(OperationAnalysisContext context, FlowTaskTypes types)
    {
        var reference = (IMethodReferenceOperation)context.Operation;
        if (!IsSkipUnwinding(reference.Method, types)) return;
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.SkipUnwindingTypeNotWritten, reference.Syntax.GetLocation()));
    }

    static bool IsSkipUnwinding(IMethodSymbol method, FlowTaskTypes types) =>
        method.Name == SkipUnwinding && method.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(method.ContainingType, types.FlowWorld);

    static void Analyze(OperationAnalysisContext context, FlowTaskTypes types, ConcurrentDictionary<INamedTypeSymbol, CleanupAroundAwait.Found> found)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!IsSkipUnwinding(method, types) || invocation.Arguments.Length != 1) return;

        var argument = invocation.Arguments[0].Value;
        while (argument is IConversionOperation { IsImplicit: true } conversion) argument = conversion.Operand;
        // null throws ArgumentNullException: nothing to check.
        if (argument.ConstantValue.HasValue && argument.ConstantValue.Value == null) return;
        var location = argument.Syntax.GetLocation();
        if (argument is not ITypeOfOperation typeOf || typeOf.TypeOperand is ITypeParameterSymbol)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SkipUnwindingTypeNotWritten, location));
            return;
        }

        // An array or a pointer type declares no methods.
        if (typeOf.TypeOperand is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error) return;
        var type = named.OriginalDefinition;
        var display = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        if (type.DeclaringSyntaxReferences.IsEmpty)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SkipUnwindingTypeWithoutSource, location, display));
            return;
        }

        var model = invocation.SemanticModel;
        var compilation = context.Compilation;
        var cancellationToken = context.CancellationToken;
        var result = found.GetOrAdd(type, t => CleanupAroundAwait.Find(t, compilation, model, types, cancellationToken));
        if (result.IsEmpty) return;
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.SkipUnwindingTypeHasCleanup, location, result.Blocks, display, string.Join(", ", result.Names)));
    }
}
