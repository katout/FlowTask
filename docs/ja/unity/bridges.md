# Unity のブリッジ

Unity の非同期の仕組み（UnityEvent、AsyncOperation、Addressables、Awaitable、Task）とフローをつなぐ方法を説明します。uGUI のボタンを待つヘルパーと、実行中のフローを見る Scope Tree ウィンドウも扱います。

## UnityEvent を Signal にする

```csharp
async FlowTask Shop(UnityEvent<int> onPurchased)
{
    using var purchased = onPurchased.ToSignal(); // EventSignal<int>, owned by the current scope
    var itemId = await purchased.Next();
}
```

`UnityEvent` と `UnityEvent<T>` に `ToSignal()` があります。返る `EventSignal<T>` は、`Next()`、`NextOrClosed()`、`Subscribe(policy)` で待てます。

- **フローのコードの中で呼んでください**。リスナーは現在のスコープが所有し、スコープの終わり（か、それより前の Dispose）で外され、EventSignal は閉じます。フローの外で呼んだときは、自分で Dispose します。
- Invoke は受け手をその場では再開しません。次の Flush ポイントか Tick で再開します（[セットアップ](setup.md) の「Tick と Flush」）。
- 引数が 2 つ以上のイベントは、`FlowBridge.FromCallback` にラムダを 1 つ書いてブリッジします。

  ```csharp
  using var hits = FlowBridge.FromCallback<(GameObject, int)>(emit =>
  {
      UnityAction<GameObject, int> listener = (target, damage) => emit((target, damage));
      onHit.AddListener(listener);
      return () => onHit.RemoveListener(listener); // called when the scope ends
  });
  ```

### Next() はエッジ

`Next()` は、待ち始めた後の Invoke だけを受け取ります。待っていない間の Invoke は捨てられます。ループの本体を実行している間の Invoke も取りこぼしたくないなら、ループの前で購読します。

```csharp
using var purchased = onPurchased.ToSignal();
using var queue = purchased.Subscribe(BufferPolicy.Latest); // keeps the latest one while the loop body runs
while (true)
{
    var itemId = await queue.Next();
    await Grant(itemId);
}
```

`BufferPolicy` の種類（`Latest`、`Queue(容量, あふれ方)`）と、エッジと購読の違いは [シグナル](../guide/signals.md) にあります。ループの中で `Next()` を待つ書き方には、アナライザが情報（FLOW006）を出します。

### 閉じた後に届くコールバック

EventSignal が閉じた後に `Next()` で待つと `SignalClosedException` になり、閉じた後に届いた値は黙って捨てられます。リワード広告の報酬や課金の結果のように、画面を閉じた後にも届きうるコールバックは、画面より長く生きるフロー（`FlowTaskUnity.World.Run` で始めたサービス）で受けてください。

## AsyncOperation を待つ

```csharp
await SceneManager.LoadSceneAsync("Battle").AsFlow();
var tex = (Texture2D)await Resources.LoadAsync<Texture2D>("icon").AsFlow(); // a ResourceRequest returns the asset
```

- `AsyncOperation.AsFlow()` は `FlowTask` を、`ResourceRequest.AsFlow()` は読み込んだアセットを返す `FlowTask<UnityEngine.Object>` を返します。
- 完了は `AsyncOperation.completed` で受け、次の Flush ポイントか Tick で再開します。すでに完了している操作は、割り当てなしですぐに完了します。
- スコープが先にキャンセルされると、ハンドラを外して完了を無視します。`AsyncOperation` には取り消す API がないので、ブリッジは待つのをやめるだけで、操作は走り続けます。
  - 止められる操作は、スコープの後始末（`finally` か `Flow.AddCleanup`）で止めます（`UnityWebRequest.Abort()`。下の「Addressables と UnityWebRequest」）。
  - 待つのをやめた後に終わった読み込み（シーン、AssetBundle）は、フローのコードで片付けます。待つのをやめたとき（取り消されたとき）だけ、`operation.completed` に片付けを登録します。終わった操作に登録しても呼ばれます。
- 進捗を表示するなら、読み込みと、`progress` を毎フレーム読むループを Race します（[サンプル](samples.md) の並列読み込み）。

> **注意**：FlowTask メソッドの中で `AsyncOperation` や `Awaitable` を `.AsFlow()` なしで直接 await しないでください。スコープの外の await なので、そこで止まった時点でスコープが `FlowMisuseException` で終わります。アナライザは FLOW002 のエラーで報告します。

## Addressables と UnityWebRequest

Addressables の `AsyncOperationHandle` は `AsyncOperation` ではないので、`.Task` を `FlowBridge.FromTask` でブリッジします。

```csharp
using var cancel = view.CancelButton.ClickedSignal();
var r = await FlowTask.Race(
    cancel.Next(),
    FlowBridge.FromTask(
        _ => Addressables.LoadAssetAsync<GameObject>("Boss").Task,
        onDiscard: asset => Addressables.Release(asset)).ToFlowTask()); // a result nobody received is released
if (!r.TryGet1(out var boss)) return; // canceled: onDiscard releases the asset when it arrives
```

- 読み込みにはトークンを渡せないので、取り消されたブリッジは待つのをやめるだけです。後で届いた結果は `onDiscard` が受け取ります（[Task / ValueTask のブリッジ](../integrations/task.md) の `onDiscard`）。
- 結果のあるブリッジを Race などに渡すときは、`.ToFlowTask()` を付けます。付けないと型引数を推論できません。
- 複数のアセットを進捗付きで読み込み、受け取られなかったものをまとめて解放する形は、[サンプル](samples.md) の並列読み込みにあります。

`UnityWebRequest` の `SendWebRequest()` は `AsyncOperation` なので、`.AsFlow()` で待てます。取り消されたら `finally` で `Abort()` を呼んで通信を止めます。実時間の上限は `timeout` で付けます。`FlowTask.WaitForSeconds` はゲームの時間で進み、ポーズで止まるためです（[合成](../guide/composition.md) の「タイムアウト」）。

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

## Awaitable を待つ

```csharp
await Awaitable.WaitForSecondsAsync(1f).AsFlow();
int n = await ComputeAsync().AsFlow(); // Awaitable<T>
```

- `Awaitable.AsFlow()` は `FlowBridge.FromTask` の上に作られていて、`task.AsFlow()` と同じく `TaskBridge`（`Awaitable<T>` なら `TaskBridge<T>`）を返します。await するほか、Race などに渡せます。`TaskBridge<T>` を渡すときは `.ToFlowTask()` を付けます。
- どのスレッドで完了しても（`Awaitable.BackgroundThreadAsync()` を含む）、フローは World のスレッドで再開します。
- スコープがキャンセルされると、`Awaitable.Cancel()` を呼んで結果を無視します。
- フローの外で取り消された Awaitable は、await で `OperationCanceledException` を投げます。受けるなら `catch (OperationCanceledException e) when (e is not FlowCanceledException)` と書きます。スコープのキャンセルはこの `catch` に来ず、そのまま巻き戻ります。

Unity では、async メソッドの Awaitable に `Cancel()` を呼んでも、その中で待っている Awaitable には伝わりません。中の待ちまで止めるには、メソッドに `CancellationToken` を受け取らせて中の待ちに渡し、`FlowBridge.FromTask` でスコープのトークンを渡します。

```csharp
var map = await FlowBridge.FromTask(async ct => await BuildMapAsync(ct)); // the scope's token reaches the awaits inside

static async Awaitable<Map> BuildMapAsync(CancellationToken ct)
{
    await Awaitable.NextFrameAsync(ct); // stops when the scope is canceled
    return Map.Generate();
}
```

Task と ValueTask のブリッジ全般（`FromTask`、`onDiscard`、外部のキャンセル）は [Task / ValueTask のブリッジ](../integrations/task.md) にあります。

## Task と UnitySynchronizationContext

Unity のメインスレッドには `UnitySynchronizationContext` があります。`FlowBridge.FromTask` のファクトリの中で `ConfigureAwait(false)` なしに await すると、その続きはこのコンテキストに Post され、Update フェーズ（`ScriptRunDelayedTasks`）で走ります。

```csharp
var bytes = await FlowBridge.FromTask(async ct =>
{
    var data = await File.ReadAllBytesAsync(path, ct); // continues on the main thread in ScriptRunDelayedTasks
    return Decode(data);
});
```

ふだんのゲームではこれで問題ありません。ただし、メインスレッドを止めたまま Task の完了を待つコード（テストで Tick をループで回すなど）では、続きが走らずに終わらなくなります。そうしたコードでは、ファクトリの中の await に `ConfigureAwait(false)` を付けます。テストでの対処は [テスト](../tools/testing.md) にあります。

重い計算は、`FlowBridge.FromTask(ct => Task.Run(() => Work(), ct))` で別のスレッドに出します。Awaitable の `BackgroundThreadAsync()` で別のスレッドに移った処理も同じく、フローは World のスレッドで結果を受け取ります（[スレッド](../guide/threads.md)）。

完了した Task がフローを再開する時機は次のとおりです。

- ブリッジは、Task を完了させたスレッドの上で同期に、完了を World に積みます。どのスレッドで完了しても、フローはすぐ後の Tick か Flush ポイントで再開します。
- `MonoBehaviour.Update` で完了させた Task は、同じフレームの Update の末尾の Flush で再開します。

逆向きに、フローを Task のコードから待つときは `FlowHandle.AsTask()` を使います。この Task は、フローが終わった Tick、Flush、Run、Dispose の直後に World のスレッドで完了します。メインスレッドでこの Task を `.Result` や `Wait()` で止まって待つと、Tick も止まるので終わりません（[フローと World](../guide/flows-and-world.md) の「FlowHandle」）。

## uGUI のボタン

`FlowTask.Unity.UI` アセンブリ（`com.unity.ugui` があるときだけコンパイルされる）に、ボタンのクリックを待つヘルパーがあります。

```csharp
using var ok = view.OkButton.ClickedSignal(); // EventSignal<FlowUnit> over Button.onClick
await ok.Next();
```

- `button.ClickedSignal()` は `EventSignal<FlowUnit>` を返します。ほかのコントロールは UnityEvent をそのままブリッジします（`toggle.onValueChanged.ToSignal()`）。どれも上の `ToSignal()` と同じく、フローのコードの中で呼びます。
- クリックは uGUI の EventSystem（Update フェーズ）で起きるので、待っているフローは同じフレームの Update の末尾の Flush で再開します。

### 演出中のクリックを拾う

`Next()` はエッジなので、待ち始める前のクリック（画面を開く演出の間など）は届かず、警告も出ません（UniTask の `button.OnClickAsync()` と同じです）。拾いたいクリックは、演出の前にシグナルを作って購読します。シグナルを演出の前に作るだけでは足りません。

```csharp
using var ok = view.OkButton.ClickedSignal();
using var okClicks = ok.Subscribe(BufferPolicy.Latest); // before the animation: a click during it is kept
await FadeIn(view);
await okClicks.Next();
```

購入中の連打のように捨てたいクリックは、`Next()` のままにします（[サンプル](samples.md) の購入の場面）。

## UniTask と一緒に使う

FlowTask と UniTask は同じ PlayerLoop で動きます。変換の API と移行の手引きは [UniTask のブリッジと移行](../integrations/unitask.md) にあり、ここでは Unity の PlayerLoop の上での順序だけを書きます。

- UniTask は、各フェーズの先頭と末尾（`PlayerLoopTiming.LastUpdate` など）にランナーを入れます。`PlayerLoopTiming.Update` の続きは、FlowTask の Tick より前に走ります。その続きで `Time.timeScale` を変えると、新しい値は次のフレームから効きます（[セットアップ](setup.md) の「時間」）。
- `PlayerLoopTiming.LastUpdate` の続きは、`MonoBehaviour.Update` の後に走ります。そこで起きた Emit は、遅くとも PreLateUpdate の末尾の Flush で、同じフレームのうちに受け手を再開します。
- UniTask の `DelayFrame` で完了した待ちをブリッジしたフローは、そのフレームのうちに再開します。
- `ToUniTask()` は、呼んだときの SynchronizationContext に完了を Post します。メインスレッドで呼んだものは `ScriptRunDelayedTasks` で完了します。
- `FlowTaskUnity.Configure` で FlowTask のシステムを入れ直しても、UniTask のシステムは残ります。

## Scope Tree ウィンドウ

`Window > FlowTask > Scope Tree` で開きます。Play Mode の間、既定の World と、`FlowWorldRegistry.Register(world)` で登録した World のスコープの木を 0.25 秒ごとに更新して表示します。

- 各ノードの種類（scope、combinator、wait）、Clock、待っているものと待った時間（`waiting: Signal.Next for 2.3s` など）、キャンセル中のスコープ（橙色、理由付き）を表示します。上部には、生きているスコープの数と、Clock ごとの時刻、Pause、倍率が出ます。
- ツールバーで、自動更新、テキスト表示（`FlowWorld.Dump()` の出力）、scope だけの表示、名前のフィルタ、ダンプのコピーを切り替えます。World が複数あれば、表示する World を選べます。
- FlowTask メソッドのスコープの行をダブルクリックすると、そのメソッドを宣言したスクリプトを開きます。
- ゲームが止まったまま動かないとき（Pause の外し忘れ）は、テキスト表示の先頭の `Paused clocks:` を見ます。止まっている Clock ごとに Pause の数と持ち主が出ます（例：`Game: paused x2 by Main > PauseMenu, <outside any flow>`）。
- World がないとき（Play Mode の外、再生中の再コンパイルの後など）は、その理由を表示します。

自分で作った World を表示するには、作ったときに登録し、Dispose の前に外します（Dispose された World は自動で一覧から消えます）。

```csharp
FlowWorldRegistry.Register(world);   // shown in the Scope Tree window
// ...
FlowWorldRegistry.Unregister(world);
world.Dispose();
```

ダンプの読み方と診断の API は [デバッグと診断](../tools/debugging.md) にあります。
