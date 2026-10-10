# 失敗の扱い

FlowTask の失敗は、キャンセルと例外の 2 つの経路を通ります。このページでは、失敗の書き分け、例外がどこへ届くか、どこでも捕まえられなかった例外（未処理の例外）と `OnUnhandledException`、ライブラリが見つける誤用、World を閉じるときの後始末を説明します。

## 2 つの経路

| 種類 | 起こし方 | 途中のスコープ | 届く先 |
|---|---|---|---|
| キャンセル | 親の終了、Race の敗北、`handle.Cancel()` | `FlowCanceledException` で巻き戻る | 例外としては届かない。ハンドルの `Status` と `CancelCause`、`Join` の `FlowJoinException`、`AsTask()` の Task の Canceled で分かる |
| 例外 | `throw`、await した子やブリッジの失敗、ライブラリが見つけた誤用 | 普通の例外として await の鎖を上る | await している呼び出し元。捕まえなければ World の `OnUnhandledException` |

キャンセルは [スコープとキャンセル](scopes-and-cancellation.md) で説明しています。このページでは例外を扱います。想定内の失敗を戻り値で返すこともできます。戻り値はどちらの経路も通らない普通の値です。

### どれを使うか

1. **呼び出し側のバグ**（引数の不正、不変条件の違反、null 参照）は、例外を投げます。捕まえなければ未処理の例外になります。
2. **想定内で、呼び出し元がその場で対処する失敗**（通信の失敗、購入の失敗）は、専用の例外型を投げて呼び出し元で `catch` するか、結果を表す値を返します（下の「想定内の失敗」）。
3. **画面や進行の単位ごと中断し、戻り先を外側が決めるもの**（メンテナンス、セッション切れ）は、専用の例外型を投げ、戻り先でその型を `catch` します。
4. **ユーザーの中断**（戻るキー、取り消しボタン）は、中断される処理とその入力を `FlowTask.Race` します。負けた枝は巻き戻ります（[合成](composition.md)）。
5. **外部から来た例外**（Task や SDK）は、その場で `catch` し、必要なら 2 の形に変えます。

## 例外

await した子の例外は、Task と同じくその await で投げ直されます。捕まえなければ await の鎖を上り、途中のスコープはその例外で終わります（`FlowStatus.Faulted`）。どこでも捕まえなければ、World の `OnUnhandledException` に届きます。

```csharp
async FlowTask ShopScreen()
{
    while (true)
    {
        var item = await buyClicked.Next();
        try
        {
            await Purchase(item);
        }
        catch (Exception e) when (e is not FlowCanceledException)
        {
            ShowError(e);   // a bug in the store SDK ends this purchase, not the screen
        }
    }
}
```

- ボタンから始める処理を `try` / `catch` で包むと、SDK のバグ 1 つで画面やゲーム全体が止まりません。
- すべてを捕まえる `catch` には、必ず `when (e is not FlowCanceledException)` を付けます。付けないとキャンセルを握りつぶします（FLOW001。[スコープとキャンセル](scopes-and-cancellation.md)）。
- `FlowTask.Race` と `FlowTask.WhenAll` は、枝が例外で終わると、ほかの枝を巻き戻し終えてから（後始末を含めて）その例外を投げ直します。
- 失敗の配達は値の完了と同じ再開のキューを通るので、Pause にも従います。
- await で投げ直した例外のスタックトレースには、最初に投げた場所と `catch` した場所だけが入ります。途中の段は `FlowExceptionInfo.ScopePath` で見ます（[デバッグ](../tools/debugging.md)）。

### 想定内の失敗

```csharp
sealed class ApiException : Exception
{
    public ApiException(ApiError error, Exception inner) : base(error.ToString(), inner) => Error = error;
    public ApiError Error { get; }
}

async FlowTask<Stage> FetchStage()
{
    string json;
    try
    {
        json = await FlowBridge.FromTask(ct => http.GetStage(ct));
    }
    catch (HttpRequestException e)
    {
        throw new ApiException(ApiError.Network, e);
    }
    catch (OperationCanceledException e) when (e is not FlowCanceledException)
    {
        throw new ApiException(ApiError.Timeout, e); // HttpClient's timeout, or a source canceled outside the flow
    }

    return Parse(json);
}

// the caller
try
{
    ShowStage(await FetchStage());
}
catch (ApiException e)
{
    ShowError(e.Error);
}
```

- 外部の例外を、その呼び出しの失敗を表す型に変えて投げ直します。呼び出し元は、その型だけを `catch` します。SDK のバグ（`NullReferenceException` など）は変えずに上へ通します。
- `catch (HttpRequestException)` だけでは、HttpClient のタイムアウト（`TaskCanceledException`）を受けられずに未処理の例外になります。外部のキャンセルは、下の「外部でキャンセルされた Task」の節を見てください。
- 呼び出し元が毎回分岐する失敗なら、例外の代わりに結果を表す値（`enum`、値と失敗の理由を持つ型、使い慣れた Result の型）を返してもかまいません。値は普通の戻り値なので、下の「受け手に届けられない例外」の規則に左右されません。

並べた処理の最初の失敗で残りを止めるには、失敗を例外で投げ、`FlowTask.WhenAll` で待ちます（[合成](composition.md)）。1 つが失敗しても残りを最後まで走らせ、枝ごとの成否を集めるなら、各枝の中で例外を `catch` して、成否を値で返します。

### 中断して戻り先へ帰る

```csharp
sealed class SessionExpiredException : Exception { }

async FlowTask Game()
{
    while (true)
    {
        await Title();
        try
        {
            await InGame();
        }
        catch (SessionExpiredException)
        {
            await ShowNotice("Session expired");
        }
    }
}
```

- 投げた側は再開しません。途中のスコープは例外で終わり、`using` と `finally` は必ず走ります。
- 途中の catch-all（`catch (Exception e) when (e is not FlowCanceledException)`）も、この例外を捕まえます。戻り先より内側の catch-all は、ログを出して `throw;` するか、`when` で型を除きます。
- どの await とも関係なく起きる中断（セッション切れの通知など）は、戻り先の近くで Race して例外に変えます。

### Spawn した子の例外

`Flow.Spawn` した子が例外で終わると、Spawn したスコープ（所有者）が取り消され（`CancelCause.Fault`）、所有者が巻き戻った後、所有者を await している呼び出し元でその例外が投げ直されます。

```csharp
async FlowTask Battle()
{
    _ = Flow.Spawn(EnemyWave());   // if EnemyWave throws, Battle is unwound...
    await PlayerTurn();
}

async FlowTask InGame()
{
    try { await Battle(); }
    catch (WaveException) { ShowWaveError(); }   // ...and the exception arrives here
}
```

- 所有者の `catch` は `FlowCanceledException` を受け、`finally` が走ります。
- 子の例外は、Spawn を囲む同じメソッドの `catch` には届きません。受けたい範囲は、メソッドに抽出します。
- `FlowWorld.Run` で始めたルートのフローの例外は、`OnUnhandledException` へ届きます。所有者を巻き込みたくない子は、子のメソッドの中で `try` / `catch` するか、`FlowWorld.Run` で始めます。
- 子のハンドルも例外を持ちます（`Exception`。`AsTask()` の Task はその例外で Faulted）。

## OnUnhandledException と未処理の例外

どこでも捕まえられずに World のルートへ届いた例外を、未処理の例外と呼びます。未処理の例外は、`FlowWorld.OnUnhandledException` に `FlowExceptionInfo` として報告されます。

```csharp
world.OnUnhandledException = info =>
{
    if (info.Kind == FlowExceptionKind.Undelivered) Log.Warning(info.ToString());
    else Log.Error(info.ToString());   // "[Unhandled] at Game > InGame > Battle: System.InvalidOperationException: ..."
};
```

`FlowExceptionInfo` は、`Exception`、`ScopePath`（例外を投げた時点のルートからのパス。`Game > InGame > Battle` の形）、`Kind` を持ちます。

| `FlowExceptionKind` | 何か |
|---|---|
| `Unhandled` | どこでも捕まえられなかった例外。`FlowWorld.Run` で始めたフローを終えた例外と、フローの外のコード（`Post` した処理、ハンドラ）の例外 |
| `Cleanup` | 後始末の例外。取り消されたスコープの `catch` や `finally` が新しく投げた例外、`AddCleanup`、`Own` の Dispose、`onDiscard` の例外。残りの後始末は続く |
| `SwallowedCancellation` | 取り消されたスコープが、キャンセルを受け取った後に return した（握りつぶした）。スコープはキャンセルで終わる |
| `Undelivered` | 受け手に届けられなかった例外（下の節） |

`Unhandled` 以外は報告だけの種類で、制御の流れを変えません。

- `OnUnhandledException` を設定しない World では、報告はいちばん外側の `Tick`、`Flush`、`Run`、`Dispose` から、その仕事を終えた後に `FlowUnhandledException`（`ExceptionInfos` に一覧）として投げられます。ハンドラが投げた例外も、渡された報告と一緒に同じ形で投げられます。
- 報告は見つけた順にすぐ届きます。そのため、後始末の失敗（`Cleanup`）が、それを引き起こした例外より先に届くことがあります。
- `OnUnhandledException` と `OnWarning` のハンドラは、スケジューラの途中（スコープを終える処理や巻き戻しの最中）で呼ばれます。そこでフローを始めたり取り消したりすると、その処理の途中に割り込んで走るので、ハンドラはログや集計にとどめます。エラー画面を出すようにフローで応じるなら、`world.Post(() => world.Run(ShowError()))` で次の Tick か Flush の取り込みに回すか、それを待っているフローに Signal で知らせます。`Tick`、`Flush`、`Dispose` は呼べません。
- Unity と Godot の統合は既定のハンドラを設定し、`Undelivered` を警告、ほかをエラーとしてログに出します。
- `FlowHandle.Exception` は、そのタスクを終えた例外です。タスクが例外で終わった（`Faulted`）ときだけ値を持ちます。例外を投げた場所のパスと、報告だけの種類は `OnUnhandledException` で見ます。

### 受け手に届けられない例外（Undelivered）

例外を受け取るはずの相手が、すでに取り消されていたか、決着していたことがあります。そのような例外は運ばずに、`Undelivered` として `OnUnhandledException` に報告し、結果を変えません。

| 場面 | 扱い |
|---|---|
| 取り消しが確定した（まだ巻き戻り始めていない）待ち手に、子の例外が届いた。ポーズ中に失敗してそのまま閉じた画面、`World.Dispose`、同じ取り込みで届いた Cancel など | 待ち手には `FlowCanceledException` だけを投げる（`catch` は例外を見ない）。スコープはキャンセルで終わる |
| 取り消されたスコープの後始末の await が受けた例外が、捕まえられずにスコープの外へ出た | スコープはキャンセルで終わる |
| キャンセルの前に入った `finally` が `Flow.NonCancelable` の await の後に投げ直した、運んでいた例外 | スコープはキャンセルで終わる |
| 勝者の決まった Race、失敗で決着した WhenAll に、後から届いた例外 | 結果を変えない |
| 2 つ目以降の例外（Race や WhenAll が最初の例外で決着した後の兄弟） | 主は最初に処理されたもの |
| 終わりかけか取り消された所有者の、Spawn した子の例外 | 子のハンドルは例外を持つ |
| ブリッジが待つのをやめた後に失敗した Task | 例外は観測済みになり、`UnobservedTaskException` にならない |

`Undelivered` は普段の遊びでも起きます（同じフレームに失敗した 2 つの通信など）。想定内の失敗が `catch` に届かずに `Undelivered` になって困るなら、その失敗は例外ではなく値で返します（並べた処理を最初の失敗で止める書き方は、[設計の背景](../advanced/design-rationale.md) の「組み合わせで書くもの」にあります）。

## 外部でキャンセルされた Task

ブリッジした Task が外部でキャンセルされたとき（HttpClient のタイムアウト、外から渡した `CancellationToken`、UniTask の取り消し）は、フローのキャンセルではなく、ブリッジの await で投げられる普通の例外になります。捕まえなければ未処理の例外になります。上の `FetchStage` のように、`catch (OperationCanceledException e) when (e is not FlowCanceledException)` で受けます。

スコープ自身が取り消されたときは、ブリッジは結果を読まずに巻き戻るので、この `catch` には来ません。`catch (TaskCanceledException)` では受けられないキャンセルがあることなど、詳しくは [Task / ValueTask](../integrations/task.md) にあります。

## ライブラリが見つける誤用

ライブラリが実行時に見つけた誤用は、`FlowMisuseException` として、スコープの普通の例外になります。待ち手の await で投げられ、`catch` で受けられます。

| 誤用 | 起きること | 直し方 |
|---|---|---|
| FlowTask メソッドが、ブリッジしていない外部の awaitable（Task、UniTask、Unity の `Awaitable`、Godot の `SignalAwaiter`、`Task.Yield()` など）で止まった | スコープがその場で終わる。残りの `finally` と `using` は走らない（`AddCleanup` と `Own` は走る） | `.AsFlow()` か `FlowBridge.FromTask` を通す。`Task.Yield()` は `FlowTask.NextFrame()` に、`Task.Delay` は `FlowTask.WaitForSeconds` に置き換える（FLOW002） |
| FlowTask メソッドでない async メソッド（`async Task`、UniTask、`async void`）が FlowTask を await して止まった | そのメソッドは再開せず、その Task は完了しない。呼んだスコープが巻き戻った後、この例外で終わる | メソッドが FlowTask を返すようにする。フローの外から待つなら `world.Run(task).AsTask()`（FLOW005） |
| 同じ FlowTask を 2 回始めた（2 回 await した、2 つの合成に渡した）、フローの外で await した | その await が投げる | [フローと World](flows-and-world.md)、[合成](composition.md) |
| 1 つのスコープが 1 回の Tick、Flush、Run の中で 1,000,000 を超える await を同期に完了させた | 無限ループとみなし、その await でスコープを終える。完了済みの値（`FlowTask.FromResult`、`CompletedTask`）の await は数えないので、それだけを回すループは Tick、Flush、Run から戻らない | 待つループに `await FlowTask.NextFrame()` を挟む |
| 受け取った `FlowCanceledException` を取っておき、取り消されていないスコープで投げ直した（この例外を作れるのはライブラリだけ） | それを InnerException に持つ例外でスコープが終わる | キャンセルは、受け取った `catch` の中で `throw;` で投げ直すだけにする |

ほかに、`BufferOverflow.Fail` の購読があふれたときは `SubscriptionOverflowException` でスコープが終わります（[シグナル](signals.md)）。別のスレッドからの使用は `FlowThreadException`（`FlowMisuseException` の派生）です（[スレッド](threads.md)）。

> **注意**：完了済みの Task（テストの偽の `Task.FromResult` など）は直接 await しても止まらないので、実行時の誤用になりません。テストでは通り、製品でだけ例外になります。偽の Task もブリッジを通して待ちます。FLOW002 はどちらの形もコンパイル時に止めます。

よく出る例外の文面と直し方は [デバッグ](../tools/debugging.md) にあります。

## World を閉じる前に後始末を終える

`World.Dispose` はすべてのフローを取り消して、1 回で巻き戻します。Dispose は Tick を回さないので、取り消されたスコープの `finally` と `catch` の await（`Flow.NonCancelable` の await も）は `FlowCanceledException` をもう一度投げ、各ブロックは最初の await までしか走りません。外側の `finally`、`using`、`AddCleanup`、`Own` は走ります。後始末の await を切ったときは、`CleanupCutAtDispose` の警告が出ます。

保存のように最後まで走らせたい後始末があるなら、Dispose の前にフローを取り消し、上限を付けて Tick を回し、フローが終わってから Dispose します。

```csharp
void CloseWorld(FlowWorld world, FlowHandle game)
{
    game.Cancel();
    // Tick until the cleanup has ended, for 2 seconds at most.
    for (var i = 0; i < 120 && game.Status == FlowStatus.Running; i++) world.Tick(1.0 / 60);
    world.Dispose();   // cuts what is still running, and warns (CleanupCutAtDispose)
}
```

- Dispose の間に走るコード（`finally`、`AddCleanup`、`Own`、ハンドラ）からの `FlowWorld.Run` は、タスクを始めずに `Canceled` で終わったハンドルを返します。`Flow.Spawn` も始めません。
- Dispose の間は、キャンセルの握りつぶしを報告しません。
- Dispose の後に別のスレッドから送ったものは捨てられます。
- エンジンの終了とシーンの切り替えでの書き方は、[Unity のセットアップ](../unity/setup.md) と [Godot のセットアップ](../godot/setup.md) にあります。

## 詳しく：例外の細かい扱い

ふだんは意識しなくてよい規則です。多くは「後始末で例外を投げない」ことで避けられます。

- 生きているスコープの `finally` が投げた例外は、進行中の例外を置き換えます（C# の規則）。
- `World.Dispose` の間に、取り消されたスコープの `catch` や `finally` が投げた例外は、外側の `finally` の await がもう一度投げる `FlowCanceledException` に置き換わり、報告されません（C# の規則）。
- 取り消されたスコープの後始末で、自分で投げた例外は `Cleanup`、await で受けた例外は `Undelivered` として報告されます。メソッドを抽出するだけで種類が変わります。
- Spawn した子の失敗で取り消された所有者が、`FlowCanceledException` がコードに届く前に例外で抜けたときは、その例外が主になり、子の例外は `Undelivered` になります。届いた後に後始末が投げた例外は `Cleanup` で、子の例外が主になります。
- 同じ取り込みで届いた Cancel は、先に届いた失敗より先に効きます（別のスレッドからの Cancel で起きます）。
- Task を経由した例外は、受け取ったフローのブリッジの場所のパスで報告されます。フローの `AsTask()` を別のフローがブリッジで待ち、どちらも捕まえなければ、同じ例外が 2 件報告されます。フローどうしは `Join` で待ちます。
