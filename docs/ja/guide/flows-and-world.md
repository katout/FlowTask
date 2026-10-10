# フローと World

FlowTask で書いた処理がいつ始まり、何に駆動されて進み、どこに属するかを説明します。`FlowTask` 型、`FlowWorld`、`Run` と `Tick`、子を始める `Flow.Spawn`、開始したタスクを操作する `FlowHandle` を扱います。

## FlowTask メソッドと World

ゲームの進行は、戻り値が `FlowTask` か `FlowTask<T>` の async メソッドで書きます。これを FlowTask メソッドと呼びます。FlowTask メソッドを動かすのは `FlowWorld`（World）で、World を毎フレーム `Tick` すると、待ちが満たされたフローが再開します。

```csharp
using Katout.FlowTask;

var world = new FlowWorld();
world.OnUnhandledException = info => Console.Error.WriteLine(info);   // where uncaught exceptions go

var title = world.Run(TitleScreen());             // starts the flow; returns a handle

// In your game loop, once per frame:
world.Tick(unscaledDeltaTime);

async FlowTask TitleScreen()
{
    ShowLogo();
    await FlowTask.WaitForSeconds(2);   // seconds of the scope's clock
    await MainMenu();                   // a child flow: starts here, and this one waits for it
}
```

- `world.Run(task)` は、タスクを World のルートの子として開始し、`FlowHandle` を返します。開始したフローは、最初に止まる await まで `Run` の中でその場で走ります。
- `world.Tick(dt)` は、渡した秒数だけ時間を進め、満たされた待ちを再開します。dt には倍率のかかっていない経過時間を渡します（[時間と Clock](time-and-clocks.md)）。
- Unity と Godot の統合は World を作って毎フレーム Tick するので、自分では Tick しません（[Unity のセットアップ](../unity/setup.md)、[Godot のセットアップ](../godot/setup.md)）。テストでは仮想時間で Tick します（[テスト](../tools/testing.md)）。
- `OnUnhandledException` は、どのフローも捕まえなかった例外の受け手です。設定しないと、`Tick` などがその例外を `FlowUnhandledException` で投げます（[失敗の扱い](failures.md)）。

World は 1 本の時間軸と 1 つのスレッドに対応する単位です。1 つのスレッドに複数の World を作って順に Tick してもかまいません（物理フレーム用の World を分けるなど）。

## タスクは遅延実行

FlowTask メソッドを呼んでも、何も走りません。タスクが始まるのは、次のどれかのときです。

- `await` されたとき
- `Flow.Spawn` か `FlowWorld.Run` に渡されたとき
- 渡した合成（`FlowTask.Race`、`FlowTask.WhenAll` など）が始まったとき

```csharp
async FlowTask Battle()
{
    PlayBgm();                   // does nothing: the task is created and dropped
    await SpawnEnemies();        // starts here, as a child of Battle
}
```

`PlayBgm();` のように捨てたタスクは実行されず、実行時には何も報告されません。アナライザの FLOW003 がこの書き方を警告します（[アナライザ](../tools/analyzers.md)）。

始めたタスクは、始めた文脈のスコープの子になります。スコープとその木の話は [スコープとキャンセル](scopes-and-cancellation.md) にあります。

### 1 回だけ await する

1 つの `FlowTask` の値を始められるのは 1 回だけです。

```csharp
var load = LoadStage();
await load;
await load;   // FlowMisuseException: this task was already started
```

複数のフローで同じ結果を待つなら、結果を `Once<T>` に入れて待たせます（[シグナル](signals.md)）。失敗やキャンセルで終わりうる処理なら、`Flow.Spawn` で始めたハンドルを各フローが `FlowTask.WaitUntil(h, x => x.IsCompleted)` で待ち、`Status` を見ます。`Flow.WithClock` や `Flow.Named` に始めたタスクを渡しても `FlowMisuseException` になります。どちらも、始める前のタスクに付けます。

### 始めないと決めたタスク：Discard

条件によって始めないことにしたタスクは、`Discard()` で解放します。

```csharp
var intro = ShowIntro();
if (tutorial) await intro;
else intro.Discard();   // not started on purpose
```

`Discard()` は UniTask の `Forget()` ではありません。タスクを始めずに捨てます。待たずに走らせたいなら、次の `Flow.Spawn` か `FlowWorld.Run` を使います。

## 待たずに走らせる：Spawn と Run

await は「子を始めて、終わるまで待つ」です。待たずに並行して走らせるには、どれくらい長く生かすかで 2 つを使い分けます。

```csharp
async FlowTask Battle()
{
    var bgm = Flow.Spawn(PlayBgm());              // a child of Battle: stops when Battle ends
    FlowWorld.Current.Run(SendAnalytics());       // a root flow: runs on after Battle ends
    await Fight();
}   // Battle returns here, and PlayBgm is unwound
```

| 書き方 | 親 | 呼んだスコープが終わると | 例外の行き先 |
|---|---|---|---|
| `await task` | 今のスコープ | （終わるまで待つ） | その await |
| `Flow.Spawn(task)` | 今のスコープ | 巻き戻る | 呼んだスコープを取り消し、その呼び出し元で投げ直す |
| `world.Run(task)` | World のルート | 走り続ける | `OnUnhandledException` |

- `Flow.Spawn` の親は、子を待たずに return できます。return すると、まだ動いている子は巻き戻ります。子が終わるまで待つなら `await handle.Join()` と書きます。
- `Flow.Spawn` の子の例外が親に届く仕組みは [失敗の扱い](failures.md) にあります。
- UniTask の `Forget()` は、スコープより長く走らせるなら `FlowWorld.Run`、スコープと一緒に止まってよいなら `Flow.Spawn` に書き換えます。
- フローの中から World を取るには `FlowWorld.Current` を使います。フローの外（イベントハンドラなど）では、取っておいた World の `Run` を呼びます。

> **注意**：ハンドルを使わない `Flow.Spawn(x);` の文は、アナライザの FLOW008 が警告します。親と一緒に終わってよいなら `_ = Flow.Spawn(x);` と書き、親より長く動かしたいなら `FlowWorld.Run` にします。親より長く動かすつもりの子が親の終了で切られても、実行時には警告しません。

> **注意**：イベントハンドラの中で `Flow.Spawn` を呼ぶと、イベントが発火したときに走っていたフローの子になります（走っているフローがなければ `FlowMisuseException`）。イベントは `FlowBridge.FromCallback` でシグナルにするか、取っておいた World の `Run` で始めます（FLOW009）。

### 別の World の Run

フローの中から別の World の `Run` で始めたフローは、その World のルートの子です。呼んだスコープが終わっても巻き戻りません。一緒に終わらせるなら、後始末に登録します。

```csharp
var h = physicsWorld.Run(Simulate());
Flow.AddCleanup(h.Cancel);   // ends with this scope
```

## FlowHandle

`Flow.Spawn` と `FlowWorld.Run` は、開始したタスクのハンドル（`FlowHandle` か `FlowHandle<T>`）を返します。

```csharp
var download = world.Run(Download());

// later, from the game's code
if (cancelPressed) download.Cancel();
if (download.IsCompleted && download.Status == FlowStatus.Succeeded) ShowDone();
```

| メンバー | 内容 |
|---|---|
| `Status` | `Running`、`Succeeded`、`Canceled`、`Faulted` など |
| `IsCompleted` | 終わったか |
| `Cancel()` | タスクとその子孫を取り消す。どのスレッドからでも呼べる |
| `CancelCause` | 取り消しが求められたかと、その理由 |
| `Exception` | 例外で終わったときの、その例外。それ以外では null |
| `Result` | 成功したときの値（`FlowHandle<T>`）。それ以外では例外 |
| `Join()` | 終わるまで待つ `FlowTask`。1 つのハンドルにつき 1 回だけ使える |
| `AsTask()` | フローの外のコードが待つための `Task` |

- ハンドル自体は await できません。待つなら `await handle.Join()` です。
- `Join()` は、相手がキャンセルか例外で終わると `FlowJoinException` を投げます。相手が例外で終わったときは、その例外が `InnerException` に入ります。一度も始まっていないハンドル（キャンセルされたスコープで断られた Spawn や、既定値のハンドル）の Join は、始まっていないことを文面に書いた `FlowJoinException` を投げます。終わり方を問わずに待つなら、`await FlowTask.WaitUntil(handle, h => h.IsCompleted)` の後で `Status` を見ます。
- 1 つのハンドルを 2 回 Join すると、2 回目の await が `FlowMisuseException` になります。Race に負けて巻き戻った Join も 1 回に数えます。もう一度待つなら `WaitUntil` を使います。
- 取り消した後も、後始末（`finally` の await など）が終わるまで `Status` は `Running` のままです。`CancelCause` が `None` 以外なら、閉じている最中です（[スコープとキャンセル](scopes-and-cancellation.md)）。
- `Join()` は普通の FlowTask なので、`FlowTask.Race(handle.Join(), FlowTask.WaitForSeconds(5))` のように合成できます（[合成](composition.md)）。
- 自分自身か祖先の Join は、決して終わらないので `FlowMisuseException` になります。フローどうしが互いに Join する形は検出しないので、止まったらダンプで探します（[デバッグ](../tools/debugging.md)）。

### フローの外で結果を受け取る

フローの外のコード（ゲームのループや Task のメソッド）は、フローの終わりを次のどれかで受け取ります。

- **終わったかを見る**：`IsCompleted` が true になった後に、`Status` と `Result` を読みます。毎フレームの処理から見るときの書き方です。
- **Tick を回して待つ**：エンジンのない .NET のプログラムでは、`while (!handle.IsCompleted)` のループの中で Tick を呼び、終わったら `Result` を読みます（[最初のフロー](../getting-started/first-flow.md)）。テストでは、`RunUntilComplete` が同じことを仮想時間で行います（[テスト](../tools/testing.md)）。
- **await する**：Task のメソッドからは `await handle.AsTask()`、UniTask のメソッドからは `await handle.ToUniTask()` で待ちます（[Task / ValueTask](../integrations/task.md)、[UniTask](../integrations/unitask.md)）。

World のスレッドで `handle.AsTask().Result` や `.Wait()` のように止まって待つと、World を進める Tick も止まるので、終わりません。止まって待つのは、別のスレッドからだけにします（[スレッド](threads.md)）。

`AsTask()` の Task がいつ、どのスレッドで完了するかは [Task / ValueTask](../integrations/task.md) にあります。

## await できるのは FlowTask メソッドの中だけ

FlowTask を await できるのは、World で走っている FlowTask メソッドの中だけです。フローの外から始めるには `FlowWorld.Run` を使います。

```csharp
// Wrong: an async Task method that awaits a flow
async Task OnClick() => await OpenShop();   // FLOW005; at run time, a misuse

// Right
void OnClick() => world.Run(OpenShop());

// Right, when Task code needs the end of the flow
async Task OnClickAsync() => await world.Run(OpenShop()).AsTask();
```

- `async Task`、`ValueTask`、UniTask、`async void` のメソッドが FlowTask を await して止まると、誤用として扱います。そのメソッドは再開せず、呼んだスコープは `FlowMisuseException` で終わります。アナライザの FLOW005 がコンパイル時に止めます。
- 逆向きに、FlowTask メソッドの中で Task や UniTask、エンジンの awaitable を直接 await するのも誤用です。外部の awaitable はブリッジを通して待ちます。こうすると、スコープのキャンセルが `ct` に届きます（FLOW002。[Task / ValueTask](../integrations/task.md)）。

```csharp
var json = await FlowBridge.FromTask(ct => File.ReadAllTextAsync(path, ct));   // the scope's cancel reaches ct
```

## Tick と Flush

完了や `Emit` は、待っていたフローをその場では再開しません。再開は予約され、次の `Tick` か `Flush` でまとめて処理されます。

```csharp
var start = new Signal<FlowUnit>();
world.Run(WaitForStart(start));   // runs up to `await start.Next()`
start.Emit(FlowUnit.Default);     // reserves the resume; WaitForStart has not resumed yet
world.Flush();                    // resumes it now, without advancing time
```

`Tick(dt)` は、別のスレッドからの送信を取り込み、時間を進め、満たされた待ちと予約された再開を順に処理します。`Flush()` は時間を進めずに、取り込みと再開だけを行います。Unity の統合は、1 フレームの中の何か所かで Flush を呼んで、そのフレームの入力をそのフレームのうちに処理します（[Unity のセットアップ](../unity/setup.md)）。Tick の手順と再開の順序は [実行モデル](../advanced/execution-model.md) にあります。

- `Tick` に負の値、NaN、無限大は渡せません（`ArgumentOutOfRangeException`）。
- フローのコード、後始末、`OnUnhandledException` などのハンドラの中からは、`Tick`、`Flush`、`Dispose` を呼べません（`FlowMisuseException`）。
- `Tick`、`Flush`、`Run` は、World を作ったスレッドから呼びます（[スレッド](threads.md)）。

## World を閉じる

`world.Dispose()` は、すべてのフローを取り消して巻き戻します。Dispose は Tick を回さないので、後始末の await は最後まで走りません。保存のように最後まで走らせたい後始末があるなら、先にフローを取り消して Tick を回してから Dispose します（[失敗の扱い](failures.md)）。

## 名前

ダンプとスコープパスには、既定でメソッド名が出ます。同じメソッドを何体も動かすなら、`Flow.Named` で個体を区別します。

```csharp
_ = Flow.Spawn(Flow.Named($"Enemy#{id}", EnemyAI(enemy)));
```

フローの中では `Flow.CurrentScopePath`（`Game > InGame > Battle` の形）で現在地をログに出せます。`Flow.IsInFlow` は、今のコードがフローの中で走っているかを返します。

## 規則と落とし穴

- `default(FlowTask)` は完了済みのタスク（`FlowTask.CompletedTask`）です。`default(FlowTask<T>)` はタスクではなく、await や合成に渡すと `FlowMisuseException` になります。値を返す完了済みのタスクは `FlowTask.FromResult(value)` で作ります。
- `GetAwaiter()` をコードに書くと、await しなくてもその場でタスクが始まります。awaiter の `IsCompleted` は、取得したときの値のまま変わりません。`GetAwaiter()` は書かずに await します（FLOW007）。
- フローは ExecutionContext を捕まえないので、フローの中で設定した `AsyncLocal<T>` の値はそのフローのものにならず、await の後に同じ値が見える保証もありません。フローごとの値は引数で渡します（[実行モデル](../advanced/execution-model.md)）。
- Task や UniTask との違いは [UniTask](../integrations/unitask.md)（語の対応と書き換え）と [用語集](../advanced/glossary.md) にあります。
