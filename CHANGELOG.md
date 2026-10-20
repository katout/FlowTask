# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is 0.x, any release may break the API
without a deprecation period; every break is listed here.

## [Unreleased]

### Added

- `FlowWorld.SkipUnwinding(Type)`: canceled in that World, the async methods of the type end at their await without throwing `FlowCanceledException` (no allocation; about 0.14 µs instead of 1.2 µs per method in `Cancel 10000 flows` of `tools/bench/ab.cs`), for types with no `finally`, `using` or cancellation-taking `catch` around an await, which `FlowAssert.CanSkipUnwinding` (FlowTask.Testing) checks in a test on the JIT and Mono (a catch-all filtered with `when (e is not FlowCanceledException)`, as FLOW001 asks, passes). The end is otherwise the same as unwinding (status, cause, reports, children first, `AddCleanup` and `Own`, `Flow.NonCancelable` awaits); types not passed to it unwind as before.

### Changed

- A Tick no longer checks every time and frame wait: `WaitForSeconds`, `DelayFrames` and `NextFrame` are kept per Clock in deadline order, and only those whose deadline has come are checked, merged with the `WaitUntil` conditions in the order the waits began. Waits that aren't due cost almost nothing per Tick. The order of the checks and resumes, and the effect of a condition on the waits after it, are unchanged.

## [0.1.0-preview.1] - 2026-10-10

First public release.

### Added

- The core (`FlowTask` on NuGet, `com.katout.flowtask` on UPM): `FlowTask` and `FlowTask<T>`, scopes and cancellation
  by unwinding, `FlowWorld`, composition (`Race`, `WhenAll`, `Flow.Spawn`), Clocks, signals, bridges for Task and
  ValueTask, diagnostics, and the analyzers FLOW001–FLOW010 with code fixes.
- Diagnostics show where each wait was created. `FlowScopeInfo.WaitingFile` and `WaitingLine` give the caller's file
  and line of a wait or combinator, and a scope gives those of what it awaits directly (none for the call of a FlowTask
  method). `FlowWorld.Dump()` shows the file name and line (`waiting: Race at InGame.cs:18`), and the Unity Scope Tree
  window shows them and opens that line on a double-click. The APIs that create waits take the place through their
  last optional parameters, `[CallerFilePath] string callerFilePath` and `[CallerLineNumber] int callerLineNumber`,
  which a function that wraps them can pass on.
- Engine integrations: `FlowTask.Unity` (Unity 2023.1 or later, Mono and IL2CPP) and `FlowTask.Godot` (Godot 4.4.1 or
  later, .NET edition).
- Bridges: `FlowTask.UniTask` and `FlowTask.R3`.
- Testing: `FlowTask.Testing` and `FlowTask.Testing.NUnit`.
- Samples of common game scenarios (a confirmation dialog, enemy AI, a tutorial, a purchase, pausing, parallel loading)
  with a demo scene: for Unity in the package (Package Manager > FlowTask for Unity > Samples), for Godot in
  `samples/godot`.

### Known issues

- The cost of unwinding and the pool limit (1,024 nodes per type) are not measured on IL2CPP ARM64 devices yet.
- The UPM packages are tested on Unity 6 only; the oldest supported versions, 2023.1 and 2023.2, have not been tried.
- Nothing has run on Android or iOS devices or on WebGL: the Unity tests run in the Editor and in Windows players,
  and the Android IL2CPP player is only built.

[Unreleased]: https://github.com/katout/FlowTask/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/katout/FlowTask/releases/tag/v0.1.0-preview.1
