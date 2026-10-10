# 最初のフロー

.NET のコンソールのプログラムで、小さなフローを段階的に作ります。World を作って Tick するループ、時間の待ち、キー入力の待ち、`FlowTask.Race` によるタイムアウト、負けた側の `finally` が走る様子を、順に確かめます。

最後には、次の動きをするプログラムができます。5 秒のカウントダウンの間にキーを押せばスタート、押さなければ時間切れです。

```text
Press any key to start.
5
4
3
Countdown ended.
Start! (Enter)
```

エンジンは要りません。.NET 6 以降の SDK の `dotnet new console` が作るプロジェクト（暗黙の `using` が有効）を前提にします。

## プロジェクトを作る

```sh
dotnet new console -n FirstFlow
cd FirstFlow
dotnet add package FlowTask --version 0.1.0-preview.1
```

以下では、`Program.cs` の中身を書き換えていきます。

## World を作って Tick する

`Program.cs` を次のようにします。1 秒ごとに数を数え、最後に「Go!」と出すフローです。

```csharp
using System.Diagnostics;
using Katout.FlowTask;

using var world = new FlowWorld();
world.OnUnhandledException = info => Console.Error.WriteLine(info);   // without a handler, the outermost Tick, Flush, Run or Dispose throws the exception
var countdown = world.Run(Countdown(3));

var time = Stopwatch.StartNew();
var last = 0.0;
while (!countdown.IsCompleted)
{
    Thread.Sleep(16);                               // one frame of your loop
    var now = time.Elapsed.TotalSeconds;
    world.Tick(Math.Min(now - last, 0.1));          // unscaled seconds since the last Tick; long frames clamped
    last = now;
}

static async FlowTask Countdown(int from)
{
    for (var i = from; i > 0; i--)
    {
        Console.WriteLine(i);
        await FlowTask.WaitForSeconds(1);
    }
    Console.WriteLine("Go!");
}
```

`dotnet run` で実行すると、1 秒おきに `3`、`2`、`1`、`Go!` と出て終わります。

部品を順に見ます。

- **`FlowWorld`**：フローを動かす単位です（World と呼びます）。フローの木、時間（Clock）、再開のキューを持ちます。`using` で、プログラムの終わりに Dispose します。
- **`async FlowTask`**：戻り値が `FlowTask` の async メソッドが、FlowTask のフローになります。Task の代わりに `FlowTask` を返す以外は、普通の async メソッドと同じに書けます。
- **`world.Run(task)`**：フローを開始し、`FlowHandle` を返します。フローは最初に止まる await まで、`Run` の中でその場で走ります。`Countdown(3)` を呼んだだけでは何も始まらない点が、Task と違います。
- **`world.Tick(dt)`**：時間を `dt` 秒進め、待ちが満たされたフローを再開します。ゲームのループで 1 フレームに 1 回呼びます。`dt` には倍率のかかっていない経過時間を渡し、長すぎるフレーム（ブレークポイントで止めたときなど）は上限で切り詰めます。
- **`FlowTask.WaitForSeconds(1)`**：World の時間で 1 秒待ちます。実時間ではなく、`Tick` に渡した時間で進みます。
- **`OnUnhandledException`**：どのフローも捕まえなかった例外（未処理の例外）の受け手です。設定しないと、いちばん外側の `Tick`、`Flush`、`Run`、`Dispose` がその例外を `FlowUnhandledException` で投げます。

> **注意**：Tick しない限り、時間待ちは進みません。フローが止まったまま動かないときは、まず Tick を呼んでいるかを確かめてください。

> **注意**：ループの中で、`Thread.Sleep` の代わりに `await Task.Delay(16)` を使わないでください。await の後はスレッドプールで続くので、次の Tick が `FlowThreadException` になります。World は作ったスレッドでしか進められません（[スレッド](../guide/threads.md)）。

## 入力を Signal にする

次に、キー入力を待てるようにします。出来事を知らせるには `Signal<T>` を使います。ループの中で、押されたキーを `Emit` します。

```csharp
var keys = new Signal<ConsoleKey>();
```

```csharp
while (...)
{
    Thread.Sleep(16);
    while (Console.KeyAvailable) keys.Emit(Console.ReadKey(intercept: true).Key);   // input of this frame
    // ... Tick as before
}
```

フローの側は、`keys.Next()` を await すると、次の Emit を待てます。

- Emit は、待っているフローをその場では再開しません。再開は予約され、次の `Tick` の中で決まった順に処理されます。
- `Next()` は「次の 1 回」を待つエッジです。誰も待っていない間の Emit は捨てられます。取りこぼしたくないときは購読を使います（[シグナル](../guide/signals.md)）。

> **注意**：`Console.KeyAvailable` は、コンソールのない環境や、入力をリダイレクトして実行したときは `InvalidOperationException` を投げます。ターミナルから `dotnet run` で実行してください。

## Race で入力待ちとタイムアウト

キーを待つフローを書きます。5 秒待っても押されなければ、時間切れにします。

```csharp
static async FlowTask Title(Signal<ConsoleKey> keys)
{
    Console.WriteLine("Press any key to start.");
    var r = await FlowTask.Race(keys.Next(), FlowTask.WaitForSeconds(5));
    if (r.TryGet0(out var key)) Console.WriteLine($"Start! ({key})");
    else Console.WriteLine("Time over.");
}
```

`FlowTask.Race` は、渡した枝を書いた順に始め、最初に終わった枝を勝ちとします。負けた枝は止められます。

- 結果の `RaceResult` は、勝った枝の番号（`Index`）と値を持ちます。`TryGet0` は、0 番目の枝が勝ったときに true を返し、その値（ここでは押されたキー）を取り出します。
- タイムアウトのための特別な API はありません。仕事と `FlowTask.WaitForSeconds` を Race させるのが、FlowTask でのタイムアウトの書き方です。
- 同じ Tick で両方が満たされたときは、先に完了が処理された枝が勝ちます。多くは前に書いた枝です（[合成](../guide/composition.md)）。入力のような割り込みは、先に書きます。

プログラムの上の部分を、`Title` を動かすように変えます。

```csharp
using var world = new FlowWorld();
world.OnUnhandledException = info => Console.Error.WriteLine(info);

var keys = new Signal<ConsoleKey>();
var title = world.Run(Title(keys));

var time = Stopwatch.StartNew();
var last = 0.0;
while (!title.IsCompleted)
{
    Thread.Sleep(16);
    while (Console.KeyAvailable) keys.Emit(Console.ReadKey(intercept: true).Key);
    var now = time.Elapsed.TotalSeconds;
    world.Tick(Math.Min(now - last, 0.1));
    last = now;
}
```

実行して 5 秒以内にキーを押すと `Start! (…)`、押さなければ 5 秒後に `Time over.` と出ます。

## 負けた枝の finally が走ることを確かめる

タイムアウトの枝を、画面に残り秒数を出すカウントダウンに替えます。カウントダウンには `try`/`finally` を付けます。

```csharp
static async FlowTask Title(Signal<ConsoleKey> keys)
{
    Console.WriteLine("Press any key to start.");
    var r = await FlowTask.Race(keys.Next(), Countdown(5));
    if (r.TryGet0(out var key)) Console.WriteLine($"Start! ({key})");
    else Console.WriteLine("Time over.");
}

static async FlowTask Countdown(int from)
{
    try
    {
        for (var i = from; i > 0; i--)
        {
            Console.WriteLine(i);
            await FlowTask.WaitForSeconds(1);
        }
    }
    finally
    {
        Console.WriteLine("Countdown ended.");
    }
}
```

カウントダウンの途中でキーを押すと、次のように出ます。

```text
Press any key to start.
5
4
3
Countdown ended.
Start! (Enter)
```

キーの枝が勝つと、負けた `Countdown` は巻き戻されます。止まっていた `await FlowTask.WaitForSeconds(1)` から `FlowCanceledException` が送出され、`finally` が走ってからメソッドを抜けます。`Title` が再開するのは、その後です。だから `Countdown ended.` が `Start!` より先に出ます。

キーを押さなければ、カウントダウンが最後まで数えて普通に終わり、同じ `finally` が走ってから `Time over.` が出ます。

```text
Press any key to start.
5
4
3
2
1
Countdown ended.
Time over.
```

止められる側のコードは、トークンを受け取ったり、止められたかを調べたりしていません。後始末を `finally`（や `using`）に書いておけば、普通に終わったときも、止められたときも走ります。

> **注意**：`catch (Exception)` や `catch (OperationCanceledException)` で `FlowCanceledException` を受け止めて処理を続けると、止めたはずのフローが動き続けます。キャンセルを受けた `catch` は `throw;` で終えます（アナライザの FLOW001 がエラーにします）。書き方は [スコープとキャンセル](../guide/scopes-and-cancellation.md) にあります。

## できあがったプログラム

```csharp
using System.Diagnostics;
using Katout.FlowTask;

using var world = new FlowWorld();
world.OnUnhandledException = info => Console.Error.WriteLine(info);   // without a handler, the outermost Tick, Flush, Run or Dispose throws the exception

var keys = new Signal<ConsoleKey>();
var title = world.Run(Title(keys));

var time = Stopwatch.StartNew();
var last = 0.0;
while (!title.IsCompleted)
{
    Thread.Sleep(16);                                // one frame of your loop
    while (Console.KeyAvailable) keys.Emit(Console.ReadKey(intercept: true).Key);
    var now = time.Elapsed.TotalSeconds;
    world.Tick(Math.Min(now - last, 0.1));           // unscaled seconds since the last Tick; long frames clamped
    last = now;
}

static async FlowTask Title(Signal<ConsoleKey> keys)
{
    Console.WriteLine("Press any key to start.");
    var r = await FlowTask.Race(keys.Next(), Countdown(5));
    if (r.TryGet0(out var key)) Console.WriteLine($"Start! ({key})");
    else Console.WriteLine("Time over.");
}

static async FlowTask Countdown(int from)
{
    try
    {
        for (var i = from; i > 0; i--)
        {
            Console.WriteLine(i);
            await FlowTask.WaitForSeconds(1);
        }
    }
    finally
    {
        Console.WriteLine("Countdown ended.");
    }
}
```

## Unity と Godot では

フローのメソッド（`Title`、`Countdown`）はそのまま使えます。変わるのは、World と入力の用意です。

- **Unity**：統合が World を作り、PlayerLoop から毎フレーム Tick します。ループは書かず、`FlowTaskUnity.World.Run(Title(keys))` で始めます。GameObject と寿命をそろえるなら `gameObject.RunWhileActive(...)` です。入力は、UnityEvent などから Signal に Emit します（[Unity のセットアップ](../unity/setup.md)、[ブリッジ](../unity/bridges.md)）。
- **Godot**：autoload にした `FlowWorldNode` が `_Process` で World を Tick します。`FlowWorldNode.Default.Run(...)` か、ノードの寿命にそろえる `node.RunWhileInTree(...)` で始めます。入力には Godot のシグナルのブリッジが使えます（[Godot のセットアップ](../godot/setup.md)、[シグナル](../godot/signals.md)）。

どちらも、統合が作る World は未処理の例外をエンジンのログ（Unity のコンソール、Godot のエラー出力）に出すので、`OnUnhandledException` を自分で設定しなくてもかまいません。

## 次に読むページ

- [フローと World](../guide/flows-and-world.md)：Run、Tick、Spawn、FlowHandle、遅延実行
- [スコープとキャンセル](../guide/scopes-and-cancellation.md)：巻き戻し、`finally` の中の await、`catch` の書き方
- [合成：Race と WhenAll](../guide/composition.md)：Race の決まり、WhenAll、タイムアウト
- [時間と Clock](../guide/time-and-clocks.md)：Tick に渡す時間、Pause、タイムスケール
- [シグナル](../guide/signals.md)：Signal、購読、FlowProperty
- [Task と ValueTask](../integrations/task.md)：ファイルや通信をフローから待ち、キャンセルボタンで止める
