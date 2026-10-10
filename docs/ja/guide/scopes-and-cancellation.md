# スコープとキャンセル

FlowTask では、CancellationToken を手で配りません。フローはスコープの木を作り、親が終わると子が巻き戻ります。このページでは、スコープの木、キャンセルと巻き戻し、後始末の書き方（`using`、`finally`、`Flow.AddCleanup`、`Flow.Own`、`Flow.NonCancelable`）、キャンセルを受けた `catch` の書き方を説明します。

## スコープの木

FlowTask メソッド 1 回の実行を、スコープと呼びます。始めたタスクは、始めた文脈のスコープの子になります。

```csharp
async FlowTask Game()
{
    _ = Flow.Spawn(PlayBgm());   // a child of Game
    await Title();                // a child of Game
    await InGame();               // a child of Game; InGame's own awaits are its children
}
```

`Title` が終わり、`InGame` が `Battle` を await しているときの木は、次のとおりです。終わったスコープ（`Title`）は木から外れます。

```
Game
├─ PlayBgm
└─ InGame
   └─ Battle
```

親が終わると、まだ動いている子孫はすべて巻き戻ります。`Game` が return すれば `PlayBgm` も止まります。どのフローがどこまで生きるかは、コードの形から決まります。

- 寿命を持つのはこの木だけです。ここでいうスコープは FlowTask のスコープで、VContainer の `LifetimeScope` などとは関係ありません。
- `world.Run` で始めたフローは World のルートの子です（[フローと World](flows-and-world.md)）。
- 今の木は `world.Dump()` で見られます（[デバッグ](../tools/debugging.md)）。

## キャンセルは巻き戻し

キャンセルされたスコープでは、止まっている await から `FlowCanceledException` が送出され、`using` と `finally` を走らせながらメソッドを抜けます。これを巻き戻しと呼びます。ゲームの状態を元に戻す（ロールバックする）わけではありません。

```csharp
async FlowTask Hud()
{
    Flow.Own(new InputLock());   // disposed when Hud ends, however it ends
    var view = OpenHud();
    try
    {
        while (true)
        {
            await FlowTask.NextFrame();
            view.Refresh();
        }
    }
    finally
    {
        view.Close();   // runs on a cancel too
    }
}

var hud = world.Run(Hud());
// ...
hud.Cancel();   // Hud unwinds: the finally runs, then the InputLock is disposed
```

スコープが取り消されるのは、次のときです。理由はハンドルの `CancelCause` で分かります。

| きっかけ | `CancelCause` |
|---|---|
| 親が return した（`Flow.Spawn` の子） | `ParentEnded` |
| `FlowTask.Race` で負けた | `RaceLost` |
| `FlowHandle.Cancel()` | `Explicit` |
| 失敗（Race や WhenAll の兄弟の例外、Spawn した子の例外、親が例外で終わった、誤用の検出） | `Fault` |
| `FlowWorld.Dispose()` | `WorldDisposed` |

取り消されたスコープの下のスコープは、そのスコープの理由を引き継ぎます。

### 巻き戻しの順序

巻き戻しは、コールスタックを戻るのと同じ順に進みます。

- 子孫が先で、根に向かいます。スコープに `FlowCanceledException` が届くのは、子がすべて（後始末の await を含めて）終わってからです。
- 兄弟の間では、後から始めたものから巻き戻し始めます。
- 1 つのスコープの中では、`finally` と `using` を内側から外側へ走らせた後、そのスコープの `Flow.AddCleanup` と `Flow.Own` を、登録と逆の順（LIFO）に走らせます。

### いつ巻き戻るか

フローのコードの中で `handle.Cancel()` を呼ぶと、相手はその場で巻き戻り始めます。次の行に進む前に、相手の `finally` が（await を含むなら最初の await まで）走ります。フローの外（ゲームのコード、UI のコールバック）で取り消したときは、次の Tick か Flush の最初に巻き戻ります。それまで `Status` は `Running` のままです。別のスレッドから取り消したときと、自分自身を取り消したときの時期は [実行モデル](../advanced/execution-model.md) にあります。

## 同期の後始末：using、finally、AddCleanup、Own

後始末は、キャンセルでも例外でも普通の return でも走ります。

```csharp
async FlowTask Shop()
{
    var view = Flow.Own(OpenShopView());                      // disposed when Shop ends
    Flow.AddCleanup(() => analytics.Log("shop closed"));      // runs when Shop ends
    using var pause = gameClock.Pause();                      // released at the end of this block
    await RunShop(view);
}
```

- `Flow.Own(x)` は、渡した `IDisposable` をスコープの終わりに Dispose し、`x` をそのまま返します。フローの外で呼ぶと何も所有せず、呼んだ側が Dispose します。
- `Flow.AddCleanup(action)` は、スコープの終わりに走る処理を登録します。フローの外では `FlowMisuseException` になります。クロージャを避けたいときは `Flow.AddCleanup(state, s => …)` を使います。
- `Clock.Pause()`、`Signal.Subscribe`、`FlowBridge.FromCallback` が返すハンドルも、作ったスコープが所有し、スコープの終わりに解放されます。早く解放したいときは `using` を付けます。戻り値を捨てると、スコープが終わるまで生き続けるので、アナライザの FLOW004 が警告します。

## 後始末の await

キャンセルが届いて入った `finally` と `catch` の中の await は、最後まで走ります。閉じるアニメーションや保存を、そのまま await で書けます。

```csharp
async FlowTask Dialog()
{
    var view = Open();
    try
    {
        await FlowTask.Never();      // left only by the cancel
    }
    finally
    {
        await view.PlayClose();      // entered by the cancel: runs to its end
        view.Destroy();
    }
}
```

`await using` の `DisposeAsync` も同じく最後まで走ります。

- 後始末が終わるまで、スコープは `Running` のままです（`CancelCause` で閉じかけと分かり、ダンプの行に `<canceling: Explicit>` のように出ます）。
- 周りはすべて、その終わりを待ちます。親の終了、Race の呼び出し元（負けた枝の後始末が終わってから再開する）、失敗した WhenAll（兄弟の後始末が終わってから例外を伝える）が待ちます。
- 後始末の await が始めたタスクは、後から祖先が取り消されても取り消されません。届くのは `World.Dispose` だけです。
- 後始末の await は、ほかの再開と同じく Pause に従います。ゲームのポーズ中にも終えたい後始末は、UI の Clock で走らせます（[時間と Clock](time-and-clocks.md)）。

> **注意**：後始末の await に時間の上限はありません。終わらない後始末（入力待ち、終わらないループ）は、フローとそれを待つ周りを止めたままにします。上限が要るなら Race にします：`await FlowTask.Race(Save(), FlowTask.WaitForSeconds(2));`（2 秒で終わらなければ `Save` は巻き戻ります）。取り消されたスコープが後始末で倍率のかからない時間（`UnscaledClock`）の 10 秒を過ぎても待ち続けると、`LongCleanup` の警告が World で 1 回出ます（[デバッグ](../tools/debugging.md)）。

Race の呼び出し元を負けた枝の後始末で待たせたくないときは、後始末を `FlowWorld.Current.Run(…)` で別のフローとして始めます。

### キャンセルの前に入った finally：Flow.NonCancelable

`return`、try ブロックの終わり、例外で入った `finally` は、まだ生きているスコープのコードです。ライブラリには、その await が本体のものか `finally` のものかが分かりません。そのため、その `finally` の await の間にキャンセルが来ると、そこで `FlowCanceledException` が投げられ、`finally` の残りは飛び、運んでいた例外もそれに置き換わって消えます。

最後まで走らせたい await は `Flow.NonCancelable` で包みます。

```csharp
try
{
    await ShowResult();
}
finally
{
    // Entered by the return above. An ancestor's cancel does not reach these awaits.
    await Flow.NonCancelable(view.PlayClose());
    await Flow.NonCancelable(FlowTask.Race(Save(), FlowTask.WaitForSeconds(5)));   // gives up after 5 s
    view.Destroy();   // runs after them, even if canceled meanwhile
}
```

- 印の付いた await は、スコープが取り消されていても（その前でも最中でも）タスクを始め、終わるまで待ち、結果か例外を受け取ります。キャンセルは、印のない次の await でスコープに届きます。
- 運んでいた例外は、印の付いた await の後にもう一度投げられます。スコープはキャンセルで終わるので、その例外は `Undelivered` として報告されます（[失敗の扱い](failures.md)）。消えはしません。
- 印は、FlowTask メソッドの中で直接 await したときだけ効きます。`Flow.Spawn`、`FlowWorld.Run`、合成（`Race`、`WhenAll`）に渡すと `FlowMisuseException` になります。上限を付けるなら、上の例のように印の中で Race にします。
- 印の付いた await も Pause に従い、時間の上限はありません。入力待ちやほかのフローの待ちには付けないでください。取り消せなくなります。
- キャンセルが届いた後に入った `finally` と `catch` では、印は要りません（付けても変わりません）。
- `World.Dispose` は、印の付いた await も終わらせます。
- アナライザの FLOW010 が、印のない `finally` の await を知らせます（[アナライザ](../tools/analyzers.md)）。上の `Dialog` のように、キャンセルでしか入らない `finally` でも知らせます。そこでは印を付けても動きは変わらないので、付けるか、抑制します。

### 取り消されたスコープの Spawn

取り消しが確定したスコープでは、`FlowCanceledException` がコードに届くまで、`Flow.Spawn` はタスクを始めず、`Canceled` で終わったハンドルを返します。`Flow.NonCancelable` の await の後に続くコードも、まだキャンセルが届いていないので、ここに入ります。キャンセルが届いた後の `finally` や `catch` の中では始まり、そのスコープが終わるときに巻き戻ります。

## 進捗を残す

巻き戻しは状態を戻しません。キャンセルされても残したい進捗は、スコープより長く生きる場所（`FlowProperty`、セーブデータ）に、進むたびに書きます。

```csharp
async FlowTask Download(FlowProperty<int> done)
{
    for (var i = done.Value; i < chunkCount; i++)
    {
        await FetchChunk(i);
        done.Set(i + 1);   // kept when the download is canceled: the next run starts here
    }
}
```

終わりに一度だけ書けばよいなら、`Flow.AddCleanup` で登録します。

## catch の書き方

`FlowCanceledException` は `OperationCanceledException` の派生です。そのため、キャンセルをまとめて捕まえる `catch` は、FlowTask のキャンセルも捕まえます。キャンセルを受け取った `catch` は、必ず `throw;` で終えます。

```csharp
// OK: everything except the cancellation
catch (Exception e) when (e is not FlowCanceledException) { ShowError(e); }

// OK: cleanup that runs only on a cancel, then let it go on
catch (FlowCanceledException) { Undo(); throw; }

// OK: only an external cancellation (an HttpClient timeout, a canceled Task)
catch (OperationCanceledException e) when (e is not FlowCanceledException) { ShowTimeout(); }

// Wrong: swallows the flow's cancellation (FLOW001, an error)
catch (OperationCanceledException) { return; }
catch (Exception e) { Log(e); }
```

- キャンセルを受け取った後に return したり先へ進んだりすることを、握りつぶしと呼びます。UniTask でよく書く `catch (OperationCanceledException) { return; }` も握りつぶしです。アナライザの FLOW001 がコンパイル時に止めます（[アナライザ](../tools/analyzers.md)）。
- 実行時には、キャンセルを受け取った後に return したスコープを `SwallowedCancellation` として 1 回報告し、キャンセルで終えます。巻き戻しはそのスコープより外へは広がらず、呼び出し元は続きます。
- ループの中で握りつぶす `catch` は、キャンセルの後の await も後始末の await として走るので、回り続けます。return するまで報告もされません。
- `when (e is not OperationCanceledException)` と書くと、外部のキャンセル（Task のタイムアウトなど）もハンドラを素通りし、捕まえなければ未処理の例外になります。外部のキャンセルは、FlowTask のキャンセルではなく例外として届きます（[失敗の扱い](failures.md)）。
- `FlowCanceledException` の `CancellationToken` は `None` です。`when (e.CancellationToken == token)` はどのトークンにも一致しません。
- `FlowCanceledException` を作れるのはライブラリだけです。受け取ったキャンセルは、その `catch` の中で `throw;` で投げ直すだけにします。取っておいて、取り消されていないスコープで投げ直すと、それを InnerException に持つ `FlowMisuseException` でスコープが終わります。

キャンセルされてもやり遂げたい処理は `finally` で await し、途中でやめて切り替えたい処理は `FlowTask.Race` で書きます（[合成](composition.md)）。

## 規則と落とし穴

- 終わったタスクの `Cancel` は何もしません。`Cancel` はどのスレッドからでも呼べるので、外部の `CancellationToken` に `ct.Register(handle.Cancel)` で登録できます（[Task / ValueTask](../integrations/task.md)）。
- 取り消すと、止まっている async メソッド 1 個ごとに `FlowCanceledException` を 1 回投げます。葉の待ち（`Next`、`WaitForSeconds`、`WaitUntil` など）は投げません。毎フレーム負ける Race の枝は、async メソッドではなく葉の待ちにします（[性能](../advanced/performance.md)）。
- デバッガが `FlowCanceledException` のたびに止まるときの設定は [デバッグ](../tools/debugging.md) にあります。
- `World.Dispose` の間の後始末は最初の await までしか走りません。最後まで走らせたい後始末があるときの閉じ方は [失敗の扱い](failures.md) にあります。
- Unity の GameObject や Godot のノードの寿命にフローを結び付けるには、[Unity の寿命](../unity/lifetime.md)、[Godot の寿命](../godot/lifetime.md) を見てください。
