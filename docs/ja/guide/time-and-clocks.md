# 時間と Clock

FlowTask の待ちは、実時間ではなくゲームの時間で進みます。このページでは、`Tick` に渡す時間、時間の流れを表す Clock、Pause とタイムスケール、待ちの種類（`WaitForSeconds`、`NextFrame`、`DelayFrames`、`WaitUntil`）を説明します。

## Tick に渡す時間

World の時間は、`Tick(dt)` に渡した秒数だけ進みます。dt には、倍率のかかっていない、前の Tick からの経過時間を渡します。

```csharp
var now = stopwatch.Elapsed.TotalSeconds;
world.Tick(Math.Min(now - last, 0.1));   // unscaled seconds since the last Tick; long frames clamped
last = now;
```

- ゲームの時間の倍率は dt に掛けずに、`world.DefaultClock.TimeScale` で掛けます。
- 長いフレーム（ロード、ブレークポイント、バックグラウンドからの復帰）の dt には、自分で上限を設けます。上限がないと、待ちが一度にまとめて満たされます。
- Unity と Godot の統合は、この 2 つを自動で行います。エンジンの倍率のかかっていない経過時間を、エンジン自身の上限で切り詰めて Tick し、`DefaultClock.TimeScale` を毎フレームエンジンの倍率に合わせます。

| エンジン | Tick に渡す時間 | 上限 | `DefaultClock` の倍率 |
|---|---|---|---|
| Unity | `Time.unscaledDeltaTime` | `Time.maximumDeltaTime` | `DefaultClock` が `Time.deltaTime` と同じだけ進む値（ふつうは `Time.timeScale`） |
| Godot | Godot の delta ÷ `Engine.TimeScale` | `Engine.MaxPhysicsStepsPerFrame ÷ Engine.PhysicsTicksPerSecond` | `Engine.TimeScale` |

細かい扱いは [Unity のセットアップ](../unity/setup.md) と [Godot のセットアップ](../godot/setup.md) にあります。

World が受け取る時間は、`Tick(dt)` に渡す値だけです。時間の源を変えるときも、この値を変えます。

- Unity：`AutoTick` を `false` にして、自分で Tick します（[Unity のセットアップ](../unity/setup.md)）。
- Godot：`FlowWorldNode` の `GetDeltaTime` を override します（[Godot のセットアップ](../godot/setup.md)）。
- テスト：テストが Tick で進める仮想時間を使います（[テスト](../tools/testing.md)）。

## Clock

Clock は、Pause と倍率を持つ時間の流れです。Clock は親子の木を作り、親の Pause と倍率が子に伝わります。

World は最初から 2 つの Clock を持っています。

- `world.UnscaledClock`：Tick に渡した dt だけ進みます。Pause も倍率の変更もできません。
- `world.DefaultClock`：ルートスコープの Clock で、何も指定しないフローはこの Clock で動きます。エンジンの統合では、エンジンの倍率がかかります。

ゲームで使う Clock は `world.CreateClock` で足します。

```csharp
var game = world.CreateClock("Game");                     // a child of DefaultClock: follows the engine's time scale
var ui = world.CreateClock("UI", world.UnscaledClock);   // keeps running while Game is paused

world.Run(InGame(), game);   // InGame and what it starts run on Game
```

- 親を省くと `DefaultClock` の子になります。
- ゲームのポーズ中も進む UI の Clock は、`UnscaledClock` を親にして作ります。自分の Pause と倍率だけに従います。
- `world.CreateClock` の Clock は、World と同じだけ生きます。
- `clock.Time`（この Clock の時刻）、`clock.DeltaTime`（直前の Tick で進んだ時間）、`clock.FrameCount`（止まっていなかった Tick の数）を読めます。

### フローの Clock を決める

スコープは、親のスコープの Clock を引き継ぎます。別の Clock で動かすには、始める前のタスクに Clock を付けます。

```csharp
FlowTask OpenPauseMenu() => Flow.WithClock(ui, OpenPauseMenuOnUi());

async FlowTask OpenPauseMenuOnUi()
{
    using var pause = game.Pause();   // Game stops until the menu closes; this flow runs on UI
    await PauseMenu();
}
```

- `Flow.WithClock(clock, task)` は、`task` と、それが始めるタスクを `clock` で動かします。
- `world.Run(task, clock)` も、ルートのフローの Clock を決めます。
- 待ち 1 つだけを別の Clock で数えるなら、待ちに Clock を渡します：`FlowTask.WaitForSeconds(3, world.UnscaledClock)`。
- 今のスコープの Clock は `Flow.CurrentClock` で取れます。

## Pause

`clock.Pause()` は Clock とその子孫を止めます。返すハンドルを Dispose すると解けます。

```csharp
async FlowTask Inventory()   // runs on the UI clock
{
    using var pause = game.Pause();   // released when this block ends, however it ends
    await InventoryScreen();
}
```

- Pause は参照カウントです。2 つのフローが Pause したら、両方が解くまで止まったままです。
- ハンドルは、作ったスコープが所有します。`using` を付けなくてもスコープの終わりに解けますが、それまで止まったままです。戻り値を捨てるとアナライザの FLOW004 が警告します（[アナライザ](../tools/analyzers.md)）。
- フローの外で取った Pause は、誰も所有しません。自分で Dispose するまで止まったままです。
- ハンドルの Dispose は何度呼んでもかまいません。

止まっている Clock では、時間が進まず、フレームも数えません。その Clock で動くスコープの再開は保留され、解けた後のフラッシュで元の順に処理されます。キャンセルによる巻き戻しの開始は保留されませんが、後始末の中の await の再開は保留されます。

> **注意**：自分が動いている Clock（かその祖先）を Pause すると、自分の再開も止まります。ポーズメニューやダイアログが `DefaultClock` で動いたまま `DefaultClock` を Pause すると、閉じるボタンにも反応しなくなります。このとき、`PausedOwnClock` の警告が World で 1 回出ます。ダイアログとメニューは、上の例のように `Flow.WithClock(ui, …)` で UI の Clock に固定します。

ゲームが止まったまま動かないときは、ダンプの先頭の `Paused clocks:` で、誰が Pause を持っているかを見ます（[デバッグ](../tools/debugging.md)）。

## TimeScale

`clock.TimeScale` は、その Clock の倍率です。実際に進む時間は、祖先の倍率をすべて掛けたものになります。

```csharp
game.TimeScale = 0.5;   // slow motion for everything on Game
```

- 有限で 0 以上の値だけを設定できます。祖先との積が有限にならない値も含めて、それ以外は `ArgumentOutOfRangeException` になります。
- 倍率 0 は時間を止めますが、フレームは数え続けます（`DelayFrames` と `NextFrame` は進みます）。フレームも止めるなら Pause を使います。
- `UnscaledClock` の倍率は変えられません（`FlowMisuseException`）。

> **注意**：Unity と Godot の統合は、`DefaultClock.TimeScale` を毎フレームエンジンの倍率で上書きします。自分のスローモーションは、`DefaultClock` の子の Clock（上の `game` など）に設定します。

## スコープの Clock

敵 1 体だけのスローのように、1 つのフローに属する時間には `Flow.CreateClock` を使います。作ったスコープが所有し、スコープの終わりに消えます。

```csharp
async FlowTask EnemyAI(Enemy self)
{
    var local = Flow.CreateClock($"Enemy#{self.Id}");   // a child of this scope's clock
    local.TimeScale = self.IsSlowed ? 0.3 : 1.0;         // this enemy only
    await Flow.WithClock(local, Behave(self));
}
```

- 親を省くと `Flow.CurrentClock` の子になり、スコープの Pause と倍率に従います。親にできるのは、World の Clock と、今のスコープか祖先が作った Clock です。別のスコープが作った Clock の上で始めたフローで親を省くと `FlowMisuseException` になるので、親を渡します。
- スコープの Clock は、子のスコープ（後始末の await を含む）がすべて終わってから消えます。子は最後までその Clock を使えます。
- 消えた Clock は進みません。その上でタスクを始める、Pause する、倍率を設定すると `FlowMisuseException` になります。消えた Clock で待っていた待ちは、次の Tick で await に `FlowMisuseException` を投げます。
- スコープより長く使う Clock は `world.CreateClock` で作ります。

> **注意**：スコープの Clock を早く消す API はありません。長生きするスコープのループの中で毎回 `Flow.CreateClock` を呼ぶと、スコープが終わるまで Clock が積み上がり、毎 Tick の費用も増えます。しばらく使う Clock は、await する子の FlowTask メソッドの中で作ります。`world.Clocks` が増え続けるなら、これを疑います。

## 待ちの種類

| 待ち | 終わるとき |
|---|---|
| `FlowTask.WaitForSeconds(秒)` | Clock の時間が指定の秒数だけ進んだ最初の Tick |
| `FlowTask.NextFrame()` | Clock の次のフレーム（`DelayFrames(1)` と同じ） |
| `FlowTask.DelayFrames(n)` | Clock が止まっていない Tick を n 回数えたとき |
| `FlowTask.WaitUntil(条件)` | 条件が true を返したとき |
| `FlowTask.Never()` | 終わらない。スコープが取り消されたときに巻き戻る |

`Never` 以外の待ちは、省略できる最後の引数に Clock を渡せます。渡さなければスコープの Clock で数えます。

```csharp
await FlowTask.WaitForSeconds(1.5);                  // seconds, not milliseconds
await FlowTask.NextFrame();
await FlowTask.WaitUntil(() => player.IsGrounded);
await FlowTask.WaitUntil(player, p => p.IsGrounded); // with state: no closure
```

- `WaitForSeconds` の引数は秒です（UniTask の `Delay` のミリ秒ではありません）。時間を double で足していくので、フレームの境界に当たる秒数では 1 フレーム遅れることがあります（60fps での 0.1 秒の待ちは 7 回目の Tick で終わります）。正確なフレーム数で待つなら `DelayFrames` を使います。
- `NextFrame` と `DelayFrames` は、早くても次の Tick で再開します。数えるのは、その Clock と祖先が Pause されていない Tick です（倍率 0 の Tick は数えます）。`DelayFrames(0)` はその場で終わります。
- `WaitUntil` は、待ち始めたときに 1 回、その後は Clock が止まっていない Tick ごとに条件を評価します。条件が投げた例外は await で投げられます。
- 毎フレーム止まり直すポーリング（`while (!ready) await FlowTask.NextFrame();`）より、`WaitUntil(() => ready)` と書くほうが、止まったときにダンプで待っている時間が見えます。値の変化で待つなら `FlowProperty<T>.WaitUntil` が向いています（[シグナル](signals.md)）。

それぞれの待ちがどの Tick で再開するかの細かい規則は [実行モデル](../advanced/execution-model.md) にあります。

## 規則と落とし穴

- 物理フレームで動かすフローは、`FixedUpdate` や `_PhysicsProcess` で Tick する別の World で動かします。Clock ごとに別の頻度で進めることはできません（[Godot の物理フレーム](../godot/physics.md)、[Unity のセットアップ](../unity/setup.md)）。
- 同じ dt の列と同じ入力を与えれば、実行順は同じになります（[実行順の規則](../advanced/execution-model.md)）。
- 1 回の大きな dt で 2 つの期限が過ぎると、Race の勝者は期限の早さではなく、その時間待ちを待ち始めた順で決まります。枝が時間待ちそのものなら、引数の順です（[合成](composition.md)）。大きな dt は上限で切り詰めます。
- Clock は `Tick` に渡した時間で進むので、実時間とは限りません（長いフレームの切り詰め、アプリが止まっている間）。通信の上限のように実時間で計るものは、外部の側の仕組み（`HttpClient.Timeout`、`CancellationTokenSource.CancelAfter`）で付けます（[合成](composition.md)）。
