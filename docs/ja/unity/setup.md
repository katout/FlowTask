# Unity のセットアップ

Unity で FlowTask を動かす仕組みと、その設定を説明します。既定の World が PlayerLoop でどう動くか、時間の進み方、Play Mode の終了やドメインリロードでどうなるか、未処理の例外がコンソールにどう出るかが分かります。

パッケージの導入（`Packages/manifest.json` の書き方、任意のパッケージ）は [インストール](../getting-started/installation.md) にあります。このページは導入の後の話です。

## 最初のフロー

導入すると、起動時に既定の `FlowWorld` が作られ、PlayerLoop で毎フレーム動きます。フローは `FlowTaskUnity.World.Run` で始めます。

```csharp
using Katout.FlowTask;
using Katout.FlowTask.Unity;
using UnityEngine;

public sealed class Boot : MonoBehaviour
{
    void Start() => FlowTaskUnity.World.Run(Game());

    static async FlowTask Game()
    {
        await FlowTask.WaitForSeconds(1.0); // DefaultClock advances like Time.deltaTime
        // ...
    }
}
```

World は `RuntimeInitializeLoadType.SubsystemRegistration` で作られるので、どの `Awake` よりも前からあります。World がないとき（Edit Mode、`Shutdown` の後、再生中の再コンパイルの後）に `FlowTaskUnity.World` を読むと、原因を書いた `InvalidOperationException` になります。あるかどうかは `FlowTaskUnity.IsInstalled` で調べます。このページのほかの例も、同じ 3 つの `using`（`Katout.FlowTask`、`Katout.FlowTask.Unity`、`UnityEngine`）を前提にします（`Math` や `ArgumentOutOfRangeException` を使う例は `using System;` も）。

GameObject が無効になったらフローを止めたいときは、`World.Run` ではなく `gameObject.RunWhileActive` で始めます（[GameObject の寿命](lifetime.md)）。

## パッケージとアセンブリ

対応するのは Unity 2023.1 以降です。動作を確かめているのは、Unity 6（6000.3）の Editor（EditMode、PlayMode）、Windows の Mono と IL2CPP のプレイヤー、Android の IL2CPP のビルド（端末での実行はしていない）です。2023.1 と 2023.2 そのものでは確かめていません。

パッケージは次のアセンブリ（asmdef）に分かれています。コードを asmdef に置いているなら、使うアセンブリを参照に足します。

| asmdef | 参照 | コンパイルされる条件 |
| --- | --- | --- |
| `FlowTask` | なし（`noEngineReferences`） | 常に |
| `FlowTask.Unity` | FlowTask | 常に。PlayerLoop 統合、ブリッジ、GameObject の寿命 |
| `FlowTask.Unity.UI` | FlowTask、FlowTask.Unity、UnityEngine.UI | `com.unity.ugui` があるとき（`FLOWTASK_UGUI`） |
| `FlowTask.Unity.Editor` | FlowTask、FlowTask.Unity | エディタだけ。Scope Tree ウィンドウ |
| `FlowTask.Testing` | FlowTask | エディタとテストプレイヤーだけ（`UNITY_INCLUDE_TESTS`）。`autoReferenced: false` |
| `FlowTask.Testing.NUnit` | FlowTask、FlowTask.Testing、`nunit.framework.dll` | 同上 |
| `FlowTask.UniTask` | FlowTask、UniTask | `com.cysharp.unitask` 2.0.0 以上があるとき（`FLOWTASK_UNITASK`） |

- `FlowTask.Unity.UI` と `FlowTask.UniTask` は、依存するパッケージがなければ黙ってコンパイルされません（エラーにならない）。
- UniTask を `.unitypackage` で `Assets/Plugins/UniTask` に入れた場合は、パッケージとして見えないので `FLOWTASK_UNITASK` が定義されず、`FlowUniTask` を使うコードが `CS0246` になります。Player Settings > Other Settings > Scripting Define Symbols に `FLOWTASK_UNITASK` をプラットフォームごとに足してください。
- 2 つのテスト用アセンブリは、manifest の `testables` に書いたパッケージでしかコンパイルされません（[インストール](../getting-started/installation.md)）。

### C# 10 と csc.rsp

パッケージのソースは C# 10 で書かれています。Unity の既定は C# 9 なので、各 asmdef の隣に `-langversion:10.0` の 1 行だけの `csc.rsp` があり、そのアセンブリだけの言語バージョンを上げています。あなたのコードの言語バージョンは既定のまま変わりません。

IDE によっては、パッケージのソースの C# 10 の構文が誤りとして表示されます。Unity が IDE 用に生成する csproj に `csc.rsp` の言語バージョンを反映するのは、Rider のパッケージと、2.0.24 以降の Visual Studio のパッケージです。反映しない IDE で誤りが表示されても、Unity のコンパイルには影響しません。

### .meta ファイル

git から入れたパッケージは読み取り専用なので、パッケージのすべてのファイルとフォルダの `.meta` がリポジトリに入っています。パッケージを `Packages/` に埋め込んで手を入れるときは、ファイルの名前や場所を変えるなら `.meta` も一緒に動かしてください（GUID がアセットの同一性です）。

パッケージのフォルダには .NET 用の csproj もありますが、Unity では単なるアセットとして取り込まれ、コンパイルには関係しません。

## Tick と Flush

既定の World は、1 フレームに 1 回、Update フェーズの先頭（`Update.ScriptRunBehaviourUpdate` の直前）で Tick します。加えて、Update フェーズの末尾と PreLateUpdate フェーズの末尾で `FlowWorld.Flush()` を呼びます。Flush は時間を進めずに、その時点までに予約された再開（Emit、`FlowProperty.Set`、UnityEvent、完了したブリッジ）を処理します。

```
EarlyUpdate        input
FixedUpdate        physics
Update             [FlowTask Tick] -> MonoBehaviour.Update, uGUI clicks, coroutines -> [Flush]
PreLateUpdate      MonoBehaviour.LateUpdate, animation events -> [Flush]
PostLateUpdate     rendering
```

この順序から、次のことが言えます。

- Tick は入力と物理の後なので、フローはそのフレームの状態を見て動けます。フローが変えた状態は、同じフレームの `MonoBehaviour.Update` から見えます。
- `MonoBehaviour.Update`（uGUI のクリックを含む）と、Update フェーズで走るコルーチンと Awaitable の続き（`yield return null`、`NextFrameAsync` など）で起きた Emit は、Update の末尾の Flush で、描画の前の同じフレームのうちに受け手を再開します。`LateUpdate` とアニメーションイベントで起きたものは、PreLateUpdate の末尾の Flush が処理します。
- FixedUpdate で起きたもの（`WaitForFixedUpdate` と `FixedUpdateAsync` の続きを含む）は、そのフレームの Tick で処理します。PreLateUpdate より後で起きたもの（`WaitForEndOfFrame` と `EndOfFrameAsync` の続きなど）は、次のフレームの Tick で処理します。早めたいときは、Flush ポイントを足します（下の「設定」）。
- 空の Flush はフィールドを数個読むだけなので、Flush ポイントを増やしても費用はほとんどかかりません。

`RunWhileActive` で結んだ GameObject を無効にしたときは、この表とは別に、その場で Flush します（[GameObject の寿命](lifetime.md)）。

各システムは `FlowTaskUnity.FlowTaskTick` などのマーカー型の名前で Profiler に出ます。

Emit が受け手を同期では再開しないことなど、Tick の中の順序の規則は [実行モデル](../advanced/execution-model.md) にあります。

## 時間

既定の World は `Time.unscaledDeltaTime`（`Time.maximumDeltaTime` で切り詰めたもの）で Tick し、`DefaultClock` がそのフレームの `Time.deltaTime` と同じだけ進むように、`World.DefaultClock.TimeScale` を毎フレーム決めます。

```csharp
var world = FlowTaskUnity.World;
await FlowTask.WaitForSeconds(2.0);                     // DefaultClock: follows Time.timeScale, stops at 0
await FlowTask.WaitForSeconds(2.0, world.UnscaledClock); // keeps running while Time.timeScale is 0
```

- `DefaultClock` は、起動直後のフレームも含めて、毎フレーム `Time.deltaTime` と同じだけ進みます。`Time.timeScale = 0` で止まります。
- `UnscaledClock` は `Time.timeScale = 0` の間も進みます。TimeScale は変えられず、Pause もできません。
- 長いフレーム（シーンの読み込み、ブレークポイント）は、Unity と同じく `Time.maximumDeltaTime` で切り詰められます。1 回の Tick で期限を過ぎた時間待ちは、期限の順ではなく、待ち始めた順に完了します（Race なら引数の順）。
- `WaitForSeconds` は、dt を double で足した時刻が目標に届いた最初の Tick で終わります。60 fps で 0.1 秒を待つと、7 回目の Tick で終わります（1/60 を 6 回足しても 0.1 にわずかに届かない）。フレーム数で待つなら `FlowTask.DelayFrames` を使います。

Clock、Pause、TimeScale の一般的な使い方は [時間と Clock](../guide/time-and-clocks.md) にあります。

### 独自のスローモーションとポーズ

`DefaultClock.TimeScale` は統合が毎フレーム上書きするので、自分では変えません。ゲームのスローモーションやポーズは、`DefaultClock` の子の Clock で行います。

```csharp
var world = FlowTaskUnity.World;
var game = world.CreateClock("Game");                    // a child of DefaultClock: follows Time.timeScale
var ui = world.CreateClock("UI", world.UnscaledClock);   // keeps running while Game is paused and at Time.timeScale 0
game.TimeScale = 0.5;                                    // slow motion on top of Time.timeScale
```

- **ダイアログやメニューは UI の Clock で動かします**（`Flow.WithClock(ui, Menu())`）。ゲームの Clock を `Pause()` するメニューがその Clock の上で動いていると、自分の再開も止まります（`PausedOwnClock` の警告が出ます）。
- **敵ごとのスロー**は、その場かぎりの Clock を `Flow.CreateClock` で作ります。作ったスコープが終わると削除されます。

  ```csharp
  async FlowTask SlowedAttack()
  {
      var slow = Flow.CreateClock("slow", Flow.CurrentClock); // removed when this method's scope ends
      slow.TimeScale = 0.5;
      await Flow.WithClock(slow, Attack());
  }
  ```

  AI のループの中で直接 `Flow.CreateClock` を呼ぶと、Clock がループのスコープ（敵の寿命）の終わりまで積み上がるので、上のようにメソッドに分けます。World が終わるまで使う Clock は `FlowWorld.CreateClock` で作ります。

- `Time.timeScale` と子の Clock の倍率の積が有限でなくなるときは、`DefaultClock` への倍率の設定が `ArgumentOutOfRangeException` で拒否されます。統合はその例外をログに出し、そのフレームは前の倍率のまま Tick します。

Clock の Pause が止めるのは、その Clock で動くフローだけです。Animator や物理も一緒に止めるには、Pause の間だけ `Time.timeScale` を 0 にします（[サンプル](samples.md) の `PauseLink`）。

### 詳しく：DefaultClock.TimeScale が Time.timeScale と違うフレーム

`DefaultClock.TimeScale` はふつう `Time.timeScale` と同じ値です。ただし `Time.deltaTime` が「非スケールの時間 × timeScale」にならないフレームでは、`DefaultClock` が `Time.deltaTime` だけ進むように、その比が入ります。次のフレームです。

- 起動直後のフレーム。Unity が仮の `Time.deltaTime` を報告します。
- Tick より前（FixedUpdate、EarlyUpdate、PreUpdate の Input System のコールバック、UniTask の `PlayerLoopTiming.Update` 以前の続き）に `Time.timeScale` を変えたフレーム。新しい値は次のフレームから効きます。
- `Time.captureDeltaTime` を設定している間（一定のフレームレートで録画するとき）。`DefaultClock` は録画の時間で進み、`UnscaledClock` とその子の Clock は実時間（`Time.unscaledDeltaTime`）で進みます。録画とそろえたい待ちは、`DefaultClock` かその子の Clock で行います。

## 設定

設定は `FlowTaskSettings` で、コードからもアセットからも変えられます。

```csharp
FlowTaskUnity.Configure(new FlowTaskSettings
{
    FlushPoints = FlowFlushPoints.Default | FlowFlushPoints.AfterFixedUpdate,
    OnUnhandledException = info => MyCrashReporter.Report(info.Exception, info.ScopePath), // replaces the default logging
});
```

| 設定 | 意味 | 既定 |
| --- | --- | --- |
| `AutoTick` | Update の先頭で Tick するか。`false` なら自分で Tick する（Flush ポイントは動く） | `true` |
| `FlushPoints` | Flush する場所。`FlowFlushPoints` のフラグ：`AfterEarlyUpdate`、`AfterFixedUpdate`、`AfterUpdate`、`AfterLateUpdate`、`EndOfFrame`、`Default`、`All`、`None` | `Default`（`AfterUpdate \| AfterLateUpdate`） |
| `AutoInstall` | 起動時に World を作り、PlayerLoop に組み込むか。アセットからだけ読まれる | `true` |
| `OnUnhandledException` | 未処理の例外などの報告の処理を差し替える。コードからだけ | 下の「未処理の例外と警告」 |

- `Configure` はいつでも呼べます。`OnUnhandledException` はすぐに、Tick と Flush ポイントの変更は次のフレームから効きます。`FlowTaskUnity.Configure` で入れ直しても、UniTask など他のライブラリが PlayerLoop に入れたシステムは残ります。
- アセットで設定するときは、`Assets > Create > FlowTask > Settings` で `FlowTaskSettingsAsset` を作り、`FlowTaskSettings` という名前で `Resources` フォルダに置きます。最初のシーンのロードの前に適用されます。
- 起動時の順：`SubsystemRegistration` で既定の設定の World を作り（`FLOWTASK_DISABLE_AUTO_INSTALL` を定義したときは作らない）、`BeforeSceneLoad` でアセットを適用します。それより前に（`AfterAssembliesLoaded` や `BeforeSplashScreen` の `RuntimeInitializeOnLoadMethod` から）コードが `Initialize`、`Configure`、`Shutdown` を呼んでいたら、アセットは適用しません。コードの設定（`OnUnhandledException` を含む）が勝ち、コードが組み込んだ World はアセットの `AutoInstall` が `false` でも残ります。`FLOWTASK_DISABLE_AUTO_INSTALL` を定義したときは、アセットを読みません。
- 今の設定は `FlowTaskUnity.Settings` で読めます。返るのは写しで、書き換えても `Configure` に渡すまで効きません。

### 自動の起動を止める

アセットの `AutoInstall` を `false` にするか、スクリプティングシンボル `FLOWTASK_DISABLE_AUTO_INSTALL` を定義すると、自動では World を作りません。自分で `FlowTaskUnity.Initialize(settings)` を呼んで始めます。自分で作った World を既定の World にするなら `Initialize(settings, myWorld)` で渡します。渡した World は `Shutdown` で Dispose されません。渡した World にすでに `OnUnhandledException` があれば、それを残します（`FlowTaskSettings.OnUnhandledException` と既定のログは効きません）。なければ統合のハンドラを入れ、`Shutdown` で外します。

### 自分で Tick する

`AutoTick = false` にしたときは、既定と同じ手順を自分で書きます。

```csharp
void Update()
{
    var world = FlowTaskUnity.World;
    FlowLifetime.PollWatched(); // completes WaitForDestroy of objects that never became active
    if (world.DefaultClock.TimeScale != Time.timeScale) world.DefaultClock.TimeScale = Time.timeScale;
    world.Tick(Math.Min(Time.unscaledDeltaTime, Time.maximumDeltaTime));
}
```

`Time.deltaTime` を渡すと、`UnscaledClock` まで `Time.timeScale` に従ってしまいます。非スケールの時間を渡し、倍率は `DefaultClock.TimeScale` で与えてください。

## 終了とドメインリロード

Play Mode の終了（シーンのオブジェクトが破棄される前）とプレイヤーの終了（`Application.quitting`）で、`FlowTaskUnity.Shutdown()` が呼ばれます。PlayerLoop からシステムを外し、World を Dispose するので、すべてのフローが巻き戻ります。ただし Dispose は Tick を回さないので、`finally` の中の await は最初の await までしか走りません（下の「終了時のセーブ」）。

Enter Play Mode Options でドメインリロードを無効にしても、起動のフックは Play Mode に入るたびに走り、前回の World を片付けてから新しい World を作ります。

### Signal などは World ごとに作る

`Signal<T>`、`FlowProperty<T>`、`Once<T>` は、最初に使った World に結び付きます。その World が Dispose された後に別の World から使うと、`FlowMisuseException` になります。

```csharp
// Wrong: with domain reload disabled, the second Play Mode session throws
static readonly Signal<int> s_damaged = new("Damaged");
```

static のフィールドに置くと、ドメインリロードを無効にしたときの 2 回目の再生で例外になります。World ができた後（`Start` など）に作り、フローには引数で渡してください。サンプルの `GameClocks` と `BackKeyRouter` がこの形です（[サンプル](samples.md)、[シグナル](../guide/signals.md)）。

### 再生中の再コンパイル

Preferences > General > **Script Changes While Playing** が「Recompile And Continue Playing」だと、再生中にスクリプトを変えたとき、Unity は再生を続けたままドメインをリロードします。FlowTask はこの状態から自動では復旧しません（理由は [設計の背景](../advanced/design-rationale.md) の「エンジンのオブジェクトの寿命」）。

- 既定の World は消えます。`RunWhileActive` で始めたフローは、Unity がリロードの前に呼ぶ `OnDisable` で巻き戻りますが、ほかのフローは巻き戻らずに消えます（`finally` は走らない）。
- 再生を止めるか `FlowTaskUnity.Initialize()` を呼ぶまで、FlowTask は止まったままです。リロードの後の `FlowTaskUnity.World` と `RunWhileActive` は `No FlowTask World: ...` で始まる `InvalidOperationException` を投げます。エディタはリロードの直前に警告を出し、この例外の文面と Scope Tree ウィンドウも、原因と設定の名前を示します。
- `Initialize()` で動かし直しても、消えたフローは戻りません。`Awake` と `Start` も呼ばれ直さないので、そこで始めるフローも始まりません。

この設定は「Recompile After Finished Playing」か「Stop Playing and Recompile」にしてください。

### 終了時のセーブ

終了の `Shutdown` で `finally` の await は最初の await で切られます。`Flow.NonCancelable` の await も切られ、`CleanupCutAtDispose` の警告が出ます。終了の前にセーブを最後まで終わらせるには、`Application.wantsToQuit` で終了をいったん取り消し、セーブが終わってから `Application.Quit()` を呼び直します。

```csharp
// on an object that lives for the whole session (DontDestroyOnLoad)
public sealed class SaveOnQuit : MonoBehaviour
{
    public IGameSave Save { get; set; } // your save system: FlowTask Write(), void WriteNow()
    FlowHandle _saving;
    bool _quitRequested;
    bool _saved;

    void OnEnable() => Application.wantsToQuit += WantsToQuit;
    void OnDisable() => Application.wantsToQuit -= WantsToQuit;

    bool WantsToQuit()
    {
        if (_saved || Application.isEditor) return true; // the Editor ignores Application.Quit()
        if (!_quitRequested)
        {
            _quitRequested = true;
            // Runs to the end, or gives up after 5 s; on the UnscaledClock, so also while Time.timeScale is 0.
            var world = FlowTaskUnity.World;
            _saving = world.Run(FlowTask.Race(Save.Write(), FlowTask.WaitForSeconds(5.0)).WithoutResult(), world.UnscaledClock);
        }
        return false; // not yet: the World keeps running while it saves
    }

    void Update()
    {
        if (!_quitRequested || _saved || !_saving.IsCompleted) return;
        _saved = true;
        Application.Quit(); // outside flow code: quitting disposes the World
    }

    // Android and iOS: the OS can end an app in the background without wantsToQuit.
    void OnApplicationPause(bool paused)
    {
        if (paused) Save.WriteNow(); // the PlayerLoop stops in the background: save synchronously
    }
}
```

World の Dispose と後始末の関係は [失敗の扱い](../guide/failures.md) にあります。

## 未処理の例外と警告

既定の World では、ルートに届いた未処理の例外は `Debug.LogException(new FlowTaskUnhandledException(info))` でコンソールに出ます。元の例外とスタックトレースの後に、スコープのパスが出ます。

```
InvalidOperationException: ...
  (stack trace)
Rethrow as FlowTaskUnhandledException: [Unhandled] at 'Game > InGame > Battle': InvalidOperationException: ...
```

- `FlowExceptionKind.Undelivered`（受け手がもういなかった例外。キャンセル済みのフロー、決着済みの Race、2 個目以降の例外など）だけは `Debug.LogWarning` で出します。普通のプレイでも起き、エラーにすると本物のエラーが埋もれるためです。
- `FlowTaskSettings.OnUnhandledException` を設定すると、上の出力はすべて置き換わります。種類で分けるなら `FlowExceptionInfo.Kind` を見ます。

  ```csharp
  OnUnhandledException = info =>
  {
      if (info.Kind == FlowExceptionKind.Undelivered) Debug.LogWarning($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
      else MyCrashReporter.Report(info.Exception, info.ScopePath);
  },
  ```

- World の警告（`FlushLimit`、`PausedOwnClock`、`LongCleanup`、`CleanupCutAtDispose`）は、種類ごとに World で 1 回、`Debug.LogWarning("[FlowTask] ...")` で出ます。止める設定はありません。

報告の種類と読み方は [失敗の扱い](../guide/failures.md) と [デバッグと診断](../tools/debugging.md) にあります。

## 自分で作る World（物理フレームなど）

物理フレームを 1 回待つだけなら、World を分けずに `await Awaitable.FixedUpdateAsync().AsFlow();` と書けます。続きは FixedUpdate の中ではなく、その後の Tick か Flush で走ります。

物理フレームごとに動かし続けたいフローは、自分の `FixedUpdate` から Tick する World で動かします。駆動源ごとに World を分けます。PlayerLoop の統合はこの World に何もしないので、未処理の例外と警告の出し先、Tick、Dispose を自分で書きます。

```csharp
[DefaultExecutionOrder(-10000)] // before the FixedUpdate of the other scripts
public sealed class PhysicsFlows : MonoBehaviour
{
    public FlowWorld World { get; private set; }

    void Awake()
    {
        World = new FlowWorld("UnityPhysics");
        // Without OnUnhandledException, the outermost Tick, Flush, Run or Dispose throws the reports (FlowUnhandledException).
        World.OnUnhandledException = info =>
        {
            if (info.Kind == FlowExceptionKind.Undelivered) Debug.LogWarning($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
            else Debug.LogException(new FlowTaskUnhandledException(info));
        };
        World.OnWarning += w => Debug.LogWarning("[FlowTask] " + w);
        FlowWorldRegistry.Register(World); // shown in the Scope Tree window
    }

    void FixedUpdate()
    {
        try
        {
            if (World.DefaultClock.TimeScale != Time.timeScale) World.DefaultClock.TimeScale = Time.timeScale;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Debug.LogException(ex); // rejected: keep the previous scale and tick anyway
        }
        World.Tick(Time.fixedUnscaledDeltaTime);
    }

    void OnDestroy()
    {
        FlowWorldRegistry.Unregister(World);
        World.Dispose(); // unwinds the physics flows
    }
}
```

- `OnUnhandledException` を設定しないと、報告はいちばん外側の `Tick`、`Flush`、`Run`、`Dispose` から `FlowUnhandledException` で投げられます。
- この World では、`NextFrame()` と `DelayFrames(n)` は物理フレームで数えます。
- `Time.timeScale = 0` の間は `FixedUpdate` が呼ばれないので、この World の `UnscaledClock` も止まります。ポーズ中にも進む必要のある待ちは、既定の World で動かします。
- World の間にスコープの親子はありません。別の World で始めたフローをこちらの寿命に結び付けるときは、ハンドルを持って `Flow.AddCleanup(handle.Cancel)` と書きます。
- World の間で `Signal<T>` をやり取りするときは、送る側が終わるときに `Close()` し、受ける側は `NextOrClosed()` で待ちます。相手の World が Dispose されても、待っている `Next()` は終わらないためです。Signal は長く生きる World で先に使います（上の「Signal などは World ごとに作る」）。
- `RunWhileActive` に第 2 引数でこの World の Clock（`World.DefaultClock` など）を渡すと、この World のフローも GameObject の寿命に結び付けられます（[GameObject の寿命](lifetime.md)）。フローの中の `WhileActive` は、await したフローの World で動きます。

Tick の手順とスレッドの規則は [フローと World](../guide/flows-and-world.md) と [スレッド](../guide/threads.md) にあります。

## アナライザ

コアのパッケージにはアナライザ（`Analyzers/FlowTask.Analyzers.dll`）が入っており、FlowTask を参照するすべてのアセンブリのコンパイルで働きます。既定でエラーのルール（FLOW001、FLOW002、FLOW005）は、Unity のコンパイルも止めます。ルールの一覧と、Unity で重大度を変える方法は [アナライザ](../tools/analyzers.md) にあります。

## IL2CPP とビルド

- コアは実行時のコード生成（`Reflection.Emit`）、リフレクション、ジェネリック仮想メソッドを使いません。IL2CPP と Managed Stripping でそのまま動きます（非同期メソッドビルダも AOT で具象化されます）。
- 割り当てがないのは、最適化したビルド（Release のプレイヤー、エディタの Code Optimization が Release）です。Development Build はスクリプトを最適化なしでコンパイルし、async のステートマシンがクラスになるので、`async FlowTask` メソッドは呼び出しごとに割り当てます。Development Build で割り当てを測るなら、asmdef の隣の `csc.rsp` に `-optimize+` を書きます。
- `GC.GetAllocatedBytesForCurrentThread()` は Unity では使えません（Mono では常に 0、IL2CPP では未実装）。割り当ては Profiler の GC Alloc 列か、`ProfilerRecorder("GC Allocated In Frame")` で確かめます。

割り当ての費用については [性能とメモリ](../advanced/performance.md) にあります。
