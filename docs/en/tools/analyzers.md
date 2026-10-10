# Analyzer rules

FlowTask comes with Roslyn analyzers that find, at compile time, code that goes against the execution model (scopes, unwinding on cancellation, deferred start). This page explains what each rule finds, why, and how to fix it.

| ID | Default | What it detects | Code fix |
|----|------|----------|-----------|
| [FLOW001](#flow001) | Error | A `catch` that receives `FlowCanceledException` and stops it | Yes |
| [FLOW002](#flow002) | Error | A FlowTask method directly `await`s an external awaitable such as `Task` | Yes |
| [FLOW003](#flow003) | Warning | A FlowTask is created but never `await`ed or started | Yes |
| [FLOW004](#flow004) | Warning | A lifetime handle is not stored, or not used | Yes |
| [FLOW005](#flow005) | Error | `await` of a FlowTask in an async function that doesn't return FlowTask | No |
| [FLOW006](#flow006) | Info | Waiting on a signal's `Next()` inside a loop | Yes |
| [FLOW007](#flow007) | Warning | Code calls `GetAwaiter()` on a FlowTask | No |
| [FLOW008](#flow008) | Warning | `Flow.Spawn(...)` whose return value isn't used | Yes |
| [FLOW009](#flow009) | Warning | `Flow.Spawn` in an event handler inside a FlowTask method | No |
| [FLOW010](#flow010) | Warning | An `await` without `Flow.NonCancelable` inside `finally` (and inside a `catch` that passes its exception on) | Yes |

Code that certainly won't work as written is an **error**, code that is usually a mistake but is sometimes intended is a **warning**, and suggestions on how to write things are **info**. You can change the severity ([Changing severity](#changing-severity)). Every rule is in the `FlowTask` category. The help links of the diagnostics in your IDE point to the sections of this page.

## Installation and display language

- **.NET / Godot (NuGet)**: the `FlowTask` package contains the analyzers and code fixes. Referencing it is enough to turn them on.
- **Unity**: they are in `Analyzers/FlowTask.Analyzers.dll` in the UPM package `com.katout.flowtask`, and apply to the compilation of every assembly that references the FlowTask assembly. Code fixes (an IDE-only feature) aren't included. Error rules stop Unity's compilation too. The info rule (FLOW006) doesn't appear in the Unity console.

The analyzers are loaded by compilers with Roslyn 3.8 or later (.NET 5 SDK or later).

Messages and code fix names are available in English and Japanese. If the display language of your IDE or compiler is Japanese, they appear in Japanese; otherwise, in English. `dotnet build` follows the OS display language, and you can switch it with the environment variable `DOTNET_CLI_UI_LANGUAGE=en` (or `ja`). The Unity console, and an IDE with a Unity project open, show English.

## Terms used on this page

- **FlowTask method**: an async method, local function, or lambda whose return type is `FlowTask` / `FlowTask<T>`. Another lambda or local function written inside its body is treated as a separate function.
- **FlowTask awaitable**: something whose awaiter is `FlowTask.Awaiter` / `FlowTask<T>.Awaiter` (`FlowTask`, `Once<T>`, `TaskBridge`, and types with a `GetAwaiter` extension that returns a FlowTask awaiter). When you await one, it registers with the current scope and can be unwound by cancellation.

---

<a id="flow001"></a>
## FLOW001: Do not stop FlowCanceledException in a catch clause (Error)

### What it detects

Among the `catch` clauses in a FlowTask method, those that can receive `FlowCanceledException` (the try block has an `await`, no earlier clause receives it first, and `when` doesn't exclude cancellation) and that match one of the following:

- `catch (FlowCanceledException)` or `catch (OperationCanceledException)` with a path that doesn't end in `throw` (`return`, `break`, `continue`, or running off the end of the clause).
- A catch-all clause (untyped `catch`, `catch (Exception)`, `catch (SystemException)`, `catch (TEx)`) with no filter that excludes `FlowCanceledException`. Clauses that end with `throw;` are included too, because their body runs on every cancellation.

The same goes for a try inside `catch` and `finally`. An `await` there also throws `FlowCanceledException` when `FlowWorld.Dispose` ends the flow.

These forms are not reported:

| Form | Reason |
|---|---|
| `catch (FlowCanceledException) { cleanup; throw; }` | Lets the cancellation pass |
| `catch (Exception e) when (e is not FlowCanceledException)` | The recommended form |
| `catch (Exception e) when (e is not OperationCanceledException)` | Lets the cancellation pass (external cancellations pass through too) |
| `catch (OperationCanceledException e) when (e is not FlowCanceledException)` | Receives only external cancellations |

An expression whose value can't be known, such as a method call inside a filter, is treated as "can receive" (`when (e is not FlowCanceledException && Log(e))` isn't reported; `when (Log(e))` is).

### Why

`FlowCanceledException` is the exception `await` throws to unwind a canceled scope. If you receive it and move on, the scope keeps running. Later `await`s wait just as in a live scope, so a loop keeps going around. When the code returns, it is reported at run time as swallowing (`FlowExceptionKind.SwallowedCancellation`), and the scope ends as canceled. Even so, the body of the `catch` (showing an error, retrying, saving) runs on every cancellation.

The recommended filter is `when (e is not FlowCanceledException)`. External cancellations (the `OperationCanceledException` of a Task canceled outside the flow, such as an HttpClient timeout) go into the handler like any other exception. Note that `catch (TaskCanceledException)` doesn't receive the `OperationCanceledException` from UniTask or `ThrowIfCancellationRequested`.

Write cleanup for cancellation in `finally`. An `await` in a `finally` entered because of cancellation runs to the end (for an `await` in a `finally` entered before the cancellation, see [FLOW010](#flow010)). To stop partway and switch to other work, write it with `FlowTask.Race`.

### Bad example

```csharp
async FlowTask<int> Download()
{
    try { return await FetchCount(); }
    catch (Exception e) // also catches FlowCanceledException
    {
        ShowError(e);
        return -1;
    }
}

async FlowTask NewsPanel()
{
    try { await ShowNews(); }
    catch (OperationCanceledException) // the UniTask habit: also catches FlowCanceledException
    {
        HidePanel();
    }
}
```

### Fixed example

```csharp
async FlowTask<int> Download()
{
    try { return await FetchCount(); }
    catch (Exception e) when (e is not FlowCanceledException)
    {
        ShowError(e);
        return -1;
    }
}

async FlowTask NewsPanel()
{
    try { await ShowNews(); }
    finally { HidePanel(); } // runs when the panel is canceled, too
}
```

### Code fix

- Clauses of a cancellation type: "End the catch clause with 'throw;'". If the body is only `return` or is empty, it also offers "Remove the catch clause (the cancellation passes on)".
- `catch (OperationCanceledException)` and catch-all clauses: adds `when (e is not FlowCanceledException)`. An untyped `catch` becomes `catch (Exception e)`, and an existing filter is joined with `&&`. In C# 8 and earlier, it writes `!(e is FlowCanceledException)`.

---

<a id="flow002"></a>
## FLOW002: Bridge external awaitables before awaiting them in a FlowTask method (Error)

### What it detects

An `await` inside a FlowTask method on something that isn't a FlowTask awaitable: `Task`, `ValueTask`, UniTask, Unity's `Awaitable` and `AsyncOperation`, Godot's `SignalAwaiter`, `Task.Yield()`, the result of `ConfigureAwait(...)`, and so on, plus `await foreach`.

`await using` is judged by the return value of the `DisposeAsync` it awaits at the end of the block. For `IAsyncDisposable`, that's a `ValueTask`, so it is reported; a `DisposeAsync` that returns FlowTask isn't.

### Why

While a flow waits on an external awaitable, its scope can't be unwound. So if the flow suspends there, the scope ends with a `FlowMisuseException`. The rest of the code and the `finally` blocks don't run; only cleanup from `Flow.AddCleanup` and `Flow.Own` runs. If the awaitable completes synchronously, the flow doesn't suspend, so at run time the problem only shows up depending on timing.

When you bridge with `FlowBridge.FromTask(ct => ...)`, the scope's cancellation reaches the external work through the `CancellationToken`, and when the work completes, the flow resumes on the World's thread. `.AsFlow()` on a Task or ValueTask only wraps work that is already running and doesn't pass cancellation through ([Task bridges](../integrations/task.md)).

### Bad example

```csharp
async FlowTask LoadStage()
{
    var json = await File.ReadAllTextAsync(path);           // no cancellation; the scope ends if it suspends here
    await Task.Delay(500);                                  // ignores the World's time and Pause
    await SceneManager.LoadSceneAsync("Stage").ToUniTask(); // a foreign awaitable
}
```

### Fixed example

```csharp
async FlowTask LoadStage()
{
    // The token is canceled when this scope is canceled.
    var json = await FlowBridge.FromTask(ct => File.ReadAllTextAsync(path, ct));
    await FlowTask.WaitForSeconds(0.5);                     // scope-aware, clock-aware wait
    await SceneManager.LoadSceneAsync("Stage").AsFlow();    // a cancel stops the wait; the load goes on
}
```

What to write instead, for each kind of awaitable:

| What you awaited | What to write instead |
|---|---|
| `Task`, `ValueTask` | `FlowBridge.FromTask(ct => ...)`. For one already running, `.AsFlow()` (cancellation doesn't reach it) |
| UniTask | `FlowUniTask.FromUniTask(ct => ...)`. For one already running, `.AsFlow()` (cancellation doesn't reach it; [UniTask](../integrations/unitask.md)) |
| Unity's `Awaitable` | `.AsFlow()`. The scope's cancellation calls `Awaitable.Cancel()` ([Unity bridges](../unity/bridges.md)) |
| Unity's `AsyncOperation` | `.AsFlow()`. A cancellation only stops the wait; the Unity operation keeps running ([Unity bridges](../unity/bridges.md)) |
| Godot's `SignalAwaiter` | Wait on `Next()` of the signal from `obj.ToFlowSignal(name)`, or bridge with `ToSignal(obj, name).AsFlow()` ([Godot signals](../godot/signals.md)) |
| `Task.Delay(milliseconds)` | `FlowTask.WaitForSeconds(seconds)`. It runs on the scope's Clock and obeys Pause |
| `Task.Yield()` | `FlowTask.NextFrame()` |
| `await foreach` | Consume the sequence outside the flow and bridge the result with `FlowBridge.FromTask(ct => ...)` |
| `await using` (`IAsyncDisposable`) | Dispose the resource with a synchronous `using`, or in a `finally` block with `await Flow.NonCancelable(FlowBridge.FromTask(_ => resource.DisposeAsync().AsTask()))` (`Flow.NonCancelable` keeps waiting until the disposal ends even if a cancel arrives meanwhile; [FLOW010](#flow010)) |

### Code fix

1. "Bridge with FlowBridge.FromTask (the scope's cancellation reaches the task)": turns `await Load(x)` into `await FlowBridge.FromTask(ct => Load(x, ct))`. Offered only when a token can be passed to the call (it has an optional `CancellationToken` parameter, it is passed `default` or `CancellationToken.None`, or there is an overload that takes a token).
2. "Bridge the running task with .AsFlow() (cancellation does not reach it)": turns `await t` and `await t.ConfigureAwait(false)` into `await t.AsFlow()`.

Bridging doesn't change how you catch exceptions; your existing `catch (IOException)` still receives them.

---

<a id="flow003"></a>
## FLOW003: A FlowTask must be awaited or started (Warning)

### What it detects

- An expression statement of type `FlowTask` / `FlowTask<T>` (`DoThing();`), an assignment to a discard (`_ = DoThing();`), and a lambda that discards the value (`Action a = () => DoThing();`).
- `Discard()` right after creation (`DoThing().Discard();`). `Discard()` releases a task without starting it; it isn't UniTask's `Forget()`. `t.Discard()` on a FlowTask stored in a variable (a branch you decided not to start) isn't reported.
- A FlowTask local variable that is never read, and collections of them (`var list = new List<FlowTask> { A(), B() };` and then not used).
- A local variable that is read only on some paths (`var t = A(); if (c) await t;`). Paths that exit with `throw` are considered fine not to start it.
- `TaskBridge` (the return value of `FlowBridge.FromTask(...)` or `.AsFlow()`) follows the same rules, except for "only on some paths".

Discarding the `FlowHandle` that `world.Run(...)` returns is fine. Discarding the `FlowHandle` of `Flow.Spawn(...)` in a statement is [FLOW008](#flow008), so if it's fine for it to end with the parent scope, write `_ = Flow.Spawn(...)`.

### Why

FlowTask uses deferred start. Calling it only creates the task; it starts only when it is `await`ed, passed to `Flow.Spawn` or `FlowWorld.Run`, or when a composition you passed it to starts. A FlowTask you throw away doesn't run, even if you discard it with `_ =`. A task that never starts simply doesn't run, and nothing is reported at run time.

To run things concurrently, choose by how they should end:

- `Flow.Spawn(task)` starts a child of the current scope, which stops together with the parent scope when the parent ends. The child's exception cancels the parent and is rethrown in the parent's caller.
- `FlowWorld.Run(task)` (from inside a flow, `FlowWorld.Current.Run(task)`) starts it as a root flow, which runs independently of this flow. Rewrite work you used to `Forget()` in UniTask with this.

### Bad example

```csharp
async FlowTask Battle(bool tutorial)
{
    PlayBgm();                  // never runs
    _ = SpawnEnemies();         // never runs
    SendAnalytics().Discard();  // never runs (Discard is not Forget)
    var intro = ShowIntro();
    if (tutorial) await intro;  // never runs when tutorial is false
}
```

### Fixed example

```csharp
async FlowTask Battle(bool tutorial)
{
    _ = Flow.Spawn(PlayBgm());                  // runs concurrently, stops when Battle ends
    await SpawnEnemies();                       // runs to completion first
    FlowWorld.Current.Run(SendAnalytics());     // outlives this flow
    var intro = ShowIntro();
    if (tutorial) await intro;
    else intro.Discard();                       // not started on purpose
}
```

### Code fix

For an expression statement, it offers three fixes: "Await the FlowTask", "Start it as a child with Flow.Spawn (stops when this scope ends)", and "Run it independently with FlowWorld.Current.Run".

- The second writes `_ = Flow.Spawn(DoThing());` ([FLOW008](#flow008)). It isn't offered where a variable named `_` is in scope.
- None are offered outside a FlowTask method. To start it from outside a scope, write `world.Run(DoThing())`.

---

<a id="flow004"></a>
## FLOW004: Store lifetime handles in a variable (Warning)

### What it detects

Calls that return a value of a type marked `[LifetimeHandle]` (`ScopedHandle` from `Clock.Pause()`, `Subscription<T>`, `EventSignal<T>`) and whose value is thrown away as an expression statement (`clock.Pause();`, `signal.Subscribe(policy);`), or stored in a local variable without `using` that is never used.

`_ = clock.Pause();` is taken to mean "keep it until the end of the scope" and isn't reported.

### Why

A lifetime handle is owned by the scope that created it and released at the end of that scope. If you don't store it, or don't use it, a Pause, a subscription, or an event listener silently lives on until the scope ends. One created outside a flow stays until you dispose it.

### Bad example

```csharp
async FlowTask<bool> RetryDialog()
{
    game.Pause();                    // the game stays paused until the scope ends, after the dialog closes
    var music = bgm.Pause();         // never used: stays until the scope ends
    return await AskRetry();
}
```

### Fixed example

```csharp
async FlowTask<bool> RetryDialog()
{
    using var pause = game.Pause();  // released at the end of this block
    using var music = bgm.Pause();
    return await AskRetry();
}
```

### Code fix

- Expression statements: "Declare it with 'using var pause'". The variable name comes from the method name (`subscription` for `Subscribe`).
- Unused local variables: "Add 'using' to the declaration of 'pause'".

It isn't offered where `using var` can't be written (the embedded statement of an `if`, directly under a `switch` section).

---

<a id="flow005"></a>
## FLOW005: Await FlowTask awaitables only in FlowTask methods (Error)

### What it detects

`await` on a FlowTask awaitable (including `handle.Join()`) in an async function that doesn't return FlowTask. This covers functions that return `Task`, `ValueTask`, `async void`, UniTask, or `IAsyncEnumerable`, and top-level statements. It also includes `await using` on a `DisposeAsync` that returns FlowTask.

### Why

An await on a FlowTask waits as a child of whatever scope is current at that moment. Only FlowTask methods become scopes; async methods that return `Task` or similar aren't scopes.

- Outside a flow, that await throws `FlowMisuseException`.
- When the method is called from inside a flow, only awaits that complete immediately get through. On an await that doesn't complete, the rest of the method is dropped, and the scope running at that await (usually the scope that called the method) ends with a `FlowMisuseException`. That Task never completes.

### Bad example

```csharp
async void Start()                 // a Unity MonoBehaviour
{
    await Title();                 // awaits a FlowTask outside any scope
}

async Task<int> GetScore() => await Arcade(); // awaits a FlowTask in a Task method
```

### Fixed example

```csharp
void Start() => world.Run(Title());                    // a root flow of the World
Task<int> GetScore() => world.Run(Arcade()).AsTask();  // bridge back to Task
```

There is no code fix.

- The `AsTask()` Task completes at the end of the Tick, Flush, Run, or Dispose in which the flow ended. Flows advance with Ticks, so it doesn't complete unless code keeps the World going. On Unity and Godot, the integration ticks every frame; in a console program, your own loop ticks ([Your first flow](../getting-started/first-flow.md)).
- On the World's thread, don't block on this Task with `.Result` or the like. The Ticks stop too, so it never ends.
- How `AsTask()` behaves is described in [Task bridges](../integrations/task.md).

---

<a id="flow006"></a>
## FLOW006: Subscribe before a loop that awaits the next emit of a signal (Info)

### What it detects

`await` on `Next()` / `NextOrClosed()` of a `Signal<T>` or `EventSignal<T>` inside a loop in a FlowTask method (including as an argument of `FlowTask.Race(...)`, and `FlowProperty<T>.Changed.Next()`).

`Next()` on a `Subscription<T>`, and receivers that can change on each iteration (`signals[i].Next()`), aren't reported. It doesn't stop the build; it appears as a suggestion in the IDE.

### Why

`Next()` waits only for the next Emit after you call it (an edge). Every Emit that happens while the loop body is doing something else (an animation, `WaitForSeconds`) is lost. A subscription created with `Subscribe` before the loop keeps the Emits from that time ([Signals](../guide/signals.md)).

### Bad example

```csharp
while (true)
{
    var damage = await _damaged.Next();      // hits during the knockback below are lost
    await FlowTask.WaitForSeconds(0.8);
}
```

### Fixed example

```csharp
using var hits = _damaged.Subscribe(BufferPolicy.Latest);   // keeps the newest hit
while (true)
{
    var damage = await hits.Next();
    await FlowTask.WaitForSeconds(0.8);
}

// Every hit in order: a bounded queue
using var queue = _damaged.Subscribe(BufferPolicy.Queue(16, BufferOverflow.DropOldest));
```

### Code fix

"Subscribe before the loop (BufferPolicy.Latest)": inserts `using var subscription = x.Subscribe(BufferPolicy.Latest);` before the loop, and changes `x.Next()` inside the loop to `subscription.Next()`.

- It is offered only when the receiver is an expression without side effects and the loop sits directly in a block. It isn't offered for loops that check the receiver for null (`while (target != null)`).
- `BufferPolicy.Queue` isn't offered. The capacity and the overflow behavior are yours to decide, so to receive everything in order, write it yourself, like the last line above.

If the edge behavior is what you want, such as dropping taps during an animation, ignore or suppress it.

---

<a id="flow007"></a>
## FLOW007: Await a FlowTask instead of calling its GetAwaiter() (Warning)

### What it detects

Calls to `GetAwaiter()` written in code whose return value is a FlowTask awaiter. Calls inside a method named `GetAwaiter` (your own awaitable type returning a FlowTask awaiter) aren't reported.

### Why

FlowTask uses deferred start, and starts inside `GetAwaiter()`. So a `GetAwaiter()` written in code also starts the task as a child of the current scope, even if you never `await` it. The awaiter's `IsCompleted` keeps the value from the moment of the call and never changes. Task's `GetAwaiter()` starts nothing, so if you call it with the same expectation, the task starts running without you meaning it to ([Migrating from UniTask](../integrations/unitask.md)).

### Bad example

```csharp
var awaiter = LoadIcons().GetAwaiter();   // LoadIcons starts here, as a child of this scope
if (awaiter.IsCompleted) Show();          // the state at the call above; it never changes
```

### Fixed example

```csharp
var icons = Flow.Spawn(LoadIcons());      // runs alongside, and ends with this scope
await Open();
await icons.Join();
```

### Code fix

None. An awaiter used by hand can be read only when `IsCompleted` is true (when it completed synchronously). Registering a continuation with `OnCompleted` is misuse, the same as a suspended await in a method that isn't a FlowTask method ([FLOW005](#flow005)). For code that reads a completed awaiter by hand on purpose, suppress it with `#pragma warning disable FLOW007`.

---

<a id="flow008"></a>
## FLOW008: Do not drop the handle of Flow.Spawn silently (Warning)

### What it detects

An expression statement of `Flow.Spawn(...)` whose return value (`FlowHandle` / `FlowHandle<T>`) isn't used (`Flow.Spawn(PlayBgm());`), and an expression-bodied member that throws the value away (`void StartBgm() => Flow.Spawn(PlayBgm());`). The following are reported too:

- The body of an expression lambda written inside a FlowTask method (`items.ForEach(x => Flow.Spawn(Explode(x)))`).
- A local variable that holds the handle and is never read (`var h = Flow.Spawn(PlayBgm());`).

Both the `FlowTask` and `FlowTask<T>` overloads are covered, as is `Spawn(...)` written with `using static Katout.FlowTask.Flow;`. It also checks outside FlowTask methods (synchronous helpers called from a flow), and inside `finally` and `catch`. In a canceled scope, a child starts in the cleanup after `FlowCanceledException` has reached the code, and it stops when the scope ends (it doesn't start during `World.Dispose`).

These forms are not reported:

- `_ = Flow.Spawn(...)` (and `var _ = Flow.Spawn(...)`), a handle stored in a field or in a local variable that is read, one used as an argument or in `return`, and one whose handle is used, as in `Flow.Spawn(x).Cancel()`. A `world.Run(...)` statement.
- The body of an expression lambda written outside a FlowTask method (`Assert.Throws<FlowMisuseException>(() => Flow.Spawn(x))`). A Spawn that runs right away outside a flow throws, so it never stops silently, and this is the way to check that a call throws, so it isn't checked.
- Spawns inside event handlers, which [FLOW009](#flow009) reports.

### Why

A child of `Flow.Spawn` stops when its parent scope ends, even when the parent returns normally. If you write it as fire-and-forget out of a UniTask `Forget()` habit, long-running work (music, particles, a retry loop) started from a short-lived parent flow silently stops the moment the parent returns. A loop that ends together with its screen is exactly what Spawn is for, so there is no warning at run time. Instead, a statement that doesn't use the handle is flagged at compile time, so that you write out which of two intentions you mean:

- It's fine for it to end with the parent scope: `_ = Flow.Spawn(...)`. This is the common C# way of saying "I'm discarding this on purpose".
- It should outlive the parent: `FlowWorld.Current.Run(...)` (`world.Run(...)` outside a flow). This is the same rewrite as [FLOW003](#flow003), and it becomes a root flow independent of this one. Its exceptions reach `OnUnhandledException` instead of the parent's caller, and its Clock is the World's default Clock (unless you pass one as an argument).

### Bad example

```csharp
async FlowTask EnterTown()
{
    Flow.Spawn(PlayBgm("town"));     // meant to play on, but stops when EnterTown returns
    await FadeIn();
}
```

### Fixed example

```csharp
async FlowTask EnterTown()
{
    FlowWorld.Current.Run(PlayBgm("town"));  // outlives EnterTown
    await FadeIn();
}

async FlowTask TownScreen()
{
    _ = Flow.Spawn(AmbientLoop());           // ends with the screen, on purpose
    await _closed.Next();
}
```

### Code fix

- "Discard the handle with '_ =' (the child stops when this scope ends)": turns it into `_ = Flow.Spawn(...)`. For a local variable that isn't read, the whole declaration becomes `_ = Flow.Spawn(...);`. It isn't offered where a variable named `_` is in scope.
- "Run it independently with FlowWorld.Current.Run (it does not stop when this flow ends)": turns it into `FlowWorld.Current.Run(...)`. `FlowWorld.Current` is set only while a flow is running, so it is offered only inside FlowTask methods.

The latter also changes where exceptions go and which Clock is used (see "Why" above), so choose the one that matches your intent.

---

<a id="flow009"></a>
## FLOW009: Do not call Flow.Spawn in an event handler (Warning)

### What it detects

`Flow.Spawn` inside a handler registered to an event with `+=` inside a FlowTask method (`button.Clicked += () => Flow.Spawn(OpenShop());`). Forms that are called synchronously right away (`list.ForEach(...)`), and registrations that aren't `event`s, such as `UnityEvent.AddListener`, aren't reported.

### Why

`Flow.Spawn` creates a child of whatever scope is current at that moment. An event handler runs when the event fires. If no flow is running at that moment, `Flow.Spawn` throws `FlowMisuseException`; if the event fires from inside another flow, the child becomes a child of that flow and ends with it.

Inside the handler, you can't rely on `FlowWorld.Current` either (it is null outside a flow, and inside another flow it is that flow's World). Save the World with `var world = FlowWorld.Current;` before registering, and start work in the handler with `world.Run(...)`. For an event that may fire on another thread, hand it over to the World's thread with `world.Post(() => world.Run(OpenShop()))` (`Run` can only be called on the World's thread). The most straightforward approach is to turn the event into a signal and wait for it in the flow.

### Bad example

```csharp
async FlowTask Menu()
{
    shopButton.Clicked += () => Flow.Spawn(OpenShop()); // joins whatever scope is running when it fires, or none
    await _closed.Next();
}
```

### Fixed example

```csharp
async FlowTask Menu()
{
    // Turn the event into a signal owned by this flow.
    using var clicks = FlowBridge.FromCallback<FlowUnit>(emit =>
    {
        Action h = () => emit(FlowUnit.Default);
        shopButton.Clicked += h;
        return () => shopButton.Clicked -= h;
    });
    var r = await FlowTask.Race(clicks.Next().WithoutResult(), _closed.Next().WithoutResult());
    if (r.Index == 0) await OpenShop();
}
```

There is no code fix.

---

<a id="flow010"></a>
## FLOW010: Mark the awaits of a finally block with Flow.NonCancelable (Warning)

### What it detects

The following `await`s (on FlowTask awaitables) inside a FlowTask method:

- An `await` inside `finally`.
- An `await` inside a `catch` that passes its exception on (one that contains `throw;` or `throw new ...(e)`). Excluded are `catch` clauses that name `FlowCanceledException` or `OperationCanceledException`, and `catch` clauses that also receive cancellation (reported by [FLOW001](#flow001)).

`await Flow.NonCancelable(...)`, an `await` on a local variable initialized with it, and an `await` inside a `catch` that handles its exception and ends aren't reported.

The rule doesn't look at how a `finally` block is entered, so it also reports the `await`s of a `finally` that only a cancellation can enter (one whose `try` block only waits on `FlowTask.Never()`, for example). The mark changes nothing there, so add it, or suppress the warning.

### Why

After a scope's cancellation has reached the code (after leaving the body with `FlowCanceledException` and entering `finally`), the `await`s in `catch` and `finally` run to the end. On the other hand, a `finally` entered before the cancellation (through `return`, the end of the try block, or an exception) isn't cleanup yet. If a cancellation comes during an `await` there (a scene change, a `Race` another branch won), that `await` throws `FlowCanceledException`, and the following happens:

- The rest of the block (a `Destroy` or a save after it) doesn't run.
- The exception the block was carrying (one thrown inside the `try`) is replaced by `FlowCanceledException`, following C# rules, and is lost.

At run time, the library can't see whether the current `await` is in the body or in a `finally`, or what exception is being carried. So it tells you at compile time and lets you choose whether to protect it with `Flow.NonCancelable`.

When you `await` with `Flow.NonCancelable(task)`, cancellation of ancestors doesn't reach the task; the scope waits for the task and receives its result or exception, and the rest of the block runs too. The cancellation reaches the scope at the next unmarked `await`. The exception that was being carried is reported as `FlowExceptionKind.Undelivered` when the scope ends as Canceled.

Mark only cleanup that ends on its own (animations, saves). A marked wait for input or for another flow can't be canceled, and holds up the flow and the scopes waiting on it until it ends. If a canceled scope keeps waiting in cleanup for 10 seconds, `FlowWarningKind.LongCleanup` is raised. If you need a time limit, use `FlowTask.Race` inside the mark. `World.Dispose` still ends marked `await`s ([Scopes and cancellation](../guide/scopes-and-cancellation.md)).

### Bad example

```csharp
async FlowTask<Choice> Confirm()
{
    var view = OpenView();
    try
    {
        return await view.Choose();
    }
    finally
    {
        await view.PlayClose(); // canceled while it plays: Destroy does not run
        view.Destroy();
    }
}
```

### Fixed example

```csharp
async FlowTask<Choice> Confirm()
{
    var view = OpenView();
    try
    {
        return await view.Choose();
    }
    finally
    {
        await Flow.NonCancelable(view.PlayClose()); // plays to its end, then Destroy runs
        view.Destroy();
    }
}
```

### Code fix

"Await it with Flow.NonCancelable (only for cleanup that ends by itself)": rewrites `await x` to `await Flow.NonCancelable(x)`. Wrapping a wait for input creates a hang, so there is no fix-all. Choose them one at a time.

---

## Changing severity

On .NET and Godot, you can change the severity of each rule (`error` / `warning` / `suggestion` / `silent` / `none`) in `.editorconfig`.

```ini
[*.cs]
dotnet_diagnostic.FLOW003.severity = error
dotnet_diagnostic.FLOW006.severity = none
# All FlowTask rules at once (the settings of individual rules take precedence).
dotnet_analyzer_diagnostic.category-FlowTask.severity = error
```

On Unity, severities in `.editorconfig` aren't applied to compilation. Use `Assets/Default.ruleset` (which applies to every assembly). A ruleset named after an asmdef assembly (`Assets/<assembly name>.ruleset`) produces a warning and isn't used in Unity 6 (the only rulesets you can put directly under `Assets` are `Default.ruleset` and ones named after predefined assemblies, such as `Assembly-CSharp.ruleset`). To turn off a rule for just one asmdef assembly, write something like `-nowarn:FLOW001` in the `csc.rsp` next to the asmdef.

```xml
<?xml version="1.0" encoding="utf-8"?>
<RuleSet Name="Game" ToolsVersion="16.0">
  <Rules AnalyzerId="FlowTask.Analyzers" RuleNamespace="FlowTask.Analyzers">
    <Rule Id="FLOW003" Action="Error" />
    <Rule Id="FLOW006" Action="None" />
  </Rules>
</RuleSet>
```

For individual places, suppress with `#pragma` or `SuppressMessage`. Error rules can be suppressed too.

```csharp
#pragma warning disable FLOW002 // not bridged on purpose: this test checks a foreign await
var v = await tcs.Task;
#pragma warning restore FLOW002

[System.Diagnostics.CodeAnalysis.SuppressMessage("FlowTask", "FLOW001", Justification = "Swallowing is the behavior under test.")]
async FlowTask Swallower() { /* ... */ }
```

In test code too, don't turn rules off for a whole folder; suppress only the places written that way on purpose, as above. FLOW002 and the others stop mistakes that pass only in tests ([Testing](testing.md)).

---

## Coexisting with other analyzers

| Analyzer | What happens | What to do |
|---|---|---|
| VSTHRD103, CA1849 | Their code fixes, which replace synchronous calls with async versions, create external awaits that trigger FLOW002 | Wrap them with `FlowBridge.FromTask(ct => ...)` |
| RCS1261 | Recommends `await using` for `IAsyncDisposable`. The `ValueTask` of the implicitly awaited `DisposeAsync` isn't a FlowTask awaitable, so in a FlowTask method it triggers [FLOW002](#flow002) | Turn it off |
| VSTHRD200, RCS1046 | Require an `Async` suffix on FlowTask method names (`ShowDialog()`, `Patrol()`) | Turn them off (see below) |
| CA2000 | Reports values whose ownership was handed to the scope, as in `Flow.Own(new X())`, as "not disposed" | See below |

FlowTask's documentation and samples don't add `Async` to methods that return FlowTask. In game flows, almost every method returns FlowTask, so the suffix carries no information, and [FLOW003](#flow003) reports a missing `await`.

```ini
[*.cs]
dotnet_diagnostic.RCS1261.severity = none
dotnet_diagnostic.VSTHRD200.severity = none
dotnet_diagnostic.RCS1046.severity = none
```

CA2000 appears when `AnalysisMode=All`. Setting `dotnet_code_quality.CA2000.dispose_ownership_transfer_at_method_call = true` makes it go away, but that also affects calls other than `Flow.Own`. If you want to keep that detection, suppress it only on the `Flow.Own` lines.

```csharp
#pragma warning disable CA2000 // Flow.Own disposes it when this scope ends
var file = Flow.Own(new SaveFile());
#pragma warning restore CA2000
```
