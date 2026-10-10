# Godot のシグナル

Godot のシグナル（`[Signal]`、`EmitSignal`、`Connect`、`ToSignal` のもの）は、FlowTask のシグナルにブリッジしてからフローで待ちます。このページでは、ブリッジの作り方、1 回だけ待つ書き方、取りこぼしと寿命の注意を説明します。

このページでは、Godot の仕組みのシグナルを「Godot のシグナル」、FlowTask の `Signal<T>` と、Godot のシグナルをブリッジした `EventSignal<T>` を「FlowTask のシグナル」と呼び分けます。

## ボタンを待つ

```csharp
// A confirmation dialog: resolves once, even when both buttons are pressed in the same frame
async FlowTask<bool> Confirm(Button ok, Button cancel)
{
    using var okPressed = ok.PressedSignal();         // disconnected when the scope ends
    using var cancelPressed = cancel.PressedSignal();
    var r = await FlowTask.Race(okPressed.Next(), cancelPressed.Next());
    return r.Index == 0;
}
```

`PressedSignal()` は `BaseButton.pressed` を `EventSignal<FlowUnit>` にします。`Next()` で次の押下を待ちます。

- 接続は呼び出した時点で行われ、今のスコープがその `EventSignal` を所有します。
- スコープが終わる（完了、キャンセル、例外）と、自動で切断して `EventSignal` を閉じます。`using` を書けば、ブロックの終わりで早めに切断できます。
- フローの外で作った `EventSignal` には所有者がいないので、呼び出し側が `Dispose` してください。
- Godot が emit した時点では、フローは再開しません。再開は予約され、World の次の Tick か Flush で行われます（[セットアップ](setup.md) のフレームの中の順序）。

## ブリッジの種類

| API | 型 | 用途 |
| --- | --- | --- |
| `obj.ToFlowSignal(signal)` | `EventSignal<FlowUnit>` | 引数を捨てる。引数の個数は問わない（`pressed`、`timeout`、`finished` など） |
| `obj.ToFlowSignal<T>(signal)` | `EventSignal<T>` | 引数がちょうど 1 個のシグナル |
| `obj.ToFlowSignalArgs(signal)` | `EventSignal<Variant[]>` | 引数を `Variant[]` で受け取る。引数の個数は問わない |
| `button.PressedSignal()` | `EventSignal<FlowUnit>` | `BaseButton.pressed` |

```csharp
// A script-defined signal: [Signal] delegate void HitEventHandler(int damage);
using var hit = enemy.ToFlowSignal<int>(Enemy.SignalName.Hit);
var damage = await hit.Next();

// Two or more arguments
using var moved = body.ToFlowSignalArgs(MyBody.SignalName.Moved);
Variant[] args = await moved.Next();
```

- 型付きの `ToFlowSignal<T>` は、Godot の `Callable.From<T>` で接続します。シグナルの引数の個数が合わないと、Godot がエラーを出します。引数が 2 個以上なら `ToFlowSignalArgs` を使ってください。
- 呼び出しの時点で、シグナル元が null、解放済み、またはそのシグナルを持たないときは、例外（`ArgumentNullException`、`ObjectDisposedException`、`ArgumentException`）を投げます。
- `EventSignal<T>` の `Signal` プロパティは、中の `Signal<T>` です。`Signal<T>` を受け取るメソッドに渡せます。

## 取りこぼさない：`Subscribe`

`EventSignal<T>` は `Signal<T>` と同じ意味論です。`Next()` はエッジで、誰も待っていない間の emit は失われます。

```csharp
using var hit = enemy.ToFlowSignal<int>(Enemy.SignalName.Hit);
using var hits = hit.Subscribe(BufferPolicy.Latest); // keeps only the latest hit while nobody waits
while (true)
{
    var damage = await hits.Next();
    await Flinch(damage); // a hit during the flinch waits in the buffer
}
```

取りこぼしたくない場合は、`Subscribe(BufferPolicy)` で購読し、購読の `Next()` を待ちます。順に全部残すなら、`BufferPolicy.Queue(8, BufferOverflow.DropOldest)` のように容量とあふれたときの方針を決めます。購読と `BufferPolicy` の詳しい説明は [シグナル](../guide/signals.md) にあります。

待ち始める前の emit も、警告なしに失われます。たとえば、ダイアログを開く演出の間のボタンの押下です。これを拾うなら、演出の前に `PressedSignal()` を作って `Subscribe(BufferPolicy.Latest)` し、演出の後で購読の `Next()` を待ちます。`EventSignal` を作るだけでは足りません。

## 1 回だけ待つ

シグナルを起こす操作より前に接続してから、`Next()` を待ちます。

```csharp
var timer = NodeLifetime.Own(new Timer { WaitTime = 0.5, OneShot = true });
AddChild(timer);
using var timeout = timer.ToFlowSignal(Timer.SignalName.Timeout); // connect before Start
timer.Start();
await timeout.Next();
```

`Next()` はエッジなので、接続より前の emit は届きません。`timer.Start()` のような操作の後に接続すると、取りこぼすことがあります。

Godot の `ToSignal` が返す `SignalAwaiter` は、`AsFlow()` でブリッジできます。

```csharp
await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout).AsFlow();
```

| 書き方 | 違い |
| --- | --- |
| `using var s = obj.ToFlowSignal(signal); await s.Next();` | スコープの終了（キャンセルを含む）で切断する。こちらを勧める |
| `await ToSignal(obj, signal).AsFlow();` | 結果は `FlowTask<Variant[]>`。キャンセルされると結果を無視するが、Godot 側の one-shot の接続は、シグナルの発火かオブジェクトの解放まで残る |

> **注意**：FlowTask メソッドの中で `await ToSignal(...)` を直接 await しないでください。`SignalAwaiter` はブリッジを通していない外部の awaiter で、待っている間はキャンセルも巻き戻しもできません。その await で止まった時点で、スコープは `FlowMisuseException` で終わります。メソッドの残りと `finally`、`using` は走りません（`AddCleanup` と `NodeLifetime.Own` は走ります）。アナライザの FLOW002 がこれをエラーとして報告します（[アナライザ](../tools/analyzers.md)）。`ToFlowSignal` 系か `.AsFlow()` でブリッジしてください。

## シグナル元が先に解放されたとき

シグナル元が emit の前に解放されると、その `Next()` は完了しません。タイムアウトと `Race` するか、シグナル元のノードに結び付けて動かします。

```csharp
// Ends when the enemy leaves the tree, even if it never emits Died
await NodeLifetime.WhileInTree(enemy, WaitDied(enemy));

async FlowTask WaitDied(Enemy enemy)
{
    using var died = enemy.ToFlowSignal(Enemy.SignalName.Died);
    await died.Next();
}
```

`WhileInTree` については [ノードの寿命](lifetime.md) にあります。

## 閉じた後の `EventSignal`

所有するスコープが終わると、`EventSignal` は閉じます。

- 閉じた後に `Next()` で待つと、`SignalClosedException` になります。捕まえなければ未処理の例外になります。
- `NextOrClosed()` で待てば、例外の代わりに `(false, default)` が返ります。購読は、残っている値を渡してから `(false, default)` を返します。
- 閉じた後に届いた emit は、黙って捨てられます。

画面を閉じた後にも届きうるシグナル（報酬の付与、課金の完了など）は、画面より長く生きるフロー（`FlowWorldNode.Default.Run` で始めたものなど）で受けてください。

## 別のスレッドで emit されるシグナル

フローの中で作った `EventSignal` は World に結び付きます。Godot のシグナルがメインスレッド以外で emit されても、World の受信箱を通って、次の Tick か Flush で届きます。

フローの外で作った `EventSignal` は、World が使うまでスレッドを検査しません。ほかのスレッドで emit されると、そのスレッドで届きます（競合します）。スレッドをまたぐシグナルは、フローの中でブリッジしてください。スレッドの一般的な規則は [スレッド](../guide/threads.md) にあります。
