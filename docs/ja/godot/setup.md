# Godot のセットアップ

FlowTask.Godot は、Godot 4 (.NET) の SceneTree から World を動かします。このページでは、`FlowWorldNode` を autoload に登録してフローを起動するまでと、フレームの中の順序、ポーズ、時間、スレッド、未処理の例外、終了の扱いを説明します。

## インストール

パッケージの入れ方は [インストール](../getting-started/installation.md) にあります。ゲームの `.csproj` に `FlowTask.Godot` を加えれば、コアの `FlowTask` とアナライザも一緒に入ります。

Godot 4.4.1 以降の .NET 版で動きます。ゲームのプロジェクトは `net8.0` で、コアは netstandard2.1 版が使われます（機能と公開 API は同じです）。エンジンと `Godot.NET.Sdk` のバージョンは一致させてください。

## FlowWorldNode を autoload にする

Godot の autoload には、プロジェクトの中のスクリプト（`res://...`）が要ります。ライブラリのクラスは直接登録できないので、1 行のサブクラスを置いて、それを登録します。

```csharp
// res://FlowAutoload.cs (the class name must match the file name)
public partial class FlowAutoload : Katout.FlowTask.Godot.FlowWorldNode { }
```

```ini
; project.godot
[autoload]
FlowAutoload="*res://FlowAutoload.cs"
```

autoload の一覧では先頭に置いてください。そうすると、ほかの autoload の `_Ready` からも `FlowWorldNode.Default` を使えます。

autoload にしない場合（テスト用のシーン、シーンごとの World など）は、コードから追加します。World は `_EnterTree` で作られるので、`AddChild` の直後から使えます。

```csharp
var flow = new FlowWorldNode();
AddChild(flow);
flow.World.Run(MyFlow());
```

## フローを起動する

```csharp
// res://Enemy.cs
using Katout.FlowTask;
using Katout.FlowTask.Godot;
using Godot;

public partial class Enemy : Node2D
{
    public override void _Ready()
    {
        // A root flow that runs while this node is in the tree
        this.RunWhileInTree(EnemyAI());
    }

    async FlowTask EnemyAI()
    {
        while (true)
        {
            Position += Vector2.Right;
            await FlowTask.NextFrame();
        }
    }
}
```

`RunWhileInTree` は、ノードがツリーを出るとフローを止めます。ノードとフローの寿命の結び方は [ノードの寿命](lifetime.md) にあります。

ノードに結び付けないフローは、`FlowWorldNode.Default.Run(...)` で起動します。

- `FlowWorldNode.Instance` は、最初にツリーに入った `FlowWorldNode`（ふつうは autoload）です。`FlowWorldNode.Default` はその World です。
- そのノードがツリーを出ると、`Instance` は null に戻ります。その間、`Default` は `InvalidOperationException` を投げます。次にツリーに入った `FlowWorldNode` が `Instance` を引き継ぎます。
- 各 `FlowWorldNode` の World は、そのノードの `World` プロパティでも取れます。

`using Godot;` と `using Katout.FlowTask;` を両方書いても、Godot の `Signal`（型引数のない構造体）と FlowTask の `Signal<T>`（型引数を取るクラス）はぶつかりません。`[Signal]` と `new Signal<int>()` はそのまま書けます。

## ライブラリに Node の派生クラスを置くとき

Godot 4 (.NET) は、`_EnterTree` や `_Process` などのコールバックを、Godot のソースジェネレータが生成したコードを通して C# に届けます。ジェネレータを通さずにビルドしたライブラリの override は、ゲームのアセンブリでサブクラスにしても呼ばれません。

自分のクラスライブラリに `FlowWorldNode` の派生クラスなどの Node を置くときは、FlowTask.Godot と同じく `Godot.SourceGenerators` を参照し、`ScriptPathAttribute` のジェネレータだけを無効にします（ライブラリには `res://` のパスがないためです）。

```xml
<PropertyGroup>
  <GodotProjectDir>$(MSBuildProjectDirectory)\</GodotProjectDir>
  <GodotDisabledSourceGenerators>ScriptPathAttribute</GodotDisabledSourceGenerators>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Godot.SourceGenerators" Version="4.4.1" PrivateAssets="all" />
</ItemGroup>
```

ゲームのプロジェクトの中に置くサブクラス（上の `FlowAutoload` など）には、この設定は要りません。

## フレームの中の順序

```
One Godot frame (outline)
  Input events               ... Button.pressed etc. are emitted (resumes are only reserved)
  Physics steps x 0..n
    (a physics World of your own is ticked here; see physics.md)
  Process
    FlowWorldNode._Process          (ProcessPriority = int.MinValue)
      -> FlowWorld.Tick(dt)          advances time and resumes the waits that are due
    Other nodes' _Process, Timers, ...
    FlowLateFlushNode._Process      (ProcessPriority = int.MaxValue, an internal child node)
      -> FlowWorld.Flush()           runs the reserved resumes; does not advance time
  End of frame
    CallDeferred calls, deletion of QueueFree'd nodes (tree_exiting)
```

- フローは、ほかの Node の `_Process` より先に動きます。入力で emit された Godot のシグナルを待つフローは、同じフレームの Tick で再開します。
- ほかの Node の `_Process` の中で emit された Godot のシグナル（Timer の `timeout` など）や、そこで Emit された `Signal<T>` を待つフローも、フレームの最後の Flush で同じフレームのうちに再開します。再開の予約がなければ Flush は何もしないので、これを切り替える設定はありません。
- Godot のシグナルや Task の完了が、その場でフローを同期的に再開することはありません。再開は必ず、メインスレッドの World の Tick か Flush で行われます。

## ツリーのポーズ

`FlowWorldNode` の `ProcessMode` は既定で `Always` です。`SceneTree.Paused = true` の間もフローは動きます。ポーズメニューなどの UI をフローで書けるようにするためです。

ゲームプレイを一時停止するには、ツリーのポーズではなく Clock の `Pause()` を使います（[時間と Clock](../guide/time-and-clocks.md)）。Clock のポーズにツリーのポーズを合わせる書き方は、[サンプル](samples.md) のポーズの場面にあります。

`ProcessMode` はコンストラクタで設定した既定値なので、インスペクタやサブクラスで変えられます。`Pausable`（または `Disabled`）にすると、ツリーのポーズ中は World 全体が止まります。

- Tick もフレームの最後の Flush も行わないので、時間とフレームの待ちは進みません。
- ポーズ中に emit されたシグナルを待つフローは、ポーズが明けた最初の Tick で再開します。
- 処理されなかったフレームの時間は、Clock に足しません。
- 例外は、`RunWhileInTree` か `WhileInTree` で結んだノードがツリーを出たときです。巻き戻しのために、その場で Flush します。この Flush では、ポーズ中に予約されたほかの再開も走ります（[ノードの寿命](lifetime.md)）。

## 時間

`FlowWorldNode` は、Godot の倍率のかかっていない時間で `Tick` します。これは `ignore_time_scale` を指定した Timer、SceneTreeTimer、Tween が進むのと同じ process のステップです。`Engine.TimeScale` が 0 より大きい間は、そのフレームの delta を `Engine.TimeScale` で割って求めます。

- `DefaultClock.TimeScale` は毎フレーム `Engine.TimeScale` に合わせられます。そのため `DefaultClock` は Godot の delta と同じだけ進みます。`UnscaledClock` は `Engine.TimeScale` に従いません。
- `DefaultClock.TimeScale` を自分で変えても、毎フレーム上書きされます。独自のスローモーションは子の Clock（`world.CreateClock("Game")`。親を省くと `DefaultClock` の子になる）で行います。
- 子の Clock の倍率との積が有限でなくなる `Engine.TimeScale` は、Clock が `ArgumentOutOfRangeException` で拒否します。そのフレームはエラーを `GD.PushError` に出し、`DefaultClock` は前の倍率のまま Tick します。World は止まりません。
- 固定しない通常の実行では、Godot のステップは実時間を平滑化した値です。1 フレーム単位では、実時間と少しずれます。

Clock の使い方は [時間と Clock](../guide/time-and-clocks.md) にあります。

### 倍率が 0 のとき

`Engine.TimeScale = 0` の間は Godot の delta が 0 なので、ステップを求められません。そのため、前のフレームからの実際の経過時間（`Time.GetTicksUsec` の差）で Tick します。`UnscaledClock` は倍率 0 の間も進みます。

同じフレームの Tick より前（`_PhysicsProcess`、入力のコールバックなど）で `Engine.TimeScale` を変えると、そのフレームの delta は変える前の倍率で作られています。そのため、`UnscaledClock` はそのフレームだけ「変える前の倍率 ÷ 変えた後の倍率」倍進みます。次のフレームからは合います。

### 長いフレームの後

ヒッチやブレークポイントで長いフレームがあると、時間を `Engine.MaxPhysicsStepsPerFrame / Engine.PhysicsTicksPerSecond` 秒で切り詰めてから倍率をかけます。これは Godot が 1 フレームの物理のステップ数に設ける上限で、既定では 8 / 60 = 0.133 秒です。待ちが一度に全部終わらないようにするためです。

Godot の `ignore_time_scale` のタイマーは、切り詰める前の時間で進みます。長いフレームの後だけは、`UnscaledClock` とずれます。

> **注意**：1 回の Tick で期限が過ぎた時間待ちは、期限の早い順ではなく待ち始めた順に完了します。`Race` の両方の枝が時間待ちで、どちらの期限も 1 回の Tick で過ぎると、勝つのは前の枝です。詳しくは [実行モデル](../advanced/execution-model.md) にあります。

### 固定のフレームレート（`--fixed-fps`、Movie Maker）

Godot を `--fixed-fps <fps>` で動かすときと、Movie Maker（`--write-movie`。`--fixed-fps` が強制される）で録画するときは、Godot の delta は実際にかかった時間と関係なく `1 / fps × Engine.TimeScale` になります。`FlowWorldNode` はこのステップで Tick します。1 フレームの処理に `1 / fps` 秒より長くかかっても、`DefaultClock` と `UnscaledClock` は Godot の時間（Timer、Tween、AnimationPlayer、物理用の World）とそろいます。

スクリプトからは `--fixed-fps` を判定できないので、実行のしかたによらず同じ規則で進めます。注意が 3 つあります。

- `1 / fps` が長いフレームの上限を超えると（既定の設定では 7.5 fps 未満）、どちらの Clock も上限で切り詰めるので、Godot の時間より遅れます。上限を変えるには、下の `GetDeltaTime` を override します。
- `Engine.TimeScale = 0` の間は、`UnscaledClock` は実際の経過時間で進みます。固定のステップで進み続ける Godot の `ignore_time_scale` のタイマーとはずれます。ゲームの一時停止には、倍率 0 ではなく Clock の `Pause()` を使ってください。
- `FlowTask.Race(x, FlowTask.WaitForSeconds(n))` のような時間の上限も、Clock の時間、つまり Godot の時間で計ります。実時間より速く回る実行（headless の `--fixed-fps` で処理の軽い場面など）では、上限が実時間では短くなります。セーブや通信のように実時間で切りたい上限は、`FlowBridge.FromTask` でブリッジする Task の側の仕組み（`CancellationTokenSource.CancelAfter`、`HttpClient.Timeout`）で付けます（[合成](../guide/composition.md) の「タイムアウト」）。

### 時間の源を変える

`Tick` に渡す値は、`protected virtual double GetDeltaTime(double unscaledDelta)` が決めます。時間の源を差し替える口はこれだけです。

実際の経過時間（壁時計）で進めたいゲームは、autoload のサブクラスで次のように override します。

```csharp
// res://FlowAutoload.cs: ticks with the measured (wall-clock) time instead of Godot's unscaled step
using System;
using Godot;

public partial class FlowAutoload : Katout.FlowTask.Godot.FlowWorldNode
{
    protected override double GetDeltaTime(double unscaledDelta) =>
        Math.Min(unscaledDelta, (double)Engine.MaxPhysicsStepsPerFrame / Engine.PhysicsTicksPerSecond);
}
```

- 引数 `unscaledDelta` は、前のフレームからの実際の経過時間（`Time.GetTicksUsec` の差）です。最初のフレームと、ノードが処理されなかったフレーム（ツリーのポーズ）の後だけは、Godot の delta ÷ `Engine.TimeScale`（倍率 0 なら 0）です。処理されなかった間の時間は入りません。
- 既定の実装は、倍率が 0 より大きければ Godot の delta ÷ `Engine.TimeScale`、0 なら `unscaledDelta` を、長いフレームの上限で切り詰めて返します。
- 上の override では Clock が実時間で進むので、固定のフレームレートでは Godot の時間より先に進みます。
- 上限だけを変えたいときは、`Engine.TimeScale > 0 ? GetProcessDeltaTime() / Engine.TimeScale : unscaledDelta` を自分の上限で切り詰めて返します。
- 記録した dt の再生など、ほかの時間の源も同じ口で与えられます。World を自分で持って `Tick` を呼んでもかまいません（[物理フレームで動かす](physics.md) と同じ形です）。

## World の設定

World の設定（`OnUnhandledException`、`OnWarning`、Clock）は、autoload のサブクラスで `CreateWorld(string name)` を override して行います。

```csharp
public partial class FlowAutoload : Katout.FlowTask.Godot.FlowWorldNode
{
    protected override FlowWorld CreateWorld(string name)
    {
        var world = base.CreateWorld(name);
        // configure the World here (OnUnhandledException, OnWarning, clocks)
        return world;
    }
}
```

World はノードがツリーに入るときに作られ、ツリーを出るときに `Dispose` されます。同じノードがツリーに入り直すと、新しい World が作られます。

## スレッド

- `FlowWorldNode` の World は Godot のメインスレッドで動きます。フローはそのスレッドでだけ再開します。
- 重い計算は、`FlowBridge.FromTask(ct => Task.Run(() => Work(), ct))` で別のスレッドに出します。ブリッジした Task がどのスレッドで終わっても、フローは World のスレッドで再開します。
- ほかのスレッドからは、`Signal<T>.EmitFromAnyThread`、`CloseFromAnyThread`、`FlowWorld.Post`、ハンドルの `Cancel()` で World に渡せます。
- Godot のシグナルが別のスレッドで emit される場合の注意は、[Godot のシグナル](signals.md) にあります。

Godot のメインスレッドには `GodotSynchronizationContext` があります。`FlowBridge.FromTask` のファクトリの中で `ConfigureAwait(false)` なしに await すると、その続きはメインスレッドに戻って走ります。

- ふだんのゲームではこれで問題ありません。
- ただし、メインスレッドを止めたまま Task の完了を待つコード（テストで Tick をループで回すなど）では、続きが走らずに終わらなくなります。そうしたコードでは、ファクトリの中の await に `ConfigureAwait(false)` を付けます。なお、メインスレッドで `AsTask()` の Task を `.Result` や `Wait()` で待つと、Tick も止まるので、`ConfigureAwait(false)` を付けても終わりません（[フローと World](../guide/flows-and-world.md)）。

一般的な規則は [スレッド](../guide/threads.md) にあります。

## 未処理の例外と警告

ルートに届いた未処理の例外（どのフローも捕まえなかった例外）は、Godot のログに出ます。

- `GD.PushError` に `[FlowTask] Unhandled at 'Game > InGame > Battle': <例外>` の形で、スコープのパスと一緒に出します（`Unhandled` の所には報告の種類が入ります）。
- `FlowExceptionKind.Undelivered`（受け手がもういなかった例外）は、`GD.PushWarning` で警告として出します。待っていたフローがすでに取り消されていた、`Race` がすでに決着していた、などで普通のプレイでも起きるので、エラーにすると本物のエラーが埋もれるためです。
- どの種類でも、出力の後に `FlowWorldNode.UnhandledException` イベントが発火します。

報告の種類と `FlowExceptionInfo` の読み方は [失敗の扱い](../guide/failures.md) にあります。

種類によって扱いを変えたいときは、`CreateWorld` の override で `OnUnhandledException` を設定し、`FlowExceptionInfo.Kind` で振り分けます。

```csharp
protected override FlowWorld CreateWorld(string name)
{
    var world = base.CreateWorld(name);
    world.OnUnhandledException = info =>
    {
        if (info.Kind == FlowExceptionKind.Undelivered) GD.PushWarning($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
        else MyCrashReporter.Report(info.Exception, info.ScopePath); // your crash reporter
    };
    return world;
}
```

`OnUnhandledException` を設定すると、既定のログ出力も `UnhandledException` イベントも置き換わります（`UnhandledException` を発火するのは既定のハンドラです）。`UnhandledException` を購読している処理があるなら、同じことを自分のハンドラの中で行ってください。

> **注意**：World の `Dispose` の間に `UnhandledException` のハンドラが例外を投げると、`_ExitTree` から `FlowUnhandledException` が投げられます。World はそれでも閉じられ、次にツリーに入った `FlowWorldNode` が `Default` を引き継ぎます。

警告（`FlushLimit`、`PausedOwnClock`、`LongCleanup`、`CleanupCutAtDispose`）は、種類ごとに World で 1 回、`GD.PushWarning("[FlowTask] ...")` に出ます。警告の意味は [デバッグと診断](../tools/debugging.md) にあります。

止まったフローを探すときは、`GD.Print(FlowWorldNode.Default.Dump())` をデバッグ用のキーに割り当てます。ダンプの読み方も [デバッグと診断](../tools/debugging.md) にあります。

## 終了

`FlowWorldNode` は `_ExitTree` で World を `Dispose` し、すべてのフローを巻き戻します（`finally`、`using`、`AddCleanup` が走ります）。

ゲームの終了時、ルートの直下の子は後ろから順に `_ExitTree` されます。autoload より後に追加されたメインシーンのノードは、この時点で既にツリーの外です（解放はまだです）。

- 巻き戻しの中でノードに触るなら、`IsInstanceValid` と `IsInsideTree` を確かめてください。
- メインシーンのノードに結び付いたフローは、`RunWhileInTree` か `WhileInTree` で起動しておけば、そのノードの `tree_exiting` の時点で、ノードがまだツリーにあるうちに巻き戻ります（[ノードの寿命](lifetime.md)）。

### 終了時のセーブ

`Dispose` は Tick を回しません。`finally` の中の await は最初の await までしか走らず、`Flow.NonCancelable` の await も切られて `CleanupCutAtDispose` の警告が出ます（[失敗の扱い](../guide/failures.md)）。

セーブのように最後まで走らせたい処理は、`SceneTree.AutoAcceptQuit` を false にして閉じる要求（`NotificationWMCloseRequest`）を受け、セーブが終わってから `GetTree().Quit()` を呼びます。

```csharp
// res://SaveOnQuit.cs: an autoload placed after FlowAutoload
using Katout.FlowTask;
using Katout.FlowTask.Godot;
using Godot;

public interface IGameSave // your save system
{
    FlowTask Write();  // saves asynchronously
    void WriteNow();   // saves synchronously
}

public partial class SaveOnQuit : Node
{
    public IGameSave Save { get; set; } // set by the code that starts the session
    FlowHandle _saving;
    bool _quitRequested;
    bool _quitting;

    public override void _Ready()
    {
        GetTree().AutoAcceptQuit = false;  // the close button only sends NotificationWMCloseRequest
        ProcessMode = ProcessModeEnum.Always; // quits also while the tree is paused
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest && !_quitRequested)
        {
            _quitRequested = true;
            // Runs to the end, or gives up after 5 s; on the UnscaledClock, so also while Engine.TimeScale is 0.
            var world = FlowWorldNode.Default;
            _saving = world.Run(FlowTask.Race(Save.Write(), FlowTask.WaitForSeconds(5.0)).WithoutResult(), world.UnscaledClock);
        }
        else if (what == NotificationApplicationPaused)
        {
            Save.WriteNow(); // Android and iOS: the OS can end an app in the background
        }
    }

    public override void _Process(double delta)
    {
        if (!_quitRequested || _quitting || !_saving.IsCompleted) return;
        _quitting = true;
        GetTree().Quit(); // outside flow code: quitting disposes the World in _ExitTree
    }
}
```

- `GetTree().Quit()` はフローの外（`_Process`）で呼びます。
- Android と iOS では、バックグラウンドに移るときの `NotificationApplicationPaused` で、同期的にセーブします。Godot の文書のとおり、iOS ではその処理に約 5 秒しかありません。
- Android の戻るボタンで終わるとき（`SceneTree.QuitOnGoBack` が true）は、閉じる要求の通知を通りません。戻るボタンでもセーブするなら、`QuitOnGoBack` を false にして、戻るキーの処理（[サンプル](samples.md) の `BackKeyInput`）から同じ手順を始めます。

シーンを切り替えるたびに `FlowWorldNode` ごと World を閉じる構成でも、考え方は同じです。閉じる前にフローを取り消し、上限を付けて Tick を回してから閉じます（[失敗の扱い](../guide/failures.md)）。Unity での同じ処理は [Unity のセットアップ](../unity/setup.md) にあります。
