# 性能とメモリ

FlowTask が割り当てる場面と割り当てない場面、キャンセルの費用、プールの性質をまとめます。毎フレーム動くフローを書くときと、大量のフローを一度に止める画面遷移を設計するときに読んでください。

## 毎フレーム負ける枝は、待ちそのものにする

最初に、いちばん効く指針です。毎フレーム決着する `Race` では、負ける枝を async メソッドではなく、葉の待ち（`NextFrame`、`WaitForSeconds`、`Signal.Next`、購読の `Next`、`WaitUntil`）にします。

```csharp
// Costly: every frame the async method loses, and unwinds by throwing an exception
while (true)
{
    var r = await FlowTask.Race(WaitForJump(jump), FlowTask.NextFrame());
    if (r.Index == 0) Jump();
    UpdateStamina();
}

static async FlowTask<int> WaitForJump(Signal<int> jump) => await jump.Next();
```

```csharp
// Cheap: both branches are leaf waits; nothing is thrown or allocated
while (true)
{
    var r = await FlowTask.Race(jump.Next(), FlowTask.NextFrame());
    if (r.Index == 0) Jump();
    UpdateStamina();
}
```

async メソッドを取り消すと、止まっている await で例外を投げて巻き戻します（下の「キャンセルの費用」）。葉の待ちの巻き戻しは例外を使いません。被弾や画面遷移のように、たまにしか起きない決着なら、async メソッドの枝で問題ありません。

## 割り当てない場面

定常状態では、次の処理はヒープに割り当てません。

- 毎 Tick の待ち（`NextFrame`、`DelayFrames`、`WaitForSeconds`、`WaitUntil`）
- 同じスコープにとどまるループ（ループの中で子の FlowTask メソッドを await するものを含む）
- シグナルの送受信と購読
- 葉の待ちを敗者に持つ `Race` の決着と、購読の値を敗者から戻す `Race`
- 葉の枝の `WhenAll` の決着
- `FlowProperty`、`Clock.Pause()`

テスト（`AllocationTests`）が、これを .NET、NativeAOT、Unity で確かめています。Unity の IL2CPP（Windows のデスクトップの開発用プレイヤー。スクリプトは最適化してコンパイル）でも 0 バイトです。

状態機械とノードは、型ごとのプールで使い回します（下の「プール」）。

## 割り当てる場面

次の処理は、1 回ごとに割り当てます。

- `FlowWorld.Run` と `Flow.Spawn` で始めたフローのルート。ハンドルからいつでも状態を読めるように、プールしません。
- ブリッジ：`FlowBridge.FromTask`、`.AsFlow()`、`FlowBridge.FromCallback`。毎フレームの待ちには、ブリッジではなく FlowTask の待ちを使ってください（[Task / ValueTask](../integrations/task.md)）。
- `Flow.CreateClock`。
- async メソッドの巻き戻し（次の節）。
- 失敗 1 回：失敗の記録、`FlowExceptionInfo`、`ExceptionDispatchInfo`、パスの文字列を 1 回ずつ。例外が await の鎖を上るときは、投げ直す段ごとにも割り当てます。成功で終わる鎖は、段数によらず割り当てません。
- Debug でコンパイルした利用者のコード。コンパイラが状態機械をクラスにします。Unity の Development Build とエディタの Code Optimization が Debug のときも同じです（[Unity のセットアップ](../unity/setup.md)）。割り当てを測るときは、最適化したビルドで測ってください。

## キャンセルの費用

取り消された async メソッドは、止まっている await で新しい `FlowCanceledException` を 1 回投げ、`finally` と `using` を走らせながら抜けます。回数は止まっている async メソッド 1 個につき 1 回です。例外を段ごとに投げ直す鎖にはなりません。葉の待ちの巻き戻しは例外を投げず、割り当てもありません。

1 個あたりの費用の目安は次のとおりです。どれもデスクトップの PC で測りました。

| ランタイム | 1 個あたりの時間 | 1 個あたりの割り当て |
|---|---|---|
| .NET 10（JIT、Release） | 約 1.2 µs | 200〜300 B |
| .NET 8（JIT。Godot 4.4 のゲームが対象にする版） | 約 5 µs | 約 208 B |
| Unity の IL2CPP（Windows の開発用プレイヤー。スクリプトは最適化してコンパイル） | 約 14〜24 µs | 約 1 KB |
| 葉の待ち（参考） | 約 20 ns | なし |

- **.NET 10**：BenchmarkDotNet の `Cancellation`（`tests/FlowTask.Benchmarks`、.NET 10.0.10）で、`try`/`finally` を持って止まっている async メソッドを 100 個まとめて取り消し、(取り消し − 正常終了) / 100 を 1 個あたりの費用としました。横に並べた形（`WhenAll`）が 1.18 µs と 224 B、入れ子の形が 1.20 µs と 224 B でした。費用のほぼすべてが例外の送出と捕捉です。割り当ての内訳は、例外オブジェクトが 128 B で、残りはランタイムが投げるたびに取るスタックトレースの領域です。後者は、投げる await が MoveNext にインライン化されていれば 72 B で、JIT の段階とメソッドの形で増えます。
- **IL2CPP**：投げるたびにスタックトレースを取り直し、そのためにスレッドのスタック全体をたどるので、呼び出しが深いほど高くなります。例外のインスタンスを使い回しても、減るのは例外オブジェクトの分だけです。
- スマートフォンなどの実機で IL2CPP を測った値はありません。対象の端末で測ってください。

### 画面遷移の見積もり

止まっている async メソッドが 500 個ほどある画面を一度に閉じると、上の IL2CPP の開発用プレイヤー（デスクトップの PC）では 6〜12 ms かかりました。.NET の JIT では 1 ms 未満です。スマートフォンなどの実機では測っていないので、実機での値は分かっていません。

仕組みは UniTask と Task の既定（`OperationCanceledException` を投げて巻き戻す）と同じです。中断する側のコードは、どの枝が勝ったかを Race の結果（`Index`）で受け取るので、例外なしに分岐できます。例外で巻き戻るのは、負けた枝の中で止まっている async メソッドだけです（[合成](../guide/composition.md)）。UniTask の `SuppressCancellationThrow` にあたる、例外を投げずに「キャンセルされたか」を返す await はありません。理由は [設計の背景](design-rationale.md) にあります。

## プール

- 状態機械とノードは、型ごとのプールで使い回します。
- 上限は型ごとに 1,024 個です。超えて同時に解放されたものは GC に任せます。設定はありません。
- プールは全 World とスレッドで共有します。World を Dispose しても空にならず、プロセス（Unity ではドメイン）の終わりまで残ります。最悪の残量は、使ったノードの型の数 × 1,024 × ノードの大きさです。
- プールを空にする API はありません。

同じ型のフローを 1,024 個より多く同時に終わらせる（敵の群れの退場など）と、上限を超えた分は次の開始で割り当て直します。

## 自分で測る

リポジトリの `tests/FlowTask.Benchmarks` に BenchmarkDotNet のベンチマークがあります。FlowTask のベンチマークは、エンジンと同じく `Emit` してから `Tick` する形で World を動かし、UniTask との比較も含みます。

```sh
dotnet run -c Release --project tests/FlowTask.Benchmarks -- --filter '*Cancellation*'
```

自分のゲームで測るときは、Release（Unity では Development Build ではないプレイヤー）で、対象のランタイム（IL2CPP など）で測ってください。上の表のとおり、キャンセルの費用は JIT と IL2CPP で 10 倍以上違います。
