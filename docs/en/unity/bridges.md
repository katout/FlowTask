# Unity bridges

This page shows how to connect Unity's async mechanisms (UnityEvent, AsyncOperation, Addressables, Awaitable, Task) to flows. It also covers the helpers for waiting on uGUI buttons and the Scope Tree window for inspecting running flows.

## Turning a UnityEvent into a Signal

```csharp
async FlowTask Shop(UnityEvent<int> onPurchased)
{
    using var purchased = onPurchased.ToSignal(); // EventSignal<int>, owned by the current scope
    var itemId = await purchased.Next();
}
```

`UnityEvent` and `UnityEvent<T>` have `ToSignal()`. You can wait on the returned `EventSignal<T>` with `Next()`, `NextOrClosed()`, and `Subscribe(policy)`.

- **Call it from flow code.** The current scope owns the listener; it is removed when the scope ends (or on an earlier Dispose), and the EventSignal closes. If you call it outside flow code, dispose it yourself.
- Invoke does not resume receivers immediately. They resume at the next Flush point or Tick (see "Tick and Flush" in [Setup](setup.md)).
- For events with two or more arguments, bridge them with `FlowBridge.FromCallback` and a single lambda.

  ```csharp
  using var hits = FlowBridge.FromCallback<(GameObject, int)>(emit =>
  {
      UnityAction<GameObject, int> listener = (target, damage) => emit((target, damage));
      onHit.AddListener(listener);
      return () => onHit.RemoveListener(listener); // called when the scope ends
  });
  ```

### Next() is an edge

`Next()` only receives Invokes that happen after you start waiting. Invokes while nobody is waiting are dropped. If you don't want to miss Invokes that happen while the loop body runs, subscribe before the loop.

```csharp
using var purchased = onPurchased.ToSignal();
using var queue = purchased.Subscribe(BufferPolicy.Latest); // keeps the latest one while the loop body runs
while (true)
{
    var itemId = await queue.Next();
    await Grant(itemId);
}
```

The kinds of `BufferPolicy` (`Latest`, `Queue(capacity, overflow)`) and the difference between edges and subscriptions are covered in [Signals](../guide/signals.md). The analyzer reports an info diagnostic (FLOW006) for code that waits on `Next()` inside a loop.

### Callbacks that arrive after closing

Waiting with `Next()` on an EventSignal after it has closed throws `SignalClosedException`, and values that arrive after it has closed are silently dropped. Receive callbacks that can arrive after a screen closes, such as rewarded ad rewards or purchase results, in a flow that outlives the screen (a service started with `FlowTaskUnity.World.Run`).

## Waiting on an AsyncOperation

```csharp
await SceneManager.LoadSceneAsync("Battle").AsFlow();
var tex = (Texture2D)await Resources.LoadAsync<Texture2D>("icon").AsFlow(); // a ResourceRequest returns the asset
```

- `AsyncOperation.AsFlow()` returns a `FlowTask`, and `ResourceRequest.AsFlow()` returns a `FlowTask<UnityEngine.Object>` that yields the loaded asset.
- Completion is received through `AsyncOperation.completed`, and the flow resumes at the next Flush point or Tick. An operation that has already completed completes immediately, without allocating.
- If the scope is canceled first, the handler is removed and the completion is ignored. `AsyncOperation` has no API to cancel it, so the bridge only stops waiting, and the operation keeps running.
  - Stop an operation that can be stopped in the scope's cleanup (`finally` or `Flow.AddCleanup`), for example with `UnityWebRequest.Abort()` (see "Addressables and UnityWebRequest" below).
  - Loads that finish after the bridge stopped waiting (a scene, an AssetBundle) are cleaned up by the flow's own code. Only when the flow stopped waiting (it was canceled), register the cleanup on `operation.completed`. A handler registered on a finished operation is called too.
- To show progress, race the load against a loop that reads `progress` every frame (the parallel loading scenario in [Samples](samples.md)).

> **Note**: Inside a FlowTask method, do not await an `AsyncOperation` or `Awaitable` directly without `.AsFlow()`. It is an await outside the scope, so when it suspends there, the scope ends with `FlowMisuseException`. The analyzer reports it as error FLOW002.

## Addressables and UnityWebRequest

An Addressables `AsyncOperationHandle` is not an `AsyncOperation`, so bridge its `.Task` with `FlowBridge.FromTask`.

```csharp
using var cancel = view.CancelButton.ClickedSignal();
var r = await FlowTask.Race(
    cancel.Next(),
    FlowBridge.FromTask(
        _ => Addressables.LoadAssetAsync<GameObject>("Boss").Task,
        onDiscard: asset => Addressables.Release(asset)).ToFlowTask()); // a result nobody received is released
if (!r.TryGet1(out var boss)) return; // canceled: onDiscard releases the asset when it arrives
```

- The load takes no token, so a canceled bridge only stops waiting. A result that arrives later goes to `onDiscard` (see `onDiscard` in [Task / ValueTask bridges](../integrations/task.md)).
- To pass a bridge with a result to a Race and similar, add `.ToFlowTask()`. Without it, the type arguments cannot be inferred.
- Loading several assets with progress, and releasing together every one nobody received, is shown in the parallel loading scenario in [Samples](samples.md).

`UnityWebRequest`'s `SendWebRequest()` is an `AsyncOperation`, so you can await it with `.AsFlow()`. When the flow is canceled, call `Abort()` in `finally` to stop the request. Put a real-time limit on it with `timeout`, because `FlowTask.WaitForSeconds` advances in game time and stops while the game is paused (see "Timeouts" in [Composition](../guide/composition.md)).

```csharp
using var request = UnityWebRequest.Get(url);
request.timeout = 10; // a real-time limit that a paused game doesn't stop
try
{
    await request.SendWebRequest().AsFlow();
}
finally
{
    if (!request.isDone) request.Abort(); // the scope gave up: stop the request
}
```

## Waiting on an Awaitable

```csharp
await Awaitable.WaitForSecondsAsync(1f).AsFlow();
int n = await ComputeAsync().AsFlow(); // Awaitable<T>
```

- `Awaitable.AsFlow()` is built on `FlowBridge.FromTask` and, like `task.AsFlow()`, returns a `TaskBridge` (`TaskBridge<T>` for `Awaitable<T>`). Besides awaiting it, you can pass it to a Race and similar. When you pass a `TaskBridge<T>`, add `.ToFlowTask()`.
- Whatever thread it completes on (including `Awaitable.BackgroundThreadAsync()`), the flow resumes on the World's thread.
- When the scope is canceled, it calls `Awaitable.Cancel()` and ignores the result.
- An Awaitable canceled outside the flow throws `OperationCanceledException` at the await. To catch it, write `catch (OperationCanceledException e) when (e is not FlowCanceledException)`. Canceling the scope doesn't reach this `catch`; the flow simply unwinds.

In Unity, calling `Cancel()` on the Awaitable of an async method does not propagate to the Awaitables it is waiting on inside. To stop the waits inside too, have the method take a `CancellationToken` and pass it to those waits, and pass the scope's token with `FlowBridge.FromTask`.

```csharp
var map = await FlowBridge.FromTask(async ct => await BuildMapAsync(ct)); // the scope's token reaches the awaits inside

static async Awaitable<Map> BuildMapAsync(CancellationToken ct)
{
    await Awaitable.NextFrameAsync(ct); // stops when the scope is canceled
    return Map.Generate();
}
```

Task and ValueTask bridges in general (`FromTask`, `onDiscard`, external cancellation) are covered in [Task / ValueTask bridges](../integrations/task.md).

## Task and UnitySynchronizationContext

Unity's main thread has a `UnitySynchronizationContext`. If you await without `ConfigureAwait(false)` inside a `FlowBridge.FromTask` factory, the continuation is posted to this context and runs in the Update phase (`ScriptRunDelayedTasks`).

```csharp
var bytes = await FlowBridge.FromTask(async ct =>
{
    var data = await File.ReadAllBytesAsync(path, ct); // continues on the main thread in ScriptRunDelayedTasks
    return Decode(data);
});
```

This is fine in a normal game. But code that blocks the main thread while waiting for a Task to complete (such as a test that runs Ticks in a loop) never finishes, because the continuation never runs. In such code, add `ConfigureAwait(false)` to the awaits inside the factory. How to handle this in tests is covered in [Testing](../tools/testing.md).

Move heavy computation to another thread with `FlowBridge.FromTask(ct => Task.Run(() => Work(), ct))`. The same goes for work an Awaitable moved to another thread with `BackgroundThreadAsync()`: the flow receives the result on the World's thread (see [Threads](../guide/threads.md)).

When a completed Task resumes its flow:

- The bridge queues the completion to the World synchronously, on the thread that completed the Task. Whatever thread it completes on, the flow resumes at the next Tick or Flush point.
- A Task completed in `MonoBehaviour.Update` resumes its flow at the Flush at the end of Update in the same frame.

In the other direction, to wait on a flow from Task code, use `FlowHandle.AsTask()`. This Task completes on the World's thread right after the Tick, Flush, Run, or Dispose in which the flow ended. If the main thread blocks on this Task with `.Result` or `Wait()`, the Tick stops too, so it never finishes (see "FlowHandle" in [Flows and the World](../guide/flows-and-world.md)).

## uGUI buttons

The `FlowTask.Unity.UI` assembly (compiled only when `com.unity.ugui` is present) has helpers for waiting on button clicks.

```csharp
using var ok = view.OkButton.ClickedSignal(); // EventSignal<FlowUnit> over Button.onClick
await ok.Next();
```

- `button.ClickedSignal()` returns an `EventSignal<FlowUnit>`. For other controls, bridge the UnityEvent directly (`toggle.onValueChanged.ToSignal()`). Like `ToSignal()` above, call all of these from flow code.
- Clicks happen in uGUI's EventSystem (in the Update phase), so a waiting flow resumes at the Flush at the end of Update in the same frame.

### Catching clicks during an animation

`Next()` is an edge, so clicks before you start waiting (during a screen's opening animation, for example) are not delivered, and no warning is given (the same as UniTask's `button.OnClickAsync()`). For clicks you want to catch, create the signal and subscribe before the animation. Creating the signal before the animation is not enough.

```csharp
using var ok = view.OkButton.ClickedSignal();
using var okClicks = ok.Subscribe(BufferPolicy.Latest); // before the animation: a click during it is kept
await FadeIn(view);
await okClicks.Next();
```

For clicks you want to drop, such as repeated taps during a purchase, keep using `Next()` (see the purchase scenario in [Samples](samples.md)).

## Using it with UniTask

FlowTask and UniTask run on the same PlayerLoop. The conversion APIs and a migration guide are in [UniTask bridge and migration](../integrations/unitask.md); this section only covers ordering on Unity's PlayerLoop.

- UniTask inserts its runners at both the start and the end of each phase (`PlayerLoopTiming.LastUpdate` and so on). Continuations at `PlayerLoopTiming.Update` run before FlowTask's Tick. If such a continuation changes `Time.timeScale`, the new value takes effect from the next frame (see "Time" in [Setup](setup.md)).
- Continuations at `PlayerLoopTiming.LastUpdate` run after `MonoBehaviour.Update`. An Emit there resumes its receivers within the same frame, at the Flush at the end of PreLateUpdate at the latest.
- A flow that bridges a wait completed by UniTask's `DelayFrame` resumes within that frame.
- `ToUniTask()` posts its completion to the SynchronizationContext current at the time of the call. One called on the main thread completes in `ScriptRunDelayedTasks`.
- Reinstalling FlowTask's systems with `FlowTaskUnity.Configure` keeps UniTask's systems.

## Scope Tree window

Open it with `Window > FlowTask > Scope Tree`. During Play Mode, it shows the scope tree of the default World and of any World registered with `FlowWorldRegistry.Register(world)`, refreshing every 0.25 seconds.

- It shows each node's kind (scope, combinator, wait), its Clock, what it is waiting on and for how long (`waiting: Signal.Next for 2.3s`, for example), and scopes being canceled (in orange, with the reason). The top shows the number of live scopes and, for each Clock, its time, Pause state, and scale.
- The toolbar toggles auto-refresh, text view (the output of `FlowWorld.Dump()`), showing scopes only, a name filter, and copying the dump. If there are several Worlds, you can choose which one to show.
- Double-clicking the scope row of a FlowTask method opens the script that declares the method.
- If the game is stuck and won't move (a Pause you forgot to release), look at `Paused clocks:` at the top of the text view. For each paused Clock, it shows the pause count and the owners (for example, `Game: paused x2 by Main > PauseMenu, <outside any flow>`).
- When there is no World (outside Play Mode, after recompiling while playing, and so on), it shows the reason.

To show a World you created, register it when you create it and unregister it before disposing it (disposed Worlds disappear from the list automatically).

```csharp
FlowWorldRegistry.Register(world);   // shown in the Scope Tree window
// ...
FlowWorldRegistry.Unregister(world);
world.Dispose();
```

How to read the dump and the diagnostics API are covered in [Debugging and diagnostics](../tools/debugging.md).
