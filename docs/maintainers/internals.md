# 内部構造

コア（`src/FlowTask`）の内部の仕組みをまとめる（保守する人向け。サイトには載せない）。利用者から見える挙動と規則は [実行モデル](../ja/advanced/execution-model.md) と `docs/ja/guide/` にあり、そちらが正である。ここでは、それを実装している型と手順を書く。内部の型はすべて `Katout.FlowTask.Internal` にある。

## ノードの木

開始されたすべての FlowTask は、`FlowNode` として木の 1 ノードになる。寿命を持つのはこの木だけである。

| 種類 | 実体 | 役割 |
|---|---|---|
| スコープ | `StateMachineScope<T>`、`FlowMethodScope<TStateMachine, T>` | async FlowTask メソッド 1 回の実行。状態機械の型ごとにプールする |
| 合成 | `CombinatorNode<TR>` の派生：`RaceNode<T0, T1>`、`RaceNode<T0, T1, T2>`、`RaceArrayNode<T>`、`RaceIndexNode`、`WhenAllNode<…>`、`WhenAllArrayNode<T>`、`WhenAllVoidNode` | 枝ごとに子ノードを作ってから開始する。2〜3 枝の型付きのものは `Internal/CombinatorNodes.g.cs`（`tools/gen_combinators.cs` が生成） |
| 待ち（葉） | `LeafNode<T>` の派生：`ClockWait`（`WaitForSecondsNode`、`DelayFramesNode`、`WaitUntilNode<TState>`）、`ValueWaitNode`（`NextNode<T>`、`NextOrClosedNode<T>`）、`PropertyWaitNode<T, TState>`、`TaskNode<T>`、`JoinNode<T>`、`ValueNode<T>`（`FromResult` を Run、Spawn、合成に渡したとき）、`NeverNode` | 時間、フレーム、条件、シグナル、FlowProperty、外部 Task、Join の待ち |
| ラッパー | `DiscardNode<T>`（`WithoutResult`） | 中身の結果を変える |
| ルート | `RootNode` | World のルートスコープ。例外の最終の受け手 |

- 子は開始順の双方向リストで持つ。巻き戻しは後順走査で行う。
- 木をたどる処理（キャンセルの印付け `Unwinder.MarkSubtree`、巻き戻しの後順の収集 `Unwinder.CollectPostOrder`、ダンプ `ScopeTreeDumper`、`FlowDiagnostics.Walk`）は、再帰せずに木のリンク（`Parent`、`FirstChild`、`NextSibling`）か明示のスタックでたどる。await の鎖は数千段になり得て、再帰ではスタックがあふれるため。ダンプは 64 段（`MaxDrawnLevels`）より深い行の字下げを止め、`[depth N]` を付ける（出力が節点の数に比例するように）。
- どのノードも、子がすべて終わってから終わる。後始末を走らせている子（取り消された後に catch や finally で await しているスコープ、`Flow.NonCancelable` の await）が残るあいだ、親は終了（後始末と待ち手への通知）を保留し（`NodeFlags.WaitsForChildren`）、最後の子が終わった時点で続ける。
- `FlowTask` は `(ノード, トークン)` の構造体。ノードを解放するたびにトークンを増やすので、再利用後のノードに古い写し、ハンドル、awaiter が触れても検出できる（`FlowMisuseException`、`FlowTask.Status` は `Invalid`）。

### NodeState、NodeTraits、NodeFlags

`NodeState`（`Internal/NodeState.cs`）は `Pooled`、`Unstarted`、`Running`、`Succeeded`、`Canceled`、`Faulted`。`NodeTraits` はノードの型で決まる性質（`StateMachine`、`Leaf`、`ParkableWait`）で、プールで戻しても消えない。

`NodeFlags`（ushort）は 16 ビットすべてを使っている。次のフラグは型を広げる。

| フラグ | 意味 |
|---|---|
| `CancelConfirmed` | キャンセルが確定した |
| `UnwindBegun` | 状態機械：`FlowCanceledException` がコードに届いた（catch と finally の await は生きているスコープと同じに走る） |
| `Completing` | 決着したか終わりかけ。ほかの結果を受けない |
| `Ending` | 終了が始まった（子を待って保留中かもしれない） |
| `NoPool` | プールしない（Run、Spawn、ブリッジ。ハンドルがいつでも観測する） |
| `ConsumerGone` | 結果を読む者がいない。終わったら解放する |
| `ReleasePending` | MoveNext の実行中に解放された。戻ったら解放する |
| `Spawned` | `Flow.Spawn` か `FlowWorld.Run` で始めた |
| `InTickList` | Tick リストにいる |
| `HandleAwaited` | ハンドルが Join された（1 回だけ） |
| `ParkedWait` | 状態機械：ノードを作らずに自分で Tick リストで待っている（下記） |
| `WaitsForChildren` | 終了か巻き戻しが子の終わりを待っている |
| `Unclaimed` | 葉：受け手がまだ受け取っていない値を持つ。この状態で解放されたら値を取得元へ戻す |
| `ResumeQueued` | 状態機械：今の await の再開がキューにある。前の await の分の再開は、これが消えているのを見て何もしない |
| `FaultQueued` | 葉：失敗し、その失敗がキューで待ち手に届く順番を待っている |
| `NonCancelable` | `Flow.NonCancelable`：周りのキャンセルが入らない（`World.Dispose` は入る）。取り消されたスコープはこの await の結果を受け取る |

### ノードにしない待ち（ParkedWait）

状態機械が `NextFrame()`、`DelayFrames(n)`、`WaitForSeconds(秒)` を直接 await したとき（Clock の指定、`Flow.Named`、`Flow.NonCancelable` がないとき）は、ノードを作らず、状態機械自身が目標を持って Tick リストに入る（`AwaitMode.Parkable`、`NodeFlags.ParkedWait`、`ParkKind`、`ParkEnd`）。最も多い待ち方なので、ノードの開始から解放までを省く。

- 挙動はノードと同じ。ダンプにはスコープの行に「NextFrame on Game, 1 frame(s) left」と出る。
- 違い：失敗のパスがスコープのパスになる。`Diagnostics.Walk()` に現れない。await した FlowTask の写しの `Status` が `Invalid` になる。
- 合成、`Spawn`、`Run` に渡した場合はノードを使う。

## ビルダーと awaiter

- `FlowTaskMethodBuilder.Start` は `MoveNext` を呼ばず、ステートマシンをプールから借りた `FlowMethodScope` に写すだけ。`_scope` を先に設定する（写した状態機械が持つビルダーの写しがスコープを指すように）。
- ビルダーは自分のスコープを持ち、再開のたびに `FlowWorld.CurrentScope` に設定するので、外部の再開経路に依存しない。
- ビルダーは ExecutionContext を捕まえない。
- `default(FlowTask)` は `CompletedTask`（ノードなし、`Succeeded`）。`default(FlowTask<T>)` はトークン 0 で、`IsDefault` として await や合成で `FlowMisuseException` にする。

**awaiter**：タスクを開始するのは `GetAwaiter`（`AwaitCore.Begin`）。awaiter は読み取り専用の構造体で、作った時点で決めた `AwaitMode` を持つ。

| `AwaitMode` | 意味 |
|---|---|
| `Value` | 完了済みの値（`FromResult`、`CompletedTask`）。同期完了の上限に数えない |
| `Completed` | 始めたタスクが同期に完了した。`SyncCount` を数える |
| `Pending` | 止まる。継続を登録する |
| `CancelOnRegister` | 取り消されたスコープの await。継続の登録で `FlowCanceledException` を投げる |
| `Parkable` | まだ始めていない時計の待ち。`OnCompleted` でスコープを Tick リストに入れる |

- 取り消されたスコープでの await（`BeginInCanceledScope`）：`UnwindBegun` 後の後始末なら生きているスコープと同じに始め、`CleanupWatch` に登録する。`NonCancelable` のタスクも始め、スコープは結果を再開で受けず、子を待つ巻き戻しと同じ仕組みで終わりを待ってから `GetResult` で受け取る。それ以外は `FlowCanceledException`。`World.Dispose` の間はすべて投げる。
- 同期完了の上限：`scope.SyncCount` が `AwaitCore.MaxSyncCompletionsPerTick`（1,000,000）を超えたら `EndlessLoop` でスコープを終える。`SyncCount` は、同期完了を数えるときに `SyncTickId` が World の `TickId` と違えば 0 に戻す。`TickId` は `Tick`、`Flush`、外からの（`IsExecuting` でないときの）`Run`、`Dispose` で増えるので、数はトップレベルの 1 回ごと（Tick、Flush、Run）になる。Flush だけで回す World でも、数が World の生きている間に積み上がらない。
- Dispose の間の投げ直しは、1 つのスコープで `MaxRethrowsAtDispose`（32）回まで。その次の await は `AwaitKind.Discard` で続きを捨て、スコープを Canceled で終える。
- ライブラリの awaitable かどうかは、awaiter の型で判定する（`LibraryAwaiter<TAwaiter>`）。`FlowTask.Awaiter` と `FlowTask<T>.Awaiter` だけがライブラリのもの。それ以外で止まったら `AwaitKind.Discard` で継続を登録せず、スコープをその例外で終える。
- FlowTask メソッドでない async メソッドが FlowTask を await して止まったら、継続を登録せず、その時点の `CurrentScope` を `FlowMisuseException` を持たせて取り消す（`CancelCause.Fault`）。手で `OnCompleted` を呼ぶコードも同じ経路。

## スケジューラ

`FlowWorld` は、手順ごとのクラスを順に呼ぶ。

| 手順 | クラス | 役割 |
|---|---|---|
| 1 取り込み | `Inbox` | 別スレッドからの送信（Emit、Close、Cancel、Post、Task の完了、onDiscard）。1 本の列（`IInboxItem`） |
| 2 時間 | `ClockTree` | World の生きている Clock。親が先。Pause と倍率。削除は線形の探索 |
| 3 時間待ち | `TickList` | Tick が評価する待ち（時間、フレーム、条件）の双方向リスト。評価は手順の始めのスナップショットに対して行う。Pause 中の Clock の待ちは飛ばし、削除された Clock の待ちを失敗させる |
| 4 フラッシュ | `ResumeQueue` | 再開の予約の FIFO。Pause 中の Clock の再開を保留し、解除で先頭に戻す。1 回 `MaxResumesPerFlush`（65,536）まで |
| 5 終わり | `IDeferredCompletion`（`TaskObserver<T>`） | `AsTask` の Task を、トップレベルに戻ってから完了させる |
| — | `Unwinder` | キャンセルの確定（部分木に印を付ける）と巻き戻し。実行中ならその場、外からならフラッシュの先頭（`_pending`） |
| — | `RunningStack` | コードがコールスタック上にある状態機械（内側が最後） |
| — | `Reporter` | 報告（未処理の例外、後始末の例外など）を見つけた順に OnUnhandledException へ。OnUnhandledException がなければトップレベルの Tick、Flush、Run、Dispose で投げる。警告は種類ごとに World で 1 回（ビットマスク） |
| — | `CleanupWatch` | 後始末で待っている取り消されたスコープ。`UnscaledClock` で 10 秒（`LimitSeconds`）を超えたら `LongCleanup` を 1 回。Tick の手順 4 の後に調べる |

- `Tick` は手順 1〜4 の後に `CleanupWatch.Check()`、その後 `CompleteDeferred()` と `ThrowUnhandledIfTopLevel()`。`Flush` は手順 1、4、5。
- `ExecutionDepth` が 0 でない間（World の実行中）は `Tick`、`Flush`、`Dispose` を `Errors.Reentrant` で拒む。
- `FlowWorld.Run` は、ルートの子としてノードを始め（`NoPool | Spawned`）、`RunningStack` が空なら `Unwinder.DrainDeferred()` を呼ぶ。Dispose の間は `ReleaseUnstarted` して `default` のハンドルを返す。

## キャンセルと巻き戻し

- キャンセルの確定は、部分木全体に即座に `CancelConfirmed` を付ける。すでに取り消されたノードの下と、`NonCancelable` のノードの下には入らない（`World.Dispose` は両方に入る）。
- 停止中の状態機械は、子がすべて終わってから再開され、await 地点の `GetResult` が `FlowCanceledException` を投げる。巻き戻しごとに新しいインスタンスを作る。送出箇所はデバッガーから隠す（`[DebuggerHidden]`）。
- 実行中のスコープ（`RunningStack` にあるもの）は再入的に再開できない。次の await で止まり、子がすべて終わってからそこで巻き戻る。祖先は、実行の連鎖がトップレベルに戻った時点で巻き戻る。そのため、失敗の発生元が最初に、停止中の兄弟が後から始めた順に、祖先が最後に巻き戻る。
- スコープの終わりの後始末は `CleanupStack`（AddCleanup、Own、スコープが所有するライフタイムハンドル、`Flow.CreateClock` の Clock）を LIFO で走らせる。`CleanupStack` はノードの struct フィールドではなくクラスにしている（struct のフィールドはランタイムが後ろに置き、ノードのホットなフィールドの配置が変わって Race が PGO なしで 3〜4% 遅くなったため）。
- 握りつぶし：`UnwindBegun` のスコープが普通に return したら、`SwallowedCancellation` を 1 回報告し、Canceled で終える（運んでいた失敗があればそれを持って終える）。

## 失敗の経路

- `FailureRecord` がノードの失敗（種類、`FlowExceptionInfo`、`ExceptionDispatchInfo`）を持つ。`ExceptionDispatchInfo` は、例外が最初にフローのコードから出た場所で 1 回だけ取り、鎖の各段の await はそれを投げ直す。成功で終わる鎖は割り当てない。
- 失敗の通知は値の完了と同じ `ResumeQueue` を通り、1 段ずつ上る。葉の失敗は `FaultQueued` で順番を待つ。
- 受け手が取り消されていたか決着していたら、運ばずに `Undelivered` として、投げた場所のパスで報告する。取り消し確定済みのスコープのコードが新しく投げた例外は `Cleanup`。キャンセルの前に取り消されたスコープから出た例外（NonCancelable の await の後に finally が投げ直した、運んでいた例外）は `Undelivered`。
- 葉の待ち自身の失敗（`FlowJoinException`、`SignalClosedException` など）は、取り消し確定後なら報告せず Canceled で終わる。
- Spawn した子の例外（所有者が実行中のとき）：
  1. 所有者の部分木（失敗した子を除く）を `CancelCause.Fault` で取り消し確定にし、所有者に子の例外を持たせる。
  2. 子を Faulted で終える。所有者の中の Join はすでに取り消されているので、同じ失敗を 2 度報告しない。
  3. 所有者を巻き戻す。所有者は子の例外で終わり、普通の例外と同じ経路で待ち手へ届く。
- ノードの `OnStart` が投げたら（束縛の誤用、使えない Clock）、そのノードは例外を通す前に Canceled で終わり（始めた子も巻き戻す）、木に残らない。合成が先に始めた枝が後始末で await していると、合成はその終わりまで木に残り、例外はそれを待たずに await へ出る。

## シグナルと値の返却

- `Emit` は待機者を確定し（葉に値を設定して完了状態にし）、配達のエントリをキューに積むだけ。待機者の一覧は `WaiterList<T>`。
- 購読（`SubscriptionCore<T>`）は受信側のリングバッファで、現在のスコープに所有される（`IScopeOwned`）。待機者がいればバッファを通さずに渡す。容量は上限で、確保は中身に合わせて伸ばす。
- 値を受け取った葉は `Unclaimed` を持つ。受け手が受け取る前に解放されたら（Race の負け、巻き戻し、失敗した WhenAll）、値を取得元へ戻す。購読の待ちは購読の先頭へ（後から始めたものから解放するので、取った順に戻る）。ブリッジは次の取り込みで `onDiscard` へ（スケジューラの解放の途中で利用者のコードを呼ばないため）。合成が値を結果に取り込んだら受け取り済みとし、戻さない。
- 合成が枝を手放すとき（`CombinatorNode.ReleaseBranches`）、自分が始めなかった枝は、まだ `Unstarted` のときだけ `ReleaseUnstarted` で解放する。同じタスクをほかの合成にも渡していて、そちらが始めていれば、その枝はそちらのものなので触れない。
- `FlowProperty` の通知中の `Set` は値をすぐ変え、通知は今の通知が終わってから Set した順に行う（`Waiter`、`Pending`）。

## スレッドの束縛

`ThreadBinding`（`Signals/ThreadBinding.cs`）が、Signal、FlowProperty、Once の World とスレッドを持つ。

- 最初に使った World（フローの中の待ち、購読、フローの中での作成、`Signal(FlowWorld)`）が束縛する。束縛は `Gate` のロックの下で行う。判定は World が違うときの遅い経路だけ。
- 派生シグナル（`FlowProperty.Changed`）は、持ち主の `ThreadBinding` を参照で共有する。
- まだ束縛されていないシグナルへの別スレッドからの Close（`CloseFromAnyThread`、`EventSignal.Dispose`）は、同じロックの下で閉じた印だけを付け、後から始まる待ちがその印を見る。

## ブリッジ

- `TaskNode<T>` が外部 Task を待つ。完了は、完了させたスレッドでその場で受信箱に積む（`IInboxItem`）。SynchronizationContext は使わない。
- ブリッジが待つのをやめた後に届いた結果：成功は onDiscard に回し、キャンセルは黙り、失敗は Undelivered として報告する（例外は観測済みになる）。onDiscard のない受け取られなかった結果は報告せずに捨てる。
- `AsTask` の Task（`TaskObserver<T>`）は、フローが終わったときに結果を記録し、トップレベルに戻ってから完了させる。

## Clock

- Clock は dt を double で累積する。`WaitForSecondsNode` は開始時に `Time + 秒` を目標にし、`Time >= 目標` で満たす。ε では比べない。
- `DelayFramesNode` は `FrameCount + n` を目標にする。0 は同期に完了する。
- `Flow.CreateClock` の Clock はスコープの `CleanupStack` で削除する。削除された Clock は Pause 扱いのまま残り（`LiveWorld == null`）、その上の待ちは手順 3 で失敗させる。

## プール

`NodePool<T>`（`Internal/Collections.cs`）は型ごとの static フィールドに置く配列のスタック。

- 全 World とスレッドが共有する。`Interlocked.Exchange` で確保し、ほかのスレッドが確保中なら待たずに諦める（`Rent` は null を返して呼び出し側が新しく作る。`Return` は GC に任せる）。空のプールは原子操作なしで判定する。
- 上限は型ごとに 1,024（`Limit`）。超えて返されたものは GC に任せる。
- 要素は struct（`Slot`）に包む（`T[]` に入れると `Return` のたびに要素型の検査が入る）。static コンストラクタを持たない struct にして、IL2CPP のクラス初期化の検査を避ける。
- World を Dispose しても空にならない。
- `FlowWorld.Run` と `Flow.Spawn` のルートは `NoPool`。

## 名前とソースの位置

`StateMachineNames<TStateMachine>` が、コンパイラが生成した状態機械の型名から、表示名、宣言している型、メソッド名を型ごとに 1 回（診断を読んだときだけ）計算する（`StateMachineNameParser`）。ラムダは囲むメソッド名、ジェネリック型は型定義を返す。`Flow.Named` の影響は受けない。await の経路には入らない。`FlowScopeInfo.DeclaringType` と `MethodName` がこれを公開し、Unity の Scope Tree がソースを開くのに使う。

待ちを作った場所は、待ちと合成を作る API の `[CallerFilePath]` と `[CallerLineNumber]` の引数を、`FlowNode.SetSite` がノードの `SiteFile` と `SiteLine` に入れる（空のパスは null）。ノードを作らずに状態機械が自分で待つ時間とフレームの待ち（`Park`）は、待ちのノードをプールへ返す前に、その場所を状態機械の `SiteFile` と `SiteLine` へ写す（状態機械には自分の場所がないので、同じフィールドを使う）。`FlowNode.GetWaitSite` が `DescribeWait` と同じ条件で場所を返し（状態機械は止まっている待ちか、直接待つノードの場所。子の状態機械を待つときはなし）、`FlowScopeInfo.WaitingFile` と `WaitingLine`、ダンプ、Unity の Scope Tree がこれを使う。ノードを返すときに消す。`WithoutResult` のノードは、包んだタスクの場所を写す。

## 実装の制約

- 公開 API を変えずに挙動を変えるときも、`docs/ja/` の該当ページを直す。
- コアはリフレクションの型（`MethodBase`、`MethodInfo` など）を使わない（`ConstraintTests`）。
- ホットパス（await、再開、Emit、Tick）では割り当てない。`AllocationTests` が定常状態を検査する。
- 性能に関わる変更は `dotnet tools/bench/ab.cs`（`--no-pgo` も）で前後を測ってから入れる。
- 位置指定の record の init アクセサに要る `IsExternalInit` は、internal の同名の型をコア（netstandard2.1 のときだけ）と FlowTask.Unity に置いている。
