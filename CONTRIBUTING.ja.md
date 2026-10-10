[English](CONTRIBUTING.md) | 日本語

# FlowTask への貢献

FlowTask への貢献を歓迎します。不具合の報告や提案は [Issues](https://github.com/katout/FlowTask/issues) にお寄せください。大きな変更や公開 API の追加は、プルリクエストを作る前に Issue で相談してください（[公開 API](#公開-api)）。

FlowTask のコアは pure C#（netstandard2.1 と net10.0、C# 10）で書かれており、同じテストを .NET、Unity、Godot、NativeAOT で実行しています。関連する文書は次のとおりです。

- 利用者向けの文書：[docs/ja/](docs/ja/index.md)
- 内部構造：[docs/maintainers/internals.md](docs/maintainers/internals.md)
- エンジン上での検証の仕組み：[docs/maintainers/engine-tests.md](docs/maintainers/engine-tests.md)
- ブランチ、CI、リリース：[docs/maintainers/ci-and-release.md](docs/maintainers/ci-and-release.md)
- 設計の理由：[設計の背景](docs/ja/advanced/design-rationale.md)

## 必要なもの

- .NET SDK 10.0.300 以上の 10.0 系（`global.json`）。ビルド、テスト、`tools/` のツールは、これだけで動きます。
- Unity：6000.3.7f1（場所は `-UnityExe` で指定できます）。IL2CPP と Android のスイートには、それぞれのモジュールが必要です。
- Godot：スクリプトが Godot 4.4.1 の .NET 版を取得して検証します（手元のものを使うときは `-GodotExe`）。
- エンジン上の検証とアナライザの DLL の作り直しは、Windows で行います。

## プルリクエスト

- main から切ったブランチで作業し、main へのプルリクエストにしてください。保守者の変更も同じ流れで入れます。
- マージは squash だけです。1 つのプルリクエストが main の 1 つのコミットになります。
- マージには CI の `ci-ok` が通る必要があります（.NET の Windows、Linux、macOS、NativeAOT、Godot）。
- Unity のテスト（`unity.yml`）はライセンスのシークレットを使うので、フォークからのプルリクエストでは走りません。Unity に関わる変更は、保守者が手元でも確かめます。

## ビルドとテスト

```sh
dotnet build FlowTask.slnx -c Release
dotnet test tests/FlowTask.Core.Tests -c Debug
dotnet test tests/FlowTask.Core.Tests -c Release
dotnet test tests/FlowTask.Analyzers.Tests
dotnet test tests/FlowTask.Bridges.Tests
powershell -ExecutionPolicy Bypass -File tools/pack.ps1   # NuGet の 6 パッケージ（artifacts/packages）
```

`tools/` のツールは .NET のファイルベースアプリで、`dotnet tools/<名前>.cs` で実行します。ツールも C# で書きます。新しい検査を足すときは、まずビルドかテストの中に置けないかを考えてください。

## エンジン上の検証

```powershell
./tools/unity/run-tests.ps1                     # EditMode、PlayMode、Mono のプレイヤー、Android IL2CPP のビルド
./tools/unity/run-tests.ps1 -Suites EditMode    # 1 分ほど
./tools/godot/run-smoke.ps1                     # Godot 4 headless。Godot の中でコアのテストも走る
dotnet publish tests/FlowTask.AotSmoke -c Release -r win-x64   # できた exe を実行する
```

- 変更がエンジンの統合やスケジューラに及ぶときは、Unity、Godot、NativeAOT のすべてで確かめてください。Windows の IL2CPP のプレイヤーは `-Suites StandaloneIl2cpp` で実行できます。
- 公開 API を削る・変える変更と、`src/FlowTask.Unity` や `tests/unity/` に触れる変更では、必ず EditMode を実行してください。Unity 統合のテスト（`tests/unity/Assets/Tests/Unity`）は dotnet ではコンパイルされず、CI の Unity のジョブ（`unity.yml`）もライセンスのシークレットがないと走りません。
- 止まったテストは、EditMode と PlayMode では 15 分、プレイヤーでは 30 分で打ち切られ、`tests/unity/TestResults/summary.json` の `StuckTest` に名前が出ます（`-TimeoutMinutes` で変えられます）。
- サンプルは、Unity が UPM パッケージの `src/FlowTask.Unity/Samples~/`、Godot が `samples/godot/` にあります。Unity のサンプルは `run-tests.ps1` が `tests/unity/` にコピーしてテストし、Godot のサンプルは `run-smoke.ps1` がビルドしてデモを起動し、擬似入力で確かめます。エンジンに依存しないファイルは両方に 1 つずつあるので、直すときは両方を直してください。
- 検証用のプロジェクト `tests/unity/`、Godot のスモークテスト、Unity の CI のライセンスの設定については [docs/maintainers/engine-tests.md](docs/maintainers/engine-tests.md) を見てください。

## テストの書き方

- コアのテストは公開 API だけを使い、内部の状態は `FlowWorld.Diagnostics` で観察します。コアのアセンブリ（FlowTask）には `InternalsVisibleTo` を置きません（例外は FlowTask.Unity と FlowTask.Analyzers の 2 つです）。
- `tests/FlowTask.Core.Tests/*.cs` は Unity（`run-tests.ps1` がそのままコピーします）と Godot（`tests/godot`）でも実行されます。Unity で挙動が違う部分は、そのファイルの中に `#if UNITY_5_3_OR_NEWER` で書いてください。
- テストは、ハングせずに失敗するように書いてください。スケジューラを回すループには上限を付けて判定で失敗させ、別スレッドは `TestThreads.JoinOrFail` / `WaitOrFail`（30 秒）で待ちます。テストのコードにはタイムアウトを書きません（`[Timeout]` は .NET では使えず、`[CancelAfter]` はライブラリの中のループを止めないためです）。
- `dotnet test` は `tests/test.runsettings` により、1 件が 2 分、プロジェクト 1 つが 10 分を超えると打ち切られ、実行中だったテストの名前を出します。このとき要約の行に「成功」と出ることがあるので、結果は終了コードで判定してください。長く実行したいときは `-- RunConfiguration.TestSessionTimeout=0` を付けます。

## 公開 API

公開 API（型、メンバー、設定項目）は小さく保ちます。追加するのは、次の 2 つをどちらも満たすときだけです。

1. 実際に必要とされている：統合パッケージやサンプルが使う、または利用者の具体的な場面がある。
2. 既存の公開 API を組み合わせても書けない（1〜2 行で書けるなら追加しない）。

「あると便利」「他のライブラリにある」「対称になるから」は理由になりません。同じことの書き方が 2 通りになるなら、1 通りに減らします。列挙型の値は末尾に足します。0.x の間は互換性を壊してもかまいませんが、壊したことは CHANGELOG.md に書いてください。

公開 API を変えたら、`FlowTask.slnx` ではコンパイルされない利用箇所（`src/FlowTask.Unity` とそのサンプル（`Samples~`）、`tests/unity/`、`samples/godot/` と `tests/godot/`（`run-smoke.ps1` がビルドする）、README、`src/package-readme.md`、`docs/` の例）も確かめてください。

## ビルドの規則

警告はすべてエラーとして扱い、解析のレベルは 10.0 に固定しています（`Directory.Build.props`。SDK を上げただけで規則が増えないようにするためです）。`src/` のライブラリには、さらにすべての CA 規則とコードスタイルをかけています（`src/Directory.Build.props`）。`tests/godot/Directory.Build.props` と `samples/godot/Directory.Build.props` はルートの値を繰り返しているので、変えるときは一緒に変えてください。

コア（FlowTask）、Testing、Testing.NUnit、UniTask のソースには FLOW 規則もかけています。Unity の利用者はこれらをソースからコンパイルするので、FLOW の警告が利用者全員のコンソールに出てしまうからです。FlowTask.Unity は dotnet ではコンパイルしないので、触れたときは、Unity のログの `warning FLOW` にパッケージのソースのものがないことを確かめてください。

規則を無効にしてよいのは、設計上の判断と衝突するときだけです。その場合は、理由をその場に書きます。

- リポジトリ全体：`.editorconfig` に書き、直前の行に理由を書きます。
- 1 か所：`[SuppressMessage(..., Justification = "…")]` を使うか、`#pragma warning disable` の行末に理由を書きます。
- `NoWarn` に足して黙らせることはしません。

`catch (Exception)` を書いてよいのは、利用者のコード（コールバック、条件、後始末）の例外を受け止めて、await で投げ直すか報告に変える箇所だけです（報告先の World が Dispose された後は捨てます）。その箇所は `#pragma warning disable CA1031 // 理由` で囲みます。

依存に既知の脆弱性があると、推移的な依存であっても復元が止まります（`NuGetAuditMode=all`）。依存を上げるか、修正版を理由付きで直接参照して直してください。`NuGetAuditSuppress` は使いません。

## 言語バージョンと書き方

`src/` のライブラリとコアのテストは C# 10 で書きます（`LangVersion` 10.0）。Unity 2023.1 以降と、Godot 4.4 のゲームをビルドする .NET 8 SDK の両方で使える、最も新しい版です。

- 使うのは構文だけの機能です。ランタイムの支援が要る機能（インターフェイスの static abstract メンバー、ref フィールド）は、Mono と IL2CPP で動きません。`CallerArgumentExpression` と補間文字列ハンドラは、netstandard2.1 に型がないので使っていません。値を運ぶだけの型は record にします。その init アクセサに必要な `IsExternalInit` は、コア（FlowTask）と FlowTask.Unity に internal で置いてあります。
- アナライザとコード修正は `latest` で書き、Roslyn 3.8（.NET 5 SDK。アナライザが読み込まれる最も古い環境）に対してビルドします。アナライザのテストも、意図して 3.8 を使っています。
- Unity は既定で C# 9 でコンパイルするので、UPM パッケージの各 `.asmdef` の隣に、`-langversion:10.0` の 1 行だけの `csc.rsp` を置いています。これはそのアセンブリにだけ効き、利用者のコードは既定のままです。asmdef を足したら、同じ `csc.rsp` と `.meta` も足してください（`ConstraintTests` が確かめます）。ただし `Samples~` のサンプルには置きません。取り込まれると利用者のコードとして、既定の C# 9 でコンパイルされるからです。
- 1 ファイルには 1 つの file-scoped namespace を置き、公開の型と `Katout.FlowTask.Internal` の型は別のファイルに分けます。MonoBehaviour と ScriptableObject（EditorWindow を含む）を定義するファイルだけは block の namespace にし、`.editorconfig` の一覧に足してください。Unity はこれらのクラスを独自の解析でファイルから探すため、file-scoped namespace の中のクラスを見つけられません（`MonoScript.GetClass()` が null になります）。これは Unity のエディタのテスト（`EveryMonoBehaviourAndScriptableObjectIsFoundThroughItsScript`）が確かめます。
- 多くのファイルが使う名前空間は、アセンブリごとの `GlobalUsings.cs` に置きます。Unity は csproj を読まないので、csproj の `<Using>` は使いません。コアのテストの `GlobalUsings.cs` は、Unity と Godot では同じアセンブリの他のファイルにも効くので、Godot の型とぶつかる `System.IO`（FileAccess）と `System.Threading`（Timer）は入れません。Unity のテストのものには、UnityEngine とぶつかる `System`（Object、Random）を入れません。
- `.editorconfig` は、`src/` のビルドで C# 10 の書き方を求めます（`Samples~` のサンプルは除く）。file-scoped namespace、`new()`、`??=`、`is A or B` とプロパティパターン、switch 式、範囲とインデックス、何も捕まえないラムダの `static` です。パターンと switch 式は長い書き方と少し違う IL になることがありますが、ホットパスの計測では揺れを越える差はありませんでした。
- 検証用の Unity プロジェクト（`tests/unity/`）のテストのアセンブリは、`-langversion:latest`（Unity 6 の Roslyn 4.3.1 では C# 10）でコンパイルします。サンプル、サンプルのテスト、UniTask のテスト、エディタの設定（`Assets/Editor/`）には `csc.rsp` を置かず、C# 9 のままにしています。

## 性能

- ホットパス（await、再開、Emit、Tick）では割り当てをしません。LINQ、クロージャ、ボックス化、`params` は使いません。`AllocationTests` が確かめます。
- 性能のための変更は、測ってから入れます。効果がなければ入れません。前後の比較には `dotnet tools/bench/ab.cs`（交互に実行して中央値を比べます）を使います。複数の変更を入れるときは、`--base-dir` で 1 つずつ外して比べてください。IL2CPP には実行時の PGO がないので、`--no-pgo` の結果も見てください。BenchmarkDotNet の short ジョブは揺れが大きいので、前後の比較には使いません。

## バージョン

- パッケージのバージョンは `Directory.Packages.props` だけに書きます。csproj には `Version` を書きません（違う版が必要なときは `VersionOverride` を使い、理由を書きます）。
- ライブラリ自身の版番号は `Directory.Build.props` の `Version` に書き、`src/*/package.json`（UPM）の `version`、FlowTask のパッケージへの依存、導入の手順に書く版（`docs/` と `src/package-readme.md`）を同じ文字列にします。`PackageVersionTests` が照合します。版を上げるのはリリースのプルリクエストだけです（[CI とリリース](docs/maintainers/ci-and-release.md)）。版を上げたら、アナライザの DLL も作り直してください。
- 配布するパッケージの依存は、なるべく増やしません。MIT か Apache-2.0 以外のライセンスのものは、先に Issue で相談してください。

## 生成してコミットするファイル

| ファイル | 作り方（古くなっていないかを CI が検査します） |
|---|---|
| `src/FlowTask/Combinators.g.cs`、`src/FlowTask/Internal/CombinatorNodes.g.cs` | `dotnet tools/gen_combinators.cs`。手で直さないでください |
| UPM パッケージ（`src/*/package.json` のあるフォルダ。サンプルの `Samples~` も含む）の `.meta` | ファイルかフォルダを足したら `dotnet tools/unity/generate_meta.cs`。名前を変えたら `.meta` も一緒に動かします |
| `src/FlowTask/Analyzers/FlowTask.Analyzers.dll`（Unity 用） | Windows の Release ビルドが、バイト列が違うときだけ書き換えます。同じコミットに入れてください |

SDK の機能帯（10.0.3xx と 10.0.4xx など）が違うと、ソースを変えていなくても DLL が書き換わることがあります。そのときはコミットしないでください。CI の照合だけが失敗したときは、ログに出ている SDK の版で作り直してください。

## アナライザ

診断の文言（タイトル、メッセージ、説明、コード修正の名前）は、`src/FlowTask.Analyzers/Resources.resx`（英語）と `Resources.ja.resx`（日本語）を同じコミットで直します。日本語のタイトルは、[docs/ja/tools/analyzers.md](docs/ja/tools/analyzers.md) の見出しと同じ文にします（`LocalizationTests`）。各ルールの節の `<a id="flow00n"></a>` は、日本語と英語のページの両方に置きます（診断のヘルプのリンクが英語のページを指すためです。`GeneralTests`）。アナライザを変えたら、Windows で Release ビルドし、DLL もコミットしてください。

## 文書

- 利用者向けの文書は `docs/ja/`（日本語、原本）と `docs/en/`（英語の翻訳）にあり、`website/` がそれをサイトにします（GitHub Pages、`.github/workflows/docs.yml`）。サイトは公開した版の説明なので、リリースのときに更新します。main で直した文書は、次の版でサイトに出ます。どちらも、GitHub の上でそのまま読める素の Markdown で書きます（`# 見出し` で始め、リンクは相対パスの `.md` にします）。
- 公開 API、挙動、コマンドを変えたら、`docs/ja/` の該当するページと CHANGELOG.md を同じ変更で直してください。英語版も同じ変更で直すのが望ましいですが、難しければ日本語版だけでかまいません（サイトは英語版が古いことを表示しないので、大きな違いが出たら英語版のページを消して、原文を表示させます）。
- 設計の判断を決めたか変えたら、その理由を [設計の背景](docs/ja/advanced/design-rationale.md) に書いてください。
- サイトを手元で確認するには、Node.js 22 以上で次を実行します。

  ```sh
  cd website
  npm ci
  npm run dev     # http://localhost:4321/FlowTask/
  ```

## 他のライブラリのコード

UniTask などのコードは、MIT ライセンスであっても取り入れません（第三者のライセンス表記を持たないためです）。参考にするのは考え方だけにして、構造から自分で書いてください。フィールドの並び、メソッドの中身、interface が元のコードに似ていないかも確かめてください。

## AI の利用

- このリポジトリのコードと文書の多くは、AI の支援を受けて書いています（コミットの `Co-Authored-By` に表れます）。保守者がすべてを読み、責任を持っています。
- 貢献に AI を使ってもかまいません。ただし、変更の全体を自分で読み、レビューで説明できるようにしてください。プルリクエストの説明には、使った道具を書いてください。
- AI が書いたコードにも、前の節の規則がかかります。第三者のコードを含めないでください。
- エージェント向けの作業規則は [AGENTS.md](AGENTS.md) にあります（Claude Code のレビューのスキルは `.claude/skills/` にあります）。
