# 合成：Race と WhenAll

複数のフローを組み合わせる方法を説明します。直列は await、「どれか 1 つ」は `FlowTask.Race`、「すべて」は `FlowTask.WhenAll` で書きます。タイムアウトや、ボタンによる読み込みや通信の中断も Race で書きます。

## 直列は await

```csharp
async FlowTask Stage()
{
    await Intro();
    await Battle();
    await Outro();
}
```

順に待つだけなら、合成の API は要りません。

## Race：最初に終わったものを取り、残りを止める

`FlowTask.Race` は、枝のうち最初に終わったものを勝者にし、ほかの枝を取り消して巻き戻してから、呼び出し元を再開します。

```csharp
async FlowTask<bool> Confirm()
{
    var r = await FlowTask.Race(okButton.Next(), cancelButton.Next(), FlowTask.WaitForSeconds(10));
    return r.Index == 0;   // OK pressed; Cancel or 10 s means no
}
```

- 2〜3 枝の Race は、枝ごとの型を持つ `RaceResult<T0, T1>`（3 枝なら `RaceResult<T0, T1, T2>`）を返します。`Index` は勝った枝の番号（0 始まり）、`TryGet0(out v)` などは勝ったときにその値を取り出し、`Value0` などは勝っていなければ例外を投げます。値を返さない枝（`FlowTask`）の値は `FlowUnit` です。
- 4 枝以上は配列などの `IReadOnlyList` で渡します。`FlowTask` のリストなら勝者の番号（`FlowTask<int>`）を、`FlowTask<T>` のリストなら `RaceResult<T>`（`Index` と `Value`）を返します。
- 各枝は合成の下の子スコープで走ります。

```csharp
var r = await FlowTask.Race(hits.Next(), Patrol(self));
if (r.TryGet0(out var hit)) await HitStun(self, hit);
```

### 負けた枝は巻き戻ってから再開する

負けた枝は、キャンセルと同じく巻き戻ります（`CancelCause.RaceLost`）。`using` と `finally` が走り、`finally` の await も最後まで走ります。呼び出し元が再開するのは、負けた枝の後始末がすべて終わってからです。

```csharp
async FlowTask Patrol(Enemy self)
{
    var effect = self.ShowAlert();
    try
    {
        while (true) await self.WalkTo(NextPoint());
    }
    finally
    {
        await effect.FadeOut();   // the caller of the Race resumes after this
    }
}
```

後始末を待たずに次へ進みたいなら、その後始末を `FlowWorld.Current.Run(…)` で別のフローとして始めます。後始末の await の規則は [スコープとキャンセル](scopes-and-cancellation.md) にあります。

### 枝は書いた順に始まる

枝は引数の順に、1 つずつ最初に止まる await まで走ります。始めた枝がその場で（同期に）終わったら、その枝が勝ち、残りの枝は始まりません。

同じフレームに複数の枝が終わったときは、処理された順で勝者が決まり、多くは書いた順になります（1 回の Tick で 2 つの期限が過ぎたときは、期限の早さではなく、その時間待ちを待ち始めた順です。枝が時間待ちそのものなら、書いた順になります。[実行モデル](../advanced/execution-model.md)）。そのため、割り込み（イベントの待ち）を先に書き、それで中断される処理を後に書きます。

```csharp
// Good: a hit already buffered in the subscription wins before Patrol moves
var r = await FlowTask.Race(hits.Next(), Patrol(self));

// Worse: Patrol starts first and takes one step even if a hit is waiting
var r = await FlowTask.Race(Patrol(self), hits.Next());
```

### 負けた枝が受け取っていた値

購読（`Subscription<T>`）の `Next()` が Race に負けたとき、その待ちがすでに受け取っていた値は購読の先頭に戻ります。同じフレームに届いた被弾は、次の `hits.Next()` で取れます。失敗した WhenAll の枝の待ちも同じです。

- 戻した値は購読のあふれの方針に従います。満杯なら、`DropOldest` と `Latest` は戻した値を捨て、`DropNewest` は最新の値を捨てます。`Fail` は容量を超えて保持し、あふれとしては扱いません。
- エッジの `Signal.Next()` が負けたときに受け取っていた値は消えます。取りこぼしたくない入力は購読で待ちます（[シグナル](signals.md)）。
- `WithoutResult()` で包んだ `Next()` も、負けたときは同じく値が戻ります。
- 戻るのは、合成が結果に取り込む前の値だけです。内側の Race が値を自分の結果に取り込んだ後に、その結果が外側の Race に負けて捨てられても、値は戻りません。
- ブリッジの値の扱い（`onDiscard`）は [Task / ValueTask](../integrations/task.md) にあります。

## タイムアウト

タイムアウトの専用の API はありません。処理と `WaitForSeconds` を Race します。

```csharp
var r = await FlowTask.Race(Download(), FlowTask.WaitForSeconds(10));
if (!r.TryGet0(out var data))
{
    ShowTimeout();   // Download was unwound before this line
    return;
}
```

時間はスコープの Clock で進むので、ゲームのポーズ中はタイムアウトも止まります。止めたくないなら、UI の Clock や `world.UnscaledClock` を渡します：`FlowTask.WaitForSeconds(10, world.UnscaledClock)`（[時間と Clock](time-and-clocks.md)）。

どの Clock も `Tick` に渡した時間で進むので、実時間とは限りません（長いフレームは切り詰められ、アプリが止まっている間は進みません）。通信の上限のように実時間で計りたいものは、外部の側の仕組み（`HttpClient.Timeout`、`CancellationTokenSource.CancelAfter`、Unity の `UnityWebRequest.timeout`）で付けます。ゲームの時間で計るのは、ゲームの都合の上限（10 秒で答えなければ No など）です。

フローの終わりを上限付きで待つなら、`Join` も Race できます。

```csharp
var r = await FlowTask.Race(handle.Join(), FlowTask.WaitForSeconds(5));
```

## 読み込みや通信をボタンで止める

ブリッジした外部の処理も、Race の枝にできます。負けたブリッジのトークンは取り消されます。

```csharp
var r = await FlowTask.Race(
    cancelButton.Next(),
    FlowBridge.FromTask(ct => File.ReadAllBytesAsync(path, ct)).ToFlowTask());   // the read stops when the button wins
if (!r.TryGet1(out var bytes)) return;
```

- 結果のあるブリッジは、`.ToFlowTask()` を付けて渡します（付けないと型引数を推論できません）。
- トークンを取らない処理（Addressables の読み込みなど）は止まらずに最後まで走ります。届いた結果を解放するなら `onDiscard` を渡します（[Task / ValueTask](../integrations/task.md)、[Unity のブリッジ](../unity/bridges.md)）。
- 画面を閉じるなどで Race を含むフローが終わったときも、同じくトークンが取り消されます。

## 負けた枝を止めたくないとき

Race の負けた枝は必ず止まります。止めずに「最初に終わったもの」だけを知りたいなら、処理を `Flow.Spawn` で始めて、`Join` を Race します。巻き戻るのは負けた `Join` の待ちだけで、処理そのものは続きます。

```csharp
var a = Flow.Spawn(LoadA());
var b = Flow.Spawn(LoadB());
var first = await FlowTask.Race(a.Join(), b.Join());   // the other load goes on
```

Spawn した処理は、呼んだスコープが終わると巻き戻ります。スコープより長く続けるなら `FlowWorld.Run` で始めます（[フローと World](flows-and-world.md)）。

> **注意**：1 つのハンドルを Join できるのは 1 回だけで、Race に負けた Join も 1 回に数えます。負けたほうの処理の終わりを後で待つなら、`FlowTask.WaitUntil(b, h => h.IsCompleted)` で待ち、`b.Result` を読みます。

## WhenAll：すべてを待つ

`FlowTask.WhenAll` は、すべての枝が終わるまで待ちます。

```csharp
var (map, units) = await FlowTask.WhenAll(LoadMap(), LoadUnits());
await FlowTask.WhenAll(FadeOutBgm(), FadeOutScreen());   // branches without values
```

- 2〜3 枝は、値をタプルで返します（`Item1` から始まる 1 始まり）。値を返さない枝の値は `FlowUnit` です。すべての枝が値を返さないなら、WhenAll も値を返しません。
- 4 枝以上は `IReadOnlyList` で渡し、`FlowTask<T>` のリストなら値の配列（`T[]`）を返します。

1 つの枝が例外で終わると、残りの枝を取り消して巻き戻し（`CancelCause.Fault`）、その後始末が終わってから await で例外を投げます。Task の `WhenAll` のように残りを走らせ続けたり、例外をまとめたりはしません。2 つ目以降の例外は `Undelivered` として報告されます（[失敗の扱い](failures.md)）。

読み込みの失敗のような想定内の失敗で残りを止めるときも、失敗を専用の例外型で投げ、WhenAll を囲む `catch` でその型を受けます（[失敗の扱い](failures.md) の「想定内の失敗」）。1 つが失敗しても残りを最後まで走らせ、枝ごとの成否を集めたいなら、各枝の中で例外を捕まえて、成否を値で返します。

## 値を捨てる：WithoutResult

型の違う 4 つ以上の枝を `FlowTask` のリストにまとめたいときなど、値が要らない枝は `WithoutResult()` で値のない `FlowTask` にします。

```csharp
var winner = await FlowTask.Race(new[]
{
    shopClicks.Next().WithoutResult(),   // a Signal<FlowUnit>
    itemPicked.Next().WithoutResult(),   // a Signal<Item>: its value is dropped
    closed.Next().WithoutResult(),
    FlowTask.WaitForSeconds(30),
});
if (winner == 0) await OpenShop();
```

`WithoutResult()` で包んだ待ちが値を受け取ると（Race の枝なら、その枝が勝つと）、値はフローが受け取ったものとして扱われ、購読へは戻りません（ブリッジの値は `onDiscard` に渡ります。[Task / ValueTask](../integrations/task.md)）。

## 規則と落とし穴

- 合成を作っただけでは、枝は始まりません。合成を await するか、`Flow.Spawn`、`FlowWorld.Run` に渡したときに始まります。
- 1 つのタスクを渡せる合成は 1 つだけです。
  - すでに始めたタスク（await したもの）を渡すと、合成を作る呼び出しが `FlowMisuseException` を投げます。
  - まだ始めていないタスクを 2 つの合成に渡すと、作る時点では分かりません。後から始めた合成の await が `FlowMisuseException` を投げます。タスクは先に始めた合成のもので、その合成はそのまま続きます。
  - 同じ合成に同じタスクを 2 回渡すと、その合成の await が `FlowMisuseException` を投げ、始めた枝は巻き戻されます。ただし、先の枝がすぐに終わると合成がそこで決着するので、誤用に気づかないまま進むことがあります。
- `Flow.NonCancelable` で包んだタスクは合成に渡せません（`FlowMisuseException`）。
- 枝の開始が失敗したら（使えない Clock の待ちなど）、合成はそれまでに始めた枝を巻き戻し、await で例外を投げます。
- 毎フレーム決着する Race（毎フレームの入力と処理の Race など）では、負ける枝を async メソッドではなく葉の待ち（`NextFrame`、`WaitForSeconds`、`Next`、`WaitUntil`）にします。async メソッドを負けさせると、毎回巻き戻しの例外の費用がかかります（[性能](../advanced/performance.md)）。被弾や画面遷移のようにたまに起きる決着なら問題ありません。
- `WhenAny` と `Timeout` に当たる API はありません。どちらも Race で書きます（タイムアウトは、処理と `FlowTask.WaitForSeconds` の Race です）。
