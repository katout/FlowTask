# 用語集

FlowTask の API 名と、このドキュメントで使う日本語の用語を、Task、UniTask、R3 の近い語と対応させます。近い語は移行の手がかりで、意味が同じとは限りません。Task や UniTask との違いは [UniTask](../integrations/unitask.md) と [Task / ValueTask](../integrations/task.md) にあります。

## このドキュメントでの書き方

- API 名と型名は訳しません。Clock と Emit は英字で書きます。
- **巻き戻し**は、止まっている await から `FlowCanceledException` を投げ、`finally`、`using`、後始末を走らせながらメソッドを抜けることです（スタックの巻き戻し）。ゲームの状態を元に戻すこと（ロールバック）ではありません。
- 「スコープ」は FlowTask のスコープだけを指します。VContainer の `LifetimeScope` とは別のものです。
- Signal への送信は Emit と書きます。「発火」は .NET や Godot のイベントに使います。Godot の `signal` は「Godot のシグナル」と書きます。
- 「保留」は、後で処理するために止めておくこと（Pause 中の再開など）です。まだ始めていないタスク（`FlowStatus.Unstarted`）のことではありません。

## 実行の単位

| 用語 | 意味 | 近い語 |
|---|---|---|
| `FlowTask`、`FlowTask<T>`（タスク） | 遅延実行の非同期処理。await か開始で始まり、一度だけ await できます | `Task`、`UniTask` |
| FlowTask メソッド | 戻り値が FlowTask の async メソッド、ローカル関数、ラムダ | async UniTask メソッド |
| フロー | 実行中の FlowTask とその子孫の木。`FlowWorld.Run` で始めたものはルートのフローです | — |
| スコープ | FlowTask メソッド 1 回の実行。始めた文脈のスコープの子になり、木を作ります | `CancellationTokenSource` の親子 |
| `FlowWorld`（World） | スコープの木、Clock、再開のキューを持つ実行の単位。毎フレーム Tick します | UniTask の PlayerLoop |
| Tick | 時間を進め、満たされた待ちを再開します | PlayerLoop の更新 |
| フラッシュ、`Flush()` | フラッシュは、再開の予約を FIFO 順に処理することです。`Flush()` は時間を進めずに、取り込みとフラッシュだけを行います | — |
| Flush ポイント | エンジンの統合が Tick のほかに `Flush()` を呼ぶ場所。Unity は PlayerLoop の中の設定した場所（`FlushPoints`）、Godot はフレームの最後です。フローの寿命に結んだ GameObject やノードが、無効になるときやツリーから出るときにも Flush します | — |
| 再開の予約 | 完了や Emit は、待っていたフローをその場で再開せず、キューに積みます | — |
| 受信箱 | 別のスレッドからの送信（`EmitFromAnyThread`、`Post`、`Cancel`、ブリッジの完了）を積み、次の Tick か Flush の最初に取り込む列 | `SynchronizationContext.Post` |
| 束縛 | Signal、FlowProperty、Once が、最初に使った World のスレッドに結び付くこと | — |

## 開始と寿命

| 用語 | 意味 | 近い語 |
|---|---|---|
| `Flow.Spawn`（Spawn） | 今のスコープの子として始めます。親が終わると巻き戻り、例外は親の呼び出し元へ届きます | — |
| `FlowWorld.Run`（ルートのフロー） | 呼んだスコープと関係なく走らせます。例外は `OnUnhandledException` へ届きます | `Forget()`、`UniTask.Void` |
| `Discard()` | タスクを始めずに解放します | `Forget()` ではありません |
| `FlowHandle`（ハンドル） | 始めたタスクの状態、結果、キャンセル、Join | `Task` を変数に持つこと |
| `Join()` | 相手の終わりを待ちます。相手がキャンセルか例外で終わると `FlowJoinException` | `await task` |
| `Flow.AddCleanup`（後始末の登録） | スコープが終わるときに走る処理を登録します | `CancellationToken.Register` |
| `Flow.Own`（所有） | 渡したものを、スコープの終わりに Dispose します | R3 の `AddTo` |
| `Flow.CreateClock`（スコープの Clock） | 今のスコープが持ち、その終わりに消える Clock | — |
| `RunWhileActive`、`WhileActive`（Unity） | GameObject が無効になるか破棄されたら、フローを巻き戻します。`RunWhileActive` はどこから呼んでもルートのフローになり、`WhileActive` は await したスコープの中で動きます | `GetCancellationTokenOnDestroy` |
| `WaitForDestroy`（Unity） | GameObject の破棄で完了します。無効化では完了しません | `GetCancellationTokenOnDestroy` |
| `RunWhileInTree`、`WhileInTree`（Godot） | ノードがツリーから出たら、フローを巻き戻します。`RunWhileInTree` はどこから呼んでもルートのフローになります | — |

## キャンセルと失敗

全体は [失敗の扱い](../guide/failures.md) にあります。

| 用語 | 意味 | 近い語 |
|---|---|---|
| キャンセル | 親の終わり、Race の負け、`Cancel()` などでフローを止めること。スコープは巻き戻ります | UniTask のキャンセル |
| 取り消しの確定（確定済み） | キャンセルが決まった時点。確定したスコープは、次の await で巻き戻り始めます | `IsCancellationRequested` |
| `FlowCanceledException` | 巻き戻しで await から投げる例外 | `OperationCanceledException`（その派生） |
| `CancelCause` | キャンセルの理由（`ParentEnded`、`RaceLost`、`Explicit` など） | — |
| 握りつぶし | 受け取ったキャンセルを投げ直さずに続けること。そのスコープが return すると報告され、スコープはキャンセルで終わります | `catch (OperationCanceledException) { }` |
| 後始末の await | 取り消されたスコープの `catch` と `finally` の await。最後まで走り、周り（親、Race、WhenAll）はその終わりを待ちます | UniTask の `finally` の await |
| `Flow.NonCancelable` | 包んだ await に、祖先のキャンセルを届けない印。キャンセルの前に入った `finally` の await を最後まで走らせます | Kotlin の `withContext(NonCancellable)` |
| 未処理の例外 | どこでも捕まえられずにルートに届いた例外。途中のスコープは `Faulted` で終わり、例外は `FlowWorld.OnUnhandledException` に報告されます | `AppDomain.UnhandledException`、Task の Faulted |
| `FlowExceptionKind` | `OnUnhandledException` への報告の種類。`Unhandled`（未処理の例外）と、報告だけの `Cleanup`（後始末の例外）、`SwallowedCancellation`（握りつぶし）、`Undelivered`（受け手に届けられなかった例外） | — |
| 受け手、待ち手 | 例外や値を受け取る側（await しているスコープ、合成、ハンドル） | — |

## 合成と待ち

| 用語 | 意味 | 近い語 |
|---|---|---|
| `FlowTask.Race` | 最初に処理された完了で決まり、負けた枝を巻き戻してから再開します（同じ Tick に満たされた時間待ちは、待ち始めた順に処理されます） | `UniTask.WhenAny` |
| `FlowTask.WhenAll` | すべてを待ちます。1 つが失敗すると残りを巻き戻します | `UniTask.WhenAll` |
| `WaitForSeconds`、`DelayFrames`、`NextFrame` | スコープの Clock の時間（秒）とフレームで待ちます | `UniTask.Delay`（ミリ秒）、`DelayFrame` |
| `Clock`、`Flow.WithClock` | ゲームの時間。Pause、倍率、親子を持ちます。`WithClock` で中のタスクの Clock を変えます | `DelayType` |
| `DefaultClock`、`UnscaledClock` | エンジンの倍率がかかる時間と、かからない時間 | `Time.deltaTime`、`Time.unscaledDeltaTime` |
| `Clock.Pause()`（Pause） | Clock を止めます。参照カウントで、返したハンドルの Dispose で解けます | `Time.timeScale = 0` |

## シグナル

| 用語 | 意味 | 近い語 |
|---|---|---|
| `Signal<T>`（Signal、シグナル） | 状態を持たない通知。待っている相手にだけ届きます | R3 の `Subject<T>` |
| `Next()` | 次の Emit を待ちます。待っていない間の Emit は捨てます（エッジ） | R3 の `FirstAsync()` |
| `Subscription<T>`（購読） | Emit をバッファにため、`Next()` で 1 つずつ取り出します（pull 型） | R3 の `Subscribe`（push 型） |
| `FlowProperty<T>` | 値を持ち、変わると `Changed` を Emit します。`WaitUntil` で条件を待てます | R3 の `ReactiveProperty<T>` |
| `Once<T>` | 一度だけ値が入り、何度でも待てます | `TaskCompletionSource<T>` |
| `EventSignal<T>`（コールバックのブリッジ） | `FlowBridge.FromCallback` で、コールバックやイベントを Signal として待ちます | R3 の `Observable.FromEvent` |

## ブリッジと診断

| 用語 | 意味 | 近い語 |
|---|---|---|
| ブリッジ | `FlowBridge.FromTask`、`.AsFlow()` で、Task や UniTask などを FlowTask として待ちます。`FlowBridge.FromTask` では、スコープのキャンセルがトークンで渡ります | `AsUniTask()` |
| ダンプ、診断 | `FlowWorld.Dump()`、`Diagnostics` で、スコープの木と待ちを見ます | `UniTaskTracker` |
| ライフタイムハンドル | `Clock.Pause()` や `Signal.Subscribe` が返す、Dispose で解放するハンドル（FLOW004） | `IDisposable` の購読 |
| 仮想時間 | テストが `Tick(dt)` で進める World の時間（[テスト](../tools/testing.md)） | — |
