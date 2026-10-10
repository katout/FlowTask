# UniTask のブリッジと移行

UniTask と FlowTask は、ブリッジで互いに待てます。このページの前半では `FlowTask.UniTask` パッケージの使い方を、後半では UniTask で書いたコードを FlowTask に書き換えるときの語の対応と、動きの違いを説明します。UniTask から移行するなら、後半の「UniTask からの移行」から読み始めてください。

## 導入

UniTask のブリッジは、本体とは別のパッケージです。

- **.NET / Godot**：NuGet の `FlowTask.UniTask` を参照します。UniTask の NuGet パッケージ（2.5.10 以上）も依存として入ります。
- **Unity**：UPM の `com.katout.flowtask.unitask` を足し、UniTask（2.0.0 以上）は git URL か OpenUPM で別に入れます。UniTask を .unitypackage で入れたときは、Scripting Define Symbols に `FLOWTASK_UNITASK` を足します。足さないと、ブリッジはエラーも出さずにコンパイルから外れ、`FlowUniTask` を使うコードが `CS0246` になります。asmdef を持つコードからは、`FlowTask.UniTask` と `UniTask` を参照に足します。

手順の詳細は [インストール](../getting-started/installation.md)にあります。名前空間は本体と同じ `Katout.FlowTask` です。

## UniTask を待つ：FromUniTask と AsFlow

```csharp
async FlowTask<Texture2D> LoadIcon(string key)
{
    // The token is canceled when this scope is canceled.
    return await FlowUniTask.FromUniTask(ct => icons.Load(key, ct));
}
```

`FlowUniTask.FromUniTask` は、[Task の `FlowBridge.FromTask`](task.md) と同じ働きをします。ただし `onDiscard` は受け取りません。

- ファクトリは、ブリッジが開始したときに呼ばれます。渡されるトークンは、スコープがキャンセルされると取り消されます。
- UniTask がどのスレッドで完了しても、フローは World のスレッドで、次の Tick か Flush のときに再開します。
- 例外は await で投げられます。フローの外で取り消された UniTask は、素の `OperationCanceledException` を投げます（捕まえなければ未処理の例外になります）。
- 解放が要る結果（Addressables のハンドルなど）を返すなら、`FlowBridge.FromTask(ct => icons.Load(key, ct).AsTask(), onDiscard: …)` のように Task にしてブリッジし、[`onDiscard`](task.md) を渡すか、結果を受け取るフローの側で解放します。

すでに走っている UniTask は `.AsFlow()` で包みます。キャンセルは届きません。UniTask は 1 回しか待てないので、包んだ UniTask をほかで await しないでください。

```csharp
var loading = icons.Load(key, destroyToken);   // started elsewhere
var icon = await loading.AsFlow();
```

FlowTask メソッドの中で `await uniTask` と直接書くと、[FLOW002](../tools/analyzers.md)（エラー）になります。結果のあるブリッジを Race などの合成に渡すときは、`.ToFlowTask()` で FlowTask にします（付けないとコンパイルエラーになります）。

```csharp
var r = await FlowTask.Race(FlowUniTask.FromUniTask(ct => icons.Load(key, ct)).ToFlowTask(), FlowTask.WaitForSeconds(5));
```

例外と外部のキャンセルの受け方は [Task のブリッジ](task.md)と同じです。

## UniTask のコードからフローを待つ：ToUniTask

`async UniTask` のメソッドの中では、FlowTask を直接 await できません（[FLOW005](../tools/analyzers.md)、エラー）。フローを `FlowWorld.Run` で始め、ハンドルを `ToUniTask()` で UniTask にします。

```csharp
async UniTask ShowIntro()
{
    await FlowTaskUnity.World.Run(Intro()).ToUniTask();
}
```

- UniTask は、フローが終わると完了します。フローが成功すれば結果を返し、キャンセルで終われば `OperationCanceledException` を投げ、例外で終われば元の例外を投げます。
- `ToUniTask()` を呼んだときに `SynchronizationContext` があれば（Unity のメインスレッドなど）、完了はそこに Post されます。なければ、フローが終わった Tick、Flush、Run、Dispose の直後に World のスレッドで完了します。
- `ToUniTask()` を呼んでも、ハンドルの `Join()` は使われません。

## Unity で一緒に使う

FlowTask と UniTask は同じ PlayerLoop で動きます。UniTask の続きと FlowTask の Tick の順序は、[Unity のブリッジ](../unity/bridges.md)にあります。

## UniTask からの移行

ここからは、UniTask で書いていたコードを FlowTask で書き直すときの手引きです。

### 語の対応表

| UniTask | FlowTask |
|---|---|
| `CancellationToken` を引数で渡す | 渡さない。スコープの構造で決まる（親の終了、Race の敗北、`FlowHandle.Cancel()`） |
| `Forget()` | `FlowWorld.Run`（呼んだスコープより長く動く）、`Flow.Spawn`（呼んだスコープと一緒に終わる） |
| `WhenAny` | `FlowTask.Race`（敗者を巻き戻す） |
| `WhenAll` | `FlowTask.WhenAll`（1 つが失敗すると残りを巻き戻す） |
| `UniTask.Delay(ミリ秒)` | `FlowTask.WaitForSeconds(秒)`（スコープの Clock で進む） |
| `UniTask.Yield()`、`NextFrame()` | `FlowTask.NextFrame()` |
| `UniTask.DelayFrame(n)` | `FlowTask.DelayFrames(n)` |
| `UniTask.WaitUntil(条件)` | `FlowTask.WaitUntil(条件)` |
| `.Timeout(…)` | `FlowTask.Race(work, FlowTask.WaitForSeconds(t))`（例外ではなく結果の `Index` で分かる。負けた work は巻き戻る） |
| `UniTaskCompletionSource<T>` | `Once<T>`（World のスレッドで 1 回だけ `Set` する。失敗やキャンセルでは終えられない）。別のスレッドから、または失敗で完了させるなら、`TaskCompletionSource<T>` を `FlowBridge.FromTask(_ => tcs.Task)` で待つ |
| `AsyncReactiveProperty<T>` | `FlowProperty<T>` |
| `GetCancellationTokenOnDestroy()` | `gameObject.RunWhileActive(task)`（無効化でも止まる）。フローの中で待つなら `await gameObject.WhileActive(task)`。破棄だけで止めるなら `FlowTask.Race(task, gameObject.WaitForDestroy())`。Godot は `node.RunWhileInTree(task)` |
| `button.OnClickAsync()` | `button.ClickedSignal()` の `Next()` |
| `catch (OperationCanceledException) { 後始末; }` | `finally { 後始末; }` |
| `SuppressCancellationThrow` | ない。止められる側は、キャンセルでいつも `FlowCanceledException` によって巻き戻る。呼び出し元は、例外なしに Race の結果（`Index`）かハンドルの `Status` で分かる（毎フレームのキャンセルを軽くする書き方は [性能](../advanced/performance.md)） |
| `TaskPool.SetMaxPoolSize` | ない（プールの上限は型ごとに 1,024） |

GameObject とノードの寿命は [Unity の寿命](../unity/lifetime.md)と [Godot の寿命](../godot/lifetime.md)、シグナルは [シグナル](../guide/signals.md)にあります。

### 動きの違い

FlowTask は見た目は `async` / `await` ですが、寿命と順序を構造で保証するため、UniTask と違う動きをします。

| 観点 | UniTask | FlowTask |
|---|---|---|
| 開始 | 呼んだ瞬間に始まる | **遅延実行**。await したとき、`Flow.Spawn` か `FlowWorld.Run` に渡したとき、渡した合成が始まったときに始まる。`DoThing();` だけでは何も起きない（FLOW003） |
| `GetAwaiter()` | 何も始めない | 呼んだ時点でタスクを始める（FLOW007） |
| await の回数 | 1 回（`Preserve()` で何度でも） | 1 回（2 回目は `FlowMisuseException`）。複数で待つなら `Once<T>` |
| 親子 | ない | 開始したスコープの子になる。親が return すると子は巻き戻る（親は子を待たない）。待つなら `handle.Join()` |
| キャンセル | トークンを手で渡す | 構造で決まる。止まっている await から `FlowCanceledException` が出る |
| 外部でキャンセルされた処理 | 呼び出し元もキャンセルになる。Forget した先では、既定で黙って捨てられる | ブリッジの await で例外として投げられ、捕まえなければ未処理の例外として報告される |
| 継続 | 完了した場で同期に走ることがある | 同期には再開しない。完了は再開の予約になり、World のフラッシュで FIFO の順に処理される |
| 時間 | 既定は `Time.deltaTime` で進み、`Time.timeScale` に従う。待ちごとに `DelayType` などで実時間や倍率なしを選ぶ | スコープの Clock の時間。Clock の Pause と倍率が、その下のスコープの待ちすべてに効く |
| await できる場所 | どこでも | FlowTask メソッドの中だけ（FLOW005）。外から始めるには `FlowWorld.Run` |
| 外部の awaitable | そのまま await する | ブリッジを通す（FLOW002） |
| スレッド | 任意。`UniTask.RunOnThreadPool` や `SwitchToThreadPool` で別のスレッドへ移れる | World とそれが使ったオブジェクトは、World のスレッドに束縛される。別のスレッドの処理は `FlowBridge.FromTask(ct => Task.Run(…, ct))` で待ち、World のスレッドで再開する |
| 止まって待つ | 終わっていない UniTask は止まって待てない（`GetAwaiter().GetResult()` は例外） | World のスレッドでは止まって待たない（Tick も止まる）。終わったフローの値は `handle.Result`。フローの外からは `AsTask()` か `ToUniTask()` を await する |
| `AsyncLocal` | ビルダーが ExecutionContext を捕まえない。設定した値は呼び出し元や、後で走るほかのコードにも見える | 同じ。フローごとの値は引数で渡す（[実行モデル](../advanced/execution-model.md)） |

遅延実行と親子の関係は [フローと World](../guide/flows-and-world.md)、キャンセルは [スコープとキャンセル](../guide/scopes-and-cancellation.md)、再開の順序は [実行モデル](../advanced/execution-model.md)にあります。

### Forget の書き換え

```csharp
// UniTask
SendAnalytics().Forget();

// FlowTask: outlives this scope
FlowWorld.Current.Run(SendAnalytics());

// FlowTask: ends with this scope
_ = Flow.Spawn(SendAnalytics());
```

- `FlowWorld.Run` で始めたフローは、World のルートの子です。呼んだスコープが終わっても動き続け、例外は `FlowWorld.OnUnhandledException` に届きます。Clock は、引数で渡さなければ World の既定の Clock です。
- `Flow.Spawn` の子は、呼んだスコープが終わると、正常に return したときも巻き戻ります。子の例外は黙って消えず、Spawn したスコープの呼び出し元に届きます。
- ハンドルを使わない `Flow.Spawn(x);` の文は [FLOW008](../tools/analyzers.md) が警告します。スコープと一緒に終わってよいなら `_ =` を付けます。親より長く動かすつもりの子が親の終了で切られても、実行時には警告されません。
- イベントハンドラの中で `Flow.Spawn` を呼ぶと、イベントが発火したときに動いていたフローの子になります（フローの外なら `FlowMisuseException`）。ハンドラからは、フローの中で取っておいた World の `Run` を呼びます（[FLOW009](../tools/analyzers.md)）。
- `Discard()` は Forget ではありません。開始していない FlowTask を、開始せずに解放するメソッドです。

### キャンセルと catch

UniTask では、キャンセルを `catch (OperationCanceledException)` で受けて後始末をする書き方がよくあります。FlowTask では、後始末は `finally` か `using` に書きます。

```csharp
// UniTask
async UniTask Open(CancellationToken ct)
{
    await UniTask.Delay(500, cancellationToken: ct);
    try { await Show(ct); }
    catch (OperationCanceledException) { Cleanup(); throw; }
}

// FlowTask: no token, cleanup in finally, time on the scope's clock
async FlowTask Open()
{
    await FlowTask.WaitForSeconds(0.5);
    try { await Show(); }
    finally { Cleanup(); }
}
```

`FlowCanceledException` は `OperationCanceledException` の派生です。そのため、UniTask の習慣の `catch (OperationCanceledException) { return; }` は、FlowTask のキャンセルも握りつぶします。これは [FLOW001](../tools/analyzers.md)（エラー）が止めます。

- すべての例外を捕まえるなら `catch (Exception e) when (e is not FlowCanceledException)` と書きます。
- 外部のキャンセル（HttpClient のタイムアウトなど）だけを受けるなら `catch (OperationCanceledException e) when (e is not FlowCanceledException)` と書きます。
- `FlowCanceledException` の `CancellationToken` は `None` です。`when (e.CancellationToken == token)` はどのトークンにも一致しません。

詳しくは [失敗の扱い](../guide/failures.md)にあります。

### finally の中の await

UniTask と同じく、`finally` の中で await できます。キャンセルで入った `finally` の await は最後まで走り、呼び出し元（親、Race、WhenAll）はその終わりを待ちます。

違うのは、キャンセルの前に（`return` や例外で）入った `finally` です。その await の最中にキャンセルが届くと、await が `FlowCanceledException` を投げ、ブロックの残りが走りません。最後まで走らせたい後始末は `Flow.NonCancelable` で包みます（[FLOW010](../tools/analyzers.md)）。

```csharp
finally
{
    // Runs on a cancel too; gives up after 5 seconds.
    await Flow.NonCancelable(FlowTask.Race(Save(), FlowTask.WaitForSeconds(5)));
}
```

後始末の await に時間の上限はありません。上限が要るなら、上のように Race にします。詳しくは [スコープとキャンセル](../guide/scopes-and-cancellation.md)にあります。

### WhenAny の書き換え

`FlowTask.Race` は、負けた枝を巻き戻してから（`using` と `finally` を走らせてから）呼び出し元を再開します。負けた処理を続けたいなら、`Flow.Spawn` で始めて、ハンドルの `Join()` を枝にします。

```csharp
// Stop the losers: the other branch is unwound before the caller resumes.
var r = await FlowTask.Race(Download(), cancelButton.Next());

// Keep the losers: only the losing Join is unwound; the spawned work goes on.
var a = Flow.Spawn(LoadA());
var b = Flow.Spawn(LoadB());
var first = await FlowTask.Race(a.Join(), b.Join());
```

- Spawn した処理も、呼んだスコープが終わると巻き戻ります。スコープより長く続けるなら `FlowWorld.Run` で始めます。
- Race の枝は書いた順に開始し、同期に完了した枝がその場で勝ちます。割り込み（入力の待ち）を先に書きます。

Race と WhenAll の詳細は [合成](../guide/composition.md)にあります。

### 例外

await した子の例外は、UniTask と同じく、その await で投げ直されます。違いは次のとおりです。

- 受け手がいなくなった例外（キャンセルされた待ち手、勝者のいる Race）は投げ直さず、`FlowExceptionKind.Undelivered` として `OnUnhandledException` に報告します。
- スタックトレースには、最初に投げた場所と `catch` した場所だけが入ります。途中の段は `FlowExceptionInfo.ScopePath` で見ます。
- どこでも捕まえられなかった例外は、ルートの `FlowWorld.OnUnhandledException` に届きます。`OnUnhandledException` を設定していない World では、いちばん外側の `Tick`、`Flush`、`Run`、`Dispose` が `FlowUnhandledException` を投げます。
- `handle.Join()` は、相手がキャンセルか例外で終わると `FlowJoinException` を投げます。終わり方を問わないなら `await FlowTask.WaitUntil(handle, h => h.IsCompleted)` で待ち、`Status` を見ます。

### スレッド

UniTask はどのスレッドからでも使えますが、FlowTask の World は作ったスレッドに束縛されます。Signal や FlowProperty も、最初に使った World のスレッドに束縛されます。

```csharp
// In an async Task / UniTask method: Set runs on a pool thread. Once a World has used the property,
// Set throws FlowThreadException there, and this await rethrows it.
await Task.Run(() => property.Set(Load()));

// In a FlowTask: load on a worker thread, then write on the World's thread.
var loaded = await FlowBridge.FromTask(ct => Task.Run(() => Load(), ct));
property.Set(loaded);
```

別のスレッドから呼べるのは、`EmitFromAnyThread`、`CloseFromAnyThread`、`FlowWorld.Post`、`FlowHandle.Cancel` です。詳しくは [スレッド](../guide/threads.md)にあります。

### 名前の付け方

この文書とサンプルは、FlowTask を返すメソッドに `Async` を付けません。ゲームのフローではほとんどのメソッドが FlowTask を返すので、接尾辞が情報を持たないためです。await の書き忘れは [FLOW003](../tools/analyzers.md) が見つけます。`Async` を求めるアナライザを無効にする方法は [アナライザ](../tools/analyzers.md)にあります。
