# FlowTask

Structured-concurrency async game flow for C#: write screen transitions, dialogs, character behavior and tutorials
step by step with `async`/`await`. Every `FlowTask` belongs to the scope that started it; when a scope ends, everything
under it is unwound (`using` and `finally` run), so lifetime, cancellation, game time and failure follow the structure
of the code instead of hand-passed cancellation tokens. The same core runs on .NET, Unity and Godot.

## Installation

Every FlowTask package on NuGet carries this same description. Each one is added the same way; for the core:

```sh
dotnet add package FlowTask --version 0.1.0-preview.1
```

The namespace is `Katout.FlowTask`. The core targets netstandard2.1 and net10.0 and carries the FLOW analyzers with
their code fixes. For Godot 4 (.NET), add `FlowTask.Godot` instead; it brings `FlowTask` with it. The other packages
(listed below) are added next to the core in the same way, for example
`dotnet add package FlowTask.Testing --version 0.1.0-preview.1`.

## Example

```csharp
using System;
using Katout.FlowTask;

sealed class Tutorial
{
    public readonly Signal<FlowUnit> Confirmed = new Signal<FlowUnit>();

    public async FlowTask Run(Func<string, IDisposable> showHint)
    {
        using (showHint("Press A to jump"))        // closed on success, cancellation and failure alike
        {
            var r = await FlowTask.Race(Confirmed.Next(), FlowTask.WaitForSeconds(10));
            if (r.Index == 1) return;              // timed out; the losing branch has been unwound
        }
        await FlowTask.WaitForSeconds(0.5);
    }
}

// var world = new FlowWorld();
// world.Run(tutorial.Run(ShowHint));
// every frame: world.Tick(unscaledDeltaTime);
```

## Packages

| Package | Contents |
|---|---|
| FlowTask | The runtime (no dependencies) and the FLOW analyzers with code fixes |
| FlowTask.Godot | Godot 4 (.NET) integration: ticking from `_Process`, Godot signals, node lifetimes |
| FlowTask.UniTask | Conversions between UniTask and FlowTask |
| FlowTask.R3 | Conversions between R3 observables and FlowTask signals |
| FlowTask.Testing | Virtual time and assertions on the scope tree, without an engine |
| FlowTask.Testing.NUnit | An adapter that turns an unhandled exception into a test failure |

Unity (2023.1 or later) uses the UPM packages `com.katout.flowtask` and `com.katout.flowtask.unity` from the
repository (git URLs; list both).

Documentation: https://katout.github.io/FlowTask/ — source and change log: https://github.com/katout/FlowTask

License: MIT.
