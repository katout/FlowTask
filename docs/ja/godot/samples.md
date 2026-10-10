# Godot のサンプル

ゲームでよくある場面を FlowTask で書いたサンプルと、それを試すデモのシーンです。写して、自分のゲームに合わせて変えて使ってください。Unity にも同じ構成のサンプルがあります（[Unity のサンプル](../unity/samples.md)）。

## 開き方

1. リポジトリを取得し、Godot 4.4.1 以降の .NET 版で [`samples/godot/`](../../../samples/godot) を開きます。
2. 実行（F5）を押します。メインシーンは `Demo/Demo.tscn` です。

- 左のボタンで場面を選びます。Escape（`ui_cancel`）が戻るキーです。
- 右上の Pause でゲームを止めると、敵は止まり、メニューと左のボタンは動き続けます。止めている間の Hit は、再開したときに届きます。
- このプロジェクトは、リポジトリの FlowTask をソースから参照します（`FlowTaskSamples.csproj` の ProjectReference）。ゲームに写すときは、NuGet の `FlowTask.Godot` を入れます（[インストール](../getting-started/installation.md)）。
- UI はシーンファイルを使わず、コードで組んでいます。ゲームではシーンにするところです。
- ストア、リソースの読み込み、設定の保存先は、デモ用の偽物です（`Demo/DemoServices.cs`）。

## 場面

| 場面 | フォルダ | 示すこと |
| --- | --- | --- |
| 戻るキー | `BackKey/` | 層付きのリスト、`Flow.Own`、`Signal` の合成。一番上の層で待つフローに届け、`Block()` で飲み込む |
| 確認ダイアログ | `ConfirmDialog/` | ボタン、戻るキー、時間切れの Race。閉じるアニメーション（Tween）を `finally` で待つ |
| 敵 AI | `Enemy/` | `RunWhileInTree` とゲームの Clock、被弾の購読、`FlowProperty.WaitUntil` |
| チュートリアル | `Tutorial/` | 手順ごとのスコープが `NodeLifetime.Own` で吹き出しを持つ、スキップの Race |
| 購入 | `Shop/` | Task のストアのブリッジ、想定した失敗を専用の例外型にする、再試行、遅れて届いたレシート |
| ポーズ | `Pause/` | `Pause()` の参照数、UI の Clock、閉じるときに時間の上限付きで保存する、ツリーのポーズを合わせる |
| 並列読み込み | `Loading/` | `WhenAll`、進捗、取り消し、スレッドの読み込みを止めずに手放す |

`GameClocks.cs` は、ほかの場面が使う Clock の組です。敵やタイマーは `Game`（`DefaultClock` の子で、`Engine.TimeScale` に従う）で、メニューやダイアログは `Ui`（`UnscaledClock` の子）で動かすので、ゲームを止めても UI は動き続けます。`GameClocks` と `BackKeyRouter` は World（セッション）ごとに 1 つ作ってフローに渡し、static なフィールドには置きません。デモは `GameClocks.ForCurrentScope()` で Clock を作ります。デモのフローが持つので、デモが終わると World から外れ、デモのシーンを読み直しても Clock は増えません。

ノードとフローの寿命は、`NodeLifetime.Own`（スコープがノードを持つ）と、`RunWhileInTree` / `WhileInTree`（ノードがフローを持つ）で結んでいます（[ノードの寿命](lifetime.md)）。詳しい説明は各ファイルのコメントにあります。以下は Godot に固有の要点です。場面の考え方は Unity のサンプルと同じです。

## 戻るキー

- `BackKeyInput` は `_UnhandledInput` で `ui_cancel` を読むので、フォーカスのあるコントロールが先にキーを使えば、そちらが優先されます。押下を待っていたフローは、同じフレームのうちに再開します。
- Android の戻るボタン（`NotificationWMGoBackRequest`）も渡します。そのためには、プロジェクト設定の `application/config/quit_on_go_back` を false にします。
- ツリーのポーズ中もポーズメニューを戻るキーで閉じられるよう、`ProcessMode` を `Always` にしています。

## 確認ダイアログ

- ダイアログのノードは `NodeLifetime.Own` でフローが持ち、フローが終わると解放されます。ボタンは `PressedSignal()` でブリッジします。
- 閉じるアニメーションの Tween は、`SetIgnoreTimeScale()` と `TweenPauseMode.Process` で倍率とツリーのポーズに影響されません。ノードが解放されると Tween は `finished` を出さずに消えるので、`FlowTask.WaitUntil(tween, t => !t.IsValid())` で待ちます。
- 判定の部分 `Ask` はノードではなく素の `Signal<FlowUnit>` を受け取るので、ノードなしでテストできます（下のテスト）。

## ポーズ

Clock の `Pause()` が止めるのは、その Clock で動くフローだけです。物理、AnimationPlayer、`_process` も止めるには、ツリーのポーズ（`GetTree().Paused`）を使います。ただし、Clock のポーズは参照数で、ツリーのポーズは 1 つの真偽値です。そこで `PauseLink` だけがツリーのポーズを書き、毎フレーム `Game` の Clock に合わせます。

- 画面は `clocks.Game.Pause()` を取るだけです。誰が Pause しても（メニュー、設定画面、カットシーン）ツリーは止まり、最後の Pause が外れたときに動き出します。
- `PauseLink` はセッションに 1 つ、ツリーに残るノードに置きます。ほかの場所で `GetTree().Paused` を書きません。
- `FlowWorldNode` はツリーのポーズ中も Tick するので、UI の Clock のフローは動き続けます。メニューのノードは `ProcessMode = Always` にします（[セットアップ](setup.md)）。

## 並列読み込み

- `ResourceLoader.LoadThreadedRequest` で読み込みを始め、`FlowTask.WaitUntil(request, r => r.IsDone)` で終わりを待ちます。リソースを読み込めないと、その読み込みは `ResourceLoadException` を投げ、`FlowTask.WhenAll` は最初の例外でほかの読み込みを巻き戻してから、その例外を投げ直します。取り消しボタンを押したときは、`LoadAll` は null を返します。
- スレッドの読み込みは止められず、結果を受け取ること（`LoadThreadedGet`）で手放します。ただし `LoadThreadedGet` は読み込みが終わるまでメインスレッドを止めます。そこで `ThreadedResourceLoader` は、読み込み中に解放されたリクエストを自分のルートのフローで終わりまで待ってから受け取ります。取り消しボタンや画面の解放でゲームが固まりません。

## テスト

[`tests/godot/Samples/`](../../../tests/godot/Samples) が、擬似入力でサンプルを動かして結果を確かめます（`tools/godot/run-smoke.ps1`）。`ConfirmDialogAskTests.cs` は、確認ダイアログの判定を、Godot のノードなしで、仮想時間の `TestWorld` で試す NUnit のテストです（[テスト](../tools/testing.md)）。
