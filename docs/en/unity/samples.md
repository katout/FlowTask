# Unity samples

Samples that write common game scenarios with FlowTask, and a demo scene to try them. Copy them and adapt them to your game. Godot has samples with the same layout (see [Godot samples](../godot/samples.md)).

## Opening the samples

1. In a project with FlowTask.Unity installed, open the Package Manager, select FlowTask for Unity, and press Import next to "Scenarios" under Samples. The files are copied to `Assets/Samples/FlowTask for Unity/<version>/Scenarios/`.
2. Open `Demo/Demo.unity` and press Play.

- Pick a scenario with the buttons on the left. Escape is the back key (the Android back button also arrives as Escape).
- Pause (top right) stops the game: the enemy stops, while the menus and the buttons on the left keep running. A Hit pressed while paused lands when the game resumes.
- The samples use uGUI (`com.unity.ugui`). For input they use the Input System package when the project has it, and the Input Manager otherwise.
- The language is Unity's default, C# 9. The views are built in code (`SampleUi.cs`) instead of prefabs, where a game would use prefabs.
- The store, the asset loads, and the settings file are fakes for the demo (`Demo/DemoServices.cs`).

## Scenarios

| Scenario | Folder | What it shows |
| --- | --- | --- |
| Back key | `BackKey/` | Combining a layered list, `Flow.Own`, and `Signal`: the press goes to the flow waiting on the top layer, and `Block()` swallows it |
| Confirmation dialog | `ConfirmDialog/` | A Race of buttons, the back key, and a timeout. Waiting for the close animation in `finally` |
| Enemy AI | `Enemy/` | `RunWhileActive` with the game Clock, subscribing to hits, `FlowProperty.WaitUntil` |
| Tutorial | `Tutorial/` | `using` per step, a Race for skipping |
| Purchase | `Shop/` | Bridging a Task-based store, expected failures as a dedicated exception type, retries, a receipt that arrives late |
| Pause | `Pause/` | Reference-counted `Pause()`, the UI Clock, saving with a time limit on close |
| Parallel loading | `Loading/` | `WhenAll`, progress, canceling, releasing every request |

`GameClocks.cs` is the pair of Clocks the other scenarios use. Enemies and timers run on `Game` (a child of `DefaultClock`), and menus and dialogs on `Ui` (a child of `UnscaledClock`), so the UI keeps running when the game is paused. Create one `GameClocks` and one `BackKeyRouter` per World (session) and pass them to your flows; do not keep them in static fields (see [Setup](setup.md)). The demo makes its clocks with `GameClocks.ForCurrentScope()`: they belong to the demo's flow and leave the World when the demo ends, so loading the demo scene again adds no clocks.

The comments in each file explain the details. The key points of each scenario follow.

## Confirmation dialog

```csharp
if (await ConfirmDialog.Show(canvas, "Buy the gem pack?", router, clocks.Ui)) Buy();
```

```csharp
var answer = await FlowTask.Race(new[]
{
    ok.Next().WithoutResult(),
    cancel.Next().WithoutResult(),
    router.Next(BackKeyRouter.Dialog),
    FlowTask.WaitForSeconds(timeoutSeconds, ui),
});
```

- The losing branches of the Race unwind before the caller resumes. A second tap in the same frame reaches nobody, and the back key's entry leaves with its branch.
- The close animation is awaited in `finally` as `await Flow.NonCancelable(view.PlayClose(ui))`. When a cancel or a destroy comes after the answer, the animation and the `Destroy` still run to the end (see [Scopes and cancellation](../guide/scopes-and-cancellation.md)).

## Enemy AI

```csharp
async FlowTask Run()
{
    await FlowTask.Race(Alive(), Hp.WaitUntil(hp => hp <= 0)); // HP 0 cuts through the patrol and a flinch at once
    await Die();
}

async FlowTask Alive()
{
    using var hits = Damaged.Subscribe(BufferPolicy.Latest); // a hit during a flinch is kept
    while (true)
    {
        var r = await FlowTask.Race(hits.Next(), Patrol()); // the hit first
        if (r.Index == 0) await Flinch();
    }
}
```

- The AI flow is bound to the GameObject with `RunWhileActive` in `Start`, so destroying it, deactivating it, or ending its scene stops it. It runs on the `Game` Clock, so pausing the game stops it, and a hit during the pause waits in the subscription.
- Waiting for `Damaged.Next()` inside the loop would miss the hits during a flinch (analyzer rule FLOW006).

## Tutorial

- The balloon and the highlight are shown with a `using` per step, so they go away when the step is done, when the player skips, and when a scene change cancels the tutorial, even on an overlay that outlives the scene.
- Bind the tutorial to a GameObject of the scene it teaches with `RunWhileActive`. It returns the number of steps done.

## Purchase

- The store's Task is bridged with `FlowBridge.FromTask(ct => _store.PurchaseAsync(itemId, ct), _onLateReceipt)`. Canceling the flow cancels the token, and a receipt that arrives after the cancel goes to the second argument. The player has paid, so grant the item there (see [Task and ValueTask bridges](../integrations/task.md)).
- Only the store's expected failures (`StoreException`) are caught. A failure that can be retried is tried up to three times, waiting 1 s and then 2 s on the UI Clock. When the store declines, or all three tries fail, `PurchaseFailedException` is thrown with the reason (`PurchaseError`). During the purchase, `router.Block()` swallows the back key.
- The screen (`ShopScreen`) shows the reason with `catch (PurchaseFailedException e)`. Any other exception is a bug and goes on up as it is: the screen's `catch (Exception bug) when (bug is not FlowCanceledException)` takes it and ends only that purchase (see [Handling failures](../guide/failures.md)).
- The Buy button is awaited with `Next()`, not a subscription. Presses during a purchase are dropped, and no second purchase queues up behind the first.

## Pause

- The menu holds `using var pause = clocks.Game.Pause();` while it is open. `Pause()` is reference-counted, so the settings screen opened on top of the menu pauses too, and the game resumes when the last pause is released.
- The menu and the settings screen are pinned to the UI Clock with `Flow.WithClock(clocks.Ui, …)`. Opened from a flow on the game Clock, they do not stop under their own pause (the `PausedOwnClock` warning).
- The settings screen saves the settings in its `finally` when it closes (including when it is canceled), and the flows around it wait for the save. The save races 2 seconds of the UI Clock; when it does not finish in time, the settings stay unsaved until the next close.

```csharp
finally
{
    if (settings.IsDirty)
    {
        var saved = await Flow.NonCancelable(FlowTask.Race(settings.Save(), FlowTask.WaitForSeconds(SaveTimeoutSeconds)));
        if (saved.Index == 0) settings.MarkClean();
    }
}
```

A Clock's `Pause()` stops only the flows on that Clock. Animators, physics, `Time.deltaTime`, and coroutines that wait with `WaitForSeconds` keep running. `PauseLink` sets `Time.timeScale` to 0 while the `Game` Clock is paused, so they stop too (`Update` and coroutines that yield `null` still run).

- Screens only take the Clock's `Pause()`, and only `PauseLink` writes `Time.timeScale`. The Clock's reference count handles nested pauses (the settings over the menu).
- While `Time.timeScale` is 0, `DefaultClock` and its children stop too. What runs during the pause (the menus) runs on the UI Clock, a child of `UnscaledClock`.
- Put `PauseLink` on a GameObject that stays active for the whole session. When it is disabled or destroyed, it gives `Time.timeScale` back.

## Parallel loading

- The loads sit behind `IAssetLoader` / `IAssetRequest`, so Addressables, AssetBundles, or Resources can be swapped in (`ResourcesLoader` is the `Resources.LoadAsync` version). To load a single asset, you can also bridge the Addressables `.Task` with `FlowBridge.FromTask` (see "Addressables and UnityWebRequest" in [Bridges](bridges.md)).
- When an asset cannot be loaded, its load throws `AssetLoadException`. `FlowTask.WhenAll` unwinds the other loads at the first exception, then rethrows it. `LoadAll` shows the asset's name on the screen before it passes the exception to the caller.
- The Cancel button is a Race that unwinds the loads and the progress display together. `LoadAll` then returns null.
- Every request that does not reach the caller is released in `finally`, whether the load failed, was canceled, or was canceled from outside (the loading screen destroyed).

## Testing

The tests of each scenario are in the repository, in [`tests/unity/Assets/Tests/Samples/`](../../../tests/unity/Assets/Tests/Samples). They end the GameObject a flow is bound to in three ways (`Destroy`, `SetActive(false)`, and closing the scene) and check that no cleanup is left behind. `EngineFreeSampleTests.cs` shows how to try the samples without the PlayerLoop, in a `TestWorld` with virtual time (see [Testing](../tools/testing.md)).
