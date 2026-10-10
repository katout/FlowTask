# FlowTask とは

FlowTask は、ゲームの進行（画面遷移、ダイアログ、キャラクターの行動、演出、チュートリアル、通信を含む手続き）を `async`/`await` で手順どおりに書くための C# ライブラリです。このページでは、FlowTask が解く問題と、その解き方、向いている場面、ほかの仕組みとの分担を説明します。

## ゲームの進行を async で書くと

チュートリアルの 1 画面を、UniTask で書いてみます。矢印を点滅させながら、少し待ってから、タップを待ちます。

```csharp
async UniTask TutorialStep(CancellationToken ct)
{
    BlinkArrow(ct).Forget();                          // forget ct here and the arrow blinks forever
    await UniTask.Delay(500, cancellationToken: ct);  // forget ct here and this step outlives the screen
    await WaitForTap(ct);
}
```

短いコードですが、正しく動かすための約束がいくつも隠れています。

- **CancellationToken の手渡し**：すべての呼び出しにトークンを渡す必要があります。1 か所でも渡し忘れると、その処理だけ止まりません。
- **寿命の漏れ**：画面を閉じたら止まるはずの処理が、トークンの配り方を間違えると残り続けます。どこまでが画面の寿命なのかは、コードの形からは分かりません。
- **Forget した処理の暴走**：`Forget()` した処理は誰にも待たれません。止める責任も、例外の行き先も、呼び出し元から離れます。
- **ばらばらの Pause**：ゲームの一時停止を `Time.timeScale = 0` で行うと、止めたくない UI の待ちまで止まります。待ちごとに、どの時間で数えるかを選び分けることになります。

## FlowTask の書き方

同じ画面を FlowTask で書きます。

```csharp
async FlowTask TutorialStep()
{
    _ = Flow.Spawn(BlinkArrow());           // a child of this flow: ends with it
    await FlowTask.WaitForSeconds(0.5);     // on this flow's clock: stops while the game is paused
    await WaitForTap();
}
```

トークンはどこにもありません。代わりに、FlowTask は**スコープ**の木を作ります。

- FlowTask メソッドを 1 回実行するたびに、スコープが 1 つできます。開始したスコープは、開始した側のスコープの子になります。上の `BlinkArrow` は `TutorialStep` の子です。
- 親が終わると、子孫はすべて止まります。画面の寿命は、コードの入れ子の形そのものです。
- 止めるときは、止まっている await から `FlowCanceledException` を送出してスタックを戻します。これを**巻き戻し**と呼びます。`using` と `finally` はそのまま走るので、後始末は普通の C# で書けます。

```csharp
async FlowTask ShowDialog(Dialog dialog)
{
    dialog.Open();
    try
    {
        await dialog.Closed.Next();
    }
    finally
    {
        dialog.Close();   // runs when the dialog closes, and when the flow is canceled from outside
    }
}
```

外から止めるのも構造で書きます。たとえば、`FlowTask.Race` は最初に終わった枝を勝ちとし、負けた枝を巻き戻してから呼び出し元を再開します。

```csharp
// The back key ends the tutorial step: TutorialStep and BlinkArrow are unwound.
await FlowTask.Race(backKey.Next(), TutorialStep());
```

このように、親子関係でフローの寿命を決め、子が親より長く生き残らないようにする考え方を**構造化並行性**と呼びます。待ちの時間も、スコープに付いた Clock で数えます。ゲームの Clock を Pause しても、別の Clock で動くメニューは止まりません。

ここで出てきた考え方は、[フローと World](../guide/flows-and-world.md) と [スコープとキャンセル](../guide/scopes-and-cancellation.md) で詳しく説明します。

## 主な特徴

- **構造化並行性**：FlowTask は、開始した文脈のスコープの子になります。親が終わると、子孫はすべて巻き戻ります。CancellationToken を手で配りません。
- **巻き戻しによるキャンセル**：止まっている await から `FlowCanceledException` を送出してスタックを戻します（ゲームの状態を元に戻すロールバックではありません）。`using` と `finally` がそのまま走り、`finally` の中の await も最後まで走ります。
- **合成**：`FlowTask.Race`（敗者を巻き戻してから呼び出し元を再開する）、`FlowTask.WhenAll`、子を開始する `Flow.Spawn`。
- **失敗の 2 つの経路**：キャンセルと例外。どの `catch` も捕まえなかった例外（未処理の例外）は、World の `OnUnhandledException` に届きます。
- **決まった実行順序**：Emit は待っていたフローをその場で再開しません。再開はすべて FIFO のキューに入り、決まった順に処理されます。
- **ゲームの時間**：Clock、参照カウント付きの Pause、タイムスケール、親子の Clock。
- **シグナル**：状態を持たない `Signal<T>`、受け取る側がバッファの方針を決める購読、`FlowProperty<T>`、`Once<T>`。
- **ブリッジ**：Task / ValueTask、UniTask、R3、Unity の AsyncOperation / Awaitable / UnityEvent、Godot のシグナル。
- **定常状態で割り当てゼロ**：毎フレームの待ちや Signal の送受信は割り当てません（[性能とメモリ](../advanced/performance.md)）。
- **アナライザ**：使い方の規則（FLOW で始まる 10 のルール）をコンパイル時に検査し、コード修正を付けます。メッセージは英語と日本語です。
- **エンジンなしのテスト**：仮想時間で World を進める `FlowTask.Testing` と、NUnit のアダプタ。
- **診断**：スコープの木のダンプ、警告、Unity の Scope Tree ウィンドウ。

同じコアを .NET、Unity、Godot で使います。

## 向いている場面

FlowTask が得意なのは、ゲームの中で「手順」と「寿命」を持つ処理です。

- 画面遷移、ダイアログ、メニュー、確認の手続き
- キャラクターや敵の行動（割り込みで中断する行動を含む）
- 演出、カットシーン、チュートリアル
- 通信や読み込みを途中に含む手続き（購入、シーンの読み込み、キャンセルボタンで止められる読み込み）
- エンジンを動かさずに、仮想時間で検証したいゲームのロジック

## ほかの仕組みとの分担

FlowTask が受け持つのは、フローの寿命、時間、実行の順序です。ファイルや通信の I/O、別のスレッドでの計算、エンジンの非同期処理、UI と入力は、それぞれの仕組みで書き、ブリッジやシグナルでフローにつなぎます。つないだ処理も、スコープの寿命に従います。

```csharp
async FlowTask<string> LoadSave(string path, Signal<FlowUnit> cancelPressed)
{
    var r = await FlowTask.Race(
        cancelPressed.Next(),
        FlowBridge.FromTask(ct => File.ReadAllTextAsync(path, ct)).ToFlowTask());   // ct is canceled when the button wins
    return r.TryGet1(out var text) ? text : null;
}
```

キャンセルボタンが先に押されると、読み込みの枝が負けてトークンが取り消され、ファイルの読み込みが止まります。画面を閉じて `LoadSave` を呼んだフローが終わったときも同じです。

| やりたいこと | 書き方 |
|---|---|
| ファイル、通信、アセットの読み込みを待ち、キャンセルボタンや画面の終わりで止める | Task などで書き、`FlowBridge.FromTask` で待ちます。止められない処理（Addressables の読み込みなど）は最後まで走り、受け取られなかった結果は `onDiscard` で解放できます（[Task と ValueTask](../integrations/task.md)、[Unity のブリッジ](../unity/bridges.md)） |
| 重い計算を別のスレッドで走らせる | `FlowBridge.FromTask(ct => Task.Run(() => Work(), ct))` で待ちます。結果は World のスレッドで受け取ります（[スレッド](../guide/threads.md)） |
| 別のスレッドから値を届ける | `signal.EmitFromAnyThread(value)` か `world.Post(action)` を使います（[スレッド](../guide/threads.md)） |
| ボタン、キー、エンジンのイベントを待つ | Signal にして待ちます（uGUI の `ClickedSignal()`、`UnityEvent.ToSignal()`、Godot のシグナル）。戻るキーを誰が受けるかのような優先順位は、Signal と `Flow.Own` で組み立てます（[シグナル](../guide/signals.md)） |
| 既存の Task や UniTask のコードからフローを待つ | `world.Run(...)` のハンドルを `AsTask()` か `ToUniTask()` にして await します（[Task と ValueTask](../integrations/task.md)、[UniTask](../integrations/unitask.md)） |
| Unity や Godot を使わない .NET のプログラムで動かす | 自分のループで `Tick` を呼びます（[最初のフロー](first-flow.md)） |

### 前提になる決まり

FlowTask の保証（決まった実行順、構造によるキャンセル、ゲームの時間）は、次の決まりの上に成り立っています。Task に慣れた書き方のいくつかがそのままでは使えませんが、どれにも代わりの書き方があります。理由は [設計の背景](../advanced/design-rationale.md) にあります。

- **World は 1 つのスレッドで動く**：フローのコードは、World を作ったスレッドで `Tick` などの中で走るので、ロックは要りません。Tick を呼ぶループも、そのスレッドから離れないように書きます（[スレッド](../guide/threads.md)）。
- **時間と外からの完了は Tick で進む**：時間の待ちは `Tick` に渡した時間で進み、別のスレッドで終わった Task の結果も `Tick` か `Flush` で取り込まれます。Unity と Godot では、統合が毎フレーム Tick します。
- **World のスレッドでは、フローの終わりを止まって待たない**：`AsTask().Result` のように止まると Tick も止まるので、終わりません。終わったフローの値は `handle.Result` で読み、終わりを待つなら await するか Tick を回します（[フローと World](../guide/flows-and-world.md)）。UniTask も、終わっていない UniTask を止まって待つことはできません。
- **`AsyncLocal<T>` の値はフローごとに分かれない**：フローは ExecutionContext を捕まえません（UniTask も同じです）。フローの中で設定した値はほかのフローにも見え、await の後に同じ値が見える保証もありません。フローごとの値は引数で渡します（[実行モデル](../advanced/execution-model.md)）。

フレームと関係のない処理だけを書くなら（サーバーの要求の処理など）、Task をそのまま使うほうが素直です。FlowTask は Task や UniTask と一緒に使えます。既存のコードはそのままにして、ブリッジで互いに待てます（[Task と ValueTask](../integrations/task.md)、[UniTask](../integrations/unitask.md)）。

> **注意**：版は 0.x です。0.x の間は、版ごとに API が変わることがあります。変更はすべて [CHANGELOG](../../../CHANGELOG.md) に書きます。

## 次に読むページ

- [インストール](installation.md)：.NET、Unity、Godot への導入
- [最初のフロー](first-flow.md)：コンソールのプログラムで、World、Tick、Race を動かしてみる
- [UniTask](../integrations/unitask.md)：UniTask を使ってきた人向けの、語の対応と書き換え
