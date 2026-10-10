[English](README.md) | 日本語

# FlowTask

[![NuGet](https://img.shields.io/nuget/vpre/FlowTask)](https://www.nuget.org/packages/FlowTask)
[![License: MIT](https://img.shields.io/github/license/katout/FlowTask)](LICENSE)

ゲームの進行（画面遷移、ダイアログ、キャラクターの行動、演出、チュートリアル）を、`async`/`await` で手順どおりに書くための C# ライブラリです。フローの寿命、キャンセル、ゲームの時間、失敗の扱いを、コードの構造（スコープ）で決めます。同じコアを .NET、Unity、Godot で使えます。

**[ドキュメント](https://katout.github.io/FlowTask/ja/)**

> FlowTask はプレビュー版（0.x）です。1.0 までは、版ごとに API が変わることがあります（[CHANGELOG](CHANGELOG.md)）。

```csharp
using Katout.FlowTask;

async FlowTask EnemyAI(Enemy self)
{
    // Keeps only the latest hit while nobody waits.
    using var hits = self.Damaged.Subscribe(BufferPolicy.Latest);
    while (true)
    {
        // The loser of the race is unwound: its using and finally blocks run.
        var r = await FlowTask.Race(hits.Next(), Patrol(self));
        if (r.TryGet0(out var hit)) await HitStun(self, hit);
    }
}

// Start the flow on a World (one of these, depending on the engine):
FlowTaskUnity.World.Run(EnemyAI(enemy));        // Unity: the World that the PlayerLoop ticks
FlowWorldNode.Default.Run(EnemyAI(enemy));      // Godot: the World that FlowWorldNode ticks
var world = new FlowWorld();                    // .NET: your own World ...
world.Run(EnemyAI(enemy));
world.Tick(deltaTime);                          // ... ticked once per frame
```

## 特徴

- **構造化並行性**：FlowTask は、開始した文脈のスコープの子になります。親が終わると子孫はすべて巻き戻るので、CancellationToken を手で配る必要はありません。
- **巻き戻しによるキャンセル**：キャンセルされたフローでは `using` と `finally` がそのまま走ります。`finally` の中の await も最後まで走ります。
- **合成**：`FlowTask.Race`（敗者を巻き戻してから続きを再開）、`FlowTask.WhenAll`、`Flow.Spawn`。
- **決定論的な実行順**：再開はすべて 1 本のキューで、決まった順に処理されます。
- **ゲームの時間**：Clock ごとの Pause とタイムスケール。ポーズ中も UI だけを動かせます。
- **シグナル**：`Signal<T>`、受信側がバッファの方針を決める購読、`FlowProperty<T>`。
- **定常状態で割り当てゼロ**：状態機械とノードをプールします。NativeAOT と IL2CPP でも確かめています。
- **アナライザ**：よくある誤用をコンパイル時に知らせ、コード修正を付けます（メッセージは日本語と英語）。
- **エンジンなしのテスト**：World を仮想時間で進める拡張メソッド（`TickFor`、`RunUntilComplete`）、例外の報告を記録する `TestWorld`、NUnit のアダプタ。
- **ブリッジ**：Task / ValueTask、UniTask、R3、Unity（AsyncOperation、Awaitable、UnityEvent）、Godot のシグナルを、フローから待てます。`FlowBridge.FromTask` がファクトリに渡すトークンは、スコープが終わると取り消されます。

## インストール

| 環境 | 導入 |
|---|---|
| .NET（netstandard2.1 / net10.0） | `dotnet add package FlowTask --prerelease` |
| Unity 2023.1 以降 | Package Manager で git URL を追加（下記） |
| Godot 4.4.1 以降の .NET 版 | `dotnet add package FlowTask.Godot --prerelease` |

Unity では、`Packages/manifest.json` に本体と Unity の統合の両方を書きます。

```json
{
  "dependencies": {
    "com.katout.flowtask": "https://github.com/katout/FlowTask.git?path=src/FlowTask",
    "com.katout.flowtask.unity": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Unity"
  }
}
```

任意のパッケージ（テスト、UniTask、R3）と、エンジンごとの最初の設定は[インストール](docs/ja/getting-started/installation.md)にあります。

## ドキュメント

- [FlowTask とは](docs/ja/getting-started/introduction.md)
- [最初のフロー](docs/ja/getting-started/first-flow.md)
- [ガイド](docs/ja/index.md#ガイド)
- [Unity](docs/ja/unity/setup.md) / [Godot](docs/ja/godot/setup.md)
- サンプル（[Unity](docs/ja/unity/samples.md) / [Godot](docs/ja/godot/samples.md)）：確認ダイアログ、敵 AI、ポーズなどの場面と、それを試すデモ
- [UniTask からの移行](docs/ja/integrations/unitask.md)

同じ内容を[ドキュメントサイト](https://katout.github.io/FlowTask/ja/)でも読めます。

## パッケージ

| パッケージ | 配布 | 内容 |
|---|---|---|
| FlowTask | NuGet、UPM `com.katout.flowtask` | コアとアナライザ |
| FlowTask.Unity | UPM `com.katout.flowtask.unity` | PlayerLoop への組み込み、Unity のブリッジ、GameObject の寿命、Scope Tree ウィンドウ |
| FlowTask.Godot | NuGet | `_Process` への組み込み、Godot のシグナルのブリッジ、ノードの寿命 |
| FlowTask.UniTask | NuGet、UPM `com.katout.flowtask.unitask` | UniTask との相互変換 |
| FlowTask.R3 | NuGet | R3 の Observable と Signal の相互変換 |
| FlowTask.Testing | NuGet、UPM `com.katout.flowtask.testing` | 仮想時間のテスト |
| FlowTask.Testing.NUnit | NuGet、UPM `com.katout.flowtask.testing.nunit` | 未処理の例外をテストの失敗にするアダプタ |

## 貢献

不具合の報告と提案は [Issues](https://github.com/katout/FlowTask/issues) へお願いします。開発の手順は [CONTRIBUTING.ja.md](CONTRIBUTING.ja.md) にあります。脆弱性は公開の Issue にせず、[SECURITY.md](SECURITY.md) の手順で報告してください。

## ライセンス

[MIT](LICENSE)
