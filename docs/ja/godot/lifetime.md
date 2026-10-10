# ノードの寿命

Godot のノードとフローの寿命は、2 つの向きで結べます。スコープがノードを持つ（スコープが終わるとノードを解放する）のが `NodeLifetime.Own`、ノードがフローを持つ（ノードがツリーを出るとフローを止める）のが `RunWhileInTree` と `NodeLifetime.WhileInTree` です。

## スコープがノードを持つ：`NodeLifetime.Own`

```csharp
async FlowTask ShowToast(string text)
{
    var toast = NodeLifetime.Own(ToastScene.Instantiate<Label>());
    toast.Text = text;
    AddChild(toast);
    await FlowTask.WaitForSeconds(2.0);
}   // toast is QueueFree'd whether the flow completes or is canceled
```

`NodeLifetime.Own(node)` は、今のスコープが終わるときにノードを `QueueFree()` します。完了でも、キャンセルでも、例外でも同じです。

- 渡したノードをそのまま返すので、作る式を包んで書けます。
- 解放は `Flow.AddCleanup` や `Flow.Own` と同じ後始末の列に入り、登録と逆の順（LIFO）で走ります（[スコープとキャンセル](../guide/scopes-and-cancellation.md)）。
- フローの外で呼んだときは、`Flow.Own` と同じく何も持たず、ノードは呼んだ側が解放します。
- 解放のときにノードがすでに解放済みか、削除の予約済みなら何もしません。

手順ごとにスコープを分けると、手順が終わるたびにその手順のノードが消えます。チュートリアルの吹き出しをこの形で書いた例が [サンプル](samples.md) にあります。

## ノードがフローを持つ：`RunWhileInTree`

```csharp
public partial class Enemy : Node2D
{
    public override void _Ready()
    {
        this.RunWhileInTree(EnemyAI());   // returns a FlowHandle<bool>
    }
}
```

`owner.RunWhileInTree(task, clock)` は、task をすぐに開始し、owner がツリーを出たら取り消します。

- task は `clock`（省略すると `FlowWorldNode.Default` の `DefaultClock`）で、その Clock の World のルートのフローとして動きます。ゲームの Clock で動かすなら `this.RunWhileInTree(EnemyAI(), clocks.Game)` と書きます。物理用の World の Clock を渡すと、その World で動きます（`this.RunWhileInTree(Dash(body), FlowAutoload.Physics.DefaultClock)`。[物理フレームで動かす](physics.md)）。
- 寿命はノードが持ちます。フローのコードの中から呼んでも、呼んだスコープの子にはならず、そのスコープが終わっても止まりません。
- task の例外は、呼んだフローではなく `OnUnhandledException`（既定では Godot のログ）へ届きます。
- ハンドルの結果は、task が完了すれば `true`、owner が先にツリーを出れば `false` です。どちらの場合も、ハンドルの状態は成功（`Succeeded`）です。
- 開始のときに owner がツリーにいない（または解放済み）なら、task は開始せずに解放され、結果は `false` です。

## await できる版：`NodeLifetime.WhileInTree`

```csharp
// Starts when awaited, like every FlowTask; canceled when enemy leaves the tree
bool completed = await NodeLifetime.WhileInTree(enemy, EnemyAI(enemy));

// With a result: (true, result) when it completed, (false, default) when the owner left first
var (finished, score) = await NodeLifetime.WhileInTree(this, MiniGame());
```

`WhileInTree` は FlowTask を返し、ほかの FlowTask と同じく await した時点で開始します。await したスコープの中で動くので、次のときに使います。

- フローの中で結果を待ちたいとき。
- ノードがツリーを出たときだけでなく、呼んだスコープが終わったときにも止めたいとき。

結果のない task には `bool`（完了なら `true`）、結果のある `FlowTask<T>` には `(bool Completed, T Value)`（完了なら `(true, result)`、owner が先にツリーを出たら `(false, default)`）を返します。開始のときに owner がツリーにいなければ、task は開始せずに解放され、`false` か `(false, default)` を返します。

## 巻き戻しはツリーにいるうちに走る

ノードの `QueueFree()` やシーンの切り替えでノードがツリーを出ると、そのノードに結び付いたフローは、ノードがまだツリーにあるうちに巻き戻ります。`finally`、`using`、`AddCleanup` の中からノードの親や兄弟に触れます。

```csharp
async FlowTask EnemyAI()
{
    try
    {
        while (true) { /* patrol */ await FlowTask.NextFrame(); }
    }
    finally
    {
        // Runs inside tree_exiting: GetParent() and GetTree() still work here
        GetParent().GetNode<Hud>("Hud").RemoveMarker(this);
    }
}
```

- `tree_exiting` を受けた時点で、フローの World が実行中でなければ（`FlowWorld.IsExecuting` が false なら）、その場で `FlowWorld.Flush()` します。そのため巻き戻しは、Node がまだツリーにある同じフレームの中で走ります。
- フロー自身が `RemoveChild` や `Free` を呼んだときなど、World の Flush の最中に `tree_exiting` が来た場合は、同じ Flush の中で巻き戻ります。このときは呼び出しから戻った後なので、Node はもうツリーの外です。

## ダンプでの見え方

`RunWhileInTree` と `WhileInTree` で動くフローは、ダンプとスコープのパスで `WhileInTree(ノード名) > EnemyAI` の形で出ます（[デバッグと診断](../tools/debugging.md)）。

## どれを使うか

| やりたいこと | 書き方 |
| --- | --- |
| フローの中で作ったノードを、フローの終わりに消す | `NodeLifetime.Own(node)` |
| ノードの `_Ready` で、そのノードが生きている間だけ動くフローを始める | `this.RunWhileInTree(task)` |
| フローの中で、あるノードが生きている間だけ動く処理を待つ | `await NodeLifetime.WhileInTree(node, task)` |
