# デバッグと診断

フローが止まったまま動かない、例外がどこから来たのか分からない、といったときに使う機能を説明します。スコープツリーのダンプ、診断の API、警告と例外の報告の読み方、よくある例外の文面、IDE の設定を扱います。

診断の機能に Debug ビルドだけのものはなく、どのビルドでも同じに動きます。診断の API（`world.Diagnostics` と、名前空間 `Katout.FlowTask.Diagnostics` の型）はツールとテストのためのもので、ゲームのロジックからは使いません。ゲームの状態は `FlowProperty` や自分のオブジェクトで表します。

## スコープツリーのダンプ

```csharp
Console.WriteLine(world.Dump());
```

```
FlowWorld [Default]
├─ Game (scope) [Default] waiting: InGame for 12.4s
│  └─ InGame (scope) [Default] waiting: Race at InGame.cs:18 for 12.4s
│     └─ Race (combinator) [Default] waiting: Race 2/2 branches at InGame.cs:18
│        ├─ Stages (scope) [Default] waiting: WaitForSeconds(1s) on Default, 0.35s left at Stages.cs:9 for 0.65s
│        └─ Confirm.Next (wait) [Default] waiting: Confirm.Next at InGame.cs:18
└─ Menu (scope) [UI] waiting: Never at Menu.cs:5 for 3s
   └─ Never (wait) [UI] waiting: Never at Menu.cs:5
```

各行には、名前、ノードの種類、Clock、待っているもの、それを作った場所、待っている時間が並びます。

- 名前は、既定ではメソッド名です。ルートの行は World の名前（`new FlowWorld("Name")`。既定は `FlowWorld`）です。
- 種類は `scope`（FlowTask メソッド）、`combinator`（Race や WhenAll）、`wait`（シグナルの待ちなど）のどれかです。
- スコープが直接待つ `NextFrame`、`DelayFrames`、`WaitForSeconds` は子の行にならず、上の `Stages` のようにスコープの行に出ます。Clock を引数で渡したものと、`Flow.Named`、`Flow.WithClock`、`Flow.NonCancelable` を付けたものは、`wait` の子の行になります。
- `at InGame.cs:18` は、待ちや合成を作った場所（ファイル名と行）です。スコープの行には、直接待っているものの場所が出ます。FlowTask メソッドの呼び出しを待つスコープ（上の `Game`）には出ません（「診断の API」の「待ちを作った場所」）。
- 待っている時間（`for 12.4s`）はスコープの行にだけ出ます。最後に止まってからの `UnscaledClock` の秒数です。
- 合成の行には、生きている枝の数が出ます（`Race 2/2 branches`）。決着した後に枝の後始末を待っているときは、`Race decided, 1 branch still ending` のように、決着のしかた（`decided`、`failed`、`canceled`）とまだ終わっていない枝の数が出ます。
- ブリッジしていない外部の await はダンプに出ません。止まった時点でスコープが例外で終わるためです。
- 64 段より深い行は、それ以上字下げせず、行の先頭に `[depth 65]` のように深さを付けます。

行に付く印は次のとおりです（`Game` と `Fade` は Clock の名前です）。

| 印 | 意味 |
|---|---|
| `[Game, paused]` | その Clock（か祖先の Clock）が Pause されている。誰が止めているかは、先頭の `Paused clocks:` で見る |
| `[Fade, removed]` | `Flow.CreateClock` の Clock が、作ったスコープの終わりで削除された。その Clock の次の待ちは `FlowMisuseException` になる |
| `<canceling: Explicit>` | キャンセルが確定し、後始末（`catch` と `finally` の await を含む）を走らせている最中。`Explicit` は `CancelCause` で、その間スコープは Running のまま |

止まっている Clock があると、ダンプの先頭に `Paused clocks:` が出ます。

```
Paused clocks:
  Game: paused x2 by Main > PauseMenu, <outside any flow>
  Battle: paused via Game
```

Clock ごとに Pause の数と持ち主（Pause したスコープのパス）が出ます。親の Clock の Pause で止まっていれば `paused via 親` と出ます。フローの外で取った Pause は持ち主がなく（`<outside any flow>`）、`Pause()` が返したハンドルを Dispose するまで残ります。ゲームが止まったまま動かないときは、まずここを見てください。

### ダンプを読みやすくする

- Signal に名前を付けます。`new Signal<int>(world, "Hits")` の待ちは `Hits.Next`、購読の待ちは `Hits subscription.Next` と出ます。
- 同じメソッドを何体も動かすなら、`Flow.Named($"Enemy#{id}", EnemyAI(e))` で個体を区別します。

### エディタで見る

- **Unity**：`Window > FlowTask > Scope Tree` が、再生中のスコープツリーと Clock を表示します。行をダブルクリックすると、待ちを作った行をスクリプトで開きます。場所のないスコープの行（FlowTask メソッドの呼び出しを待つもの）は、そのメソッドを開きます。右クリックのメニューからは、どちらも開けます。既定の World のほかに、`FlowWorldRegistry.Register(world)` で登録した World も見られます（[Unity のブリッジとツール](../unity/bridges.md)）。
- **Godot**：`GD.Print(FlowWorldNode.Default.Dump())` をデバッグ用のキーに割り当てます（[Godot のセットアップ](../godot/setup.md)）。

## 止まったフローを探す

ダンプで、待っている時間が伸び続けるスコープを探します。コードから探すなら、`Diagnostics.Walk()` の `WaitingSeconds` を見ます。

```csharp
var stuck = world.Diagnostics.Walk()
    .Where(s => s.Kind == FlowScopeKind.Scope && s.WaitingSeconds > 10)
    .Select(s => $"{s.Path}: {s.Waiting}");
```

- 止まった子孫を待つ祖先も長く待っているので、いちばん深いスコープから見ます。
- 時間はスコープにだけ出ます。`world.Run(signal.Next())` のように待ちを直接始めたものは、ルートの子（`Diagnostics.Root.Children`）の `Waiting` で見ます。
- `<canceling: …>` のまま残るスコープは、終わらない後始末です。後始末の await に時間の上限はありません。上限が要るなら `FlowTask.Race(x, FlowTask.WaitForSeconds(n))` と書きます（[スコープとキャンセル](../guide/scopes-and-cancellation.md)）。
- 毎フレーム止まり直すポーリング（`while (!ready) await FlowTask.NextFrame();`）は、待っている時間が伸びません。条件を待つなら `await FlowTask.WaitUntil(() => ready)` と書くと、止まったときに見つけられます。
- 閉じたシグナルの `Next()` は `SignalClosedException` を投げるので、そこで待ち続けることはありません。
- フローどうしが互いに `Join` で待つ形や、自分か祖先の `AsTask()` をブリッジで待つ形は検出されません。Task と同じく待ち続けるので、ダンプで探します。

## 診断の API

`world.Diagnostics` は、次のメンバーを持ちます。

| メンバー | 内容 |
|---|---|
| `Root` | ルートの `FlowScopeInfo` |
| `Walk()` | ルートから始めた、全ノード（スコープ、合成、待ち）の深さ優先の列挙 |

`FlowScopeInfo` は、`Name`、`Kind`（`FlowScopeKind` の `Root`、`Scope`、`Combinator`、`Wait`。ノードが解放された後は `Invalid`）、`Status`、`ClockName`、`Waiting`、`WaitingFile`、`WaitingLine`、`WaitingSeconds`、`Path`、`Cause`、`IsCanceling`、`Children` を持ちます。ノードが終わって解放されると、`IsValid` が false になります。

- `DeclaringType` と `MethodName` は、FlowTask メソッドのスコープのソースの場所です。ラムダは囲むメソッドの名前になり、`Flow.Named` の影響は受けず、行番号はありません。待っている行は `WaitingFile` と `WaitingLine` で見ます。
- フローの中では、`Flow.CurrentScopePath` で今のスコープのパスを取れます。ログに添えると便利です。
- `world.Clocks` は生きている Clock の一覧です。これが増え続けるなら、長生きするスコープのループの中で `Flow.CreateClock` を呼んでいます。子の FlowTask メソッドの中で作ってください。

### 待ちを作った場所

`WaitingFile` と `WaitingLine` は、待っているものを作った場所です。待ちと合成のノードは自分を作った場所、スコープは直接待っているもの（ノードのない `NextFrame`、`DelayFrames`、`WaitForSeconds` を含む）の場所を返します。

- 場所を記録するのは、`FlowTask.WaitForSeconds`、`DelayFrames`、`NextFrame`、`WaitUntil`、`Never`、`Race`、`WhenAll`、`Signal<T>`・`EventSignal<T>`・`Subscription<T>` の `Next` と `NextOrClosed`、`FlowProperty<T>.WaitUntil`、`Once<T>.Wait`、`FlowHandle.Join` です。末尾の省略できる引数 `callerFilePath` と `callerLineNumber` に、コンパイラが呼び出し元のファイルと行（`[CallerFilePath]`、`[CallerLineNumber]`）を入れます。
- 記録するのは、待ちを作った場所です。`var t = FlowTask.WaitForSeconds(1);` と作って後で `await t;` すると、作った行が出ます。`WithoutResult()` は、包んだタスクの場所を出します。
- 場所がないときは、`WaitingFile` が null、`WaitingLine` が 0 です。FlowTask メソッドの呼び出しを待つスコープ（呼び出しは場所を記録しません。その子のスコープの行に、子が待つ場所が出ます）、`FlowBridge` でブリッジした Task、`Once<T>` を直接 `await` したもの（`once.Wait()` なら出ます）、Unity の `WaitForDestroy()` が返す待ち（パッケージの中で作るため）、場所に空の文字列を渡したもの（行を渡しても 0 になります）が、これに当たります。
- `WaitingFile` は、コンパイラが渡したパスそのままです（ふつうはビルドしたマシンでの絶対パス。`PathMap` の設定で変わります）。ダンプにはファイル名だけが出ます。

待ちを作る関数をゲームの側で包むなら、自分の呼び出し元の場所をそのまま渡します。渡さないと、包んだ関数の中の行が出ます。

```csharp
public static FlowTask Frames(int count,
    [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) =>
    FlowTask.DelayFrames(count, null, callerFilePath, callerLineNumber);
```

## 警告

```csharp
world.OnWarning += w => Console.Error.WriteLine(w);   // [PausedOwnClock] ... at Main > PauseMenu
```

警告は 4 種類です。どのビルドでも、種類ごとに World で 1 回だけ届きます。Unity と Godot の統合は、エンジンのログに出します。

| `FlowWarningKind` | 起きたこと | 直し方 |
|---|---|---|
| `FlushLimit` | 1 回のフラッシュが上限（65,536 回の再開）に達し、残りを次のフラッシュに回した | 再開が増え続ける原因（互いを再開し続けるフローなど）を探す |
| `PausedOwnClock` | フローが、自分の動いている Clock（か祖先）を Pause した。自分の再開も止まる | ダイアログやメニューを `Flow.WithClock(ui, …)` で別の Clock で動かす（[時間と Clock](../guide/time-and-clocks.md)） |
| `LongCleanup` | 取り消されたスコープが、後始末（キャンセルの後の `catch` と `finally` の await、`Flow.NonCancelable` の await）で `UnscaledClock` の 10 秒待ち続けている。フローとそれを待つスコープは Running のまま | 終わらない後始末（ループ、取り消された兄弟のシグナルの待ち、`Flow.NonCancelable` を付けた入力の待ち）を探し、`FlowTask.Race` で上限を付ける。Clock が止まっているなら文面にそう出る |
| `CleanupCutAtDispose` | `World.Dispose` が、後始末の await か `Flow.NonCancelable` の await を切った。そのブロックの残りは走っていない | Dispose の前に取り消して Tick を回す（[失敗の扱い](../guide/failures.md)） |

- 親より長く動かすつもりの `Flow.Spawn` の子が親の終了で切られても、実行時には警告されません（ハンドルは Canceled で、原因は `ParentEnded`）。ハンドルを使わない `Flow.Spawn(x);` の文は、[FLOW008](analyzers.md) がコンパイル時に知らせます。
- `OnWarning` と `OnUnhandledException` のハンドラは、スケジューラの途中（スコープを終える処理や巻き戻しの最中）で呼ばれます。そこでフローを始めたり取り消したりすると、その処理の途中に割り込んで走るので、ハンドラはログや集計にとどめます。エラー画面を出すようにフローで応じるなら、`world.Post(() => world.Run(ShowError()))` で次の Tick か Flush の取り込みに回すか、それを待っているフローに Signal で知らせます。ハンドラから Tick、Flush、World の Dispose は呼べません（`FlowMisuseException`）。
- ハンドラが投げても、スケジューラは止まりません。`OnWarning` のハンドラの例外は、ScopePath が `<FlowWorld.OnWarning handler>` の未処理の例外として `OnUnhandledException` に報告されます。`OnUnhandledException` のハンドラの例外は、渡された報告と一緒に、いちばん外側の Tick、Flush、Run、Dispose から `FlowUnhandledException` として投げられます。

## 例外の報告を読む

```csharp
world.OnUnhandledException = info => Console.Error.WriteLine(info);   // [Unhandled] at Game > InGame > Battle: System.IO.IOException: ...
```

`OnUnhandledException` に届く `FlowExceptionInfo` は、例外（`Exception`）、例外を投げた時点のスコープのパス（`ScopePath`。例：`Game > InGame > Battle`）、報告の種類（`Kind`）を持ちます。

| `FlowExceptionKind` | 何か |
|---|---|
| `Unhandled` | どこでも捕まえられなかった例外。`FlowWorld.Run` で始めたフローを終えた例外と、`Post` やハンドラなどフローの外のコードの例外 |
| `Cleanup` | 後始末の例外（キャンセルが確定したスコープのコード、`AddCleanup`、`Own` の Dispose、`onDiscard`）。後始末は続く |
| `SwallowedCancellation` | キャンセルされたスコープが、`FlowCanceledException` を受け取った後に return した（`catch` が握りつぶした）。スコープはキャンセルで終わる |
| `Undelivered` | 受け手に届けられなかった例外（取り消された待ち手、決着済みの Race、2 つ目以降の例外、所有者が終わりかけているときに Spawn した子が投げた例外、ブリッジが待つのをやめた後に失敗した Task） |

- `Undelivered` は、ふつうのプレイ中にも起きます（同じフレームに失敗した 2 つの通信など）。Unity と Godot の統合は、これを警告として、ほかの種類をエラーとしてログに出します。
- 報告は見つけた順にすぐ届きます。そのため、後始末の失敗（`Cleanup`）が、それを引き起こした例外より先に届くことがあります。
- `FlowHandle.Exception` は、フローを終えた例外です。フローが Faulted で終わったときだけ値を持ちます。例外を投げた場所のパスと報告の種類は `OnUnhandledException` で見ます。
- `OnUnhandledException` を設定していない World では、報告はどの種類も、いちばん外側の Tick、Flush、Run、Dispose から、まとめて `FlowUnhandledException`（`ExceptionInfos` に一覧）として投げられます。

await で投げ直した例外のスタックトレースには、最初に投げた場所と `catch` した場所だけが入り、途中の await の段は入りません。途中は `ScopePath` で見ます。

失敗の経路の全体は [失敗の扱い](../guide/failures.md)にあります。

## よくある例外の文面

ライブラリが見つけた誤用は、スコープの普通の例外として待ち手の await で投げられ、`catch` で受けられます。

| 文面（抜粋） | 意味 | 直し方 |
|---|---|---|
| `FlowTask method '…' suspended on TaskAwaiter<Int32>, which is not a FlowTask` | FlowTask メソッドが、ブリッジしていない外部の awaitable で止まった。スコープはその場で終わり、残りの `finally` と `using` は走らない（`AddCleanup` と `Own` は走る） | `FlowBridge.FromTask(ct => …)` か `.AsFlow()` でブリッジする（[FLOW002](analyzers.md)） |
| `Scope '…' returned after FlowCanceledException reached its code` | `catch` がキャンセルを受け取って return した（`SwallowedCancellation` の報告） | `throw;` で終えるか、`when (e is not FlowCanceledException)` を付ける（[FLOW001](analyzers.md)） |
| `Scope '…' completed 1000000 awaits synchronously in one Tick, Flush or Run` | 同期に完了し続ける await のループを無限ループとみなし、その await でスコープを終えた | 待つループに `await FlowTask.NextFrame()` を挟む |
| `A method that is not a FlowTask method (async Task, ValueTask, UniTask or async void) awaited a FlowTask in scope '…', and the await did not complete at once` | FlowTask メソッドでない async メソッドが FlowTask を await して止まった。そのメソッドは再開せず、その Task は完了しない。そのコードを動かしていたスコープは終わる | メソッドが FlowTask を返すようにする（[FLOW005](analyzers.md)） |
| `A FlowTask can only be awaited inside a FlowTask method running in a World` | フローの外で FlowTask を await した | `FlowWorld.Run` で始める。Task のコードで結果を待つなら `world.Run(task).AsTask()`（[FLOW005](analyzers.md)） |
| `Clock '…' was removed when its scope '…' ended (Flow.CreateClock)` | `Flow.CreateClock` の Clock を、作ったスコープが終わった後に使った（待つ、その Clock で動かす、Pause、TimeScale） | スコープより長く使う Clock は `FlowWorld.CreateClock` で作る |
| `… is bound to FlowWorld(A), the first World that used it, which is disposed` | Dispose した World で使った Signal などを、別の World で使った（Unity でドメインリロードを切り、static に置いたときなど） | Signal、FlowProperty、Once は World ごとに作って渡す |
| `… is bound to FlowWorld(A) on thread N and cannot be used by FlowWorld(B) on thread M`、`… was called from thread N, but the object belongs to thread M` | 束縛したのとは別のスレッドで使った（最初に使った World のスレッドに束縛される） | 別のスレッドからは `EmitFromAnyThread`、`CloseFromAnyThread`、`FlowWorld.Post` を使う（[スレッド](../guide/threads.md)） |
| `EmitFromAnyThread was called on a signal that no World uses yet` | まだどの World も使っていない Signal に `EmitFromAnyThread` で送った。値を積む先の World がない | `new Signal<T>(world)` で作るか、先に World のスレッドで使う（フローの中で作る、フローの中で `Next` を待つか `Subscribe` する） |
| `… subscription.Next: this subscription has already ended` | 終わった購読（コピー経由の Dispose、所有スコープの終了）で `Next()` を待った | 購読は作った場所で 1 回だけ Dispose する |
| `The subscription to '…' overflowed: its queue (capacity 1) was full …` | `BufferOverflow.Fail` の購読があふれた | 容量を上げるか、`DropOldest` か `DropNewest` にする |
| `Join of '…' from inside it can never complete` | 自分自身か祖先の Join | フローの外から Join する |
| `No FlowTask World: …` | Unity で既定の World がない（Edit Mode、再生中の再コンパイル、`Shutdown` の後、自動の導入を切ったとき） | 文面の続きの指示に従う。再コンパイルなら再生をやり直す（[Unity のセットアップ](../unity/setup.md)） |

完了済みの値（`FlowTask.FromResult`、`FlowTask.CompletedTask`）の await は、同期の完了の数に入りません。それだけを await するループは、例外にならずに Tick、Flush、Run から戻らなくなります。

## ハンドルの状態

`FlowHandle` の `Status` は、フローの今の状態です。

| `FlowStatus` | 意味 |
|---|---|
| `Running` | 始まって、まだ終わっていない。キャンセルされても、後始末（`finally` の await など）が終わるまでは Running のまま |
| `Succeeded` | 自分のコードが return した（待ちなら、待ちが完了した）。キャンセルされていない |
| `Canceled` | キャンセルで終わった（`CancelCause` が理由）。キャンセルの後に届いた例外は報告されるだけで、状態は Canceled のまま |
| `Faulted` | 例外で終わった（自分のコードから出た例外、Spawn した子の例外、ライブラリが見つけた誤用）。`FlowHandle.Exception` が値を持つ |

`CancelCause` は、キャンセルが求められたかとその理由で、`Status` とは別です。Running で `CancelCause` が `None` 以外なら、閉じている最中です（`handle.ToString()` は `FlowHandle(Screen, Running: Explicit)`）。別のスレッドから `Cancel` したハンドルは、次の Tick か Flush まで Running のままです。

## 不具合を再現する

同じ `deltaTime` の列と同じ入力を同じ順に与えれば、実行順は同じになります。不具合を再現するには、次を記録します。

- `Tick` に渡した `deltaTime` と、`Tick` と `Flush` を呼んだ順。
- World のスレッドでフローの外から与えた入力（`Emit`、`FlowProperty.Set`、`Cancel`、`Run`）と、そのときの `world.UnscaledClock.FrameCount`。
- 別のスレッドから来る値は、どの Tick に取り込まれるかがスレッドの都合で決まります。`world.Post(() => …)` の中でフレーム数と値を記録してから Signal に Emit する形にそろえます。再生では、記録した値を World のスレッドから同じ順に `Post` します。

端末をまたいで同じ結果をそろえる用途（ロックステップの同期）は、この決定論の範囲の外です。フローの実行順の規則は同じでも、ゲームのコードの計算（浮動小数点、物理、乱数）が同じになるかは、FlowTask が保証しません。フローの途中の状態を保存して戻す手段はないので、状態を巻き戻して再計算するロールバック型の同期には使えません。実行順の規則は [実行モデル](../advanced/execution-model.md)にあります。

## IDE で FlowCanceledException のたびに止まらないようにする

`FlowCanceledException` は、キャンセルのたびに投げられる正常な例外です。ライブラリの側に `[DebuggerHidden]` と `[DebuggerNonUserCode]` を付けてありますが、「スローされたときに中断」が有効だと止まることがあります。

- **Visual Studio**：例外設定（Ctrl+Alt+E）の Common Language Runtime Exceptions に `Katout.FlowTask.FlowCanceledException` を追加し、チェックを外します。「マイ コードのみを有効にする」はオンのままにします。
- **Rider**：View Breakpoints（Ctrl+Shift+F8）の .NET Exception Breakpoints で、「Break on all exceptions」の Exclude に `Katout.FlowTask.FlowCanceledException` を足し、「Only break on exceptions thrown from user code」を有効にします。
- **VS Code（C# Dev Kit）**：`launch.json` に `"justMyCode": true` を指定し、BREAKPOINTS の「All Exceptions」を外して「User-Unhandled Exceptions」だけにします。
