# Testing without an engine

You can test FlowTask flows without running the engine. World time is virtual time that the test advances with Tick, so a 10-second timeout passes in an instant, and the same input always runs in the same order. This page explains how to use `FlowTask.Testing` and the NUnit adapter, and how to use them with xUnit and other frameworks.

## A minimal test

Here is a flow to test. It waits until OK or Cancel is pressed, or until time runs out.

```csharp
public static class ConfirmDialog
{
    public static async FlowTask<bool> Ask(Signal<FlowUnit> ok, Signal<FlowUnit> cancel, double timeoutSeconds)
    {
        var r = await FlowTask.Race(ok.Next(), cancel.Next(), FlowTask.WaitForSeconds(timeoutSeconds));
        return r.Index == 0;
    }
}
```

An NUnit test looks like this.

```csharp
using Katout.FlowTask;
using Katout.FlowTask.Testing;
using Katout.FlowTask.Testing.NUnit;
using NUnit.Framework;

public class ConfirmDialogTests
{
    [Test, FailOnUnhandledFlowException]
    public void TheFirstPressWins()
    {
        using var tw = FlowNUnit.CreateWorld();
        var ok = new Signal<FlowUnit>();
        var cancel = new Signal<FlowUnit>();

        var h = tw.World.Run(ConfirmDialog.Ask(ok, cancel, timeoutSeconds: 10)); // 1. start
        ok.Emit(FlowUnit.Default);                                              // 2. inject the input
        tw.World.TickUntil(() => h.IsCompleted);                                // 3. run until it ends

        Assert.That(h.Result, Is.True);
    }

    [Test, FailOnUnhandledFlowException]
    public void ItTimesOut()
    {
        using var tw = FlowNUnit.CreateWorld();
        var h = tw.World.Run(ConfirmDialog.Ask(new Signal<FlowUnit>(), new Signal<FlowUnit>(), timeoutSeconds: 10));

        tw.World.TickFor(11);   // past the 10-second timeout, at once

        Assert.That(h.IsCompleted, Is.True);
        Assert.That(h.Result, Is.False);
    }
}
```

A test has three parts: start, inject input, and advance time.

- Inject input with `Emit` on a `Signal` the test holds, `Set` on a `FlowProperty`, or by completing a fake Task. Input that is created inside the flow (such as a button press) should be made into something you can pass as an argument.
- For a flow that needs no input, you can get the result in one line with `tw.World.RunUntilComplete(task)`.
- `TestWorld` converts implicitly to `FlowWorld`, but it doesn't have `Run` or `Tick` itself. Always write `tw.World.`.

## Packages

| Package | Contents | Namespace |
|---|---|---|
| `FlowTask.Testing` | `TestWorld`, extension methods that advance virtual time, `FlowAssert` | `Katout.FlowTask.Testing` |
| `FlowTask.Testing.NUnit` | `FlowNUnit.CreateWorld()`, `[FailOnUnhandledFlowException]` (NUnit 3.14 or later) | `Katout.FlowTask.Testing.NUnit` |

Install them from NuGet on .NET and Godot, and through UPM on Unity. The steps are in [Installation](../getting-started/installation.md).

## Advancing virtual time

You advance a World with extension methods on `FlowWorld`. Unless you pass an interval as an argument, each Tick is 1/60 of a second.

| Method | Behavior |
|---|---|
| `TickFrames(count)` | Ticks `count` times |
| `TickFor(seconds)` | Ticks until `UnscaledClock` advances by `seconds`, and returns the number of Ticks |
| `TickUntil(condition, maxTicks)` | Ticks until the condition is true. If it isn't satisfied within `maxTicks` (default 100,000) Ticks, throws a `TimeoutException` with a dump attached |
| `RunUntilComplete(task, maxTicks)` | Runs the task, Ticks until it ends, and returns the result. Throws `FlowUnhandledExceptionAssertionException` if it ends with an exception, `FlowAssertionException` if it ends with cancellation, and `TimeoutException` if it doesn't end within `maxTicks` Ticks |

Time waits behave as follows. For tests that check order frame by frame, run them at the same interval as the game.

- Time waits whose deadlines come in the same Tick complete in the order they began waiting, not in order of their deadlines (when the branches of a Race are the time waits themselves, in the order of the arguments).
- `WaitForSeconds` ends on the first Tick in which the Clock's time (an accumulated double) reaches the target. Adding 1/60 six times falls just short of 0.1, so a 0.1-second wait at 60fps ends on the seventh Tick. To wait an exact number of frames, use `DelayFrames`.

## Make fake Tasks already completed

Make fakes of bridged work with Tasks that complete on the test's thread.

```csharp
// The fake completes at once, on the test's thread.
Func<CancellationToken, Task<Profile>> getProfile = _ => Task.FromResult(new Profile("alice"));
var profile = tw.World.RunUntilComplete(LoadProfile(getProfile));

static async FlowTask<Profile> LoadProfile(Func<CancellationToken, Task<Profile>> get) =>
    await FlowBridge.FromTask(get);   // bridged, even in tests
```

- Use: `Task.FromResult`, `Task.FromException`, `Task.CompletedTask`, and a `TaskCompletionSource<T>` that the test completes itself between Ticks (with default options).
- Avoid: Tasks that complete on another thread (real I/O, `Task.Delay`, `Task.Run`) and a `TaskCompletionSource` with `RunContinuationsAsynchronously`. Which Tick takes in their completion isn't fixed. `TickUntil` and `RunUntilComplete` advance virtual time without waiting in real time, so a `WaitForSeconds` timeout in the flow can pass first, or `maxTicks` can run out with a `TimeoutException`.
- Don't make a test method `async Task` and put an `await` between Ticks. The code after the `await` may run on another thread, and a Tick there throws `FlowThreadException` (a World runs only on the thread that created it).
- For integration tests that go through real I/O, tick on one thread, passing the real time that has elapsed to `Tick`.
- **Await fake Tasks through a bridge too** (`FlowBridge.FromTask`, `.AsFlow()`). An already-completed Task doesn't suspend even if you await it directly, so the test passes, and only in production does the scope end with `FlowMisuseException`. [FLOW002](analyzers.md) stops this, so don't remove the analyzers from your test projects either.

Bridges are covered in detail in [Task bridges](../integrations/task.md).

## Make unhandled exceptions fail the test

`TestWorld` records every report that reaches `OnUnhandledException` (including `Cleanup`, `SwallowedCancellation`, and `Undelivered`) in `Exceptions`, and warnings in `Warnings`.

- `ThrowIfUnhandled()` throws `FlowUnhandledExceptionAssertionException` with the first report if there are any reports. It derives from `FlowAssertionException`, and its `Info` holds the report.
- `Dispose()` disposes the World and then runs the same check. If you write `using var tw = …`, the check runs at the end of the test.
- In tests that check for a failure, examine `tw.Exceptions`, then clear it with `AcceptExceptions()`.

```csharp
using var tw = new TestWorld();
tw.World.Run(FailingFlow());
tw.World.TickFrames(1);

Assert.That(tw.Exceptions[0].Exception, Is.InstanceOf<SaveFailedException>());
tw.AcceptExceptions();   // expected: Dispose does not fail the test
```

`Undelivered` also fails the test. In tests that exercise a cancellation and a failure happening in the same Tick, check for it as an expected report.

The framework adapters write reports to the test output.

- **NUnit**: the World from `FlowNUnit.CreateWorld()` writes reports to `TestContext.Out`. Put `[FailOnUnhandledFlowException]` on a method, class, or assembly to fail the test if there are reports after it runs. A World the test disposes itself is checked by that Dispose, and `[FailOnUnhandledFlowException]` doesn't check it again.
- **xUnit and other frameworks**: use `TestWorld` as it is. If there are reports when it's disposed, it throws and fails the test. To write the reports to the test output too, override `OnExceptionRecorded`.

```csharp
sealed class XunitTestWorld : TestWorld
{
    readonly ITestOutputHelper _output;
    public XunitTestWorld(ITestOutputHelper output) => _output = output;
    protected override void OnExceptionRecorded(FlowExceptionInfo info) => _output.WriteLine(info.ToString());
}

public class TitleTests
{
    readonly ITestOutputHelper _output;
    public TitleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void StartGoesToTheMenu()
    {
        using var tw = new XunitTestWorld(_output);
        var start = new Signal<FlowUnit>();
        var h = tw.World.Run(Title(start));

        start.Emit(FlowUnit.Default);
        tw.World.TickUntil(() => h.IsCompleted);

        Assert.Equal(Screen.Menu, h.Result);
    }
}
```

## Checking the scope tree

`FlowAssert` provides assertions that examine live scopes. If an assertion isn't satisfied, it throws a `FlowAssertionException` with a dump attached.

```csharp
tw.World.Run(Game(input));
tw.World.TickFrames(1);

FlowAssert.ScopePathExists(tw.World, "Game > InGame > Battle");
FlowAssert.ScopeIsWaitingOn(tw.World, "Battle", "Next");

input.Emit(Key.Quit);
tw.World.TickFrames(1);
FlowAssert.NoLiveScopes(tw.World);   // every flow ended, nothing leaked
```

| Method | What it checks |
|---|---|
| `ScopeExists(world, name)` | A live scope with that name exists |
| `ScopeDoesNotExist(world, name)` | No live scope with that name exists |
| `ScopePathExists(world, path)` | A scope with exactly that path (`"Game > InGame > Battle"`) exists |
| `ScopeIsWaitingOn(world, name, waitingFor)` | The scope's wait description contains `waitingFor` |
| `NoLiveScopes(world)` | There are no live scopes |

A scope's name is the method name, or the name given with `Flow.Named`. The wait description is the same as in the dump in [Debugging and diagnostics](debugging.md).

## Running on Unity

EditMode tests can run with `TestWorld` without spinning the PlayerLoop. The test asmdef references `FlowTask`, `FlowTask.Testing`, and `FlowTask.Testing.NUnit`.

```json
{
    "name": "MyGame.Tests",
    "references": ["FlowTask", "FlowTask.Testing", "FlowTask.Testing.NUnit", "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
    "includePlatforms": ["Editor"],
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll"],
    "autoReferenced": false,
    "defineConstraints": ["UNITY_INCLUDE_TESTS"]
}
```

To run in PlayMode and in the test player, make `includePlatforms` empty and remove `UnityEditor.TestRunner`.

> **Note**: list the test packages in the manifest's `testables` as well as in `dependencies`. Otherwise, the packages aren't compiled, and tests that reference them stop with `CS0246` ([Installation](../getting-started/installation.md)). `com.katout.flowtask.testing.nunit` depends on `com.unity.test-framework` 1.1.33 or later.

The main thread of Unity tests has a `UnitySynchronizationContext`. If you await a bridged Task while spinning the World with `TickUntil` or `RunUntilComplete`, the continuation of an await without `ConfigureAwait(false)` inside the factory is posted to the context, and doesn't run during the loop. `TickUntil` ends with a `TimeoutException`. Avoid this in one of these ways:

- Make fake Tasks already completed (`Task.FromResult` and so on).
- Add `ConfigureAwait(false)` to awaits inside the factory.
- Put a `[SetUpFixture]` in the test namespace that removes the context only during the tests.

```csharp
using System.Threading;
using NUnit.Framework;

namespace MyGame.Tests.Flows // applies to the tests in this namespace
{
    [SetUpFixture]
    public sealed class NoUnitySynchronizationContext
    {
        SynchronizationContext _saved;

        [OneTimeSetUp]
        public void Remove()
        {
            _saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [OneTimeTearDown]
        public void Restore() => SynchronizationContext.SetSynchronizationContext(_saved);
    }
}
```

Don't use the last approach for PlayMode `[UnityTest]` tests that spin the PlayerLoop. The continuations of awaits without `ConfigureAwait(false)` would no longer return to the main thread.

Samples of Unity tests are in [Unity samples](../unity/samples.md).

## Running on Godot

Godot types such as `Node` can't be used outside the Godot process. If you split the flows you want to test into a form that doesn't touch Godot types (taking a plain `Signal<T>`, waiting on a Clock that is passed in), you can run them with `dotnet test` in a separate test project (net8.0 or later, with `FlowTask.Testing` from NuGet).

An example of running NUnit tests inside Godot is in `CoreSuiteRunner.cs` in the repository's `tests/godot`.
