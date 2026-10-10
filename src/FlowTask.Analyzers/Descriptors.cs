namespace Katout.FlowTask.Analyzers;

/// <summary>
/// Diagnostic descriptors. Some ids have more than one descriptor so the message can name the exact construct.
/// Titles, messages and descriptions live in Resources.resx (English, the neutral language) and Resources.ja.resx
/// (Japanese); the host picks the language from its UI culture (docs/en/tools/analyzers.md). Change both files together.
/// </summary>
internal static class Descriptors
{
    /// <summary>A string of Resources.resx, looked up in the culture the host asks for when it shows the diagnostic.</summary>
    static LocalizableResourceString L(string name) => new(name, Resources.ResourceManager, typeof(Resources));

    // Takes LocalizableResourceString, not string or LocalizableString, so an English literal does not compile here
    // (RS1007 guards direct DiagnosticDescriptor constructions; LocalizationTests checks the result).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "Markdown anchors in docs/*/tools/analyzers.md are lower case (#flow001).")]
    static DiagnosticDescriptor Create(
        string id,
        DiagnosticSeverity severity,
        LocalizableResourceString title,
        LocalizableResourceString message,
        LocalizableResourceString description) =>
        new(
            id,
            title,
            message,
            DiagnosticIds.Category,
            severity,
            isEnabledByDefault: true,
            description: description,
            helpLinkUri: DiagnosticIds.HelpLinkBase + id.ToLowerInvariant());

    // ------------------------------------------------------------------ FLOW001

    public static readonly DiagnosticDescriptor CatchAllTakesCancellation = Create(
        DiagnosticIds.CatchCanObserveCancellation,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow001Title)),
        L(nameof(Resources.Flow001MessageCatchAll)),
        L(nameof(Resources.Flow001Description)));

    public static readonly DiagnosticDescriptor CancellationCatchNotRethrown = Create(
        DiagnosticIds.CatchCanObserveCancellation,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow001Title)),
        L(nameof(Resources.Flow001MessageNotRethrown)),
        L(nameof(Resources.Flow001Description)));

    public static readonly DiagnosticDescriptor OperationCanceledCatchNotRethrown = Create(
        DiagnosticIds.CatchCanObserveCancellation,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow001Title)),
        L(nameof(Resources.Flow001MessageOperationCanceledNotRethrown)),
        L(nameof(Resources.Flow001Description)));

    // ------------------------------------------------------------------ FLOW002

    public static readonly DiagnosticDescriptor ForeignAwaitTaskLike = Create(
        DiagnosticIds.ForeignAwaitInFlowTask,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow002Title)),
        L(nameof(Resources.Flow002MessageForeignAwaitTaskLike)),
        L(nameof(Resources.Flow002Description)));

    public static readonly DiagnosticDescriptor ForeignAwaitWithAsFlow = Create(
        DiagnosticIds.ForeignAwaitInFlowTask,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow002Title)),
        L(nameof(Resources.Flow002MessageForeignAwaitWithAsFlow)),
        L(nameof(Resources.Flow002Description)));

    public static readonly DiagnosticDescriptor ForeignAwaitYield = Create(
        DiagnosticIds.ForeignAwaitInFlowTask,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow002Title)),
        L(nameof(Resources.Flow002MessageForeignAwaitYield)),
        L(nameof(Resources.Flow002Description)));

    public static readonly DiagnosticDescriptor ForeignAwaitOther = Create(
        DiagnosticIds.ForeignAwaitInFlowTask,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow002Title)),
        L(nameof(Resources.Flow002MessageForeignAwaitOther)),
        L(nameof(Resources.Flow002Description)));

    public static readonly DiagnosticDescriptor ForeignAwaitForEach = Create(
        DiagnosticIds.ForeignAwaitInFlowTask,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow002Title)),
        L(nameof(Resources.Flow002MessageForeignAwaitForEach)),
        L(nameof(Resources.Flow002Description)));

    public static readonly DiagnosticDescriptor ForeignAwaitUsing = Create(
        DiagnosticIds.ForeignAwaitInFlowTask,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow002Title)),
        L(nameof(Resources.Flow002MessageForeignAwaitUsing)),
        L(nameof(Resources.Flow002Description)));

    // ------------------------------------------------------------------ FLOW003

    public static readonly DiagnosticDescriptor FlowTaskDropped = Create(
        DiagnosticIds.FlowTaskDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow003Title)),
        L(nameof(Resources.Flow003MessageFlowTaskDropped)),
        L(nameof(Resources.Flow003Description)));

    public static readonly DiagnosticDescriptor FlowTaskDiscarded = Create(
        DiagnosticIds.FlowTaskDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow003Title)),
        L(nameof(Resources.Flow003MessageFlowTaskDiscarded)),
        L(nameof(Resources.Flow003Description)));

    public static readonly DiagnosticDescriptor FlowTaskDiscardedWithoutRunning = Create(
        DiagnosticIds.FlowTaskDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow003Title)),
        L(nameof(Resources.Flow003MessageFlowTaskDiscardedWithoutRunning)),
        L(nameof(Resources.Flow003Description)));

    public static readonly DiagnosticDescriptor FlowTaskLocalNeverRead = Create(
        DiagnosticIds.FlowTaskDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow003Title)),
        L(nameof(Resources.Flow003MessageFlowTaskLocalNeverRead)),
        L(nameof(Resources.Flow003Description)));

    public static readonly DiagnosticDescriptor FlowTaskCollectionNeverRead = Create(
        DiagnosticIds.FlowTaskDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow003Title)),
        L(nameof(Resources.Flow003MessageFlowTaskCollectionNeverRead)),
        L(nameof(Resources.Flow003Description)));

    public static readonly DiagnosticDescriptor FlowTaskStartedOnSomePaths = Create(
        DiagnosticIds.FlowTaskDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow003Title)),
        L(nameof(Resources.Flow003MessageFlowTaskStartedOnSomePaths)),
        L(nameof(Resources.Flow003Description)));

    // ------------------------------------------------------------------ FLOW004

    public static readonly DiagnosticDescriptor LifetimeHandleDropped = Create(
        DiagnosticIds.LifetimeHandleDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow004Title)),
        L(nameof(Resources.Flow004MessageLifetimeHandleDropped)),
        L(nameof(Resources.Flow004Description)));

    public static readonly DiagnosticDescriptor LifetimeHandleLocalNeverUsed = Create(
        DiagnosticIds.LifetimeHandleDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow004Title)),
        L(nameof(Resources.Flow004MessageLifetimeHandleLocalNeverUsed)),
        L(nameof(Resources.Flow004Description)));

    // ------------------------------------------------------------------ FLOW005

    public static readonly DiagnosticDescriptor FlowAwaitOutsideFlowTask = Create(
        DiagnosticIds.FlowAwaitOutsideFlowTask,
        DiagnosticSeverity.Error,
        L(nameof(Resources.Flow005Title)),
        L(nameof(Resources.Flow005Message)),
        L(nameof(Resources.Flow005Description)));

    // ------------------------------------------------------------------ FLOW006

    public static readonly DiagnosticDescriptor SignalNextInLoop = Create(
        DiagnosticIds.SignalNextInLoop,
        DiagnosticSeverity.Info,
        L(nameof(Resources.Flow006Title)),
        L(nameof(Resources.Flow006Message)),
        L(nameof(Resources.Flow006Description)));

    // ------------------------------------------------------------------ FLOW007

    public static readonly DiagnosticDescriptor DirectGetAwaiter = Create(
        DiagnosticIds.DirectGetAwaiter,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow007Title)),
        L(nameof(Resources.Flow007Message)),
        L(nameof(Resources.Flow007Description)));

    // ------------------------------------------------------------------ FLOW008

    public static readonly DiagnosticDescriptor SpawnHandleDropped = Create(
        DiagnosticIds.SpawnHandleDropped,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow008Title)),
        L(nameof(Resources.Flow008Message)),
        L(nameof(Resources.Flow008Description)));

    // ------------------------------------------------------------------ FLOW009

    public static readonly DiagnosticDescriptor SpawnInEventHandler = Create(
        DiagnosticIds.SpawnInEventHandler,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow009Title)),
        L(nameof(Resources.Flow009Message)),
        L(nameof(Resources.Flow009Description)));

    // ------------------------------------------------------------------ FLOW010

    public static readonly DiagnosticDescriptor CleanupAwaitNotProtected = Create(
        DiagnosticIds.CleanupAwaitNotProtected,
        DiagnosticSeverity.Warning,
        L(nameof(Resources.Flow010Title)),
        L(nameof(Resources.Flow010Message)),
        L(nameof(Resources.Flow010Description)));
}
