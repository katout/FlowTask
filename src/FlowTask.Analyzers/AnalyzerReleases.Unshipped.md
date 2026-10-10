; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
FLOW001 | FlowTask | Error | Catch clause in a FlowTask method takes FlowCanceledException and does not rethrow it (or is a catch-all without a filter that excludes it)
FLOW002 | FlowTask | Error | Non-FlowTask awaitable awaited in a FlowTask method
FLOW003 | FlowTask | Warning | FlowTask is never awaited or started (or started on some paths only)
FLOW004 | FlowTask | Warning | Lifetime handle is not stored, or stored in a local that is never used
FLOW005 | FlowTask | Error | FlowTask awaitable awaited outside a FlowTask method
FLOW006 | FlowTask | Info | Signal.Next() awaited in a loop drops the emits between iterations
FLOW007 | FlowTask | Warning | GetAwaiter() of a FlowTask awaitable called in code instead of by await starts the task
FLOW008 | FlowTask | Warning | Handle of Flow.Spawn dropped by a statement (the child ends with the current scope)
FLOW009 | FlowTask | Warning | Flow.Spawn in an event handler written in a FlowTask method
FLOW010 | FlowTask | Warning | Await in a finally block (or a catch that rethrows) of a FlowTask method without Flow.NonCancelable
