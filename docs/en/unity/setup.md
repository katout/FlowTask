# Unity setup

This page explains how FlowTask runs in Unity and how to configure it. You will learn how the default World runs on the PlayerLoop, how time advances, what happens when Play Mode ends or the domain reloads, and how unhandled exceptions show up in the Console.

Installing the packages (what to write in `Packages/manifest.json`, the optional packages) is covered in [Installation](../getting-started/installation.md). This page is about what comes after installation.

## Your first flow

Once installed, a default `FlowWorld` is created at startup and runs every frame on the PlayerLoop. Start a flow with `FlowTaskUnity.World.Run`.

```csharp
using Katout.FlowTask;
using Katout.FlowTask.Unity;
using UnityEngine;

public sealed class Boot : MonoBehaviour
{
    void Start() => FlowTaskUnity.World.Run(Game());

    static async FlowTask Game()
    {
        await FlowTask.WaitForSeconds(1.0); // DefaultClock advances like Time.deltaTime
        // ...
    }
}
```

The World is created at `RuntimeInitializeLoadType.SubsystemRegistration`, so it exists before any `Awake`. Reading `FlowTaskUnity.World` when there is no World (in Edit Mode, after `Shutdown`, after a recompile during play) throws an `InvalidOperationException` that names the cause. Check whether there is one with `FlowTaskUnity.IsInstalled`. The other examples on this page assume the same three `using` directives (`Katout.FlowTask`, `Katout.FlowTask.Unity`, `UnityEngine`), plus `using System;` for examples that use `Math` or `ArgumentOutOfRangeException`.

If you want a flow to stop when its GameObject is deactivated, start it with `gameObject.RunWhileActive` instead of `World.Run` (see [GameObject lifetime](lifetime.md)).

## Packages and assemblies

FlowTask supports Unity 2023.1 and later. It is tested in the Unity 6 (6000.3) Editor (EditMode and PlayMode), in Windows Mono and IL2CPP players, and with an Android IL2CPP build (built, but not run on a device). It has not been tested on 2023.1 or 2023.2 themselves.

The package is split into the following assemblies (asmdefs). If your code lives in an asmdef, add the assemblies you use to its references.

| asmdef | References | Compiled when |
| --- | --- | --- |
| `FlowTask` | None (`noEngineReferences`) | Always |
| `FlowTask.Unity` | FlowTask | Always. PlayerLoop integration, bridges, GameObject lifetime |
| `FlowTask.Unity.UI` | FlowTask, FlowTask.Unity, UnityEngine.UI | `com.unity.ugui` is present (`FLOWTASK_UGUI`) |
| `FlowTask.Unity.Editor` | FlowTask, FlowTask.Unity | Editor only. The Scope Tree window |
| `FlowTask.Testing` | FlowTask | Editor and test players only (`UNITY_INCLUDE_TESTS`). `autoReferenced: false` |
| `FlowTask.Testing.NUnit` | FlowTask, FlowTask.Testing, `nunit.framework.dll` | Same as above |
| `FlowTask.UniTask` | FlowTask, UniTask | `com.cysharp.unitask` 2.0.0 or later is present (`FLOWTASK_UNITASK`) |

- If the package they depend on is missing, `FlowTask.Unity.UI` and `FlowTask.UniTask` are silently left out of compilation (no error).
- If you installed UniTask as a `.unitypackage` into `Assets/Plugins/UniTask`, Unity does not see it as a package, so `FLOWTASK_UNITASK` is not defined and code that uses `FlowUniTask` fails with `CS0246`. Add `FLOWTASK_UNITASK` for each platform under Player Settings > Other Settings > Scripting Define Symbols.
- The two testing assemblies are compiled only for packages listed in the manifest's `testables` (see [Installation](../getting-started/installation.md)).

### C# 10 and csc.rsp

The package source is written in C# 10. Unity defaults to C# 9, so each asmdef has a `csc.rsp` next to it containing the single line `-langversion:10.0`, which raises the language version for that assembly only. The language version of your own code stays at the default.

Some IDEs show the C# 10 syntax in the package source as errors. The Rider package and the Visual Studio package 2.0.24 or later carry the `csc.rsp` language version into the csproj files that Unity generates for the IDE. If your IDE does not and shows errors, they do not affect Unity's compilation.

### .meta files

Packages installed from git are read-only, so the repository includes a `.meta` file for every file and folder in the package. If you embed the package in `Packages/` and modify it, move the `.meta` files along with any file you rename or move (the GUID is the asset's identity).

The package folders also contain csproj files for .NET. Unity imports them as plain assets, and they play no part in compilation.

## Tick and Flush

The default World ticks once per frame, at the start of the Update phase (just before `Update.ScriptRunBehaviourUpdate`). It also calls `FlowWorld.Flush()` at the end of the Update phase and at the end of the PreLateUpdate phase. Flush does not advance time; it processes the resumptions scheduled up to that point (Emit, `FlowProperty.Set`, UnityEvents, completed bridges).

```
EarlyUpdate        input
FixedUpdate        physics
Update             [FlowTask Tick] -> MonoBehaviour.Update, uGUI clicks, coroutines -> [Flush]
PreLateUpdate      MonoBehaviour.LateUpdate, animation events -> [Flush]
PostLateUpdate     rendering
```

This order means:

- Tick comes after input and physics, so flows act on the current frame's state. State changed by flows is visible to `MonoBehaviour.Update` in the same frame.
- An Emit from `MonoBehaviour.Update` (including uGUI clicks), or from a coroutine or Awaitable continuation that runs in the Update phase (`yield return null`, `NextFrameAsync`, and so on), resumes its receivers at the Flush at the end of Update, within the same frame and before rendering. Emits from `LateUpdate` and animation events are handled by the Flush at the end of PreLateUpdate.
- Emits in FixedUpdate (including continuations of `WaitForFixedUpdate` and `FixedUpdateAsync`) are handled by that frame's Tick. Emits after PreLateUpdate (continuations of `WaitForEndOfFrame` and `EndOfFrameAsync`, for example) are handled by the next frame's Tick. To handle them sooner, add Flush points (see "Settings" below).
- An empty Flush only reads a few fields, so adding Flush points costs almost nothing.

When you deactivate a GameObject bound with `RunWhileActive`, FlowTask also flushes right then, apart from the points in the table (see [GameObject lifetime](lifetime.md)).

Each system shows up in the Profiler under the name of a marker type such as `FlowTaskUnity.FlowTaskTick`.

The ordering rules inside a Tick, such as Emit not resuming receivers synchronously, are in [Execution model](../advanced/execution-model.md).

## Time

The default World ticks with `Time.unscaledDeltaTime` (clamped by `Time.maximumDeltaTime`), and sets `World.DefaultClock.TimeScale` every frame so that `DefaultClock` advances by the same amount as that frame's `Time.deltaTime`.

```csharp
var world = FlowTaskUnity.World;
await FlowTask.WaitForSeconds(2.0);                     // DefaultClock: follows Time.timeScale, stops at 0
await FlowTask.WaitForSeconds(2.0, world.UnscaledClock); // keeps running while Time.timeScale is 0
```

- `DefaultClock` advances by exactly `Time.deltaTime` every frame, including the first frames after startup. It stops at `Time.timeScale = 0`.
- `UnscaledClock` keeps running while `Time.timeScale = 0`. You cannot change its TimeScale or pause it.
- Long frames (scene loads, breakpoints) are clamped by `Time.maximumDeltaTime`, as in Unity. Timed waits that pass their deadline within a single Tick complete in the order they started waiting, not in deadline order (in argument order for a Race).
- `WaitForSeconds` finishes on the first Tick where the time, summed from dt as a double, reaches the target. Waiting 0.1 seconds at 60 fps finishes on the 7th Tick (six additions of 1/60 fall just short of 0.1). To wait a number of frames, use `FlowTask.DelayFrames`.

General use of Clocks, Pause, and TimeScale is covered in [Time and Clocks](../guide/time-and-clocks.md).

### Your own slow motion and pause

The integration overwrites `DefaultClock.TimeScale` every frame, so do not change it yourself. Do slow motion and pause for your game with Clocks that are children of `DefaultClock`.

```csharp
var world = FlowTaskUnity.World;
var game = world.CreateClock("Game");                    // a child of DefaultClock: follows Time.timeScale
var ui = world.CreateClock("UI", world.UnscaledClock);   // keeps running while Game is paused and at Time.timeScale 0
game.TimeScale = 0.5;                                    // slow motion on top of Time.timeScale
```

- **Run dialogs and menus on the UI Clock** (`Flow.WithClock(ui, Menu())`). If a menu that calls `Pause()` on the game Clock runs on that same Clock, it also stops its own resumption (you get a `PausedOwnClock` warning).
- **For slowing down a single enemy**, create a temporary Clock with `Flow.CreateClock`. It is removed when the scope that created it ends.

  ```csharp
  async FlowTask SlowedAttack()
  {
      var slow = Flow.CreateClock("slow", Flow.CurrentClock); // removed when this method's scope ends
      slow.TimeScale = 0.5;
      await Flow.WithClock(slow, Attack());
  }
  ```

  If you call `Flow.CreateClock` directly inside an AI loop, the Clocks pile up until the loop's scope (the enemy's lifetime) ends, so put the call in its own method as above. For a Clock that lives until the World ends, use `FlowWorld.CreateClock`.

- If the product of `Time.timeScale` and the scales of the child Clocks would become non-finite, setting the scale on `DefaultClock` is rejected with `ArgumentOutOfRangeException`. The integration logs the exception and ticks that frame with the previous scale.

Pausing a Clock stops only the flows on it. To stop Animators and physics with it, set `Time.timeScale` to 0 while the Clock is paused (`PauseLink` in [Samples](samples.md)).

### In depth: frames where DefaultClock.TimeScale differs from Time.timeScale

`DefaultClock.TimeScale` is normally the same as `Time.timeScale`. But on frames where `Time.deltaTime` is not "unscaled time × timeScale", it gets the ratio instead, so that `DefaultClock` advances by exactly `Time.deltaTime`. These frames are:

- The first frames after startup. Unity reports a provisional `Time.deltaTime`.
- Frames where `Time.timeScale` was changed before the Tick (in FixedUpdate, EarlyUpdate, Input System callbacks in PreUpdate, or UniTask continuations at `PlayerLoopTiming.Update` or earlier). The new value takes effect from the next frame.
- While `Time.captureDeltaTime` is set (when recording at a fixed frame rate). `DefaultClock` advances in recording time, while `UnscaledClock` and its children advance in real time (`Time.unscaledDeltaTime`). Do waits that should line up with the recording on `DefaultClock` or one of its children.

## Settings

Settings live in `FlowTaskSettings`, and you can change them from code or from an asset.

```csharp
FlowTaskUnity.Configure(new FlowTaskSettings
{
    FlushPoints = FlowFlushPoints.Default | FlowFlushPoints.AfterFixedUpdate,
    OnUnhandledException = info => MyCrashReporter.Report(info.Exception, info.ScopePath), // replaces the default logging
});
```

| Setting | Meaning | Default |
| --- | --- | --- |
| `AutoTick` | Whether to Tick at the start of Update. If `false`, you Tick yourself (the Flush points still run) | `true` |
| `FlushPoints` | Where to Flush. `FlowFlushPoints` flags: `AfterEarlyUpdate`, `AfterFixedUpdate`, `AfterUpdate`, `AfterLateUpdate`, `EndOfFrame`, `Default`, `All`, `None` | `Default` (`AfterUpdate \| AfterLateUpdate`) |
| `AutoInstall` | Whether to create the World at startup and install it into the PlayerLoop. Read only from the asset | `true` |
| `OnUnhandledException` | Replaces the handling of unhandled exceptions that reach the root. Code only | See "Unhandled exceptions and warnings" below |

- You can call `Configure` at any time. `OnUnhandledException` takes effect immediately; changes to Tick and the Flush points take effect from the next frame. Reinstalling with `FlowTaskUnity.Configure` keeps the systems that other libraries, such as UniTask, have added to the PlayerLoop.
- To configure with an asset, create a `FlowTaskSettingsAsset` with `Assets > Create > FlowTask > Settings`, name it `FlowTaskSettings`, and put it in a `Resources` folder. It is applied before the first scene loads.
- Order at startup: at `SubsystemRegistration` the World is created with the default settings (unless `FLOWTASK_DISABLE_AUTO_INSTALL` is defined), and the asset is applied at `BeforeSceneLoad`. If your code calls `Initialize`, `Configure` or `Shutdown` before that (from a `RuntimeInitializeOnLoadMethod` at `AfterAssembliesLoaded` or `BeforeSplashScreen`), the asset is not applied: the settings from code win, `OnUnhandledException` included, and a World you installed stays even if the asset sets `AutoInstall` to `false`. With `FLOWTASK_DISABLE_AUTO_INSTALL` defined, the asset is not read.
- You can read the current settings with `FlowTaskUnity.Settings`. It returns a copy: changing it has no effect until you pass it to `Configure`.

### Turning off automatic startup

If you set `AutoInstall` to `false` in the asset, or define the scripting symbol `FLOWTASK_DISABLE_AUTO_INSTALL`, no World is created automatically. Call `FlowTaskUnity.Initialize(settings)` yourself to start. To make a World you created the default World, pass it with `Initialize(settings, myWorld)`. A World passed this way is not disposed by `Shutdown`. If it already has an `OnUnhandledException` handler, that handler is kept (`FlowTaskSettings.OnUnhandledException` and the default logging do not apply to it); otherwise the integration installs its handler and `Shutdown` removes it again.

### Ticking yourself

With `AutoTick = false`, write the same steps as the default yourself.

```csharp
void Update()
{
    var world = FlowTaskUnity.World;
    FlowLifetime.PollWatched(); // completes WaitForDestroy of objects that never became active
    if (world.DefaultClock.TimeScale != Time.timeScale) world.DefaultClock.TimeScale = Time.timeScale;
    world.Tick(Math.Min(Time.unscaledDeltaTime, Time.maximumDeltaTime));
}
```

If you pass `Time.deltaTime`, even `UnscaledClock` will follow `Time.timeScale`. Pass unscaled time, and supply the scale through `DefaultClock.TimeScale`.

## Shutdown and domain reload

`FlowTaskUnity.Shutdown()` is called when Play Mode ends (before the scene's objects are destroyed) and when the player quits (`Application.quitting`). It removes the systems from the PlayerLoop and disposes the World, so every flow unwinds. Dispose does not run Ticks, however, so an await inside `finally` only runs up to its first await (see "Saving on quit" below).

Even with domain reload disabled in Enter Play Mode Options, the startup hook runs every time you enter Play Mode, cleans up the previous World, and then creates a new one.

### Create Signals and similar types per World

`Signal<T>`, `FlowProperty<T>`, and `Once<T>` are bound to the World that first uses them. Using one from another World after that World has been disposed throws `FlowMisuseException`.

```csharp
// Wrong: with domain reload disabled, the second Play Mode session throws
static readonly Signal<int> s_damaged = new("Damaged");
```

If you keep one in a static field, it throws on the second play session when domain reload is disabled. Create it after the World exists (in `Start`, for example) and pass it to flows as an argument. The samples' `GameClocks` and `BackKeyRouter` follow this pattern (see [Samples](samples.md) and [Signals](../guide/signals.md)).

### Recompiling while playing

If Preferences > General > **Script Changes While Playing** is set to "Recompile And Continue Playing", Unity reloads the domain while still playing when you change a script. FlowTask does not recover from this on its own (the reason is under "Lifetime of engine objects" in [Design rationale](../advanced/design-rationale.md)).

- The default World is gone. Flows started with `RunWhileActive` unwind in `OnDisable`, which Unity calls before the reload, but other flows vanish without unwinding (their `finally` blocks do not run).
- FlowTask stays stopped until you stop playing or call `FlowTaskUnity.Initialize()`. After the reload, `FlowTaskUnity.World` and `RunWhileActive` throw an `InvalidOperationException` that starts with `No FlowTask World: ...`. The Editor logs a warning just before the reload, and both this exception's message and the Scope Tree window point out the cause and the name of the setting.
- Starting it again with `Initialize()` does not bring back the lost flows. `Awake` and `Start` are not called again either, so the flows they start do not start.

Set this preference to "Recompile After Finished Playing" or "Stop Playing and Recompile".

### Saving on quit

During the `Shutdown` on quit, awaits in `finally` are cut at their first await. Awaits in `Flow.NonCancelable` are cut too, and you get a `CleanupCutAtDispose` warning. To finish saving before the app quits, cancel the quit once with `Application.wantsToQuit`, then call `Application.Quit()` again after the save is done.

```csharp
// on an object that lives for the whole session (DontDestroyOnLoad)
public sealed class SaveOnQuit : MonoBehaviour
{
    public IGameSave Save { get; set; } // your save system: FlowTask Write(), void WriteNow()
    FlowHandle _saving;
    bool _quitRequested;
    bool _saved;

    void OnEnable() => Application.wantsToQuit += WantsToQuit;
    void OnDisable() => Application.wantsToQuit -= WantsToQuit;

    bool WantsToQuit()
    {
        if (_saved || Application.isEditor) return true; // the Editor ignores Application.Quit()
        if (!_quitRequested)
        {
            _quitRequested = true;
            // Runs to the end, or gives up after 5 s; on the UnscaledClock, so also while Time.timeScale is 0.
            var world = FlowTaskUnity.World;
            _saving = world.Run(FlowTask.Race(Save.Write(), FlowTask.WaitForSeconds(5.0)).WithoutResult(), world.UnscaledClock);
        }
        return false; // not yet: the World keeps running while it saves
    }

    void Update()
    {
        if (!_quitRequested || _saved || !_saving.IsCompleted) return;
        _saved = true;
        Application.Quit(); // outside flow code: quitting disposes the World
    }

    // Android and iOS: the OS can end an app in the background without wantsToQuit.
    void OnApplicationPause(bool paused)
    {
        if (paused) Save.WriteNow(); // the PlayerLoop stops in the background: save synchronously
    }
}
```

How disposing a World relates to cleanup is covered in [Failures](../guide/failures.md).

## Unhandled exceptions and warnings

In the default World, an unhandled exception that reaches the root is written to the Console with `Debug.LogException(new FlowTaskUnhandledException(info))`. The scope path appears after the original exception and its stack trace.

```
InvalidOperationException: ...
  (stack trace)
Rethrow as FlowTaskUnhandledException: [Unhandled] at 'Game > InGame > Battle': InvalidOperationException: ...
```

- Only `FlowExceptionKind.Undelivered` (an exception with no one left to receive it: from a canceled flow, from a Race that was already decided, the second and later exceptions, and so on) is logged with `Debug.LogWarning`. It happens in normal play, and logging it as an error would bury real errors.
- Setting `FlowTaskSettings.OnUnhandledException` replaces all of the output above. To handle kinds differently, check `FlowExceptionInfo.Kind`.

  ```csharp
  OnUnhandledException = info =>
  {
      if (info.Kind == FlowExceptionKind.Undelivered) Debug.LogWarning($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
      else MyCrashReporter.Report(info.Exception, info.ScopePath);
  },
  ```

- World warnings (`FlushLimit`, `PausedOwnClock`, `LongCleanup`, `CleanupCutAtDispose`) are logged once per kind per World with `Debug.LogWarning("[FlowTask] ...")`. There is no setting to turn them off.

The kinds of reports and how to read them are covered in [Failures](../guide/failures.md) and [Debugging and diagnostics](../tools/debugging.md).

## Your own World (for physics frames and more)

To wait for a single physics frame, you don't need another World: write `await Awaitable.FixedUpdateAsync().AsFlow();`. The code after it runs not inside FixedUpdate, but at the Tick or Flush that follows.

Run flows that should keep advancing on physics frames in a World that you Tick from your own `FixedUpdate`. Use a separate World for each driver. The PlayerLoop integration does nothing for this World, so you write the exception and warning output, the Tick, and the Dispose yourself.

```csharp
[DefaultExecutionOrder(-10000)] // before the FixedUpdate of the other scripts
public sealed class PhysicsFlows : MonoBehaviour
{
    public FlowWorld World { get; private set; }

    void Awake()
    {
        World = new FlowWorld("UnityPhysics");
        // Without OnUnhandledException, the outermost Tick, Flush, Run or Dispose throws the reports (FlowUnhandledException).
        World.OnUnhandledException = info =>
        {
            if (info.Kind == FlowExceptionKind.Undelivered) Debug.LogWarning($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
            else Debug.LogException(new FlowTaskUnhandledException(info));
        };
        World.OnWarning += w => Debug.LogWarning("[FlowTask] " + w);
        FlowWorldRegistry.Register(World); // shown in the Scope Tree window
    }

    void FixedUpdate()
    {
        try
        {
            if (World.DefaultClock.TimeScale != Time.timeScale) World.DefaultClock.TimeScale = Time.timeScale;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Debug.LogException(ex); // rejected: keep the previous scale and tick anyway
        }
        World.Tick(Time.fixedUnscaledDeltaTime);
    }

    void OnDestroy()
    {
        FlowWorldRegistry.Unregister(World);
        World.Dispose(); // unwinds the physics flows
    }
}
```

- Without `OnUnhandledException`, unhandled exceptions and the other reports are thrown from the outermost `Tick`, `Flush`, `Run` or `Dispose` as `FlowUnhandledException`.
- In this World, `NextFrame()` and `DelayFrames(n)` count physics frames.
- `FixedUpdate` is not called while `Time.timeScale = 0`, so this World's `UnscaledClock` stops too. Run waits that must keep going during a pause in the default World.
- Scopes have no parent-child relationship across Worlds. To tie a flow started in another World to this lifetime, keep its handle and write `Flow.AddCleanup(handle.Cancel)`.
- When passing `Signal<T>` between Worlds, the sending side calls `Close()` when it ends, and the receiving side waits with `NextOrClosed()`. A pending `Next()` does not finish when the other World is disposed. Use the Signal in the longer-lived World first (see "Create Signals and similar types per World" above).
- If you pass a Clock of this World (`World.DefaultClock`, for example) as the second argument of `RunWhileActive`, its flows can also be tied to a GameObject's lifetime (see [GameObject lifetime](lifetime.md)). `WhileActive` in flow code runs in the World of the awaiting flow.

The Tick steps and the threading rules are covered in [Flows and the World](../guide/flows-and-world.md) and [Threads](../guide/threads.md).

## Analyzers

The core package includes analyzers (`Analyzers/FlowTask.Analyzers.dll`), which run when compiling every assembly that references FlowTask. The rules that are errors by default (FLOW001, FLOW002, FLOW005) also stop Unity's compilation. The list of rules and how to change their severity in Unity are in [Analyzers](../tools/analyzers.md).

## IL2CPP and builds

- The core does not use runtime code generation (`Reflection.Emit`), reflection, or generic virtual methods. It works as is under IL2CPP and Managed Stripping (the async method builders are also instantiated ahead of time).
- Allocation-free behavior applies to optimized builds (Release players, and the Editor with Code Optimization set to Release). A Development Build compiles scripts without optimization, which turns async state machines into classes, so every call to an `async FlowTask` method allocates. To measure allocations in a Development Build, add `-optimize+` to the `csc.rsp` next to your asmdef.
- `GC.GetAllocatedBytesForCurrentThread()` does not work in Unity (always 0 on Mono, not implemented on IL2CPP). Check allocations with the Profiler's GC Alloc column or with `ProfilerRecorder("GC Allocated In Frame")`.

Allocation costs are covered in [Performance and memory](../advanced/performance.md).
