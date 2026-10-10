# インストール

FlowTask を .NET、Unity、Godot のプロジェクトに入れる手順を説明します。本体のほかに、テスト、UniTask、R3 のための任意のパッケージがあります。

## パッケージの一覧

| パッケージ | 配布 | 内容 |
|---|---|---|
| FlowTask | NuGet `FlowTask`、UPM `com.katout.flowtask` | 本体（FlowTask、スコープ、World、Clock、シグナル、Task のブリッジ）とアナライザ |
| FlowTask.Unity | UPM `com.katout.flowtask.unity` | PlayerLoop への組み込み、Unity のブリッジ、GameObject の寿命、Scope Tree ウィンドウ |
| FlowTask.Godot | NuGet `FlowTask.Godot` | `_Process` への組み込み、Godot のシグナルのブリッジ、ノードの寿命 |
| FlowTask.Testing | NuGet `FlowTask.Testing`、UPM `com.katout.flowtask.testing` | エンジンなしのテスト（`TestWorld`、仮想時間、スコープの木のアサーション） |
| FlowTask.Testing.NUnit | NuGet `FlowTask.Testing.NUnit`、UPM `com.katout.flowtask.testing.nunit` | 未処理の例外を NUnit のテストの失敗にする |
| FlowTask.UniTask | NuGet `FlowTask.UniTask`、UPM `com.katout.flowtask.unitask` | UniTask と FlowTask を互いに待つ |
| FlowTask.R3 | NuGet `FlowTask.R3` | R3 の Observable と Signal をつなぐ |

依存はすべて本体へ向かい、本体はどのパッケージにも依存しません。名前空間は `Katout.FlowTask` です（エンジンの統合とテストのパッケージは、その下の名前空間を持ちます）。

## .NET

```sh
dotnet add package FlowTask --version 0.1.0-preview.1
```

- 対象は netstandard2.1 と net10.0 です。net8.0 などのプロジェクトでは netstandard2.1 版が使われます。NativeAOT でも動きます。
- アナライザとコード修正はパッケージに入っていて、参照するだけで効きます。メッセージは、IDE と `dotnet build` の表示言語（OS の表示言語か `DOTNET_CLI_UI_LANGUAGE`）に従って日本語か英語で出ます（[アナライザのルール](../tools/analyzers.md)）。

任意のパッケージも同じように足します。

```sh
dotnet add package FlowTask.Testing --version 0.1.0-preview.1
dotnet add package FlowTask.Testing.NUnit --version 0.1.0-preview.1
dotnet add package FlowTask.UniTask --version 0.1.0-preview.1
dotnet add package FlowTask.R3 --version 0.1.0-preview.1
```

| パッケージ | 依存として入るもの |
|---|---|
| FlowTask.Testing.NUnit | NUnit 3.14 以上 |
| FlowTask.UniTask | NuGet の UniTask 2.5.10 以上 |
| FlowTask.R3 | R3 1.3.1 以上 |

入れたら、[最初のフロー](first-flow.md) で World を動かしてみてください。

## Unity

Unity 2023.1 以降で使えます。`Packages/manifest.json` に、本体と Unity の統合を git URL で書きます。

```json
{
  "dependencies": {
    "com.katout.flowtask": "https://github.com/katout/FlowTask.git?path=src/FlowTask",
    "com.katout.flowtask.unity": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Unity"
  }
}
```

Package Manager の「Add package from git URL...」に 1 つずつ入れてもかまいません。

> **注意**：統合のパッケージは本体に依存しますが、UPM は git URL の依存を自動では取ってきません。本体の `com.katout.flowtask` も必ず書いてください。任意のパッケージを足すときも、それが依存する FlowTask のパッケージをすべて書きます。

### 版を固定する

URL の末尾に `#` とタグ（かコミット）を付けると、その版に固定できます。FlowTask のパッケージには、すべて同じタグを付けます。

```json
{
  "dependencies": {
    "com.katout.flowtask": "https://github.com/katout/FlowTask.git?path=src/FlowTask#v0.1.0-preview.1",
    "com.katout.flowtask.unity": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Unity#v0.1.0-preview.1"
  }
}
```

### テストのパッケージ

テストのパッケージは `dependencies` に足し、**`testables` にも書きます**。

```json
{
  "dependencies": {
    "com.katout.flowtask": "https://github.com/katout/FlowTask.git?path=src/FlowTask",
    "com.katout.flowtask.testing": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Testing",
    "com.katout.flowtask.testing.nunit": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Testing.NUnit"
  },
  "testables": ["com.katout.flowtask.testing", "com.katout.flowtask.testing.nunit"]
}
```

- この 2 つのアセンブリは、通常のプレイヤーに入らないように、エディタとテストプレイヤーでだけコンパイルされます。Unity はそうしたアセンブリを `testables` に書いたパッケージでしかコンパイルしないので、書かないと、参照するテストが `CS0246` で止まります。2 つのパッケージ自体はテストを持たないので、Test Runner の一覧は増えません。
- `com.katout.flowtask.testing.nunit` は `com.unity.test-framework` 1.1.33 以上に依存します。
- テストの asmdef の書き方は [エンジンなしのテスト](../tools/testing.md) にあります。

### UniTask のブリッジ

`com.katout.flowtask.unitask` を足し、UniTask 自体は git URL か OpenUPM で別に入れます。Unity では UPM の `com.cysharp.unitask` 2.0.0 以上でブリッジがコンパイルされます（2.5.10 以上という下限は NuGet のパッケージの依存です）。

```json
"com.katout.flowtask.unitask": "https://github.com/katout/FlowTask.git?path=src/FlowTask.UniTask",
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#2.5.10"
```

- UniTask がプロジェクトにないと、ブリッジは黙ってコンパイルされません（エラーにもなりません）。
- UniTask を `.unitypackage` で `Assets/Plugins/UniTask` に入れた場合は、パッケージとして見えないので、ブリッジはやはりコンパイルされず、`FlowUniTask` を使うコードが `CS0246` になります。Player Settings > Other Settings > Scripting Define Symbols に `FLOWTASK_UNITASK` を、プラットフォームごとに足してください。
- 使い方は [UniTask](../integrations/unitask.md) にあります。

`FlowTask.R3` には UPM のパッケージがありません。

### Unity で知っておくこと

- 統合は起動時に World を作り、PlayerLoop から毎フレーム Tick します。自分では Tick しません。フローは `FlowTaskUnity.World.Run(...)` で始めます。
- パッケージのソースは C# 10 です。Unity の既定は C# 9 ですが、各アセンブリの隣の `csc.rsp` がそのアセンブリだけの言語バージョンを上げます。あなたのコードは既定のままで使えます。
- アナライザの DLL は本体のパッケージに入っています。エラーのルール（FLOW001、FLOW002、FLOW005）は Unity のコンパイルも止めます。
- コードを asmdef に置いているなら、`FlowTask` と `FlowTask.Unity` を参照に足します。

アセンブリの構成、PlayerLoop との関係、設定は [Unity のセットアップ](../unity/setup.md) にあります。

## Godot

Godot 4.4.1 以降の .NET 版で使えます。ゲームの `.csproj` に `FlowTask.Godot` を足します。本体の `FlowTask` とアナライザは依存として入ります。

```xml
<ItemGroup>
  <PackageReference Include="FlowTask.Godot" Version="0.1.0-preview.1" />
</ItemGroup>
```

`dotnet add package FlowTask.Godot --version 0.1.0-preview.1` でも同じです。

| | 対応 |
|---|---|
| Godot | 4.4.1 以降の .NET 版（パッケージは `GodotSharp` 4.4.1 以上に依存する） |
| ゲームのプロジェクト | `Godot.NET.Sdk/4.4.1`、`net8.0` |
| .NET SDK | Godot 4.4 の要件（.NET 8 以上） |

- FlowTask.Godot は net8.0 を対象にし、本体は netstandard2.1 版が使われます。機能と公開 API は同じです。
- エンジンと `Godot.NET.Sdk` のバージョンは一致させてください。
- 任意のパッケージ（テスト、UniTask、R3）は、上の .NET の節と同じく NuGet で足します。

入れたら、`FlowWorldNode` のサブクラスを 1 行で書いて autoload に登録します。

```csharp
// res://FlowAutoload.cs (the class name must match the file name)
public partial class FlowAutoload : Katout.FlowTask.Godot.FlowWorldNode { }
```

autoload の登録とフローの起動は [Godot のセットアップ](../godot/setup.md) にあります。

## 対応バージョン

| 環境 | バージョン |
|---|---|
| .NET | netstandard2.1 / net10.0（NativeAOT を含む） |
| Unity | 2023.1 以降（Mono、IL2CPP） |
| Godot | 4.4.1 以降の .NET 版（net8.0） |
| ブリッジ | UniTask 2.x（NuGet は 2.5.10 以上、Unity は 2.0.0 以上）、R3 1.3.1 以上 |

動作を確かめている環境は次のとおりです。

- Unity 6（6000.3）の Editor（EditMode、PlayMode）
- Windows の Unity のプレイヤー（Mono、IL2CPP）
- Android の IL2CPP のビルド（ビルドだけで、端末では実行していません）
- Godot 4.4.1（.NET SDK 10 でビルド）
- .NET の NativeAOT

Android と iOS の端末、WebGL、6000.3 より前の Unity（2023.1 と 2023.2 そのものを含む）、4.4.1 以外の Godot では確かめていません。

## 次に読むページ

- [最初のフロー](first-flow.md)：コンソールのプログラムで、World を動かしてみる
- [Unity のセットアップ](../unity/setup.md)、[Godot のセットアップ](../godot/setup.md)
- サンプル：[Unity](../unity/samples.md)（Package Manager から取り込む）、[Godot](../godot/samples.md)（`samples/godot` を開く）
