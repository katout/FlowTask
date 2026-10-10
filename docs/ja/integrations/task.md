# Task / ValueTask のブリッジ

ファイルの読み込みや通信のように `Task` を返す処理は、ブリッジを通してフローから待ちます。このページでは、`FlowBridge.FromTask` と `.AsFlow()` の使い方、例外と外部のキャンセルの扱い、受け取られなかった結果の後始末、フローの外から結果を待つ `AsTask()` を説明します。

## 外部の処理を待つ：FromTask

```csharp
async FlowTask<Stage> LoadStage(string path)
{
    // The token is canceled when this scope is canceled.
    var json = await FlowBridge.FromTask(ct => File.ReadAllTextAsync(path, ct));
    return Stage.Parse(json);
}
```

`FlowBridge.FromTask` には、`CancellationToken` を受け取って `Task` を返すファクトリを渡します。

- ファクトリは、ほかの FlowTask と同じく、ブリッジが開始したとき（await したときや、合成に渡して合成が始まったとき）に呼ばれます。
- 渡されるトークンは、スコープがキャンセルされると取り消されます。Race に負けたときも、親が終わったときも、外部の処理にキャンセルが届きます。
- Task がどのスレッドで完了しても、フローは World のスレッドで、次の Tick か Flush のときに再開します。重い計算を別のスレッドで走らせるときも、`FlowBridge.FromTask(ct => Task.Run(() => Work(), ct))` と書いて同じように待ちます（[スレッド](../guide/threads.md)）。

結果のない `Task` も同じように書けます（`FlowBridge.FromTask(ct => SaveAsync(data, ct))`）。

## 走っている Task を待つ：AsFlow

すでに始まっている `Task` や `ValueTask` は、`.AsFlow()` で包んで待ちます。

```csharp
var download = http.GetStringAsync(url);   // started elsewhere, without the scope's token
var body = await download.AsFlow();
```

`.AsFlow()` はキャンセルを伝えません。スコープがキャンセルされると、フローは待つのをやめて巻き戻りますが、Task は動き続けます。トークンを受け取れる処理なら `FromTask` を使ってください。

## 直接 await しない

FlowTask メソッドの中で、外部の awaitable を直接 await してはいけません。

```csharp
async FlowTask LoadStage()
{
    var json = await File.ReadAllTextAsync(path);   // FLOW002: not bridged
    await Task.Delay(500);                          // FLOW002: ignores the World's time and Pause
}
```

外部の awaitable を待っている間、FlowTask はスコープをキャンセルも巻き戻しもできません。そのため、フローがそこで止まると、スコープはその時点で `FlowMisuseException` で終わります。

- 対象は `Task`、`ValueTask`、UniTask、Unity の `Awaitable` と `AsyncOperation`、Godot の `SignalAwaiter`、`Task.Yield()`、`ConfigureAwait(...)` の結果、`await foreach` です。
- スコープの残りの `finally` と `using` は走りません。`Flow.AddCleanup` と `Flow.Own` の後始末だけが走ります。
- 例外は待ち手の await で投げられ、`catch` で受けられます。文面は `FlowTask method '…' suspended on TaskAwaiter<Int32>, which is not a FlowTask` のように、awaiter の型とスコープのパス、使うべきブリッジを示します。
- すぐに完了した await は止まらないので、実行時には見つかりません。テストの完了済みの偽の Task では通り、製品でだけ失敗することがあります。

これをコンパイル時に止めるのが [FLOW002](../tools/analyzers.md)（エラー）です。コード修正で `FromTask` か `.AsFlow()` に書き換えられます。`Task.Delay` の代わりは `FlowTask.WaitForSeconds`、`Task.Yield()` の代わりは `FlowTask.NextFrame()` です（[時間と Clock](../guide/time-and-clocks.md)）。

Unity と Godot の awaitable のブリッジは、[Unity のブリッジ](../unity/bridges.md)と [Godot のシグナル](../godot/signals.md)にあります。

## 例外と外部のキャンセル

ブリッジした Task の例外は、その await で投げられます。ふつうの `try` / `catch` で受けられます。

```csharp
try
{
    var save = await FlowBridge.FromTask(ct => cloud.Download(ct));
    Apply(save);
}
catch (HttpRequestException e)
{
    await ShowError(e.Message);
}
catch (OperationCanceledException e) when (e is not FlowCanceledException)
{
    // HttpClient's timeout: the Task was canceled outside the flow.
    await ShowError("Timed out");
}
```

フローの外でキャンセルされた Task（HttpClient のタイムアウト、外から渡した `CancellationToken`）は、フローのキャンセルではなく、ふつうの例外として届きます。捕まえなければ未処理の例外になります。

- スコープ自身がキャンセルされたときは、ブリッジは結果を読まずに巻き戻ります。ブリッジの await に `OperationCanceledException` が届くのは、スコープの外が原因のキャンセルだけです。
- 外部のキャンセルだけを受けるなら、`catch (OperationCanceledException e) when (e is not FlowCanceledException)` と書きます。フィルタのない `catch (OperationCanceledException)` は、フロー自身のキャンセルも握りつぶします（[FLOW001](../tools/analyzers.md)）。
- `catch (TaskCanceledException)` では足りないことがあります。UniTask や `ThrowIfCancellationRequested` のキャンセルは、素の `OperationCanceledException` です。
- Task から届いた例外を `OnUnhandledException` に報告するときの `ScopePath` は、ブリッジした場所のパスです。

外部の例外を、呼び出し元が受ける想定内の失敗に変える書き方は、[失敗の扱い](../guide/failures.md)にあります。

## 受け取られなかった結果の後始末：onDiscard

Addressables のハンドルのように、解放が要る結果を返す処理には `onDiscard` を渡します。

```csharp
var boss = await FlowBridge.FromTask(
    ct => assets.Load("boss", ct),
    onDiscard: a => a.Release());   // called only for a result no flow received
```

`onDiscard` は、どのフローも受け取らなかった成功の結果を 1 回受け取ります。

- 呼ばれるのは、ブリッジがキャンセルされた後に届いた結果、待ち手が巻き戻って誰も取らなかった結果、同じフラッシュで Race に負けた結果、失敗した WhenAll の中の結果、`WithoutResult()` で無視した結果です。
- フローが await で受け取った値は、そのフローのものです。後で捨てても `onDiscard` は呼ばれません。
- Race や WhenAll に渡したブリッジの値は、合成が受け取った時点で受け取られたとみなします。合成の結果がフローに届く前に捨てられても（外側の Race に負けたなど）、`onDiscard` は呼ばれません。
- `Flow.Spawn` か `FlowWorld.Run` で始めたブリッジの値は、ハンドルのために残ります（`Result` をいつでも読めます）。このブリッジには `onDiscard` は呼ばれません。
- 呼ばれるのは World のスレッドで、次の Tick か Flush の取り込みのとき、または `FlowWorld.Dispose` の中です。
- ただし、World を Dispose した後に Task が終わったときは、Task を完了させたスレッド（多くはスレッドプール）でその場で呼ばれます。そこでエンジンの API を呼ぶなら、メインスレッドへ戻してから行ってください。このとき `onDiscard` が投げた例外は報告されずに捨てられます。
- それ以外のときに `onDiscard` が投げた例外は、`FlowExceptionKind.Cleanup` として報告されます。

`onDiscard` を渡さないブリッジの受け取られなかった結果は、報告せずに捨てられます。誰も await しない Task の結果と同じです。

ブリッジが待つのをやめた後に Task が失敗したときは、例外を `FlowExceptionKind.Undelivered` として報告します（例外は観測済みになり、`UnobservedTaskException` にはなりません）。キャンセルで終わったときは何も報告しません。

## 合成に渡す：キャンセルボタンとタイムアウト

ブリッジは Race や WhenAll にも渡せます。結果のあるブリッジは、`.ToFlowTask()` で `FlowTask<T>` にしてから渡します。付けないと型引数を推論できず、コンパイルエラー（CS1503）になります。結果のないブリッジは、そのまま渡せます。

```csharp
var r = await FlowTask.Race(
    cancelButton.Next(),
    FlowBridge.FromTask(ct => assets.Load("boss", ct), onDiscard: a => a.Release()).ToFlowTask());
if (!r.TryGet1(out var boss)) return;   // canceled: ct was canceled, and a result that still arrives goes to onDiscard
```

- Race に負けたブリッジのトークンは取り消されます。トークンを受け取る処理なら、そこで止まります。
- 止められない処理（トークンを取らない読み込みなど）は最後まで走ります。届いた結果はどのフローも受け取らないので、`onDiscard` に渡ります（上の「受け取られなかった結果の後始末」）。

時間の上限も Race で付けられます。

```csharp
var r = await FlowTask.Race(
    FlowBridge.FromTask(ct => http.GetStage(ct)).ToFlowTask(),
    FlowTask.WaitForSeconds(10));
if (!r.TryGet0(out var json)) return ApiError.Timeout;   // the request was canceled through its token
```

`WaitForSeconds` はスコープの Clock で進むので、ゲームのポーズ中は上限も止まります。通信の上限を実時間で付けるなら、外部の側の仕組み（`HttpClient.Timeout`、`CancellationTokenSource.CancelAfter`）を使います。その場合のキャンセルは例外として届きます（上の「例外と外部のキャンセル」）。合成の使い方は [合成](../guide/composition.md)にあります。

## 外部のトークンでフローを止める

外から渡された `CancellationToken` でフローを止めるには、ハンドルの `Cancel` を登録します。`Cancel` はどのスレッドからでも呼べます。

```csharp
async Task<Save> Download(CancellationToken token)
{
    var handle = world.Run(DownloadSave());
    using var registration = token.Register(handle.Cancel);
    return await handle.AsTask();   // canceled when the flow is canceled
}
```

別のスレッドからの `Cancel` は、次の Tick か Flush で効きます。それまで `Status` は変わりません。World が Dispose 済みか、フローが終わっていれば何もしません。

## フローの外から結果を待つ：AsTask

`Task` を返すメソッドの中で FlowTask を await してはいけません（[FLOW005](../tools/analyzers.md)、エラー）。フローを `FlowWorld.Run` で始め、ハンドルの `AsTask()` を待ちます。

```csharp
Task<int> GetScore() => world.Run(Arcade()).AsTask();
```

- Task は、フローが終わった Tick、Flush、Run、Dispose の直後に、World のスレッドで完了します。フローが Succeeded なら RanToCompletion、Canceled なら Canceled、Faulted なら元の例外で Faulted になります。
- Task の続きは World のスレッドで同期に走ります。背景のスレッドで `await handle.AsTask()` したコードも、`ConfigureAwait(false)` を付けても同じです。重い処理は `Task.Run` などで移してください。
- 続きの中で、別の `AsTask()` を `.Result` や `.Wait()` で待ったり、Tick を回して待ったりしないでください。続きの中の Tick は AsTask を完了させないので、終わりません。ハンドルの `Status` か `IsCompleted` を見ます。
- フロー自身やその子孫が、自分の `AsTask()` をブリッジして待つと、終わりません。フローは子が終わってから終わるためです。これは検出されません。
- `AsTask()` を呼んでも、ハンドルの `Join()` は使われません。何度でも呼べます。
- `AsTask()` は World のスレッドで呼びます（別のスレッドからは `FlowThreadException`）。

Task は World の Tick や Flush の最後に完了するので、World を進めるコードが動いていないと完了しません。Unity と Godot では統合が毎フレーム Tick します。コンソールのプログラムでは、自分のループで Tick します（[最初のフロー](../getting-started/first-flow.md)）。

World のスレッドで、この Task を `.Result` や `.Wait()` で止まって待たないでください。Tick も止まるので、終わりません。終わったフローの値は `handle.Result` で読めます。別のスレッドからは、World のスレッドで取った Task を止まって待てます（[スレッド](../guide/threads.md)）。

## フローどうしは Join で待つ

フローどうしは、Task を経由せず、ハンドルの `Join()` で待ちます。

```csharp
var loading = Flow.Spawn(LoadAssets());
await PlayIntro();
await loading.Join();
```

ただし、相手の World が別のスレッドにあるときは `Join` を使えません（await で `FlowThreadException` になります）。相手の World のスレッドで `AsTask()` を取り、それをブリッジで待ちます。

```csharp
// On the other World's thread:
Task<Map> mapTask = otherWorld.Run(BuildMap()).AsTask();

// In a flow of this World:
var map = await FlowBridge.FromTask(_ => mapTask);
```

同じスレッドのフローどうしをこの形でつながないでください。相手のフローが失敗し、待った側もその例外を捕まえないと、同じ例外が 2 件報告されます（相手のフローの分と、待った側の分）。

## 詳しく

- ブリッジは 1 回ごとに割り当てます。毎フレームの待ちには、FlowTask の待ち（`WaitForSeconds`、`NextFrame`、シグナル）を使ってください（[性能](../advanced/performance.md)）。
- Unity では、ファクトリの中で `ConfigureAwait(false)` を付けずに await すると、続きが `UnitySynchronizationContext` を通ります（[Unity のブリッジ](../unity/bridges.md)）。
- テストで偽の Task を作るときの注意は、[テスト](../tools/testing.md)にあります。
- UniTask は [UniTask](unitask.md)、R3 は [R3](r3.md) のページを見てください。
