English | [日本語](README.ja.md)

# FlowTask

[![NuGet](https://img.shields.io/nuget/vpre/FlowTask)](https://www.nuget.org/packages/FlowTask)
[![License: MIT](https://img.shields.io/github/license/katout/FlowTask)](LICENSE)

A C# library for writing game flow (screen transitions, dialogs, character behavior, cutscenes, tutorials) step by
step with `async`/`await`. The lifetime of a flow, its cancellation, game time and failure follow the structure of the
code (scopes). The same core runs on .NET, Unity and Godot.

**[Documentation](https://katout.github.io/FlowTask/)**

> FlowTask is in preview (0.x). Until 1.0, any release may change the API ([CHANGELOG](CHANGELOG.md)).

```csharp
using Katout.FlowTask;

async FlowTask EnemyAI(Enemy self)
{
    // Keeps only the latest hit while nobody waits.
    using var hits = self.Damaged.Subscribe(BufferPolicy.Latest);
    while (true)
    {
        // The loser of the race is unwound: its using and finally blocks run.
        var r = await FlowTask.Race(hits.Next(), Patrol(self));
        if (r.TryGet0(out var hit)) await HitStun(self, hit);
    }
}

// Start the flow on a World (one of these, depending on the engine):
FlowTaskUnity.World.Run(EnemyAI(enemy));        // Unity: the World that the PlayerLoop ticks
FlowWorldNode.Default.Run(EnemyAI(enemy));      // Godot: the World that FlowWorldNode ticks
var world = new FlowWorld();                    // .NET: your own World ...
world.Run(EnemyAI(enemy));
world.Tick(deltaTime);                          // ... ticked once per frame
```

## Features

- **Structured concurrency**: a FlowTask becomes a child of the scope that started it. When the parent ends, all its
  descendants are unwound, so there are no cancellation tokens to pass by hand.
- **Cancellation by unwinding**: a canceled flow runs its `using` and `finally` blocks as usual, and the awaits inside
  `finally` run to their end.
- **Composition**: `FlowTask.Race` (unwinds the losers before resuming), `FlowTask.WhenAll`, `Flow.Spawn`.
- **Deterministic order**: every resumption goes through one queue and runs in a fixed order.
- **Game time**: pause and time scale per Clock, so the UI keeps running while the game is paused.
- **Signals**: `Signal<T>`, subscriptions whose buffering the receiver chooses, `FlowProperty<T>`.
- **No allocation in steady state**: state machines and nodes are pooled; checked on NativeAOT and IL2CPP too.
- **Analyzers**: common mistakes are reported at compile time, with code fixes (messages in English and Japanese).
- **Testing without an engine**: extension methods that advance a World in virtual time (`TickFor`,
  `RunUntilComplete`), `TestWorld` that records unhandled exceptions, and an adapter for NUnit.
- **Bridges**: flows can await Task / ValueTask, UniTask, R3, Unity (AsyncOperation, Awaitable, UnityEvent), and Godot signals. The token that `FlowBridge.FromTask` passes to its factory is canceled when the scope ends.

## Installation

| Platform | How |
|---|---|
| .NET (netstandard2.1 / net10.0) | `dotnet add package FlowTask --prerelease` |
| Unity 2023.1 or later | Add the git URLs in the Package Manager (below) |
| Godot 4.4.1 or later, .NET edition | `dotnet add package FlowTask.Godot --prerelease` |

In Unity, add both the core and the Unity integration to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.katout.flowtask": "https://github.com/katout/FlowTask.git?path=src/FlowTask",
    "com.katout.flowtask.unity": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Unity"
  }
}
```

The optional packages (testing, UniTask, R3) and the first setup for each engine are in
[Installation](docs/en/getting-started/installation.md).

## Documentation

- [What is FlowTask](docs/en/getting-started/introduction.md)
- [Your first flow](docs/en/getting-started/first-flow.md)
- [Guide](docs/en/index.md#guide)
- [Unity](docs/en/unity/setup.md) / [Godot](docs/en/godot/setup.md)
- Samples ([Unity](docs/en/unity/samples.md) / [Godot](docs/en/godot/samples.md)): a confirmation dialog, enemy AI, pausing and more, with a demo to try them
- [Migrating from UniTask](docs/en/integrations/unitask.md)

The same pages are on the [documentation site](https://katout.github.io/FlowTask/).

## Packages

| Package | Distribution | Contents |
|---|---|---|
| FlowTask | NuGet, UPM `com.katout.flowtask` | The core and the analyzers |
| FlowTask.Unity | UPM `com.katout.flowtask.unity` | PlayerLoop integration, Unity bridges, GameObject lifetime, Scope Tree window |
| FlowTask.Godot | NuGet | `_Process` integration, Godot signal bridges, node lifetime |
| FlowTask.UniTask | NuGet, UPM `com.katout.flowtask.unitask` | UniTask ⇄ FlowTask |
| FlowTask.R3 | NuGet | R3 Observable ⇄ Signal |
| FlowTask.Testing | NuGet, UPM `com.katout.flowtask.testing` | Testing in virtual time |
| FlowTask.Testing.NUnit | NuGet, UPM `com.katout.flowtask.testing.nunit` | An adapter that fails the test on an unhandled exception |

## Contributing

Please report bugs and suggestions in [Issues](https://github.com/katout/FlowTask/issues). How to build and test is in
[CONTRIBUTING.md](CONTRIBUTING.md). Do not report vulnerabilities in a public issue; follow [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
