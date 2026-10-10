# エンジン上の検証

FlowTask のリポジトリが Unity、Godot で自分を検証する仕組みの説明です（保守する人向け。利用者向けの文書はサイトの `docs/ja/` にあります）。コマンドの一覧は [CONTRIBUTING.ja.md](../../CONTRIBUTING.ja.md) にあります。

## Unity：検証プロジェクト `tests/unity/`

```
tests/unity/
  Packages/manifest.json   FlowTask の 5 パッケージ（file:）、UniTask（git URL。ネットワークが要る）、test-framework、ugui
  Assets/AllocationProbe/  Unity 用の割り当ての計測（下の「割り当ての計測」）
  Assets/Tests/EditMode/   コアのテスト（エディタ）。Synced/ ← tests/FlowTask.Core.Tests/*.cs
  Assets/Tests/PlayMode/   コアのテスト（すべてのプラットフォーム）。Synced/ ← 同上
  Assets/Tests/Unity/      PlayerLoop、ブリッジ、寿命、uGUI、割り当ての計測のテスト
  Assets/Tests/UniTask/    UniTask のブリッジのテスト
  Assets/Tests/Editor/     Scope Tree ウィンドウ、.meta、asmdef、アナライザの DLL のテスト
  Assets/Samples/          パッケージのサンプル（src/FlowTask.Unity/Samples~/Scenarios）のコピー（gitignore）。テストは Assets/Tests/Samples/
  Assets/Editor/           テストプレイヤーの設定（TestPlayerSetup.cs）、テストの開始のログ（TestProgressLog.cs）
```

`tools/unity/run-tests.ps1`（CI では `unity.yml`）が、`tests/FlowTask.Core.Tests/*.cs` を EditMode 用と PlayMode 用の `Synced/`（gitignore）に **そのままコピー** します。パッケージのサンプル（`src/FlowTask.Unity/Samples~/Scenarios`）も `.meta` ごと `Assets/Samples/` にコピーします。Package Manager の Import と同じ形で、利用者が取り込むものをそのままテストするためです。Unity で違うことは、コピーで書き換えず、テストのソースの中の `#if UNITY_5_3_OR_NEWER` に書きます。

- 割り当ては `Allocations.CurrentThreadBytes()`（`TestSupport.cs`）で測り、Unity では `AllocationProbe` を呼びます。
- Unity でコンパイルできないファイルは全体を `#if !UNITY_5_3_OR_NEWER` で囲みます。今は `ConstraintTests.cs`（`System.Reflection.Metadata` で DLL の IL を調べる）です。
- `GlobalUsings.cs` もコピーされるので、その global using はコピー先のアセンブリの他のファイルにも効きます（Godot のスモークでも同じ。そのため Godot の型とぶつかる `System.IO` と `System.Threading` は入れない）。

コアのテストのアセンブリには、コピーの対象でない `UnityTestEnvironment.cs`（利用者のテストの環境の設定と同じ `[SetUpFixture]` をグローバル名前空間に置いたもの）があり、.NET の NUnit と同じくコンテキストなしで走らせます。

テストのアセンブリの隣の `csc.rsp` は次のとおりです（最後の行はコアのテストだけ）。

```
-langversion:latest
-optimize+
-nowarn:1998
-nowarn:FLOW001;FLOW002;FLOW003;FLOW004;FLOW005;FLOW006;FLOW007;FLOW008;FLOW009;FLOW010
```

- `-langversion:latest` は Unity 6 の Roslyn 4.3.1 では C# 10 です（`LangVersionTests.cs` が確かめる）。サンプル（`src/FlowTask.Unity/Samples~/`、`Assets/Tests/Samples/`）、UniTask のテスト、エディタの設定（`Assets/Editor/`）には `csc.rsp` を置かず、利用者のコードと同じ既定の C# 9 でコンパイルします。
- パッケージ（`src/`）の asmdef の隣の `csc.rsp` は、`latest` ではなく `-langversion:10.0` と書きます。Roslyn の新しい Unity で、通る構文が Unity の版によって変わらないようにするためです。UPM パッケージに asmdef を足したら、同じ `csc.rsp` とその `.meta` も足します（`ConstraintTests.CoreIsCSharp10OnNetStandard21AndNet10` が確かめる）。
- MonoBehaviour と ScriptableObject（EditorWindow を含む）を定義するファイルは、block の namespace にします。Unity はこれらのクラスを、コンパイラではなく自前の解析でスクリプトのファイルから探し、file-scoped namespace の中のクラスを見つけません（コンパイルは通るが `MonoScript.GetClass()` が null になり、Inspector から追加できず、そのクラスを使うアセットは参照を失う）。こうしたファイルを足したら `.editorconfig` の一覧に足します。`EditorIntegrationTests.EveryMonoBehaviourAndScriptableObjectIsFoundThroughItsScript` が、パッケージ、`Assets/Tests/Unity`、`Assets/Samples` のスクリプトについて、Unity が型を見つけられることを確かめます。
- `-optimize+` は Development のテストプレイヤーでも割り当てを正しく測るため、`-nowarn:1998` はすぐ終わる `async FlowTask` をテストで書くためです。
- コアのテストは実行時の検出を確かめるために FLOW 規則にわざと違反するので、規則をすべて切ります。規則を足したら 2 つの `csc.rsp` に足します（`TheUnityCoreTestAssembliesTurnOffEveryFlowRule` が確かめる）。

## Unity：実行

`tools/unity/run-tests.ps1` が、コアのテストのコピー、`.meta` の生成、Unity の実行、結果の集計をまとめて行います（Windows）。

```powershell
./tools/unity/run-tests.ps1                              # EditMode, PlayMode, StandaloneMono, AndroidIl2cppBuild
./tools/unity/run-tests.ps1 -Suites EditMode
./tools/unity/run-tests.ps1 -Suites StandaloneIl2cpp     # needs the Windows Build Support (IL2CPP) module
./tools/unity/run-tests.ps1 -Suites EditMode -TestFilter "Katout.FlowTask.Tests.AllocationTests" -TimeoutMinutes 10
```

- スイート：`EditMode`、`PlayMode`（Editor の中）、`StandaloneMono`、`StandaloneIl2cpp`（Windows x64 のプレイヤーを Test Framework がビルドして実行。IL2CPP のモジュールがなければ飛ばす）、`AndroidIl2cppBuild`（Android の IL2CPP / ARM64 のテストプレイヤーのビルドだけ。AOT コンパイルを確かめる）。
- Unity は `-UnityVersion` か `-UnityExe` で選びます。`-NoSync` はコアのテストとサンプルのコピーを飛ばします（clone した直後は、一度は付けずに実行する）。
- どの実行にも `-releaseCodeOptimization` を付けます（Debug のコンパイルでは async のステートマシンがクラスになり、割り当てのテストがコンパイラを測ってしまう）。
- 結果の XML と Editor のログは `tests/unity/TestResults/` に出て、`summary.json` に集計されます。失敗したスイートがあると終了コードは 1 です。
- Windows のプレイヤーは毎回同じ `tests/unity/TestResults/StandalonePlayer/PlayerWithTests/PlayerWithTests.exe` にビルドします（`-buildPlayerPath`）。Windows Defender ファイアウォールは実行ファイルのパスごとに答えを覚えるので、アクセスの許可を尋ねるのは最初の 1 回だけです。

**タイムアウト**：1 スイートの実行は `-TimeoutMinutes`（既定は EditMode と PlayMode が 15 分、プレイヤーのスイートがビルドを含めて 30 分）で打ち切り、Unity のプロセスの木を止めて、`summary.json` の `StuckTest` に止まっていたものの名前を書きます。

- `StuckTest` は、`TestProgressLog.cs` が Editor のログに書く `[FlowTask] test started:` と `suite started:` の最後の行です。スイートなら、その `[OneTimeSetUp]` か `[SetUpFixture]` で止まっています。
- プレイヤーのスイートでは、プレイヤーが開始を 1 フレームに 1 通ずつ送るので、`StuckTest` は Editor が最後に受け取った開始で、止まったテストより前のものです。止まったテストは、プレイヤーのログか、同じテストを PlayMode で走らせて探します。
- `[UnityTest]` のコルーチンは Test Framework のタイムアウト（180 秒）で失敗して終わります。上限まで待つのは、メインスレッドを止めるテストと、ビルドやインポートが止まったときです。

## Unity：割り当ての計測

`AllocationProbe` は起動時に既知の割り当てで各手段を較正し、観測できた最も精密なものを使います。

| モード | 手段 | 使われる環境 |
| --- | --- | --- |
| `ProfilerGcAllocatedInFrame` | `ProfilerRecorder` の `GC Allocated In Frame`（バイト単位） | エディタ、Development のプレイヤー |
| `BoehmTotalBytes` | IL2CPP の Boehm GC の `GC_get_total_bytes()`（プロセス全体で粗い） | IL2CPP の Release のプレイヤー |
| `GcHeapUsage` | `Profiler.GetMonoUsedSizeLong()` | 最後の手段 |

精密なモードでは、コアのテストの判定（最後の 2 回で 0 バイト）がそのまま使われます。粗いモードでは 64 回分をまとめて測り、4096 バイト（GC のブロック 1 個分）以下を求めます。`AllocationProbeTests` は、手段が既知の割り当てを実際に観測できることを確かめます。

## Unity：CI

`.github/workflows/unity.yml` は、GitHub の Linux のランナーで game-ci の Unity のイメージを使い、EditMode と PlayMode を実行します（Unity の版は `tests/unity/ProjectSettings/ProjectVersion.txt` から決まる）。Unity がコンパイルか実行するファイルを変えたプルリクエストと main への push、リリース（`release.yml` が呼ぶ）、手動の起動で走ります（[CI とリリース](ci-and-release.md)）。プレイヤーは CI では動かないので、手元の `run-tests.ps1` で確かめます。

ライセンスは、リポジトリの Settings → Secrets and variables → Actions に登録します。

1. Personal：Unity Hub でライセンスを有効にしたマシンの `Unity_lic.ulf` の中身を `UNITY_LICENSE` に登録します（Windows は `C:\ProgramData\Unity\`、macOS は `/Library/Application Support/Unity/`、Linux は `~/.local/share/unity3d/Unity/`）。あわせて `UNITY_EMAIL` と `UNITY_PASSWORD` を登録します。
2. Plus / Pro：`UNITY_SERIAL`、`UNITY_EMAIL`、`UNITY_PASSWORD` を登録します。
3. シークレットがないとき（フォークと Dependabot からのプルリクエストを含む）、ジョブはテストを飛ばして通知だけを出します。
4. CI での Personal ライセンスの利用条件は、https://game.ci/docs/github/activation と Unity の規約で確かめてください。

Action（`game-ci/unity-test-runner`）はシークレットを受け取り、v4.3.2（v4 の構成の最後の版。v4.4.0 から game-ci/cli を呼ぶ形に変わった）に留めているので、Dependabot の対象から外しています。上げるときは手で SHA を書き換え、リリースノートを読みます。

## Godot：スモークテスト

`tests/godot/` は、FlowTask.Godot を実際のエンジンで確かめる最小の Godot プロジェクトです。FlowTask のプロジェクトを直接参照するリポジトリ用のテストで、ゲームへの導入には NuGet パッケージを使います。

| ファイル | 内容 |
| --- | --- |
| `FlowAutoload.cs` | autoload。物理用の World（docs/ja/godot/physics.md）も持つ |
| `SmokeMain.cs`（`Main.tscn`） | エンジン統合のチェックを実際のフレームの上でフローとして実行し、`[smoke] PASS/FAIL` を出す。最後にコアのテストスイートを Godot のプロセスの中で実行する |
| `HandoverMain.cs`（`Handover.tscn`） | autoload をツリーから外し、`Dispose` 中に `UnhandledException` のハンドラが投げても、次にツリーに入った FlowWorldNode が `Default` を引き継ぐことを確かめる |
| `FixedFpsMain.cs`（`FixedFps.tscn`） | `--fixed-fps 60` で固定のフレームレートの規則を確かめる |
| `WallClockWorldNode.cs` | docs/ja/godot/setup.md の `GetDeltaTime` の override の例と同じ中身 |
| `CoreSuiteRunner.cs` | `tests/FlowTask.Core.Tests/*.cs` をそのままリンクしたコアのテストスイートを、Godot のプロセスの中で NUnit で実行する |
| `Samples/` | サンプル（`samples/godot`。利用者向けの説明は docs/ja/godot/samples.md）を擬似入力で動かして判定する部分。サンプルのソースは `GodotSmoke.csproj` が `samples/godot` から取り込む（プロジェクトのファイルとデモを除く） |

エンジンに依存しないサンプルのファイル（`GameClocks.cs`、`BackKey/BackKeyRouter.cs`、`Pause/GameSettings.cs`、`Shop/Store.cs`、`Shop/Shop.cs`）は、`src/FlowTask.Unity/Samples~/Scenarios` と `samples/godot` に 1 つずつあります。どちらのサンプルもそれだけで写せるようにするためです。違うのは namespace の書き方と、エンジンの名前を書いたコメントだけなので、直すときは両方を直します。

- コアのテストは NUnit が立てたスレッドで 1 件ずつ走り（メインスレッドではないので、テストがメインスレッドを止めてデッドロックすることはない）、`CoreSuiteRunner` が見張ります。1 件が 60 秒を超えると、そのテストの名前を出してスモークテストを失敗させます。
- コアのテストのうち、リポジトリのファイルを読む検査（`ConstraintTests`）は、Godot ではアセンブリにリポジトリのパスがないので結果なし（inconclusive）になります。
- ログの `ERROR:` と `WARNING:` の行の一部は意図したものです。直前に `[smoke] (the next ERROR line is expected ...)` のような行が出ます。

### 実行方法

リポジトリの `global.json` のとおり .NET SDK 10.0.300 以上が要ります。

```powershell
powershell -ExecutionPolicy Bypass -File tools/godot/run-smoke.ps1
```

スクリプトは次の順に進みます。

1. Godot 4.4.1 の .NET 版（`Godot_v4.4.1-stable_mono_win64.zip`）を GitHub Releases から `%LOCALAPPDATA%\FlowTask\godot-cache` にダウンロードし、SHA-512 を確かめてから展開する。既定の版の値はスクリプトに固定してあり、`-GodotVersion` で別の版を指定したときはそのリリースの `SHA512-SUMS.txt` と比べる。確かめられないときは展開せずに止まる。
2. `dotnet build tests/godot -c Debug` と `dotnet build samples/godot -c Debug`（エディタのバイナリは `.godot/mono/temp/bin/Debug` を読む）。
3. headless で 3 つのシーンを別々のプロセスで実行する：`Main.tscn`、`Handover.tscn`、`--fixed-fps 60` を付けた `FixedFps.tscn`。続けて、サンプルのデモ（`samples/godot`）を `--quit-after 300` で起動する。
4. 4 回の終了コードがすべて 0 で、ログに `SMOKE RESULT: PASS` と `EXIT UNWIND OK`、`HANDOVER RESULT: PASS`、`FIXEDFPS RESULT: PASS` があり、デモのログに `ERROR` の行がなければ成功（終了コード 0）。それ以外は 1。

オプション：`-GodotExe <path>`（手元のエンジンを使う。検証はしない）、`-GodotVersion`、`-CacheDir`、`-LogFile`、`-TimeoutSec`（Godot の実行 1 回ごとの上限。既定 300 秒。超えると `SMOKE: TIMEOUT` を出して終了コード 1）、`-SkipBuild`。

手動で行う場合：

```powershell
dotnet build tests/godot -c Debug
& "$env:LOCALAPPDATA\FlowTask\godot-cache\Godot_v4.4.1-stable_mono_win64\Godot_v4.4.1-stable_mono_win64_console.exe" --headless --path tests/godot
```

CI（`.github/workflows/ci.yml`）は同じスクリプトを Windows のランナーで実行します。

---
