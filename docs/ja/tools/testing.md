# エンジンなしのテスト

FlowTask のフローは、エンジンを動かさずにテストできます。World の時間はテストが Tick で進める仮想時間なので、10 秒のタイムアウトも一瞬で過ぎ、同じ入力なら毎回同じ順序で動きます。このページでは、`FlowTask.Testing` と NUnit のアダプタの使い方と、xUnit などほかのフレームワークでの使い方を説明します。

## 最小のテスト

テストするフローの例です。OK か Cancel が押されるか、時間切れになるまで待ちます。

```csharp
public static class ConfirmDialog
{
    public static async FlowTask<bool> Ask(Signal<FlowUnit> ok, Signal<FlowUnit> cancel, double timeoutSeconds)
    {
        var r = await FlowTask.Race(ok.Next(), cancel.Next(), FlowTask.WaitForSeconds(timeoutSeconds));
        return r.Index == 0;
    }
}
```

NUnit のテストはこう書きます。

```csharp
using Katout.FlowTask;
using Katout.FlowTask.Testing;
using Katout.FlowTask.Testing.NUnit;
using NUnit.Framework;

public class ConfirmDialogTests
{
    [Test, FailOnUnhandledFlowException]
    public void TheFirstPressWins()
    {
        using var tw = FlowNUnit.CreateWorld();
        var ok = new Signal<FlowUnit>();
        var cancel = new Signal<FlowUnit>();

        var h = tw.World.Run(ConfirmDialog.Ask(ok, cancel, timeoutSeconds: 10)); // 1. start
        ok.Emit(FlowUnit.Default);                                              // 2. inject the input
        tw.World.TickUntil(() => h.IsCompleted);                                // 3. run until it ends

        Assert.That(h.Result, Is.True);
    }

    [Test, FailOnUnhandledFlowException]
    public void ItTimesOut()
    {
        using var tw = FlowNUnit.CreateWorld();
        var h = tw.World.Run(ConfirmDialog.Ask(new Signal<FlowUnit>(), new Signal<FlowUnit>(), timeoutSeconds: 10));

        tw.World.TickFor(11);   // past the 10-second timeout, at once

        Assert.That(h.IsCompleted, Is.True);
        Assert.That(h.Result, Is.False);
    }
}
```

テストは、始める、入力を注入する、時間を進める、の 3 つでできています。

- 入力は、テストが持つ `Signal` の `Emit`、`FlowProperty` の `Set`、偽の Task の完了で注入します。フローの中で作られる入力（ボタンの押下など）は、引数で渡せる形にしておきます。
- 入力の要らないフローは、`tw.World.RunUntilComplete(task)` の 1 行で結果を受け取れます。
- `TestWorld` は `FlowWorld` に暗黙に変換できますが、`Run` や `Tick` は持ちません。いつも `tw.World.` を付けて書きます。

## パッケージ

| パッケージ | 中身 | 名前空間 |
|---|---|---|
| `FlowTask.Testing` | `TestWorld`、仮想時間を進める拡張メソッド、`FlowAssert` | `Katout.FlowTask.Testing` |
| `FlowTask.Testing.NUnit` | `FlowNUnit.CreateWorld()`、`[FailOnUnhandledFlowException]`（NUnit 3.14 以上） | `Katout.FlowTask.Testing.NUnit` |

.NET と Godot では NuGet で、Unity では UPM で入れます。手順は [インストール](../getting-started/installation.md)にあります。

## 仮想時間を進める

`FlowWorld` の拡張メソッドで World を進めます。Tick の間隔は、引数で渡さなければ 1/60 秒です。

| メソッド | 動き |
|---|---|
| `TickFrames(count)` | `count` 回 Tick する |
| `TickFor(seconds)` | `UnscaledClock` が `seconds` 進むまで Tick し、回数を返す |
| `TickUntil(condition, maxTicks)` | 条件が真になるまで Tick する。`maxTicks`（既定 100,000）回で満たされなければ、ダンプを添えた `TimeoutException` を投げる |
| `RunUntilComplete(task, maxTicks)` | `Run` して終わるまで Tick し、結果を返す。例外で終わると `FlowUnhandledExceptionAssertionException`、キャンセルで終わると `FlowAssertionException`、`maxTicks` 回で終わらなければ `TimeoutException` を投げる |

時間の待ちには、次の性質があります。フレームごとの順序を確かめるテストは、ゲームと同じ間隔で回してください。

- 同じ Tick に期限が来た時間待ちは、期限の早さではなく、待ち始めた順に完了します（Race の枝が時間待ちそのものなら、引数の順）。
- `WaitForSeconds` は、Clock の時間（double の累積）が目標に届いた最初の Tick で終わります。1/60 秒を 6 回足しても 0.1 にわずかに届かないので、60fps での 0.1 秒の待ちは 7 回目の Tick で終わります。フレーム数で正確に待つなら `DelayFrames` を使います。

## 偽の Task は完了済みで作る

ブリッジする処理の偽物は、テストのスレッドで完了する Task で作ります。

```csharp
// The fake completes at once, on the test's thread.
Func<CancellationToken, Task<Profile>> getProfile = _ => Task.FromResult(new Profile("alice"));
var profile = tw.World.RunUntilComplete(LoadProfile(getProfile));

static async FlowTask<Profile> LoadProfile(Func<CancellationToken, Task<Profile>> get) =>
    await FlowBridge.FromTask(get);   // bridged, even in tests
```

- 使うもの：`Task.FromResult`、`Task.FromException`、`Task.CompletedTask`、テストが Tick の間に自分で完了させる `TaskCompletionSource<T>`（既定のオプション）。
- 避けるもの：別のスレッドで完了する Task（本物の I/O、`Task.Delay`、`Task.Run`）と、`RunContinuationsAsynchronously` を付けた `TaskCompletionSource`。完了がどの Tick に取り込まれるかが決まりません。`TickUntil` と `RunUntilComplete` は実時間を待たずに仮想時間を進めるので、フローの中の `WaitForSeconds` のタイムアウトが先に過ぎたり、`maxTicks` を使い切って `TimeoutException` になったりします。
- テストのメソッドを `async Task` にして、Tick の間に `await` を挟まないでください。`await` の続きは別のスレッドで走ることがあり、そこでの Tick は `FlowThreadException` になります（World は作ったスレッドでしか動きません）。
- 本物の I/O を通す結合テストでは、1 つのスレッドで、経過した実時間を `Tick` に渡して回します。
- **偽の Task もブリッジを通して await します**（`FlowBridge.FromTask`、`.AsFlow()`）。完了済みの Task は直接 await しても止まらないので、テストは通り、製品でだけスコープが `FlowMisuseException` で終わります。これは [FLOW002](analyzers.md) が止めるので、テストのプロジェクトでもアナライザを外さないでください。

ブリッジの詳細は [Task のブリッジ](../integrations/task.md)にあります。

## 未処理の例外をテストの失敗にする

`TestWorld` は、`OnUnhandledException` に届くすべての報告（`Cleanup`、`SwallowedCancellation`、`Undelivered` を含む）を `Exceptions` に、警告を `Warnings` に記録します。

- `ThrowIfUnhandled()` は、報告があれば最初の報告を持つ `FlowUnhandledExceptionAssertionException`（`FlowAssertionException` の派生。`Info` で報告を見られる）を投げます。
- `Dispose()` は World を Dispose してから、同じ検査をします。`using var tw = …` と書けば、テストの終わりに検査されます。
- 失敗を確かめるテストでは、`tw.Exceptions` を調べてから `AcceptExceptions()` で消します。

```csharp
using var tw = new TestWorld();
tw.World.Run(FailingFlow());
tw.World.TickFrames(1);

Assert.That(tw.Exceptions[0].Exception, Is.InstanceOf<SaveFailedException>());
tw.AcceptExceptions();   // expected: Dispose does not fail the test
```

`Undelivered` もテストの失敗になります。キャンセルと失敗が同じ Tick に重なる場面を試すテストでは、期待する報告として確かめてください。

フレームワークのアダプタは、報告をテストの出力に書きます。

- **NUnit**：`FlowNUnit.CreateWorld()` の World は、報告を `TestContext.Out` に書きます。`[FailOnUnhandledFlowException]` をメソッド、クラス、アセンブリに付けると、テストの後で報告があれば失敗にします。テストが自分で Dispose した World は、その Dispose で検査され、`[FailOnUnhandledFlowException]` の検査には含まれません。
- **xUnit などほかのフレームワーク**：`TestWorld` をそのまま使います。Dispose のときに報告があれば、例外を投げてテストを失敗にします。報告をテストの出力にも書くなら、`OnExceptionRecorded` を上書きします。

```csharp
sealed class XunitTestWorld : TestWorld
{
    readonly ITestOutputHelper _output;
    public XunitTestWorld(ITestOutputHelper output) => _output = output;
    protected override void OnExceptionRecorded(FlowExceptionInfo info) => _output.WriteLine(info.ToString());
}

public class TitleTests
{
    readonly ITestOutputHelper _output;
    public TitleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void StartGoesToTheMenu()
    {
        using var tw = new XunitTestWorld(_output);
        var start = new Signal<FlowUnit>();
        var h = tw.World.Run(Title(start));

        start.Emit(FlowUnit.Default);
        tw.World.TickUntil(() => h.IsCompleted);

        Assert.Equal(Screen.Menu, h.Result);
    }
}
```

## スコープの木を確かめる

`FlowAssert` は、生きているスコープを調べるアサーションです。満たさなければ、ダンプを添えた `FlowAssertionException` を投げます。

```csharp
tw.World.Run(Game(input));
tw.World.TickFrames(1);

FlowAssert.ScopePathExists(tw.World, "Game > InGame > Battle");
FlowAssert.ScopeIsWaitingOn(tw.World, "Battle", "Next");

input.Emit(Key.Quit);
tw.World.TickFrames(1);
FlowAssert.NoLiveScopes(tw.World);   // every flow ended, nothing leaked
```

| メソッド | 確かめること |
|---|---|
| `ScopeExists(world, name)` | その名前の生きているスコープがある |
| `ScopeDoesNotExist(world, name)` | その名前の生きているスコープがない |
| `ScopePathExists(world, path)` | ちょうどそのパス（`"Game > InGame > Battle"`）のスコープがある |
| `ScopeIsWaitingOn(world, name, waitingFor)` | そのスコープの待ちの表示に `waitingFor` が含まれる |
| `NoLiveScopes(world)` | 生きているスコープがない |

スコープの名前は、メソッド名か `Flow.Named` で付けた名前です。待ちの表示は [デバッグと診断](debugging.md)のダンプと同じです。

## Unity で動かす

EditMode のテストは、PlayerLoop を回さずに `TestWorld` で動かせます。テストの asmdef は `FlowTask`、`FlowTask.Testing`、`FlowTask.Testing.NUnit` を参照します。

```json
{
    "name": "MyGame.Tests",
    "references": ["FlowTask", "FlowTask.Testing", "FlowTask.Testing.NUnit", "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
    "includePlatforms": ["Editor"],
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll"],
    "autoReferenced": false,
    "defineConstraints": ["UNITY_INCLUDE_TESTS"]
}
```

PlayMode とテストプレイヤーで走らせるなら、`includePlatforms` を空にし、`UnityEditor.TestRunner` を外します。

> **注意**：テストのパッケージは、manifest の `dependencies` に加えて `testables` にも書きます。書かないと、パッケージがコンパイルされず、参照するテストが `CS0246` で止まります（[インストール](../getting-started/installation.md)）。`com.katout.flowtask.testing.nunit` は `com.unity.test-framework` 1.1.33 以上に依存します。

Unity のテストのメインスレッドには `UnitySynchronizationContext` があります。`TickUntil` や `RunUntilComplete` で World を回しながらブリッジした Task を待つと、ファクトリの中の `ConfigureAwait(false)` のない await の続きがコンテキストに Post され、ループの間は走りません。`TickUntil` は `TimeoutException` で終わります。次のどれかで避けます。

- 偽の Task を完了済みで作る（`Task.FromResult` など）。
- ファクトリの中の await に `ConfigureAwait(false)` を付ける。
- テストの名前空間に、テストの間だけコンテキストを外す `[SetUpFixture]` を置く。

```csharp
using System.Threading;
using NUnit.Framework;

namespace MyGame.Tests.Flows // applies to the tests in this namespace
{
    [SetUpFixture]
    public sealed class NoUnitySynchronizationContext
    {
        SynchronizationContext _saved;

        [OneTimeSetUp]
        public void Remove()
        {
            _saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [OneTimeTearDown]
        public void Restore() => SynchronizationContext.SetSynchronizationContext(_saved);
    }
}
```

最後の方法は、PlayerLoop を回す PlayMode の `[UnityTest]` には使いません。`ConfigureAwait(false)` のない await の続きが、メインスレッドに戻らなくなります。

Unity のテストのサンプルは [Unity のサンプル](../unity/samples.md)にあります。

## Godot で動かす

Godot の `Node` などは、Godot のプロセスの外では使えません。テストしたいフローを、Godot の型に触れない形（素の `Signal<T>` を受け取る、渡された Clock で待つ）に分けておけば、別のテストのプロジェクト（net8.0 以上、NuGet の `FlowTask.Testing`）で `dotnet test` できます。

Godot の中で NUnit のテストを走らせる例は、リポジトリの `tests/godot` の `CoreSuiteRunner.cs` にあります。
