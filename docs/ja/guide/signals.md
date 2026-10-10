# シグナルと共有の状態

フローどうしやゲームのコードとフローの間で、出来事と値を受け渡す型を説明します。出来事の通知は `Signal<T>`、取りこぼしたくない出来事は購読（`Subscription<T>`）、変わる値は `FlowProperty<T>`、一度だけ決まる値は `Once<T>` で表します。寿命付きの優先順位は、これらと `Flow.Own` で組み立てます。

## Signal：出来事を待つ

`Signal<T>` は状態を持たない通知です。`Emit` で送り、フローは `Next()` で次の Emit を待ちます。

```csharp
var jump = new Signal<FlowUnit>("Jump");

async FlowTask Tutorial()
{
    ShowHint("Press A to jump");
    await jump.Next();          // waits for the next Emit
    HideHint();
}

// in the input code
if (pad.A.WasPressed) jump.Emit(FlowUnit.Default);
```

- 値のない通知には `Signal<FlowUnit>` を使い、`Emit(FlowUnit.Default)` で送ります。
- Emit は、待っていたフローをその場では再開しません。再開は予約され、次のフラッシュ（Tick か Flush）で処理されます。フローの中から Emit した場合も、Emit した側が先に続きます。
- 名前（`new Signal<int>("Hits")`）を付けると、ダンプに `Hits.Next` のように出ます（[デバッグ](../tools/debugging.md)）。

### Next はエッジ

`Next()` は、待ち始めた後の次の Emit だけを受け取ります。待っていない間の Emit は捨てられます。

```csharp
while (true)
{
    var damage = await damaged.Next();
    await FlowTask.WaitForSeconds(0.8);   // hits during this knockback are lost
}
```

ループの本体が別のことをしている間と、待ち始める前（画面を開く演出の間など）の Emit は、警告なしに届きません。UniTask の `button.OnClickAsync()` と同じ扱いです。購入中の連打のように、捨ててよい入力なら `Next()` のままで構いません。取りこぼしたくない入力は、次の購読で待ちます。アナライザの FLOW006 が、ループの中の `Next()` に購読を提案します（[アナライザ](../tools/analyzers.md)）。

## 購読：取りこぼさずに待つ

`Subscribe(BufferPolicy)` は、その時点からの Emit をバッファにためる購読を作ります。フローは購読の `Next()` で 1 つずつ取り出します。

```csharp
async FlowTask EnemyAI(Enemy self)
{
    using var hits = self.Damaged.Subscribe(BufferPolicy.Latest);   // keeps the newest hit
    while (true)
    {
        var r = await FlowTask.Race(hits.Next(), Patrol(self));
        if (r.TryGet0(out var hit)) await HitStun(self, hit);
    }
}
```

| 方針 | 動き |
|---|---|
| `BufferPolicy.Latest` | 最新の 1 つだけを保つ |
| `BufferPolicy.Queue(容量, BufferOverflow.DropOldest)` | 順に保ち、満杯なら最も古いものを捨てる |
| `BufferPolicy.Queue(容量, BufferOverflow.DropNewest)` | 順に保ち、満杯なら新しく来たものを捨てる |
| `BufferPolicy.Queue(容量, BufferOverflow.Fail)` | 順に保ち、あふれたら購読を持つスコープを `SubscriptionOverflowException` で終える |

- 購読は、作ったスコープが所有し、スコープの終わりに終わります。早く終えるなら `using` を付けます。フローの外で作った購読は誰も所有しないので、自分で Dispose します。
- `Queue` の容量は上限で、領域は中身に合わせて伸びます。
- 待っているフローがいれば、値はバッファを通さずに渡ります。
- `TryTake(out v)` は待たずに 1 つ取り出し、`Count` はたまっている数を返します。
- 拾いたい入力は、待ち始めるより前（画面を開くとき、演出の前）に Subscribe します。
- Race に負けた購読の待ちが受け取っていた値は、購読の先頭に戻ります（[合成](composition.md)）。
- `BufferOverflow.Fail` の購読は、フローの中で作ります。フローの外では `Subscribe` が `FlowMisuseException` を投げます。

> **注意**：購読は値型です。コピーした購読を Dispose すると、元の購読も終わります。終わった購読の `Next()` は `FlowMisuseException` で失敗します。購読は作った場所で 1 回だけ Dispose します。

## 閉じる

送り手がもう Emit しないときは、Signal を閉じます。

```csharp
var lobby = new Signal<Player>("Lobby");

// the producer
lobby.Close();                       // or lobby.Close(error) when it failed

// a consumer
while (true)
{
    var (received, player) = await lobby.NextOrClosed();
    if (!received) break;            // closed
    Greet(player);
}
```

- 閉じた Signal の `Next()` は `SignalClosedException` で失敗します。`Close(error)` にエラーを渡すと、それが `InnerException` に入ります。
- `NextOrClosed()` は `(bool Received, T Value)` を返します。値が届けば `(true, 値)`、閉じたら例外の代わりに `(false, default)` です。
- 購読は、たまった値を渡し終えてから閉じます。
- 閉じた Signal への `Emit` は `FlowMisuseException` です。2 回目の `Close` は何もしません。

## イベントを Signal にする：FromCallback

C# のイベントやコールバックは、`FlowBridge.FromCallback` で Signal にして待ちます。

```csharp
async FlowTask Menu()
{
    using var clicks = FlowBridge.FromCallback<FlowUnit>(emit =>
    {
        Action h = () => emit(FlowUnit.Default);
        shopButton.Clicked += h;
        return () => shopButton.Clicked -= h;   // detached when Menu ends
    });
    var r = await FlowTask.Race(clicks.Next(), closed.Next());
    if (r.Index == 0) await OpenShop();
}
```

- 作ったスコープが所有し、スコープの終わりか Dispose でイベントから外れ、閉じます。その後に届いたコールバックはどこにも届きません。画面より後に届くコールバック（広告の報酬、課金）は、画面より長く生きるフロー（`FlowWorld.Run` で始めたもの）の中で受けます。
- `Next()`、`NextOrClosed()`、`Subscribe` は Signal と同じです。
- フローの中で作ったものは、別のスレッドから呼ばれるコールバックも受けられます（[スレッド](threads.md)）。
- イベントハンドラの中で `Flow.Spawn` を呼ぶのではなく、この形で待ちます（[フローと World](flows-and-world.md)）。
- Unity と Godot のイベントのブリッジは [Unity のブリッジ](../unity/bridges.md) と [Godot のシグナル](../godot/signals.md) にあります。

## FlowProperty：変わる値を待つ

`FlowProperty<T>` は値を持ち、変わると通知します。条件が成り立つまで待てます。

```csharp
var hp = new FlowProperty<int>(100);

async FlowTask WatchHealth()
{
    await hp.WaitUntil(x => x <= 0);   // completes at once if it already holds
    await GameOver();
}

// elsewhere
hp.Set(hp.Value - damage);
```

- `Value` は最後に `Set` した値です。`Set` は同じ値（既定では `EqualityComparer<T>.Default`、コンストラクタで比較器を指定できる）なら何もしません。
- `WaitUntil` は、すでに条件が成り立っていればその場で終わります。成り立っていなければ `Set` のたびに条件を評価し、成り立った値を返します。同じフレームに `Set(5); Set(0); Set(3);` と続いても、`x <= 0` の待ちは 0 を受け取ります。
- `Changed` は、変わるたびに新しい値を Emit する Signal です。変化をすべて受けるなら購読します。`Changed` を `Close` すると、それ以降は何も Emit しません。FlowProperty はそのまま使えます。
- 条件の中から `Set` を呼ぶと、値はすぐ変わり、通知は今の通知が終わってから `Set` した順に行われます。
- 条件が投げた例外は、その待ちの await で投げられます。

## Once：一度だけ決まる値

`Once<T>` は一度だけ値が入り、何度でも、いくつのフローからでも待てます。

```csharp
var connected = new Once<Session>();

async FlowTask Chat()
{
    var session = await connected;   // or connected.Wait(); completes at once once it is set
    await RunChat(session);
}

// when the connection is ready
connected.Set(session);
```

- 2 回目の `Set` は `FlowMisuseException` です。
- `IsSet`、`Value`、`TryGetValue` で、待たずに読めます。
- FlowTask は 1 回しか await できないので、複数の待ち手が同じ結果を待つときに使います。
- Once には失敗やキャンセルの状態がありません。`Set` する前に処理が失敗するか取り消されると、待ち手は待ち続けます。失敗しうる結果（読み込み、通信）を配るなら、成否を表す値を入れて失敗やキャンセルのときも `Set` するか、処理のハンドルを配ります。ハンドルの待ち手は `await FlowTask.WaitUntil(h, x => x.IsCompleted)` で待ってから、`Status`、`Result`、`Exception` を読みます。

## 寿命付きの優先順位を組み立てる

戻るキーを誰が受けるか、入力の遮断のように、「今いちばん上にいるのは誰か」を表すものは、リストとシグナルで組み立てます（専用の型はありません）。積んだ要素は `Flow.Own` で今のスコープに持たせます。スコープが終わると要素は Dispose されて外れ、`using` を付ければもっと早く外れます。フローの外では `Flow.Own` は何も持たないので、フローの外で積んだ要素は自分で Dispose します。`Flow.Own` で持たせたものは、早く Dispose してもスコープが終わるまでその後始末の一覧に残ります。長く続くスコープのループで何度も積むときは、積んで待つところをメソッドに分け、そのメソッドのスコープに持たせます（サンプルの `BackKeyRouter.Next` がこの形です）。

層（ダイアログは画面より上）と、キーを飲み込む `Block()` まで入れた戻るキーのルーターは、サンプルの `BackKeyRouter` にあります（[Unity のサンプル](../unity/samples.md)、[Godot のサンプル](../godot/samples.md)）。

## セッションごとに作って渡す

`Signal`、`FlowProperty`、`Once` は、static のフィールドに置かずに、World（ゲームのセッション）ごとに作って、使うフローに引数で渡します。

```csharp
sealed class GameSession
{
    public readonly Signal<int> Damaged = new("Damaged");
    public readonly FlowProperty<int> Hp = new(100);
}

var session = new GameSession();
world.Run(InGame(session));
```

- これらのオブジェクトは、最初に使った World とそのスレッドに結び付きます（束縛）。別のスレッドからの書き込みは `FlowThreadException` になります（[スレッド](threads.md)）。
- 結び付いた World を Dispose した後で、別の World からそのオブジェクトを使うと `FlowMisuseException` になります。Unity でドメインリロードを無効にしたとき、static に置いた Signal は 2 回目の再生でこの例外になります。

## 規則と落とし穴

- 同じフレームに 2 つのボタンが押されても、`Race(ok.Next(), cancel.Next())` が受け取るのは先に Emit された 1 つだけです。もう一方は（エッジなので）捨てられます。
- フローの外の `Emit` と `Set` は、World のスレッドから呼びます。別のスレッドからは `EmitFromAnyThread` か `FlowWorld.Post` を使います（[スレッド](threads.md)）。
- 別の World のフローが送る Signal を待つときは、送り手の World が先に Dispose されると待ちが終わりません。送り手は後始末で Signal を `Close` し、受け手は `NextOrClosed()` で待ちます。
- R3 の Observable との変換は [R3](../integrations/r3.md) にあります。
