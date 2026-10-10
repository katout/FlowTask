# GameObject の寿命

フローを GameObject の寿命に結び付ける方法を説明します。GameObject を無効にするか破棄すると、結んだフローがその場で巻き戻ります。フローの中で待つ書き方、プールで使い回すオブジェクトの書き方、破棄だけで止める書き方も扱います。

## RunWhileActive

```csharp
public sealed class Enemy : MonoBehaviour
{
    void Start() => gameObject.RunWhileActive(Patrol()); // canceled by SetActive(false) or Destroy

    async FlowTask Patrol()
    {
        try
        {
            while (true)
            {
                // ...
                await FlowTask.WaitForSeconds(0.5);
            }
        }
        finally
        {
            // runs inside OnDisable: the object is still accessible
        }
    }
}
```

`gameObject.RunWhileActive(task)` はフローを始めて `FlowHandle` を返します（`FlowTask<T>` を渡すと `FlowHandle<T>`）。GameObject がヒエラルキーで無効になると、フローはキャンセルされます。無効になるのは次のときです。

- 自分か親の `SetActive(false)`
- `Destroy`
- シーンのアンロード（シーン遷移）

キャンセルは、FlowTask が GameObject に付ける非表示のコンポーネントの `OnDisable` で行い、その World を **その場で Flush** します。そのため `using`、`finally`、`AddCleanup` は `OnDisable` の中、オブジェクトがまだ使えるうちに走ります。`Destroy` の場合も、Unity がその呼び出しの中で `OnDisable` を呼ぶ（破棄そのものはフレームの終わり）ので、`Destroy` から戻る前に巻き戻ります。

別の Clock で動かすときは、第 2 引数に Clock を渡します（`gameObject.RunWhileActive(Patrol(), clocks.Game)`）。既定の World 以外の Clock を渡すと、その World で動きます（`gameObject.RunWhileActive(task, physicsWorld.DefaultClock)`）。

> **注意**：再び有効にしても、フローは再開しません。コルーチンと同じく無効化で止まり、コルーチンと違って、止まったフローは巻き戻って終わります。もう一度動かすなら、新しく始めます（下の「プールで使い回すオブジェクト」）。

### 無効な GameObject で呼んだとき

`activeInHierarchy` が false の GameObject（自分か親が非アクティブ）で呼ぶと、何も実行しません。渡した task は開始せずに破棄し、返すハンドルは初めから `Canceled` です。

### どこから呼んでもルートのフロー

`RunWhileActive` で始めたフローは、どこから呼んでもルートのフロー（`FlowWorld.Run` で始めたのと同じ）です。寿命は GameObject が持つので、フローのコードの中から呼んでも、呼んだスコープの子にはなりません。呼んだスコープが終わっても止まらず、例外は呼んだフローではなく `OnUnhandledException` へ届きます。

フローの中で結果を待ちたいときや、呼んだスコープの終わりでも止めたいときは、次の `WhileActive` を使います。

## await できる版：WhileActive

```csharp
// Starts when awaited, like every FlowTask; canceled when enemy is deactivated or destroyed
bool completed = await enemy.WhileActive(Attack());

// With a result: (true, result) when it completed, (false, default) when the GameObject was deactivated first
var (finished, score) = await stage.WhileActive(MiniGame());
```

`gameObject.WhileActive(task)` は FlowTask を返し、ほかの FlowTask と同じく await した時点で開始します。GameObject が無効になるか破棄されると task を取り消すのは `RunWhileActive` と同じで、巻き戻しも `OnDisable` の中で走ります。違うのは、await したスコープの中で動くことです。

- 呼んだスコープの子なので、そのスコープが終わると一緒に終わります。
- task の例外は、`OnUnhandledException` ではなく await したところで投げられます。
- 結果のない task には `bool`（完了なら `true`、GameObject が先に無効になったら `false`）、結果のある `FlowTask<T>` には `(bool Completed, T Value)`（完了なら `(true, result)`、先に無効になったら `(false, default)`）を返します。無効化で終わっても例外にはならず、await したフローは続きます。
- 開始のときに GameObject が無効（または破棄済み）なら、task は開始せずに解放され、`false` か `(false, default)` を返します。
- ダンプとスコープのパスには `WhileActive(GameObject 名) > Attack` の形で出ます（[デバッグと診断](../tools/debugging.md)）。

スコープの親子は [スコープとキャンセル](../guide/scopes-and-cancellation.md) にあります。

## その場の巻き戻りに気を付ける

フローの外で GameObject を無効にするか `Destroy` すると、その呼び出しがその World の Flush ポイントにもなります。結んだフローが巻き戻るだけでなく、キューにあるほかのフローの再開も、`SetActive(false)` や `Destroy` の呼び出しの中で走ります（物理のコールバックの中で `Destroy` したときも同じです）。

そのため、リストを列挙しながら無効にしたり `Destroy` したりすると、`finally` がそのリストを変えることがあります（`enemies.Remove(this)` など）。コピーを列挙してください。

```csharp
foreach (var enemy in enemies.ToArray()) // a copy: a finally may remove itself from the list
{
    Destroy(enemy.gameObject);
}
```

### フローのコードの中で無効にしたとき

フローのコードの中で `SetActive(false)` や `Destroy` を呼んだとき（プールへ返すフローなど）も、そのコードを実行している World の結んだフローは、その呼び出しの中で巻き戻ります。別の World で動いているフローは、その World の次の Flush で巻き戻ります。

フローが自分の GameObject を無効にするか `Destroy` すると、そのフローは次の await でキャンセルされます。それまでのコードは走ります。await せずに終わっても、ハンドルは `Canceled` になります。最初の await より前（`RunWhileActive` の中で動く部分）で無効にしたフローは、その await で取り消され、返るハンドルはもう `Canceled` です。ただし、そこで await せずに終わったフローは、結ぶ前に終わっているので `Succeeded` です。

```csharp
async FlowTask Die()
{
    IsDead = true;
    await FlowTask.WaitForSeconds(0.3); // the death animation
    Destroy(gameObject);                // this flow ends Canceled when it returns
}
```

`WhileActive` の task が自分の GameObject を無効にしたときは、その呼び出しの中ではなく、その World が次に再開を処理するときに、task が取り消されて `false` か `(false, default)` が返ります。Tick や Flush の中のコードならその Tick や Flush の続きで、外から呼んだ `FlowWorld.Run` の中のコードなら次の Flush ポイントです。

## シーン遷移

シーンを閉じると、オブジェクトは無効になってから破棄されるので、`Destroy` と同じく巻き戻ります。`DontDestroyOnLoad` に移したオブジェクトのフローは残ります。

画面より長く続く処理（課金の結果を受けるサービスなど）は、シーンを越えて残るオブジェクトに結ぶか、`FlowTaskUnity.World.Run` で始めます。

## プールで使い回すオブジェクト

プールで `SetActive` を切り替えるオブジェクトは、`OnEnable` の 1 行でフローを始めます。プールへ返す（`SetActive(false)`）とフローは巻き戻って終わり、次に取り出す（`SetActive(true)`）と `OnEnable` が新しいフローを始めます。

```csharp
public sealed class PooledBullet : MonoBehaviour
{
    void OnEnable() => gameObject.RunWhileActive(Fly()); // ends when the bullet goes back to the pool

    async FlowTask Fly()
    {
        try
        {
            while (true)
            {
                transform.position += transform.forward * 0.5f;
                await FlowTask.NextFrame();
            }
        }
        finally
        {
            if (TryGetComponent<TrailRenderer>(out var trail)) trail.Clear(); // runs inside OnDisable
        }
    }
}
```

- `Start` で始めたフローは、無効にした後で有効に戻しても動きません。使い回すオブジェクトは `OnEnable` で始めます。
- プールから取り出すフローの中で `SetActive(true)` しても、弾のフローはそのフローの子になりません（ルートのフロー）。取り出したフローが終わっても、弾は飛び続けます。
- `OnDisable` の中で走るのは、`finally` の最初の await までです。await の続きは `OnDisable` の後に走るので、そこからオブジェクトに触れないでください（プールから出し直されているかもしれません）。

> **注意**：結び付くのは GameObject の有効・無効です。コンポーネントの `enabled` を切り替えても、フローは止まりません。ただし、すべての MonoBehaviour を無効にする書き方（`GetComponents<MonoBehaviour>()` を回して `enabled = false`）は FlowTask の非表示のコンポーネントも無効にするので、そのとき動いていたフローは止まります。

コンポーネントの `enabled` でも止めたいときは、`OnEnable` で始めたフローのハンドルを持ち、`OnDisable` で `Cancel()` します。`this.RunWhileActive` がない理由は [設計の背景](../advanced/design-rationale.md) の「エンジンのオブジェクトの寿命」にあります。

```csharp
FlowHandle _aim;

void OnEnable() => _aim = gameObject.RunWhileActive(Aim());
void OnDisable() => _aim.Cancel(); // enabled = false, Destroy(this)
```

- フローの外で `enabled = false` にしたときは、`OnDisable` の中ではなく、次の Flush ポイントか Tick で巻き戻ります（[実行モデル](../advanced/execution-model.md) の「キャンセルの時期」）。
- GameObject を無効にしたときは、上と同じく、その呼び出しの中で巻き戻ります。

## 破棄だけで止める（WaitForDestroy）

無効化では止めず、破棄でだけ止めたいときは、`gameObject.WaitForDestroy()` と Race します。

```csharp
await FlowTask.Race(Work(), gameObject.WaitForDestroy()); // survives SetActive(false), stops on Destroy
```

- `WaitForDestroy()` は、GameObject の破棄で完了する `FlowTask` です。無効化では完了しません。すでに破棄されていれば、すぐに完了します。
- Race は `OnDestroy` の中の Flush で決着するので、負けた `Work()` の `finally` も破棄の前に走ります。
- フローのコードの中で作った `WaitForDestroy()` は、そのコードを動かしている World で待つものです。同じ World で await するか、そこでの Race などに渡してください。
- 一度もアクティブになっていない GameObject は、Unity が `OnDestroy` を呼びません。その `WaitForDestroy()` は、PlayerLoop の Tick ごとに破棄を調べて完了させます（`FlowLifetime.PollWatched()`）。`AutoTick = false` で自分で Tick するときは、`PollWatched()` も自分で呼びます（[セットアップ](setup.md)）。

Race と敗者の巻き戻しは [合成](../guide/composition.md) にあります。

## 再生中の再コンパイルの後

再生中にスクリプトを再コンパイルして既定の World が消えた後は、`FlowTaskUnity.World` と `RunWhileActive` が `No FlowTask World: ...` の例外を投げます。原因と設定は [セットアップ](setup.md) の「再生中の再コンパイル」にあります。

## どれを使うか

| やりたいこと | 書き方 |
| --- | --- |
| `Start` や `OnEnable` で、その GameObject が有効な間だけ動くフローを始める | `gameObject.RunWhileActive(task)` |
| フローの中で、ある GameObject が有効な間だけ動く処理を待つ | `await gameObject.WhileActive(task)` |
| 無効化では止めず、破棄でだけ止める | `FlowTask.Race(task, gameObject.WaitForDestroy())` |
| どの GameObject にも結ばず、シーンを越えて動かす | `FlowTaskUnity.World.Run(task)` |

## Godot との対応

`RunWhileActive` と `WhileActive` は、Godot の `RunWhileInTree` と `WhileInTree` にあたります。Godot がツリーの外のノードで何も実行しないのと同じく、無効な GameObject では何も実行しません（[Godot のノードの寿命](../godot/lifetime.md)）。

`RunWhileActive` のハンドルは task の結果と状態をそのまま返し、無効化で終わったときは `Canceled` です。Godot の `RunWhileInTree` は `FlowHandle<bool>` を返し、ツリーを出たときも `Succeeded`（結果は `false`）です。
