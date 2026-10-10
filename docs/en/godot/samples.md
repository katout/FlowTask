# Godot samples

Samples that write common game scenarios with FlowTask, and a demo scene to try them. Copy them and adapt them to your game. Unity has samples with the same layout (see [Unity samples](../unity/samples.md)).

## Opening the samples

1. Get the repository and open [`samples/godot/`](../../../samples/godot) in Godot 4.4.1 or later, .NET edition.
2. Run the project (F5). The main scene is `Demo/Demo.tscn`.

- Pick a scenario with the buttons on the left. Escape (`ui_cancel`) is the back key.
- Pause (top right) stops the game: the enemy stops, while the menus and the buttons on the left keep running. A Hit pressed while paused lands when the game resumes.
- This project builds FlowTask from the repository's sources (a ProjectReference in `FlowTaskSamples.csproj`). When you copy the samples into a game, add the `FlowTask.Godot` NuGet package (see [Installation](../getting-started/installation.md)).
- The UI is built in code, without scene files, where a game would use scenes.
- The store, the resource loads, and the settings file are fakes for the demo (`Demo/DemoServices.cs`).

## Scenarios

| Scenario | Folder | What it shows |
| --- | --- | --- |
| Back key | `BackKey/` | Combining a layered list, `Flow.Own`, and `Signal`: the press goes to the flow waiting on the top layer, and `Block()` swallows it |
| Confirmation dialog | `ConfirmDialog/` | A Race of buttons, the back key, and a timeout. Waiting for the close animation (a Tween) in `finally` |
| Enemy AI | `Enemy/` | `RunWhileInTree` with the game Clock, subscribing to hits, `FlowProperty.WaitUntil` |
| Tutorial | `Tutorial/` | A scope per step that owns its balloon with `NodeLifetime.Own`, a Race for skipping |
| Purchase | `Shop/` | Bridging a Task-based store, expected failures as a dedicated exception type, retries, a receipt that arrives late |
| Pause | `Pause/` | Reference-counted `Pause()`, the UI Clock, saving with a time limit on close, making the tree's pause follow |
| Parallel loading | `Loading/` | `WhenAll`, progress, canceling, letting go of threaded loads without blocking |

`GameClocks.cs` is the pair of Clocks the other scenarios use. Enemies and timers run on `Game` (a child of `DefaultClock`, which follows `Engine.TimeScale`), and menus and dialogs on `Ui` (a child of `UnscaledClock`), so the UI keeps running when the game is paused. Create one `GameClocks` and one `BackKeyRouter` per World (session) and pass them to your flows; do not keep them in static fields. The demo makes its clocks with `GameClocks.ForCurrentScope()`: they belong to the demo's flow and leave the World when the demo ends, so loading the demo scene again adds no clocks.

Nodes and flows are tied with `NodeLifetime.Own` (a scope owns a node) and `RunWhileInTree` / `WhileInTree` (a node owns a flow) (see [Node lifetime](lifetime.md)). The comments in each file explain the details. The points specific to Godot follow; the ideas of each scenario are the same as in the Unity samples.

## Back key

- `BackKeyInput` reads `ui_cancel` in `_UnhandledInput`, so a focused control that uses the key first keeps it. The flow waiting for the press resumes in the same frame.
- It also forwards the Android back button (`NotificationWMGoBackRequest`). For that, set `application/config/quit_on_go_back` to false in the project settings.
- Its `ProcessMode` is `Always`, so the back key also closes the pause menu while the tree is paused.

## Confirmation dialog

- The flow owns the dialog's node with `NodeLifetime.Own`, and the node is freed when the flow ends. The buttons are bridged with `PressedSignal()`.
- The close animation's Tween ignores the time scale and the tree's pause (`SetIgnoreTimeScale()` and `TweenPauseMode.Process`). When its node is freed, a Tween disappears without emitting `finished`, so the sample waits with `FlowTask.WaitUntil(tween, t => !t.IsValid())`.
- The decision, `Ask`, takes plain `Signal<FlowUnit>` values instead of nodes, so it can be tested without nodes (see Testing below).

## Pause

A Clock's `Pause()` stops only the flows on that Clock. To stop the physics, AnimationPlayers, and `_process` as well, use the tree's pause (`GetTree().Paused`). But a Clock's pauses are counted, and the tree's pause is a single flag. So only `PauseLink` writes the tree's pause, following the `Game` Clock every frame.

- Screens only take `clocks.Game.Pause()`. Whoever pauses (a menu, the settings screen, a cutscene), the tree stops, and it starts again when the last pause is released.
- Put one `PauseLink` per session on a node that stays in the tree. Do not write `GetTree().Paused` anywhere else.
- `FlowWorldNode` keeps ticking while the tree is paused, so the flows on the UI Clock keep running. Set `ProcessMode = Always` on the menu's nodes (see [Setup](setup.md)).

## Parallel loading

- Loads start with `ResourceLoader.LoadThreadedRequest` and are awaited with `FlowTask.WaitUntil(request, r => r.IsDone)`. When a resource cannot be loaded, its load throws `ResourceLoadException`, and `FlowTask.WhenAll` unwinds the other loads at the first exception, then rethrows it. When the Cancel button is pressed, `LoadAll` returns null.
- A threaded load cannot be stopped; it is let go by taking its result (`LoadThreadedGet`). But `LoadThreadedGet` blocks the main thread until the load ends. So `ThreadedResourceLoader` takes a request released while loading in a root flow of its own, once the load is done. The Cancel button or a screen being freed does not freeze the game.

## Testing

[`tests/godot/Samples/`](../../../tests/godot/Samples) runs the samples with simulated input and checks the results (`tools/godot/run-smoke.ps1`). `ConfirmDialogAskTests.cs` is an NUnit test that tries the confirmation dialog's decision without Godot nodes, in a `TestWorld` with virtual time (see [Testing](../tools/testing.md)).
