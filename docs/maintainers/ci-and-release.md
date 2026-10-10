# CI とリリース

FlowTask のブランチの運用、GitHub Actions のワークフロー、リリースの手順の説明です（保守する人向け）。貢献の手順は [CONTRIBUTING.ja.md](../../CONTRIBUTING.ja.md)、エンジン上の検証は [engine-tests.md](engine-tests.md) にあります。

## ブランチ

GitHub Flow で運用します。

- `main` は常にリリースできる状態に保ちます。変更はすべて、main から切ったブランチのプルリクエストで入れます。保守者の変更も同じです。Linux と macOS での検証は CI でしかできないためです。
- マージは squash だけにし、main の履歴を直線に保ちます。
- リリースは main のコミットにタグ `v<版>` を打って出します。NuGet の 6 パッケージと UPM の 5 パッケージは同じ版で、タグは 1 つです。
- 1.0 までは、修正は最新の版にだけ入れます（[SECURITY.md](../../SECURITY.md)）。古い版を直す必要が出たら、そのときにタグから `release/<major>.<minor>` を作ります。それまでは、main のほかに長く続くブランチを作りません。

## ワークフロー

| ワークフロー | プルリクエスト | main への push | タグ `v*` | 手動 |
|---|---|---|---|---|
| `ci.yml`：.NET（Windows、Linux、macOS）、NativeAOT、Godot、パッケージ | 毎回。`ci-ok` が必須 | 毎回 | `release.yml` が呼ぶ | 可 |
| `unity.yml`：Unity Editor の EditMode と PlayMode | Unity がコンパイルか実行するファイルを変えたとき | 同左 | `release.yml` が呼ぶ | 可 |
| `docs.yml`：サイト | `docs/` か `website/` を変えたとき（ビルドだけ） | 同左 | `release.yml` が呼んで公開 | 選んだ ref を公開 |
| `release.yml`：リリース | なし | なし | 毎回 | なし |

- 必須のチェックは `ci-ok` だけです。`ci.yml` の全ジョブの結果をまとめるジョブなので、ジョブの名前やマトリクスを変えてもルールセットを直さずに済みます。
- `unity.yml` と `docs.yml` は必須にしません。パスで絞っているので走らないプルリクエストがあり、必須のチェックが走らないとマージできなくなるためです。`unity.yml` は、シークレットのないフォークと Dependabot のプルリクエストではテストを飛ばします。Unity に関わるフォークのプルリクエストは、保守者が手元で EditMode を実行してからマージします。
- main への push でも `ci.yml`（とパスが合えば `unity.yml`、`docs.yml` のビルド）を走らせます。プルリクエストに main への追従を求めないので、別々に通った 2 つの変更の組み合わせを確かめるためです。
- サイトは main ではなく、公開した版を説明します。公開した版のアナライザのヘルプのリンクと実行時のメッセージがサイトを指しているので、main で規則を足したり番号を変えたりしても、その版の利用者が見るページを変えないためです。サイトから docs/ の外（サンプル、CHANGELOG.md）へのリンクも、その版のタグを指します。文書だけの修正を急いで出したいときは、`docs.yml` を手動で main から実行できます。その前に、まだ公開していない API の説明が main の文書に入っていないことを確かめてください。
- actions はすべてコミットの SHA に固定し、Dependabot が月に 1 回まとめて上げます（`.github/dependabot.yml`。出てから 7 日たった版だけを取ります）。`game-ci/unity-test-runner` は手で上げます（[engine-tests.md](engine-tests.md)）。NuGet のパッケージは Dependabot の対象にしません（理由は `dependabot.yml` にあります）。
- 定期実行はしません。Dependabot の月 1 回のプルリクエストが CI を走らせるので、ランナーや取得元の変化にはそこで気づけます。ただし `release.yml` でしか使わない actions（download-artifact、NuGet/login、deploy-pages）は、次のリリースで初めて動きます。
- CI で走らないのは Unity のプレイヤー（Windows の IL2CPP と Mono、Android の IL2CPP のビルド）だけです。リリースの前に手元で実行します。

## リリースの手順

1. リリースのプルリクエストを作ります。版を上げるのは、このプルリクエストだけです。
   - 版：`Directory.Build.props` の `Version`、`src/*/package.json` の `version` と FlowTask のパッケージへの依存、導入の手順に書いた版（`docs/ja`、`docs/en`、NuGet のパッケージの README の `src/package-readme.md`）を上げます。
   - CHANGELOG.md：`## [Unreleased]` の内容を `## [<版>] - <日付>` に移し、空の `## [Unreleased]` を残します。末尾に、タグと比較のリンクの定義を足します。
   - 版、導入の手順、CHANGELOG.md の日付のある最新の節がそろっていることは、`PackageVersionTests` がこのプルリクエストの CI で確かめます。タグを打ってから気づくと版を 1 つ失うので、ここで落とします。
   - アナライザ：安定版（版に `-` がない）では、`src/FlowTask.Analyzers/AnalyzerReleases.Unshipped.md` の規則を `AnalyzerReleases.Shipped.md` の `## Release <版>` に移します。プレリリースでは Unshipped に残します（見出しの版は `major.minor.patch` しか書けず、`0.1.0-preview.1` はビルドを RS2007 で止めます）。
   - 版が変わるとアナライザの DLL のバイト列も変わるので、Windows で Release ビルドし、`src/FlowTask/Analyzers/FlowTask.Analyzers.dll` もコミットします。
   - CI で走らない検証を手元で行います（`./tools/unity/run-tests.ps1` の既定のすべて）。
2. マージしたら、main のそのコミットにタグを打って push します。

   ```sh
   git switch main
   git pull
   git tag v<版>
   git push origin v<版>
   ```

3. `release.yml` が次の順に進みます。
   1. タグが main の上にあり、`Version` と同じ版を指していることを確かめます。
   2. タグのコミットで `ci.yml` と `unity.yml` を実行します。
   3. `release` environment の承認を待ちます。Actions の画面で結果を確かめます。Unity のライセンスのシークレットがないと `unity.yml` はテストを飛ばして成功になるので、Unity のジョブが「Unity tests skipped」の notice を出していないかも見てください。承認すると、`ci.yml` が作ったパッケージを nuget.org に push し（trusted publishing。API キーをリポジトリに置きません）、CHANGELOG.md のその版の節を本文にした GitHub の Release を作ります（版に `-` があればプレリリース）。
   4. `docs.yml` でサイトを公開します。
4. OpenUPM は、タグを見つけて UPM のパッケージを自動でビルドします。`release.yml` の承認を待たないので、タグを打った時点で UPM には出ると考えてください。

タグは動かせません（ルールセット）。UPM の git URL と OpenUPM がタグを指すためです。失敗したときは次のようにします。

- 設定の誤り（environment、nuget.org のポリシー、Pages）による失敗：設定を直し、失敗したジョブを再実行します。nuget.org に push 済みのパッケージは `--skip-duplicate` で飛ばします。
- テストか、`release.yml` そのものの誤りによる失敗：再実行では直りません（再実行はタグのコミットのワークフローとソースを使います）。直したプルリクエストをマージし、次の版でタグを打ち直します。最初のリリースでは `release.yml` が初めて動くので、この形の失敗がありえます。

## リポジトリの設定

ファイルに書けない GitHub と nuget.org の設定です。リポジトリを作ったときに一度だけ行います。

- Settings → General → Pull Requests：「Allow squash merging」だけを残し、「Automatically delete head branches」を有効にします。
- Settings → Rules → Rulesets：
  - main（対象は Default branch）：Restrict deletions、Block force pushes、Require linear history、Require a pull request before merging（必要な承認は 0。1 人では自分のプルリクエストを承認できないため）、Require status checks to pass（`ci-ok`。GitHub Actions のもの）。Bypass list に Repository admin を「For pull requests only」で入れます。CI の外の原因で `ci-ok` が落ちたときもマージでき、main への直接の push はできないままになります。
  - タグ（対象は `v*`）：Restrict updates、Restrict deletions。Bypass list は空にします。
- Settings → Environments：
  - `release`：Required reviewers に保守者を入れ、Prevent self-review は外します。Deployment branches and tags を Selected にし、タグ `v*` だけを許します。
  - `github-pages`（Pages の設定で作られます）：Deployment branches and tags にタグ `v*` を足します。既定の main は、手動の公開のために残します。
- Settings → Pages → Source：GitHub Actions。
- Settings → Secrets and variables → Actions：Variables に `NUGET_USER`（nuget.org のユーザー名）を作ります。Unity のシークレットは [engine-tests.md](engine-tests.md) にあります。
- Settings → Advanced Security：Private vulnerability reporting（SECURITY.md の窓口）と Dependabot alerts を有効にします。
- nuget.org → Trusted Publishing：Repository owner `katout`、Repository `FlowTask`、Workflow file `release.yml`、Environment `release` のポリシーを作ります。
- OpenUPM：最初のタグを打った後で、UPM の 5 パッケージを 1 つずつ登録します。登録した後は、タグから自動でビルドされます。
