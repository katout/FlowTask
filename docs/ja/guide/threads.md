# スレッド

World とフローは 1 つのスレッドで動きます。重い計算や I/O を別のスレッドで走らせるときは、Task で書いてブリッジで待ちます。フローは待つだけで、World のスレッドから離れません。このページでは、World とオブジェクトがどのスレッドに結び付くか、別のスレッドで処理を走らせる方法、別のスレッドから World に値や処理を渡す方法（`EmitFromAnyThread`、`Post`、`Cancel`）、フローの結果を別のスレッドで受け取る方法を説明します。

## World は作ったスレッドで動く

`FlowWorld` は、作ったスレッドに結び付きます。フローのコードはすべてそのスレッドで、`Tick` か `Flush` の中で走ります。ロックは要りません。

```csharp
// Wrong: the World belongs to the main thread
await Task.Run(() => world.Run(Load()));   // FlowThreadException
```

- `Tick`、`Flush`、`Run`、`Dispose`、`CreateClock` を別のスレッドから呼ぶと、`FlowThreadException` になります。
- `world.IsBoundToCurrentThread` で、今のスレッドが World のスレッドかを確かめられます。
- 1 つのスレッドに複数の World を作って、順に Tick できます。
- Tick を呼ぶループは、World のスレッドから離れないように書きます。コンソールの `async Main` で、Tick の間に `await Task.Delay(16)` を挟むと、await の後はスレッドプールで続くので、次の Tick が `FlowThreadException` になります。`Thread.Sleep` で待つか（[最初のフロー](../getting-started/first-flow.md)）、ループ専用のスレッドを作ります。

## 別のスレッドで処理を走らせる

重い計算や I/O は、Task で書いてブリッジで待ちます。Task はどのスレッドで走ってもかまいません。フローは World のスレッドで結果を受け取ります。

```csharp
var map = await FlowBridge.FromTask(ct => Task.Run(() => GenerateMap(seed, ct), ct));   // resumes on the World thread
```

- ブリッジした Task は、どのスレッドで完了しても、SynchronizationContext に関係なく World の受信箱に積まれます。フローは、次の Tick か Flush の取り込みで再開します。
- スコープが終わると（Race に負けたときや、親が終わったときを含む）、`ct` が取り消されます。トークンを見るかどうかは処理の側で決まるので、長い計算は途中で `ct.ThrowIfCancellationRequested()` を呼びます。
- 別のスレッドの処理の中では、World が使っているオブジェクト（`Signal`、`FlowProperty`）に書き込みません。結果を返してフローの側で書くか、下の「別のスレッドから World に渡す」の API で渡します。

ブリッジの使い方（例外、外部のキャンセル、受け取られなかった結果の後始末）は [Task / ValueTask](../integrations/task.md) にあります。

## オブジェクトの束縛

`Signal`、`FlowProperty`、`Once` は、最初に使った World とそのスレッドに結び付きます。これを束縛と呼びます。

```csharp
var hp = new FlowProperty<int>(LoadSavedHp());   // no World has used it yet: no check
world.Run(WatchHealth(hp));                       // WatchHealth waits on hp: bound to world's thread

await Task.Run(() => hp.Set(0));                  // FlowThreadException
```

- World が使った時点（フローの中での待ち、購読、フローの中での作成、`new Signal<T>(world)`）で、その World のスレッドに束縛されます。作ったスレッドではありません。
- どの World もまだ使っていないオブジェクトは検査しません。普通の .NET のオブジェクトと同じく、1 つのスレッドから使います。読み込みのスレッドで初期値を `Set` してから World に渡すのは構いません。
- 束縛した後の、別のスレッドからの書き込み（`Emit`、`Set`、購読の `TryTake` や `Dispose`）と、別のスレッドの World からの使用は、`FlowThreadException` になります。読み出し（`Value` など）は検査しません。
- `FlowProperty.Changed` は、持ち主の `FlowProperty` と同じ束縛を共有します。
- 同じスレッドのほかの World からは使えます（束縛は最初の World のままです）。複数の World で共有するなら、最初に使う World をほかより長く生かします。束縛先の World が Dispose された後は使えなくなるので、ふつうはセッションごとに作ります（[シグナル](signals.md)）。

## 別のスレッドから World に渡す

World に働きかける操作のうち、別のスレッドから呼べるのは次の 4 つです。ほかに、`FlowBridge.FromCallback` で登録したコールバックと、それが返す `EventSignal` の `Dispose` も、別のスレッドから呼べます（下の「規則と落とし穴」）。

| API | すること |
|---|---|
| `signal.EmitFromAnyThread(value)` | 値を Emit する |
| `signal.CloseFromAnyThread(error)` | Signal を閉じる |
| `world.Post(action)` | World のスレッドで処理を走らせる |
| `handle.Cancel()` | フローを取り消す |

どれも World の受信箱に積まれ、次の `Tick` か `Flush` の最初（取り込み）で、積まれた順に処理されます。同じスレッドから送ったものは、種類によらず送った順に処理されます。

```csharp
var downloaded = new Signal<byte[]>(world, "Downloaded");   // bound to world: ready for other threads

// on a worker thread
downloaded.EmitFromAnyThread(bytes);

// a flow on the World thread
var data = await downloaded.Next();
```

### EmitFromAnyThread と CloseFromAnyThread

- `EmitFromAnyThread` は、束縛先の World の受信箱に積みます。まだどの World にも束縛されていない Signal では、積む先がないので `FlowMisuseException` になります。別のスレッドから送る Signal は、`new Signal<T>(world)` で作るか、先に World のスレッドで使います（フローの中で作る、フローの中で待つか購読する）。フローの外の `Subscribe` や `Emit` だけでは束縛されません。
- `CloseFromAnyThread` は、それより前に送った Emit の後に効きます。まだどの World にも束縛されていない Signal には、その場で閉じた印を付けます。
- 束縛先の World が Dispose された後の送信は、例外にならずに捨てられます。

### Post

`world.Post(action)` は、`action` を World のスレッドで、次の Tick か Flush の取り込みで走らせます。`FlowProperty.Set` のような書き込みや、フローの開始を別のスレッドから行うときに使います。

```csharp
// a callback on a worker thread
void OnLoaded(int value) => world.Post(() => property.Set(value));

// an event that may fire on another thread starts a flow on the World thread
void OnPurchased(Receipt r) => world.Post(() => world.Run(ShowReceipt(r)));
```

- `action` はどのスコープにも属しません。投げた例外は未処理の例外（`Unhandled`）として `OnUnhandledException` に届き、取り込みは続きます。
- `action` の中から `Tick`、`Flush`、`Dispose` は呼べません。
- World が Dispose された後に Post したものは、走らずに捨てられます。

### 別のスレッドからの Cancel

`FlowHandle.Cancel()` はどのスレッドからでも呼べます。別のスレッドからの Cancel は、次の Tick か Flush の取り込みで確定し、同じフラッシュの最初に巻き戻します。それまで `Status` は変わりません。World が Dispose 済みか、タスクが終わっていれば何もしません。外部の `CancellationToken` に登録する書き方は [Task / ValueTask](../integrations/task.md) にあります。

## フローの結果を別のスレッドで受け取る

フローの外のコードは、ハンドルの `AsTask()` でフローの終わりを待ちます。

- `AsTask()` は World のスレッドで呼びます（別のスレッドからは `FlowThreadException`）。別のスレッドのコードに渡すなら、World のスレッドで取った Task を、World のスレッドの側から渡します。
- その Task は、フローが終わった Tick、Flush、Run、Dispose の最後に、World のスレッドで完了します。コンテキストを捕まえていない続き（スレッドプールで await したもの、`ConfigureAwait(false)` を付けたもの）は、そこで World のスレッドのまま同期に走ります。重い処理は `Task.Run` などで移します。
- 別のスレッドでは、その Task を止まって待ってもかまいません（`Wait()` や `.Result`）。World のスレッドが Tick を続けていれば完了します。World のスレッドで止まって待つと Tick も止まるので、終わりません（[フローと World](flows-and-world.md)）。
- 別のスレッドの World のフローは `Join` できません（await で `FlowThreadException`）。相手の World のスレッドで `AsTask()` を取り、それをブリッジで待ちます。

`AsTask()` の詳細は [Task / ValueTask](../integrations/task.md) にあります。

## 規則と落とし穴

- イベントのブリッジ（`FlowBridge.FromCallback`）は、別のスレッドから呼ばれるコールバックも受けられます（その値は受信箱を通って届きます）。フローの外で作ったブリッジは、最初に使った World に束縛されてからです。束縛される前は、ほかのまだ束縛されていないオブジェクトと同じく検査も受信箱もないので、そのブリッジは World のスレッドで使い始めるまで別のスレッドから呼ばせないでください。
- フローは ExecutionContext を捕まえず、Tick などを呼んだ側の ExecutionContext で走ります。フローの中で設定した `AsyncLocal<T>` の値はそのフローのものにならず、ほかのフローや Tick の呼び出し元にも見えます。await の後に同じ値が見える保証もありません。フローごとの値は引数で渡します（[実行モデル](../advanced/execution-model.md)）。
- 別のスレッドから来る値は、どの Tick に取り込まれるかがスレッドの都合で決まります。同じ入力を再現したいときの記録のしかたは [デバッグ](../tools/debugging.md) にあります。
- Unity と Godot での、メインスレッドと World の関係は [Unity のセットアップ](../unity/setup.md) と [Godot のセットアップ](../godot/setup.md) にあります。
