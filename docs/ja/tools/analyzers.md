# アナライザのルール

FlowTask には、実行モデル（スコープ、キャンセルの巻き戻し、遅延実行）に反するコードをコンパイル時に見つける Roslyn アナライザが付いています。このページでは、各ルールが見つけるもの、その理由、直し方を説明します。

| ID | 既定 | 検出内容 | コード修正 |
|----|------|----------|-----------|
| [FLOW001](#flow001) | エラー | `FlowCanceledException` を受け取って止めてしまう `catch` | あり |
| [FLOW002](#flow002) | エラー | FlowTask メソッドで `Task` などの外部の awaitable を直接 `await` している | あり |
| [FLOW003](#flow003) | 警告 | FlowTask を作ったまま `await` も開始もしていない | あり |
| [FLOW004](#flow004) | 警告 | ライフタイムハンドルを保持していない、または使っていない | あり |
| [FLOW005](#flow005) | エラー | FlowTask を返さない async 関数での FlowTask の `await` | なし |
| [FLOW006](#flow006) | 情報 | ループの中でシグナルの `Next()` を待っている | あり |
| [FLOW007](#flow007) | 警告 | FlowTask の `GetAwaiter()` をコードで呼んでいる | なし |
| [FLOW008](#flow008) | 警告 | 戻り値を使わない `Flow.Spawn(...)` | あり |
| [FLOW009](#flow009) | 警告 | FlowTask メソッドの中のイベントハンドラでの `Flow.Spawn` | なし |
| [FLOW010](#flow010) | 警告 | `finally`（と例外を渡す `catch`）の中の、`Flow.NonCancelable` のない `await` | あり |

書いたとおりに動かないことが確かなものは**エラー**、たいていは誤りだが意図どおりのこともあるものは**警告**、書き方の提案は**情報**です。重大度は変えられます（[重大度の変更](#重大度の変更)）。カテゴリはすべて `FlowTask` です。IDE の診断のヘルプのリンクは、このページの英語版の各節を指します。

## 導入と表示言語

- **.NET / Godot（NuGet）**：`FlowTask` パッケージにアナライザとコード修正が入っています。参照するだけで有効になります。
- **Unity**：UPM パッケージ `com.katout.flowtask` の `Analyzers/FlowTask.Analyzers.dll` に入っていて、アセンブリ FlowTask を参照するすべてのアセンブリのコンパイルに効きます。コード修正（IDE だけの機能）は入っていません。エラーのルールは Unity のコンパイルも止めます。情報のルール（FLOW006）は Unity のコンソールに出ません。

アナライザは Roslyn 3.8 以降のコンパイラ（.NET 5 SDK 以降）で読み込まれます。

メッセージとコード修正の名前は、英語と日本語で用意しています。IDE やコンパイラの表示言語が日本語なら日本語で、それ以外では英語で出ます。`dotnet build` は OS の表示言語に従い、環境変数 `DOTNET_CLI_UI_LANGUAGE=en`（または `ja`）で切り替えられます。Unity のコンソールと、Unity のプロジェクトを開いた IDE では英語です。

## このページの用語

- **FlowTask メソッド**：戻り値が `FlowTask` / `FlowTask<T>` の async メソッド、ローカル関数、ラムダ。本体の中に書いた別のラムダやローカル関数は、別の関数として扱います。
- **FlowTask の awaitable**：awaiter が `FlowTask.Awaiter` / `FlowTask<T>.Awaiter` のもの（`FlowTask`、`Once<T>`、`TaskBridge` と、FlowTask の awaiter を返す `GetAwaiter` 拡張を持つ型）。await すると今のスコープに登録され、キャンセルで巻き戻せます。

---

<a id="flow001"></a>
## FLOW001: catch で FlowCanceledException を止めない（エラー）

### 検出するもの

FlowTask メソッドの `catch` のうち、`FlowCanceledException` を受け取れるもの（try ブロックに `await` があり、前の節が先に受け取らず、`when` がキャンセルを除外しないもの）で、次に当たるもの。

- `catch (FlowCanceledException)` か `catch (OperationCanceledException)` で、`throw` で終わらない経路がある（`return`、`break`、`continue`、節の終わりまで進んで抜ける）。
- すべてを捕まえる節（型なしの `catch`、`catch (Exception)`、`catch (SystemException)`、`catch (TEx)`）で、`FlowCanceledException` を除外するフィルタがない。`throw;` で終わる節も、本体がキャンセルのたびに走るので対象です。

`catch` と `finally` の中の try も同じです。そこでの `await` も、`FlowWorld.Dispose` がフローを終えるときは `FlowCanceledException` を投げます。

次の形は対象外です。

| 形 | 理由 |
|---|---|
| `catch (FlowCanceledException) { 後始末; throw; }` | キャンセルを通している |
| `catch (Exception e) when (e is not FlowCanceledException)` | 推奨する形 |
| `catch (Exception e) when (e is not OperationCanceledException)` | キャンセルを通す（外部のキャンセルも素通りする） |
| `catch (OperationCanceledException e) when (e is not FlowCanceledException)` | 外部のキャンセルだけを受け取る |

フィルタの中のメソッド呼び出しなど、値の分からない式は「受け取れる」とみなします（`when (e is not FlowCanceledException && Log(e))` は対象外、`when (Log(e))` は対象）。

### 理由

`FlowCanceledException` は、キャンセルされたスコープを巻き戻すために `await` が投げる例外です。これを受け取って先へ進むと、スコープが動き続けます。その後の `await` は生きているスコープと同じく待つので、ループなら回り続けます。コードが戻ると、握りつぶし（`FlowExceptionKind.SwallowedCancellation`）として実行時に報告され、スコープはキャンセルで終わります。それでも、`catch` の本体（エラー表示、リトライ、セーブ）はキャンセルのたびに走ります。

フィルタは `when (e is not FlowCanceledException)` を勧めます。外部のキャンセル（HttpClient のタイムアウトなど、フローの外でキャンセルされた Task の `OperationCanceledException`）は、ほかの例外と同じくハンドラに入ります。`catch (TaskCanceledException)` は、UniTask や `ThrowIfCancellationRequested` の `OperationCanceledException` を受け取らない点に注意してください。

キャンセルに合わせた後始末は `finally` に書きます。キャンセルで入った `finally` の `await` は最後まで走ります（キャンセルの前に入った `finally` の `await` は [FLOW010](#flow010) を見てください）。途中でやめて別の処理に切り替えるなら `FlowTask.Race` で書きます。

### 悪い例

```csharp
async FlowTask<int> Download()
{
    try { return await FetchCount(); }
    catch (Exception e) // also catches FlowCanceledException
    {
        ShowError(e);
        return -1;
    }
}

async FlowTask NewsPanel()
{
    try { await ShowNews(); }
    catch (OperationCanceledException) // the UniTask habit: also catches FlowCanceledException
    {
        HidePanel();
    }
}
```

### 修正例

```csharp
async FlowTask<int> Download()
{
    try { return await FetchCount(); }
    catch (Exception e) when (e is not FlowCanceledException)
    {
        ShowError(e);
        return -1;
    }
}

async FlowTask NewsPanel()
{
    try { await ShowNews(); }
    finally { HidePanel(); } // runs when the panel is canceled, too
}
```

### コード修正

- キャンセルの型の節：「catch 節を 'throw;' で終える」。本体が `return` だけか空なら、「catch 節を消す（キャンセルはそのまま伝わる）」も出します。
- `catch (OperationCanceledException)` と、すべてを捕まえる節：`when (e is not FlowCanceledException)` を足します。型なしの `catch` は `catch (Exception e)` にし、元のフィルタは `&&` でつなぎます。C# 8 以前では `!(e is FlowCanceledException)` と書きます。

---

<a id="flow002"></a>
## FLOW002: FlowTask メソッドで外部の awaitable を直接 await しない（エラー）

### 検出するもの

FlowTask メソッドの中の、FlowTask の awaitable でないものの `await`。`Task`、`ValueTask`、UniTask、Unity の `Awaitable` と `AsyncOperation`、Godot の `SignalAwaiter`、`Task.Yield()`、`ConfigureAwait(...)` の結果などと、`await foreach` です。

`await using` は、ブロックの終わりに await する `DisposeAsync` の戻り値で判定します。`IAsyncDisposable` なら `ValueTask` なので対象で、FlowTask を返す `DisposeAsync` なら対象外です。

### 理由

外部の awaitable を待っている間は、スコープを巻き戻せません。そのため、フローがそこで止まると、スコープは `FlowMisuseException` で終わります。残りのコードと `finally` は走らず、`Flow.AddCleanup` と `Flow.Own` の後始末だけが走ります。同期に完了すれば止まらないので、実行時にはタイミングしだいでしか見つかりません。

`FlowBridge.FromTask(ct => ...)` でブリッジすると、スコープのキャンセルが `CancellationToken` で外部の処理に届き、完了するとフローは World のスレッドで再開します。Task と ValueTask の `.AsFlow()` は、すでに走っている処理を包むだけで、キャンセルを伝えません（[Task のブリッジ](../integrations/task.md)）。

### 悪い例

```csharp
async FlowTask LoadStage()
{
    var json = await File.ReadAllTextAsync(path);           // no cancellation; the scope ends if it suspends here
    await Task.Delay(500);                                  // ignores the World's time and Pause
    await SceneManager.LoadSceneAsync("Stage").ToUniTask(); // a foreign awaitable
}
```

### 修正例

```csharp
async FlowTask LoadStage()
{
    // The token is canceled when this scope is canceled.
    var json = await FlowBridge.FromTask(ct => File.ReadAllTextAsync(path, ct));
    await FlowTask.WaitForSeconds(0.5);                     // scope-aware, clock-aware wait
    await SceneManager.LoadSceneAsync("Stage").AsFlow();    // a cancel stops the wait; the load goes on
}
```

待っていたものごとの書き換え先は次のとおりです。

| 待っていたもの | 代わりに書くもの |
|---|---|
| `Task`、`ValueTask` | `FlowBridge.FromTask(ct => ...)`。すでに走っているものは `.AsFlow()`（キャンセルは届かない） |
| UniTask | `FlowUniTask.FromUniTask(ct => ...)`。すでに走っているものは `.AsFlow()`（キャンセルは届かない。[UniTask](../integrations/unitask.md)） |
| Unity の `Awaitable` | `.AsFlow()`。スコープのキャンセルで `Awaitable.Cancel()` を呼ぶ（[Unity のブリッジ](../unity/bridges.md)） |
| Unity の `AsyncOperation` | `.AsFlow()`。キャンセルでは待つのをやめるだけで、Unity の操作は走り続ける（[Unity のブリッジ](../unity/bridges.md)） |
| Godot の `SignalAwaiter` | `obj.ToFlowSignal(name)` で作ったシグナルの `Next()` を待つか、`ToSignal(obj, name).AsFlow()` でブリッジする（[Godot のシグナル](../godot/signals.md)） |
| `Task.Delay(ミリ秒)` | `FlowTask.WaitForSeconds(秒)`。スコープの Clock で進み、Pause に従う |
| `Task.Yield()` | `FlowTask.NextFrame()` |
| `await foreach` | シーケンスはフローの外で読み、その結果を `FlowBridge.FromTask(ct => ...)` でブリッジする |
| `await using`（`IAsyncDisposable`） | 同期の `using` で破棄するか、`finally` で `await Flow.NonCancelable(FlowBridge.FromTask(_ => resource.DisposeAsync().AsTask()))` と書いて破棄する（`Flow.NonCancelable` で、破棄の途中にキャンセルが来ても最後まで待つ。[FLOW010](#flow010)） |

### コード修正

1. 「FlowBridge.FromTask でブリッジする（スコープのキャンセルがタスクに届く）」：`await Load(x)` を `await FlowBridge.FromTask(ct => Load(x, ct))` にします。呼び出しにトークンを渡せるとき（省略できる `CancellationToken` 引数がある、`default` か `CancellationToken.None` を渡している、トークン付きのオーバーロードがある）だけ提示します。
2. 「走っているタスクを .AsFlow() でブリッジする（キャンセルは届かない）」：`await t` と `await t.ConfigureAwait(false)` を `await t.AsFlow()` にします。

ブリッジしても例外の受け方は変わらず、元の `catch (IOException)` で受けられます。

---

<a id="flow003"></a>
## FLOW003: FlowTask は await するか開始する（警告）

### 検出するもの

- 型が `FlowTask` / `FlowTask<T>` の式ステートメント（`DoThing();`）、破棄への代入（`_ = DoThing();`）、値を捨てるラムダ（`Action a = () => DoThing();`）。
- 作った直後の `Discard()`（`DoThing().Discard();`）。`Discard()` は開始せずに解放するメソッドで、UniTask の `Forget()` ではありません。変数に入れた FlowTask の `t.Discard()`（開始しないと決めた枝）は対象外です。
- 一度も読まれない FlowTask のローカル変数と、そのコレクション（`var list = new List<FlowTask> { A(), B() };` の後で使わない）。
- 一部の経路でだけ読まれるローカル変数（`var t = A(); if (c) await t;`）。`throw` で抜ける経路は開始しなくてよいとみなします。
- `TaskBridge`（`FlowBridge.FromTask(...)` や `.AsFlow()` の戻り値）にも、「一部の経路」以外の同じ規則を当てます。

`world.Run(...)` が返す `FlowHandle` を捨てるのは問題ありません。`Flow.Spawn(...)` の `FlowHandle` を文で捨てると [FLOW008](#flow008) になるので、親のスコープと一緒に終わってよいなら `_ = Flow.Spawn(...)` と書きます。

### 理由

FlowTask は遅延実行です。呼んでもタスクが作られるだけで、`await` されたとき、`Flow.Spawn` か `FlowWorld.Run` に渡されたとき、渡した合成が始まったときに初めて始まります。捨てた FlowTask は、`_ =` で破棄しても実行されません。開始されなかったタスクは実行されないだけで、実行時には何も報告されません。

並行して動かすときは、終わり方で書き分けます。

- `Flow.Spawn(task)` は今のスコープの子を始め、親のスコープが終わると一緒に止まります。子の例外は親を取り消し、親の呼び出し元で投げ直されます。
- `FlowWorld.Run(task)`（フローの中からは `FlowWorld.Current.Run(task)`）はルートのフローとして始め、このフローとは無関係に動きます。UniTask で `Forget()` していた処理は、こちらに書き換えます。

### 悪い例

```csharp
async FlowTask Battle(bool tutorial)
{
    PlayBgm();                  // never runs
    _ = SpawnEnemies();         // never runs
    SendAnalytics().Discard();  // never runs (Discard is not Forget)
    var intro = ShowIntro();
    if (tutorial) await intro;  // never runs when tutorial is false
}
```

### 修正例

```csharp
async FlowTask Battle(bool tutorial)
{
    _ = Flow.Spawn(PlayBgm());                  // runs concurrently, stops when Battle ends
    await SpawnEnemies();                       // runs to completion first
    FlowWorld.Current.Run(SendAnalytics());     // outlives this flow
    var intro = ShowIntro();
    if (tutorial) await intro;
    else intro.Discard();                       // not started on purpose
}
```

### コード修正

式ステートメントに、「FlowTask を await する」「Flow.Spawn で子として開始する（このスコープの終わりで止まる）」「FlowWorld.Current.Run で独立して実行する」の 3 つを提示します。

- 2 つめは `_ = Flow.Spawn(DoThing());` と書きます（[FLOW008](#flow008)）。`_` という名前の変数が見えるところでは提示しません。
- FlowTask メソッドの外では提示しません。スコープの外から始めるなら `world.Run(DoThing())` と書きます。

---

<a id="flow004"></a>
## FLOW004: ライフタイムハンドルは変数に保持する（警告）

### 検出するもの

`[LifetimeHandle]` が付いた型（`Clock.Pause()` が返す `ScopedHandle`、`Subscription<T>`、`EventSignal<T>`）の値を返す呼び出しを、式ステートメントとして捨てているもの（`clock.Pause();`、`signal.Subscribe(policy);`）と、`using` のないローカル変数に入れて一度も使わないもの。

`_ = clock.Pause();` は「スコープの終わりまで保持する」意図とみなし、報告しません。

### 理由

ライフタイムハンドルは作ったスコープが所有し、スコープの終わりに解放されます。保持しない、または使わないと、Pause、購読、イベントのリスナーが、スコープが終わるまで黙って生き続けます。フローの外で作ったものは、Dispose するまで残ります。

### 悪い例

```csharp
async FlowTask<bool> RetryDialog()
{
    game.Pause();                    // the game stays paused until the scope ends, after the dialog closes
    var music = bgm.Pause();         // never used: stays until the scope ends
    return await AskRetry();
}
```

### 修正例

```csharp
async FlowTask<bool> RetryDialog()
{
    using var pause = game.Pause();  // released at the end of this block
    using var music = bgm.Pause();
    return await AskRetry();
}
```

### コード修正

- 式ステートメント：「'using var pause' で宣言する」。変数名はメソッド名から作ります（`Subscribe` は `subscription`）。
- 使わないローカル変数：「'pause' の宣言に 'using' を付ける」。

`using var` を書けない場所（`if` の埋め込みステートメント、`switch` セクションの直下）では提示しません。

---

<a id="flow005"></a>
## FLOW005: FlowTask の await は FlowTask メソッドの中で行う（エラー）

### 検出するもの

FlowTask の awaitable（`handle.Join()` なども）を、FlowTask を返さない async 関数で `await` しているもの。対象は、`Task`、`ValueTask`、`async void`、UniTask、`IAsyncEnumerable` を返す関数と、トップレベルステートメントです。FlowTask を返す `DisposeAsync` を `await using` するものも含みます。

### 理由

FlowTask の await は、その時点の今のスコープの子として待ちます。スコープになるのは FlowTask メソッドだけで、`Task` などを返す async メソッドはスコープではありません。

- フローの外では、その await が `FlowMisuseException` になります。
- フローの中から呼ばれたときに通るのは、すぐに完了する await だけです。完了しない await ではメソッドの続きが捨てられ、その await のときに走っているスコープ（ふつうはメソッドを呼んだスコープ）が `FlowMisuseException` で終わります。その Task は完了しません。

### 悪い例

```csharp
async void Start()                 // a Unity MonoBehaviour
{
    await Title();                 // awaits a FlowTask outside any scope
}

async Task<int> GetScore() => await Arcade(); // awaits a FlowTask in a Task method
```

### 修正例

```csharp
void Start() => world.Run(Title());                    // a root flow of the World
Task<int> GetScore() => world.Run(Arcade()).AsTask();  // bridge back to Task
```

コード修正はありません。

- `AsTask()` の Task は、フローが終わった Tick、Flush、Run、Dispose の最後に完了します。フローは Tick で進むので、World を進めるコードが動いていないと完了しません。Unity と Godot では統合が毎フレーム Tick し、コンソールのプログラムでは自分のループで Tick します（[最初のフロー](../getting-started/first-flow.md)）。
- World のスレッドで、この Task を `.Result` などで止まって待たないでください。Tick も止まるので、終わりません。
- `AsTask()` の性質は [Task のブリッジ](../integrations/task.md)にあります。

---

<a id="flow006"></a>
## FLOW006: ループの中でシグナルの次の Emit を待つなら、ループの前で購読する（情報）

### 検出するもの

FlowTask メソッドのループの中で、`Signal<T>` か `EventSignal<T>` の `Next()` / `NextOrClosed()` を `await` しているもの（`FlowTask.Race(...)` の引数や `FlowProperty<T>.Changed.Next()` も。呼び出し元の場所を渡す `Next(file, line)` も）。

`Subscription<T>` の `Next()` と、受け手が繰り返しごとに変わりうるもの（`signals[i].Next()`）は対象外です。ビルドは止まらず、IDE の提案として出ます。

### 理由

`Next()` は、呼んだ後の次の Emit だけを待ちます（エッジ）。ループの本体が別の処理（演出、`WaitForSeconds`）をしている間の Emit は、すべて失われます。ループの前に `Subscribe` した購読は、その間の Emit を保持します（[シグナル](../guide/signals.md)）。

### 悪い例

```csharp
while (true)
{
    var damage = await _damaged.Next();      // hits during the knockback below are lost
    await FlowTask.WaitForSeconds(0.8);
}
```

### 修正例

```csharp
using var hits = _damaged.Subscribe(BufferPolicy.Latest);   // keeps the newest hit
while (true)
{
    var damage = await hits.Next();
    await FlowTask.WaitForSeconds(0.8);
}

// Every hit in order: a bounded queue
using var queue = _damaged.Subscribe(BufferPolicy.Queue(16, BufferOverflow.DropOldest));
```

### コード修正

「ループの前で購読する（BufferPolicy.Latest）」：ループの前に `using var subscription = x.Subscribe(BufferPolicy.Latest);` を入れ、ループの中の `x.Next()` を `subscription.Next()` にします。

- 受け手が副作用のない式で、ループがブロックの直下にあるときだけ提示します。受け手の null を調べるループ（`while (target != null)`）には提示しません。
- `BufferPolicy.Queue` は提示しません。容量とあふれ方は利用者が決めるものなので、全部を順に受けるなら、上の最後の行のように自分で書きます。

演出中のタップを捨てたいときなど、エッジの動きが意図どおりなら、無視するか抑制してください。

---

<a id="flow007"></a>
## FLOW007: FlowTask は GetAwaiter() を呼ばずに await する（警告）

### 検出するもの

コードに書いた `GetAwaiter()` の呼び出しのうち、戻り値が FlowTask の awaiter のもの。`GetAwaiter` という名前のメソッドの中の呼び出し（自分の awaitable 型が FlowTask の awaiter を返す形）は対象外です。

### 理由

FlowTask は遅延実行で、`GetAwaiter()` の中で開始します。そのため、コードに書いた `GetAwaiter()` も、`await` しなくてもタスクを今のスコープの子として開始します。awaiter の `IsCompleted` は、呼んだ時点の値のまま変わりません。Task の `GetAwaiter()` は何も始めないので、同じつもりで呼ぶと、意図せずタスクが動き出します（[UniTask からの移行](../integrations/unitask.md)）。

### 悪い例

```csharp
var awaiter = LoadIcons().GetAwaiter();   // LoadIcons starts here, as a child of this scope
if (awaiter.IsCompleted) Show();          // the state at the call above; it never changes
```

### 修正例

```csharp
var icons = Flow.Spawn(LoadIcons());      // runs alongside, and ends with this scope
await Open();
await icons.Join();
```

### コード修正

ありません。手で使った awaiter は、`IsCompleted` が true のとき（同期に完了したとき）にしか読めません。`OnCompleted` で継続を登録するのは、FlowTask メソッドでないメソッドの止まった await と同じく誤用です（[FLOW005](#flow005)）。完了した awaiter を意図して手で読むコードは、`#pragma warning disable FLOW007` で抑制してください。

---

<a id="flow008"></a>
## FLOW008: Flow.Spawn のハンドルを黙って捨てない（警告）

### 検出するもの

戻り値（`FlowHandle` / `FlowHandle<T>`）を使わない `Flow.Spawn(...)` の式ステートメント（`Flow.Spawn(PlayBgm());`）と、値を捨てる式形式メンバー（`void StartBgm() => Flow.Spawn(PlayBgm());`）。次も同じです。

- FlowTask メソッドの中に式で書いたラムダの本体（`items.ForEach(x => Flow.Spawn(Explode(x)))`）。
- ハンドルを入れて一度も読まないローカル変数（`var h = Flow.Spawn(PlayBgm());`）。

`FlowTask` と `FlowTask<T>` のどちらのオーバーロードも、`using static Katout.FlowTask.Flow;` で書いた `Spawn(...)` も対象です。FlowTask メソッドの外（フローから呼ぶ同期のヘルパー）も、`finally` と `catch` の中も調べます。取り消されたスコープでは、`FlowCanceledException` がコードに届いた後の後始末の中で子が始まり、スコープが終わると止まります（`World.Dispose` の間は始まりません）。

次の形は対象外です。

- `_ = Flow.Spawn(...)`（と `var _ = Flow.Spawn(...)`）、フィールドや読むローカル変数に入れたもの、引数や `return` に使ったもの、`Flow.Spawn(x).Cancel()` のようにハンドルを使うもの。`world.Run(...)` の文。
- FlowTask メソッドの外に式で書いたラムダの本体（`Assert.Throws<FlowMisuseException>(() => Flow.Spawn(x))`）。フローの外ですぐに走る Spawn は例外になるので黙って止まることはなく、呼び出しが投げることを確かめる書き方なので調べません。
- イベントハンドラの中の Spawn（[FLOW009](#flow009) が報告します）。

### 理由

`Flow.Spawn` の子は、親のスコープが終わると、正常に戻ったときも止まります。UniTask の `Forget()` の癖で投げっぱなしのつもりで書くと、短命な親のフローから開始した長く続く処理（BGM、パーティクル、再試行のループ）が、親が戻った時点で黙って止まります。画面と一緒に終わるループが Spawn の目的なので、実行時には警告しません。そこで、ハンドルを使わない文をコンパイル時に知らせ、2 つの意図を書き分けてもらいます。

- 親のスコープと一緒に終わってよい：`_ = Flow.Spawn(...)`。C# でよくある「分かって捨てる」書き方です。
- 親より長く動かす：`FlowWorld.Current.Run(...)`（フローの外では `world.Run(...)`）。[FLOW003](#flow003) と同じ書き換え先で、このフローとは無関係のルートのフローになります。例外は親の呼び出し元ではなく `OnUnhandledException` に届き、Clock は（引数で渡さなければ）World の既定の Clock です。

### 悪い例

```csharp
async FlowTask EnterTown()
{
    Flow.Spawn(PlayBgm("town"));     // meant to play on, but stops when EnterTown returns
    await FadeIn();
}
```

### 修正例

```csharp
async FlowTask EnterTown()
{
    FlowWorld.Current.Run(PlayBgm("town"));  // outlives EnterTown
    await FadeIn();
}

async FlowTask TownScreen()
{
    _ = Flow.Spawn(AmbientLoop());           // ends with the screen, on purpose
    await _closed.Next();
}
```

### コード修正

- 「'_ =' でハンドルを破棄する（子はこのスコープの終わりで止まる）」：`_ = Flow.Spawn(...)` にします。読まないローカル変数は、宣言ごと `_ = Flow.Spawn(...);` にします。`_` という名前の変数が見えるところでは提示しません。
- 「FlowWorld.Current.Run で独立して実行する（このフローが終わっても止まらない）」：`FlowWorld.Current.Run(...)` にします。`FlowWorld.Current` はフローの実行中だけ設定されるので、FlowTask メソッドの中だけで提示します。

後者は例外の届き先と Clock も変える（上の「理由」）ので、意図に合うほうを選んでください。

---

<a id="flow009"></a>
## FLOW009: イベントハンドラの中で Flow.Spawn しない（警告）

### 検出するもの

FlowTask メソッドの中で、`+=` でイベントに登録したハンドラの中の `Flow.Spawn`（`button.Clicked += () => Flow.Spawn(OpenShop());`）。すぐに同期に呼ばれる形（`list.ForEach(...)`）と、`UnityEvent.AddListener` のような `event` でない登録は対象外です。

### 理由

`Flow.Spawn` は、その時点の今のスコープの子を作ります。イベントハンドラは、イベントが発火したときに走ります。そのとき実行中のフローがなければ `Flow.Spawn` は `FlowMisuseException` になり、別のフローの中から発火すれば、そのフローの子になって一緒に終わります。

ハンドラの中では `FlowWorld.Current` も当てにできません（フローの外では null、別のフローの中ではそのフローの World です）。登録の前に `var world = FlowWorld.Current;` で World を取っておき、ハンドラでは `world.Run(...)` で始めます。別のスレッドで発火しうるイベントでは、`world.Post(() => world.Run(OpenShop()))` で World のスレッドへ渡します（`Run` は World のスレッドでしか呼べません）。いちばん素直なのは、イベントをシグナルにしてフローで待つ形です。

### 悪い例

```csharp
async FlowTask Menu()
{
    shopButton.Clicked += () => Flow.Spawn(OpenShop()); // joins whatever scope is running when it fires, or none
    await _closed.Next();
}
```

### 修正例

```csharp
async FlowTask Menu()
{
    // Turn the event into a signal owned by this flow.
    using var clicks = FlowBridge.FromCallback<FlowUnit>(emit =>
    {
        Action h = () => emit(FlowUnit.Default);
        shopButton.Clicked += h;
        return () => shopButton.Clicked -= h;
    });
    var r = await FlowTask.Race(clicks.Next().WithoutResult(), _closed.Next().WithoutResult());
    if (r.Index == 0) await OpenShop();
}
```

コード修正はありません。

---

<a id="flow010"></a>
## FLOW010: finally の await は Flow.NonCancelable で守る（警告）

### 検出するもの

FlowTask メソッドの中の、次の `await`（FlowTask の awaitable を待つもの）。

- `finally` の中の `await`。
- 例外を渡す `catch`（`throw;` や `throw new ...(e)` を含むもの）の中の `await`。ただし、`FlowCanceledException` や `OperationCanceledException` を名指しする `catch` と、キャンセルも受け取る `catch`（[FLOW001](#flow001) が報告する）は除きます。

`await Flow.NonCancelable(...)` と、それで初期化したローカル変数の `await`、例外を処理して終わる `catch` の中の `await` は報告しません。

`finally` にどこから入るかは見ないので、キャンセルでしか入らない `finally`（`try` の本体が `FlowTask.Never()` を待つだけのものなど）の `await` も報告します。そこでは印を付けても動きは変わらないので、印を付けるか、抑制してください。

### 理由

スコープのキャンセルがそのコードに届いた後（`FlowCanceledException` で本体を抜けて `finally` に入った後）は、`catch` と `finally` の `await` は最後まで走ります。一方、キャンセルの前に（`return`、try ブロックの終わり、例外で）入った `finally` は、まだ後始末ではありません。その `await` の最中にキャンセルが来ると（シーンの切り替え、ほかの枝が勝った `Race`）、その `await` が `FlowCanceledException` を投げ、次のことが起きます。

- ブロックの残り（その後の `Destroy` や保存）が走りません。
- ブロックが運んでいた例外（`try` の中で投げられた例外）が、C# の規則どおり `FlowCanceledException` に置き換わり、消えます。

実行時には、今の `await` が本体にあるのか `finally` にあるのかも、運んでいる例外も見えません。そこで、コンパイル時に知らせ、`Flow.NonCancelable` で守るかを選んでもらいます。

`Flow.NonCancelable(task)` で `await` すると、祖先のキャンセルはそのタスクに届かず、スコープはタスクを待って結果か例外を受け取り、ブロックの残りも走ります。キャンセルは、印のない次の `await` でスコープに届きます。運んでいた例外は、スコープが Canceled で終わるときに `FlowExceptionKind.Undelivered` として報告されます。

印を付けるのは、ひとりでに終わる後始末（アニメーション、保存）だけにしてください。印を付けた入力の待ちやほかのフローの待ちは取り消せず、終わるまでフローとそれを待つスコープを止めます。取り消されたスコープが後始末で 10 秒待ち続けると、`FlowWarningKind.LongCleanup` が出ます。時間の上限が要るなら、印の中で `FlowTask.Race` にします。`World.Dispose` は印を付けた `await` も終わらせます（[スコープとキャンセル](../guide/scopes-and-cancellation.md)）。

### 悪い例

```csharp
async FlowTask<Choice> Confirm()
{
    var view = OpenView();
    try
    {
        return await view.Choose();
    }
    finally
    {
        await view.PlayClose(); // canceled while it plays: Destroy does not run
        view.Destroy();
    }
}
```

### 修正例

```csharp
async FlowTask<Choice> Confirm()
{
    var view = OpenView();
    try
    {
        return await view.Choose();
    }
    finally
    {
        await Flow.NonCancelable(view.PlayClose()); // plays to its end, then Destroy runs
        view.Destroy();
    }
}
```

### コード修正

「Flow.NonCancelable で await する（ひとりでに終わる後始末だけ）」：`await x` を `await Flow.NonCancelable(x)` に書き換えます。入力の待ちを包むとハングを作るので、一括の修正はありません。1 つずつ選んでください。

---

## 重大度の変更

.NET と Godot では、`.editorconfig` でルールごとに重大度（`error` / `warning` / `suggestion` / `silent` / `none`）を変えられます。

```ini
[*.cs]
dotnet_diagnostic.FLOW003.severity = error
dotnet_diagnostic.FLOW006.severity = none
# All FlowTask rules at once (the settings of individual rules take precedence).
dotnet_analyzer_diagnostic.category-FlowTask.severity = error
```

Unity では、`.editorconfig` の重大度はコンパイルに反映されません。`Assets/Default.ruleset`（すべてのアセンブリに効く）を使います。asmdef のアセンブリの名前の ruleset（`Assets/<アセンブリ名>.ruleset`）は、Unity 6 では警告が出て使われません（`Assets` の直下に置ける ruleset は、`Default.ruleset` と、`Assembly-CSharp.ruleset` など定義済みのアセンブリの名前のものだけです）。asmdef のアセンブリ 1 つだけでルールを切るときは、asmdef の隣の `csc.rsp` に `-nowarn:FLOW001` のように書きます。

```xml
<?xml version="1.0" encoding="utf-8"?>
<RuleSet Name="Game" ToolsVersion="16.0">
  <Rules AnalyzerId="FlowTask.Analyzers" RuleNamespace="FlowTask.Analyzers">
    <Rule Id="FLOW003" Action="Error" />
    <Rule Id="FLOW006" Action="None" />
  </Rules>
</RuleSet>
```

箇所ごとには、`#pragma` か `SuppressMessage` で抑制します。エラーのルールも抑制できます。

```csharp
#pragma warning disable FLOW002 // not bridged on purpose: this test checks a foreign await
var v = await tcs.Task;
#pragma warning restore FLOW002

[System.Diagnostics.CodeAnalysis.SuppressMessage("FlowTask", "FLOW001", Justification = "Swallowing is the behavior under test.")]
async FlowTask Swallower() { /* ... */ }
```

テストのコードでも、ルールをフォルダごとに切らずに、意図して書いた箇所だけをこの形で抑制します。テストだけで通る誤りを、FLOW002 などが止めるためです（[テスト](testing.md)）。

---

## 他のアナライザとの共存

| アナライザ | 起きること | 対処 |
|---|---|---|
| VSTHRD103、CA1849 | 同期呼び出しを async 版に置き換えるコード修正が、FLOW002 になる外部の await を作る | `FlowBridge.FromTask(ct => ...)` で包む |
| RCS1261 | `IAsyncDisposable` に `await using` を勧める。暗黙に await する `DisposeAsync` の `ValueTask` は FlowTask の awaitable ではないので、FlowTask メソッドでは [FLOW002](#flow002) になる | 無効にする |
| VSTHRD200、RCS1046 | FlowTask のメソッド名（`ShowDialog()`、`Patrol()`）に `Async` 接尾辞を求める | 無効にする（下記） |
| CA2000 | `Flow.Own(new X())` のように所有権をスコープに渡した値を「Dispose されない」と報告する | 下記 |

FlowTask の文書とサンプルは、FlowTask を返すメソッドに `Async` を付けません。ゲームのフローではほとんどのメソッドが FlowTask を返すので接尾辞は情報を持たず、`await` の書き忘れは [FLOW003](#flow003) が報告します。

```ini
[*.cs]
dotnet_diagnostic.RCS1261.severity = none
dotnet_diagnostic.VSTHRD200.severity = none
dotnet_diagnostic.RCS1046.severity = none
```

CA2000 は、`AnalysisMode=All` のときに出ます。`dotnet_code_quality.CA2000.dispose_ownership_transfer_at_method_call = true` にすると出なくなりますが、`Flow.Own` 以外の呼び出しにも効きます。その検出を残したいなら、`Flow.Own` の行だけを抑制します。

```csharp
#pragma warning disable CA2000 // Flow.Own disposes it when this scope ends
var file = Flow.Own(new SaveFile());
#pragma warning restore CA2000
```
