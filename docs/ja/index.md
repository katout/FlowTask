# FlowTask ドキュメント

FlowTask は、ゲームの進行（画面遷移、ダイアログ、キャラクターの行動、演出、チュートリアル）を `async`/`await` で手順どおりに書くための C# ライブラリです。フローの寿命、キャンセル、ゲームの時間、失敗の扱いを、コードの構造（スコープ）で決めます。同じコアを .NET、Unity、Godot で使えます。

```csharp
async FlowTask OpenShop(Shop shop)
{
    using var window = shop.Open();                  // closed when this flow ends, however it ends
    var r = await FlowTask.Race(window.Purchased.Next(), window.Closed.Next());
    if (r.TryGet0(out var item)) await Purchase(item);
}
```

## はじめに

- [FlowTask とは](getting-started/introduction.md)：解く問題、向いている場面、ほかの仕組みとの分担
- [インストール](getting-started/installation.md)：.NET、Unity、Godot
- [最初のフロー](getting-started/first-flow.md)：小さなフローを作りながら、基本の動きを確かめる

## ガイド

- [フローと World](guide/flows-and-world.md)
- [スコープとキャンセル](guide/scopes-and-cancellation.md)
- [合成：Race と WhenAll](guide/composition.md)
- [時間と Clock](guide/time-and-clocks.md)
- [シグナル](guide/signals.md)
- [失敗の扱い](guide/failures.md)
- [スレッド](guide/threads.md)

## エンジン

- Unity：[セットアップ](unity/setup.md)、[GameObject の寿命](unity/lifetime.md)、[ブリッジ](unity/bridges.md)、[サンプル](unity/samples.md)
- Godot：[セットアップ](godot/setup.md)、[ノードの寿命](godot/lifetime.md)、[シグナル](godot/signals.md)、[物理フレーム](godot/physics.md)、[サンプル](godot/samples.md)

## 連携

- [Task と ValueTask](integrations/task.md)
- [UniTask](integrations/unitask.md)（UniTask からの移行を含む）
- [R3](integrations/r3.md)

## テストと診断

- [エンジンなしのテスト](tools/testing.md)
- [デバッグと診断](tools/debugging.md)
- [アナライザのルール](tools/analyzers.md)

## 詳しく

- [実行モデル](advanced/execution-model.md)
- [性能とメモリ](advanced/performance.md)
- [設計の背景](advanced/design-rationale.md)
- [用語集](advanced/glossary.md)
