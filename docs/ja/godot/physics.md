# 物理フレームで動かす

`FlowWorldNode` の World は `_Process` で動きます。物理フレームごとに動かしたいフローには、World をもう 1 つ作って `_PhysicsProcess` から Tick します。このページでは、その World の作り方と、2 つの World の関係を説明します。物理フレームを 1 回待つだけなら、World を分けずに待てます（下の「物理フレームを 1 回だけ待つ」）。

## 物理用の World を作る

autoload のサブクラスに数行足します。

```csharp
// res://FlowAutoload.cs
using System;
using Katout.FlowTask;
using Katout.FlowTask.Godot;
using Godot;

public partial class FlowAutoload : FlowWorldNode
{
    public FlowAutoload() => ProcessPhysicsPriority = int.MinValue; // before other nodes' _PhysicsProcess

    public static FlowWorld Physics { get; private set; }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (Physics == null || Physics.IsDisposed)
        {
            Physics = new FlowWorld("GodotPhysics");
            // Route unhandled exceptions and warnings to Godot's log like FlowWorldNode does
            // (without OnUnhandledException, an unhandled exception is thrown from _PhysicsProcess)
            Physics.OnUnhandledException = info =>
            {
                if (info.Kind == FlowExceptionKind.Undelivered) GD.PushWarning($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
                else GD.PushError($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
            };
            Physics.OnWarning += w => GD.PushWarning("[FlowTask] " + w);
        }
    }

    // Same time model as FlowWorldNode: tick with the unscaled step, let DefaultClock follow Engine.TimeScale
    public override void _PhysicsProcess(double delta)
    {
        var w = Physics;
        if (w == null || w.IsDisposed) return;
        var scale = Engine.TimeScale;
        if (scale >= 0 && w.DefaultClock.TimeScale != scale) ApplyTimeScale(w, scale);
        w.Tick(1.0 / Engine.PhysicsTicksPerSecond);
    }

    // A rejected scale goes to the log and the World ticks with the previous one;
    // skipping the Tick on the exception would stop the physics World
    static void ApplyTimeScale(FlowWorld w, double scale)
    {
        try { w.DefaultClock.TimeScale = scale; }
        catch (ArgumentOutOfRangeException ex) { GD.PushError($"[FlowTask] Engine.TimeScale {scale} was not applied to the physics World. {ex.Message}"); }
    }

    public override void _ExitTree()
    {
        Physics?.Dispose(); // unwinds the physics flows
        Physics = null;
        base._ExitTree();
    }
}
```

フローは、この World の `Run` で起動します。

```csharp
FlowAutoload.Physics.Run(Dash(body));

async FlowTask Dash(CharacterBody2D body)
{
    for (var i = 0; i < 10; i++)
    {
        body.Velocity = new Vector2(600, 0);
        body.MoveAndSlide();
        await FlowTask.NextFrame(); // = the next physics frame
    }
}
```

## 時間の進み方

- `FlowTask.NextFrame()` と `FlowTask.DelayFrames(n)` は、物理フレームで数えます。
- `UnscaledClock` は物理のステップ（`1 / Engine.PhysicsTicksPerSecond`）ずつ進みます。`DefaultClock` は Godot の物理の delta（ステップ × `Engine.TimeScale`）ずつ進みます。メインの World と同じ時間の扱いなので、2 つの World の Clock はそろいます（[セットアップ](setup.md) の時間）。
- `_PhysicsProcess` の `delta` をそのまま `Tick` に渡さないでください。`UnscaledClock` まで `Engine.TimeScale` に従い、倍率 0 の間は止まってしまいます。上のように、ステップを渡します。
- 物理のステップは固定で、1 フレームあたりのステップ数は Godot が抑えます。そのため、メインの World のような長いフレームの切り詰めは要りません。

## 未処理の例外と終了

自分で作る World には、`OnUnhandledException` を設定してください。設定しないと、未処理の例外はいちばん外側の `Tick`（`_PhysicsProcess`）、`Flush`、`Run`、`Dispose`（`_ExitTree`）から `FlowUnhandledException` として投げられます。例外の扱いは [失敗の扱い](../guide/failures.md) にあります。

物理用の World も、`_ExitTree` の `Dispose` ですべてのフローを巻き戻します。終了時の注意（`Dispose` は Tick を回さないので、`finally` の await は最初の await までしか走らない）は、メインの World と同じです（[セットアップ](setup.md) の終了）。

## 2 つの World の関係

2 つの World はどちらもメインスレッドにあるので、`Signal<T>` で連携できます。メインの World のフローが Emit すると、物理用の World で待っているフローは、次の物理の Tick で再開します。

```csharp
// main World: input decides, physics World: moves the body
readonly Signal<FlowUnit> _dashRequested = new("DashRequested");

async FlowTask InputLoop() // run on FlowWorldNode.Default
{
    while (true)
    {
        await FlowTask.WaitUntil(() => Input.IsActionJustPressed("dash"));
        _dashRequested.Emit(FlowUnit.Default); // resumes PhysicsLoop at the next physics Tick
        await FlowTask.NextFrame();
    }
}

async FlowTask PhysicsLoop(CharacterBody2D body) // run on FlowAutoload.Physics
{
    using var requests = _dashRequested.Subscribe(BufferPolicy.Latest);
    while (true)
    {
        await requests.Next();
        await Dash(body);
    }
}
```

World の間に、スコープの親子関係はありません。メインの World のフローが終わっても、物理用の World で起動したフローは止まりません。物理側のフローをほかの寿命に結び付けたいときは、次のどれかにします。

- ノードの寿命に結ぶなら、`node.RunWhileInTree(task, FlowAutoload.Physics.DefaultClock)` で起動する。フローは物理用の World で動き、ノードがツリーを出ると巻き戻る（[ノードの寿命](lifetime.md)）。
- `Physics.Run` が返すハンドルを持ち、メイン側の `finally` か `Flow.AddCleanup` で `Cancel()` する。
- `Signal<T>` でやり取りする。

```csharp
async FlowTask Stage(CharacterBody2D body) // on the main World
{
    var physics = FlowAutoload.Physics.Run(PhysicsLoop(body));
    try
    {
        await RunStage();
    }
    finally
    {
        physics.Cancel(); // the physics flow ends with this one
    }
}
```

## 物理フレームを 1 回だけ待つ

物理フレームを 1 回待つだけなら、World を分けなくてもかまいません。メインの World のフローで、`SceneTree` の `physics_frame` シグナルを待ちます。

```csharp
using var physicsFrame = GetTree().ToFlowSignal(SceneTree.SignalName.PhysicsFrame);
await physicsFrame.Next(); // resumes at the main World's Tick, after the physics step
```

- 続きは物理のステップの中ではなく、その後のメインの World の Tick で走ります。
- 物理フレームごとに動かし続けたいフローには、このページの別の World を使います。1 つの World の中に、物理フレームで進む Clock は作れません。`Tick(dt)` は World のすべての Clock を同じ dt で進めるためです。World と Clock の関係は [時間と Clock](../guide/time-and-clocks.md) にあります。
