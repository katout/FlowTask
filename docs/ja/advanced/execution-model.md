# 実行モデル

FlowTask のフローがいつ始まり、いつ再開し、どの順に走るかをまとめます。ふだんは [フローと World](../guide/flows-and-world.md) と [スコープとキャンセル](../guide/scopes-and-cancellation.md) の説明で足ります。このページは、同じフレームに複数のことが起きたときの順序を正確に知りたいときに読んでください。

## タスクが始まるとき

FlowTask は遅延実行です。メソッドを呼んだだけでは何も走らず、`await` されたとき、`Flow.Spawn` か `FlowWorld.Run` に渡されたとき、渡した合成が始まったときに始まります（[フローと World](../guide/flows-and-world.md)）。

始まったタスクは、最初に止まる await まで、その場で同期に走ります。

```csharp
world.Run(Intro());
Console.WriteLine("after Run");   // printed after "intro starts"

static async FlowTask Intro()
{
    Console.WriteLine("intro starts");   // runs inside Run
    await FlowTask.NextFrame();
    Console.WriteLine("intro resumes");  // runs in the first Tick after Run
}
```

- `await` が始めるのは、awaiter を作る時点（`GetAwaiter`）です。そのため、コードに書いた `GetAwaiter()` の呼び出しもタスクを始めます。アナライザの FLOW007 がこれを警告します（[アナライザ](../tools/analyzers.md)）。
- awaiter の `IsCompleted` は、作った時点の値のまま変わりません。awaiter を手で使えるのは、`IsCompleted` が true のとき（同期に完了したとき）だけです。
- 1 つの FlowTask を始められるのは 1 回だけです。2 回目の await は `FlowMisuseException` になります。
- `default(FlowTask)` は完了済みのタスク（`FlowTask.CompletedTask`）です。`default(FlowTask<T>)` はタスクではなく、await や合成に渡すと `FlowMisuseException` になります。

### ExecutionContext と AsyncLocal

FlowTask は ExecutionContext を捕まえません。フローは、`Tick`、`Flush`、`Run`、`Dispose` を呼んだ側の ExecutionContext で走ります。

- フローの外で、Tick を呼ぶ前に設定した `AsyncLocal<T>` の値は、どのフローからも見えます。
- フローの中で設定した値は、そのフローのものになりません。同じスレッドで後に走るほかのフローや、Tick の呼び出し元にも見えます。await の後に同じ値が見える保証もありません。
- ブリッジした Task を作る async メソッド（`FlowBridge.FromTask` のファクトリが呼ぶもの）の中では、普通の .NET と同じく、値が await をまたいで引き継がれます。
- フローごとの値（ログの相関 ID など）は、引数で渡します。ログにフローの位置を出すなら `Flow.CurrentScopePath` を使います（[フローと World](../guide/flows-and-world.md)）。
- `ILogger.BeginScope` のように `AsyncLocal<T>` で作るスコープは、await をまたいで開いたままにしません。ほかのフローのログにも付きます。

UniTask のビルダーも ExecutionContext を捕まえません。理由は [設計の背景](design-rationale.md) にあります。

## 1 回の Tick で起きること

`world.Tick(dt)` は、次の順に処理します。

- **取り込み**：別のスレッドからの送信を、World の受信箱から積まれた順に処理します。対象は `EmitFromAnyThread`、`CloseFromAnyThread`、`FlowWorld.Post`、別スレッドからの `Cancel` と `EventSignal.Dispose`、ブリッジした Task の完了、受け取られなかった値の `onDiscard` です。
- **時間を進める**：Clock を、作った順（親が先）に `dt × 実効スケール` だけ進めます。Pause 中の Clock は進めません。
- **時間とフレームの待ちを調べる**：`WaitForSeconds`、`DelayFrames`、`NextFrame`、`WaitUntil` を、待ち始めた順に調べ、満たされたものの再開を予約します。
- **フラッシュ**：再開の予約を、積まれた順（FIFO）に処理します。処理の途中で積まれた予約も、同じフラッシュで処理します。
- **Tick の終わり**：この Tick で終わったフローの `AsTask()` の Task を完了させます。`OnUnhandledException` を設定していなければ、未処理の例外などの報告をここで `FlowUnhandledException` として投げます。

`world.Flush()` は、時間を進めずに、取り込み、フラッシュ、終わりの処理だけを行います。時間とフレームの待ちは調べません。

次の例では、3 つのフローの再開が 1 回の Tick にそろいます。

```csharp
var main = new Signal<string>(world);
var cross = new Signal<string>(world);

world.Run(Delay());
world.Run(Print(cross));
world.Run(Print(main));

// Wait keeps this thread as the World's thread (await would continue on a pool thread)
Task.Run(() => cross.EmitFromAnyThread("from another thread")).Wait();
main.Emit("from the World thread");   // reserved now
world.Tick(1.0 / 60);
// from the World thread
// from another thread
// delay

static async FlowTask Print(Signal<string> s) => Console.WriteLine(await s.Next());

static async FlowTask Delay()
{
    await FlowTask.WaitForSeconds(1.0 / 60);
    Console.WriteLine("delay");
}
```

World のスレッドでの `Emit` は、その場で再開を予約します。別のスレッドからの送信は Tick の最初の取り込みで予約され、時間待ちは時間を進めた後に予約されます。そのため、この順になります。

### 取り込みの順序

- 同じスレッドから送ったものは、種類によらず送った順に処理します。
- ブリッジした Task の完了は、SynchronizationContext に関係なく、完了させたスレッドでその場で受信箱に積みます。
- `Post` に渡した処理は、どのスコープにも属しません。投げた例外は、未処理の例外として報告されます。

別のスレッドとのやり取りは [スレッド](../guide/threads.md) にあります。

### AsTask の完了

`FlowHandle.AsTask()` の Task は、フローが終わった Tick、Flush、Run、Dispose の最後に、World のスレッドで完了します。フラッシュの途中では完了させません。外部のコードをフラッシュの中で走らせず、再開の時期を決めておくためです。

コンテキストを捕まえていない続きは、そこで同期に走ります。その続きの中で、別の `AsTask()` を `.Result` や Tick のループで待つと、完了しません。

## 再開は予約される

完了も `Emit` も、待っているフローをその場では再開しません。再開を予約し、フラッシュで順に処理します。

```csharp
var hit = new Signal<int>();
world.Run(Listen(hit));

hit.Emit(10);
Console.WriteLine("after Emit");   // printed first
world.Flush();                     // "hit 10" is printed here

static async FlowTask Listen(Signal<int> hit)
{
    var damage = await hit.Next();
    Console.WriteLine($"hit {damage}");
}
```

フローの中で `Emit` しても同じです。`Emit` は、その時点で待っているフローを受け手として確定し、再開を予約するだけで、`Emit` した側のコードは次の await まで割り込まれずに続きます。`Emit` の後で待ち始めたフローには、その値は届きません。

待っている側の `Next()` のタスクは、`Emit` から戻った時点でもう終わっていて、`Status` は `Succeeded` です。フラッシュを待つのは再開だけです。そのため、フローの外で値を 1 つの受け手に配る処理は、`Emit` の直後に `Status` を見て、受け取られたかを確かめられます。フローの外で取り消したフローは、巻き戻る前でも受け手になりません（その `Next()` のタスクの `Status` は `Running` のままです）。ただし `Flow.NonCancelable` で包んだ待ちは、取り消された後も値を受け取ります。

予約はキューの末尾に積まれ、積まれた順に処理されます。

```csharp
var a = new Signal<int>();
var b = new Signal<int>();

world.Run(Waiter(a, "a1"));
world.Run(Waiter(a, "a2"));
world.Run(Waiter(b, "b1"));
world.Run(Waiter(a, "a3"));
a.Emit(0);
world.Tick(1.0 / 60);
// a1, a2, a3, b1: a1 emits b while a2 and a3 are already queued

async FlowTask Waiter(Signal<int> s, string name)
{
    await s.Next();
    Console.WriteLine(name);
    if (name == "a1") b.Emit(0);
}
```

- 値の完了も、例外での失敗も、同じキューを通ります。子が終わると、待っている親の再開が予約されます。子から親へは、1 段ずつ予約を通って伝わります。
- 子がすぐに（止まらずに）完了した await は、予約を通らずにそのまま続きます。

## フレームと時間の待ち

### NextFrame と DelayFrames

`NextFrame` と `DelayFrames` は、待ちを始めたフラッシュの中では再開しません。それより後の Tick の「時間とフレームの待ちを調べる」段階で再開します。

```csharp
async FlowTask Steps(Signal<int> sig)
{
    await sig.Next();             // resumed by the flush of Tick 1
    await FlowTask.NextFrame();   // resumes in Tick 2
    await FlowTask.DelayFrames(2);  // resumes in Tick 4
}
```

- フレームは、Clock が Pause していない Tick を 1 回と数えます（`Clock.FrameCount`）。
- `DelayFrames(0)` はその場で完了します。
- `Flush` はフレームを進めないので、何回呼んでも `NextFrame` は再開しません。
- `FlowWorld.Post` の処理の中で始めた待ちは例外です。Post の処理は Tick の最初の取り込みで走り、その後に同じ Tick がフレームを進めて待ちを調べるので、`NextFrame` はその Tick のうちに再開します。

### WaitForSeconds

`WaitForSeconds(秒)` は、Clock の時間が目標に届いた最初の Tick で再開します。引数は秒です（ミリ秒ではありません）。

- `WaitForSeconds(0)` も、その場では完了しません。次に Tick が待ちを調べるときに再開します（dt が 0 の Tick でも再開します）。`Post` の処理の中で始めたときは、`NextFrame` と同じく、その Tick のうちに再開します。
- Clock は時間を double で足していきます。フレームの境界に当たる秒数では、1 フレーム遅れることがあります。たとえば 1/60 を 6 回足すと 0.09999999999999999 なので、60fps で `WaitForSeconds(0.1)` は 7 回目の Tick で再開します。誤差を許して比べると実行順が変わるので、そうしません。
- フレーム数で正確に待つなら `DelayFrames` を使います。

### WaitUntil

`WaitUntil(条件)` は、await した時点で 1 回条件を調べ、満たされていればそのまま続きます。満たされていなければ、以後は Tick ごとに調べます。

- Clock が Pause している間は調べません。
- 条件が投げた例外は、await で投げます。最初の評価でも 2 回目以降でも同じです。

### 同じ Tick に満たされた待ちは、待ち始めた順

1 回の Tick で複数の時間待ちが満たされると、期限の早い順ではなく、待ち始めた順に再開します。

```csharp
var r = await FlowTask.Race(FlowTask.WaitForSeconds(30), FlowTask.WaitForSeconds(20));
// After world.Tick(60), r.Index is 0: both deadlines passed in one Tick,
// and the first branch began to wait first.
```

1 回の大きな dt で 2 つの期限が過ぎると、Race の勝者は、その時間待ちを待ち始めた順で決まります。枝が時間待ちそのものなら、引数の順です。期限の順に並べ替えると、すべてのフローの実行順が変わり、費用も増えるので、そうしません。Unity と Godot の統合は、大きな dt をエンジンの上限で切り詰めて Tick します（[時間と Clock](../guide/time-and-clocks.md)）。

## Race と WhenAll の順序

- `Race` は、最初に完了が**処理された**枝が勝ちます。葉の待ちの枝はフラッシュで値が配られた時点、async メソッドの枝はそのメソッドが終わった時点です。
- 枝は書いた順に始めます。始めた枝が同期に完了したら、残りの枝は始めません。
- 勝者が決まったら、負けた枝を取り消して巻き戻し、その後始末（finally の await を含む）が終わってから、呼び出し元の再開を予約します。
- `WhenAll` は、すべての枝が終わったら呼び出し元の再開を予約します。1 つが例外で終わったら残りを取り消して巻き戻し、その後始末が終わってから例外を伝えます。

同時に完了した枝のどれが勝つかは、処理の順（多くは書いた順）で決まります。割り込みの待ちを先に書く理由と、負けた枝が受け取っていた値の扱いは [合成](../guide/composition.md) にあります。

## キャンセルの時期

キャンセルの要求は、どこから出してもその場で確定します。巻き戻しが走る時期は、要求した場所で違います。

| 要求した場所 | 巻き戻しが走る時期 |
|---|---|
| フローのコードの中（フラッシュ、`Run` の同期の部分、巻き戻しの最中） | その場で |
| フローの外（ゲームのコード、UI のコールバック） | 次の Tick か Flush のフラッシュの先頭。それまで `Status` は `Running` |
| 別のスレッド | 次の Tick か Flush の取り込みで確定し、同じフラッシュの先頭で |
| 実行中のスコープが自分か祖先を取り消した | そのスコープの次の await で |

フローの外で取り消したフローは、すでに再開が予約されていても再開せず、フラッシュの先頭で巻き戻ります。

```csharp
var victim = world.Run(Victim(sig));
world.Run(Other(sig));
sig.Emit(0);       // both resumes are reserved
victim.Cancel();   // confirmed now; victim.Status is still Running
world.Tick(1.0 / 60);
// victim finally   <- unwound at the head of the flush; "victim resumed" is never printed
// other resumed
```

巻き戻しは、子孫が先で根に向かいます。兄弟の間では後から始めたものから巻き戻し始め、1 つのスコープの中では finally を内側から外側へ走らせた後、`Flow.AddCleanup` と `Flow.Own` を登録と逆の順に走らせます。後始末の await の扱いを含めて、詳しくは [スコープとキャンセル](../guide/scopes-and-cancellation.md) にあります。

## Pause と再開

- Pause 中の Clock に属するスコープの再開は、保留します。Pause が解けたら、保留した再開を元の順のまま、キューの先頭に戻します。その時点でキューにある予約より先に処理されます。
- 巻き戻しの始まり（await での `FlowCanceledException`）は保留しません。Pause 中でもキャンセルは届きます。
- 後始末の中の await の再開は、ほかの再開と同じく保留します。Pause 中にも終えたい後始末は、UI の Clock で走らせます（[時間と Clock](../guide/time-and-clocks.md)）。

## 上限

World には、ゲームが固まるのを防ぐ上限が 2 つあります。どちらも定数で、設定はありません。

- **1 回のフラッシュで処理する再開は 65,536 まで**。超えた分は次のフラッシュに送り、`FlowWarningKind.FlushLimit` を World で 1 回だけ警告します。互いに Emit し合って止まらない 2 つのフローなどで当たります。
- **1 つのスコープが 1 回の Tick、Flush、Run（外から呼んだもの）の中で同期に完了させる await は 1,000,000 まで**。超えたら無限ループとみなし、その await でスコープを終えます。続きは走りません。待ち手は `FlowMisuseException` を受け取ります。

同期完了の上限に数えるのは、始めたタスクが止まらずに完了した await です。完了済みの値（`FlowTask.FromResult`、`FlowTask.CompletedTask`）の await は数えずに、その場で続けます。Task や UniTask と同じです。そのため、完了済みの値だけを await するループは、それを走らせた Tick、Flush、Run から戻りません。

```csharp
// Never returns from the Tick that runs it
while (!ready) await FlowTask.CompletedTask;

// Checks once per frame
while (!ready) await FlowTask.NextFrame();
```

条件を待つループには `NextFrame` を挟むか、`FlowTask.WaitUntil` を使います。

## World の実行中に呼べないもの

World がフローのコード、後始末、`OnUnhandledException` と `OnWarning` のハンドラ、受信箱の処理を走らせている間は、`Tick`、`Flush`、`Dispose` を呼べません（`FlowMisuseException`）。ハンドラは、スケジューラの途中で呼ばれます。ハンドラの中ではログや集計だけを行い、フローの開始やキャンセルはしません。エラー画面を出すようにフローで応じるなら、`world.Post(() => world.Run(ShowError()))` で次の取り込みに回すか、それを待っているフローに Signal で知らせます。ハンドラが投げても、スケジューラとほかのハンドラは続きます。

## 決定論の範囲

同じ dt の列と同じ入力（World のスレッドからの呼び出しと、受信箱への送信）を同じ順で与えれば、フローの実行順は毎回同じになります。.NET、Unity、Godot で同じ基準のログと照合するテストがあります。

FlowTask 自身は、Clock の時間を double の足し算・掛け算と比べ算だけで計算し、CPU によって結果の末尾が変わりうる関数（三角関数、指数、対数、累乗、推定値の命令、融合積和）を使いません（テストで確かめています）。

この決定論は、テストと不具合の再現のためのものです。

- 別のスレッドから受信箱に届く順は、スレッドの競合で決まります。
- フローの状態を保存して戻す手段はありません。そのため、状態を巻き戻して再計算するロールバック型の同期には使えません。
- 端末をまたいで同じ結果をそろえる用途（ロックステップの同期）は、この決定論の範囲の外です。フローの実行順の規則は同じでも、浮動小数点の計算など、利用者のコードが同じ結果になるかは FlowTask が保証しません。

テストで決まった dt を与えて World を進める方法は [テスト](../tools/testing.md) にあります。
