# Unity のサンプル

ゲームでよくある場面を FlowTask で書いたサンプルと、それを試すデモのシーンです。写して、自分のゲームに合わせて変えて使ってください。Godot にも同じ構成のサンプルがあります（[Godot のサンプル](../godot/samples.md)）。

## 開き方

1. FlowTask.Unity を入れたプロジェクトで Package Manager を開き、FlowTask for Unity の Samples の「Scenarios」で Import を押します。`Assets/Samples/FlowTask for Unity/<版>/Scenarios/` にコピーされます。
2. `Demo/Demo.unity` を開いて Play を押します。

- 左のボタンで場面を選びます。Escape が戻るキーです（Android の戻るボタンも Escape として届きます）。
- 右上の Pause でゲームを止めると、敵は止まり、メニューと左のボタンは動き続けます。止めている間の Hit は、再開したときに届きます。
- uGUI（`com.unity.ugui`）を使います。入力は、Input System のパッケージがあればそれを、なければ Input Manager を使います。
- 言語は Unity の既定の C# 9 です。ビューはプレハブではなくコードで組んでいます（`SampleUi.cs`）。ゲームではプレハブにするところです。
- ストア、アセットの読み込み、設定の保存先は、デモ用の偽物です（`Demo/DemoServices.cs`）。

## 場面

| 場面 | フォルダ | 示すこと |
| --- | --- | --- |
| 戻るキー | `BackKey/` | 層付きのリスト、`Flow.Own`、`Signal` の合成。一番上の層で待つフローに届け、`Block()` で飲み込む |
| 確認ダイアログ | `ConfirmDialog/` | ボタン、戻るキー、時間切れの Race。閉じるアニメーションを `finally` で待つ |
| 敵 AI | `Enemy/` | `RunWhileActive` とゲームの Clock、被弾の購読、`FlowProperty.WaitUntil` |
| チュートリアル | `Tutorial/` | 手順ごとの `using`、スキップの Race |
| 購入 | `Shop/` | Task のストアのブリッジ、想定した失敗を専用の例外型にする、再試行、遅れて届いたレシート |
| ポーズ | `Pause/` | `Pause()` の参照数、UI の Clock、閉じるときに時間の上限付きで保存する |
| 並列読み込み | `Loading/` | `WhenAll`、進捗、取り消し、全リクエストの解放 |

`GameClocks.cs` は、ほかの場面が使う Clock の組です。敵やタイマーは `Game`（`DefaultClock` の子）で、メニューやダイアログは `Ui`（`UnscaledClock` の子）で動かすので、ゲームを止めても UI は動き続けます。`GameClocks` と `BackKeyRouter` は World（セッション）ごとに 1 つ作ってフローに渡し、static なフィールドには置きません（[セットアップ](setup.md)）。デモは `GameClocks.ForCurrentScope()` で Clock を作ります。デモのフローが持つので、デモが終わると World から外れ、デモのシーンを読み直しても Clock は増えません。

詳しい説明は各ファイルのコメントにあります。以下は場面ごとの要点です。

## 確認ダイアログ

```csharp
if (await ConfirmDialog.Show(canvas, "Buy the gem pack?", router, clocks.Ui)) Buy();
```

```csharp
var answer = await FlowTask.Race(new[]
{
    ok.Next().WithoutResult(),
    cancel.Next().WithoutResult(),
    router.Next(BackKeyRouter.Dialog),
    FlowTask.WaitForSeconds(timeoutSeconds, ui),
});
```

- Race の負けた枝は、呼び出し元が再開する前に巻き戻ります。同じフレームの 2 回目のタップは誰にも届かず、戻るキーの登録も負けた枝と一緒に外れます。
- 閉じるアニメーションは `finally` の中で `await Flow.NonCancelable(view.PlayClose(ui))` として待ちます。答えを返した後に取り消しや破棄が来ても、アニメーションと `Destroy` は最後まで走ります（[スコープとキャンセル](../guide/scopes-and-cancellation.md)）。

## 敵 AI

```csharp
async FlowTask Run()
{
    await FlowTask.Race(Alive(), Hp.WaitUntil(hp => hp <= 0)); // HP 0 cuts through the patrol and a flinch at once
    await Die();
}

async FlowTask Alive()
{
    using var hits = Damaged.Subscribe(BufferPolicy.Latest); // a hit during a flinch is kept
    while (true)
    {
        var r = await FlowTask.Race(hits.Next(), Patrol()); // the hit first
        if (r.Index == 0) await Flinch();
    }
}
```

- AI のフローは `Start` で `RunWhileActive` に渡して GameObject に結ぶので、破棄、無効化、シーンの終わりで止まります。`Game` の Clock で動くので、ゲームのポーズで止まり、ポーズ中の被弾は購読に残ります。
- ループの中で `Damaged.Next()` を待つと、ひるみ中の被弾を取りこぼします（アナライザの FLOW006）。

## チュートリアル

- 吹き出しとハイライトは手順ごとの `using` で出すので、手順を終えたとき、スキップしたとき、シーンの切り替えで取り消されたときのどれでも消えます。シーンを越えて残るオーバーレイでも残りません。
- チュートリアルは教える対象のシーンの GameObject に `RunWhileActive` で結びます。返り値は終えた手順の数です。

## 購入

- ストアの Task は `FlowBridge.FromTask(ct => _store.PurchaseAsync(itemId, ct), _onLateReceipt)` でブリッジします。フローが取り消されるとトークンが取り消され、取り消した後に届いたレシートは 2 つ目の引数に渡ります。代金は払われているので、そこでアイテムを渡します（[Task / ValueTask のブリッジ](../integrations/task.md)）。
- ストアの想定した失敗（`StoreException`）だけを `catch` し、再試行できる失敗は UI の Clock で 1 秒、2 秒と待って 3 回まで試します。断られたときと、3 回とも失敗したときは、理由（`PurchaseError`）を持つ `PurchaseFailedException` を投げます。購入の間は `router.Block()` で戻るキーを飲み込みます。
- 画面（`ShopScreen`）は `catch (PurchaseFailedException e)` で理由を表示します。それ以外の例外はバグとしてそのまま上がり、画面の `catch (Exception bug) when (bug is not FlowCanceledException)` が受けて、その 1 回の購入だけを終えます（[失敗の扱い](../guide/failures.md)）。
- 購入ボタンは購読せずに `Next()` で待ちます。購入中の連打は捨てられ、2 回目の購入は後ろに並びません。

## ポーズ

- メニューは開いている間 `using var pause = clocks.Game.Pause();` を持ちます。`Pause()` は参照数で数えるので、メニューの上に開いた設定画面もポーズを取り、最後の 1 つが外れたときにゲームが動き出します。
- メニューと設定画面は `Flow.WithClock(clocks.Ui, …)` で UI の Clock に固定します。ゲームの Clock で動くフローから開いても、自分のポーズで止まりません（`PausedOwnClock` の警告）。
- 設定画面は閉じるとき（取り消されたときも）に `finally` の中で設定を保存し、周りのフローはその終わりを待ちます。保存は UI の Clock の 2 秒と Race し、間に合わなかったら、設定は次に閉じるときまで未保存のまま残ります。

```csharp
finally
{
    if (settings.IsDirty)
    {
        var saved = await Flow.NonCancelable(FlowTask.Race(settings.Save(), FlowTask.WaitForSeconds(SaveTimeoutSeconds)));
        if (saved.Index == 0) settings.MarkClean();
    }
}
```

Clock の `Pause()` が止めるのは、その Clock で動くフローだけです。Animator、物理、`Time.deltaTime`、`WaitForSeconds` で待つコルーチンは動き続けます。`PauseLink` は、`Game` の Clock がポーズしている間だけ `Time.timeScale` を 0 にして、それらも止めます（`Update` と、`yield return null` で回すコルーチンは止まりません）。

- 画面はポーズを Clock の `Pause()` で取るだけにして、`Time.timeScale` は `PauseLink` だけが書きます。入れ子のポーズ（メニューの上の設定画面）は Clock の参照数が数えます。
- `Time.timeScale` が 0 の間は `DefaultClock` とその子も止まります。ポーズ中に動かすもの（メニュー）は、`UnscaledClock` の子の UI の Clock で動かします。
- `PauseLink` はセッションの間ずっと有効な GameObject に付けます。無効化や破棄のときに `Time.timeScale` を元に戻します。

## 並列読み込み

- 読み込みは `IAssetLoader` / `IAssetRequest` の向こうに置き、Addressables、AssetBundle、Resources を差し替えられるようにしています（`ResourcesLoader` は `Resources.LoadAsync` 版）。1 つのアセットを読むだけなら、Addressables の `.Task` を `FlowBridge.FromTask` でブリッジする書き方もあります（[ブリッジ](bridges.md) の「Addressables と UnityWebRequest」）。
- アセットを読み込めないと、その読み込みは `AssetLoadException` を投げます。`FlowTask.WhenAll` は最初の例外でほかの読み込みを巻き戻してから、その例外を投げ直します。`LoadAll` は画面にアセットの名前を表示してから、例外を呼び出し元に渡します。
- 取り消しボタンは Race で、読み込みと進捗の表示をまとめて巻き戻します。そのとき `LoadAll` は null を返します。
- 呼び出し元に渡らなかったリクエストは、失敗、取り消し、外からの取り消し（読み込み画面の破棄）のどれでも `finally` で解放します。

## テスト

各場面のテストは、リポジトリの [`tests/unity/Assets/Tests/Samples/`](../../../tests/unity/Assets/Tests/Samples) にあります。フローを結んだ GameObject を `Destroy`、`SetActive(false)`、シーンを閉じる、の 3 通りで終わらせ、後始末が残らないことを確かめています。`EngineFreeSampleTests.cs` は、PlayerLoop なしで、仮想時間の `TestWorld` でサンプルを試す例です（[テスト](../tools/testing.md)）。
