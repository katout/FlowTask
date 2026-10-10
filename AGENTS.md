# FlowTask：エージェント向けの作業規則

非同期ゲームフローライブラリ FlowTask（名前空間は `Katout.FlowTask`）。コアは pure C# で、Unity、Godot、.NET、NativeAOT で同じテストを通す。全体像は [README.ja.md](README.ja.md)、開発の手順と規則は [CONTRIBUTING.ja.md](CONTRIBUTING.ja.md)（日本語が原本。CONTRIBUTING.md はその英訳）、利用者向けの文書は [docs/ja/](docs/ja/index.md)、実行モデルは [docs/ja/advanced/execution-model.md](docs/ja/advanced/execution-model.md)、内部構造は [docs/maintainers/internals.md](docs/maintainers/internals.md)、ブランチと CI とリリースは [docs/maintainers/ci-and-release.md](docs/maintainers/ci-and-release.md)、設計の理由は [docs/ja/advanced/design-rationale.md](docs/ja/advanced/design-rationale.md)、用語は [docs/ja/advanced/glossary.md](docs/ja/advanced/glossary.md) にある。

## まず守ること

1. **機能を足さない**。公開 API（型、メンバー、設定項目）を追加・変更できるのは、実際の利用者があり、既存 API の合成では書けない場合だけ（CONTRIBUTING.ja.md）。迷ったら足さずに、ユーザーに確認する。
2. **ビルドを止めるルールを回避しない**。警告はすべてエラーで、src/ ではすべての CA 規則が有効。規則を抑制してよいのは設計上の判断と衝突するときだけで、その場合は理由を必ず書く（`.editorconfig` のコメント、`Justification`、`#pragma` の行末）。`NoWarn` に足して黙らせない。
3. **パッケージのバージョンは `Directory.Packages.props` だけに書く**。ライブラリ自身の版番号は `Directory.Build.props` の `Version` に書き、UPM の `src/*/package.json` をそれに合わせる（`PackageVersionTests` が照合する）。

## コードの制約

- コア（`src/FlowTask`）は netstandard2.1 と net10.0 を対象にする。netstandard2.1 にない API は使わない。FlowTask.Godot、tests/godot、samples/godot は Godot 4.4 に合わせて net8.0 で、コアの netstandard2.1 版を使う。
- `src/` のライブラリとコアのテストは C# 10 で書く（Unity 2023.1 以降と Godot の両方で使える最も新しい版）。C# 11 以降の構文と、ランタイムの支援が要る機能（static abstract メンバー、ref フィールド）は使わない。アナライザとコード修正は `latest` のまま、Roslyn 3.8 に対してビルドする。
  - Unity の既定は C# 9 なので、UPM パッケージの各 asmdef の隣に `-langversion:10.0` だけの `csc.rsp` がある。asmdef を足したら同じ `csc.rsp`（と `.meta`）も置く（`ConstraintTests` が検査する）。`Samples~` のサンプルには置かない（利用者のコードとして C# 9 でコンパイルされる）。
  - 多くのファイルが使う名前空間は、アセンブリごとの `GlobalUsings.cs` にある。Unity は csproj を読まないので、csproj の `<Using>` にしない。
  - 1 ファイルに 1 つの file-scoped namespace。MonoBehaviour と ScriptableObject（EditorWindow を含む）を定義するファイルだけは block の namespace にし、`.editorconfig` の一覧に足す（Unity は file-scoped namespace の中のこれらのクラスを見つけない）。
- ホットパス（await、再開、Emit、Tick）では割り当てない。LINQ、クロージャ、ボックス化、`params` を使わない。定常状態の割り当てゼロは `AllocationTests` が検査する。
- 性能に関わる変更は、`dotnet tools/bench/ab.cs` で前後を測ってから入れる（IL2CPP には実行時の PGO がないので `--no-pgo` の結果も見る）。測って効果がない最適化は入れない。
- 他のライブラリ（UniTask など）のコードは、MIT でも取り入れない。参考にするのは考え方だけで、構造から自分で書く。
- 利用者のコード（コールバック、条件、後始末）の例外を受け止める箇所だけが `catch (Exception)` を書いてよい。理由を `#pragma warning disable CA1031 // …` に書く。
- コア（FlowTask）、FlowTask.Testing、FlowTask.Testing.NUnit、FlowTask.UniTask のビルドには FLOW 規則もかかる（Unity の利用者はこれらのソースをコアのアナライザと一緒にコンパイルする）。FlowTask.Unity は dotnet でコンパイルしないので、触れたら Unity のログの `warning FLOW` にパッケージのソースのものがないことを確かめる。
- 要件定義書の ID（`SC-04` など）、実行順の規則の番号、レビューの番号は、コード、テスト名、文書のどこにも書かない。挙動を言葉で書く。
- アナライザの文言は、`src/FlowTask.Analyzers/Resources.resx`（英語）と `Resources.ja.resx`（日本語）の両方を同じ変更で直す。日本語のタイトルは docs/ja/tools/analyzers.md の見出しと同じ文にする（`LocalizationTests`）。アナライザを変えたら Windows で Release ビルドし、作り直された `src/FlowTask/Analyzers/FlowTask.Analyzers.dll` もコミットする。

## テスト

- コアのテストは公開 API だけを使う。コアのアセンブリ（FlowTask）に `InternalsVisibleTo` を置かない（同じソースを Unity と Godot でも実行するため）。内部状態は `FlowWorld.Diagnostics` で観察する。例外は `src/FlowTask.Unity/Runtime/AssemblyInfo.cs` と `src/FlowTask.Analyzers/FlowTask.Analyzers.csproj` の 2 つで、どちらも消さない。
- `tests/FlowTask.Core.Tests/*.cs` は Unity（`tools/unity/run-tests.ps1` がコピーする）と Godot（`tests/godot`）にも取り込まれる。Unity で違うことは、そのファイルの中に `#if UNITY_5_3_OR_NEWER` で書く。
- テストはハングせずに失敗するように書く。スケジューラを回すループには上限を付け、別スレッドは `TestThreads.JoinOrFail` / `WaitOrFail` で待つ。`dotnet test` は `tests/test.runsettings` の上限で打ち切られ、そのときは終了コードが 1 になる（要約の行ではなく終了コードで判定する）。

## コマンド

コマンドの一覧は CONTRIBUTING.ja.md にある。変更を終える前に最低限これを通す。

```sh
dotnet build FlowTask.slnx -c Release       # Windows では Unity 用のアナライザの DLL も作り直す
dotnet test tests/FlowTask.Core.Tests -c Debug
dotnet test tests/FlowTask.Core.Tests -c Release
dotnet test tests/FlowTask.Analyzers.Tests
dotnet test tests/FlowTask.Bridges.Tests
dotnet tools/unity/generate_meta.cs --check # UPM パッケージ（src/*/package.json のあるフォルダ。Samples~ を含む）にファイルを足したら
dotnet tools/gen_combinators.cs --check
```

公開 API を削る・変える変更と、`src/FlowTask.Unity`、`tests/unity/` に触れる変更では、Unity の EditMode を必ず実行する（`./tools/unity/run-tests.ps1 -Suites EditMode`）。Unity 統合のテストは dotnet ではコンパイルされず、CI の Unity のジョブもライセンスのシークレットがあるときしか走らない。エンジンの統合やスケジューラに及ぶ変更では、`./tools/unity/run-tests.ps1`（既定のすべて）、`./tools/godot/run-smoke.ps1`、NativeAOT のスモーク（`dotnet publish tests/FlowTask.AotSmoke -c Release -r win-x64` で作った exe を実行）も通す。

## 変更を終える前に

1. 上のテストを通す。失敗を残したまま「完了」と報告しない。
2. 公開 API、挙動、コマンドを変えたら、docs/ja/ の該当するページ（英語版の docs/en/ も、直せる範囲で）と CHANGELOG.md を直す。設計の判断を新しく決めたか変えたら、その理由を docs/ja/advanced/design-rationale.md に書く。文書の書き方は CONTRIBUTING.ja.md の「文書」にある。
3. 設計に関わる変更は、変更を書いたのとは別の文脈でレビューする。Claude Code では `.claude/skills/flowtask-review` のスキル（`/flowtask-review`）を使う。ほかの道具では、そのファイルの観点に沿って、別のセッションで差分をレビューする。
