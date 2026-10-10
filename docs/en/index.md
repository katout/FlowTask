# FlowTask documentation

FlowTask is a C# library for writing game progression (screen transitions, dialogs, character behavior, cutscenes, tutorials) step by step with `async`/`await`. The structure of your code (scopes) decides how long a flow lives, how it is canceled, which game time it runs on, and how its failures are handled. The same core works on .NET, Unity, and Godot.

```csharp
async FlowTask OpenShop(Shop shop)
{
    using var window = shop.Open();                  // closed when this flow ends, however it ends
    var r = await FlowTask.Race(window.Purchased.Next(), window.Closed.Next());
    if (r.TryGet0(out var item)) await Purchase(item);
}
```

## Getting started

- [What is FlowTask?](getting-started/introduction.md): the problems it solves, where it fits, and what it leaves to other mechanisms
- [Installation](getting-started/installation.md): .NET, Unity, Godot
- [Your first flow](getting-started/first-flow.md): build a small flow and see the basics in action

## Guide

- [Flows and the World](guide/flows-and-world.md)
- [Scopes and cancellation](guide/scopes-and-cancellation.md)
- [Composition: Race and WhenAll](guide/composition.md)
- [Time and Clocks](guide/time-and-clocks.md)
- [Signals](guide/signals.md)
- [Handling failures](guide/failures.md)
- [Threads](guide/threads.md)

## Engines

- Unity: [Setup](unity/setup.md), [GameObject lifetime](unity/lifetime.md), [Bridges](unity/bridges.md), [Samples](unity/samples.md)
- Godot: [Setup](godot/setup.md), [Node lifetime](godot/lifetime.md), [Signals](godot/signals.md), [Physics frames](godot/physics.md), [Samples](godot/samples.md)

## Integrations

- [Task and ValueTask](integrations/task.md)
- [UniTask](integrations/unitask.md) (including migrating from UniTask)
- [R3](integrations/r3.md)

## Testing and diagnostics

- [Testing without an engine](tools/testing.md)
- [Debugging and diagnostics](tools/debugging.md)
- [Analyzer rules](tools/analyzers.md)

## In depth

- [Execution model](advanced/execution-model.md)
- [Performance and memory](advanced/performance.md)
- [Design rationale](advanced/design-rationale.md)
- [Glossary](advanced/glossary.md)
